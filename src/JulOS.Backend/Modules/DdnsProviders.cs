using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JulOS.Backend.Modules;

/// <summary>Ein bestehender DNS-Eintrag (Type: A oder AAAA).</summary>
public sealed record DnsRecord(string Host, string Type, string Content);

/// <summary>Ergebnis eines Schreibens. Message: ok, missing (es gibt den Eintrag nicht, es wird nie einer angelegt), ambiguous (mehrere Eintraege), rejected (der Anbieter lehnt ab), unreachable.</summary>
public sealed record DnsWriteResult(bool Ok, string Message);

/// <summary>
/// Schmale Schnittstelle zu einem DNS-Anbieter, nur wenn JulOS die Aktualisierung selbst macht. Read nennt den aktuellen Inhalt der benannten Eintraege (null: der Anbieter laesst sich nicht
/// lesen, dann entscheidet der Abgleich mit dem Namensdienst). Update aendert den Inhalt eines BESTEHENDEN Eintrags; angelegt oder geloescht wird nie etwas.
/// </summary>
public interface IDnsProvider
{
    string Name { get; }
    Task<IReadOnlyList<DnsRecord>?> ReadAsync(string[] hosts, string type, CancellationToken ct);
    Task<DnsWriteResult> UpdateAsync(string host, IPAddress ip, CancellationToken ct);
}

/// <summary>Liest ein Geheimnis aus einer Datei (erste Zeile, bei jedem Aufruf neu, damit ein Austausch ohne Neustart gilt). Es wird nie angezeigt oder geloggt.</summary>
public static class SecretFile
{
    public static async Task<string?> ReadAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var text = (await File.ReadAllTextAsync(path, ct)).Split('\n')[0].Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Cloudflare: Zone (Kennung) und Token aus einer Datei, am besten ein Token nur fuer diese eine Zone mit der Berechtigung "DNS bearbeiten". Gelesen wird GET /zones/{zone}/dns_records?type=&amp;name=,
/// geschrieben PATCH /zones/{zone}/dns_records/{id} mit nur "content" (Proxy-Schalter und TTL bleiben). Genau ein Eintrag je Name wird geaendert, kein POST, kein DELETE.
/// </summary>
public sealed class CloudflareProvider(HttpClient http, string zoneId, string? tokenFile) : IDnsProvider
{
    public const string Base = "https://api.cloudflare.com/client/v4/";
    public string Name => "Cloudflare";

    private async Task<HttpRequestMessage?> RequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var token = await SecretFile.ReadAsync(tokenFile, ct);
        if (token is null) return null;
        var req = new HttpRequestMessage(method, new Uri(new Uri(Base), $"zones/{Uri.EscapeDataString(zoneId)}/{path}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<(string Id, string Content)[]?> FindAsync(string host, string type, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Get, $"dns_records?type={type}&name={Uri.EscapeDataString(host)}", ct);
        if (req is null) return null;
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await OutboundHttp.ReadLimitedAsync(res, 256 * 1024, ct));
        if (!doc.RootElement.TryGetProperty("result", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        return [.. list.EnumerateArray().Where(r => r.TryGetProperty("id", out _) && r.TryGetProperty("content", out _))
            .Select(r => (r.GetProperty("id").GetString()!, r.GetProperty("content").GetString() ?? ""))];
    }

    public async Task<IReadOnlyList<DnsRecord>?> ReadAsync(string[] hosts, string type, CancellationToken ct)
    {
        var result = new List<DnsRecord>();
        foreach (var h in hosts)
        {
            try { if (await FindAsync(h, type, ct) is not { } found) return null; result.AddRange(found.Select(f => new DnsRecord(h, type, f.Content))); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException && !ct.IsCancellationRequested) { return null; }
        }
        return result;
    }

    public async Task<DnsWriteResult> UpdateAsync(string host, IPAddress ip, CancellationToken ct)
    {
        var type = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "AAAA" : "A";
        try
        {
            if (await FindAsync(host, type, ct) is not { } found) return new DnsWriteResult(false, "unreachable");
            if (found.Length == 0) return new DnsWriteResult(false, "missing");
            if (found.Length > 1) return new DnsWriteResult(false, "ambiguous");
            using var req = await RequestAsync(new HttpMethod("PATCH"), $"dns_records/{Uri.EscapeDataString(found[0].Id)}", ct);
            if (req is null) return new DnsWriteResult(false, "unreachable");
            req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = ip.ToString() }), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return res.IsSuccessStatusCode ? new DnsWriteResult(true, "ok") : new DnsWriteResult(false, "rejected");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException && !ct.IsCancellationRequested) { return new DnsWriteResult(false, "unreachable"); }
    }
}

/// <summary>
/// Allgemeine Aktualisierungs-Adresse (DynDNS-Art): JULOS_DDNS_UPDATE_URL mit {host} und {ip}, GET, optional Zugangsdaten aus einer Datei ("benutzer:passwort" oder "Bearer ..."). Antwort: 2xx,
/// und beginnt der Text mit einem DynDNS-Fehlercode (badauth, nohost, abuse, ...), gilt es als abgelehnt. Die Adresse kann ein Token enthalten und wird deshalb nie angezeigt oder geloggt. Lesen geht hier nicht.
/// </summary>
public sealed class GenericUrlProvider(HttpClient http, string template, string? authFile) : IDnsProvider
{
    public string Name => "Update-URL";
    private static readonly string[] Errors = ["badauth", "nohost", "notfqdn", "abuse", "badagent", "dnserr", "911", "!yours", "numhost", "ko"];

    public Task<IReadOnlyList<DnsRecord>?> ReadAsync(string[] hosts, string type, CancellationToken ct) => Task.FromResult<IReadOnlyList<DnsRecord>?>(null);

    public async Task<DnsWriteResult> UpdateAsync(string host, IPAddress ip, CancellationToken ct)
    {
        try
        {
            var url = template.Replace("{host}", Uri.EscapeDataString(host)).Replace("{ip}", Uri.EscapeDataString(ip.ToString()));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (await SecretFile.ReadAsync(authFile, ct) is { } auth)
                req.Headers.Authorization = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? new AuthenticationHeaderValue("Bearer", auth[7..].Trim())
                    : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return new DnsWriteResult(false, "rejected");
            var body = OutboundHttp.Text(await OutboundHttp.ReadLimitedAsync(res, 1024, ct)).Trim().ToLowerInvariant();
            return Errors.Any(e => body.StartsWith(e)) ? new DnsWriteResult(false, "rejected") : new DnsWriteResult(true, "ok");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or UriFormatException && !ct.IsCancellationRequested) { return new DnsWriteResult(false, "unreachable"); }
    }
}

/// <summary>DuckDNS: GET https://www.duckdns.org/update?domains=NAME&amp;token=...&amp;ip=... ("OK" oder "KO"). Der Token kommt aus einer Datei. Lesen geht nicht (der Anbieter nennt den Eintrag nicht), den Vergleich uebernimmt der Namensdienst.</summary>
public sealed class DuckDnsProvider(HttpClient http, string? tokenFile) : IDnsProvider
{
    public const string Base = "https://www.duckdns.org/update";
    public string Name => "DuckDNS";

    public Task<IReadOnlyList<DnsRecord>?> ReadAsync(string[] hosts, string type, CancellationToken ct) => Task.FromResult<IReadOnlyList<DnsRecord>?>(null);

    /// <summary>"home.duckdns.org" -> "home"; null fuer alles, was keine Subdomain von duckdns.org ist.</summary>
    public static string? Subdomain(string host) =>
        host.EndsWith(".duckdns.org", StringComparison.OrdinalIgnoreCase) && host.Count(c => c == '.') == 2 ? host[..host.IndexOf('.')] : null;

    public async Task<DnsWriteResult> UpdateAsync(string host, IPAddress ip, CancellationToken ct)
    {
        if (Subdomain(host) is not { } sub) return new DnsWriteResult(false, "missing");
        if (await SecretFile.ReadAsync(tokenFile, ct) is not { } token) return new DnsWriteResult(false, "unreachable");
        try
        {
            var family = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "ipv6" : "ip";
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}?domains={Uri.EscapeDataString(sub)}&token={Uri.EscapeDataString(token)}&{family}={Uri.EscapeDataString(ip.ToString())}");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return new DnsWriteResult(false, "rejected");
            var body = OutboundHttp.Text(await OutboundHttp.ReadLimitedAsync(res, 256, ct)).Trim();
            return body.StartsWith("OK", StringComparison.Ordinal) ? new DnsWriteResult(true, "ok") : new DnsWriteResult(false, "rejected");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException && !ct.IsCancellationRequested) { return new DnsWriteResult(false, "unreachable"); }
    }
}
