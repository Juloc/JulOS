using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace JulOS.Backend.Modules;

public sealed record CaddyOptions(ModuleMode Mode, string? CaddyfilePath, Uri? AdminUrl);
/// <summary>Source: admin (laufende Konfiguration), caddyfile (Datei) oder none (nichts lesbar, Warnings sagen warum).</summary>
public sealed record CaddyListing(string Source, CaddyRoute[] Routes, string[] Warnings);
/// <summary>State: up, down oder skipped (nicht pruefbar: Unix-Socket, Platzhalter, verbotene Adresse).</summary>
public sealed record UpstreamState(string Target, string State, int? Ms);
public sealed record CaddyReachability(UpstreamState[] Upstreams, DateTimeOffset CheckedAt);

/// <summary>
/// Caddy, nur lesen. Quelle ist die Admin-API (genau ein Abruf: GET /config/apps/http/servers, fester Pfad, die Adresse kommt aus
/// JULOS_CADDY_ADMIN_URL und zeigt am besten auf einen Filter, der nur diesen Abruf durchlaesst) und/oder das Caddyfile (JULOS_CADDYFILE,
/// nur gelesen). JulOS aendert Caddy nie. Die Erreichbarkeit ist ein einfacher TCP-Verbindungstest, nur zu Zielen aus der Konfiguration.
/// </summary>
public sealed class CaddyModule(CaddyOptions options, HttpMessageHandler? handler = null, Func<UpstreamTarget, CancellationToken, Task<string>>? connect = null, TimeProvider? time = null)
{
    public const string AdminPath = "/config/apps/http/servers";
    public const int MaxFileBytes = 1024 * 1024, MaxAdminBytes = 2 * 1024 * 1024, MaxUpstreams = 40;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly HttpClient _http = OutboundHttp.Create(handler);
    private readonly Func<UpstreamTarget, CancellationToken, Task<string>> _connect = connect ?? TcpConnectAsync;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool Enabled => options.Mode != ModuleMode.Off;

    public async Task<CaddyListing> ReadAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        if (options.AdminUrl is null && string.IsNullOrWhiteSpace(options.CaddyfilePath))
            return new CaddyListing("none", [], ["Weder JULOS_CADDYFILE noch JULOS_CADDY_ADMIN_URL ist gesetzt."]);

        if (options.AdminUrl is { } admin)
        {
            try { return new CaddyListing("admin", await ReadAdminAsync(admin, ct), [.. warnings]); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
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
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, AdminPath));
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidDataException("Status " + (int)res.StatusCode);
        var body = await OutboundHttp.ReadLimitedAsync(res, MaxAdminBytes, ct);
        using var doc = JsonDocument.Parse(body);
        return CaddyAdminConfig.Parse(doc.RootElement);
    }

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
