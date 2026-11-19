namespace JulOS.Integrations;

/// <summary>
/// Merkt sich die offenen Fernsteuer-Sitzungen (Desktop-Stream und Terminal).
/// Pro Slot (Benutzer + Verbindung + Sitzungskennung des Browsers) ist nur ein WebSocket offen: ein neuer loest den alten ab, sonst nichts.
/// Dazu gilt eine Obergrenze offener Sitzungen pro Benutzer (ein ablosender WebSocket zaehlt nicht doppelt).
/// </summary>
public sealed class SessionRegistry(int maxPerUser = SessionRegistry.DefaultMaxPerUser)
{
    public const int DefaultMaxPerUser = 8;

    /// <summary>Statuscode "zu viele offene Sitzungen"; die Seite zeigt dazu die Meldung des Servers.</summary>
    public const int TooManyStatus = 797;

    /// <summary>
    /// Der Slot einer Sitzung: Benutzer + Verbindung + Sitzungskennung des Browsers (pro offenem Fenster). Ohne gueltige Kennung
    /// (8 bis 40 Zeichen aus Buchstaben, Ziffern, - und _) gilt ein Slot je Verbindung, wie vor den Mehrfach-Sitzungen.
    /// </summary>
    public static string SlotOf(Guid userId, Guid connectionId, string? sid) =>
        sid is { Length: >= 8 and <= 40 } && sid.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? $"{userId}:{connectionId}:{sid}"
            : $"{userId}:{connectionId}";

    private readonly object _gate = new();
    private readonly Dictionary<(Guid User, string Slot), Lease> _open = [];
    private long _anonymous;

    public int MaxPerUser { get; } = Math.Max(1, maxPerUser);

    public string LimitMessage => $"Es sind schon {MaxPerUser} Sitzungen offen. Schließe eine, um eine neue zu öffnen.";

    /// <summary>Wie viele Sitzungen der Benutzer gerade offen hat.</summary>
    public int CountOf(Guid userId)
    {
        lock (_gate)
        {
            return _open.Keys.Count(k => k.User == userId);
        }
    }

    /// <summary>Ob eine Sitzung in diesem Slot aufgemacht werden duerfte (Vorabpruefung, nicht bindend: <see cref="TryOpen"/> entscheidet).</summary>
    public bool CanOpen(Guid userId, string? slot)
    {
        lock (_gate)
        {
            return (slot is not null && _open.ContainsKey((userId, slot))) || _open.Keys.Count(k => k.User == userId) < MaxPerUser;
        }
    }

    /// <summary>
    /// Meldet eine neue Sitzung an. Ist der Slot schon belegt, wird die alte abgeloest (ihr <c>onReplaced</c> wird gerufen, ausserhalb der Sperre).
    /// Gibt null zurueck, wenn der Benutzer die Obergrenze erreicht hat. Ohne Slot gilt die Sitzung als eigene (loest keine andere ab).
    /// </summary>
    public SessionLease? TryOpen(Guid userId, string? slot, Action onReplaced)
    {
        Lease? old = null;
        Lease mine;
        lock (_gate)
        {
            var key = (userId, slot ?? "#" + ++_anonymous);
            if (!_open.TryGetValue(key, out old) && _open.Keys.Count(k => k.User == userId) >= MaxPerUser)
            {
                return null;
            }

            mine = new Lease(this, key, onReplaced);
            _open[key] = mine;
        }

        old?.Replace();
        return mine;
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(lease.Key, out var current) && ReferenceEquals(current, lease))
            {
                _open.Remove(lease.Key);
            }
        }
    }

    private sealed class Lease(SessionRegistry owner, (Guid User, string Slot) key, Action onReplaced) : SessionLease
    {
        public (Guid User, string Slot) Key { get; } = key;

        public void Replace()
        {
            Replaced = true;
            try { onReplaced(); }
            catch (ObjectDisposedException) { /* die alte Sitzung war schon zu Ende */ }
        }

        public override void Dispose() => owner.Release(this);
    }
}

/// <summary>Der Platz einer offenen Sitzung; Dispose gibt ihn frei (loest nichts ab, wenn schon eine neuere uebernommen hat).</summary>
public abstract class SessionLease : IDisposable
{
    /// <summary>Ob eine neuere Sitzung im selben Slot diese abgeloest hat.</summary>
    public volatile bool Replaced;

    public abstract void Dispose();
}
