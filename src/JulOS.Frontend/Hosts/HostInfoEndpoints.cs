using System.Text.Json.Serialization.Metadata;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Hosts;

/// <summary>
/// Systemwerte, Updates, fehlgeschlagene Dienste und Docker-Container eines Hosts, vom Host-Agenten gelesen (docs/HOST-AGENT.md). Nur lesend, alles braucht eine Sitzung (ApiGuard),
/// ein fremder Host ist 404. Die Anfragen an den Agenten sind feste Pfade (siehe die Felder unten), der Browser nennt keinen. Antworten kommen aus dem Speicher, hoechstens alle 60 s je Host neu.
/// </summary>
internal static class HostInfoEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static readonly HostQuery<HostSysinfo> Status = new("status", "sysinfo", "/v1/host/sysinfo", TimeSpan.FromSeconds(12), HostReportParser.Sysinfo);
    public static readonly HostQuery<HostUpdates> Updates = new("updates", "updates", "/v1/host/updates", TimeSpan.FromSeconds(50), HostReportParser.Updates);
    public static readonly HostQuery<HostServices> Services = new("services", "services", "/v1/host/services", TimeSpan.FromSeconds(15), HostReportParser.Services);
    public static readonly HostQuery<HostDocker> Docker = new("docker", "docker", "/v1/host/docker", TimeSpan.FromSeconds(15), HostReportParser.Docker);

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static async Task<IResult> Run<T, R>(Guid host, HttpContext ctx, HostInfoService svc, HitCounter hits, HostQuery<T> query, Func<HostInfoResult<T>, R> wrap, JsonTypeInfo<R> info) where T : class
    {
        var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
        if (!hits.Allow("host-info:" + id.UserId, 90, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
        var fresh = ctx.Request.Query["fresh"] == "1";
        var result = await svc.GetAsync(id.UserId, host, query, fresh, ctx.RequestAborted);
        return result is null ? Error(404, "Host nicht gefunden.") : Results.Json(wrap(result), info);
    }

    public static void MapHostInfo(this IEndpointRouteBuilder app)
    {
        // ?fresh=1: ausdruecklich neu lesen (hoechstens alle 15 s je Host und Art, sonst gilt der Speicher)
        app.MapGet("/api/hosts/{host:guid}/status", (Guid host, HttpContext ctx, HostInfoService svc, HitCounter hits) =>
            Run(host, ctx, svc, hits, Status, r => new HostStatusResponse(r.State, r.At, r.Stale, r.Error, r.Data), AppJson.Default.HostStatusResponse));
        app.MapGet("/api/hosts/{host:guid}/updates", (Guid host, HttpContext ctx, HostInfoService svc, HitCounter hits) =>
            Run(host, ctx, svc, hits, Updates, r => new HostUpdatesResponse(r.State, r.At, r.Stale, r.Error, r.Data), AppJson.Default.HostUpdatesResponse));
        app.MapGet("/api/hosts/{host:guid}/services", (Guid host, HttpContext ctx, HostInfoService svc, HitCounter hits) =>
            Run(host, ctx, svc, hits, Services, r => new HostServicesResponse(r.State, r.At, r.Stale, r.Error, r.Data), AppJson.Default.HostServicesResponse));
        app.MapGet("/api/hosts/{host:guid}/docker", (Guid host, HttpContext ctx, HostInfoService svc, HitCounter hits) =>
            Run(host, ctx, svc, hits, Docker, r => new HostDockerResponse(r.State, r.At, r.Stale, r.Error, r.Data), AppJson.Default.HostDockerResponse));
    }
}
