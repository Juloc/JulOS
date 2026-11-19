using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Agent;

namespace JulOS.Frontend.Agent;

internal enum AgentRoute
{
    /// <summary>Kein Agent eingerichtet: der lokale Socket (Server und Ziel-Maschine sind derselbe Rechner).</summary>
    Socket,
    /// <summary>Der Agent dieser Verbindung ist verbunden.</summary>
    Link,
    /// <summary>Fuer diese Verbindung ist ein Agent eingerichtet, er ist aber gerade nicht da (der lokale Socket gehoert dann nicht dazu).</summary>
    Offline,
}

internal sealed class AgentTarget(AgentRoute route, AgentLink? link, AgentClient socket, IResult? rejection)
{
    public AgentRoute Route => route;
    public AgentLink? Link => link;
    /// <summary>Gesetzt, wenn die Anfrage schon hier abgelehnt wurde (ungueltige oder fremde Verbindung).</summary>
    public IResult? Rejection => rejection;

    public Task<AgentReply> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct) =>
        link is not null ? link.RequestAsync(method, path, body, AgentClient.DefaultTimeout, ct) : socket.SendAsync(method, path, body, ct);
}

/// <summary>
/// Waehlt den Weg zum Agenten fuer einen Aufruf des Browsers. Der Browser nennt die Verbindung mit <c>?c=&lt;Kennung&gt;</c>; sie muss dem angemeldeten
/// Benutzer gehoeren. Ist ihr Agent verbunden, geht es ueber seinen Link; ist einer eingerichtet, aber getrennt, gibt es keine Antwort (nie ein
/// anderer Agent); sonst, und ohne <c>c</c>, wie bisher ueber den lokalen Socket.
/// </summary>
internal sealed class AgentRouter(AgentLinks links, AgentStore store, AgentClient socket)
{
    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    public async Task<AgentTarget> ResolveAsync(HttpContext ctx)
    {
        var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
        var raw = ctx.Request.Query["c"].ToString();
        if (raw.Length == 0) return new AgentTarget(AgentRoute.Socket, null, socket, null);
        if (!Guid.TryParse(raw, out var connection)) return new AgentTarget(AgentRoute.Socket, null, socket, Error(400, "Ungültige Verbindung."));
        if (links.Get(connection, id.UserId) is { } link) return new AgentTarget(AgentRoute.Link, link, socket, null);
        return await store.IsEnrolledAsync(id.UserId, connection, ctx.RequestAborted) switch
        {
            null => new AgentTarget(AgentRoute.Socket, null, socket, Error(404, "Verbindung nicht gefunden.")),
            true => new AgentTarget(AgentRoute.Offline, null, socket, null),
            false => new AgentTarget(AgentRoute.Socket, null, socket, null),
        };
    }
}
