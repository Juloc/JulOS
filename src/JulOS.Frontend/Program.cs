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
using JulOS.Frontend.Folders;
using JulOS.Frontend.Hosts;
using JulOS.Frontend.Modules;
using JulOS.Frontend.Push;
using JulOS.Frontend.Remote;
using JulOS.Frontend.Security;
using JulOS.Frontend.Settings;
using JulOS.Frontend.SystemInfo;
using JulOS.Frontend.Tools;
using JulOS.Integrations;
using JulOS.Integrations.Agent;
using JulOS.Integrations.Display;
using JulOS.Integrations.Display.Rdp.Drive;
using JulOS.Integrations.Display.Video;
using JulOS.Integrations.Reachability;
using JulOS.Integrations.Ssh;
using JulOS.Workers;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

// Konsolenwerkzeug: dotnet JulOS.dll user ...
if (args.Length > 0 && args[0] == "user") return await UserCli.RunAsync(args[1..]);
// Sicherung auf der Konsole: dotnet JulOS.dll backup create | restore DATEI
if (args.Length > 0 && args[0] == "backup") return await BackupCli.RunAsync(args[1..]);
// Diagnose ohne Datenbank: dotnet JulOS.dll diag encoders
if (args.Length > 0 && args[0] == "diag") return DiagCli.Run(args[1..]);
// Gesundheitspruefung fuer den Container-Healthcheck: dotnet JulOS.dll health
if (args.Length > 0 && args[0] == "health") return await HealthCli.RunAsync();

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
builder.Services.AddSingleton<HostStore>();
builder.Services.AddSingleton<HostLoginStore>();
builder.Services.AddSingleton<HostLoginMigration>();
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddSingleton<FolderStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<QuickTrust>();
builder.Services.AddSingleton<TcpProbe>();
builder.Services.AddSingleton<NetTools>();
builder.Services.AddSingleton(new SecretBox(SecretBox.ResolveKey(builder.Configuration["JULOS_SECRET_KEY"], builder.Configuration["JULOS_DATA"])));
// Einstellungen der Instanz in der Datenbank (Sicherheit: vertraute Proxys, erlaubte Zielhosts, zweiter Faktor); JULOS_TRUSTED_PROXIES und JULOS_ALLOWED_HOSTS sind nur noch der Rueckfall
builder.Services.AddSingleton<InstanceSettings>();
builder.Services.AddSingleton(sp => new SecurityConfig(sp.GetRequiredService<InstanceSettings>(), k => builder.Configuration[k]));
builder.Services.AddSingleton<SecurityAuditStore>();
builder.Services.AddSingleton(sp => { var cfg = sp.GetRequiredService<SecurityConfig>(); return new HostPolicy(() => cfg.Hosts.Entries); });
builder.Services.AddSingleton<BackupKeys>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<LoginLimiter>();
builder.Services.AddSingleton(new SetupCode(builder.Configuration["JULOS_SETUP_CODE"]));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuthService>();
builder.Services.AddHostedService<SessionPurgeWorker>();
// Taegliche Sicherung der Datenbank und des Schluessels in den Backup-Ordner, siehe docs/BACKUP.md
builder.Services.AddSingleton(BackupOptions.FromSettings(k => builder.Configuration[k]));
builder.Services.AddSingleton(sp => new BackupService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<BackupOptions>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<BackupService>>(), BackupTargets.Factory(sp.GetRequiredService<BackupOptions>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("JulOS.BackupCopy")), sp.GetRequiredService<BackupKeys>().Plan));
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddSingleton<ScreenTickets>();
// Laufwerksumleitung: der eine Ordner, der je Verbindung als Laufwerk gereicht werden darf (leer oder "off": keine Freigabe), siehe docs/DRIVES.md
builder.Services.AddSingleton(sp => DriveRoot.FromSetting(builder.Configuration["JULOS_DRIVE_ROOT"], sp.GetRequiredService<ILoggerFactory>().CreateLogger("JulOS.Drive")));
builder.Services.AddSingleton<SshTickets>();
builder.Services.AddHostedService<TicketSweepWorker>();
// Obergrenze offener Fernsteuer-Sitzungen pro Benutzer (alle Arten zusammen), siehe docs/SESSIONS.md
builder.Services.AddSingleton(new SessionRegistry(int.TryParse(builder.Configuration["JULOS_MAX_SESSIONS_PER_USER"], out var maxSessions) ? maxSessions : SessionRegistry.DefaultMaxPerUser));
// Bildkodierer: JULOS_ENCODER (auto, software, vaapi) und JULOS_CODEC (auto, h264, h265), siehe docs/ENCODERS.md
builder.Services.AddSingleton(sp =>
{
    var encoderLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("JulOS.Encoder");
    var settings = EncoderSettings.Parse(builder.Configuration["JULOS_ENCODER"], builder.Configuration["JULOS_CODEC"], message => encoderLog.LogWarning("{Message}", message));
    return EncoderSelector.CreateDefault(settings, encoderLog);
});
// Dateien in der Zwischenablage: Grenze (JULOS_CLIP_MAX_MB, Standard 2048) und Ablageordner (JULOS_CLIP_DIR), siehe docs/CLIPBOARD.md
ClipboardOptions.Configure(ClipboardOptions.Parse(builder.Configuration["JULOS_CLIP_MAX_MB"], builder.Configuration["JULOS_CLIP_DIR"], builder.Configuration["JULOS_CLIP_EOL"]));
ClipboardBridge.SweepOld(ClipboardOptions.Default.TempRoot);
// Sitzungserhalt: so lange lebt eine RDP-Sitzung ohne Browser weiter (JULOS_STREAM_KEEP_SECONDS, Standard 300, 0 = aus), siehe docs/SESSIONS.md
builder.Services.AddSingleton(sp => new ScreenRelay(sp.GetRequiredService<ILogger<ScreenRelay>>(), registry: sp.GetRequiredService<SessionRegistry>(),
    encoders: sp.GetRequiredService<EncoderSelector>(), keep: ScreenRelay.ParseKeep(builder.Configuration["JULOS_STREAM_KEEP_SECONDS"])));
builder.Services.AddSingleton<SshRelay>(sp => new SshRelay(sp.GetRequiredService<ILogger<SshRelay>>(), registry: sp.GetRequiredService<SessionRegistry>()));
// Agent in der Sitzung der Ziel-Maschine (Unix-Socket) und der gemeinsame Ordner fuer Foto-Uploads, beide vom Host eingebunden
builder.Services.AddSingleton(new AgentOptions(builder.Configuration["JULOS_AGENT_SOCKET"] ?? "/uploads/.agent/agent.sock"));
builder.Services.AddSingleton<AgentClient>();
// Agenten, die sich von der Ziel-Maschine aus melden (WebSocket): Einrichtung, Verbindungen, Weg eines Aufrufs
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton<AgentLinks>();
builder.Services.AddSingleton<AgentRelease>();
builder.Services.AddSingleton<AgentUpdater>();
builder.Services.AddSingleton<AgentRouter>();
builder.Services.AddSingleton<HostInfoService>();
builder.Services.AddSingleton<PushStore>();
builder.Services.AddSingleton(sp => new PushService(sp.GetRequiredService<PushStore>(), PushService.CreateClient(), sp.GetRequiredService<HitCounter>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PushService>>()));
builder.Services.AddSingleton<IHostObserver, HostObserver>();
builder.Services.AddSingleton<AlertRunner>();
builder.Services.AddHostedService<AlertWorker>();
// Aktionen auf Hosts (Stufe 3): Bestaetigung, Verlauf, Pausenschalter (JULOS_HOST_ACTIONS=off), siehe docs/HOST-AGENT.md
builder.Services.AddSingleton<HostAuditStore>();
builder.Services.AddSingleton(HostActionOptions.Default);
builder.Services.AddSingleton<HostActionService>();
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
// Dateien auf Hosts per SFTP (docs/FILES.md): die gespeicherten SSH-Zugaenge, kurzlebige Sitzungen im Pool, Auftraege (kopieren, verschieben, loeschen), Verlauf.
// Schreiben ist standardmaessig aus: JULOS_FILES_WRITE=on schaltet es ein; JULOS_FILES_MAX_MB und JULOS_FILES_MAX_SESSIONS begrenzen.
builder.Services.AddSingleton(sp => FilesOptions.Parse(builder.Configuration["JULOS_FILES_WRITE"], builder.Configuration["JULOS_FILES_MAX_MB"], builder.Configuration["JULOS_FILES_MAX_SESSIONS"],
    m => sp.GetRequiredService<ILoggerFactory>().CreateLogger("JulOS.Files").LogWarning("{Message}", m)));
builder.Services.AddSingleton(FileSessionFactory.Default);
builder.Services.AddSingleton(sp => new SftpPool(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<FilesOptions>().MaxSessionsPerUser, sp.GetRequiredService<FilesOptions>().Idle));
builder.Services.AddSingleton(sp => new FileJobs(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<FilesOptions>().MaxJobsPerUser));
builder.Services.AddSingleton<FileAuditStore>();
builder.Services.AddSingleton<RootFiles>();
builder.Services.AddSingleton(sp => new FileConfirmations(sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(60)));
builder.Services.AddHostedService<SftpPoolSweeper>();
// Module Caddy und DDNS (nur lesen, standardmaessig aus): JULOS_CADDY, JULOS_DDNS, siehe docs/MODULES.md
builder.Services.AddModules(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));

// Hinter einem Proxy im eigenen Netz (zum Beispiel Caddy) kommen Client-Adresse, Protokoll und Host aus den X-Forwarded-Kopfzeilen. Welche Adressen als Proxy gelten, steht in den
// Einstellungen (Sicherheit) und wird bei jeder Anfrage nachgesehen (SecurityConfig): eine Aenderung gilt ohne Neustart. Die Middleware selbst vertraut jeder Adresse, aber nur
// Anfragen von einem vertrauten Proxy kommen ueberhaupt zu ihr (UseWhen unten).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("0.0.0.0/0"));
    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("::/0"));
});

var app = builder.Build();
var log = app.Logger;
var security = app.Services.GetRequiredService<SecurityConfig>();
app.Use((ctx, next) => { ctx.Items[SecurityEndpoints.PeerItem] = ctx.Connection.RemoteIpAddress; return next(ctx); });      // die Adresse des Gegenuebers, bevor ein Proxy sie ersetzt (Anzeige in den Einstellungen)
app.UseWhen(ctx => security.IsTrustedProxy(ctx.Connection.RemoteIpAddress), branch => branch.UseForwardedHeaders());

await MigrateWithRetryAsync(app.Services.GetRequiredService<Migrator>(), log);
await app.Services.GetRequiredService<InstanceSettings>().LoadAsync();
// Die bisherigen Zugangsdaten der Verbindungen werden zu einem Login je Host (nichts wird geloescht, docs/HOSTS.md); ein Fehler hier darf den Start nicht verhindern
try { var made = await app.Services.GetRequiredService<HostLoginMigration>().RunAsync(); if (made > 0) log.LogInformation("Host-Login aus den Verbindungen übernommen: {Count} Host(s)", made); }
catch (Exception e) { log.LogWarning(e, "Übernahme der Zugangsdaten in den Host-Login fehlgeschlagen; sie wird beim nächsten Start wiederholt"); }

// Die TOTP-Geheimnisse der Konten liegen verschluesselt in der Datenbank (F-10): beim Start werden noch unverschluesselte umgestellt, gelesen werden beide Formen
try { var sealedTotp = await app.Services.GetRequiredService<UserStore>().MigrateTotpAsync(); if (sealedTotp > 0) log.LogInformation("TOTP-Geheimnisse verschlüsselt: {Count} Konto/Konten", sealedTotp); }
catch (Exception e) { log.LogWarning(e, "Verschlüsseln der TOTP-Geheimnisse fehlgeschlagen; es wird beim nächsten Start wiederholt"); }

// Ohne Einstellung (App oder JULOS_TRUSTED_PROXIES) glaubt der Server X-Forwarded-* von jedem privaten Netz: ein Geraet im eigenen LAN, das den Port direkt erreicht, kann die Client-Adresse
// (Sperre pro Adresse, Sitzungsliste) und das Protokoll faelschen (docs/SECURITY-REVIEW.md, F-03, F-06). Deshalb eine laute Warnung, solange es nicht eingeschraenkt ist.
if (security.ProxiesUnset)
    log.LogWarning("Vertraute Proxy-Adressen sind nicht gesetzt: X-Forwarded-For/-Proto/-Host werden von jedem privaten Netz geglaubt. Hinter einem Proxy in der App (Einstellungen, Sicherheit) oder mit JULOS_TRUSTED_PROXIES auf dessen Adresse einschränken (zum Beispiel 192.0.2.10/32) und den Port 8080 nur für den Proxy erreichbar machen.");
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
app.MapHosts();
app.MapHostLogin();
app.MapHostInfo();
app.MapPush();
app.MapHostActions();
app.MapConnections();
app.MapFolders();
app.MapSettings();
app.MapSecurity();
app.MapAgent();
app.MapAgentLink(info);
app.MapSystem();
app.MapFiles();
app.MapRemoteFiles();
app.MapModules();
app.MapNetTools();

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
