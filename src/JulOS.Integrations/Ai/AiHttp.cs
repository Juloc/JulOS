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

    /// <summary>Laenger darf eine Zeile eines Stroms nicht sein: die Grenze gilt beim Lesen, nicht erst danach (ein Anbieter ohne Zeilenende fuellt sonst den Speicher).</summary>
    public const int MaxLine = 1 << 20;

    /// <summary>Die Zeilen eines Server-Sent-Events-Stroms als (Ereignis, Daten). Eine Zeile ist hoechstens <see cref="MaxLine"/> Zeichen lang.</summary>
    public static async IAsyncEnumerable<(string Event, string Data)> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new LineReader(reader, MaxLine);
        string evt = "message";
        var data = new StringBuilder();
        while (await lines.ReadLineAsync(ct) is { } line)
        {
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

/// <summary>
/// Liest Zeilen (Ende: \n, \r\n oder \r) mit einer Obergrenze schon waehrend des Lesens: eine zu lange Zeile ist ein Fehler, bevor sie im Speicher liegt. Anders als
/// StreamReader.ReadLineAsync, das die ganze Zeile aufbaut, auch wenn das Gegenueber nie ein Zeilenende schickt.
/// </summary>
public sealed class LineReader(TextReader reader, int max)
{
    private readonly char[] _buffer = new char[4096];
    private int _pos, _len;
    private bool _skipLf;

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_pos >= _len)
            {
                _len = await reader.ReadAsync(_buffer.AsMemory(), ct);
                _pos = 0;
                if (_len == 0) return line.Length > 0 ? line.ToString() : null;
            }
            if (_skipLf) { _skipLf = false; if (_buffer[_pos] == '\n') { _pos++; continue; } }
            var start = _pos;
            while (_pos < _len && _buffer[_pos] is not ('\n' or '\r')) _pos++;
            if (line.Length + (_pos - start) > max) throw new AiProviderException("provider_error", "Die Antwort enthält eine zu lange Zeile.");
            line.Append(_buffer, start, _pos - start);
            if (_pos < _len)
            {
                if (_buffer[_pos] == '\r') _skipLf = true;
                _pos++;
                return line.ToString();
            }
        }
    }
}
