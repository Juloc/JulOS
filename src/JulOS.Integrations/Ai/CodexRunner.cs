using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;

namespace JulOS.Integrations.Ai;

/// <summary>Was die Bruecke vom Codex-Programm will. Immer eine feste Auswahl, nie ein freier Text: ueber den Socket zum eigenen Container geht nur der Name des Befehls.</summary>
public enum CodexCommand { AppServer, Login, LoginStatus, Logout, Version, SelfTest }

/// <summary>Die Angaben des Selbsttests: der Pfad der Kanarien-Datei, wie JulOS ihn sieht, und das Wort darin.</summary>
public sealed record CodexSelfTestArgs(string CanaryPath, string Word);

/// <summary>Ein laufender Aufruf des Programms. Ein- und Ausgabe sind Byte-Stroeme (UTF-8, Zeilen); bei Login, LoginStatus und Logout ist die Fehlerausgabe in die Ausgabe gemischt.</summary>
public interface ICodexProcess : IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Task<int> WaitForExitAsync(CancellationToken ct);
    void Kill();
}

/// <summary>Wie das Programm gestartet wird: im selben Container (LocalRunner) oder ueber den Socket im eigenen Container (SocketRunner).</summary>
public interface ICodexRunner
{
    /// <summary>true, wenn das Programm in einem eigenen Container laeuft (Socket): dann laedt JulOS nichts und die Selbstpruefung ersetzt die Pruefung von Hand.</summary>
    bool Isolated { get; }
    /// <summary>Installiert (lokal) oder erreichbar (Socket), Stand des letzten <see cref="RefreshAsync"/>.</summary>
    bool Available { get; }
    string? Version { get; }
    /// <summary>Der Arbeitsordner des Programms, so wie das Programm ihn sieht.</summary>
    string WorkDirectory { get; }
    /// <summary>Fragt nach (Socket: Version), hoechstens alle paar Sekunden wirklich; lokal nichts.</summary>
    Task RefreshAsync(CancellationToken ct);
    /// <summary>Startet den Befehl. Wirft <see cref="AiProviderException"/> (not_installed, provider_unavailable), wenn das nicht geht.</summary>
    ICodexProcess Start(CodexCommand command, CodexSelfTestArgs? selfTest = null);
}

/// <summary>Das Programm im selben Container: die geprueft abgelegte Datei, ohne Shell, mit leerer Umgebung und eigenem Arbeitsordner (wie bisher).</summary>
public sealed class LocalRunner(CodexInstaller installer, string? executable = null) : ICodexRunner
{
    private string? Executable => executable ?? installer.VerifiedPath();

    public bool Isolated => false;
    public bool Available => Executable is not null;
    public string? Version => Available ? CodexRelease.PinnedVersion : null;
    public string WorkDirectory => installer.Work;
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;

    public ICodexProcess Start(CodexCommand command, CodexSelfTestArgs? selfTest = null)
    {
        if (command == CodexCommand.SelfTest) throw new NotSupportedException("Der Selbsttest gibt es nur mit dem eigenen Container.");
        var exe = Executable ?? throw new AiProviderException("not_installed", "Das Codex-Programm ist noch nicht geladen.");
        string[] args = command switch
        {
            CodexCommand.AppServer => ["app-server"],
            CodexCommand.Login => ["login", "--device-auth"],
            CodexCommand.LoginStatus => ["login", "status"],
            CodexCommand.Logout => ["logout"],
            CodexCommand.Version => ["--version"],
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        CodexInstaller.CreatePrivate(installer.Home); CodexInstaller.CreatePrivate(installer.Work);
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = installer.Work, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        // Eine leere Umgebung mit nur dem Noetigen (nichts von JulOS)
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        start.Environment["HOME"] = installer.Home;
        start.Environment["CODEX_HOME"] = installer.Home;
        start.Environment["NO_COLOR"] = "1";
        start.Environment["LANG"] = "C.UTF-8";
        try { return new LocalProcess(Process.Start(start) ?? throw new Win32Exception(), mergeErrors: command != CodexCommand.AppServer); }
        catch (Win32Exception) { throw new AiProviderException("provider_unavailable", "Das Codex-Programm lässt sich nicht starten."); }
    }

    private sealed class LocalProcess : ICodexProcess
    {
        private readonly Process _process;
        private readonly Stream _output;

        public LocalProcess(Process process, bool mergeErrors)
        {
            _process = process;
            if (!mergeErrors)
            {
                // Die Fehlerausgabe von app-server kann das Konto nennen: sie wird gelesen und verworfen
                _ = Task.Run(async () => { var sink = new byte[4096]; try { while (await process.StandardError.BaseStream.ReadAsync(sink) > 0) { } } catch (Exception e) when (e is IOException or ObjectDisposedException) { } });
                _output = process.StandardOutput.BaseStream;
                return;
            }
            var merged = Channel.CreateUnbounded<byte[]>();
            async Task Pump(Stream from)
            {
                var buffer = new byte[4096];
                try { int n; while ((n = await from.ReadAsync(buffer)) > 0) merged.Writer.TryWrite(buffer.AsSpan(0, n).ToArray()); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }
            _ = Task.WhenAll(Pump(process.StandardOutput.BaseStream), Pump(process.StandardError.BaseStream)).ContinueWith(_ => merged.Writer.TryComplete(), TaskScheduler.Default);
            _output = new ChannelReadStream(merged.Reader);
        }

        public Stream StandardInput => _process.StandardInput.BaseStream;
        public Stream StandardOutput => _output;
        public async Task<int> WaitForExitAsync(CancellationToken ct) { await _process.WaitForExitAsync(ct); return _process.ExitCode; }

        public void Kill()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or ObjectDisposedException) { }
        }

        public void Dispose() { Kill(); _process.Dispose(); }
    }
}

/// <summary>Ein Lese-Strom ueber eine Warteschlange von Stuecken (zwei Quellen, eine Ausgabe).</summary>
internal sealed class ChannelReadStream(ChannelReader<byte[]> reader) : Stream
{
    private byte[] _current = [];
    private int _pos;

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

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_pos >= _current.Length)
        {
            if (!await reader.WaitToReadAsync(ct)) return 0;
            if (reader.TryRead(out var next)) { _current = next; _pos = 0; }
        }
        var n = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsMemory(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }
}
