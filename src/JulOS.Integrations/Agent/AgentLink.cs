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
    public static readonly TimeSpan UpdateTotal = TimeSpan.FromSeconds(60);    // Datei holen, pruefen, ersetzen

    private readonly WebSocket _ws;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _transferLock = new(1, 1);
    private int _updating;
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private int _nextId;

    public Guid HostId { get; }
    public Guid UserId { get; }
    public string? Version { get; private set; }
    public string? Platform { get; private set; }
    public string? Hostname { get; private set; }
    public string? Os { get; private set; }
    /// <summary>Was der Agent kann (nur bekannte Namen, siehe <see cref="AgentLabels.Caps"/>): "desktop" (Fenster, Programme, Zwischenablage), "host" (beantwortet die Host-Anfragen) und die Host-Faehigkeiten sysinfo, updates, services, docker.
    /// Seit Agent 0.7.0 kann ein Desktop-Agent beides; aeltere Agenten werden aus ihrer Plattform und ihren Faehigkeiten ergaenzt (siehe <see cref="AgentLabels.NormalizeCaps"/>), ein alter Desktop-Agent bleibt bei "desktop".</summary>
    public IReadOnlyList<string> Caps { get; private set; } = [];
    /// <summary>Aktionsarten, die der Agent lokal erlaubt (hello "allow", siehe <see cref="AgentLabels.Rights"/>); Stufe 3.</summary>
    public IReadOnlyList<string> Allow { get; private set; } = [];
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;

    public AgentLink(WebSocket ws, Guid hostId, Guid userId, ILogger log)
    {
        _ws = ws; HostId = hostId; UserId = userId; _log = log;
    }

    private sealed class Pending(int blobCap, Action<AgentProgress>? onProgress = null)
    {
        public readonly Action<AgentProgress>? OnProgress = onProgress;
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
        string? version, platform, hostname, os;
        IReadOnlyList<string> caps, allow;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.GetProperty("t").GetString() != "hello") throw new JsonException();
            version = root.TryGetProperty("agent", out var v) && v.ValueKind == JsonValueKind.String ? AgentLabels.Clean(v.GetString()) : null;
            platform = root.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String ? AgentLabels.Clean(p.GetString()) : null;
            hostname = root.TryGetProperty("hostname", out var hn) && hn.ValueKind == JsonValueKind.String ? AgentLabels.CleanText(hn.GetString()) : null;
            os = root.TryGetProperty("os", out var o) && o.ValueKind == JsonValueKind.String ? AgentLabels.CleanText(o.GetString()) : null;
            caps = AgentLabels.NormalizeCaps(platform, AgentLabels.Words(root, "caps", AgentLabels.Caps));
            allow = AgentLabels.Words(root, "allow", AgentLabels.Rights);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            await CloseAsync(WebSocketCloseStatus.ProtocolError, "Begrüßung ungültig").ConfigureAwait(false);
            return null;
        }
        Version = version; Platform = platform; Hostname = hostname; Os = os; Caps = caps; Allow = allow;
        if (!versionOk(version))
        {
            await TrySendAsync(w => w.WriteString("t", "bye"), w => w.WriteString("reason", "agent_outdated"), ct).ConfigureAwait(false);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "Agent zu alt").ConfigureAwait(false);
            return new AgentHello(new AgentInfo(version, platform, hostname, os, caps), false);
        }
        await SendJsonAsync(w => { w.WriteString("t", "welcome"); w.WriteString("server", serverVersion); }, ct).ConfigureAwait(false);
        return new AgentHello(new AgentInfo(version, platform, hostname, os, caps), true);
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
            _log.LogInformation("Agent-Link {Host}: Verbindung beendet ({Reason})", HostId, e is ProtocolViolation ? e.Message : e.GetType().Name);
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
            case "progress":
            {
                // Fortschritt einer langen Aktion (Stufe 3): nur zu einer offenen Anfrage, Text gekuerzt und bereinigt, Zaehler begrenzt
                var id = root.GetProperty("id").GetInt32();
                if (!_pending.TryGetValue(id, out var p) || p.OnProgress is not { } cb) return;
                var text = root.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? AgentLabels.Printable(tx.GetString(), 160) : "";
                int? done = root.TryGetProperty("done", out var dn) && dn.ValueKind == JsonValueKind.Number && dn.TryGetInt32(out var d) ? Math.Clamp(d, 0, 100000) : null;
                int? total = root.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number && tt.TryGetInt32(out var n) ? Math.Clamp(n, 0, 100000) : null;
                try { cb(new AgentProgress(text, done, total)); } catch (Exception e) when (e is not OutOfMemoryException) { _log.LogDebug("Fortschritt nicht verarbeitet ({Type})", e.GetType().Name); }
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

    private (int Id, Pending Pending) Register(int blobCap = 0, Action<AgentProgress>? onProgress = null)
    {
        if (_pending.Count >= MaxPending) throw new AgentUnavailableException("Agent ist überlastet.");
        var id = Interlocked.Increment(ref _nextId);
        var p = new Pending(blobCap, onProgress);
        _pending[id] = p;
        return (id, p);
    }

    /// <summary>Eine Anfrage der Agent-API v1; Pfad und Inhalt kommen vom Server, nie vom Browser frei. <paramref name="onProgress"/> bekommt die Fortschrittsmeldungen langer Aktionen.</summary>
    public async Task<AgentReply> RequestAsync(HttpMethod method, string path, string? jsonBody, TimeSpan timeout, CancellationToken ct, Action<AgentProgress>? onProgress = null)
    {
        var (id, p) = Register(0, onProgress);
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

    /// <summary>
    /// Bittet den Agenten, sich zu aktualisieren: er holt die Datei selbst von der Adresse seines Servers (<paramref name="path"/> ist nur der feste Pfad, nie eine volle Adresse),
    /// prueft die SHA-256-Summe und ersetzt sich. Die Antwort kommt, bevor er neu startet (200) oder mit dem Grund der Ablehnung; danach reisst die Verbindung kurz ab und der Agent
    /// meldet sich mit der neuen Version. Hoechstens eine Aktualisierung gleichzeitig.
    /// </summary>
    public async Task<AgentReply> UpdateAsync(string path, string sha256, string version, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0) return new AgentReply(409, """{"error":"Eine Aktualisierung läuft schon."}""");
        var (id, p) = Register();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(UpdateTotal);
            await SendJsonAsync(w => { w.WriteString("t", "update"); w.WriteNumber("id", id); w.WriteString("path", path); w.WriteString("sha256", sha256); w.WriteString("version", version); }, limit.Token).ConfigureAwait(false);
            return await p.Done.Task.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AgentUnavailableException("Zeitüberschreitung beim Agenten."); }
        finally { _pending.TryRemove(id, out _); Volatile.Write(ref _updating, 0); }
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

/// <summary>Eine Fortschrittsmeldung des Agenten zu einer laufenden Aktion: Text (bereinigt), erledigt und gesamt (wenn bekannt).</summary>
public sealed record AgentProgress(string Text, int? Done, int? Total);

/// <summary>Was ein Agent von sich meldet (reine Anzeigewerte, jeder darf fehlen).</summary>
public sealed record AgentInfo(string? Version, string? Platform, string? Hostname = null, string? Os = null, IReadOnlyList<string>? Caps = null);

internal sealed record AgentHello(AgentInfo Info, bool Accepted)
{
    public string? Version => Info.Version;
}

public static class AgentLabels
{
    /// <summary>Die Faehigkeiten, die ein Agent melden darf (docs/HOST-AGENT.md): "desktop" und "host" sind die zwei Gruppen, die anderen vier die Lese-Faehigkeiten der Host-Gruppe. Andere Woerter im hello werden verworfen.</summary>
    public static readonly string[] Caps = ["desktop", "host", "sysinfo", "updates", "services", "docker"];

    /// <summary>Die Lese-Faehigkeiten der Host-Gruppe (je ein fester Pfad /v1/host/...).</summary>
    public static readonly string[] HostCaps = ["sysinfo", "updates", "services", "docker"];

    /// <summary>Ab dieser Version meldet ein Desktop-Agent die Host-Faehigkeiten selbst (sonst "liefert noch keine Systemwerte": <see cref="HostSupport"/> sagt "update").</summary>
    public static readonly Version MinDesktopHostVersion = new(0, 7, 0);

    /// <summary>
    /// Ergaenzt die gemeldeten Woerter fuer Agenten vor 0.7.0, damit die Oberflaeche einheitlich nach Woertern fragen kann: "host", sobald eine Lese-Faehigkeit da ist (ein Host-Agent 0.5/0.6),
    /// "desktop", wenn die Plattform keinen Host-Agenten ohne Desktop meint (ein alter Desktop-Agent meldet gar keine Woerter). Die Reihenfolge ist die von <see cref="Caps"/>.
    /// </summary>
    public static IReadOnlyList<string> NormalizeCaps(string? platform, IReadOnlyList<string> reported)
    {
        var set = new HashSet<string>(reported, StringComparer.Ordinal);
        if (set.Overlaps(HostCaps)) set.Add("host");
        if (KindOf(platform) == "desktop") set.Add("desktop");
        return [.. Caps.Where(set.Contains)];
    }

    /// <summary>
    /// Kann der Agent dieses Hosts die Host-Funktionen (Systemwerte, Updates, Aktionen)? "yes": er hat "host" gemeldet; "update": ein Desktop-Agent vor 0.7.0 (bringt sich per Selbst-Update auf den Stand, ohne dass dabei ein Recht entsteht);
    /// "none": verbunden und neu genug, meldet aber nichts davon (kein Linux); "unknown": nicht verbunden, der Stand ist nicht bekannt. <paramref name="liveCaps"/> ist null, solange der Agent nicht verbunden ist.
    /// </summary>
    public static string HostSupport(string? platform, string? version, IReadOnlyCollection<string>? liveCaps)
    {
        if (liveCaps is not null && liveCaps.Contains("host")) return "yes";
        var old = KindOf(platform) == "desktop" && AgentLinks.TryParseVersion(version) is { } v && v < MinDesktopHostVersion;
        if (old) return "update";
        return liveCaps is null ? "unknown" : "none";
    }

    /// <summary>Die lokalen Rechte (Aktionsarten), die ein Agent melden darf (dieselben Woerter wie AgentControl.Rights).</summary>
    public static readonly string[] Rights = ["updates", "reboot", "services", "docker", "logs"];

    /// <summary>Anzeigetext des Agenten: nur druckbare Zeichen, hoechstens <paramref name="max"/> lang (Steuerzeichen und Umbrueche fallen weg).</summary>
    public static string Printable(string? s, int max)
    {
        if (s is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var rune in s.EnumerateRunes())
        {
            if (sb.Length >= max) break;
            if (System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
            sb.Append(rune.ToString());
        }
        return sb.ToString().Trim();
    }

    /// <summary>Ausgabe eines Programms auf dem Host (zum Beispiel apt): wie <see cref="Printable"/>, aber Zeilenumbrueche bleiben (CRLF wird LF, ein Tabulator ein Leerzeichen, nie mehr als eine Leerzeile hintereinander), damit der Dialog sie als Text mit Umbruch zeigen kann.</summary>
    public static string PrintableLines(string? s, int max)
    {
        if (s is null) return "";
        var sb = new System.Text.StringBuilder();
        var newlines = 0;
        foreach (var rune in s.Replace("\r\n", "\n").EnumerateRunes())
        {
            if (sb.Length >= max) break;
            if (rune.Value == '\n') { if (++newlines <= 2) sb.Append('\n'); continue; }
            newlines = 0;
            if (rune.Value == '\t') { sb.Append(' '); continue; }
            if (System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
            sb.Append(rune.ToString());
        }
        return sb.ToString().Trim();
    }

    /// <summary>Eine Liste von Woertern aus dem hello: nur aus <paramref name="allowed"/>, ohne Doppelte, in der Reihenfolge der Erlaubnisliste. Alles Falsche gibt eine leere Liste.</summary>
    internal static IReadOnlyList<string> Words(JsonElement root, string name, string[] allowed)
    {
        if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var n = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (++n > 32) break;
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } w && allowed.Contains(w)) seen.Add(w);
        }
        return [.. allowed.Where(seen.Contains)];
    }

    /// <summary>'host' fuer einen Agenten ohne Desktop (Plattform "linux-host"), sonst 'desktop'.</summary>
    public static string KindOf(string? platform) => platform is not null && platform.EndsWith("-host", StringComparison.Ordinal) ? "host" : "desktop";

    /// <summary>Anzeigewerte, die der Agent meldet: kurz, nur harmlose Zeichen, sonst null.</summary>
    public static string? Clean(string? s, int max = 32)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or ' ') ? s : null;
    }

    /// <summary>Freier Anzeigetext, den der Agent meldet (Rechnername, Betriebssystem): hoechstens 64 Zeichen aus Buchstaben, Ziffern, Leerzeichen und . - _ + / ( ) ,. Alles andere gilt als unbrauchbar, nicht als zu kuerzen.</summary>
    public static string? CleanText(string? s, int max = 64)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or ' ' or '/' or '(' or ')' or ',') ? s : null;
    }
}
