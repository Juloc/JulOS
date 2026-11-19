using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace JulOS.Backend.Modules;

public sealed record CaddyOptions(ModuleMode Mode, string? CaddyfilePath, Uri? AdminUrl, string? AdminAuthFile = null, UpstreamTarget? TlsConnect = null, string? LogFile = null);
/// <summary>Source: admin (laufende Konfiguration), caddyfile (Datei) oder none (nichts lesbar, Warnings sagen warum).</summary>
public sealed record CaddyListing(string Source, CaddyRoute[] Routes, string[] Warnings);
/// <summary>State: up, down oder skipped (nicht pruefbar: Unix-Socket, Platzhalter, verbotene Adresse).</summary>
public sealed record UpstreamState(string Target, string State, int? Ms);
public sealed record CaddyReachability(UpstreamState[] Upstreams, DateTimeOffset CheckedAt);
/// <summary>Zustand des Caddy ueber seine Admin-API: up (antwortet), down (antwortet nicht oder falsch), unknown (keine Admin-API eingestellt). Upstreams: was Caddy selbst ueber seine Ziele zaehlt.</summary>
public sealed record CaddyUpstreamStat(string Address, int Requests, int Fails);
public sealed record CaddyHealth(string State, int? Ms, CaddyUpstreamStat[] Upstreams, DateTimeOffset CheckedAt, string? Warning);
public sealed record CaddyConfigText(string Source, string Text, string? Warning);

/// <summary>
/// Caddy, nur lesen. Quelle ist die Admin-API (genau ein Abruf: GET /config/apps/http/servers, fester Pfad, die Adresse kommt aus
/// JULOS_CADDY_ADMIN_URL und zeigt am besten auf einen Filter, der nur diesen Abruf durchlaesst) und/oder das Caddyfile (JULOS_CADDYFILE,
/// nur gelesen). JulOS aendert Caddy nie. Die Erreichbarkeit ist ein einfacher TCP-Verbindungstest, nur zu Zielen aus der Konfiguration.
/// An die Admin-API gehen ausschliesslich GET-Abrufe auf vier feste Pfade (Routen, Konfiguration, Ziele, interne CA), optional mit Zugangsdaten aus einer Datei
/// (JULOS_CADDY_ADMIN_AUTH_FILE: "Bearer TOKEN", "Basic ..." oder "benutzer:passwort"), die nie angezeigt oder geloggt werden. Zertifikate kommen aus einem TLS-Handshake
/// mit den gelisteten Domains, das Zugriffsprotokoll aus einer lesbaren Datei.
/// </summary>
public sealed class CaddyModule(CaddyOptions options, HttpMessageHandler? handler = null, Func<UpstreamTarget, CancellationToken, Task<string>>? connect = null, TimeProvider? time = null,
    Func<string, UpstreamTarget?, CancellationToken, Task<CertInfo>>? tls = null)
{
    public const string AdminPath = "/config/apps/http/servers";
    public const string ConfigPath = "/config/", UpstreamsPath = "/reverse_proxy/upstreams", PkiPath = "/pki/ca/local";
    public static readonly TimeSpan CertCache = TimeSpan.FromMinutes(5);
    public const int MaxFileBytes = 1024 * 1024, MaxAdminBytes = 2 * 1024 * 1024, MaxUpstreams = 40;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly HttpClient _http = OutboundHttp.Create(handler);
    private readonly Func<UpstreamTarget, CancellationToken, Task<string>> _connect = connect ?? TcpConnectAsync;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, UpstreamTarget?, CancellationToken, Task<CertInfo>> _tls = tls ?? ((d, c, ct) => CaddyCerts.ProbeAsync(d, c, time ?? TimeProvider.System, ct));
    private readonly SemaphoreSlim _certGate = new(1, 1);
    private CaddyCertificates? _certs;

    public bool Enabled => options.Mode != ModuleMode.Off;

    public async Task<CaddyListing> ReadAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        if (options.AdminUrl is null && string.IsNullOrWhiteSpace(options.CaddyfilePath))
            return new CaddyListing("none", [], ["Weder JULOS_CADDYFILE noch JULOS_CADDY_ADMIN_URL ist gesetzt."]);

        if (options.AdminUrl is { } admin)
        {
            try { return new CaddyListing("admin", await ReadAdminAsync(admin, ct), [.. warnings]); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
            {
                warnings.Add("Die Admin-API von Caddy war nicht erreichbar oder hat etwas Unerwartetes geantwortet.");
            }
        }
        if (!string.IsNullOrWhiteSpace(options.CaddyfilePath))
        {
            try { return new CaddyListing("caddyfile", CaddyfileParser.Parse(await ReadFileAsync(options.CaddyfilePath, ct)), [.. warnings]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                warnings.Add("Das Caddyfile ist nicht lesbar (Pfad, Rechte, Groesse).");
            }
        }
        return new CaddyListing("none", [], [.. warnings]);
    }

    private async Task<CaddyRoute[]> ReadAdminAsync(Uri baseUrl, CancellationToken ct)
    {
        using var doc = await AdminGetAsync(baseUrl, AdminPath, MaxAdminBytes, ct);
        return CaddyAdminConfig.Parse(doc.RootElement);
    }

    /// <summary>Genau ein GET auf einen festen Pfad der Admin-API, ohne Weiterleitung, mit Zugangsdaten aus der Datei (falls eingestellt).</summary>
    private async Task<JsonDocument> AdminGetAsync(Uri baseUrl, string path, int max, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
        req.Headers.Accept.ParseAdd("application/json");
        if (await AuthHeaderAsync(ct) is { } auth) req.Headers.TryAddWithoutValidation("Authorization", auth);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidDataException("Status " + (int)res.StatusCode);
        return JsonDocument.Parse(await OutboundHttp.ReadLimitedAsync(res, max, ct));
    }

    private async Task<string?> AuthHeaderAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.AdminAuthFile)) return null;
        var text = (await File.ReadAllTextAsync(options.AdminAuthFile, ct)).Trim().Split('\n')[0].Trim();
        if (text.Length == 0) return null;
        if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return text;
        return text.Contains(':') ? "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)) : "Bearer " + text;
    }

    /// <summary>Lebt Caddy? Antwortet die Admin-API auf den Routenabruf, wie schnell, und was Caddy ueber seine Ziele zaehlt (Anfragen, Fehlversuche). Nur Admin-API, nie der Container selbst.</summary>
    public async Task<CaddyHealth> HealthAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (options.AdminUrl is not { } admin) return new CaddyHealth("unknown", null, [], now, "Keine Admin-API eingestellt (JULOS_CADDY_ADMIN_URL).");
        var sw = Stopwatch.StartNew();
        try { (await AdminGetAsync(admin, AdminPath, MaxAdminBytes, ct)).Dispose(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            return new CaddyHealth("down", null, [], now, "Die Admin-API von Caddy antwortet nicht oder hat etwas Unerwartetes geantwortet.");
        }
        var ms = (int)sw.ElapsedMilliseconds;
        var stats = new List<CaddyUpstreamStat>();
        try
        {
            using var doc = await AdminGetAsync(admin, UpstreamsPath, 256 * 1024, ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var u in doc.RootElement.EnumerateArray().Take(MaxUpstreams))
                    if (u.ValueKind == JsonValueKind.Object && u.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String)
                        stats.Add(new CaddyUpstreamStat(CaddyRedact.Target(a.GetString()!), u.TryGetProperty("num_requests", out var n) && n.TryGetInt32(out var nn) ? nn : 0, u.TryGetProperty("fails", out var f) && f.TryGetInt32(out var ff) ? ff : 0));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException && !ct.IsCancellationRequested) { /* die Zaehler sind nur ein Zusatz */ }
        return new CaddyHealth("up", ms, [.. stats], now, null);
    }

    /// <summary>Die Konfiguration als Text ohne Geheimnisse (Admin-API: laufende Konfiguration; sonst das Caddyfile), hoechstens 256 KB.</summary>
    public async Task<CaddyConfigText> ConfigAsync(CancellationToken ct)
    {
        string? warning = null;
        if (options.AdminUrl is { } admin)
        {
            try { using var doc = await AdminGetAsync(admin, ConfigPath, MaxAdminBytes, ct); return new CaddyConfigText("admin", CaddyRedactor.Json(doc.RootElement), null); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
            { warning = "Die Admin-API von Caddy war nicht erreichbar oder hat etwas Unerwartetes geantwortet."; }
        }
        if (!string.IsNullOrWhiteSpace(options.CaddyfilePath))
        {
            try { return new CaddyConfigText("caddyfile", CaddyRedactor.Caddyfile(await ReadFileAsync(options.CaddyfilePath, ct)), warning); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { warning ??= "Das Caddyfile ist nicht lesbar (Pfad, Rechte, Groesse)."; }
        }
        return new CaddyConfigText("none", "", warning ?? "Weder JULOS_CADDYFILE noch JULOS_CADDY_ADMIN_URL ist gesetzt.");
    }

    /// <summary>Zertifikate der gelisteten Domains aus je einem TLS-Handshake (hoechstens 40, 6 gleichzeitig, 5 Minuten gemerkt), dazu die interne CA von Caddy, falls die Admin-API sie nennt.</summary>
    public async Task<CaddyCertificates> CertificatesAsync(CancellationToken ct)
    {
        await _certGate.WaitAsync(ct);
        try
        {
            if (_certs is { } c && _time.GetUtcNow() - c.CheckedAt < CertCache) return c;
            var listing = await ReadAsync(ct);
            var domains = CaddyCerts.Domains(listing.Routes);
            using var gate = new SemaphoreSlim(6);
            var certs = await Task.WhenAll(domains.Select(async d => { await gate.WaitAsync(ct); try { return await _tls(d, options.TlsConnect, ct); } finally { gate.Release(); } }));
            CaInfo? ca = null;
            if (options.AdminUrl is { } admin)
            {
                try { using var doc = await AdminGetAsync(admin, PkiPath, 64 * 1024, ct); ca = CaddyCerts.ParseCa(doc.RootElement, _time.GetUtcNow()); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException && !ct.IsCancellationRequested) { /* keine interne CA oder nicht lesbar */ }
            }
            return _certs = new CaddyCertificates([.. certs.OrderBy(x => x.State == "ok" ? 1 : 0).ThenBy(x => x.NotAfter)], ca, _time.GetUtcNow());
        }
        finally { _certGate.Release(); }
    }

    public Task<CaddyLog> LogAsync(CancellationToken ct) => CaddyAccessLog.ReadAsync(options.LogFile, ct);

    private static async Task<string> ReadFileAsync(string path, CancellationToken ct)
    {
        // Nur lesen; die Datei darf gleichzeitig von Caddy oder einem Editor geaendert werden
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (f.Length > MaxFileBytes) throw new InvalidDataException("Datei zu gross.");
        using var r = new StreamReader(f);
        return await r.ReadToEndAsync(ct);
    }

    /// <summary>Prueft die Ziele der gelisteten Proxy-Zeilen (hoechstens <see cref="MaxUpstreams"/>, je ein TCP-Verbindungsaufbau mit kurzer Frist).</summary>
    public async Task<CaddyReachability> CheckAsync(CancellationToken ct)
    {
        var listing = await ReadAsync(ct);
        var targets = listing.Routes.Where(r => r.Kind == "proxy").SelectMany(r => r.Targets).Distinct().Take(MaxUpstreams).ToArray();
        using var gate = new SemaphoreSlim(8);
        var states = await Task.WhenAll(targets.Select(async t =>
        {
            if (UpstreamTarget.Parse(t) is not { } target) return new UpstreamState(t, "skipped", null);
            await gate.WaitAsync(ct);
            try
            {
                var sw = Stopwatch.StartNew();
                var state = await _connect(target, ct);
                return new UpstreamState(t, state, state == "up" ? (int)sw.ElapsedMilliseconds : null);
            }
            finally { gate.Release(); }
        }));
        return new CaddyReachability(states, _time.GetUtcNow());
    }

    /// <summary>Verbindet nur (nichts wird gesendet). Adressen, zu denen nie verbunden wird, siehe <see cref="UpstreamTarget.IsForbidden"/>.</summary>
    public static async Task<string> TcpConnectAsync(UpstreamTarget t, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            var addresses = IPAddress.TryParse(t.Host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(t.Host, cts.Token);
            addresses = [.. addresses.Where(a => !UpstreamTarget.IsForbidden(a))];
            if (addresses.Length == 0) return "skipped";
            using var client = new TcpClient();
            await client.ConnectAsync(addresses, t.Port, cts.Token);
            return "up";
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { return "down"; }
    }
}
