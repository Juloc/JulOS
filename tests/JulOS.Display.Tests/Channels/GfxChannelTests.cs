namespace JulOS.Integrations.Display.Rdp.Channels.Tests;

// The graphics pipeline (MS-RDPEGFX) as far as this client takes it: H.264 pictures of one surface, passed on as they are.
// The specification has no byte-for-byte examples for these messages in the parts that matter here, so the bytes below are the
// layouts of its tables (see GfxServer) and, where marked, bytes a real xrdp 0.10.1 sent.
public class GfxChannelTests
{
    private static (GfxChannel Channel, Wire Wire, List<GfxFrame> Frames, List<string> Unusable, List<(int, int)> Sizes) Opened()
    {
        var channel = new GfxChannel();
        var wire = new Wire(channel);
        var frames = new List<GfxFrame>();
        var unusable = new List<string>();
        var sizes = new List<(int, int)>();
        channel.Frame += frames.Add;
        channel.Unusable += unusable.Add;
        channel.DesktopSize += (width, height) => sizes.Add((width, height));
        channel.Opened();
        return (channel, wire, frames, unusable, sizes);
    }

    private static void Feed(GfxChannel channel, params byte[][] messages)
    {
        foreach (var message in messages)
        {
            channel.Receive(message);
        }
    }

    [Fact]
    public void The_channel_starts_with_the_capabilities_it_can_take_and_nothing_older()
    {
        var (_, wire, _, _, _) = Opened();

        // CAPS_ADVERTISE: command 0x12, 22 bytes, one set: version 8.1 (0x00080105), 4 bytes of data, flags AVC420_ENABLED.
        Hex.Same("12 00 00 00 16 00 00 00  01 00  05 01 08 00  04 00 00 00  10 00 00 00", wire.One());
    }

    [Fact]
    public void An_acknowledgement_names_the_frame_and_counts_the_frames_done()
    {
        var (channel, wire, _, _, _) = Opened();
        wire.Take();

        channel.Acknowledge(7);
        channel.Acknowledge(8);

        // FRAME_ACKNOWLEDGE: command 0x0D, 20 bytes, queue depth 0, frame id, total frames decoded.
        var sent = wire.Take();
        Hex.Same("0D 00 00 00 14 00 00 00  00 00 00 00  07 00 00 00  01 00 00 00", sent[0]);
        Hex.Same("0D 00 00 00 14 00 00 00  00 00 00 00  08 00 00 00  02 00 00 00", sent[1]);
    }

    [Fact]
    public void What_a_real_xrdp_sent_to_set_up_the_desktop_is_understood()
    {
        var (channel, wire, _, unusable, sizes) = Opened();
        wire.Take();

        // Captured from xrdp 0.10.1 (Debian 13): CAPS_CONFIRM (8.1, flags 0x10), CREATE_SURFACE 0 of 1280 x 720, MAP_SURFACE_TO_OUTPUT at 0,0.
        Feed(channel,
            Hex.Bytes("E0 04 13 00 00 00 14 00 00 00 05 01 08 00 04 00 00 00 10 00 00 00"),
            Hex.Bytes("E0 04 09 00 00 00 0F 00 00 00 00 00 00 05 D0 02 20"),
            Hex.Bytes("E0 04 0F 00 00 00 14 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00"));

        Assert.True(channel.IsConfirmed);
        Assert.Equal("8.1, flags 0x10", channel.Version);
        Assert.Equal([(1280, 720)], sizes);
        Assert.Empty(unusable);
        wire.Nothing();
    }

    [Fact]
    public void The_pictures_of_a_frame_come_out_as_the_bare_bitstream_and_wait_for_their_acknowledgement()
    {
        var (channel, wire, frames, unusable, sizes) = Opened();
        wire.Take();
        Feed(channel, GfxServer.Start());
        Assert.Equal([(1280, 720)], sizes);

        Feed(channel, GfxServer.AvcFrame(1, GfxServer.KeyBitstream));
        Feed(channel, GfxServer.AvcFrame(2, GfxServer.DeltaBitstream));

        Assert.Empty(unusable);
        Assert.Equal(2, frames.Count);
        Assert.Equal(1u, frames[0].FrameId);
        var key = Assert.Single(frames[0].Pictures);
        Assert.True(key.IsKey);
        Assert.Equal(GfxServer.KeyBitstream, key.Data);
        var delta = Assert.Single(frames[1].Pictures);
        Assert.False(delta.IsKey);
        Assert.Equal(GfxServer.DeltaBitstream, delta.Data);
        // The server hears nothing until the owner says the picture is on the screen
        wire.Nothing();

        channel.Acknowledge(frames[0].FrameId);
        Assert.Equal(1u, GfxServer.Read32(wire.One(), 12));
    }

    [Fact]
    public void Several_pictures_in_one_frame_keep_their_order_and_the_rectangles_are_skipped_whatever_their_number()
    {
        var (channel, _, frames, _, _) = Opened();
        Feed(channel, GfxServer.Start());

        var message = GfxServer.Container(
            GfxServer.StartFrame(5),
            GfxServer.Wire(0, 0x000B, GfxServer.Avc420(GfxServer.KeyBitstream, rectangles: 3)),
            GfxServer.Wire(0, 0x000B, GfxServer.Avc420(GfxServer.DeltaBitstream, rectangles: 0)),
            GfxServer.EndFrame(5));
        Feed(channel, message);

        var pictures = Assert.Single(frames).Pictures;
        Assert.Equal(2, pictures.Count);
        Assert.Equal(GfxServer.KeyBitstream, pictures[0].Data);
        Assert.Equal(GfxServer.DeltaBitstream, pictures[1].Data);
    }

    [Fact]
    public void A_frame_without_a_picture_for_the_screen_is_answered_at_once()
    {
        var (channel, wire, frames, unusable, _) = Opened();
        Feed(channel, GfxServer.Start());
        wire.Take();

        // The cache and a surface that is deleted again: nothing for the screen.
        Feed(channel, GfxServer.Container(GfxServer.StartFrame(9), GfxServer.Pdu(0x0008, GfxServer.U16(3)), GfxServer.EndFrame(9)));

        Assert.Empty(frames);
        Assert.Empty(unusable);
        Assert.Equal(9u, GfxServer.Read32(wire.One(), 12));
    }

    [Fact]
    public void A_message_in_several_segments_is_put_together()
    {
        var (channel, _, frames, _, _) = Opened();
        Feed(channel, GfxServer.Start());
        var pdus = GfxServer.AvcFrame(1, GfxServer.KeyBitstream)[2..];

        // The multipart container: segment count, the size of everything together, then every segment with its size and bulk header.
        var cut = 20;
        byte[] Segment(byte[] data) => [.. GfxServer.U32((uint)data.Length + 1), 0x04, .. data];
        var multipart = (byte[])[0xE1, 0x02, 0x00, .. GfxServer.U32((uint)pdus.Length), .. Segment(pdus[..cut]), .. Segment(pdus[cut..])];
        Feed(channel, multipart);

        Assert.Equal(GfxServer.KeyBitstream, Assert.Single(Assert.Single(frames).Pictures).Data);
    }

    [Fact]
    public void A_reset_forgets_the_surfaces_and_the_new_one_is_reported()
    {
        var (channel, _, frames, unusable, sizes) = Opened();
        Feed(channel, GfxServer.Start());

        // The desktop got another size: reset, a new surface under a new number, mapped.
        Feed(channel, GfxServer.Container(GfxServer.ResetGraphics(1920, 1080), GfxServer.CreateSurface(1, 1920, 1080), GfxServer.Map(1)));
        Feed(channel, GfxServer.Container(GfxServer.StartFrame(3), GfxServer.Wire(1, 0x000B, GfxServer.Avc420(GfxServer.KeyBitstream)), GfxServer.EndFrame(3)));

        Assert.Equal([(1280, 720), (1920, 1080)], sizes);
        Assert.Empty(unusable);
        Assert.Single(frames);
    }

    [Theory]
    [InlineData("an other codec (planar)", 0x000A)]
    [InlineData("progressive", 0x0009)]
    [InlineData("avc444", 0x000E)]
    public void A_picture_in_another_codec_ends_the_pipeline(string name, ushort codec)
    {
        var (channel, wire, frames, unusable, _) = Opened();
        Feed(channel, GfxServer.Start());

        Feed(channel, GfxServer.Container(GfxServer.StartFrame(1), GfxServer.Wire(0, codec, [1, 2, 3, 4, 5, 6, 7, 8]), GfxServer.EndFrame(1)));

        var reason = Assert.Single(unusable);
        Assert.Contains($"0x{codec:X4}", reason);
        Assert.True(channel.IsUnusable);
        Assert.Empty(frames);

        // Nothing more is handled after that, not even a good picture
        Feed(channel, GfxServer.AvcFrame(2, GfxServer.KeyBitstream));
        Assert.Empty(frames);
        Assert.Single(unusable);
        Assert.NotEmpty(name);
        _ = wire;
    }

    [Fact]
    public void Painting_into_the_surface_ends_the_pipeline()
    {
        foreach (ushort command in new ushort[] { 0x0004, 0x0005, 0x0007, 0x0002 })
        {
            var (channel, _, _, unusable, _) = Opened();
            Feed(channel, GfxServer.Start());

            Feed(channel, GfxServer.Container(GfxServer.Pdu(command, new byte[16])));

            Assert.Contains($"0x{command:X4}", Assert.Single(unusable));
        }
    }

    [Fact]
    public void A_server_that_does_not_use_h264_ends_the_pipeline_at_the_capabilities()
    {
        // Version 8.1 without the flag; version 8 (no H.264 at all); version 10 with AVC switched off.
        foreach (var (version, flags) in new (uint, uint)[] { (0x00080105, 0), (0x00080004, 0x10), (0x000A0200, 0x20) })
        {
            var (channel, _, _, unusable, _) = Opened();

            Feed(channel, GfxServer.Container(GfxServer.CapsConfirm(version, flags)));

            Assert.Contains("does not use H.264", Assert.Single(unusable));
            Assert.False(channel.IsConfirmed);
        }
    }

    [Fact]
    public void A_newer_set_with_h264_is_taken()
    {
        var (channel, _, _, unusable, _) = Opened();

        Feed(channel, GfxServer.Container(GfxServer.CapsConfirm(0x000A0200, 0x2)));

        Assert.Empty(unusable);
        Assert.True(channel.IsConfirmed);
    }

    [Fact]
    public void A_surface_that_is_not_at_the_origin_or_a_picture_for_another_surface_ends_the_pipeline()
    {
        var (channel, _, _, unusable, _) = Opened();
        Feed(channel, GfxServer.Container(GfxServer.ResetGraphics(1920, 1080), GfxServer.CreateSurface(0, 960, 1080), GfxServer.Map(0, 960, 0)));
        Assert.Contains("960,0", Assert.Single(unusable));

        (channel, _, _, unusable, _) = Opened();
        Feed(channel, GfxServer.Start());
        Feed(channel, GfxServer.Container(GfxServer.CreateSurface(5, 64, 64), GfxServer.StartFrame(1), GfxServer.Wire(5, 0x000B, GfxServer.Avc420(GfxServer.KeyBitstream)), GfxServer.EndFrame(1)));
        Assert.Contains("surface 5", Assert.Single(unusable));
    }

    [Fact]
    public void Compressed_data_ends_the_pipeline_because_it_is_not_unpacked()
    {
        var (channel, _, _, unusable, _) = Opened();

        // A single segment whose bulk header has the "compressed" bit (0x20).
        channel.Receive([0xE0, 0x24, 1, 2, 3]);

        Assert.Contains("compressed", Assert.Single(unusable));
    }

    [Fact]
    public void Whatever_the_server_sends_it_does_not_throw()
    {
        var random = new Random(11);
        for (var round = 0; round < 3000; round++)
        {
            var (channel, _, _, _, _) = Opened();
            var valid = GfxServer.Start().Concat([GfxServer.AvcFrame(1, GfxServer.KeyBitstream)]).ToArray();
            foreach (var message in valid)
            {
                var broken = message.ToArray();
                // Flip a few bytes, cut it, or both
                for (var i = random.Next(0, 4); i > 0; i--)
                {
                    broken[random.Next(broken.Length)] = (byte)random.Next(256);
                }

                channel.Receive(broken.AsSpan(0, random.Next(0, broken.Length + 1)));
            }
        }

        var noise = new byte[4096];
        for (var round = 0; round < 500; round++)
        {
            random.NextBytes(noise);
            noise[0] = (byte)(0xE0 + (round & 1));
            var (channel, _, _, _, _) = Opened();
            channel.Receive(noise.AsSpan(0, random.Next(1, noise.Length)));
        }
    }

    [Fact]
    public void The_whole_way_through_the_dynamic_channel_with_a_picture_of_many_pieces()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var gfx = new GfxChannel();
        var frames = new List<GfxFrame>();
        gfx.Frame += frames.Add;
        channels.Register(gfx);

        channels.Receive(DvcServer.Capabilities(2));
        channels.Receive(DvcServer.Create(9, GfxChannel.ChannelName));
        // The answer to the create request, the client's capabilities and the DVC capabilities answer
        var sent = wire.Take();
        Assert.Equal(3, sent.Length);
        Assert.Equal(0x12, sent[2][1 + 1]);     // header byte, channel id, then CAPS_ADVERTISE's command 0x12

        foreach (var message in GfxServer.Start())
        {
            foreach (var pdu in DvcServer.Message(9, message))
            {
                channels.Receive(pdu);
            }
        }

        // A key frame of 50 000 bytes: Data First and Data PDUs of the dynamic channel.
        var big = GfxServer.KeyBitstream.Concat(new byte[50_000]).ToArray();
        foreach (var pdu in DvcServer.Message(9, GfxServer.AvcFrame(1, big)))
        {
            channels.Receive(pdu);
        }

        Assert.Equal(big, Assert.Single(Assert.Single(frames).Pictures).Data);
        gfx.Acknowledge(1);
        var ack = wire.One();
        Assert.Equal(0x0D, ack[2]);
    }
}
