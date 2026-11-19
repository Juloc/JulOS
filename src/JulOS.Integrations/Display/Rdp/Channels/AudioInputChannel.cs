using System.Buffers.Binary;

namespace JulOS.Integrations.Display.Rdp.Channels;

// The microphone (MS-RDPEAI), over the dynamic channel "AUDIO_INPUT". The server opens it, agrees on a version
// and a list of formats with the client - here plain 16-bit PCM and nothing else, so no encoder is needed - and
// asks the client to start recording (Open). From then on the client sends what it captured in packets of the
// size the server asked for, each announced by an Incoming Data message. The channel ends when the dynamic
// channel is closed; there is no message to stop in between.
//
// Receive runs on the core's thread. SendSamples may be called from any thread.
public sealed class AudioInputChannel : DynamicChannel
{
    public const string ChannelName = "AUDIO_INPUT";

    // The highest version this client answers with. Version 2 only adds the Format Change messages, which are
    // understood.
    public const uint ClientVersion = 2;

    // The client's list is at most this long, and a format is named by its place in it.
    public const int MaxFormats = 32;

    // A packet of more than this many frames is not sent as the server asked: it is cut into smaller ones.
    public const int MaxFramesPerPacket = 48000;

    private const byte TypeVersion = 0x01;
    private const byte TypeFormats = 0x02;
    private const byte TypeOpen = 0x03;
    private const byte TypeOpenReply = 0x04;
    private const byte TypeDataIncoming = 0x05;
    private const byte TypeData = 0x06;
    private const byte TypeFormatChange = 0x07;

    private const int FormatLength = 18;
    private const int MaxFormatExtra = 64;
    private const ushort Pcm = 0x0001;
    private const uint Ok = 0;
    private const uint Failed = 0x80004005;

    private readonly Lock _gate = new();
    private readonly List<AudioFormat> _formats = [];
    private readonly List<byte> _pending = [];
    private AudioFormat? _current;
    private int _frames;
    private bool _open;
    private bool _channelOpen;

    // What a format of the list says about the sound.
    public readonly record struct AudioFormat(int Rate, int Channels)
    {
        public int FrameBytes => Channels * 2;
    }

    public override string Name => ChannelName;

    // The server opened the channel and agreed on formats, or ended that: the microphone can be offered (or not).
    public event Action? StateChanged;

    // The server asks for sound from the microphone in this format (raised again on a format change), or no longer.
    public event Action<AudioFormat?>? Recording;

    // The channel is open and the server knows which formats this client takes.
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _channelOpen && _formats.Count > 0;
            }
        }
    }

    // The server wants sound now.
    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    // The format the server wants, or - before it asked - the one this client lists first, which is the one a
    // server takes if it takes any (null while there is no list).
    public AudioFormat? Format
    {
        get
        {
            lock (_gate)
            {
                return _current ?? (_formats.Count > 0 ? _formats[0] : null);
            }
        }
    }

    // Hands over sound captured by the microphone in the format the server wants (16-bit little-endian PCM, whole
    // frames or not: a rest is kept for the next call). It goes out in packets of the size the server asked for.
    // False when the server has not asked for sound; nothing is kept then.
    public bool SendSamples(ReadOnlySpan<byte> pcm)
    {
        var send = Send;
        List<byte[]> packets = [];
        lock (_gate)
        {
            if (!_open || _current is not { } format || send is null)
            {
                return false;
            }

            _pending.AddRange(pcm);
            var packet = Math.Min(_frames, MaxFramesPerPacket) * format.FrameBytes;
            while (_pending.Count >= packet)
            {
                var data = new byte[1 + packet];
                data[0] = TypeData;
                _pending.CopyTo(0, data, 1, packet);
                _pending.RemoveRange(0, packet);
                packets.Add(data);
            }
        }

        // Each packet is announced by a message of its own (MS-RDPEAI 3.2.5.2.1).
        foreach (var data in packets)
        {
            if (!Transmit(send, [TypeDataIncoming]) || !Transmit(send, data))
            {
                return false;
            }
        }

        return true;
    }

    public override void Opened()
    {
        lock (_gate)
        {
            _channelOpen = true;
        }
    }

    public override void Closed()
    {
        bool wasRecording;
        lock (_gate)
        {
            wasRecording = _open;
            _channelOpen = false;
            _open = false;
            _current = null;
            _formats.Clear();
            _pending.Clear();
        }

        if (wasRecording)
        {
            Guarded.Raise(Recording, null, Log, ChannelName + ": the handler of Recording");
        }

        Guarded.Raise(StateChanged, Log, ChannelName + ": the handler of StateChanged");
    }

    public override void Receive(ReadOnlySpan<byte> message)
    {
        if (message.Length < 1)
        {
            Report("dropped an empty message");
            return;
        }

        var body = message[1..];
        switch (message[0])
        {
            case TypeVersion:
                Version(body);
                break;
            case TypeFormats:
                ServerFormats(body);
                break;
            case TypeOpen:
                Open(body);
                break;
            case TypeFormatChange:
                FormatChange(body);
                break;
            default:
                Report($"ignored message {message[0]}, which this client does not know");
                break;
        }
    }

    // The two sides tell each other the version they speak; the lower one holds.
    private void Version(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
        {
            Report("dropped a version that is too short");
            return;
        }

        var server = BinaryPrimitives.ReadUInt32LittleEndian(body);
        Transmit(new ByteWriter(5).U8(TypeVersion).U32(Math.Clamp(server, 1, ClientVersion)).ToArray());
    }

    // The server lists what it can take; the answer is the part of that this client does: 16-bit PCM, best
    // first (see Rank). The server finds a format by its place in this answer.
    private void ServerFormats(ReadOnlySpan<byte> body)
    {
        var reader = new ByteReader(body);
        if (!reader.TryU32(out var count) || !reader.TrySkip(4))
        {
            Report("dropped formats too short for their fixed part");
            return;
        }

        var offered = new List<(AudioFormat Format, byte[] Wire)>();
        for (var i = 0u; i < count; i++)
        {
            var start = reader.Rest;
            if (!reader.TryU16(out var tag) || !reader.TryU16(out var channels) || !reader.TryU32(out var rate)
                || !reader.TrySkip(4) || !reader.TryU16(out var align) || !reader.TryU16(out var bits)
                || !reader.TryU16(out var extra) || !reader.TrySkip(extra))
            {
                Report("dropped formats: the list is shorter than it says");
                return;
            }

            if (tag != Pcm || bits != 16 || channels is < 1 or > 2 || rate is < 8000 or > 96000 || align != channels * 2
                || extra > MaxFormatExtra || offered.Count >= MaxFormats)
            {
                continue;
            }

            var format = new AudioFormat((int)rate, channels);
            if (offered.All(known => known.Format != format))
            {
                offered.Add((format, start[..(FormatLength + extra)].ToArray()));
            }
        }

        offered.Sort((a, b) => Rank(a.Format).CompareTo(Rank(b.Format)));
        lock (_gate)
        {
            _formats.Clear();
            _formats.AddRange(offered.Select(entry => entry.Format));
            _current = null;
            _open = false;
            _pending.Clear();
        }

        // The size counts everything but extra data, which there is none of (MS-RDPEAI 2.2.2.2).
        var length = 9 + offered.Sum(entry => entry.Wire.Length);
        var answer = new ByteWriter(length).U8(TypeFormats).U32((uint)offered.Count).U32((uint)length);
        foreach (var entry in offered)
        {
            answer.Bytes(entry.Wire);
        }

        Transmit(answer.ToArray());
        if (offered.Count == 0)
        {
            Report("the server offers no 16-bit PCM: there will be no microphone");
        }

        Guarded.Raise(StateChanged, Log, ChannelName + ": the handler of StateChanged");
    }

    // The server asks the client to start recording: how many frames go into a packet, the format of the list
    // to encode in, and the format of the device, which is of no interest here.
    private void Open(ReadOnlySpan<byte> body)
    {
        var reader = new ByteReader(body);
        if (!reader.TryU32(out var frames) || !reader.TryU32(out var index))
        {
            Report("dropped an open that is too short");
            Reply(Failed);
            return;
        }

        AudioFormat format;
        lock (_gate)
        {
            if (index >= _formats.Count || frames == 0)
            {
                format = default;
            }
            else
            {
                format = _formats[(int)index];
                _current = format;
                _frames = (int)Math.Min(frames, MaxFramesPerPacket);
                _open = true;
                _pending.Clear();
            }
        }

        if (format == default)
        {
            Report($"refused an open with format {index} and {frames} frames per packet");
            Reply(Failed);
            return;
        }

        Reply(Ok);
        Guarded.Raise(Recording, format, Log, ChannelName + ": the handler of Recording");
    }

    // The server switches to another format of the list; the client confirms with the same number.
    private void FormatChange(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
        {
            Report("dropped a format change that is too short");
            return;
        }

        var index = BinaryPrimitives.ReadUInt32LittleEndian(body);
        AudioFormat format;
        lock (_gate)
        {
            if (index >= _formats.Count)
            {
                Report($"dropped a change to format {index}, which was never agreed on");
                return;
            }

            format = _formats[(int)index];
            _current = format;
            _pending.Clear();
        }

        Transmit(new ByteWriter(5).U8(TypeFormatChange).U32(index).ToArray());
        if (IsRecording)
        {
            Guarded.Raise(Recording, format, Log, ChannelName + ": the handler of Recording");
        }
    }

    // Voice does not need more than 48 kHz and a server that lists mono first gets the lighter stream: mono before
    // stereo, then the highest rate up to 48 kHz, then what is above that.
    private static (int, int, int) Rank(AudioFormat format) =>
        (format.Rate > 48000 ? 1 : 0, format.Channels, format.Rate > 48000 ? format.Rate : -format.Rate);

    private void Reply(uint result) => Transmit(new ByteWriter(5).U8(TypeOpenReply).U32(result).ToArray());

    private void Transmit(byte[] message)
    {
        var send = Send;
        if (send is null)
        {
            Report("a message is dropped, the channel has no way to send yet");
            return;
        }

        Transmit(send, message);
    }

    private bool Transmit(Action<ReadOnlyMemory<byte>> send, byte[] message)
    {
        try
        {
            send(message);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The connection is going down; the core notices that by itself.
            Report("sending failed: " + exception.Message);
            return false;
        }
    }
}
