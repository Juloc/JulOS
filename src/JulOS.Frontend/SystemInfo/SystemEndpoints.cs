using JulOS.Backend.Logic;

namespace JulOS.Frontend.SystemInfo;

/// <summary>Nur lesen: Auslastung des Rechners, auf dem der Server laeuft. Braucht eine Sitzung (ApiGuard), nimmt keine Eingaben an.</summary>
internal static class SystemEndpoints
{
    public static void MapSystem(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/overview", (HostMonitor monitor) =>
        {
            var o = monitor.Current;
            return Results.Json(new OverviewResponse(
                o.CpuPercent is { } c ? Math.Round(c, 1) : null, o.Cores, o.MemoryTotal, o.MemoryUsed, o.DiskTotal, o.DiskUsed,
                o.RxBytesPerSecond is { } rx ? Math.Round(rx) : null, o.TxBytesPerSecond is { } tx ? Math.Round(tx) : null, o.SampledAt), AppJson.Default.OverviewResponse);
        });
    }
}

internal sealed record OverviewResponse(
    double? CpuPercent, int? Cores, long? MemoryTotal, long? MemoryUsed, long? DiskTotal, long? DiskUsed,
    double? RxBytesPerSecond, double? TxBytesPerSecond, DateTimeOffset? SampledAt);
