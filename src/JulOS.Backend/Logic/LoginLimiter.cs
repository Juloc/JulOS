using System.Collections.Concurrent;

namespace JulOS.Backend.Logic;

/// <summary>
/// Sperre nach Fehlversuchen mit wachsender Wartezeit. Schluessel: "ip:..." (grosszuegiger), "user:..." und "setup:..." (strenger).
/// Nach einer Stunde ohne Fehlversuch wird der Zaehler vergessen.
/// </summary>
public sealed class LoginLimiter(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed class Entry { public int Failures; public DateTimeOffset Last; public DateTimeOffset LockedUntil; }

    private static (int Limit, TimeSpan Base, TimeSpan Max) PolicyFor(string key) =>
        key.StartsWith("ip:", StringComparison.Ordinal) ? (10, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15))
                                                        : (5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));

    /// <summary>Restliche Sperrzeit, TimeSpan.Zero wenn frei.</summary>
    public TimeSpan Wait(string key)
    {
        if (!_entries.TryGetValue(key, out var e)) return TimeSpan.Zero;
        lock (e) { var left = e.LockedUntil - _clock.GetUtcNow(); return left > TimeSpan.Zero ? left : TimeSpan.Zero; }
    }

    public void Fail(string key)
    {
        var now = _clock.GetUtcNow();
        var (limit, baseWait, max) = PolicyFor(key);
        var e = _entries.GetOrAdd(key, _ => new Entry { Last = now });
        lock (e)
        {
            if (now - e.Last > TimeSpan.FromHours(1)) e.Failures = 0;
            e.Failures++;
            e.Last = now;
            if (e.Failures >= limit)
            {
                var factor = Math.Pow(2, Math.Min(e.Failures - limit, 20));
                var wait = TimeSpan.FromTicks((long)Math.Min(max.Ticks, baseWait.Ticks * factor));
                e.LockedUntil = now + wait;
            }
        }
    }

    public void Succeed(string key) => _entries.TryRemove(key, out _);

    public void Sweep()
    {
        var now = _clock.GetUtcNow();
        foreach (var (k, e) in _entries) if (now - e.Last > TimeSpan.FromHours(1)) _entries.TryRemove(k, out _);
    }
}
