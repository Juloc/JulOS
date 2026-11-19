using System.Collections.Concurrent;
using System.Net.Sockets;

namespace JulOS.Integrations.Reachability;

/// <summary>
/// Ob ein Dienst (Adresse und Port einer gespeicherten Verbindung) erreichbar ist: ein TCP-Verbindungsaufbau mit kurzer Frist, ohne ein Byte zu senden,
/// danach sofort wieder zu. Das Ergebnis gilt kurz (<see cref="Lifetime"/>), damit eine Seite, die oft nachfragt, das Ziel nicht belaestigt,
/// und es laufen hoechstens <see cref="MaxParallel"/> Versuche zugleich. Die Ziele kommen nur aus den gespeicherten Verbindungen des Benutzers
/// (der Aufrufer prueft die Erlaubnisliste), nie aus einer Anfrage.
/// </summary>
public sealed class TcpProbe(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);
    public const int MaxParallel = 8;
    private const int MaxCached = 2000;

    private readonly SemaphoreSlim _gate = new(MaxParallel);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, bool Up)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsUpAsync(string host, int port, CancellationToken ct = default)
    {
        var key = host + ":" + port;
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < Lifetime) return hit.Up;

        var up = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            using var tcp = new TcpClient();
            try { await tcp.ConnectAsync(host, port, limit.Token).ConfigureAwait(false); up = true; }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested) throw;      // der Aufrufer ist weg: nichts merken
            }
        }
        finally { _gate.Release(); }

        if (_cache.Count >= MaxCached) foreach (var (k, v) in _cache) if (now - v.At >= Lifetime) _cache.TryRemove(k, out _);
        _cache[key] = (time.GetUtcNow(), up);
        return up;
    }
}
