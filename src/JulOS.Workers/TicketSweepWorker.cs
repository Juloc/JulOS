using JulOS.Integrations.Display;
using JulOS.Integrations.Ssh;
using Microsoft.Extensions.Hosting;

namespace JulOS.Workers;

/// <summary>Vergisst abgelaufene Verbindungs-Tickets (sie enthalten das getippte Passwort und sollen nicht laenger als 30 Sekunden im Speicher liegen).</summary>
public sealed class TicketSweepWorker(ScreenTickets screenTickets, SshTickets sshTickets) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try { while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) { screenTickets.Sweep(); sshTickets.Sweep(); } }
        catch (OperationCanceledException) { /* Ende */ }
    }
}
