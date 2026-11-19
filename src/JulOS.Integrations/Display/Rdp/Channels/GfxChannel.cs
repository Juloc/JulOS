namespace JulOS.Integrations.Display.Rdp.Channels;

// One H.264 picture of the desktop, as the server coded it: Annex B, with the parameter sets in front of a key frame.
public sealed record GfxPicture(byte[] Data, bool IsKey);

// What the server sent between its "start frame" and "end frame": the pictures to show, in order. Each frame has to
// be answered (Acknowledge) or the server stops sending after a while.
public sealed record GfxFrame(uint FrameId, IReadOnlyList<GfxPicture> Pictures);

// The graphics pipeline extension (MS-RDPEGFX), reduced to what it takes to hand the server's own H.264 pictures
// on without coding them again: the channel is negotiated for H.264 (AVC420) alone, and the one surface that is the
// desktop delivers its pictures as they come. Whatever would need more than that - another codec, drawing into the
// surface, a second surface, compressed messages - is reported with Unusable, once, and the owner leaves the
// pipeline (for a session without it). The channel never decodes a picture.
//
// Like every channel this is a state machine over bytes whose input is the remote server's: Receive drops and
// reports what does not hold together and never throws on it.
//
// Receive and the events belong to the core's thread (the events must return quickly); Acknowledge may be called
// from any thread.
public sealed class GfxChannel : DynamicChannel
{
    public const string ChannelName = "Microsoft::Windows::RDS::Graphics";

    // The most pictures of one frame this client keeps; a server sends one, a few more mean a mixed-up stream.
    private const int MaxPicturesPerFrame = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<ushort, (int Width, int Height)> _surfaces = [];
    private List<GfxPicture>? _pictures;
    private uint _frameId;
    private int _output = -1;
    private uint _decoded;
    private bool _confirmed;
    private bool _unusable;
    private string? _version;

    public override string Name => ChannelName;

    // The server opened the channel and confirmed a set of capabilities with H.264.
    public bool IsConfirmed
    {
        get
        {
            lock (_gate)
            {
                return _confirmed;
            }
        }
    }

    // True once the server said the pipeline cannot be used; nothing is passed on after that.
    public bool IsUnusable
    {
        get
        {
            lock (_gate)
            {
                return _unusable;
            }
        }
    }

    // What the server picked, for the log ("8.1, flags 0x10"); null before it did.
    public string? Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    // The desktop has this size (the surface that is shown): raised when it is mapped and whenever it changes.
    public event Action<int, int>? DesktopSize;

    // The pictures of one frame, at its end. Frames without a picture of the desktop are answered by the channel.
    public event Action<GfxFrame>? Frame;

    // The pipeline cannot be used (see the class comment); the text says why. Raised at most once.
    public event Action<string>? Unusable;

    public override void Opened()
    {
        lock (_gate)
        {
            _surfaces.Clear();
            _pictures = null;
            _output = -1;
            _decoded = 0;
            _confirmed = false;
        }

        // The client starts the conversation: this is what it can take.
        Send?.Invoke(GfxPdu.BuildCapsAdvertise());
    }

    public override void Closed()
    {
        lock (_gate)
        {
            _surfaces.Clear();
            _pictures = null;
            _output = -1;
            _confirmed = false;
        }
    }

    // Tells the server that this frame is on the screen (the browser has drawn it): that is what lets it send the next.
    public void Acknowledge(uint frameId)
    {
        uint decoded;
        lock (_gate)
        {
            decoded = ++_decoded;
        }

        try
        {
            Send?.Invoke(GfxPdu.BuildFrameAcknowledge(frameId, decoded));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report("sending an acknowledgement failed: " + exception.Message);
        }
    }

    public override void Receive(ReadOnlySpan<byte> message)
    {
        if (IsUnusable)
        {
            return;
        }

        if (!GfxPdu.TryUnwrap(message, out var payload, out var problem))
        {
            GiveUp("the server sent " + problem);
            return;
        }

        var reader = new ByteReader(payload);
        while (reader.Remaining > 0)
        {
            if (!GfxPdu.TryNext(ref reader, out var command, out var body))
            {
                Report("dropped the end of a message that is cut short");
                return;
            }

            Handle(command, body);
            if (IsUnusable)
            {
                return;
            }
        }
    }

    private void Handle(ushort command, ReadOnlySpan<byte> body)
    {
        switch (command)
        {
            case GfxPdu.CapsConfirm:
                CapsConfirmed(body);
                break;
            case GfxPdu.ResetGraphics:
                Reset(body);
                break;
            case GfxPdu.CreateSurface:
                if (GfxPdu.TryCreateSurface(body, out var id, out var width, out var height))
                {
                    lock (_gate)
                    {
                        _surfaces[id] = (width, height);
                    }
                }
                else
                {
                    Report("dropped a surface without size");
                }

                break;
            case GfxPdu.DeleteSurface:
                if (GfxPdu.TryDeleteSurface(body, out var gone))
                {
                    lock (_gate)
                    {
                        _surfaces.Remove(gone);
                        if (_output == gone)
                        {
                            _output = -1;
                        }
                    }
                }

                break;
            case GfxPdu.MapSurfaceToOutput:
                Map(body);
                break;
            case GfxPdu.StartFrame:
                if (GfxPdu.TryStartFrame(body, out var frameId))
                {
                    lock (_gate)
                    {
                        _frameId = frameId;
                        _pictures = [];
                    }
                }

                break;
            case GfxPdu.WireToSurface1:
                Wire(body);
                break;
            case GfxPdu.EndFrame:
                EndOfFrame(body);
                break;
            case GfxPdu.SolidFill:
            case GfxPdu.SurfaceToSurface:
            case GfxPdu.CacheToSurface:
            case GfxPdu.WireToSurface2:
            case GfxPdu.MapSurfaceToScaledOutput:
                // Painting into the surface (or a picture the other way of coding it): the desktop is no longer
                // just the server's H.264 stream, and passing that on would show a wrong picture.
                GiveUp($"the server paints with command 0x{command:X4}, which is more than its H.264 pictures");
                break;
            default:
                // The cache, the offer of a cache import, windows, quality reports: nothing a picture needs.
                break;
        }
    }

    private void CapsConfirmed(ReadOnlySpan<byte> body)
    {
        if (!GfxPdu.TryCapsConfirm(body, out var version, out var flags))
        {
            GiveUp("the server's capabilities are cut short");
            return;
        }

        var text = $"{GfxPdu.VersionName(version)}, flags 0x{flags:X}";
        if (!GfxPdu.AvcEnabled(version, flags))
        {
            GiveUp($"the server does not use H.264 (it chose {text})");
            return;
        }

        lock (_gate)
        {
            _confirmed = true;
            _version = text;
        }

        Report("the server chose version " + text);
    }

    private void Reset(ReadOnlySpan<byte> body)
    {
        if (!GfxPdu.TryResetGraphics(body, out var width, out var height))
        {
            Report("dropped a reset without a usable size");
            return;
        }

        // Everything that was drawn is gone; the server creates the surfaces again.
        lock (_gate)
        {
            _surfaces.Clear();
            _output = -1;
            _pictures = null;
        }

        Report($"desktop reset to {width} x {height}");
    }

    private void Map(ReadOnlySpan<byte> body)
    {
        if (!GfxPdu.TryMapSurface(body, out var id, out var x, out var y))
        {
            Report("dropped a surface mapping that is cut short");
            return;
        }

        (int Width, int Height) size;
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(id, out size))
            {
                Report($"dropped the mapping of surface {id}, which does not exist");
                return;
            }
        }

        if (x != 0 || y != 0)
        {
            GiveUp($"a surface sits at {x},{y} on the desktop: more than one picture would have to be put together");
            return;
        }

        lock (_gate)
        {
            _output = id;
        }

        DesktopSizeChanged(size.Width, size.Height);
    }

    private void DesktopSizeChanged(int width, int height)
    {
        var handler = DesktopSize;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(width, height);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report("the desktop size handler failed: " + exception.Message);
        }
    }

    private void Wire(ReadOnlySpan<byte> body)
    {
        if (!GfxPdu.TryWireToSurface1(body, out var surface, out var codec, out var data))
        {
            Report("dropped a picture that is cut short");
            return;
        }

        if (codec != GfxPdu.CodecAvc420)
        {
            GiveUp($"the server codes pictures with codec 0x{codec:X4} (only H.264 AVC420 is passed on)");
            return;
        }

        if (!GfxPdu.TryAvc420(data, out var bitstream))
        {
            Report("dropped an H.264 picture without a bitstream");
            return;
        }

        lock (_gate)
        {
            if (surface != _output)
            {
                // Pictures into a surface that is not the desktop are put on it later by copying - not something to pass on.
                GiveUp($"the server sends H.264 for surface {surface}, which is not the desktop");
                return;
            }

            if (_pictures is null)
            {
                Report("dropped a picture outside of a frame");
                return;
            }

            if (_pictures.Count >= MaxPicturesPerFrame)
            {
                Report("dropped a picture: too many in one frame");
                return;
            }

            _pictures.Add(new GfxPicture(bitstream.ToArray(), GfxPdu.HasIdr(bitstream)));
        }
    }

    private void EndOfFrame(ReadOnlySpan<byte> body)
    {
        if (!GfxPdu.TryEndFrame(body, out var frameId))
        {
            Report("dropped the end of a frame without a number");
            return;
        }

        List<GfxPicture>? pictures;
        lock (_gate)
        {
            pictures = frameId == _frameId ? _pictures : null;
            _pictures = null;
        }

        if (pictures is not { Count: > 0 })
        {
            // Nothing for the screen in it (a frame of other surfaces, or an empty one): done at once.
            Acknowledge(frameId);
            return;
        }

        var handler = Frame;
        if (handler is null)
        {
            Acknowledge(frameId);
            return;
        }

        try
        {
            handler(new GfxFrame(frameId, pictures));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report("the frame handler failed: " + exception.Message);
            Acknowledge(frameId);
        }
    }

    private void GiveUp(string reason)
    {
        lock (_gate)
        {
            if (_unusable)
            {
                return;
            }

            _unusable = true;
        }

        Report("not usable: " + reason);
        try
        {
            Unusable?.Invoke(reason);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report("the handler of the pipeline's failure failed: " + exception.Message);
        }
    }
}
