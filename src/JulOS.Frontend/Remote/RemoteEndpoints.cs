using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Guacamole;

namespace JulOS.Frontend.Remote;

internal static class RemoteEndpoints
{
    public const string WebSocketPath = "/api/remote/ws";

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    public static void MapRemote(this IEndpointRouteBuilder app)
    {
        // Schritt 1: Verbindung waehlen und das Passwort tippen -> Einmal-Ticket (30 s, nur im Speicher)
        app.MapPost("/api/remote/ticket", async (TicketRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, GuacTickets tickets) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (body.Connection is not { } connectionId) return Error(400, "Verbindung fehlt.");
            if (string.IsNullOrEmpty(body.Password) || body.Password.Length > 256) return Error(400, "Passwort fehlt.");
            var c = await connections.GetAsync(id.UserId, connectionId, ctx.RequestAborted);
            if (c is null) return Error(404, "Verbindung nicht gefunden.");
            if (!policy.Allows(c.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");

            // Remote-Aufloesung: Breite Vielfaches von 4, Hoehe von 2, sinnvolle Grenzen
            var w = Math.Clamp(body.Width ?? 900, 400, 4096) & ~3;
            var h = Math.Clamp(body.Height ?? 1600, 300, 4096) & ~1;
            var dpi = Math.Clamp(body.Dpi ?? 96, 72, 288);
            GuacSpec? spec = c.Kind switch
            {
                "desktop" => GuacSpec.Desktop(c.Host, c.Port, c.Username, body.Password, w, h, dpi),
                "terminal" => GuacSpec.Terminal(c.Host, c.Port, c.Username, body.Password, w, h, dpi),
                _ => null,
            };
            if (spec is null) return Error(400, "Diese Art von Verbindung wird noch nicht unterstützt.");
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
            await relay.RunAsync(ws, spec, ct => auth.IsSessionAliveAsync(id.Id, ct), ctx.RequestAborted);
            return Results.Empty;
        });
    }
}
