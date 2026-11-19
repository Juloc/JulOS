using JulOS.Backend.Modules;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JulOS.Workers;

/// <summary>
/// Automatische DDNS-Aktualisierung (nur mit JULOS_DDNS=control, einem Anbieter und JULOS_DDNS_AUTO=on): alle JULOS_DDNS_AUTO_SECONDS (mindestens 5 Minuten) prueft der Server die oeffentliche Adresse
/// und aendert nur die benannten Eintraege, wenn sie abweichen. Ohne diese Schalter laeuft nichts und es geht nichts nach aussen.
/// </summary>
public sealed class DdnsAutoWorker(DdnsUpdater updater, ILogger<DdnsAutoWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!updater.AutoOn) return;
        log.LogInformation("DDNS: automatische Aktualisierung an, alle {Seconds} s.", (int)updater.AutoEvery.TotalSeconds);
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await updater.UpdateAsync("auto", stoppingToken);
                if (r.Changes.Length > 0) log.LogInformation("DDNS automatisch: {Outcome}, {Count} Eintrag/Einträge.", r.Outcome, r.Changes.Length);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning("DDNS automatisch fehlgeschlagen ({Type}).", e.GetType().Name); }
            try { await Task.Delay(updater.AutoEvery, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
