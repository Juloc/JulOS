using JulOS.Integrations.Files;

namespace JulOS.Frontend.Files;

/// <summary>Es sind schon so viele SFTP-Verbindungen offen, wie dieser Benutzer haben darf, und keine ist frei zum Schliessen.</summary>
internal sealed class PoolFullException() : Exception("Zu viele offene SFTP-Verbindungen.");

/// <summary>
/// Kurzlebige SFTP-Sitzungen je Benutzer und Verbindung (docs/FILES.md). Eine Sitzung wird beim ersten Gebrauch geoeffnet, von allen Anfragen dieses Benutzers fuer diese Verbindung geteilt und
/// geschlossen, wenn sie <see cref="FilesOptions.Idle"/> lang niemand geliehen hat (<see cref="SweepAsync"/>, vom Worker aufgerufen). Hoechstens <c>MaxPerUser</c> je Benutzer: ist die Grenze
/// erreicht, wird die am laengsten unbenutzte (ohne laufende Leihe) geschlossen, sonst <see cref="PoolFullException"/>. Eine abgerissene Sitzung wird beim Zurueckgeben verworfen. Hier liegt nie ein Passwort.
/// </summary>
internal sealed class SftpPool(TimeProvider time, int maxPerUser, TimeSpan idle) : IAsyncDisposable
{
    internal sealed class Entry(Guid user, Guid connection)
    {
        public Guid User = user, Connection = connection;
        public Task<ISftpFileSession>? Connecting;
        public ISftpFileSession? Session;
        public int Leases;
        public bool Doomed;                 // aus dem Pool genommen, wird geschlossen, sobald die letzte Leihe zurueck ist
        public DateTimeOffset LastUsed;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid, Guid), Entry> _entries = [];
    private bool _disposed;

    /// <summary>Eine geliehene Sitzung; Dispose gibt sie zurueck (die Sitzung selbst bleibt im Pool).</summary>
    internal sealed class Lease(SftpPool pool, Entry entry, ISftpFileSession session) : IAsyncDisposable
    {
        private int _released;
        public ISftpFileSession Session { get; } = session;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0 ? pool.ReleaseAsync(entry, Session) : ValueTask.CompletedTask;
    }

    public int Count { get { lock (_gate) return _entries.Count; } }
    public int CountFor(Guid user) { lock (_gate) return _entries.Values.Count(e => e.User == user); }

    /// <summary>Gibt es eine offene (verbundene) Sitzung? Ohne sie zu leihen und ohne die Leerlaufzeit anzufassen.</summary>
    public bool IsOpen(Guid user, Guid connection)
    {
        lock (_gate) return !_disposed && _entries.TryGetValue((user, connection), out var e) && e.Session is { IsConnected: true };
    }

    /// <summary>Leiht die offene Sitzung dieser Verbindung, wenn es eine gibt (und sie noch verbunden ist); null sonst. Fasst die Leerlaufzeit neu an.</summary>
    public Lease? TryAcquire(Guid user, Guid connection)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue((user, connection), out var e) || e.Session is not { IsConnected: true } s) return null;
            e.Leases++; e.LastUsed = time.GetUtcNow();
            return new Lease(this, e, s);
        }
    }

    /// <summary>
    /// Leiht die Sitzung; gibt es keine, wird mit <paramref name="connect"/> eine geoeffnet (ein Fehler dort laesst nichts im Pool zurueck). Zwei gleichzeitige Anfragen teilen sich ein Verbinden.
    /// </summary>
    public async Task<Lease> AcquireAsync(Guid user, Guid connection, Func<CancellationToken, Task<ISftpFileSession>> connect, CancellationToken ct)
    {
        Entry entry;
        Task<ISftpFileSession> connecting;
        List<ISftpFileSession>? evicted = null;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SftpPool));
            if (_entries.TryGetValue((user, connection), out var existing) && (existing.Connecting is not null || existing.Session is { IsConnected: true }))
            {
                existing.Leases++; existing.LastUsed = time.GetUtcNow();
                if (existing.Session is { IsConnected: true } ready) return new Lease(this, existing, ready);
                entry = existing; connecting = existing.Connecting!;
                goto Wait;
            }
            if (existing is not null) { _entries.Remove((user, connection)); if (existing.Session is not null) (evicted ??= []).Add(existing.Session); }   // abgerissen: ersetzen
            while (_entries.Values.Count(e => e.User == user) >= maxPerUser)
            {
                var oldest = _entries.Values.Where(e => e.User == user && e.Leases == 0 && e.Connecting is null).OrderBy(e => e.LastUsed).FirstOrDefault();
                if (oldest is null) throw new PoolFullException();
                _entries.Remove((oldest.User, oldest.Connection));
                if (oldest.Session is not null) (evicted ??= []).Add(oldest.Session);
            }
            entry = new Entry(user, connection) { Leases = 1, LastUsed = time.GetUtcNow() };
            connecting = entry.Connecting = connect(CancellationToken.None);          // das Verbinden gehoert dem Pool, nicht der einen Anfrage: ein Abbruch dort soll es den anderen nicht nehmen
            _entries[(user, connection)] = entry;
        }
        if (evicted is not null) foreach (var s in evicted) await s.DisposeAsync();
        Wait:
        ISftpFileSession session;
        try { session = await connecting.WaitAsync(ct); }
        catch
        {
            // Der Verbindungsfehler gilt allen Wartenden; der Eintrag verschwindet, sobald keiner mehr wartet
            lock (_gate) { entry.Leases--; if (entry.Session is null && entry.Leases <= 0 && _entries.TryGetValue((user, connection), out var cur) && cur == entry) _entries.Remove((user, connection)); }
            throw;
        }
        lock (_gate) { entry.Session ??= session; entry.Connecting = null; }
        return new Lease(this, entry, session);
    }

    private async ValueTask ReleaseAsync(Entry entry, ISftpFileSession session)
    {
        var drop = false;
        lock (_gate)
        {
            entry.Leases--; entry.LastUsed = time.GetUtcNow();
            if (entry.Doomed && entry.Leases <= 0) drop = true;
            else if (!session.IsConnected && entry.Leases <= 0 && _entries.TryGetValue((entry.User, entry.Connection), out var cur) && cur == entry) { _entries.Remove((entry.User, entry.Connection)); drop = true; }
        }
        if (drop) await session.DisposeAsync();
    }

    /// <summary>Schliesst die Sitzung dieser Verbindung (zum Beispiel wenn der Benutzer den Ort verlaesst); laufende Leihen behalten sie bis zum Zurueckgeben nicht: sie wird erst geschlossen, wenn keine Leihe mehr offen ist.</summary>
    public async Task CloseAsync(Guid user, Guid connection)
    {
        ISftpFileSession? s = null;
        lock (_gate)
            if (_entries.TryGetValue((user, connection), out var e) && e.Leases == 0 && e.Connecting is null) { _entries.Remove((user, connection)); s = e.Session; }
        if (s is not null) await s.DisposeAsync();
    }

    /// <summary>
    /// Nimmt die Sitzung dieser Verbindung aus dem Pool und schliesst sie: gleich, wenn sie niemand benutzt, sonst sobald die letzte Leihe (zum Beispiel ein laufender Auftrag) zurueck ist. Es wird nie wieder jemand
    /// damit versorgt. Fuer eine geaenderte oder geloeschte Verbindung (die Zugangsdaten gelten nicht mehr) und das Ende einer App-Sitzung.
    /// </summary>
    public Task InvalidateAsync(Guid user, Guid connection) => InvalidateWhereAsync(e => e.User == user && e.Connection == connection);

    /// <summary>Wie <see cref="InvalidateAsync"/> fuer alle Sitzungen eines Benutzers.</summary>
    public Task InvalidateUserAsync(Guid user) => InvalidateWhereAsync(e => e.User == user);

    private async Task InvalidateWhereAsync(Func<Entry, bool> match)
    {
        List<ISftpFileSession> close = [];
        lock (_gate)
            foreach (var e in _entries.Values.Where(match).ToList())
            {
                _entries.Remove((e.User, e.Connection));
                e.Doomed = true;
                if (e.Leases == 0 && e.Connecting is null && e.Session is not null) close.Add(e.Session);
            }
        foreach (var s in close) await s.DisposeAsync();
    }

    /// <summary>Schliesst alle Sitzungen ohne Leihe, die laenger als die Leerlaufzeit unbenutzt sind, und die abgerissenen. Gibt zurueck, wie viele.</summary>
    public async Task<int> SweepAsync()
    {
        var now = time.GetUtcNow();
        List<ISftpFileSession> close = [];
        lock (_gate)
            foreach (var e in _entries.Values.ToList())
                if (e.Leases == 0 && e.Connecting is null && (e.Session is not { IsConnected: true } || now - e.LastUsed >= idle))
                {
                    _entries.Remove((e.User, e.Connection));
                    if (e.Session is not null) close.Add(e.Session);
                }
        foreach (var s in close) await s.DisposeAsync();
        return close.Count;
    }

    public async ValueTask DisposeAsync()
    {
        List<ISftpFileSession> all;
        lock (_gate) { _disposed = true; all = [.. _entries.Values.Select(e => e.Session).OfType<ISftpFileSession>()]; _entries.Clear(); }
        foreach (var s in all) await s.DisposeAsync();
    }
}

/// <summary>Raeumt den Pool regelmaessig auf (Sitzungen ohne Gebrauch schliessen).</summary>
internal sealed class SftpPoolSweeper(SftpPool pool) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await pool.SweepAsync(); }
        catch (OperationCanceledException) { /* Ende */ }
    }
}
