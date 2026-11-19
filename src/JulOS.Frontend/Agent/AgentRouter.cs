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
    /// <summary>Der Agent des Hosts dieser Verbindung ist verbunden.</summary>
    Link,
    /// <summary>Fuer den Host ist ein Agent eingerichtet, er ist aber gerade nicht da (der lokale Socket gehoert dann nicht dazu).</summary>
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
/// Waehlt den Weg zum Agenten fuer einen Aufruf des Browsers. Der Agent gehoert zum Host: der Browser nennt entweder die Verbindung (<c>?c=&lt;Kennung&gt;</c>,
/// ihr Host zaehlt) oder den Host selbst (<c>?h=&lt;Kennung&gt;</c>); beides muss dem angemeldeten Benutzer gehoeren. Ist der Agent des Hosts verbunden,
/// geht es ueber seinen Link; ist einer eingerichtet, aber getrennt, gibt es keine Antwort (nie ein anderer Agent); sonst, und ohne <c>c</c> und <c>h</c>,
/// wie bisher ueber den lokalen Socket.
/// </summary>
internal sealed class AgentRouter(AgentLinks links, HostStore hosts, AgentClient socket)
{
    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private AgentTarget Rejected(IResult error) => new(AgentRoute.Socket, null, socket, error);

    public async Task<AgentTarget> ResolveAsync(HttpContext ctx)
    {
        var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
        var rawHost = ctx.Request.Query["h"].ToString();
        var rawConnection = ctx.Request.Query["c"].ToString();
        if (rawHost.Length == 0 && rawConnection.Length == 0) return new AgentTarget(AgentRoute.Socket, null, socket, null);

        Guid host; bool enrolled;
        if (rawHost.Length > 0)
        {
            if (!Guid.TryParse(rawHost, out host)) return Rejected(Error(400, "Ungültiger Host."));
            if (await hosts.AgentEnrolledAsync(id.UserId, host, ctx.RequestAborted) is not { } e) return Rejected(Error(404, "Host nicht gefunden."));
            enrolled = e;
        }
        else
        {
            if (!Guid.TryParse(rawConnection, out var connection)) return Rejected(Error(400, "Ungültige Verbindung."));
            if (await hosts.OfConnectionAsync(id.UserId, connection, ctx.RequestAborted) is not { } of) return Rejected(Error(404, "Verbindung nicht gefunden."));
            (host, enrolled) = of;
        }
        if (links.Get(host, id.UserId) is { } link) return new AgentTarget(AgentRoute.Link, link, socket, null);
        return new AgentTarget(enrolled ? AgentRoute.Offline : AgentRoute.Socket, null, socket, null);
    }
}
