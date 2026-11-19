using System.Buffers.Binary;

namespace JulOS.Integrations.Display.Rdp.Channels.Tests;

// The microphone channel (MS-RDPEAI): the sequence a server runs, the choice of a format, and what it can get wrong.
public class AudioInputTests
{
    private static byte[] Format(ushort tag, ushort channels, uint rate, ushort bits = 16, params byte[] extra)
    {
        var align = (ushort)(channels * bits / 8);
        return
        [
            .. ClipServer.U16(tag), .. ClipServer.U16(channels), .. ClipServer.U32(rate), .. ClipServer.U32(rate * align),
            .. ClipServer.U16(align), .. ClipServer.U16(bits), .. ClipServer.U16((ushort)extra.Length), .. extra,
        ];
    }

    private static byte[] Pcm(uint rate, ushort channels) => Format(1, channels, rate);

    private static byte[] Version(uint version) => [0x01, .. ClipServer.U32(version)];

    // Its size field is "reserved" in a message of the server and has to be ignored: it says something wrong here.
    private static byte[] ServerFormats(params byte[][] formats) =>
        [0x02, .. ClipServer.U32((uint)formats.Length), .. ClipServer.U32(0xDEADBEEF), .. formats.SelectMany(format => format)];

    private static byte[] Open(uint frames, uint format, byte[] device) => [0x03, .. ClipServer.U32(frames), .. ClipServer.U32(format), .. device];

    private static (AudioInputChannel Channel, Wire Wire, List<AudioInputChannel.AudioFormat?> Recorded) Started()
    {
        var channel = new AudioInputChannel();
        var wire = new Wire(channel);
        var recorded = new List<AudioInputChannel.AudioFormat?>();
        channel.Recording += recorded.Add;
        channel.Opened();
        return (channel, wire, recorded);
    }

    // A whole start: version, formats (stereo 44100 first, mono 16000 and 48000 behind, and two the client does not
    // take), open, and then sound.
    private static (AudioInputChannel Channel, Wire Wire, List<AudioInputChannel.AudioFormat?> Recorded) Recording(uint frames = 4, uint format = 0)
    {
        var (channel, wire, recorded) = Started();
        channel.Receive(Version(2));
        channel.Receive(ServerFormats(Pcm(44100, 2), Format(6, 1, 8000, 8), Pcm(16000, 1), Pcm(48000, 1), Format(1, 1, 22050, 8)));
        wire.Take();
        channel.Receive(Open(frames, format, Pcm(44100, 2)));
        return (channel, wire, recorded);
    }

    [Fact]
    public void The_version_is_answered_with_the_lower_of_the_two()
    {
        var (channel, wire, _) = Started();

        channel.Receive(Version(2));
        Hex.Same("01 02 00 00 00", wire.One());

        channel.Receive(Version(1));
        Hex.Same("01 01 00 00 00", wire.One());

        // A server from the future is answered with what this client knows.
        channel.Receive(Version(7));
        Hex.Same("01 02 00 00 00", wire.One());
    }

    [Fact]
    public void The_answer_lists_the_pcm_formats_mono_first_and_says_how_long_it_is()
    {
        var (channel, wire, _) = Started();

        channel.Receive(ServerFormats(Pcm(44100, 2), Format(6, 1, 8000, 8), Pcm(16000, 1), Pcm(48000, 1), Format(1, 1, 22050, 8), Pcm(44100, 2)));

        var answer = wire.One();
        var expected = new byte[] { 0x02 }.Concat(ClipServer.U32(3)).Concat(ClipServer.U32(9 + 3 * 18))
            .Concat(Pcm(48000, 1)).Concat(Pcm(16000, 1)).Concat(Pcm(44100, 2)).ToArray();
        Hex.Same(expected, answer);
        Assert.True(channel.IsAvailable);
        Assert.Equal(new AudioInputChannel.AudioFormat(48000, 1), channel.Format);
        Assert.False(channel.IsRecording);
    }

    [Fact]
    public void A_server_without_pcm_gets_an_empty_list_and_no_microphone()
    {
        var (channel, wire, _) = Started();

        channel.Receive(ServerFormats(Format(6, 1, 8000, 8), Format(0x55, 2, 44100, 16, 1, 2, 3)));

        Hex.Same("02 00 00 00 00 09 00 00 00", wire.One());
        Assert.False(channel.IsAvailable);
        Assert.Contains("no microphone", Assert.Single(wire.Log));
    }

    [Fact]
    public void An_open_starts_the_recording_in_the_format_it_names_and_is_answered_ok()
    {
        var (channel, wire, recorded) = Recording(frames: 4, format: 1);

        Hex.Same("04 00 00 00 00", wire.One());
        Assert.True(channel.IsRecording);
        Assert.Equal([new AudioInputChannel.AudioFormat(16000, 1)], recorded);
    }

    [Fact]
    public void An_open_with_an_unknown_format_or_no_frames_is_refused()
    {
        var (channel, wire, recorded) = Started();
        channel.Receive(ServerFormats(Pcm(16000, 1)));
        wire.Take();

        channel.Receive(Open(4, 1, Pcm(16000, 1)));
        Hex.Same("04 05 40 00 80", wire.One());                                      // E_FAIL
        channel.Receive(Open(0, 0, Pcm(16000, 1)));
        Hex.Same("04 05 40 00 80", wire.One());
        channel.Receive([0x03, 1, 2]);                                               // too short
        Hex.Same("04 05 40 00 80", wire.One());

        Assert.False(channel.IsRecording);
        Assert.Empty(recorded);
    }

    // Each packet has the number of frames the server asked for, announced by an Incoming Data message.
    [Fact]
    public void Sound_goes_out_in_packets_of_the_size_the_server_asked_for()
    {
        var (channel, wire, _) = Recording(frames: 4, format: 2);                 // 48000 Hz stereo? no: list is 48000 mono, 16000 mono, 44100 stereo
        wire.Take();
        var format = channel.Format!.Value;
        Assert.Equal(new AudioInputChannel.AudioFormat(44100, 2), format);

        var sound = Enumerable.Range(0, 40).Select(i => (byte)(i + 1)).ToArray();   // 10 frames of 4 bytes
        Assert.True(channel.SendSamples(sound));

        // 10 frames = two packets of 4 and 2 left over
        var sent = wire.Take();
        Assert.Equal(4, sent.Length);
        Hex.Same("05", sent[0]);
        Hex.Same([0x06, .. sound.AsSpan(0, 16)], sent[1]);
        Hex.Same("05", sent[2]);
        Hex.Same([0x06, .. sound.AsSpan(16, 16)], sent[3]);

        // the rest is kept, together with new sound it makes the next packet
        Assert.True(channel.SendSamples(sound.AsSpan(0, 8)));
        sent = wire.Take();
        Assert.Equal(2, sent.Length);
        Hex.Same([0x06, .. sound.AsSpan(32, 8), .. sound.AsSpan(0, 8)], sent[1]);
    }

    [Fact]
    public void Sound_is_dropped_while_the_server_has_not_asked_for_it()
    {
        var (channel, wire, _) = Started();
        Assert.False(channel.SendSamples(new byte[100]));

        channel.Receive(ServerFormats(Pcm(16000, 1)));
        wire.Take();
        Assert.False(channel.SendSamples(new byte[100]));
        wire.Nothing();
    }

    [Fact]
    public void A_format_change_is_confirmed_and_applies_from_then_on()
    {
        var (channel, wire, recorded) = Recording(frames: 2, format: 0);
        wire.Take();

        channel.Receive([0x07, .. ClipServer.U32(1)]);

        Hex.Same("07 01 00 00 00", wire.One());
        Assert.Equal(new AudioInputChannel.AudioFormat(16000, 1), channel.Format);
        Assert.Equal(2, recorded.Count);

        channel.Receive([0x07, .. ClipServer.U32(9)]);
        wire.Nothing();
        Assert.Contains("never agreed", wire.Log.Last());
    }

    [Fact]
    public void A_second_open_starts_over()
    {
        var (channel, wire, recorded) = Recording(frames: 4, format: 0);
        wire.Take();
        channel.SendSamples(new byte[3]);                                          // a rest of an incomplete frame

        channel.Receive(Open(2, 1, Pcm(16000, 1)));

        Hex.Same("04 00 00 00 00", wire.One());
        Assert.Equal(2, recorded.Count);
        channel.SendSamples(new byte[4]);                                          // 2 frames of 2 bytes: one packet, nothing from the rest
        Assert.Equal(2, wire.Take().Length);
    }

    [Fact]
    public void Closing_the_channel_ends_everything()
    {
        var (channel, wire, recorded) = Recording();
        var changes = 0;
        channel.StateChanged += () => changes++;

        channel.Closed();

        Assert.False(channel.IsAvailable);
        Assert.False(channel.IsRecording);
        Assert.Null(channel.Format);
        Assert.Null(recorded.Last());
        Assert.Equal(1, changes);
        Assert.False(channel.SendSamples(new byte[16]));
    }

    [Fact]
    public void Garbage_is_dropped_and_reported_never_thrown()
    {
        var (channel, wire, _) = Started();

        channel.Receive([]);
        channel.Receive([0x01, 1]);
        channel.Receive([0x02, 1, 0, 0, 0]);
        channel.Receive(ServerFormats(Pcm(16000, 1))[..20]);                       // shorter than it says
        channel.Receive([0x07, 1]);
        channel.Receive([0x7F]);
        channel.Receive(new byte[3000]);

        wire.Nothing();
        Assert.True(wire.Log.Count >= 6);
        Assert.False(channel.IsAvailable);
    }

    [Fact]
    public void An_absurd_number_of_formats_is_cut_off_and_a_huge_count_does_not_hang()
    {
        var (channel, wire, _) = Started();

        channel.Receive(ServerFormats([.. Enumerable.Range(0, 100).Select(i => Pcm((uint)(8000 + i * 100), 1))]));
        Assert.Equal((uint)AudioInputChannel.MaxFormats, BinaryPrimitives.ReadUInt32LittleEndian(wire.One().AsSpan(1)));

        channel.Receive([0x02, .. ClipServer.U32(uint.MaxValue), .. ClipServer.U32(0)]);
        wire.Nothing();
    }

    [Fact]
    public void A_failing_send_does_not_throw_out_of_the_channel()
    {
        var (channel, wire, _) = Recording(frames: 1, format: 1);
        channel.Send = _ => throw new IOException("gone");

        Assert.False(channel.SendSamples(new byte[2]));
        Assert.Contains("sending failed", wire.Log.Last());
    }
}
