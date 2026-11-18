using System.Net;
using System.Reflection;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend;
using JulOS.Frontend.Agent;
using JulOS.Frontend.Auth;
using JulOS.Frontend.Cli;
using JulOS.Frontend.Connections;
using JulOS.Frontend.Remote;
using JulOS.Integrations.Agent;
using JulOS.Integrations.Display;
using JulOS.Integrations.Guacamole;
using JulOS.Workers;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

// Konsolenwerkzeug: dotnet JulOS.dll user ...
if (args.Length > 0 && args[0] == "user") return await UserCli.RunAsync(args[1..]);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
// Nicht jede Anfrage ins Log (nur Warnungen und Fehler des Frameworks); eigene Meldungen wie Anmeldungen bleiben sichtbar
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var connectionString = builder.Configuration["JULOS_DB"]
    ?? throw new InvalidOperationException("JULOS_DB fehlt (Verbindung zu PostgreSQL, siehe deploy/dev/compose.yaml).");
// Kerberos/GSSAPI wird nicht gebraucht (auf Alpine fehlt die Bibliothek und Npgsql meldet es sonst im Log)
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
builder.Services.AddSingleton(dataSourceBuilder.Build());
builder.Services.AddSingleton<Migrator>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddSingleton(new SecretBox(SecretBox.ResolveKey(builder.Configuration["JULOS_SECRET_KEY"], builder.Configuration["JULOS_DATA"])));
builder.Services.AddSingleton(new HostPolicy(builder.Configuration["JULOS_ALLOWED_HOSTS"]));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<LoginLimiter>();
builder.Services.AddSingleton(new SetupCode(builder.Configuration["JULOS_SETUP_CODE"]));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuthService>();
builder.Services.AddHostedService<SessionPurgeWorker>();
builder.Services.AddSingleton<GuacTickets>();
builder.Services.AddSingleton<ScreenTickets>();
builder.Services.AddHostedService<TicketSweepWorker>();
// guacd (Container im selben Netz), erreichbar nur fuer den Server
var guacd = (builder.Configuration["JULOS_GUACD"] ?? "guacd:4822").Split(':');
builder.Services.AddSingleton(new GuacdOptions(guacd[0], guacd.Length > 1 ? int.Parse(guacd[1]) : 4822));
builder.Services.AddSingleton<GuacRelay>();
builder.Services.AddSingleton<ScreenRelay>();
// Agent in der Sitzung der Ziel-Maschine (Unix-Socket) und der gemeinsame Ordner fuer Foto-Uploads, beide vom Host eingebunden
builder.Services.AddSingleton(new AgentOptions(builder.Configuration["JULOS_AGENT_SOCKET"] ?? "/uploads/.agent/agent.sock"));
builder.Services.AddSingleton<AgentClient>();
builder.Services.AddSingleton(new UploadOptions(builder.Configuration["JULOS_UPLOAD_DIR"] ?? "/uploads"));
builder.Services.AddSingleton<ClipboardUploader>();
builder.Services.AddSingleton<HitCounter>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));

// Hinter einem Proxy im eigenen Netz (zum Beispiel Caddy) kommen Client-Adresse, Protokoll und Host aus den X-Forwarded-Kopfzeilen
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    var trusted = builder.Configuration["JULOS_TRUSTED_PROXIES"] ?? "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,fc00::/7";
    foreach (var net in trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(net));
});

var app = builder.Build();
var log = app.Logger;
app.UseForwardedHeaders();

await MigrateWithRetryAsync(app.Services.GetRequiredService<Migrator>(), log);

var auth = app.Services.GetRequiredService<AuthService>();
if (await auth.SetupNeededAsync())
    log.LogWarning("ERSTEINRICHTUNG: Es gibt noch kein Konto. Öffne die Seite im Browser und gib diesen Setup-Code ein: {Code}", app.Services.GetRequiredService<SetupCode>().Value);

const string Csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; "
                 + "manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] = Csp;
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h["Cross-Origin-Resource-Policy"] = "same-origin";
    if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
    await next();
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
// Der Browser fragt bei jedem Start nach, ob sich eine Datei geaendert hat (ETag), damit Updates sofort ankommen
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
app.UseMiddleware<ApiGuard>();

var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";
app.MapGet("/api/health", async (NpgsqlDataSource data, CancellationToken ct) =>
{
    var dbOk = false;
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await using var cmd = data.CreateCommand("select 1");
        dbOk = await cmd.ExecuteScalarAsync(timeout.Token) is not null;
    }
    catch (Exception) { /* Datenbank nicht erreichbar: ok=false */ }
    return new Health(dbOk, dbOk, version);
});
app.MapAuth();
app.MapRemote();
app.MapConnections();
app.MapAgent();

app.Run();
return 0;

static async Task MigrateWithRetryAsync(Migrator migrator, ILogger log)
{
    // Beim gemeinsamen Start ist PostgreSQL manchmal noch nicht bereit: bis zu 60 s warten
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var applied = await migrator.RunAsync();
            log.LogInformation("Datenbank bereit, {Count} Migration(en) angewendet.", applied.Count);
            return;
        }
        catch (Exception e) when (attempt < 30 && e is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            log.LogWarning("Datenbank noch nicht erreichbar (Versuch {Attempt}): {Message}", attempt, e.Message);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

public partial class Program;
