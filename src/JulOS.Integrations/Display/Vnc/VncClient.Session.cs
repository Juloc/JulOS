using System.Buffers.Binary;
using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Vnc;

// What the server sends once the session runs: picture updates, the pointer's shape, a new desktop size, clipboard text.
public sealed partial class VncClient
{
    // Whether the server takes SetDesktopSize (it announced ExtendedDesktopSize) and the screen it described, which a size request refers to.
    private bool _extendedSize;
    private uint _screenId;
    private uint _screenFlags;
    private bool _screenKnown;
    private int _updates;

    /// <summary>Whether the desktop can be given another size (the server speaks ExtendedDesktopSize).</summary>
    public bool CanResize => Volatile.Read(ref _extendedSize);

    /// <summary>How many picture updates (FramebufferUpdate messages) arrived; for tests and diagnosis.</summary>
    public int UpdateCount => Volatile.Read(ref _updates);

    // Reads messages until the server hangs up or the token is cancelled. Runs on a thread of its own: the reads block.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        // A blocking read ends with an exception when the socket is closed
        using var abort = limit.Token.Register(Abort);
        try
        {
            await Task.Factory.StartNew(() => ReadLoop(limit.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // The server hung up - or this side did.
        }
        catch (OperationCanceledException)
        {
        }
        catch (VncProtocolException exception)
        {
            throw new RdpException("The VNC server sent something JulOS does not understand: " + exception.Message, false, exception);
        }
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        var reader = _reader!;
        while (!cancellationToken.IsCancellationRequested)
        {
            switch (reader.U8())
            {
                case 0:
                    ReadUpdate(reader);
                    break;
                case 1:
                    // The colour map: this client asked for true colour, so a server has no reason to send one; read it to stay in step
                    reader.Skip(1);
                    reader.U16();
                    reader.Skip((long)reader.U16() * 6);
                    break;
                case 2:
                    // The bell
                    break;
                case 3:
                    ReadCutText(reader);
                    break;
                case var other:
                    throw new VncProtocolException("a message of type " + other);
            }
        }
    }

    private void ReadUpdate(RfbReader reader)
    {
        reader.Skip(1);
        var count = reader.U16();
        for (var i = 0; i < count; i++)
        {
            var x = reader.U16();
            var y = reader.U16();
            var w = reader.U16();
            var h = reader.U16();
            var encoding = reader.I32();
            switch (encoding)
            {
                case VncDecoders.Raw:
                    VncDecoders.DecodeRaw(reader, _back, x, y, w, h);
                    Commit(x, y, w, h);
                    break;
                case VncDecoders.CopyRect:
                    VncDecoders.DecodeCopyRect(reader, _back, x, y, w, h);
                    Commit(x, y, w, h);
                    break;
                case VncDecoders.Rre:
                    VncDecoders.DecodeRre(reader, _back, x, y, w, h);
                    Commit(x, y, w, h);
                    break;
                case VncDecoders.Hextile:
                    VncDecoders.DecodeHextile(reader, _back, x, y, w, h);
                    Commit(x, y, w, h);
                    break;
                case VncDecoders.Zlib:
                    VncDecoders.DecodeZlib(reader, _back, _zlib, x, y, w, h);
                    Commit(x, y, w, h);
                    break;
                case VncDecoders.Zrle:
                    VncDecoders.DecodeZrle(reader, _back, _zrle, x, y, w, h);
                    Commit(x, y, w, h);
                    break;
                case EncodingCursor:
                    ReadCursor(reader, x, y, w, h);
                    break;
                case EncodingDesktopSize:
                    Resize(w, h);
                    break;
                case EncodingExtendedDesktopSize:
                    ReadExtendedSize(reader, x, y, w, h);
                    break;
                case EncodingLastRect:
                    i = count;
                    break;
                case EncodingExtendedClipboard:
                    break;
                default:
                    throw new VncProtocolException("a rectangle in encoding " + encoding);
            }
        }

        Interlocked.Increment(ref _updates);
        // The next change, as soon as there is one
        RequestUpdate(true);
    }

    // The decoded rectangle goes to the framebuffer the session encodes from, under its lock (a few rows copied: short), then the handler hears of it.
    private void Commit(int x, int y, int w, int h)
    {
        if (w == 0 || h == 0)
        {
            return;
        }

        lock (FrameLock)
        {
            var stride = Width * 4;
            for (var row = 0; row < h; row++)
            {
                _back.Pixels.AsSpan((y + row) * stride + x * 4, w * 4).CopyTo(Framebuffer.AsSpan((y + row) * stride + x * 4));
            }
        }

        _handler.Updated(x, y, w, h);
    }

    // The pointer's shape: x and y are the hot spot; the pixels follow (our format), then a bit mask (a set bit: the pixel counts), rows padded to whole bytes.
    private void ReadCursor(RfbReader reader, int hotX, int hotY, int w, int h)
    {
        if (w == 0 || h == 0)
        {
            _handler.CursorHidden();
            return;
        }

        if (w > 1024 || h > 1024)
        {
            throw new VncProtocolException($"a pointer of {w} x {h}");
        }

        var pixels = reader.Bytes(w * h * 4);
        var maskStride = (w + 7) / 8;
        var mask = reader.Bytes(maskStride * h);
        var rgba = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            for (var column = 0; column < w; column++)
            {
                var from = (row * w + column) * 4;
                rgba[from] = pixels[from + 2];
                rgba[from + 1] = pixels[from + 1];
                rgba[from + 2] = pixels[from];
                rgba[from + 3] = (mask[row * maskStride + column / 8] & (0x80 >> (column % 8))) != 0 ? (byte)255 : (byte)0;
            }
        }

        _handler.Cursor(Math.Min(hotX, w - 1), Math.Min(hotY, h - 1), w, h, rgba);
    }

    // ExtendedDesktopSize: x is why (0 the server, 1 this client asked, 2 another client), y the outcome of a request (0 fine, else refused);
    // then the list of screens. With a refusal the size stays as it was.
    private void ReadExtendedSize(RfbReader reader, int reason, int status, int w, int h)
    {
        var screens = reader.U8();
        reader.Skip(3);
        for (var i = 0; i < screens; i++)
        {
            var id = reader.U32();
            reader.Skip(8);
            var flags = reader.U32();
            if (i == 0)
            {
                _screenId = id;
                _screenFlags = flags;
                _screenKnown = true;
            }
        }

        Volatile.Write(ref _extendedSize, true);
        if (reason == 1 && status != 0)
        {
            _log?.Invoke("VNC: the server refused the desktop size (code " + status + ")");
            return;
        }

        Resize(w, h);
    }

    // A new desktop size: new buffers, the handler is told, and the whole picture is asked for again.
    private void Resize(int w, int h)
    {
        if (!SizeOk(w, h))
        {
            throw new VncProtocolException($"a desktop of {w} x {h}");
        }

        if (w == Width && h == Height)
        {
            return;
        }

        Allocate(w, h);
        _handler.Resized(w, h);
        RequestUpdate(false);
    }

    /// <summary>Asks the server for another desktop size (ExtendedDesktopSize). The answer comes as an update; nothing happens when the server cannot.</summary>
    public void RequestSize(int width, int height)
    {
        if (!CanResize || !SizeOk(width, height))
        {
            return;
        }

        var message = new byte[8 + 16];
        message[0] = 251;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)height);
        message[6] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), _screenKnown ? _screenId : 0);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(16), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(18), (ushort)height);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(20), _screenKnown ? _screenFlags : 0);
        Send(message);
    }
}
