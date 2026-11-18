using System.Net.Sockets;
using System.Text.Json;

namespace JulOS.Integrations.Agent;

public sealed record AgentOptions(string SocketPath);

/// <summary>Antwort des Agenten: HTTP-Status und sein JSON (unveraendert durchgereicht, aber auf Gueltigkeit geprueft).</summary>
public sealed record AgentReply(int Status, string Json);

/// <summary>Der Agent antwortet nicht (nicht gestartet, Socket fehlt, zu langsam, ungueltige oder zu grosse Antwort).</summary>
public sealed class AgentUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Spricht die Agent-API v1 (docs/AGENT_API.md) ueber einen Unix-Socket mit dem Agenten in der Sitzung der Ziel-Maschine.
/// Kein Netzwerkport: der Socket liegt in einem Ordner mit Rechten 0700.
/// </summary>
public sealed class AgentClient : IDisposable
{
    public const int MaxResponseBytes = 600_000;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public AgentClient(AgentOptions options, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
            // der Agent schliesst nach jeder Antwort (HTTP/1.0): keine Verbindungen aufbewahren
            PooledConnectionIdleTimeout = TimeSpan.Zero,
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://agent/"), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public async Task<AgentReply> SendAsync(HttpMethod method, string path, string? jsonBody = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(method, path.TrimStart('/'));
            if (jsonBody is not null) request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxResponseBytes) throw new AgentUnavailableException("Antwort des Agenten zu groß.");
            }
            var json = buffer.Length == 0 ? "{}" : System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            using (JsonDocument.Parse(json)) { /* nur pruefen */ }
            return new AgentReply((int)response.StatusCode, json);
        }
        catch (AgentUnavailableException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AgentUnavailableException("Zeitüberschreitung beim Agenten."); }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or JsonException)
        {
            throw new AgentUnavailableException("Agent nicht erreichbar.", e);
        }
    }

    public void Dispose() => _http.Dispose();
}
