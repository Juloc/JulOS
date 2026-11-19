using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Reachability;

namespace JulOS.Frontend.Tools;

internal sealed record ToolTargetsResponse(string[] Targets);
internal sealed record PingRequest(string? Host, int? Count);
internal sealed record DnsRequest(string? Host);
internal sealed record PortsRequest(string? Host, int[]? Ports);
internal sealed record PortsResponse(string Host, PortOutcome[] Results);

/// <summary>
/// Netzwerkwerkzeuge: Ping, DNS-Abfrage und Port-Test gegen die Ziele der gespeicherten Verbindungen (nie gegen frei eingegebene Adressen, siehe NetToolRules).
/// Alles braucht eine Sitzung (ApiGuard) und die Kopfzeile fuer Aenderungen; Aufrufe sind je Benutzer begrenzt und werden ins Log geschrieben.
/// Mit <c>JULOS_NETTOOLS=off</c> antworten alle Wege 404.
/// </summary>
internal static class NetToolEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    public static void MapNetTools(this WebApplication app)
    {
        var enabled = !string.Equals(app.Configuration["JULOS_NETTOOLS"], "off", StringComparison.OrdinalIgnoreCase);
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NetTools");

        // Die Ziele, die die Werkzeuge nehmen duerfen (Auswahl in der Oberflaeche)
        app.MapGet("/api/tools/targets", async (HttpContext ctx, ConnectionStore connections, HostPolicy policy) =>
        {
            if (!enabled) return Error(404, "Die Netzwerkwerkzeuge sind auf diesem Server nicht eingeschaltet.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var hosts = (await connections.ListAsync(id.UserId, ctx.RequestAborted)).Select(c => c.Host);
            return Results.Json(new ToolTargetsResponse([.. NetToolRules.Targets(hosts, policy)]), AppJson.Default.ToolTargetsResponse);
        });

        // gemeinsam: Schalter, Rate, Ziel, Platz fuer einen Lauf
        async Task<(string? Host, IDisposable? Slot, IResult? Refused)> Enter(HttpContext ctx, string tool, string? requested, ConnectionStore connections, HostPolicy policy, HitCounter hits, NetTools tools)
        {
            if (!enabled) return (null, null, Error(404, "Die Netzwerkwerkzeuge sind auf diesem Server nicht eingeschaltet."));
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("nettools:" + id.UserId, 30, Minute)) return (null, null, Error(429, "Zu viele Anfragen, bitte kurz warten."));
            var saved = (await connections.ListAsync(id.UserId, ctx.RequestAborted)).Select(c => c.Host);
            var host = NetToolRules.Resolve(requested, saved, policy);
            if (host is null) { log.LogWarning("Netzwerkwerkzeug {Tool} abgelehnt für {User}: Ziel nicht erlaubt", tool, id.UserName); return (null, null, Error(403, "Dieses Ziel ist nicht erlaubt. Es muss als Verbindung gespeichert sein.")); }
            var slot = await tools.TryEnterAsync();
            if (slot is null) return (null, null, Error(429, "Es laufen schon mehrere Prüfungen, bitte gleich noch einmal."));
            log.LogInformation("Netzwerkwerkzeug {Tool} gegen {Host} für {User}", tool, host, id.UserName);
            return (host, slot, null);
        }

        app.MapPost("/api/tools/ping", async (PingRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, HitCounter hits, NetTools tools) =>
        {
            var (host, slot, refused) = await Enter(ctx, "ping", body.Host, connections, policy, hits, tools);
            if (refused is not null) return refused;
            using (slot) return Results.Json(await tools.PingAsync(host!, NetToolRules.PingCount(body.Count), ctx.RequestAborted), AppJson.Default.PingOutcome);
        });

        app.MapPost("/api/tools/dns", async (DnsRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, HitCounter hits, NetTools tools) =>
        {
            var (host, slot, refused) = await Enter(ctx, "dns", body.Host, connections, policy, hits, tools);
            if (refused is not null) return refused;
            using (slot) return Results.Json(await tools.DnsAsync(host!, ctx.RequestAborted), AppJson.Default.DnsOutcome);
        });

        app.MapPost("/api/tools/ports", async (PortsRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, HitCounter hits, NetTools tools) =>
        {
            var (ports, error) = NetToolRules.Ports(body.Ports);
            if (ports is null) return Error(400, error!);
            var (host, slot, refused) = await Enter(ctx, "ports", body.Host, connections, policy, hits, tools);
            if (refused is not null) return refused;
            using (slot) return Results.Json(new PortsResponse(host!, await tools.PortsAsync(host!, ports, ctx.RequestAborted)), AppJson.Default.PortsResponse);
        });
    }
}
