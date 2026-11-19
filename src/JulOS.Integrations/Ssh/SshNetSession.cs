using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JulOS.Integrations.Ssh;

/// <summary>
/// <see cref="ISshSession"/> mit SSH.NET (MIT). Passwort und "keyboard-interactive" (viele Server mit PAM fragen so), Host-Schluessel ueber
/// den Rueckruf. Die Shell wird in einem eigenen Thread gelesen (ShellStream blockiert) und ueber einen begrenzten Kanal weitergereicht:
/// ist der Browser langsam, fuellt sich der Kanal, der Thread wartet, und das SSH-Fenster bremst das Ziel.
/// </summary>
public sealed class SshNetSession : ISshSession
{
    private const int ChunkSize = 16 * 1024;

    private SshClient? _client;
    private ShellStream? _shell;
    private Channel<byte[]>? _output;
    private byte[]? _pending;
    private int _pendingOffset;
    private int _cols, _rows;
    private bool _hostKeyRejected;
    private readonly CancellationTokenSource _stop = new();                 // beendet den Lese-Thread, auch wenn niemand mehr aus dem Kanal liest

    public SshHostKey? PresentedHostKey { get; private set; }

    public async Task ConnectAsync(SshConnectOptions o, Func<SshHostKey, bool> trustHostKey, CancellationToken ct)
    {
        var password = o.Password;
        var plain = new PasswordAuthenticationMethod(o.User, password);
        var interactive = new KeyboardInteractiveAuthenticationMethod(o.User);
        // Jede Abfrage des Ziels wird mit dem Passwort beantwortet (nur Abfragen, die nach einem Passwort aussehen: sonst bliebe die Antwort leer)
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var p in e.Prompts) p.Response = p.Request.Contains("assword", StringComparison.OrdinalIgnoreCase) ? password : "";
        };
        var info = new ConnectionInfo(o.Host, o.Port, o.User, plain, interactive) { Timeout = o.ConnectTimeout, MaxSessions = 1 };
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        client.HostKeyReceived += (_, e) =>
        {
            var key = new SshHostKey(e.HostKeyName, Fingerprint(e.HostKey));
            PresentedHostKey = key;
            e.CanTrust = trustHostKey(key);
            _hostKeyRejected = !e.CanTrust;
        };
        _client = client;
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            _cols = o.Cols; _rows = o.Rows;
            // Terminalart wie ein gewoehnliches Terminal; die Groesse in Zeichen, Pixel unbekannt (0)
            _shell = client.CreateShellStream("xterm-256color", (uint)o.Cols, (uint)o.Rows, 0, 0, ChunkSize);
        }
        catch (Exception e)
        {
            throw Translate(e, ct);
        }

        _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var shell = _shell;
        var output = _output;
        _ = Task.Factory.StartNew(() => PumpShell(shell, output, _stop.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>SHA-256 des Schluessel-Blobs, Base64 ohne Auffuellung, wie bei "ssh-keygen -l".</summary>
    internal static string Fingerprint(byte[] hostKey) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    private static void PumpShell(ShellStream shell, Channel<byte[]> output, CancellationToken stop)
    {
        var buffer = new byte[ChunkSize];
        try
        {
            while (true)
            {
                var n = shell.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                // ShellStream.Read liefert 0 erst, wenn der Kanal zu ist; Warten ist hier gewollt (Gegendruck)
                output.Writer.WriteAsync(buffer.AsSpan(0, n).ToArray(), stop).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception)
        {
            // Verbindung weg oder Sitzung freigegeben: das Ende ist das Ergebnis
        }
        finally
        {
            output.Writer.TryComplete();
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var output = _output ?? throw new InvalidOperationException("Nicht verbunden.");
        if (_pending is null)
        {
            if (!await output.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
            if (!output.Reader.TryRead(out _pending)) return 0;
            _pendingOffset = 0;
        }

        var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, n).CopyTo(buffer);
        _pendingOffset += n;
        if (_pendingOffset >= _pending.Length) _pending = null;
        return n;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var shell = _shell ?? throw new InvalidOperationException("Nicht verbunden.");
        await shell.WriteAsync(data, ct).ConfigureAwait(false);
        await shell.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Resize(int cols, int rows)
    {
        if (_shell is null || (cols == _cols && rows == _rows)) return;
        _cols = cols; _rows = rows;
        _shell.ChangeWindowSize((uint)cols, (uint)rows, 0, 0);
    }

    private SshException Translate(Exception e, CancellationToken ct)
    {
        if (e is SshException ours) return ours;
        // Abbruch durch den Aufrufer (Browser weg) ist kein Verbindungsfehler: weiterreichen
        if (e is OperationCanceledException && ct.IsCancellationRequested) throw e;
        if (_hostKeyRejected) return new SshException(SshFailure.HostKeyRejected, "Host-Schlüssel nicht vertraut.", e);
        return e switch
        {
            SshAuthenticationException => new SshException(SshFailure.LogonFailed, "Anmeldung abgelehnt.", e),
            SshOperationTimeoutException or TimeoutException => new SshException(SshFailure.Timeout, "Zeitüberschreitung beim Verbinden.", e),
            SocketException => new SshException(SshFailure.Unreachable, "Das Ziel ist nicht erreichbar.", e),
            _ when e.InnerException is SocketException => new SshException(SshFailure.Unreachable, "Das Ziel ist nicht erreichbar.", e),
            _ => new SshException(SshFailure.Failed, "Die Verbindung zum Ziel ist fehlgeschlagen.", e),
        };
    }

    public ValueTask DisposeAsync()
    {
        // Erst den Lese-Thread aus dem Warten auf den Kanal holen, dann die Shell und den Client schliessen
        try { _stop.Cancel(); } catch (ObjectDisposedException) { /* schon weg */ }
        try { _shell?.Dispose(); } catch (Exception) { /* schon weg */ }
        try { _client?.Dispose(); } catch (Exception) { /* schon weg */ }
        return ValueTask.CompletedTask;
    }
}
