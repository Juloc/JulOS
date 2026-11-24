using JulOS.Backend.Services;

namespace JulOS.Frontend.Oidc;

/// <summary>Stellt die Logout-Token der Anmelde-Zentrale zu (alle 3 Sekunden ein Durchgang, siehe <see cref="OidcLogoutService"/>). Ein Fehler stoppt den Dienst nicht.</summary>
internal sealed class OidcLogoutWorker(OidcLogoutService logout, ILogger<OidcLogoutWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try { await logout.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Anmelde-Zentrale: Logout-Token konnten nicht zugestellt werden."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
