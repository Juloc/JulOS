using JulOS.Integrations.Files;
using JulOS.Integrations.Ssh;

namespace JulOS.Backend.Tests;

/// <summary>
/// Ein Dateisystem im Speicher hinter <see cref="ISftpFileSession"/>: Dateien, Ordner und Links, mit den Regeln eines SFTP-Ziels (Anlegen nur neu, Umbenennen ersetzt nur mit overwrite, Loeschen eines
/// Ordners nur leer). Dazu Fehler zum Einstellen (kein Zugriff auf Pfade, Abbruch nach n geschriebenen Bytes, eine Verbindung, die abreisst) und Zaehler (offene Ströme, Aufrufe).
/// </summary>
internal sealed class FakeFileSession : ISftpFileSession
{
    internal abstract record Node(DateTimeOffset Modified);
    internal sealed record FileNode(byte[] Data, DateTimeOffset Modified) : Node(Modified);
    internal sealed record DirNode(DateTimeOffset Modified) : Node(Modified);
    internal sealed record LinkNode(string Target, DateTimeOffset Modified) : Node(Modified);

    public readonly SortedDictionary<string, Node> Nodes = new(StringComparer.Ordinal);
    private static readonly DateTimeOffset Epoch = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    public SshHostKey Key { get; set; } = new("ssh-ed25519", "SHA256:" + new string('A', 43));
    public string Home { get; set; } = "/home/tester";
    public bool Connected { get; set; } = true;
    public string? LogonPassword { get; set; }                      // null: jedes Passwort gilt
    public int Disposed, Connects;
    public string? ConnectedPassword;
    public readonly HashSet<string> Denied = [];                    // Pfade (und alles darunter), auf die der Benutzer nicht darf
    public long? FailWriteAfter;                                     // ein Schreibstrom wirft nach so vielen Bytes
    public readonly List<string> Log = [];                           // "stat /x", "create /x" ... fuer Pruefungen
    public int OpenStreams;
    public Func<string, CancellationToken, Task>? BeforeRead;       // Haken, etwa um einen Abbruch mitten in einer Datei auszuloesen
    public bool RenameFails;                                          // ein Umbenennen ueber Ordner scheitert (zum Beispiel ein anderes Dateisystem); die temporaeren Namen beim Schreiben nicht

    public FakeFileSession() { Nodes["/"] = new DirNode(Epoch); Nodes[Home] = new DirNode(Epoch); AddParents(Home); }

    private void AddParents(string path)
    {
        for (var p = RemotePath.Parent(path); p != "/" && !Nodes.ContainsKey(p); p = RemotePath.Parent(p)) Nodes[p] = new DirNode(Epoch);
    }

    public FakeFileSession Dir(string path) { AddParents(path); Nodes[path] = new DirNode(Epoch); return this; }
    public FakeFileSession File(string path, string content, DateTimeOffset? at = null) => File(path, System.Text.Encoding.UTF8.GetBytes(content), at);
    public FakeFileSession File(string path, byte[] data, DateTimeOffset? at = null) { AddParents(path); Nodes[path] = new FileNode(data, at ?? Epoch); return this; }
    public FakeFileSession Link(string path, string target) { AddParents(path); Nodes[path] = new LinkNode(target, Epoch); return this; }
    public string Text(string path) => System.Text.Encoding.UTF8.GetString(((FileNode)Nodes[path]).Data);
    public bool Has(string path) => Nodes.ContainsKey(path);
    public IEnumerable<string> Under(string dir) => Nodes.Keys.Where(k => k != dir && RemotePath.IsInside(dir, k));

    public SshHostKey? PresentedHostKey { get; private set; }
    public bool IsConnected => Connected;
    public string HomeDirectory => Home;

    public Task ConnectAsync(SftpConnectOptions options, Func<SshHostKey, bool> trustHostKey, CancellationToken ct)
    {
        Connects++;
        PresentedHostKey = Key;
        if (!trustHostKey(Key)) throw new SshException(SshFailure.HostKeyRejected, "Host-Schlüssel nicht vertraut.");
        if (LogonPassword is not null && options.Password != LogonPassword) throw new SshException(SshFailure.LogonFailed, "Anmeldung abgelehnt.");
        ConnectedPassword = options.Password;
        Connected = true;
        return Task.CompletedTask;
    }

    public void Drop() => Connected = false;

    private void Check(string path)
    {
        if (!Connected) throw new RemoteFileException(RemoteFileError.Closed, "Die Verbindung zum Ziel ist abgerissen.");
        if (Denied.Any(d => RemotePath.IsInside(d, path))) throw new RemoteFileException(RemoteFileError.Denied, "Zugriff verweigert.");
    }

    private string Resolve(string path, int hops = 0)
    {
        if (!Nodes.TryGetValue(path, out var n) || n is not LinkNode l || hops > 20) return path;
        return Resolve(l.Target.StartsWith('/') ? l.Target : RemotePath.Join(RemotePath.Parent(path), l.Target), hops + 1);
    }

    private static RemoteKind KindOf(Node n) => n switch { FileNode => RemoteKind.File, DirNode => RemoteKind.Directory, _ => RemoteKind.Link };

    private RemoteEntry Entry(string path, bool follow)
    {
        var node = Nodes[path];
        RemoteKind? resolved = null;
        if (node is LinkNode)
        {
            var real = Resolve(path);
            if (Nodes.TryGetValue(real, out var target) && target is not LinkNode) resolved = KindOf(target);
            if (follow) { if (resolved is null) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden."); node = Nodes[real]; }
        }
        return new RemoteEntry(RemotePath.NameOf(path), path, KindOf(node), node is FileNode f ? f.Data.Length : 0, node.Modified, "rw-r--r--", follow ? null : resolved);
    }

    public Task<RemoteListing> ListAsync(string directory, int limit, CancellationToken ct)
    {
        Log.Add("list " + directory);
        Check(directory);
        var real = Resolve(directory);
        if (!Nodes.TryGetValue(real, out var n)) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        if (n is not DirNode) throw new RemoteFileException(RemoteFileError.NotADirectory, "Das ist kein Ordner.");
        var list = new List<RemoteEntry>();
        var hidden = 0;
        var truncated = false;
        foreach (var (p, node) in Nodes)
        {
            if (p == real || RemotePath.Parent(p) != real || p == "/") continue;
            if (RemotePath.NameOf(p).Any(char.IsControl)) { hidden++; continue; }
            if (list.Count >= limit) { truncated = true; break; }
            list.Add(Entry(p, follow: false));
        }
        return Task.FromResult(new RemoteListing(list, truncated, hidden));
    }

    public Task<RemoteEntry?> StatAsync(string path, bool follow, CancellationToken ct)
    {
        Log.Add("stat " + path);
        Check(path);
        if (!Nodes.ContainsKey(path)) return Task.FromResult<RemoteEntry?>(null);
        try { return Task.FromResult<RemoteEntry?>(Entry(path, follow)); }
        catch (RemoteFileException e) when (e.Error == RemoteFileError.NotFound) { return Task.FromResult<RemoteEntry?>(null); }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        Log.Add("read " + path);
        Check(path);
        var real = Resolve(path);
        if (!Nodes.TryGetValue(real, out var n)) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        if (n is not FileNode f) throw new RemoteFileException(RemoteFileError.Failed, "Das ist keine Datei.");
        if (BeforeRead is not null) await BeforeRead(path, ct);
        Interlocked.Increment(ref OpenStreams);
        return new CountingStream(new MemoryStream(f.Data, writable: false), () => Interlocked.Decrement(ref OpenStreams));
    }

    public Task<Stream> CreateAsync(string path, CancellationToken ct)
    {
        Log.Add("create " + path);
        Check(path);
        if (Nodes.ContainsKey(path)) throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.");
        if (!Nodes.TryGetValue(RemotePath.Parent(path), out var parent) || parent is not DirNode) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        Nodes[path] = new FileNode([], Epoch);                       // wie bei SFTP: die Datei ist gleich da, auch halb geschrieben
        Interlocked.Increment(ref OpenStreams);
        return Task.FromResult<Stream>(new WriteStream(this, path));
    }

    private sealed class CountingStream(Stream inner, Action onDispose) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); onDispose(); } base.Dispose(disposing); }
    }

    private sealed class WriteStream(FakeFileSession fs, string path) : Stream
    {
        private readonly MemoryStream _data = new();
        private bool _closed;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _data.Length; public override long Position { get => _data.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!fs.Connected) throw new RemoteFileException(RemoteFileError.Closed, "Die Verbindung zum Ziel ist abgerissen.");
            _data.Write(buffer, offset, count);
            fs.Nodes[path] = new FileNode(_data.ToArray(), Epoch);
            if (fs.FailWriteAfter is { } max && _data.Length >= max) throw new RemoteFileException(RemoteFileError.Failed, "Schreiben fehlgeschlagen.");
        }
        protected override void Dispose(bool disposing) { if (disposing && !_closed) { _closed = true; Interlocked.Decrement(ref fs.OpenStreams); } base.Dispose(disposing); }
    }

    public Task RenameAsync(string from, string to, bool overwrite, bool fromIsLink, CancellationToken ct)
    {
        Log.Add($"rename {from} {to}");
        // Wie die Bibliothek: ueber den Pfad eines Links wird sein ZIEL umbenannt, ausser der Aufrufer sagt, dass es ein Link ist
        if (!fromIsLink && Nodes.TryGetValue(from, out var maybeLink) && maybeLink is LinkNode) from = Resolve(from);
        Check(from); Check(to);
        if (RenameFails && !from.Contains("/.julos-")) throw new RemoteFileException(RemoteFileError.Failed, "Umbenennen scheitert.");
        if (!Nodes.TryGetValue(from, out var n)) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        if (Nodes.ContainsKey(to))
        {
            if (!overwrite) throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.");
            if (Nodes[to] is DirNode) throw new RemoteFileException(RemoteFileError.Failed, "Ziel ist ein Ordner.");
        }
        if (!Nodes.TryGetValue(RemotePath.Parent(to), out var parent) || parent is not DirNode) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        foreach (var key in Nodes.Keys.Where(k => RemotePath.IsInside(from, k)).ToList())
        {
            var moved = to + key[from.Length..];
            Nodes[moved] = Nodes[key];
            Nodes.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, bool isLink, CancellationToken ct)
    {
        Log.Add("delete " + path);
        Check(path);
        // Wie die Bibliothek: ueber den Pfad eines Links wird sein ZIEL geloescht, ausser der Aufrufer sagt, dass es ein Link ist
        if (!isLink && Nodes.TryGetValue(path, out var maybeLink) && maybeLink is LinkNode) path = Resolve(path);
        if (!Nodes.TryGetValue(path, out var n)) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        if (n is DirNode) throw new RemoteFileException(RemoteFileError.Failed, "Das ist ein Ordner.");
        Nodes.Remove(path);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        Log.Add("rmdir " + path);
        Check(path);
        if (!Nodes.TryGetValue(path, out var n)) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        if (n is not DirNode) throw new RemoteFileException(RemoteFileError.NotADirectory, "Das ist kein Ordner.");
        if (Under(path).Any()) throw new RemoteFileException(RemoteFileError.NotEmpty, "Der Ordner ist nicht leer.");
        Nodes.Remove(path);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        Log.Add("mkdir " + path);
        Check(path);
        if (Nodes.ContainsKey(path)) throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.");
        if (!Nodes.TryGetValue(RemotePath.Parent(path), out var parent) || parent is not DirNode) throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        Nodes[path] = new DirNode(Epoch);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { Disposed++; Connected = false; return ValueTask.CompletedTask; }
}
