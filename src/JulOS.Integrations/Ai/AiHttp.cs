using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace JulOS.Integrations.Ai;

/// <summary>
/// Der HttpClient der beiden Schluessel-Anbieter: feste Basisadresse (kein frei waehlbares Ziel), keine Weiterleitungen, kein Proxy aus der Umgebung. Die Frist je Anfrage setzt der Aufrufer
/// ueber das Abbruch-Token. Ein anderer Handler ist nur fuer Tests da.
/// </summary>
internal static class AiHttp
{
    public static HttpClient Create(Uri baseAddress, HttpMessageHandler? handler = null) =>
        new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) }, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("JulOS", null) } },
        };

    /// <summary>Die Zeilen eines Server-Sent-Events-Stroms als (Ereignis, Daten). Eine Zeile ist hoechstens 1 MiB lang.</summary>
    public static async IAsyncEnumerable<(string Event, string Data)> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string evt = "message";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 1 << 20) throw new AiProviderException("provider_error", "Antwort des Anbieters ist ungültig.");
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (evt, data.ToString());
                evt = "message"; data.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal)) evt = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); }
        }
        if (data.Length > 0) yield return (evt, data.ToString());
    }

    /// <summary>Die Fehlerantwort eines Anbieters als Ausnahme mit festem Code und gekuerztem Text.</summary>
    public static async Task<AiProviderException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var buffer = new char[4096];
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            var n = await reader.ReadBlockAsync(buffer, ct);
            using var doc = JsonDocument.Parse(new string(buffer, 0, n));
            if (doc.RootElement.TryGetProperty("error", out var error))
                detail = error.ValueKind == JsonValueKind.String ? error.GetString() : error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or IOException or HttpRequestException) { }
        var safe = AiText.Safe(detail);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiProviderException("not_signed_in", "Der Anbieter hat den Schlüssel abgelehnt."),
            HttpStatusCode.TooManyRequests => new AiProviderException("plan_limit", safe.Length > 0 ? safe : "Das Limit beim Anbieter ist erreicht."),
            >= HttpStatusCode.InternalServerError => new AiProviderException("provider_unavailable", "Der Anbieter ist gerade nicht erreichbar."),
            _ => new AiProviderException("provider_error", safe.Length > 0 ? safe : $"Der Anbieter antwortete mit {(int)response.StatusCode}."),
        };
    }
}
