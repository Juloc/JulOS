using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Agent;

namespace JulOS.Frontend.Agent;

/// <summary>
/// Der Agent auf der Ziel-Maschine meldet sich von dort aus beim Server (docs/AGENT_API.md): ein einmaliger Einrichtungscode (fuer einen Host) wird gegen ein Token
/// getauscht, mit dem der Agent danach eine WebSocket-Verbindung aufbaut. Diese Wege brauchen keine Sitzung des Browsers (der Agent hat keine),
/// aber den Code oder das Token; alles andere hier (Code erzeugen, Agent entfernen) braucht die Sitzung wie jede Route.
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
            var redeemed = await store.RedeemAsync(body.Code!, AgentLabels.Clean(body.Version), AgentLabels.Clean(body.Platform), AgentLabels.CleanText(body.Hostname), AgentLabels.CleanText(body.Os), ctx.RequestAborted);
            if (redeemed is not { } r)
            {
                log.LogWarning("Agent-Einrichtung abgelehnt (Code unbekannt oder abgelaufen) von {Ip}", Ip(ctx));
                return Error(400, "Der Code ist ungültig oder abgelaufen.");
            }
            log.LogInformation("Agent eingerichtet: Host {Host}, Benutzer {User}", r.HostId, r.UserId);
            return Results.Json(new AgentEnrollResponse(r.Token, r.HostId), AppJson.Default.AgentEnrollResponse);
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
            await links.RunAsync(ws, agent.HostId, agent.UserId, info.Version,
                a => store.TouchAsync(agent.HostId, a.Version, a.Platform, a.Hostname, a.Os, CancellationToken.None), ctx.RequestAborted);
            return Results.Empty;
        });

        // Einrichtungscode fuer einen Host des Benutzers (wird nur diesmal angezeigt)
        app.MapPost("/api/hosts/{host:guid}/agent/code", async (Guid host, HttpContext ctx, AgentStore store, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("agent-code:" + id.UserId, 10, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var made = await store.CreateCodeAsync(id.UserId, host, ctx.RequestAborted);
            if (made is not { } c) return Error(404, "Host nicht gefunden.");
            log.LogInformation("Agent-Einrichtungscode erzeugt: Host {Host}, Benutzer {User}", host, id.UserName);
            return Results.Json(new AgentCodeResponse(c.Code, (int)AgentStore.CodeLifetime.TotalSeconds), AppJson.Default.AgentCodeResponse);
        });

        // Agent entfernen: das Token gilt sofort nicht mehr und eine laufende Verbindung wird getrennt
        app.MapDelete("/api/hosts/{host:guid}/agent", async (Guid host, HttpContext ctx, AgentStore store, AgentLinks links) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!await store.RevokeAsync(id.UserId, host, ctx.RequestAborted)) return Error(404, "Kein Agent eingerichtet.");
            links.Disconnect(host);
            log.LogInformation("Agent entfernt: Host {Host}, Benutzer {User}", host, id.UserName);
            return Results.NoContent();
        });
    }
}
