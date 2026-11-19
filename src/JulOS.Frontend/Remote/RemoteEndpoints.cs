using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend.Auth;
using JulOS.Integrations;
using JulOS.Integrations.Display;
using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Rdp.Drive;
using JulOS.Integrations.Ssh;

namespace JulOS.Frontend.Remote;

internal static class RemoteEndpoints
{
    public const string ScreenWebSocketPath = "/api/remote/screen";
    public const string SshWebSocketPath = "/api/remote/ssh";

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    public static void MapRemote(this IEndpointRouteBuilder app)
    {
        // Schritt 1: Verbindung waehlen und das Passwort tippen -> Einmal-Ticket (30 s, nur im Speicher)
        app.MapPost("/api/remote/ticket", async (TicketRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, ScreenTickets screenTickets, SshTickets sshTickets, SecretBox box, DriveRoot drives, HitCounter hits, QuickTrust quickTrust) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (body.Password is { Length: > 256 }) return Error(400, "Das Passwort ist zu lang.");
            ConnectionRecord c;
            string? password = body.Password;
            Func<string, CancellationToken, Task> saveHostKey, saveCert;
            var owner = id.UserId;
            if (body.Quick is { } q)
            {
                // Quick Connect: dieselben Regeln und dieselbe Erlaubnisliste wie bei gespeicherten Verbindungen; nichts kommt in die Datenbank, das Passwort gilt nur fuer diese Sitzung
                var (v, quickError) = ConnectionRules.Validate("Quick Connect", q.Kind, q.Host, q.Port, q.Username);
                if (v is null) return Error(400, quickError!);
                if (!policy.Allows(v.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
                if (string.IsNullOrEmpty(password)) return Error(400, "Passwort fehlt.");
                if (!hits.Allow("quick:" + owner, QuickConnect.MaxPerMinute, TimeSpan.FromMinutes(1))) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
                var known = quickTrust.Get(owner, v.Kind, v.Host, v.Port);
                c = new ConnectionRecord(QuickConnect.IdOf(v.Kind, v.Host, v.Port, v.Username), v.Host, v.Kind, v.Host, v.Port, v.Username, false, Guid.Empty,
                    HostKey: v.Kind == "terminal" ? known : null, CertFingerprint: v.Kind == "desktop" ? known : null);
                saveHostKey = saveCert = (value, _) => { quickTrust.Set(owner, v.Kind, v.Host, v.Port, value); return Task.CompletedTask; };
            }
            else
            {
                if (body.Connection is not { } connectionId) return Error(400, "Verbindung fehlt.");
                var stored = await connections.GetAsync(owner, connectionId, ctx.RequestAborted);
                if (stored is null) return Error(404, "Verbindung nicht gefunden.");
                c = stored;
                if (!policy.Allows(c.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
                saveHostKey = (key, ct) => connections.SetHostKeyAsync(owner, connectionId, key, ct);
                saveCert = (fingerprint, ct) => connections.SetCertFingerprintAsync(owner, connectionId, fingerprint, ct);

                // Ein getipptes Passwort gilt immer; sonst das gespeicherte (nur auf dem Server entschluesselt, es geht nie zum Browser)
                if (string.IsNullOrEmpty(password) && c.HasPassword)
                {
                    var sealedValue = await connections.GetSecretAsync(owner, connectionId, ctx.RequestAborted);
                    password = sealedValue is null ? null : box.Decrypt(sealedValue, connectionId);
                    if (password is null) return Error(409, "Das gespeicherte Passwort lässt sich nicht lesen. Bitte neu eingeben und speichern.");
                }
                if (string.IsNullOrEmpty(password)) return Error(400, "Passwort fehlt.");
            }

            // Terminal: eigener SSH-Client im Server, der Browser zeichnet den Text selbst (xterm.js). Spalten und Zeilen kommen vom Browser.
            if (c.Kind == "terminal")
            {
                var options = new SshConnectOptions(c.Host, c.Port, c.Username, password,
                    SshProtocol.Clamp(body.Cols ?? 80, SshProtocol.MinCols, SshProtocol.MaxCols), SshProtocol.Clamp(body.Rows ?? 24, SshProtocol.MinRows, SshProtocol.MaxRows));
                var sshTicket = sshTickets.Create(id.UserId, new SshTarget(owner, options, c.HostKey, body.AcceptHostKey, saveHostKey, SessionRegistry.SlotOf(owner, c.Id, body.Sid)));
                return sshTicket is null ? Error(429, "Zu viele offene Verbindungen.") : Results.Json(new TicketResponse(sshTicket, c.Kind, options.Cols, options.Rows, "ssh"), AppJson.Default.TicketResponse);
            }

            // Remote-Aufloesung: Breite Vielfaches von 4, Hoehe von 2, sinnvolle Grenzen
            var w = Math.Clamp(body.Width ?? 900, 400, 4096) & ~3;
            var h = Math.Clamp(body.Height ?? 1600, 300, 4096) & ~1;
            // Desktops laufen immer ueber die eigene Strom-Engine (RDP-Client, H.264); ohne Kodierer auf diesem Server gibt es keine Desktop-Verbindung.
            if (!ScreenStream.IsAvailable) return Error(503, "Die Bildübertragung ist auf diesem Server nicht verfügbar: " + ScreenStream.UnavailableReason);
            // Zertifikat des Ziels: beim ersten Mal merken, danach muss es dasselbe sein (Warnung mit Bestaetigung im Browser, siehe CertificateTrust)
            var trust = new CertificateTrust(c.CertFingerprint, body.AcceptCert, saveCert);
            var rdp = new RdpOptions
            {
                Host = c.Host, Port = c.Port, UserName = c.Username, Password = password, Width = w & ~1, Height = h & ~1,
                KeyboardLayout = 0x407, CertificateCheck = trust.Check, Sound = true,
            };
            // Laufwerk: nur, wenn die Verbindung es verlangt (beide Schalter standardmaessig aus) und der Server eine Wurzel hat; die Wurzel kommt nur aus der Server-Einstellung
            var drive = c.DriveShare ? drives.For(c.DriveWrite) : null;
            var screenTicket = screenTickets.Create(id.UserId, new ScreenTarget(rdp, SessionRegistry.SlotOf(id.UserId, c.Id, body.Sid), Trust: trust, Drive: drive));
            return screenTicket is null ? Error(429, "Zu viele offene Verbindungen.") : Results.Json(new TicketResponse(screenTicket, c.Kind, w & ~1, h & ~1, "stream", drive is null ? null : drive.Writable ? "rw" : "ro"), AppJson.Default.TicketResponse);
        });

        // Schritt 2 (Desktop): WebSocket mit dem Ticket, nur binaere Nachrichten (siehe ScreenProtocol). Die Sitzung wurde schon vom ApiGuard geprueft (ohne Cookie-Erneuerung).
        app.Map(ScreenWebSocketPath, async (HttpContext ctx, ScreenTickets tickets, ScreenRelay relay, AuthService auth) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Error(400, "WebSocket erwartet.");
            if (!ApiGuard.SameOrigin(ctx.Request)) return Error(403, "Anfrage abgelehnt.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            // Entweder ein Ticket (neue Sitzung) oder das Token einer erhaltenen Sitzung (Wiederaufnahme nach einem Abriss, kein Passwort noetig:
            // das Token gehoert genau diesem Benutzer, gilt nur solange die Sitzung wartet und ist nicht erratbar)
            var resume = ctx.Request.Query["resume"].ToString();
            ScreenTarget? target = null;
            if (resume.Length is 0 or > 64)
            {
                resume = "";
                target = tickets.Take(ctx.Request.Query["ticket"].ToString(), id.UserId);
                if (target is null) return Error(403, "Ticket ungültig oder abgelaufen.");
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await relay.RunAsync(ws, target, ct => auth.IsSessionAliveAsync(id.Id, ct), ctx.RequestAborted, owner: id.UserId,
                clientCodecs: ScreenProtocol.ParseCodecs(ctx.Request.Query["codecs"]), resume: resume);
            return Results.Empty;
        });
        // Terminal: dasselbe Ticket-Verfahren, binaeres Protokoll (siehe SshProtocol)
        app.Map(SshWebSocketPath, async (HttpContext ctx, SshTickets tickets, SshRelay relay, AuthService auth) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Error(400, "WebSocket erwartet.");
            if (!ApiGuard.SameOrigin(ctx.Request)) return Error(403, "Anfrage abgelehnt.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var target = tickets.Take(ctx.Request.Query["ticket"].ToString(), id.UserId);
            if (target is null) return Error(403, "Ticket ungültig oder abgelaufen.");
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await relay.RunAsync(ws, target, ct => auth.IsSessionAliveAsync(id.Id, ct), ctx.RequestAborted);
            return Results.Empty;
        });
    }
}
