using System.Collections.Concurrent;

namespace JulOS.Backend.Logic;

/// <summary>Einfache Mengenbegrenzung: hoechstens N Aufrufe pro Schluessel in einem Zeitfenster (zum Beispiel Uploads pro Benutzer und Minute).</summary>
public sealed class HitCounter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    public bool Allow(string key, int max, TimeSpan window)
    {
        var now = time.GetUtcNow();
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= max) return false;
            q.Enqueue(now);
            return true;
        }
    }

    /// <summary>Vergisst Schluessel ohne neue Aufrufe (der Worker ruft das auf).</summary>
    public void Sweep(TimeSpan window)
    {
        var now = time.GetUtcNow();
        foreach (var (key, q) in _hits)
            lock (q) { if (q.Count == 0 || now - q.Peek() >= window && now - q.Last() >= window) _hits.TryRemove(key, out _); }
    }
}
