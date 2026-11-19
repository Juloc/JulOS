using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;
using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Vnc;

// A VNC client (RFB 3.3, 3.7 and 3.8, RFC 6143): it connects to a remote desktop, keeps the picture in a framebuffer, and passes on
// pointer, keys and the clipboard. The picture goes into the same pipeline as the RDP client's (BGRX framebuffer, rectangles reported to
// an IScreenHandler), so everything behind it - encoder, refinement, browser protocol - does not know which of the two it is showing.
//
// One thread reads (RunAsync: blocking reads; the messages have no length in front, so decoders pull what they need as they parse);
// whatever goes to the server - input, requests - is queued and written by one task, so nobody who calls in ever waits for the network.
// Errors are reported as RdpException, like the RDP client's, so the relay treats both alike.
public sealed partial class VncClient : IScreenSource
{
    private readonly VncOptions _options;
    private readonly IScreenHandler _handler;
    private readonly Action<string>? _log;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closing = new();
    private readonly VncDecoders.Inflation _zrle = new();
    private readonly VncDecoders.Inflation _zlib = new();

    private TcpClient? _tcp;
    private Stream? _stream;
    private RfbReader? _reader;
    private Task? _writer;
    private RfbCanvas _back = new(0, 0);
    private int _disposed;
    private bool _certificateRefused;
    private bool _certificateDeclined;
    private string? _presentedFingerprint;

    public VncClient(VncOptions options, IScreenHandler handler, Action<string>? log = null)
    {
        _options = options;
        _handler = handler;
        _log = log;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    // BGRX: 4 bytes a pixel, B first, top-down, stride = Width * 4. Read it only while holding FrameLock; it is replaced when the desktop gets another size.
    public byte[] Framebuffer { get; private set; } = [];

    public object FrameLock { get; } = new();

    public string? EndReason { get; private set; }

    // The name the server gives its desktop (often the host and display), shown nowhere yet but useful in logs.
    public string DesktopName { get; private set; } = "";

    // The version of the protocol the two settled on, "3.8" and so on.
    public string ProtocolVersion { get; private set; } = "";

    // The security type that was used (1 none, 2 VNC password, 19 VeNCrypt) and for VeNCrypt the sub-type.
    public int SecurityType { get; private set; }

    public int SecuritySubType { get; private set; }

    // Called on the read loop with text the remote side copied.
    public Action<string>? ClipboardReceived { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outgoing.Writer.TryComplete();
        if (_writer is not null)
        {
            // What is queued gets a moment to leave.
            await Task.WhenAny(_writer, Task.Delay(300));
        }

        _closing.Cancel();
        Abort();
        if (_writer is not null)
        {
            try
            {
                await _writer;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
        }

        _zrle.Dispose();
        _zlib.Dispose();
        _closing.Dispose();
    }

    // VNC has no goodbye message: closing the connection is it.
    public void Disconnect() => _outgoing.Writer.TryComplete();

    // Closes the sockets, which ends blocking reads and writes with an exception.
    private void Abort()
    {
        try
        {
            _tcp?.Client?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }

        try
        {
            _stream?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        _tcp?.Dispose();
    }

    private void Send(byte[] message) => _outgoing.Writer.TryWrite(message);

    private async Task WriteLoopAsync()
    {
        var stream = _stream!;
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_closing.Token))
            {
                await stream.WriteAsync(message, _closing.Token);
                if (!_outgoing.Reader.TryPeek(out _))
                {
                    await stream.FlushAsync(_closing.Token);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    // What an exception means for the person who sees it.
    private RdpException Explain(Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return new RdpException("The connection was cancelled.", false, exception);
            case OperationCanceledException:
                return new RdpException("The VNC server did not answer in time.", false, exception);
            case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                return new RdpException("The host name could not be resolved.", false, exception);
            case SocketException:
                return new RdpException("The VNC server could not be reached.", false, exception);
            case AuthenticationException when _certificateDeclined:
                return new RdpException("The VNC server's certificate is not the one that was trusted.", false, exception) { CertificateFingerprint = _presentedFingerprint };
            case AuthenticationException:
                return new RdpException(
                    _certificateRefused
                        ? "The VNC server's certificate is not trusted. Allow it in the connection's settings (ignore certificate) if this server is yours."
                        : "The secure connection to the VNC server failed (certificate or TLS settings).",
                    false,
                    exception);
            case EndOfStreamException or IOException or ObjectDisposedException:
                return new RdpException(EndReason ?? "The VNC server closed the connection.", false, exception);
            case VncProtocolException:
                return new RdpException("The VNC server sent something JulOS does not understand: " + exception.Message, false, exception);
            default:
                return new RdpException("The connection to the VNC server failed: " + exception.Message, false, exception);
        }
    }
}
