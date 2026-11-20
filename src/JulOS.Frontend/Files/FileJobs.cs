using System.Collections.Concurrent;
using JulOS.Integrations.Files;

namespace JulOS.Frontend.Files;

/// <summary>Ein laufender oder fertiger Auftrag der Dateien-App (kopieren, verschieben, loeschen), nur im Speicher. Die Seite fragt den Stand ab und kann abbrechen.</summary>
internal sealed class FileJob(Guid id, Guid userId, string kind, string host, DateTimeOffset started)
{
    public Guid Id { get; } = id;
    public Guid UserId { get; } = userId;
    public string Kind { get; } = kind;
    /// <summary>Anzeigetext fuer den Verlauf ("Name" oder "Quelle → Ziel").</summary>
    public string Host { get; } = host;
    public DateTimeOffset Started { get; } = started;
    public DateTimeOffset? Finished { get; set; }
    /// <summary>Das Ergebnis (done, failed, cancelled), sobald die Arbeit endet; vorher running.</summary>
    public volatile string Outcome = "running";
    private volatile bool _settled;
    /// <summary>running, done, failed, cancelled. Ein Auftrag gilt erst als fertig, wenn auch sein Eintrag im Verlauf geschrieben ist (wer "fertig" sieht, findet ihn dort).</summary>
    public string State => _settled ? Outcome : "running";
    internal void Settle() => _settled = true;
    /// <summary>Kurzer Code bei failed (siehe <see cref="FileErrors"/>).</summary>
    public string? Error { get; set; }
    public TransferProgress Progress { get; } = new();
    public TransferResult? Result { get; set; }
    internal CancellationTokenSource Cts { get; } = new();
    internal Task? Work { get; set; }
    public bool Final => State != "running";
}

/// <summary>Die Auftraege je Benutzer: hoechstens <c>maxRunning</c> laufende, fertige bleiben 10 Minuten (hoechstens 20) abfragbar.</summary>
internal sealed class FileJobs(TimeProvider time, int maxRunning)
{
    private readonly ConcurrentDictionary<Guid, FileJob> _jobs = new();
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    private const int KeepFinished = 20;

    /// <summary>Startet <paramref name="work"/> im Hintergrund; null, wenn der Benutzer schon so viele Auftraege laufen hat. <paramref name="work"/> setzt Result; Fehler und Abbruch erledigt diese Methode.</summary>
    public FileJob? Start(Guid user, string kind, string host, Func<FileJob, CancellationToken, Task> work, Func<FileJob, Task>? finished = null)
    {
        if (_jobs.Values.Count(j => j.UserId == user && !j.Final) >= maxRunning) return null;
        var job = new FileJob(Guid.NewGuid(), user, kind, host, time.GetUtcNow());
        _jobs[job.Id] = job;
        job.Work = Task.Run(async () =>
        {
            try { await work(job, job.Cts.Token); job.Outcome = "done"; }
            catch (OperationCanceledException) { job.Outcome = "cancelled"; }
            catch (Exception e) { job.Error = FileErrors.Code(e); job.Outcome = "failed"; }
            finally
            {
                job.Finished = time.GetUtcNow();
                try { if (finished is not null) await finished(job); } catch (Exception) { /* der Verlauf darf den Auftrag nicht beschaedigen */ }
                job.Settle();
            }
        });
        Sweep();
        return job;
    }

    public FileJob? Get(Guid user, Guid id) => _jobs.TryGetValue(id, out var j) && j.UserId == user ? j : null;

    public bool Cancel(Guid user, Guid id)
    {
        if (Get(user, id) is not { Final: false } j) return false;
        j.Cts.Cancel();
        return true;
    }

    public void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var j in _jobs.Values.Where(j => j.Final && now - j.Finished >= Keep)) _jobs.TryRemove(j.Id, out _);
        foreach (var group in _jobs.Values.Where(j => j.Final).GroupBy(j => j.UserId))
            foreach (var j in group.OrderByDescending(j => j.Finished).Skip(KeepFinished)) _jobs.TryRemove(j.Id, out _);
    }
}
