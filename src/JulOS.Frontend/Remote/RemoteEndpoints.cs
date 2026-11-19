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
using JulOS.Integrations.Guacamole;
using JulOS.Integrations.Ssh;

namespace JulOS.Frontend.Remote;

internal static class RemoteEndpoints
{
    public const string WebSocketPath = "/api/remote/ws";
    public const string ScreenWebSocketPath = "/api/remote/screen";
    public const string SshWebSocketPath = "/api/remote/ssh";

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    public static void MapRemote(this IEndpointRouteBuilder app)
    {
        // Schritt 1: Verbindung waehlen und das Passwort tippen -> Einmal-Ticket (30 s, nur im Speicher)
        app.MapPost("/api/remote/ticket", async (TicketRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, GuacTickets tickets, ScreenTickets screenTickets, SshTickets sshTickets, SecretBox box) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (body.Connection is not { } connectionId) return Error(400, "Verbindung fehlt.");
            if (body.Password is { Length: > 256 }) return Error(400, "Das Passwort ist zu lang.");
            var c = await connections.GetAsync(id.UserId, connectionId, ctx.RequestAborted);
            if (c is null) return Error(404, "Verbindung nicht gefunden.");
            if (!policy.Allows(c.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");

            // Ein getipptes Passwort gilt immer; sonst das gespeicherte (nur auf dem Server entschluesselt, es geht nie zum Browser)
            var password = body.Password;
            if (string.IsNullOrEmpty(password) && c.HasPassword)
            {
                var sealedValue = await connections.GetSecretAsync(id.UserId, connectionId, ctx.RequestAborted);
                password = sealedValue is null ? null : box.Decrypt(sealedValue, connectionId);
                if (password is null) return Error(409, "Das gespeicherte Passwort lässt sich nicht lesen. Bitte neu eingeben und speichern.");
            }
            if (string.IsNullOrEmpty(password)) return Error(400, "Passwort fehlt.");

            // Terminal: eigener SSH-Client im Server, der Browser zeichnet den Text selbst (xterm.js). Spalten und Zeilen kommen vom Browser.
            if (c.Kind == "terminal")
            {
                var options = new SshConnectOptions(c.Host, c.Port, c.Username, password,
                    SshProtocol.Clamp(body.Cols ?? 80, SshProtocol.MinCols, SshProtocol.MaxCols), SshProtocol.Clamp(body.Rows ?? 24, SshProtocol.MinRows, SshProtocol.MaxRows));
                var (owner, connection) = (id.UserId, c.Id);
                var sshTicket = sshTickets.Create(id.UserId, new SshTarget(owner, options, c.HostKey, body.AcceptHostKey,
                    (key, ct) => connections.SetHostKeyAsync(owner, connection, key, ct), SessionRegistry.SlotOf(owner, connection, body.Sid)));
                return sshTicket is null ? Error(429, "Zu viele offene Verbindungen.") : Results.Json(new TicketResponse(sshTicket, c.Kind, options.Cols, options.Rows, "ssh"), AppJson.Default.TicketResponse);
            }

            // Remote-Aufloesung: Breite Vielfaches von 4, Hoehe von 2, sinnvolle Grenzen
            var w = Math.Clamp(body.Width ?? 900, 400, 4096) & ~3;
            var h = Math.Clamp(body.Height ?? 1600, 300, 4096) & ~1;
            var dpi = Math.Clamp(body.Dpi ?? 96, 72, 288);
            // Die Engine der Verbindung gilt, ausser die Seite verlangt ausdruecklich eine andere (Test-Schalter im Browser oder Rueckfall nach
            // einem misslungenen Stream). Der Stream (eigener RDP-Client, H.264) ist nur fuer Desktops und nur, wenn der Kodierer da ist; sonst
            // bleibt es bei Guacamole, und die Antwort sagt, welche Engine es wurde.
            var wanted = body.Engine ?? c.Engine;
            if (c.Kind == "desktop" && string.Equals(wanted, ConnectionRules.EngineStream, StringComparison.Ordinal) && ScreenStream.IsAvailable)
            {
                var options = new RdpOptions
                {
                    Host = c.Host, Port = c.Port, UserName = c.Username, Password = password, Width = w & ~1, Height = h & ~1,
                    KeyboardLayout = 0x407,
                };
                var screenTicket = screenTickets.Create(id.UserId, new ScreenTarget(options, SessionRegistry.SlotOf(id.UserId, c.Id, body.Sid)));
                return screenTicket is null ? Error(429, "Zu viele offene Verbindungen.") : Results.Json(new TicketResponse(screenTicket, c.Kind, w & ~1, h & ~1, "stream"), AppJson.Default.TicketResponse);
            }

            GuacSpec? spec = c.Kind switch
            {
                "desktop" => GuacSpec.Desktop(c.Host, c.Port, c.Username, password, w, h, dpi),
                _ => null,
            };
            if (spec is null) return Error(400, "Diese Art von Verbindung wird noch nicht unterstützt.");
            spec = spec with { Slot = SessionRegistry.SlotOf(id.UserId, c.Id, body.Sid) };
            var ticket = tickets.Create(id.UserId, spec);
            return ticket is null ? Error(429, "Zu viele offene Verbindungen.") : Results.Json(new TicketResponse(ticket, c.Kind, w, h), AppJson.Default.TicketResponse);
        });

        // Schritt 2: WebSocket mit dem Ticket. Die Sitzung wurde schon vom ApiGuard geprueft (ohne Cookie-Erneuerung).
        app.Map(WebSocketPath, async (HttpContext ctx, GuacTickets tickets, GuacRelay relay, AuthService auth) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Error(400, "WebSocket erwartet.");
            // Fremde Seiten duerfen keinen WebSocket mit unserem Cookie aufbauen (zusaetzlich zu SameSite=Strict)
            if (!ApiGuard.SameOrigin(ctx.Request)) return Error(403, "Anfrage abgelehnt.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var spec = tickets.Take(ctx.Request.Query["ticket"].ToString(), id.UserId);
            if (spec is null) return Error(403, "Ticket ungültig oder abgelaufen.");

            // Der Guacamole-Tunnel im Browser fragt das Unterprotokoll "guacamole" an und bricht ab, wenn der Server keins waehlt
            var accept = new WebSocketAcceptContext { SubProtocol = ctx.WebSockets.WebSocketRequestedProtocols.Contains("guacamole") ? "guacamole" : null };
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync(accept);
            await relay.RunAsync(ws, spec, ct => auth.IsSessionAliveAsync(id.Id, ct), ctx.RequestAborted, owner: id.UserId);
            return Results.Empty;
        });
        // Bildschirm-Stream: dasselbe Ticket-Verfahren, aber ohne Unterprotokoll, nur binaere Nachrichten (siehe ScreenProtocol)
        app.Map(ScreenWebSocketPath, async (HttpContext ctx, ScreenTickets tickets, ScreenRelay relay, AuthService auth) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Error(400, "WebSocket erwartet.");
            if (!ApiGuard.SameOrigin(ctx.Request)) return Error(403, "Anfrage abgelehnt.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var target = tickets.Take(ctx.Request.Query["ticket"].ToString(), id.UserId);
            if (target is null) return Error(403, "Ticket ungültig oder abgelaufen.");
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await relay.RunAsync(ws, target, ct => auth.IsSessionAliveAsync(id.Id, ct), ctx.RequestAborted, owner: id.UserId,
                clientCodecs: ScreenProtocol.ParseCodecs(ctx.Request.Query["codecs"]));
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
