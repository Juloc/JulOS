using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace JulOS.Integrations.Ai;

/// <summary>
/// Das Programm im eigenen Container (docs/CODEX-SIDECAR.md): eine Verbindung zum Unix-Socket des Tores (codex-gate) je Aufruf. Die erste Zeile ist {"cmd":"app-server"} (oder login,
/// login-status, logout, version, selftest); danach gehen rohe Bytes in beide Richtungen. Das Tor schliesst die Verbindung, wenn das Programm endet, und schickt davor eine Zeile
/// "\u001e{"exit":N}" (Satztrenner zuerst); sie wird hier abgetrennt. Ohne diese Zeile gilt der Aufruf als abgebrochen (Exit -1). Kill schliesst die Verbindung; das Tor beendet dann das Programm.
/// Es gibt keinen freien Text zum Tor: nur der Name des Befehls (und beim Selbsttest Pfad und Wort).
/// </summary>
public sealed class CodexSocketRunner(string socketPath, TimeProvider? clock = null) : ICodexRunner
{
    /// <summary>Der Arbeitsordner im Container des Programms (das Tor legt ihn an).</summary>
    public const string SidecarWork = "/home/codex/work";

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset _refreshed;
    private DateTime? _socketStamp;
    private volatile bool _available;
    private volatile string? _version;

    public bool Isolated => true;
    public bool Available => _available;
    public string? Version => _version;
    public string WorkDirectory => SidecarWork;

    /// <summary>
    /// Wird ausgeloest, wenn der Container nach einer Pause wieder antwortet (auch beim ersten Mal) oder neu gestartet wurde: dann gehoert der Selbsttest neu gemacht. Ein Neustart
    /// zwischen zwei Nachfragen faellt nicht als Pause auf; das Tor legt seinen Socket aber bei jedem Start neu an, darum zaehlt auch eine neue Zeit der Socket-Datei.
    /// </summary>
    public event Action? BecameAvailable;

    public static string CommandName(CodexCommand c) => c switch
    {
        CodexCommand.AppServer => "app-server", CodexCommand.Login => "login", CodexCommand.LoginStatus => "login-status", CodexCommand.Logout => "logout",
        CodexCommand.Version => "version", CodexCommand.SelfTest => "selftest", _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>Fragt hoechstens alle 10 Sekunden wirklich nach der Version (und damit, ob der Container antwortet).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            if (_clock.GetUtcNow() - _refreshed < TimeSpan.FromSeconds(10)) return;
            var was = _available;
            string? version = null; var ok = false;
            try
            {
                using var process = Start(CodexCommand.Version);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(8));
                using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8);
                var text = reader.ReadToEndAsync(limit.Token);
                var code = await process.WaitForExitAsync(limit.Token);
                version = (await text).Trim();
                ok = code == 0 && version.Length > 0 && version.Length < 200;
            }
            catch (Exception e) when (e is AiProviderException or IOException or OperationCanceledException && !ct.IsCancellationRequested) { }
            var stamp = SocketStamp();
            var restarted = ok && was && stamp != _socketStamp;
            _available = ok; _version = ok ? version : null; _refreshed = _clock.GetUtcNow(); _socketStamp = stamp;
            if (ok && (!was || restarted)) BecameAvailable?.Invoke();
        }
        finally { _refresh.Release(); }
    }

    private DateTime? SocketStamp()
    {
        try { return File.Exists(socketPath) ? File.GetLastWriteTimeUtc(socketPath) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public ICodexProcess Start(CodexCommand command, CodexSelfTestArgs? selfTest = null)
    {
        if (command == CodexCommand.SelfTest && selfTest is null) throw new ArgumentNullException(nameof(selfTest));
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            socket.Send(FirstLine(command, selfTest));
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            socket.Dispose();
            _available = false;
            throw new AiProviderException("provider_unavailable", "Der Codex-Container ist nicht erreichbar.");
        }
        return new SocketProcess(socket);
    }

    /// <summary>Die erste Zeile zum Tor. Mit Utf8JsonWriter, nicht JsonSerializer: der Server laeuft ohne Reflection-JSON (JsonSerializerIsReflectionEnabledByDefault=false).</summary>
    internal static byte[] FirstLine(CodexCommand command, CodexSelfTestArgs? selfTest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("cmd", CommandName(command));
            if (command == CodexCommand.SelfTest) { w.WriteString("canary", selfTest!.CanaryPath); w.WriteString("word", selfTest.Word); }
            w.WriteEndObject();
        }
        return [.. buffer.WrittenSpan, (byte)'\n'];
    }

    private sealed class SocketProcess : ICodexProcess
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly FrameStream _output;

        public SocketProcess(Socket socket)
        {
            _socket = socket; _stream = new NetworkStream(socket, ownsSocket: false);
            _output = new FrameStream(_stream);
            StandardInput = new KeepOpenStream(_stream);
        }

        public Stream StandardInput { get; }
        public Stream StandardOutput => _output;
        public Task<int> WaitForExitAsync(CancellationToken ct) => _output.Exit.WaitAsync(ct);

        public void Kill()
        {
            try { _socket.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            _output.End();
        }

        public void Dispose() { Kill(); _stream.Dispose(); _socket.Dispose(); }
    }

    /// <summary>Schreiben zum Tor; Schliessen tut nichts (eine halb geschlossene Verbindung wuerde das Tor als "Programm abbrechen" lesen). Beendet wird mit Kill.</summary>
    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override void Close() { }
        protected override void Dispose(bool disposing) { }
    }
}

/// <summary>
/// Liest die Ausgabe des Tores und trennt die Abschlusszeile ab: nur eine Zeile, die mit dem Satztrenner (0x1E) beginnt UND genau {"exit":N} ist, zaehlt. Alles andere, auch eine
/// Zeile mit Satztrenner, die nicht so aussieht, geht als Daten durch. Das Ende der Verbindung ohne solche Zeile ist Exit -1.
/// </summary>
internal sealed class FrameStream(Stream inner) : Stream
{
    private const byte Separator = 0x1E;
    private const int MaxFrame = 80;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<byte> _ready = new();
    private readonly List<byte> _frame = [];
    private readonly byte[] _raw = new byte[8192];
    private bool _lineStart = true, _collecting, _done;

    public Task<int> Exit => _exit.Task;

    /// <summary>Das Ende ohne Abschlusszeile (Verbindung weg, abgebrochen).</summary>
    public void End() { _done = true; _exit.TrySetResult(-1); }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        while (true)
        {
            if (_ready.Count > 0)
            {
                var n = Math.Min(destination.Length, _ready.Count);
                var span = destination.Span;
                for (var i = 0; i < n; i++) span[i] = _ready.Dequeue();
                return n;
            }
            if (_done) return 0;
            int got;
            try { got = await inner.ReadAsync(_raw.AsMemory(), ct); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { got = 0; }
            if (got == 0)
            {
                foreach (var b in _frame) _ready.Enqueue(b);          // eine angefangene, nie beendete Abschlusszeile ist Daten
                _frame.Clear(); _collecting = false; _done = true; _exit.TrySetResult(-1);
                continue;
            }
            for (var i = 0; i < got && !_done; i++) Feed(_raw[i]);
        }
    }

    private void Feed(byte b)
    {
        if (_collecting)
        {
            _frame.Add(b);
            if (b != (byte)'\n' && _frame.Count <= MaxFrame) return;
            _collecting = false;
            if (b == (byte)'\n' && ParseExit(_frame) is { } code) { _frame.Clear(); _done = true; _exit.TrySetResult(code); return; }
            foreach (var x in _frame) _ready.Enqueue(x);
            _frame.Clear(); _lineStart = b == (byte)'\n';
        }
        else if (_lineStart && b == Separator) { _collecting = true; _frame.Add(b); }
        else { _ready.Enqueue(b); _lineStart = b == (byte)'\n'; }
    }

    private static int? ParseExit(List<byte> frame)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame.Skip(1).ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1) return null;
            return root.TryGetProperty("exit", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var code) ? code : null;
        }
        catch (JsonException) { return null; }
    }
}
