using JulOS.Backend.Logic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JulOS.Workers;

/// <summary>Misst den Rechner alle 2 Sekunden (ein paar kleine Dateien unter /proc), damit die Uebersicht sofort Raten kennt.</summary>
public sealed class HostSamplerWorker(HostMonitor monitor, TimeProvider clock, ILogger<HostSamplerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { monitor.Sample(clock.GetUtcNow()); }
            catch (Exception e) { log.LogWarning(e, "Messung des Rechners fehlgeschlagen."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
