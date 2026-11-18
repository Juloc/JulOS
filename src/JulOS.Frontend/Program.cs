using System.Reflection;
using System.Text.Json.Serialization;
using JulOS.Backend.Data;
using Npgsql;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

var connectionString = builder.Configuration["JULOS_DB"]
    ?? throw new InvalidOperationException("JULOS_DB fehlt (Verbindung zu PostgreSQL, siehe deploy/dev/compose.yaml).");
// Kerberos/GSSAPI wird nicht gebraucht (auf Alpine fehlt die Bibliothek und Npgsql meldet es sonst im Log)
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
var db = dataSourceBuilder.Build();
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<Migrator>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));

var app = builder.Build();
var log = app.Logger;

await MigrateWithRetryAsync(app.Services.GetRequiredService<Migrator>(), log);

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

app.UseDefaultFiles();
app.UseStaticFiles();

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

app.Run();

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

internal sealed record Health(bool Ok, bool Db, string Version);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Health))]
internal sealed partial class AppJson : JsonSerializerContext;

public partial class Program;
