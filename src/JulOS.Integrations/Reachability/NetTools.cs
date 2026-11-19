using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace JulOS.Integrations.Reachability;

public sealed record PingReplyInfo(int Seq, string Status, long? Ms);
public sealed record PingOutcome(string Host, string? Address, int Sent, int Received, long? MinMs, long? AvgMs, long? MaxMs, PingReplyInfo[] Replies, string? Note);
public sealed record DnsOutcome(string Host, string[] Addresses, long Ms, string? Note);
public sealed record PortOutcome(int Port, string State, long? Ms);

/// <summary>
/// Ping, DNS-Abfrage und Port-Test vom Server aus, kurz und begrenzt (Fristen, hoechstens <see cref="MaxParallel"/> Werkzeuge zugleich).
/// Die Ziele prueft der Aufrufer gegen die gespeicherten Verbindungen und die Erlaubnisliste (<c>NetToolRules</c>); hier steht nur die Messung.
/// Ping ist ICMP ohne besondere Rechte (ein Linux-Container erlaubt das meist, siehe docs/DEVELOPMENT.md); geht es nicht, kommt eine Meldung statt eines Absturzes.
/// </summary>
public sealed class NetTools
{
    public const int MaxParallel = 4;
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly SemaphoreSlim _gate = new(MaxParallel);

    /// <summary>Ob gerade zu viele Werkzeuge laufen; dann lieber "spaeter" sagen als Anfragen anzustauen.</summary>
    public async Task<IDisposable?> TryEnterAsync() => await _gate.WaitAsync(0).ConfigureAwait(false) ? new Release(_gate) : null;

    private sealed class Release(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DnsTimeout);
        return await Dns.GetHostAddressesAsync(host, limit.Token).ConfigureAwait(false);
    }

    public async Task<DnsOutcome> DnsAsync(string host, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var addresses = (await ResolveAsync(host, ct).ConfigureAwait(false)).Select(a => a.ToString()).Distinct().ToArray();
            return new DnsOutcome(host, addresses, clock.ElapsedMilliseconds, IPAddress.TryParse(host, out _) ? "Das ist schon eine Adresse." : addresses.Length == 0 ? "Keine Adresse gefunden." : null);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new DnsOutcome(host, [], clock.ElapsedMilliseconds, e is OperationCanceledException ? "Keine Antwort des Namensdienstes." : "Der Name lässt sich nicht auflösen.");
        }
    }

    public async Task<PingOutcome> PingAsync(string host, int count, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await ResolveAsync(host, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new PingOutcome(host, null, 0, 0, null, null, null, [], "Der Name lässt sich nicht auflösen.");
        }
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (address is null) return new PingOutcome(host, null, 0, 0, null, null, null, [], "Keine Adresse gefunden.");

        using var ping = new Ping();
        var replies = new List<PingReplyInfo>();
        string? note = null;
        for (var i = 1; i <= count; i++)
        {
            if (i > 1) await Task.Delay(PingInterval, ct).ConfigureAwait(false);
            try
            {
                var r = await ping.SendPingAsync(address, PingTimeout).WaitAsync(ct).ConfigureAwait(false);   // ohne eigene Nutzlast: die braucht erhoehte Rechte
                replies.Add(new PingReplyInfo(i, r.Status == IPStatus.Success ? "ok" : r.Status == IPStatus.TimedOut ? "timeout" : "error", r.Status == IPStatus.Success ? r.RoundtripTime : null));
            }
            catch (Exception e) when (e is PingException or PlatformNotSupportedException or SocketException or InvalidOperationException)
            {
                // meist: der Container darf keine ICMP-Pakete schicken (net.ipv4.ping_group_range) oder es gibt kein ping-Programm
                note = "Ping (ICMP) ist auf diesem Server nicht möglich. Der Port-Test geht trotzdem.";
                replies.Add(new PingReplyInfo(i, "unavailable", null));
                break;
            }
        }
        var ok = replies.Where(r => r.Ms is not null).Select(r => r.Ms!.Value).ToArray();
        return new PingOutcome(host, address.ToString(), replies.Count, ok.Length, ok.Length > 0 ? ok.Min() : null, ok.Length > 0 ? (long)Math.Round(ok.Average()) : null, ok.Length > 0 ? ok.Max() : null, [.. replies], note);
    }

    /// <summary>TCP-Verbindungsversuch je Port, ohne ein Byte zu senden. "open", "closed" (abgelehnt) oder "filtered" (keine Antwort in der Frist).</summary>
    public async Task<PortOutcome[]> PortsAsync(string host, IReadOnlyList<int> ports, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await ResolveAsync(host, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { return [.. ports.Select(p => new PortOutcome(p, "error", null))]; }
        if (addresses.Length == 0) return [.. ports.Select(p => new PortOutcome(p, "error", null))];
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        return await Task.WhenAll(ports.Select(p => TryPortAsync(address, p, ct))).ConfigureAwait(false);
    }

    private static async Task<PortOutcome> TryPortAsync(IPAddress address, int port, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ConnectTimeout);
        using var tcp = new TcpClient(address.AddressFamily);
        try
        {
            await tcp.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
            return new PortOutcome(port, "open", clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new PortOutcome(port, "filtered", null); }
        catch (SocketException e) { return new PortOutcome(port, e.SocketErrorCode == SocketError.ConnectionRefused ? "closed" : "error", e.SocketErrorCode == SocketError.ConnectionRefused ? clock.ElapsedMilliseconds : null); }
    }
}
