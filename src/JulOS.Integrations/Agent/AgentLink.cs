using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Agent;

/// <summary>Antwort des Agenten auf eine Zwischenablage-Abfrage: sein Status und die Bytes, falls er ein Bild geschickt hat.</summary>
public sealed record AgentBlobReply(AgentReply Reply, byte[]? Blob);

/// <summary>
/// Die Verbindung eines Agenten, der sich von der Ziel-Maschine aus beim Server gemeldet hat (WebSocket, docs/AGENT_API.md). Der Server schickt
/// nur feste Anfragen (Agent-API v1, Foto, Datei, Zwischenablage holen) und ordnet jede Antwort ueber eine Nummer zu; der Agent kann dem Server
/// nichts anderes auffordern. Grosse Daten gehen in Stuecken als Binaerframes (4 Byte Nummer + Nutzdaten), nie in einer Nachricht.
/// Alles gehoert zu genau einer Verbindung eines Benutzers; die Kennung steht beim Aufbau fest.
/// </summary>
public sealed class AgentLink
{
    public const int ChunkSize = 48 * 1024;                  // Nutzdaten je Binaerframe, die der Server schickt
    public const int MaxFramePayload = 64 * 1024;            // so viel nimmt der Server je Binaerframe hoechstens an
    public const int MaxTextBytes = 1_000_000;               // eine Textnachricht des Agenten (die Antwort selbst hoechstens 600 kB)
    public const int MaxPending = 32;
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(75);   // der Agent meldet sich alle 20 s
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan TransferWait = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PutTotal = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan PutAnswer = TimeSpan.FromSeconds(12);     // nach dem letzten Stueck: der Helfer hat selbst 8 s
    public static readonly TimeSpan PullTotal = TimeSpan.FromSeconds(30);

    private readonly WebSocket _ws;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _transferLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private int _nextId;

    public Guid ConnectionId { get; }
    public Guid UserId { get; }
    public string? Version { get; private set; }
    public string? Platform { get; private set; }
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;

    public AgentLink(WebSocket ws, Guid connectionId, Guid userId, ILogger log)
    {
        _ws = ws; ConnectionId = connectionId; UserId = userId; _log = log;
    }

    private sealed class Pending(int blobCap)
    {
        public readonly TaskCompletionSource<AgentReply> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly int BlobCap = blobCap;
        public MemoryStream? Blob;
        public long Declared = -1;
    }

    // ------------------------------------------------------------------ Aufbau und Empfang

    /// <summary>Wartet auf die Begruessung des Agenten und prueft Version. Gibt null zurueck, wenn der Agent abgelehnt wurde (die Verbindung ist dann schon geschlossen).</summary>
    internal async Task<AgentHello?> HandshakeAsync(Func<string?, bool> versionOk, string serverVersion, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);
        var msg = await ReceiveAsync(timeout.Token).ConfigureAwait(false);
        if (msg is not { Text: { } bytes }) { await CloseAsync(WebSocketCloseStatus.ProtocolError, "Begrüßung erwartet").ConfigureAwait(false); return null; }
        string? version, platform;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.GetProperty("t").GetString() != "hello") throw new JsonException();
            version = root.TryGetProperty("agent", out var v) && v.ValueKind == JsonValueKind.String ? AgentLabels.Clean(v.GetString()) : null;
            platform = root.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String ? AgentLabels.Clean(p.GetString()) : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            await CloseAsync(WebSocketCloseStatus.ProtocolError, "Begrüßung ungültig").ConfigureAwait(false);
            return null;
        }
        Version = version; Platform = platform;
        if (!versionOk(version))
        {
            await TrySendAsync(w => w.WriteString("t", "bye"), w => w.WriteString("reason", "agent_outdated"), ct).ConfigureAwait(false);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "Agent zu alt").ConfigureAwait(false);
            return new AgentHello(version, platform, false);
        }
        await SendJsonAsync(w => { w.WriteString("t", "welcome"); w.WriteString("server", serverVersion); }, ct).ConfigureAwait(false);
        return new AgentHello(version, platform, true);
    }

    internal async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(IdleTimeout);
                var msg = await ReceiveAsync(idle.Token).ConfigureAwait(false);
                if (msg is null) break;
                if (msg.Text is { } text) HandleText(text);
                else HandleBinary(msg.Binary!);
            }
        }
        catch (OperationCanceledException) { /* Leerlauf oder Ende des Servers */ }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or ProtocolViolation or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            _log.LogInformation("Agent-Link {Connection}: Verbindung beendet ({Reason})", ConnectionId, e is ProtocolViolation ? e.Message : e.GetType().Name);
        }
        finally
        {
            foreach (var (_, p) in _pending) p.Done.TrySetException(new AgentUnavailableException("Agent getrennt."));
            _pending.Clear();
            await CloseAsync(WebSocketCloseStatus.NormalClosure, "Ende").ConfigureAwait(false);
        }
    }

    private sealed class ProtocolViolation(string message) : Exception(message);

    private sealed record Message(byte[]? Text, byte[]? Binary);

    /// <summary>Liest eine ganze Nachricht (hoechstens MaxTextBytes als Text, MaxFramePayload + 4 als Binaer). Null bei Close.</summary>
    private async Task<Message?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, r.Count);
                var limit = r.MessageType == WebSocketMessageType.Text ? MaxTextBytes : MaxFramePayload + 4;
                if (ms.Length > limit) throw new ProtocolViolation("Nachricht zu groß");
                if (r.EndOfMessage) return r.MessageType == WebSocketMessageType.Text ? new Message(ms.ToArray(), null) : new Message(null, ms.ToArray());
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void HandleText(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        switch (root.GetProperty("t").GetString())
        {
            case "hb": return;
            case "res":
            {
                var id = root.GetProperty("id").GetInt32();
                if (!_pending.TryGetValue(id, out var p)) return;           // abgebrochen oder zu spaet
                var status = root.GetProperty("status").GetInt32();
                var json = root.TryGetProperty("body", out var b) ? b.GetRawText() : "{}";
                if (json.Length > AgentClient.MaxResponseBytes) { p.Done.TrySetException(new AgentUnavailableException("Antwort des Agenten zu groß.")); return; }
                if (status is < 100 or > 599) throw new ProtocolViolation("Status ungültig");
                p.Done.TrySetResult(new AgentReply(status, json));
                return;
            }
            case "blob":
            {
                var id = root.GetProperty("id").GetInt32();
                if (!_pending.TryGetValue(id, out var p)) return;
                var size = root.GetProperty("size").GetInt64();
                if (size < 0 || size > p.BlobCap || p.BlobCap == 0) { p.Done.TrySetResult(new AgentReply(500, """{"error":"Bild zu groß"}""")); return; }
                p.Declared = size; p.Blob = new MemoryStream((int)Math.Min(size, 1 << 20));
                return;
            }
            default: return;                                                // Unbekanntes wird ueberlesen (neuere Agenten)
        }
    }

    private void HandleBinary(byte[] frame)
    {
        if (frame.Length < 4) throw new ProtocolViolation("Binärframe zu kurz");
        var id = (int)BinaryPrimitives.ReadUInt32BigEndian(frame);
        if (!_pending.TryGetValue(id, out var p) || p.Blob is null) return;   // nach Abbruch noch unterwegs
        if (p.Blob.Length + frame.Length - 4 > Math.Min(p.Declared, p.BlobCap))
        {
            p.Blob = null;
            p.Done.TrySetResult(new AgentReply(500, """{"error":"Bild zu groß"}"""));
            return;
        }
        p.Blob.Write(frame, 4, frame.Length - 4);
    }

    // ------------------------------------------------------------------ Senden

    private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            try { await _ws.SendAsync(data, type, true, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _ws.Abort();                                               // der Agent liest nicht mehr
                throw new AgentUnavailableException("Agent antwortet nicht.");
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
            {
                throw new AgentUnavailableException("Agent nicht erreichbar.", e);
            }
        }
        finally { _sendLock.Release(); }
    }

    private Task SendJsonAsync(Action<Utf8JsonWriter> write, CancellationToken ct)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf)) { w.WriteStartObject(); write(w); w.WriteEndObject(); }
        return SendAsync(buf.WrittenMemory, WebSocketMessageType.Text, ct);
    }

    private async Task TrySendAsync(Action<Utf8JsonWriter> a, Action<Utf8JsonWriter> b, CancellationToken ct)
    {
        try { await SendJsonAsync(w => { a(w); b(w); }, ct).ConfigureAwait(false); } catch (AgentUnavailableException) { }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string text)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync(status, text, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException) { _ws.Abort(); }
    }

    /// <summary>Beendet die Verbindung von unserer Seite (Agent entfernt, neuer Agent hat uebernommen).</summary>
    public void Disconnect() { try { _ws.Abort(); } catch (ObjectDisposedException) { } }

    // ------------------------------------------------------------------ Anfragen

    private (int Id, Pending Pending) Register(int blobCap = 0)
    {
        if (_pending.Count >= MaxPending) throw new AgentUnavailableException("Agent ist überlastet.");
        var id = Interlocked.Increment(ref _nextId);
        var p = new Pending(blobCap);
        _pending[id] = p;
        return (id, p);
    }

    /// <summary>Eine Anfrage der Agent-API v1; Pfad und Inhalt kommen vom Server, nie vom Browser frei.</summary>
    public async Task<AgentReply> RequestAsync(HttpMethod method, string path, string? jsonBody, TimeSpan timeout, CancellationToken ct)
    {
        var (id, p) = Register();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            await SendJsonAsync(w =>
            {
                w.WriteString("t", "req"); w.WriteNumber("id", id); w.WriteString("method", method.Method); w.WriteString("path", path);
                if (jsonBody is not null) { w.WritePropertyName("body"); using var d = JsonDocument.Parse(jsonBody); d.RootElement.WriteTo(w); }
            }, limit.Token).ConfigureAwait(false);
            return await p.Done.Task.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AgentUnavailableException("Zeitüberschreitung beim Agenten."); }
        finally { _pending.TryRemove(id, out _); }
    }

    private Task CancelAsync(int id) =>
        TrySendAsync(w => w.WriteString("t", "cancel"), w => w.WriteNumber("id", id), CancellationToken.None);

    /// <summary>
    /// Bild oder Datei zum Agenten: Kopf, Stuecke von <see cref="ChunkSize"/>, Ende; der Agent legt sie in seinen eigenen Ordner, uebergibt sie
    /// dem Zwischenablage-Helfer und antwortet mit dessen Ergebnis. Es laeuft hoechstens eine Uebertragung je Agent. Ein Abbruch (Browser weg)
    /// sagt dem Agenten, dass er das Angefangene wegwerfen soll.
    /// </summary>
    public async Task<AgentReply> PutAsync(string kind, string name, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!await _transferLock.WaitAsync(TransferWait, ct).ConfigureAwait(false)) throw new AgentUnavailableException("Agent ist mit einer anderen Übertragung beschäftigt.");
        var (id, p) = Register();
        try
        {
            using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
            total.CancelAfter(PutTotal);
            await SendJsonAsync(w => { w.WriteString("t", "put"); w.WriteNumber("id", id); w.WriteString("kind", kind); w.WriteString("name", name); w.WriteNumber("size", data.Length); }, total.Token).ConfigureAwait(false);
            var frame = new byte[4 + ChunkSize];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)id);
            for (var offset = 0; offset < data.Length; offset += ChunkSize)
            {
                if (p.Done.Task.IsCompleted) break;                         // der Agent hat schon geantwortet (Fehler): nichts mehr schicken
                var n = Math.Min(ChunkSize, data.Length - offset);
                data.Slice(offset, n).CopyTo(frame.AsMemory(4));
                await SendAsync(frame.AsMemory(0, 4 + n), WebSocketMessageType.Binary, total.Token).ConfigureAwait(false);
            }
            if (!p.Done.Task.IsCompleted) await SendJsonAsync(w => { w.WriteString("t", "end"); w.WriteNumber("id", id); }, total.Token).ConfigureAwait(false);
            using var answer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            answer.CancelAfter(PutAnswer);
            return await p.Done.Task.WaitAsync(answer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(id).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            throw new AgentUnavailableException("Zeitüberschreitung beim Agenten.");
        }
        catch (AgentUnavailableException) { await CancelAsync(id).ConfigureAwait(false); throw; }
        finally { _pending.TryRemove(id, out _); _transferLock.Release(); }
    }

    /// <summary>Das Bild aus der Zwischenablage der Ziel-Maschine holen (nur Bilder, hoechstens <paramref name="maxBytes"/>).</summary>
    public async Task<AgentBlobReply> PullAsync(int maxBytes, CancellationToken ct)
    {
        if (!await _transferLock.WaitAsync(TransferWait, ct).ConfigureAwait(false)) throw new AgentUnavailableException("Agent ist mit einer anderen Übertragung beschäftigt.");
        var (id, p) = Register(maxBytes);
        try
        {
            using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
            total.CancelAfter(PullTotal);
            await SendJsonAsync(w => { w.WriteString("t", "pull"); w.WriteNumber("id", id); w.WriteNumber("max", maxBytes); }, total.Token).ConfigureAwait(false);
            var reply = await p.Done.Task.WaitAsync(total.Token).ConfigureAwait(false);
            if (reply.Status != 200) return new AgentBlobReply(reply, null);
            if (p.Blob is null || p.Blob.Length != p.Declared) return new AgentBlobReply(new AgentReply(500, """{"error":"Bild unvollständig"}"""), null);
            return new AgentBlobReply(reply, p.Blob.ToArray());
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(id).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            throw new AgentUnavailableException("Zeitüberschreitung beim Agenten.");
        }
        finally { _pending.TryRemove(id, out _); _transferLock.Release(); }
    }
}

internal sealed record AgentHello(string? Version, string? Platform, bool Accepted);

public static class AgentLabels
{
    /// <summary>Anzeigewerte, die der Agent meldet: kurz, nur harmlose Zeichen, sonst null.</summary>
    public static string? Clean(string? s, int max = 32)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or ' ') ? s : null;
    }
}
