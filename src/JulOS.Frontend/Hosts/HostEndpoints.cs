using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Agent;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Agent;
using JulOS.Integrations.Reachability;

namespace JulOS.Frontend.Hosts;

/// <summary>
/// Hosts: Rechner oder Server mit eigenen Verbindungen und hoechstens einem eigenen Agenten (docs/HOSTS.md). Alles braucht eine Sitzung (ApiGuard)
/// und liefert nur Hosts des angemeldeten Benutzers. Den Agenten eines Hosts verwalten die Wege in AgentLinkEndpoints.
/// </summary>
internal static class HostEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    /// <summary>
    /// Der Zustand eines Hosts: der Agent (Link, sonst die letzte Meldung aus der Datenbank) und je Verbindung, ob ihr Port antwortet.
    /// Der Host gilt als online, wenn sein Agent verbunden ist oder mindestens ein Dienst antwortet.
    /// </summary>
    private static async Task<HostDto> Describe(HostRecord host, AgentRecord? agent, IReadOnlyList<ConnectionRecord> connections, Guid userId, AgentLinks links, AgentRelease release, TcpProbe probe, HostPolicy policy, CancellationToken ct)
    {
        var live = links.Get(host.Id, userId) is not null ? links.Status(host.Id) : null;
        var version = live?.Version ?? agent?.Version;
        var services = await Task.WhenAll(connections.Select(async c => new HostServiceDto(c.Id, c.Kind, policy.Allows(c.Host) ? await probe.IsUpAsync(c.Host, c.Port, ct) : null)));
        var latest = release.Latest();
        var agentDto = agent is null ? null : new HostAgentDto(live is not null, live is not null ? DateTimeOffset.UtcNow : agent.LastSeen, version, live?.Platform ?? agent.Platform, version is not null && !AgentLinks.VersionOk(version),
            latest?.Version, AgentLinks.IsNewer(latest?.Version, version), AgentLinks.CanUpdate(version), agent.AutoUpdate,
            AgentLabels.KindOf(live?.Platform ?? agent.Platform), [.. live?.Caps ?? []], [.. live?.Allow ?? []], agent.Control);
        var online = live is not null || services.Any(s => s.Online == true);
        return new HostDto(host.Id, host.Name, online, live?.Hostname ?? agent?.Hostname, live?.Os ?? agent?.Os, agentDto, services);
    }

    public static void MapHosts(this IEndpointRouteBuilder app)
    {
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Hosts");

        // Alle Hosts mit Zustand. Ein Benutzer hat immer mindestens einen (ohne einen wird der Standard-Host angelegt).
        // Die Liste loest Verbindungsversuche zu den gespeicherten Zielen aus (kurz gemerkt, TcpProbe), daher die Grenze je Benutzer.
        app.MapGet("/api/hosts", async (HttpContext ctx, HostStore hosts, ConnectionStore connections, AgentStore agents, AgentLinks links, AgentRelease release, TcpProbe probe, HostPolicy policy, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("hosts:" + id.UserId, 30, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var ct = ctx.RequestAborted;
            var list = await hosts.ListAsync(id.UserId, ct);
            if (list.Count == 0) { await hosts.EnsureDefaultAsync(id.UserId, ct); list = await hosts.ListAsync(id.UserId, ct); }
            var byHost = (await connections.ListAsync(id.UserId, ct)).ToLookup(c => c.HostId);
            var agentOf = (await agents.ListAsync(id.UserId, ct)).ToDictionary(a => a.HostId);
            var described = await Task.WhenAll(list.Select(h => Describe(h, agentOf.GetValueOrDefault(h.Id), [.. byHost[h.Id]], id.UserId, links, release, probe, policy, ct)));
            return Results.Json(new HostsResponse(described), AppJson.Default.HostsResponse);
        });

        app.MapPost("/api/hosts", async (HostRequest body, HttpContext ctx, HostStore hosts) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (name, error) = HostRules.ValidateName(body.Name);
            if (name is null) return Error(400, error!);
            if (await hosts.CountAsync(id.UserId, ctx.RequestAborted) >= HostRules.MaxPerUser) return Error(400, "Zu viele Hosts.");
            var h = await hosts.CreateAsync(id.UserId, name, ctx.RequestAborted);
            log.LogInformation("Host angelegt: {Host}, Benutzer {User}", h.Id, id.UserName);
            return Results.Json(new HostDto(h.Id, h.Name, false, null, null, null, []), AppJson.Default.HostDto);
        });

        app.MapPut("/api/hosts/{host:guid}", async (Guid host, HostRequest body, HttpContext ctx, HostStore hosts) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (name, error) = HostRules.ValidateName(body.Name);
            if (name is null) return Error(400, error!);
            var h = await hosts.RenameAsync(id.UserId, host, name, ctx.RequestAborted);
            return h is null ? Error(404, "Host nicht gefunden.") : Results.Json(new HostDto(h.Id, h.Name, false, null, null, null, []), AppJson.Default.HostDto);
        });

        // Nur ein Host ohne Verbindungen: sonst gingen sie nebenbei verloren (erst verschieben oder loeschen). Sein Agent wird mit entfernt und getrennt.
        app.MapDelete("/api/hosts/{host:guid}", async (Guid host, HttpContext ctx, HostStore hosts, AgentLinks links) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            switch (await hosts.DeleteAsync(id.UserId, host, ctx.RequestAborted))
            {
                case HostDelete.NotFound: return Error(404, "Host nicht gefunden.");
                case HostDelete.HasConnections: return Error(409, "Der Host hat noch Verbindungen: erst verschieben oder löschen.");
            }
            links.Disconnect(host);
            log.LogInformation("Host gelöscht: {Host}, Benutzer {User}", host, id.UserName);
            return Results.NoContent();
        });
    }
}
