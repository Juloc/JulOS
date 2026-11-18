using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JulOS.Workers;

/// <summary>Raeumt stuendlich abgelaufene Sitzungen auf und vergisst alte Fehlversuche.</summary>
public sealed class SessionPurgeWorker(SessionStore sessions, LoginLimiter limiter, HitCounter hits, ILogger<SessionPurgeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var n = await sessions.PurgeAsync(stoppingToken);
                limiter.Sweep();
                hits.Sweep(TimeSpan.FromMinutes(1));
                if (n > 0) log.LogInformation("{Count} abgelaufene Sitzung(en) entfernt.", n);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Aufräumen der Sitzungen fehlgeschlagen."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
