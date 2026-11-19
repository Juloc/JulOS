using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Agent;

namespace JulOS.Frontend.Agent;

/// <summary>
/// Der Agent auf der Ziel-Maschine meldet sich von dort aus beim Server (docs/AGENT_API.md): ein einmaliger Einrichtungscode wird gegen ein Token
/// getauscht, mit dem der Agent danach eine WebSocket-Verbindung aufbaut. Diese Wege brauchen keine Sitzung des Browsers (der Agent hat keine),
/// aber den Code oder das Token; alles andere hier (Code erzeugen, Agent entfernen, Status) braucht die Sitzung wie jede Route.
/// </summary>
internal static class AgentLinkEndpoints
{
    public const string LinkPath = "/api/agent-link";
    public const string EnrollPath = "/api/agent-link/enroll";
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static bool IsAgentPath(PathString path) => path.Equals(LinkPath) || path.Equals(EnrollPath);

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    public static void MapAgentLink(this IEndpointRouteBuilder app, AppInfo info)
    {
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AgentLink");

        // Die drei Dateien fuer die Ziel-Maschine (kein Geheimnis, wie der Rest des Quelltexts): so passt, was dort laeuft, zur Version dieses Servers
        app.MapGet("/agent-files/{name}", (string name, HttpContext ctx) =>
        {
            if (name is not ("julos-agent.py" or "julos-clip.py" or "install-linux-agent.sh")) return Results.NotFound();
            var path = Path.Combine(AppContext.BaseDirectory, "agent-files", name);
            if (!File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.File(path, "text/plain; charset=utf-8", fileDownloadName: name);
        });

        // Code gegen Token tauschen (der Code gilt einmal und nur kurz). Wenige Versuche je Adresse und insgesamt: der Code hat 50 Bit.
        app.MapPost(EnrollPath, async (AgentEnrollRequest body, HttpContext ctx, AgentStore store, HitCounter hits) =>
        {
            if (ctx.Request.ContentLength is null or > 1024) return Error(413, "Anfrage zu groß.");
            if (!hits.Allow("enroll:" + Ip(ctx), 10, Minute) || !hits.Allow("enroll:all", 60, Minute)) return Error(429, "Zu viele Versuche, bitte kurz warten.");
            if (!AgentCredentials.LooksLikeCode(body.Code)) return Error(400, "Der Code ist ungültig oder abgelaufen.");
            var redeemed = await store.RedeemAsync(body.Code!, AgentLabels.Clean(body.Version), AgentLabels.Clean(body.Platform), ctx.RequestAborted);
            if (redeemed is not { } r)
            {
                log.LogWarning("Agent-Einrichtung abgelehnt (Code unbekannt oder abgelaufen) von {Ip}", Ip(ctx));
                return Error(400, "Der Code ist ungültig oder abgelaufen.");
            }
            log.LogInformation("Agent eingerichtet: Verbindung {Connection}, Benutzer {User}", r.ConnectionId, r.UserId);
            return Results.Json(new AgentEnrollResponse(r.Token, r.ConnectionId), AppJson.Default.AgentEnrollResponse);
        });

        // Die dauerhafte Verbindung des Agenten: Token im Kopf "Authorization: Bearer ...", nie in der Adresse (sie landet in Protokollen)
        app.Map(LinkPath, async (HttpContext ctx, AgentStore store, AgentLinks links, HitCounter hits) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Error(400, "WebSocket erwartet.");
            if (!hits.Allow("agent-link:" + Ip(ctx), 30, Minute)) return Error(429, "Zu viele Verbindungsversuche.");
            var header = ctx.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : "";
            var agent = await store.FindByTokenAsync(token, ctx.RequestAborted);
            if (agent is null)
            {
                log.LogWarning("Agent-Link abgelehnt (Token unbekannt oder widerrufen) von {Ip}", Ip(ctx));
                return Error(401, "Nicht angemeldet.");
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await links.RunAsync(ws, agent.ConnectionId, agent.UserId, info.Version,
                (version, platform) => store.TouchAsync(agent.ConnectionId, version, platform, CancellationToken.None), ctx.RequestAborted);
            return Results.Empty;
        });

        // Einrichtungscode fuer eine Verbindung des Benutzers (wird nur diesmal angezeigt)
        app.MapPost("/api/connections/{connection:guid}/agent/code", async (Guid connection, HttpContext ctx, AgentStore store, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("agent-code:" + id.UserId, 10, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var made = await store.CreateCodeAsync(id.UserId, connection, ctx.RequestAborted);
            if (made is not { } c) return Error(404, "Verbindung nicht gefunden (einen Agenten gibt es nur für Desktops).");
            log.LogInformation("Agent-Einrichtungscode erzeugt: Verbindung {Connection}, Benutzer {User}", connection, id.UserName);
            return Results.Json(new AgentCodeResponse(c.Code, (int)AgentStore.CodeLifetime.TotalSeconds), AppJson.Default.AgentCodeResponse);
        });

        // Agent entfernen: das Token gilt sofort nicht mehr und eine laufende Verbindung wird getrennt
        app.MapDelete("/api/connections/{connection:guid}/agent", async (Guid connection, HttpContext ctx, AgentStore store, AgentLinks links) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!await store.RevokeAsync(id.UserId, connection, ctx.RequestAborted)) return Error(404, "Kein Agent eingerichtet.");
            links.Disconnect(connection);
            log.LogInformation("Agent entfernt: Verbindung {Connection}, Benutzer {User}", connection, id.UserName);
            return Results.NoContent();
        });

        // Status der eingerichteten Agenten des Benutzers
        app.MapGet("/api/agents", async (HttpContext ctx, AgentStore store, AgentLinks links) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var list = new List<AgentStatusDto>();
            foreach (var a in await store.ListAsync(id.UserId, ctx.RequestAborted))
            {
                var live = links.Get(a.ConnectionId, id.UserId) is not null ? links.Status(a.ConnectionId) : null;
                var version = live?.Version ?? a.Version;
                list.Add(new AgentStatusDto(a.ConnectionId, live is not null, live is not null ? DateTimeOffset.UtcNow : a.LastSeen, version, live?.Platform ?? a.Platform, version is not null && !AgentLinks.VersionOk(version)));
            }
            return Results.Json(new AgentsResponse([.. list]), AppJson.Default.AgentsResponse);
        });
    }
}
