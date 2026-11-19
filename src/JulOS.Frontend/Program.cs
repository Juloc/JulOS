using System.Net;
using System.Reflection;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend;
using JulOS.Frontend.About;
using JulOS.Frontend.Agent;
using JulOS.Frontend.Auth;
using JulOS.Frontend.Cli;
using JulOS.Frontend.Connections;
using JulOS.Frontend.Files;
using JulOS.Frontend.Remote;
using JulOS.Frontend.SystemInfo;
using JulOS.Integrations;
using JulOS.Integrations.Agent;
using JulOS.Integrations.Display;
using JulOS.Integrations.Display.Video;
using JulOS.Integrations.Guacamole;
using JulOS.Integrations.Ssh;
using JulOS.Workers;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

// Konsolenwerkzeug: dotnet JulOS.dll user ...
if (args.Length > 0 && args[0] == "user") return await UserCli.RunAsync(args[1..]);
// Diagnose ohne Datenbank: dotnet JulOS.dll diag encoders
if (args.Length > 0 && args[0] == "diag") return DiagCli.Run(args[1..]);

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
builder.Services.AddSingleton<SshTickets>();
builder.Services.AddHostedService<TicketSweepWorker>();
// guacd (Container im selben Netz), erreichbar nur fuer den Server
var guacd = (builder.Configuration["JULOS_GUACD"] ?? "guacd:4822").Split(':');
builder.Services.AddSingleton(new GuacdOptions(guacd[0], guacd.Length > 1 ? int.Parse(guacd[1]) : 4822));
// Obergrenze offener Fernsteuer-Sitzungen pro Benutzer (alle Arten zusammen), siehe docs/SESSIONS.md
builder.Services.AddSingleton(new SessionRegistry(int.TryParse(builder.Configuration["JULOS_MAX_SESSIONS_PER_USER"], out var maxSessions) ? maxSessions : SessionRegistry.DefaultMaxPerUser));
builder.Services.AddSingleton<GuacRelay>();
// Bildkodierer: JULOS_ENCODER (auto, software, vaapi) und JULOS_CODEC (auto, h264, h265), siehe docs/ENCODERS.md
builder.Services.AddSingleton(sp =>
{
    var encoderLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("JulOS.Encoder");
    var settings = EncoderSettings.Parse(builder.Configuration["JULOS_ENCODER"], builder.Configuration["JULOS_CODEC"], message => encoderLog.LogWarning("{Message}", message));
    return EncoderSelector.CreateDefault(settings, encoderLog);
});
builder.Services.AddSingleton<ScreenRelay>();
builder.Services.AddSingleton<SshRelay>(sp => new SshRelay(sp.GetRequiredService<ILogger<SshRelay>>(), registry: sp.GetRequiredService<SessionRegistry>()));
// Agent in der Sitzung der Ziel-Maschine (Unix-Socket) und der gemeinsame Ordner fuer Foto-Uploads, beide vom Host eingebunden
builder.Services.AddSingleton(new AgentOptions(builder.Configuration["JULOS_AGENT_SOCKET"] ?? "/uploads/.agent/agent.sock"));
builder.Services.AddSingleton<AgentClient>();
// Agenten, die sich von der Ziel-Maschine aus melden (WebSocket): Einrichtung, Verbindungen, Weg eines Aufrufs
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton<AgentLinks>();
builder.Services.AddSingleton<AgentRouter>();
builder.Services.AddSingleton(new UploadOptions(builder.Configuration["JULOS_UPLOAD_DIR"] ?? "/uploads"));
builder.Services.AddSingleton<ClipboardUploader>();
builder.Services.AddSingleton<ClipboardFileUploader>();
builder.Services.AddSingleton<ClipboardPuller>();
builder.Services.AddSingleton<HitCounter>();
// Auslastung des Rechners (CPU, Speicher, Datentraeger des Upload-Ordners, Netzwerk), alle 2 s gemessen
builder.Services.AddSingleton(new HostMonitor(HostSource.Linux(builder.Configuration["JULOS_UPLOAD_DIR"] ?? "/uploads")));
builder.Services.AddHostedService<HostSamplerWorker>();
// Dateien (nur lesen): genau ein Ordner, Standard der Upload-Ordner; "off" schaltet ab
builder.Services.AddSingleton(new FileBrowser(builder.Configuration["JULOS_FILES_ROOT"] ?? builder.Configuration["JULOS_UPLOAD_DIR"] ?? "/uploads"));
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

if (!app.Services.GetRequiredService<SecretBox>().Enabled)
    log.LogWarning("Passwörter speichern ist aus: kein Schlüssel. JULOS_SECRET_KEY setzen oder dafür sorgen, dass der Server in JULOS_DATA schreiben darf (dort legt er secret.key an).");
// Was der Bildkodierer kann, einmal ins Log (Einzelheiten: dotnet JulOS.dll diag encoders)
foreach (var line in EncoderReport.Summary(app.Services.GetRequiredService<EncoderSelector>())) log.LogInformation("Bildkodierer: {Line}", line);

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
    h["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=(), payment=(), usb=()";
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

var info = AppInfo.From(typeof(Program).Assembly, Environment.GetEnvironmentVariable("JULOS_BUILD_DATE"));
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
    return new Health(dbOk, dbOk, info.Version);
});
app.MapAuth();
app.MapAbout(info);
app.MapRemote();
app.MapConnections();
app.MapAgent();
app.MapAgentLink(info);
app.MapSystem();
app.MapFiles();

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
