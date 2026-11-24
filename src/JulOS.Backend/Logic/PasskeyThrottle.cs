using System.Collections.Concurrent;

namespace JulOS.Backend.Logic;

/// <summary>
/// Begrenzt die Passkey-Wege je Adresse (Optionen anfordern und Antworten pruefen): hoechstens <see cref="PasskeyRules.RequestsPerMinute"/> je Minute. Unabhaengig von der Sperre nach
/// Fehlversuchen (LoginLimiter): die zaehlt nur Fehler, diese hier auch Anfragen, die gelingen, denn schon das Anfordern von Optionen legt eine Zeile in der Datenbank an.
/// </summary>
public sealed class PasskeyThrottle(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    /// <summary>true: die Anfrage darf weiter, und sie ist gezaehlt. false: zu viele, Wartezeit in <paramref name="retryAfter"/> (Sekunden).</summary>
    public bool Allow(string key, out int retryAfter)
    {
        var now = _clock.GetUtcNow();
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= PasskeyRules.RequestsPerMinute) { retryAfter = Math.Max(1, (int)Math.Ceiling((q.Peek() + TimeSpan.FromMinutes(1) - now).TotalSeconds)); return false; }
            q.Enqueue(now);
        }
        retryAfter = 0;
        if (_hits.Count > 5000) Sweep(now);
        return true;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (k, q) in _hits) lock (q) if (q.Count == 0 || now - q.Peek() >= TimeSpan.FromMinutes(1)) _hits.TryRemove(k, out _);
    }
}
