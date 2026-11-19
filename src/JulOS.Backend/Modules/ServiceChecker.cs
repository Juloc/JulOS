using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace JulOS.Backend.Modules;

/// <summary>
/// Wohin die Abfragen der Dienste gehen duerfen (JULOS_SERVICES_HOSTS, sonst JULOS_ALLOWED_HOSTS; Namen oder Adressen, kommagetrennt).
/// Mit Liste: nur diese Namen. Ohne Liste: nur Adressen im eigenen Netz (privat, Loopback, CGNAT), nie ein oeffentlicher Rechner.
/// Immer verboten (auch mit Liste): Link-Local (Metadaten-Dienste der Cloud), 0.0.0.0, Multicast. Geprueft wird nach der Namensaufloesung, direkt beim Verbinden.
/// </summary>
public sealed class ServiceHosts(string? servicesList, string? generalList)
{
    private readonly HashSet<string> _listed = new(
        (string.IsNullOrWhiteSpace(servicesList) ? generalList ?? "" : servicesList).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    public bool Restricted => _listed.Count > 0;
    public bool Listed(string host) => _listed.Contains(host);

    /// <summary>Beim Speichern: steht der Name auf der Liste (mit Liste), oder ist er wenigstens keine oeffentliche Adresse (ohne Liste)?</summary>
    public bool Allows(string host)
    {
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (Restricted) return Listed(host);
        return !IPAddress.TryParse(host, out var ip) || (!UpstreamTarget.IsForbidden(ip) && DdnsMonitor.IsPrivate(ip));
    }

    /// <summary>Beim Verbinden: darf der Name mit dieser aufgeloesten Adresse angesprochen werden?</summary>
    public bool AllowsAddress(string host, IPAddress address)
    {
        if (UpstreamTarget.IsForbidden(address)) return false;
        return Restricted ? Listed(host) : DdnsMonitor.IsPrivate(address);
    }
}

/// <summary>Die Abfrage wurde abgelehnt, weil die Adresse nicht erlaubt ist (kein Verbindungsversuch).</summary>
public sealed class NotAllowedException() : HttpRequestException("not allowed");

/// <summary>
/// Fragt die gespeicherten Adressen eines Dienstes ab: GET, keine Weiterleitungen, kein Proxy, 4 s je Abfrage, Antwort hoechstens 256 KB, hoechstens 8 Abfragen
/// gleichzeitig. Der Server fragt nur, was gespeichert ist (die Oberflaeche nennt nie eine Adresse). Der Schluessel geht als Kopfzeile nur an die Kennzahlen-Adresse
/// (und an die Gesundheitsadresse, wenn sie denselben Ursprung hat oder es keine Kennzahlen-Adresse gibt), nie ins Log, nie zurueck. Ergebnisse bleiben
/// je Dienst so lange stehen, wie sein Intervall sagt: es gibt keinen Hintergrundlauf, es fragt nur, wer hinschaut.
/// </summary>
public sealed class ServiceChecker(ServiceHosts hosts, HttpMessageHandler? handler = null, TimeProvider? time = null)
{
    public const int MaxBytes = 256 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http = OutboundHttp.Create(handler ?? Handler(hosts));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, ServiceStatus> _cache = new();
    private readonly SemaphoreSlim _gate = new(8);

    public void Forget(Guid id) => _cache.TryRemove(id, out _);

    private static SocketsHttpHandler Handler(ServiceHosts hosts) => new()
    {
        AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var all = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            var allowed = all.Where(a => hosts.AllowsAddress(host, a)).ToArray();
            if (allowed.Length == 0) throw new NotAllowedException();
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    };

    /// <param name="key">Liefert den entschluesselten Schluessel (wird nur aufgerufen, wenn wirklich gefragt wird).</param>
    /// <param name="force">true: das Ergebnis im Zwischenspeicher gilt nicht (Knopf "jetzt pruefen").</param>
    public async Task<ServiceStatus> CheckAsync(ServiceRecord s, Func<CancellationToken, Task<string?>> key, bool force, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!force && _cache.TryGetValue(s.Id, out var cached) && now - cached.CheckedAt < TimeSpan.FromSeconds(s.IntervalSeconds)) return cached;
        if (s.HealthUrl.Length == 0 && s.MetricsUrl.Length == 0) return new ServiceStatus(s.Id, "unknown", null, null, now, null, []);

        await _gate.WaitAsync(ct);
        try
        {
            var secret = s.HeaderName.Length > 0 && s.HasKey ? await key(ct) : null;
            var send = (HeaderName: s.HeaderName, Value: secret);
            var wantsMetrics = s.MetricsUrl.Length > 0;
            // Der Schluessel gehoert zur Kennzahlen-Adresse; zur Gesundheitsadresse nur, wenn sie derselbe Ursprung ist oder es sonst keine Adresse gibt
            var healthGetsKey = s.HealthUrl.Length > 0 && (!wantsMetrics || SameOrigin(s.HealthUrl, s.MetricsUrl));

            Fetch? health = s.HealthUrl.Length > 0 ? await GetAsync(s.HealthUrl, healthGetsKey ? send : default, false, ct) : null;
            Fetch? metrics = wantsMetrics ? await GetAsync(s.MetricsUrl, send, s.Metrics.Length > 0, ct) : null;

            var values = new List<MetricValue>();
            string? metricsError = metrics?.Error;
            if (metrics?.Body is { } body && s.Metrics.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    foreach (var m in s.Metrics) values.Add(ServiceRules.Extract(doc.RootElement, m) ?? new MetricValue(m.Label, m.Unit, m.Format, null, null));
                }
                catch (JsonException) { metricsError = "invalid"; }
            }
            var main = health ?? metrics!;
            var state = main.Error is null ? "up" : main.Error == "notAllowed" ? "unknown" : "down";
            // Bei Gesundheitsadresse bestimmt sie den Zustand; ein Fehler nur bei den Kennzahlen bleibt als Hinweis stehen (error)
            var error = main.Error ?? metricsError;
            var result = new ServiceStatus(s.Id, state, main.Error is null ? main.Ms : null, main.Status, _time.GetUtcNow(), error, [.. values]);
            _cache[s.Id] = result;
            return result;
        }
        finally { _gate.Release(); }
    }

    private static bool SameOrigin(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) && x.Scheme == y.Scheme && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port;

    private sealed record Fetch(int? Status, int Ms, byte[]? Body, string? Error);

    private async Task<Fetch> GetAsync(string url, (string HeaderName, string? Value) header, bool wantBody, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (!hosts.Allows(uri.Host)) return new Fetch(null, 0, null, "notAllowed");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Limit);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.ParseAdd("application/json, text/plain;q=0.8, */*;q=0.5");
            if (header.Value is { Length: > 0 } v && header.HeaderName.Length > 0) req.Headers.TryAddWithoutValidation(header.HeaderName, v);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)res.StatusCode;
            if (!res.IsSuccessStatusCode) return new Fetch(status, (int)sw.ElapsedMilliseconds, null, "status");
            var body = wantBody ? await OutboundHttp.ReadLimitedAsync(res, MaxBytes, cts.Token) : null;
            return new Fetch(status, (int)sw.ElapsedMilliseconds, body, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Fetch(null, 0, null, "timeout"); }
        catch (InvalidDataException) { return new Fetch(null, 0, null, "tooLarge"); }
        catch (Exception e) when (e is NotAllowedException || e.InnerException is NotAllowedException) { return new Fetch(null, 0, null, "notAllowed"); }
        catch (HttpRequestException) { return new Fetch(null, 0, null, "refused"); }
    }
}
