using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Integrations.Agent;
using JulOS.Workers;

namespace JulOS.Frontend.Hosts;

/// <summary>
/// Liest fuer die Auswertung der Meldungen (docs/PUSH.md) dieselben Antworten wie die Host-Seite, ueber <see cref="HostInfoService"/>: gilt der Zwischenspeicher (60 s), kostet es nichts, sonst
/// genau eine uebliche Abfrage je Art, mit denselben Fristen. Hosts ohne Agent werden uebersprungen; was der Agent nicht kann oder nicht beantwortet, bleibt "unbekannt" (null).
/// </summary>
internal sealed class HostObserver(HostInfoService info, AgentLinks links, HostStore hosts) : IHostObserver
{
    private static readonly HashSet<string> ReadOnlyFs = new(StringComparer.OrdinalIgnoreCase) { "squashfs", "iso9660", "erofs", "udf" };      // immer voll, nie ein Grund zur Meldung
    private static readonly HashSet<string> DownStates = new(StringComparer.OrdinalIgnoreCase) { "exited", "dead", "restarting", "removing" };

    public async Task<HostObservation?> ObserveAsync(Guid userId, HostRecord host, CancellationToken ct)
    {
        if (await hosts.AgentEnrolledAsync(userId, host.Id, ct) is not true) return null;
        if (!links.IsOnline(host.Id)) return new HostObservation(host.Id, host.Name, false);

        var status = (await info.GetAsync(userId, host.Id, HostInfoEndpoints.Status, false, ct)) is { State: "ok", Data: { } s } ? s : null;
        var updates = (await info.GetAsync(userId, host.Id, HostInfoEndpoints.Updates, false, ct)) is { State: "ok", Data: { } u } ? u : null;
        var services = (await info.GetAsync(userId, host.Id, HostInfoEndpoints.Services, false, ct)) is { State: "ok", Data: { } f } ? f : null;
        var docker = (await info.GetAsync(userId, host.Id, HostInfoEndpoints.Docker, false, ct)) is { State: "ok", Data: { Available: true } d } ? d : null;

        return new HostObservation(host.Id, host.Name, true,
            Security: updates is { SecurityKnown: true, Unreadable: false, Security: { } n } ? n : null,
            Reboot: status?.RebootRequired,
            Disks: status?.Disks.Where(x => !ReadOnlyFs.Contains(x.Fs) && x.TotalBytes > 0).Select(x => new DiskFill(x.Mount, x.Percent)).ToArray(),
            Failed: services?.Failed.Select(x => x.Unit).ToArray(),
            Running: docker?.Containers.Where(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).ToArray(),
            Stopped: docker?.Containers.Where(c => DownStates.Contains(c.State)).Select(c => c.Name).ToArray());
    }
}
