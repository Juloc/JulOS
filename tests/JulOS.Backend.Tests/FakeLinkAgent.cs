using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace JulOS.Backend.Tests;

/// <summary>Ein Agent im Speicher, der sich wie tools/julos-agent.py ueber WebSocket beim Server meldet (docs/AGENT_API.md) und merkt, was der Server will.</summary>
internal sealed class FakeLinkAgent : IAsyncDisposable
{
    public sealed record Put(int Id, string Kind, string Name, long Size, MemoryStream Data);

    private const string Empty = "{}";
    private readonly WebSocket _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Dictionary<int, Put> _puts = [];
    private readonly SemaphoreSlim _send = new(1, 1);

    public List<(string Method, string Path, string Body)> Requests { get; } = [];
    public List<Put> Finished { get; } = [];
    public List<int> Cancelled { get; } = [];
    public bool Closed { get; private set; }
    /// <summary>Was der Agent auf das Ende einer Uebertragung antwortet (Status, Body). Null: gar nicht (der Server wartet).</summary>
    public (int Status, string Body)? PutAnswer { get; set; } = (200, """{"ok":true}""");
    public byte[]? PullImage { get; set; }
    public (int Status, string Body)? PullError { get; set; }
    public string? HealthBody { get; set; }
    /// <summary>Nach dem ersten Stueck einer Uebertragung die Verbindung kappen.</summary>
    public bool DropDuringPut { get; set; }
    public string? WelcomeText { get; private set; }

    private FakeLinkAgent(WebSocket ws)
    {
        _ws = ws;
        _loop = Task.Run(Loop);
    }

    /// <summary>Baut die Verbindung auf und begruesst den Server. Wirft, wenn der Server die Verbindung ablehnt.</summary>
    public static async Task<FakeLinkAgent> ConnectAsync(TestServer server, string? token, string version = "0.3.0", string platform = "test-linux")
    {
        var client = server.CreateWebSocketClient();
        client.ConfigureRequest = r => { if (token is not null) r.Headers["Authorization"] = "Bearer " + token; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/api/agent-link"), CancellationToken.None);
        await SendText(ws, JsonSerializer.Serialize(new { t = "hello", agent = version, platform }));
        var agent = new FakeLinkAgent(ws);
        return agent;
    }

    private static Task SendText(WebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task Reply(string json)
    {
        await _send.WaitAsync();
        try { await SendText(_ws, json); } finally { _send.Release(); }
    }

    private async Task Loop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) { Closed = true; return; }
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    var frame = ms.ToArray();
                    var id = (int)BinaryPrimitives.ReadUInt32BigEndian(frame);
                    if (_puts.TryGetValue(id, out var p))
                    {
                        p.Data.Write(frame, 4, frame.Length - 4);
                        if (DropDuringPut) { _ws.Abort(); Closed = true; return; }
                    }
                    continue;
                }
                await HandleText(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException) { Closed = true; }
    }

    private async Task HandleText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var t = root.GetProperty("t").GetString();
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        switch (t)
        {
            case "welcome": WelcomeText = root.GetProperty("server").GetString(); break;
            case "bye": Closed = true; break;
            case "req":
            {
                var path = root.GetProperty("path").GetString()!;
                var method = root.GetProperty("method").GetString()!;
                lock (Requests) Requests.Add((method, path, root.TryGetProperty("body", out var b) ? b.GetRawText() : ""));
                var (status, body) = (method, path) switch
                {
                    ("GET", "/v1/health") => (200, HealthBody ?? """{"ok":true,"api":1,"platform":"link-test","mode":"desktop"}"""),
                    ("GET", "/v1/windows") => (200, """{"api":1,"mode":"desktop","screen":{"w":856,"h":1448},"windows":[{"id":777,"title":"Per Link","app":"claude","class":"Claude","pid":9,"minimized":false,"maximized":true,"active":true,"icon":""}]}"""),
                    ("GET", "/v1/apps") => (200, """{"apps":[{"id":"claude.desktop","name":"Claude","icon":""}]}"""),
                    ("POST", _) => (200, """{"ok":true}"""),
                    _ => (404, """{"error":"unbekannt"}"""),
                };
                await Reply($$"""{"t":"res","id":{{id}},"status":{{status}},"body":{{body}}}""");
                break;
            }
            case "put":
                _puts[id] = new Put(id, root.GetProperty("kind").GetString()!, root.GetProperty("name").GetString()!, root.GetProperty("size").GetInt64(), new MemoryStream());
                break;
            case "end":
                if (_puts.Remove(id, out var done))
                {
                    lock (Finished) Finished.Add(done);
                    if (PutAnswer is { } a) await Reply($$"""{"t":"res","id":{{id}},"status":{{a.Status}},"body":{{a.Body}}}""");
                }
                break;
            case "cancel":
                lock (Cancelled) Cancelled.Add(id);
                _puts.Remove(id);
                break;
            case "pull":
                if (PullError is { } err) await Reply($$"""{"t":"res","id":{{id}},"status":{{err.Status}},"body":{{err.Body}}}""");
                else if (PullImage is null) await Reply($$"""{"t":"res","id":{{id}},"status":204,"body":{{Empty}}}""");
                else
                {
                    await Reply($$"""{"t":"blob","id":{{id}},"size":{{PullImage.Length}}}""");
                    for (var off = 0; off < PullImage.Length; off += 20_000)
                    {
                        var n = Math.Min(20_000, PullImage.Length - off);
                        var frame = new byte[4 + n];
                        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)id);
                        Array.Copy(PullImage, off, frame, 4, n);
                        await _send.WaitAsync();
                        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None); } finally { _send.Release(); }
                    }
                    await Reply($$"""{"t":"res","id":{{id}},"status":200,"body":{{Empty}}}""");
                }
                break;
        }
    }

    /// <summary>Wartet, bis der Server die Verbindung beendet hat (Agent entfernt, abgewiesen, ersetzt).</summary>
    public async Task<bool> WaitClosedAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until) { if (Closed || _loop.IsCompleted) return true; await Task.Delay(20); }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _ws.Abort(); } catch (ObjectDisposedException) { }
        try { await _loop; } catch (Exception) { /* Ende des Tests */ }
        _ws.Dispose();
    }
}
