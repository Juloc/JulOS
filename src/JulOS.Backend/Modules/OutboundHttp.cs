using System.Net.Http.Headers;
using System.Text;

namespace JulOS.Backend.Modules;

/// <summary>
/// HttpClient fuer die wenigen festen Abrufe der Module (Caddy-Admin-API, IP-Lookup, Router): keine Weiterleitungen, kein
/// Proxy aus der Umgebung, kurze Fristen, Antwortgroesse begrenzt. Die Adressen kommen nur aus den Einstellungen des Servers, nie vom Browser.
/// </summary>
public static class OutboundHttp
{
    public static HttpClient Create(HttpMessageHandler? handler = null) =>
        new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5),
            DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("JulOS", null) } },
        };

    /// <summary>Liest hoechstens <paramref name="max"/> Bytes; mehr ist ein Fehler (kein Speicher fuer beliebig grosse Antworten).</summary>
    public static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 0 and var len && len > max) throw new InvalidDataException("Antwort zu gross.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException("Antwort zu gross.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public static Uri? ParseHttpUrl(string name, string? value, List<string> warnings, bool rootOnly = false)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
        {
            warnings.Add($"{name}: keine gueltige http(s)-Adresse (ohne Benutzer:Passwort). Die Einstellung wird nicht benutzt.");
            return null;
        }
        if (rootOnly && (uri.AbsolutePath != "/" || uri.Query.Length > 0))
        {
            warnings.Add($"{name}: nur Adresse und Port angeben, ohne Pfad (der Pfad ist fest). Die Einstellung wird nicht benutzt.");
            return null;
        }
        return uri;
    }

    internal static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
