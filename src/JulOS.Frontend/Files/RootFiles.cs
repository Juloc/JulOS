using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Hosts;
using JulOS.Integrations.Agent;
using JulOS.Integrations.Files;
using JulOS.Integrations.Ssh;

namespace JulOS.Frontend.Files;

/// <summary>Der Ort "Root" eines Hosts darf gerade nicht benutzt werden. Code: root_off (Stufe nicht "full"), root_not_permitted (der Agent erlaubt es lokal nicht), root_offline, root_outdated, root_paused, root_helper.</summary>
internal sealed class RootGateException(string code) : Exception("Root-Zugriff nicht möglich: " + code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Dateien als root auf einem Host (docs/FILES.md, "Root"): kein SSH, sondern der Agent des Hosts und sein root-Helfer (Wort <c>files-root</c> in /etc/julos-agent/permissions, Verben <c>files.*</c>).
/// Dieser Dienst pruft, ob der Ort gerade benutzbar ist (alle Schluessel: Pausenschalter, Stufe "full" am Server, Agent online und mindestens 0.8.0, Wort lokal erlaubt) und baut die Sitzung
/// (<see cref="RootFileSession"/>), die <see cref="ISftpFileSession"/> ueber den Agenten-Link spricht: dieselben Operationen, dieselbe Logik (<see cref="FileTransfer"/>) wie bei SFTP.
/// </summary>
internal sealed class RootFiles(AgentLinks links, AgentStore agents, HostStore hosts, HostActionService actions, TimeProvider time)
{
    public static readonly Version MinVersion = new(0, 8, 0);
    private static readonly TimeSpan GateLife = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<(Guid, Guid), (string? Code, DateTimeOffset At)> _gate = new();

    public AgentLinks Links => links;

    /// <summary>Der Ort fuer einen Host des Benutzers (als Verbindungssatz: Id = Host, Name "Host (Root)", Art "root"); null, wenn es den Host oder seinen Agenten nicht gibt.</summary>
    public async Task<ConnectionRecord?> PlaceAsync(Guid userId, Guid hostId, CancellationToken ct)
    {
        if (await hosts.GetAsync(userId, hostId, ct) is not { } host) return null;
        if (await agents.GetAsync(userId, hostId, ct) is null) return null;
        return new ConnectionRecord(hostId, host.Name + " (Root)", "root", host.Name, 0, "root", false, hostId);
    }

    /// <summary>Null, wenn der Ort jetzt benutzbar ist, sonst der Grund. Hoechstens alle 2 s neu gepruft (jeder Block fragt, die Datenbank nicht jedes Mal).</summary>
    public async Task<string?> GateAsync(Guid userId, Guid hostId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_gate.TryGetValue((userId, hostId), out var c) && now - c.At < GateLife) return c.Code;
        var code = await CheckAsync(userId, hostId, ct);
        _gate[(userId, hostId)] = (code, now);
        return code;
    }

    /// <summary>Vergisst das Ergebnis der letzten Pruefung (nach einer Aenderung der Stufe oder des Pausenschalters).</summary>
    public void Forget(Guid userId, Guid hostId) => _gate.TryRemove((userId, hostId), out _);

    private async Task<string?> CheckAsync(Guid userId, Guid hostId, CancellationToken ct)
    {
        if (await actions.IsPausedAsync(userId, ct)) return "root_paused";
        if (await agents.GetAsync(userId, hostId, ct) is not { } agent) return "root_offline";
        if (!AgentControl.Allows(agent.Control, "files-root")) return "root_off";
        if (links.Get(hostId, userId) is not { } link) return "root_offline";
        if (AgentLinks.TryParseVersion(link.Version) is not { } v || v < MinVersion) return "root_outdated";
        if (!link.Allow.Contains("files-root")) return "root_not_permitted";
        return null;
    }

    public RootFileSession NewSession(Guid userId, Guid hostId) => new(this, userId, hostId);
}

/// <summary>
/// <see cref="ISftpFileSession"/> fuer den Ort "Root": jede Operation ist eine Anfrage an den Agenten (<c>POST /v1/host/files/&lt;verb&gt;</c>), der sie an den root-Helfer weiterreicht. Lesen und Schreiben
/// gehen in Bloecken von 48 KiB, ein Block nach dem anderen: die naechste Anfrage kommt erst, wenn die vorige beantwortet ist, das ist der Rueckdruck. Jede Anfrage prueft die Schluessel neu
/// (<see cref="RootFiles.GateAsync"/>), eine Aenderung der Stufe oder der Pause beendet also auch laufende Uebertragungen. Was der Agent meldet, wird geprueft (Namen, Groessen), nicht geglaubt.
/// </summary>
internal sealed class RootFileSession(RootFiles root, Guid userId, Guid hostId) : ISftpFileSession
{
    public const int Block = 48 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public SshHostKey? PresentedHostKey => null;
    public bool IsConnected => root.Links.Get(hostId, userId) is not null;
    public string HomeDirectory => "/";

    public Task ConnectAsync(SftpConnectOptions options, Func<SshHostKey, bool> trustHostKey, CancellationToken ct) => throw new NotSupportedException("Der Ort Root verbindet über den Agenten.");

    /// <summary>Eine Anfrage; gibt die Antwort des Agenten oder wirft (Schluessel, Verbindung, Fehlercode des Helfers).</summary>
    private async Task<JsonDocument> CallAsync(string verb, Action<Utf8JsonWriter> body, CancellationToken ct)
    {
        if (await root.GateAsync(userId, hostId, ct) is { } closed) throw new RootGateException(closed);
        if (root.Links.Get(hostId, userId) is not { } link) throw new RootGateException("root_offline");
        string json;
        using (var ms = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
            json = Encoding.UTF8.GetString(ms.ToArray());
        }
        AgentReply reply;
        try { reply = await link.RequestAsync(HttpMethod.Post, "/v1/host/files/" + verb, json, Timeout, ct).ConfigureAwait(false); }
        catch (AgentUnavailableException e) { throw new RemoteFileException(RemoteFileError.Closed, "Die Verbindung zum Agenten ist abgerissen.", e); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(reply.Json); }
        catch (JsonException) { throw new RemoteFileException(RemoteFileError.Failed, "Ungültige Antwort des Agenten."); }
        if (reply.Status == 200 && doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
        var code = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e2) && e2.ValueKind == JsonValueKind.String ? e2.GetString() : null;
        doc.Dispose();
        throw Map(code ?? "failed");
    }

    /// <summary>Den Fehlercode des Helfers in die Ausnahmen der Dateioperationen (nie sein Text).</summary>
    private static Exception Map(string code) => code switch
    {
        "not_found" => new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden."),
        "denied" => new RemoteFileException(RemoteFileError.Denied, "Zugriff verweigert."),
        "exists" => new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon."),
        "not_empty" => new RemoteFileException(RemoteFileError.NotEmpty, "Der Ordner ist nicht leer."),
        "not_a_directory" => new RemoteFileException(RemoteFileError.NotADirectory, "Das ist kein Ordner."),
        "type_mismatch" => new FileRuleException("type_mismatch", "Am Ziel gibt es etwas anderes mit diesem Namen."),
        "is_link" => new FileRuleException("target_is_link", "Am Ziel gibt es einen Link."),
        "protected_path" => new FileRuleException("protected_path", "Dieser Pfad gehört dem Agenten."),
        "root_forbidden" => new FileRuleException("root_forbidden", "Das Wurzelverzeichnis wird nie verändert."),
        "invalid_path" or "invalid_name" or "invalid_request" => new FileRuleException("invalid_path", "Ungültiger Pfad."),
        "not_permitted" => new RootGateException("root_not_permitted"),
        "helper_unavailable" or "helper_closed" => new RootGateException("root_helper"),
        _ => new RemoteFileException(RemoteFileError.Failed, "Die Operation am Ziel ist fehlgeschlagen."),        // auch cross_device: der Aufrufer weicht auf Kopieren und Loeschen aus
    };

    private static RemoteKind Kind(string? k) => k switch { "file" => RemoteKind.File, "directory" => RemoteKind.Directory, "link" => RemoteKind.Link, _ => RemoteKind.Other };

    /// <summary>Ein Eintrag aus der Antwort des Agenten; null, wenn er sich nicht prueft (Name mit "/" oder Steuerzeichen: ein Eintrag kann nie aus dem Ordner hinausverweisen, F-24).</summary>
    private static RemoteEntry? Entry(string directory, JsonElement e, string? fixedName = null)
    {
        var name = fixedName ?? (e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
        if (fixedName is null && !RemotePath.ValidName(name)) return null;
        var size = e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sz) ? Math.Max(0, sz) : 0;
        long mt = 0;
        if (e.TryGetProperty("mtime", out var m) && m.ValueKind == JsonValueKind.Number) m.TryGetInt64(out mt);
        var mode = e.TryGetProperty("mode", out var mo) && mo.ValueKind == JsonValueKind.String && mo.GetString() is { Length: 9 } ms && ms.All(c => c is 'r' or 'w' or 'x' or '-') ? ms : "---------";
        var kind = Kind(e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null);
        RemoteKind? resolved = kind == RemoteKind.Link && e.TryGetProperty("resolved", out var r) && r.ValueKind == JsonValueKind.String ? Kind(r.GetString()) : null;
        var at = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(mt, 0, 253402300799));
        return new RemoteEntry(name!, name == "/" ? "/" : RemotePath.Join(directory, name!), kind, size, at, mode, resolved);
    }

    public async Task<RemoteListing> ListAsync(string directory, int limit, CancellationToken ct)
    {
        using var doc = await CallAsync("list", w => { w.WriteString("path", directory); w.WriteNumber("limit", limit); }, ct).ConfigureAwait(false);
        var entries = new List<RemoteEntry>();
        var hidden = doc.RootElement.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var hn) ? Math.Max(0, hn) : 0;
        var truncated = doc.RootElement.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
        if (doc.RootElement.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (entries.Count >= limit) { truncated = true; break; }
                if (Entry(directory, e) is { } entry) entries.Add(entry); else hidden++;
            }
        return new RemoteListing(entries, truncated, hidden);
    }

    public async Task<RemoteEntry?> StatAsync(string path, bool follow, CancellationToken ct)
    {
        using var doc = await CallAsync("stat", w => { w.WriteString("path", path); w.WriteBoolean("follow", follow); }, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("exists", out var x) || x.ValueKind != JsonValueKind.True) return null;
        return doc.RootElement.TryGetProperty("entry", out var e) && e.ValueKind == JsonValueKind.Object ? Entry(RemotePath.Parent(path), e, RemotePath.NameOf(path)) : null;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var s = new ReadStream(this, path);
        await s.FillAsync(ct).ConfigureAwait(false);               // der erste Block gleich: ein Fehler (nicht gefunden, kein Zugriff, keine Datei) kommt jetzt, nicht beim ersten Lesen
        return s;
    }

    public async Task<Stream> CreateAsync(string path, CancellationToken ct)
    {
        using (await CallAsync("create", w => w.WriteString("path", path), ct).ConfigureAwait(false)) { }
        return new WriteStream(this, path);
    }

    public async Task RenameAsync(string from, string to, bool overwrite, bool fromIsLink, CancellationToken ct)
    {
        using (await CallAsync("rename", w => { w.WriteString("from", from); w.WriteString("to", to); w.WriteBoolean("overwrite", overwrite); }, ct).ConfigureAwait(false)) { }
    }

    public async Task DeleteFileAsync(string path, bool isLink, CancellationToken ct)
    {
        using (await CallAsync("delete", w => w.WriteString("path", path), ct).ConfigureAwait(false)) { }
    }

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        using (await CallAsync("rmdir", w => w.WriteString("path", path), ct).ConfigureAwait(false)) { }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        using (await CallAsync("mkdir", w => w.WriteString("path", path), ct).ConfigureAwait(false)) { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Liest Block fuer Block: der naechste Block wird erst angefragt, wenn der Leser den vorigen ganz genommen hat.</summary>
    private sealed class ReadStream(RootFileSession s, string path) : Stream
    {
        private byte[] _block = [];
        private int _pos;
        private long _offset;
        private bool _eof;

        public async Task FillAsync(CancellationToken ct)
        {
            using var doc = await s.CallAsync("read", w => { w.WriteString("path", path); w.WriteNumber("offset", _offset); w.WriteNumber("length", Block); }, ct).ConfigureAwait(false);
            var text = doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            byte[] data;
            try { data = Convert.FromBase64String(text ?? ""); }
            catch (FormatException) { throw new RemoteFileException(RemoteFileError.Failed, "Ungültige Daten vom Agenten."); }
            if (data.Length > Block) throw new RemoteFileException(RemoteFileError.Failed, "Der Agent schickt mehr als angefragt.");
            _block = data; _pos = 0; _offset += data.Length;
            _eof = data.Length < Block || (doc.RootElement.TryGetProperty("eof", out var e) && e.ValueKind == JsonValueKind.True);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            if (_pos >= _block.Length)
            {
                if (_eof) return 0;
                await FillAsync(ct).ConfigureAwait(false);
                if (_block.Length == 0) return 0;
            }
            var n = Math.Min(buffer.Length, _block.Length - _pos);
            _block.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Schreibt Block fuer Block in die neue (temporaere) Datei: ein voller Block geht sofort, der Rest beim Leeren oder Schliessen.</summary>
    private sealed class WriteStream(RootFileSession s, string path) : Stream
    {
        private readonly byte[] _buffer = new byte[Block];
        private int _filled;
        private long _written;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            while (data.Length > 0)
            {
                var n = Math.Min(data.Length, Block - _filled);
                data[..n].CopyTo(_buffer.AsMemory(_filled));
                _filled += n; data = data[n..];
                if (_filled == Block) await SendAsync(ct).ConfigureAwait(false);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private async Task SendAsync(CancellationToken ct)
        {
            if (_filled == 0) return;
            var text = Convert.ToBase64String(_buffer, 0, _filled);
            var offset = _written;
            using (await s.CallAsync("write", w => { w.WriteString("path", path); w.WriteNumber("offset", offset); w.WriteString("data", text); }, ct).ConfigureAwait(false)) { }
            _written += _filled; _filled = 0;
        }

        public override Task FlushAsync(CancellationToken ct) => SendAsync(ct);
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;            // der Rest wird beim Leeren (FlushAsync) gesendet; nach einem Fehler nichts mehr
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _written + _filled; public override long Position { get => _written + _filled; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
