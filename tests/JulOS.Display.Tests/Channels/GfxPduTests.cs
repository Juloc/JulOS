namespace JulOS.Integrations.Display.Rdp.Channels.Tests;

// How the graphics pipeline's messages are read and built (MS-RDPEGFX). Whatever is read from the wire is checked against bytes laid
// out from the specification's tables, not against what this code wrote.
public class GfxPduTests
{
    [Fact]
    public void The_capabilities_advertise_exactly_version_8_1_with_h264()
    {
        Hex.Same("12 00 00 00 16 00 00 00 01 00 05 01 08 00 04 00 00 00 10 00 00 00", GfxPdu.BuildCapsAdvertise());
    }

    [Fact]
    public void The_acknowledgement_has_the_queue_depth_the_frame_and_the_count()
    {
        Hex.Same("0D 00 00 00 14 00 00 00 00 00 00 00 EF BE AD DE 05 00 00 00", GfxPdu.BuildFrameAcknowledge(0xDEADBEEF, 5));
        Hex.Same("0D 00 00 00 14 00 00 00 03 00 00 00 01 00 00 00 01 00 00 00", GfxPdu.BuildFrameAcknowledge(1, 1, 3));
    }

    [Fact]
    public void A_single_segment_is_unwrapped_whether_or_not_it_has_data()
    {
        Assert.True(GfxPdu.TryUnwrap([0xE0, 0x04, 1, 2, 3], out var payload, out _));
        Assert.Equal([1, 2, 3], payload);
        Assert.True(GfxPdu.TryUnwrap([0xE0, 0x04], out payload, out _));
        Assert.Empty(payload);
    }

    [Fact]
    public void Segments_that_do_not_hold_together_are_refused_with_a_reason()
    {
        // Compressed, empty, a header only, an unknown descriptor, a multipart container whose sizes lie.
        foreach (var message in new byte[][]
        {
            [0xE0, 0x24, 1], [], [0xE0], [0xE2, 0x04, 0x00],
            [0xE1, 0x01, 0x00, 5, 0, 0, 0, 9, 0, 0, 0, 0x04, 1],          // the segment is longer than what follows
            [0xE1, 0x01, 0x00, 5, 0, 0, 0, 3, 0, 0, 0, 0x04, 1, 2],      // the segment is longer than the container says
            [0xE1, 0x01, 0x00, 5, 0, 0, 0, 2, 0, 0, 0, 0x04, 1],         // shorter
            [0xE1, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0],       // a claim of 2 GB
            [0xE1, 0x01, 0x00, 1, 0, 0, 0, 2, 0, 0, 0, 0x24, 1],         // compressed
        })
        {
            Assert.False(GfxPdu.TryUnwrap(message, out _, out var problem), Hex.Text(message));
            Assert.False(string.IsNullOrEmpty(problem));
        }
    }

    [Fact]
    public void Pdus_are_cut_by_their_own_length()
    {
        var data = GfxServer.Container(GfxServer.EndFrame(3), GfxServer.StartFrame(4))[2..];
        var reader = new ByteReader(data);

        Assert.True(GfxPdu.TryNext(ref reader, out var command, out var body));
        Assert.Equal(GfxPdu.EndFrame, command);
        Assert.True(GfxPdu.TryEndFrame(body, out var end));
        Assert.Equal(3u, end);

        Assert.True(GfxPdu.TryNext(ref reader, out command, out body));
        Assert.Equal(GfxPdu.StartFrame, command);
        Assert.True(GfxPdu.TryStartFrame(body, out var start));
        Assert.Equal(4u, start);
        Assert.False(GfxPdu.TryNext(ref reader, out _, out _));
    }

    [Fact]
    public void A_pdu_whose_length_is_wrong_is_not_cut()
    {
        foreach (var message in new byte[][]
        {
            [0x0C, 0, 0, 0, 7, 0, 0, 0],                // shorter than its own header
            [0x0C, 0, 0, 0, 20, 0, 0, 0, 1, 2],         // longer than what is there
            [0x0C, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF],     // a huge length
            [0x0C, 0, 0],                                // not even a header
        })
        {
            var reader = new ByteReader(message);
            Assert.False(GfxPdu.TryNext(ref reader, out _, out _), Hex.Text(message));
        }
    }

    [Fact]
    public void Wire_to_surface_gives_the_codec_and_the_bytes_of_the_picture_and_the_h264_stream_inside_it()
    {
        var pdu = GfxServer.Wire(0, 0x000B, GfxServer.Avc420(GfxServer.DeltaBitstream, rectangles: 2));
        var reader = new ByteReader(pdu);
        Assert.True(GfxPdu.TryNext(ref reader, out _, out var body));

        Assert.True(GfxPdu.TryWireToSurface1(body, out var surface, out var codec, out var data));
        Assert.Equal(0, surface);
        Assert.Equal(GfxPdu.CodecAvc420, codec);
        Assert.True(GfxPdu.TryAvc420(data, out var bitstream));
        Assert.Equal(GfxServer.DeltaBitstream, bitstream.ToArray());
    }

    [Fact]
    public void Wire_to_surface_with_a_length_that_lies_is_refused()
    {
        var pdu = GfxServer.Wire(0, 0x000B, GfxServer.Avc420(GfxServer.DeltaBitstream));
        var reader = new ByteReader(pdu);
        GfxPdu.TryNext(ref reader, out _, out var body);

        Assert.False(GfxPdu.TryWireToSurface1(body[..^3], out _, out _, out _));
        Assert.False(GfxPdu.TryWireToSurface1(body[..10], out _, out _, out _));
        // The rectangle count of an H.264 stream larger than the data, or a stream without a bitstream
        Assert.False(GfxPdu.TryAvc420([0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3], out _));
        Assert.False(GfxPdu.TryAvc420([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], out _));
    }

    [Fact]
    public void Only_a_stream_with_an_idr_unit_counts_as_a_key_frame()
    {
        Assert.True(GfxPdu.HasIdr(GfxServer.KeyBitstream));
        Assert.True(GfxPdu.HasIdr([0, 0, 1, 0x65, 0x88]));                  // three-byte start code
        Assert.False(GfxPdu.HasIdr(GfxServer.DeltaBitstream));
        Assert.False(GfxPdu.HasIdr([0, 0, 0, 1, 0x67, 0x64, 0, 0x28]));     // parameter sets alone
        Assert.False(GfxPdu.HasIdr([]));
        Assert.False(GfxPdu.HasIdr([0, 0, 1]));
    }

    [Fact]
    public void The_capabilities_the_server_confirms_are_read_and_judged()
    {
        Assert.True(GfxPdu.TryCapsConfirm(GfxServer.CapsConfirm(0x00080105, 0x10)[8..], out var version, out var flags));
        Assert.Equal(0x00080105u, version);
        Assert.Equal(0x10u, flags);
        Assert.False(GfxPdu.TryCapsConfirm([1, 2, 3], out _, out _));
        Assert.False(GfxPdu.TryCapsConfirm([5, 1, 8, 0, 4, 0, 0, 0, 1], out _, out _));   // says 4 bytes, has 1

        Assert.True(GfxPdu.AvcEnabled(0x00080105, 0x10));
        Assert.False(GfxPdu.AvcEnabled(0x00080105, 0x02));
        Assert.False(GfxPdu.AvcEnabled(0x00080004, 0x10));
        Assert.True(GfxPdu.AvcEnabled(0x000A0002, 0));
        Assert.True(GfxPdu.AvcEnabled(0x000A0100, 0));          // 10.1 has no flags at all
        Assert.False(GfxPdu.AvcEnabled(0x000A0200, 0x20));
        Assert.False(GfxPdu.AvcEnabled(0x00090000, 0));
    }

    [Fact]
    public void Desktop_and_surface_sizes_are_read_and_nonsense_is_refused()
    {
        var reset = GfxServer.ResetGraphics(1920, 1080);
        Assert.Equal(340, reset.Length);
        Assert.True(GfxPdu.TryResetGraphics(reset[8..], out var width, out var height));
        Assert.Equal((1920, 1080), (width, height));
        Assert.False(GfxPdu.TryResetGraphics(GfxServer.ResetGraphics(0, 1080)[8..], out _, out _));
        Assert.False(GfxPdu.TryResetGraphics([1, 2, 3], out _, out _));

        Assert.True(GfxPdu.TryCreateSurface([3, 0, 0x80, 0x07, 0x38, 0x04, 0x20], out var id, out width, out height));
        Assert.Equal((3, 1920, 1080), (id, width, height));
        Assert.False(GfxPdu.TryCreateSurface([3, 0, 0, 0, 0x38, 0x04, 0x20], out _, out _, out _));

        Assert.True(GfxPdu.TryMapSurface(GfxServer.Map(2, 10, 20)[8..], out id, out var x, out var y));
        Assert.Equal((2, 10u, 20u), (id, x, y));
        Assert.False(GfxPdu.TryMapSurface([2, 0, 0, 0, 1, 0], out _, out _, out _));
    }
}
