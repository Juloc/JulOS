using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JulOS.Workers;

/// <summary>Was die Auswertung von einem Host sieht (aus den vorhandenen Abfragen der Host-Seite, nie aus neuen schweren Anfragen). Null: Host ohne Agent, oder es gibt nichts auszuwerten.</summary>
public interface IHostObserver
{
    Task<HostObservation?> ObserveAsync(Guid userId, HostRecord host, CancellationToken ct);
}

/// <summary>
/// Die Auswertung der festen Regeln (docs/PUSH.md): je Benutzer, der Meldungen eingeschaltet hat, wird jeder Host beobachtet, <see cref="AlertRules"/> auf den letzten Zustand angewandt,
/// der neue Zustand gespeichert und, ausserhalb der Ruhezeit, alles, was noch nicht zugestellt ist, als eine Nachricht an die Geraete geschickt.
/// </summary>
public sealed class AlertRunner(PushStore store, HostStore hosts, IHostObserver observer, PushService push, TimeProvider time, ILogger<AlertRunner> log)
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);    // was so lange nicht zugestellt werden konnte, wird nicht mehr nachgeschickt (laenger als jede Ruhezeit, die hoechstens 23 Stunden dauert)

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var user in await store.AlertUsersAsync(ct))
        {
            try { await RunUserAsync(user, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Meldungen: Auswertung fehlgeschlagen ({Type})", e.GetType().Name); }
        }
    }

    public async Task RunUserAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var cfg = await store.SettingsAsync(userId, ct);
        var list = await hosts.ListAsync(userId, ct);
        await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (host, c) =>
        {
            try
            {
                if (await observer.ObserveAsync(userId, host, c) is not { } seen) return;
                var (events, state) = AlertRules.Evaluate(seen, await store.StateAsync(userId, host.Id, c), cfg, now);
                await store.SaveStateAsync(userId, host.Id, state, c);
                foreach (var e in events) await store.AddLogAsync(userId, now, host.Id, host.Name, e.Rule, e.Kind, e.Detail, "pending", c);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Meldungen: Host {Host} nicht ausgewertet ({Type})", host.Id, e.GetType().Name); }
        });
        await DeliverAsync(userId, cfg, now, ct);
    }

    /// <summary>Stellt alles Wartende als eine Sammelnachricht zu, ausser in der Ruhezeit (dann kommt es gesammelt danach).</summary>
    public async Task DeliverAsync(Guid userId, AlertSettings cfg, DateTimeOffset now, CancellationToken ct)
    {
        await store.TidyAsync(userId, now, MaxWait, ct);
        if (AlertRules.InQuietHours(cfg, now)) return;
        var pending = (await store.LogAsync(userId, "pending", 100, ct)).OrderBy(r => r.Id).ToList();
        if (pending.Count == 0) return;
        var outcome = await push.SendAsync(userId, PushPayload.Build([.. pending.Select(r => new PushItem(r.HostName, r.Rule, r.Kind, r.HostId))]), ct);
        var ids = pending.Select(r => r.Id);
        if (outcome.Sent > 0) await store.SetPushAsync(userId, ids, "sent", ct);
        else if (outcome.Devices == 0 || outcome is { Failed: 0, Limited: false }) await store.SetPushAsync(userId, ids, "none", ct);     // kein Geraet (mehr): nur in der Liste der App
        // sonst bleibt es wartend und wird bei der naechsten Abfrage noch einmal versucht, bis es zu alt ist
    }
}

/// <summary>Ruft die Auswertung in festen Abstaenden auf (alle 5 Minuten).</summary>
public sealed class AlertWorker(AlertRunner runner, ILogger<AlertWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false); }          // nach dem Start erst die Agenten wieder verbinden lassen
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await runner.RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Auswertung der Meldungen fehlgeschlagen."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
