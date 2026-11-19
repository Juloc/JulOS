using JulOS.Backend.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JulOS.Workers;

/// <summary>
/// Taegliche Sicherung zur eingestellten Uhrzeit (Ortszeit des Servers). Fehlt beim Start ein frischer Auszug (neue Installation, Server war zur Sicherungszeit aus),
/// kommt zwei Minuten spaeter ein Nachholer. Nach einem Fehler wird nach einer Stunde noch einmal versucht, nicht erst am naechsten Tag.
/// </summary>
public sealed class BackupWorker(BackupService backup, TimeProvider clock, ILogger<BackupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        backup.Initialize();
        if (!backup.CanRun) return;
        log.LogInformation("Backup an: täglich {Time} nach {Dir}, {Days} Tage aufbewahrt.", backup.Options.Time.ToString("HH:mm"), backup.Options.Dir, backup.Options.KeepDays);
        try
        {
            var wait = backup.Overdue() ? TimeSpan.FromMinutes(2) : BackupService.NextRun(clock.GetUtcNow(), clock.LocalTimeZone, backup.Options.Time) - clock.GetUtcNow();
            for (;;)
            {
                await Task.Delay(wait, stoppingToken);
                try
                {
                    await backup.RunAsync(stoppingToken);
                    wait = BackupService.NextRun(clock.GetUtcNow(), clock.LocalTimeZone, backup.Options.Time) - clock.GetUtcNow();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    wait = TimeSpan.FromHours(1);                      // der Fehler steht schon im Log und im Status
                }
            }
        }
        catch (OperationCanceledException) { /* Herunterfahren */ }
    }
}
