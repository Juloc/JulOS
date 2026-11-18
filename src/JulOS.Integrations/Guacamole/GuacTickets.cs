using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace JulOS.Integrations.Guacamole;

/// <summary>
/// Einmal-Tickets fuer den WebSocket. Der Browser holt per API ein Ticket (mit den Verbindungsdaten, auch dem getippten Passwort) und
/// oeffnet damit den WebSocket. Das Ticket liegt nur im Speicher, gilt 30 Sekunden, genau einmal und nur fuer denselben Benutzer.
/// </summary>
public sealed class GuacTickets(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    public const int MaxPerUser = 20;

    private sealed record Entry(Guid UserId, GuacSpec Spec, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

    /// <returns>Das Ticket, oder null wenn der Benutzer zu viele offene hat.</returns>
    public string? Create(Guid userId, GuacSpec spec)
    {
        Sweep();
        if (_tickets.Values.Count(e => e.UserId == userId) >= MaxPerUser) return null;
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tickets[ticket] = new Entry(userId, spec, time.GetUtcNow() + Lifetime);
        return ticket;
    }

    /// <summary>Loest das Ticket ein und loescht es dabei. Ein zweiter Versuch, ein fremder Benutzer oder Ablauf ergeben null.</summary>
    public GuacSpec? Take(string ticket, Guid userId)
    {
        if (!_tickets.TryRemove(ticket, out var e)) return null;
        return e.UserId == userId && e.Expires > time.GetUtcNow() ? e.Spec : null;
    }

    public int Sweep()
    {
        var now = time.GetUtcNow();
        var n = 0;
        foreach (var (key, e) in _tickets) if (e.Expires <= now && _tickets.TryRemove(key, out _)) n++;
        return n;
    }
}
