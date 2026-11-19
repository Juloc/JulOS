using System.Buffers.Binary;
using JulOS.Integrations.Display.Rdp.Channels;
using JulOS.Integrations.Display.Rdp.Channels.Tests;

namespace JulOS.Integrations.Display.Tests;

public sealed class AudioBridgeTests
{
    private static byte[] Pcm(uint rate, ushort channels) =>
    [
        .. ClipServer.U16(1), .. ClipServer.U16(channels), .. ClipServer.U32(rate), .. ClipServer.U32(rate * channels * 2u),
        .. ClipServer.U16((ushort)(channels * 2)), .. ClipServer.U16(16), .. ClipServer.U16(0),
    ];

    private static byte[] ServerFormats(params byte[][] formats) =>
    [
        0x07, 0x00, .. ClipServer.U16((ushort)(20 + formats.Sum(f => f.Length))), .. ClipServer.U32(0), .. ClipServer.U32(0), .. ClipServer.U32(0),
        .. ClipServer.U16(0), .. ClipServer.U16((ushort)formats.Length), 0, .. ClipServer.U16(8), 0, .. formats.SelectMany(f => f),
    ];

    private static byte[] Wave2(byte block, byte[] sound) =>
    [
        0x0D, 0, .. ClipServer.U16((ushort)(12 + sound.Length)), .. ClipServer.U16(0), .. ClipServer.U16(0), block, 0, 0, 0, .. ClipServer.U32(0), .. sound,
    ];

    private sealed class Rig
    {
        public SoundChannel Sound { get; } = new();
        public AudioInputChannel Microphone { get; } = new();
        public AudioBridge Bridge { get; }
        public Wire SoundWire { get; }
        public Wire MicWire { get; }
        public int Ready { get; private set; }
        public List<AudioStatus> Statuses { get; } = [];

        public Rig()
        {
            SoundWire = new Wire(Sound);
            MicWire = new Wire(Microphone);
            Bridge = new AudioBridge(Sound, Microphone);
            Bridge.Ready += () => Ready++;
            Bridge.StatusChanged += Statuses.Add;
            Sound.Receive(ServerFormats(Pcm(48000, 2)));
        }
    }

    // 20 ms of 48 kHz stereo
    private static byte[] Block => new byte[960 * 4];

    [Fact]
    public void Sound_is_off_until_the_browser_asks_for_it_but_every_block_is_confirmed()
    {
        var rig = new Rig();
        rig.SoundWire.Take();

        rig.Sound.Receive(Wave2(7, Block));

        Assert.Equal(0, rig.Ready);
        Assert.False(rig.Bridge.TryTake(out _));
        Assert.Equal(0x05, Assert.Single(rig.SoundWire.Take())[0]);                // confirmed anyway
    }

    [Fact]
    public void Sound_waits_for_the_sender_and_comes_out_in_order_with_its_format()
    {
        var rig = new Rig();
        rig.Bridge.OutputOn = true;

        rig.Sound.Receive(Wave2(1, [.. Block.Select(_ => (byte)1)]));
        rig.Sound.Receive(Wave2(2, [.. Block.Select(_ => (byte)2)]));

        Assert.Equal(1, rig.Ready);                                                // one signal until it was drained
        Assert.True(rig.Bridge.TryTake(out var first));
        Assert.Equal(48000, first.Rate);
        Assert.Equal(2, first.Channels);
        Assert.Equal(1, first.Pcm.Span[0]);
        Assert.True(rig.Bridge.TryTake(out var second));
        Assert.Equal(2, second.Pcm.Span[0]);
        Assert.False(rig.Bridge.TryTake(out _));

        rig.Sound.Receive(Wave2(3, Block));
        Assert.Equal(2, rig.Ready);                                                // drained: signalled again
    }

    [Fact]
    public void A_sender_that_never_comes_leaves_a_bounded_queue_of_the_newest_sound()
    {
        var rig = new Rig();
        rig.Bridge.OutputOn = true;

        for (var i = 0; i < 500; i++)                                              // 10 seconds
        {
            rig.Sound.Receive(Wave2((byte)i, [.. Block.Select(_ => (byte)i)]));
        }

        var taken = new List<AudioBlock>();
        while (rig.Bridge.TryTake(out var block))
        {
            taken.Add(block);
        }

        Assert.InRange(taken.Count, 1, AudioBridge.MaxQueuedMs / 20);
        Assert.Equal((byte)(499 % 256), taken[^1].Pcm.Span[0]);                    // the newest is kept
        Assert.Equal(1, rig.Ready);
    }

    [Fact]
    public void Switching_off_throws_away_what_waits()
    {
        var rig = new Rig();
        rig.Bridge.OutputOn = true;
        rig.Sound.Receive(Wave2(1, Block));

        rig.Bridge.OutputOn = false;

        Assert.False(rig.Bridge.TryTake(out _));
    }

    [Fact]
    public void The_status_tells_what_the_remote_desktop_offers()
    {
        var rig = new Rig();
        Assert.True(rig.Bridge.Status.Output);
        Assert.False(rig.Bridge.Status.MicrophoneAvailable);
        Assert.Equal(AudioBridge.DefaultMicrophoneRate, rig.Bridge.Status.MicrophoneRate);

        rig.Microphone.Opened();
        rig.Microphone.Receive([0x02, .. ClipServer.U32(1), .. ClipServer.U32(0), .. Pcm(16000, 1)]);
        Assert.Equal(new AudioStatus(true, true, false, 16000), rig.Bridge.Status);
        Assert.Equal(3, rig.Bridge.Status.Flags);

        rig.Microphone.Receive([0x03, .. ClipServer.U32(160), .. ClipServer.U32(0), .. Pcm(16000, 1)]);
        Assert.Equal(7, rig.Bridge.Status.Flags);
        Assert.Equal(new AudioStatus(true, true, true, 16000), rig.Statuses.Last());

        rig.Microphone.Closed();
        Assert.Equal(new AudioStatus(true, false, false, AudioBridge.DefaultMicrophoneRate), rig.Statuses.Last());
        Assert.All(rig.Statuses.Zip(rig.Statuses.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));   // only changes are reported
    }

    [Fact]
    public void Microphone_sound_is_converted_to_the_format_the_server_asked_for()
    {
        var rig = new Rig();
        rig.Microphone.Opened();
        rig.Microphone.Receive([0x02, .. ClipServer.U32(1), .. ClipServer.U32(0), .. Pcm(16000, 2)]);
        rig.Microphone.Receive([0x03, .. ClipServer.U32(160), .. ClipServer.U32(0), .. Pcm(16000, 2)]);
        rig.MicWire.Take();

        // 160 frames of 16000 Hz mono become 160 frames of stereo: one packet
        var mono = new byte[160 * 2];
        for (var i = 0; i < 160; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(mono.AsSpan(i * 2), (short)(i * 10));
        }

        rig.Bridge.Microphone(mono, 16000);

        var sent = rig.MicWire.Take();
        Assert.Equal(2, sent.Length);
        Assert.Equal(1 + 160 * 4, sent[1].Length);
        Assert.Equal(50, BinaryPrimitives.ReadInt16LittleEndian(sent[1].AsSpan(1 + 5 * 4)));           // left
        Assert.Equal(50, BinaryPrimitives.ReadInt16LittleEndian(sent[1].AsSpan(1 + 5 * 4 + 2)));       // right
    }

    [Fact]
    public void Microphone_sound_is_dropped_while_the_server_does_not_record()
    {
        var rig = new Rig();
        rig.Bridge.Microphone(new byte[640], 16000);
        rig.MicWire.Nothing();
    }

    [Fact]
    public async Task Messages_from_the_browser_switch_the_sound_and_reach_the_microphone_without_harm()
    {
        await using var stream = new ScreenStream(new Rdp.RdpOptions { Host = "unused", UserName = "u", Password = "p", Width = 800, Height = 600 });
        Assert.False(stream.Audio.OutputOn);

        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserAudio, 1]);
        Assert.True(stream.Audio.OutputOn);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserAudio, 0]);
        Assert.False(stream.Audio.OutputOn);

        // too short, an absurd rate, an odd length and a message while nobody records: all without an exception
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserAudio]);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserMicrophone, 0, 0, 0, 0]);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserMicrophone, 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3]);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserMicrophone, 0, 0, 0x3E, 0x80, 1, 2, 3, 4]);
    }

    [Fact]
    public async Task A_browser_that_takes_over_a_kept_session_is_told_what_the_desktop_offers_for_sound()
    {
        await using var stream = new ScreenStream(new Rdp.RdpOptions { Host = "unused", UserName = "u", Password = "p", Width = 800, Height = 600 });
        stream.Audio.OutputOn = true;
        stream.Park();
        Assert.False(stream.Audio.OutputOn);                                       // the old browser is gone: no sound is sent to nobody

        stream.Resume();

        var events = new List<ScreenEvent>();
        while (stream.Events.TryRead(out var evt))
        {
            events.Add(evt);
        }

        var status = Assert.Single(events.OfType<ScreenAudioStatus>());           // the bridge only reports changes, so the new browser must be told
        Assert.Equal(stream.Audio.Status, status.Status);
    }

    [Fact]
    public void The_message_to_the_browser_has_the_kind_format_time_and_sound()
    {
        var buffer = new byte[64];
        var length = ScreenProtocol.WriteAudio(buffer, new AudioBlock(new byte[] { 1, 2, 3, 4 }, 44100, 2, 0x01020304));

        Assert.Equal(ScreenProtocol.AudioHeader + 4, length);
        Assert.Equal(20, buffer[0]);
        Assert.Equal(2, buffer[1]);
        Assert.Equal(44100, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(2)));
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(6)));
        Assert.Equal([1, 2, 3, 4], buffer[10..14]);

        Assert.Equal(6, ScreenProtocol.WriteAudioStatus(buffer, new AudioStatus(true, true, false, 16000)));
        Assert.Equal([21, 3, 0, 0, 0x3E, 0x80], buffer[..6]);
    }
}

public sealed class PcmResamplerTests
{
    private static byte[] Ramp(int count, int from = 0) =>
        [.. Enumerable.Range(0, count).SelectMany(i => BitConverter.GetBytes((short)(from + i * 10)))];

    private static short[] Samples(byte[] pcm) => [.. Enumerable.Range(0, pcm.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)))];

    [Fact]
    public void The_same_rate_only_spreads_over_the_channels()
    {
        var resampler = new PcmResampler();
        Assert.Equal(Ramp(5), resampler.Convert(Ramp(5), 16000, 16000, 1));
        Assert.Equal([0, 0, 10, 10, 20, 20], Samples(resampler.Convert(Ramp(3), 16000, 16000, 2)));
    }

    [Fact]
    public void A_ramp_stays_a_ramp_in_the_new_rate_and_across_chunks()
    {
        var resampler = new PcmResampler();

        var whole = Samples(new PcmResampler().Convert(Ramp(480), 48000, 16000, 1));
        var parts = Samples(resampler.Convert(Ramp(240), 48000, 16000, 1)).Concat(Samples(resampler.Convert(Ramp(240, 2400), 48000, 16000, 1))).ToArray();

        Assert.InRange(whole.Length, 159, 161);
        Assert.Equal(whole.Length, parts.Length);
        for (var i = 2; i < whole.Length; i++)
        {
            Assert.InRange(whole[i] - whole[i - 1], 29, 31);                       // 3 input samples of 10 per output sample
            Assert.Equal(whole[i], parts[i]);                                      // cutting the input anywhere changes nothing
        }
    }

    [Fact]
    public void Upsampling_gives_the_right_count_and_empty_input_gives_nothing()
    {
        var resampler = new PcmResampler();
        Assert.InRange(resampler.Convert(Ramp(160), 16000, 48000, 2).Length / 4, 479, 481);
        Assert.Empty(resampler.Convert([], 16000, 48000, 2));
        Assert.Empty(resampler.Convert([1], 16000, 48000, 2));
    }
}
