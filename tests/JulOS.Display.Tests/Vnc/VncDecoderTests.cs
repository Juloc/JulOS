using System.IO.Compression;
using JulOS.Integrations.Display.Vnc;

namespace JulOS.Integrations.Display.Tests.Vnc;

/// <summary>Each encoding with rectangles built by hand: the bytes a server would send, and the picture that has to come out.</summary>
public sealed class VncDecoderTests
{
    private static RfbReader Reader(params byte[][] parts) => new(new MemoryStream(parts.SelectMany(p => p).ToArray()));

    private static (byte B, byte G, byte R) At(RfbCanvas canvas, int x, int y)
    {
        var at = y * canvas.Stride + x * 4;
        return (canvas.Pixels[at], canvas.Pixels[at + 1], canvas.Pixels[at + 2]);
    }

    private static byte[] Px(byte b, byte g, byte r) => [b, g, r, 0];

    private static byte[] Cpx(byte b, byte g, byte r) => [b, g, r];

    private static byte[] Repeat(byte[] pixel, int count) => Enumerable.Range(0, count).SelectMany(_ => pixel).ToArray();

    [Fact]
    public void Raw_copies_the_pixels_row_by_row_and_makes_them_opaque()
    {
        var canvas = new RfbCanvas(6, 4);
        var data = new List<byte>();
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                data.AddRange(Px((byte)(x * 10), (byte)(y * 10), 200));
            }
        }

        VncDecoders.DecodeRaw(Reader(data.ToArray()), canvas, 2, 1, 3, 2);
        Assert.Equal(((byte)20, (byte)10, (byte)200), At(canvas, 4, 2));
        Assert.Equal(((byte)0, (byte)0, (byte)200), At(canvas, 2, 1));
        Assert.Equal(0xFF, canvas.Pixels[(1 * 6 + 2) * 4 + 3]);
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(canvas, 1, 1));              // outside stays as it was
    }

    [Fact]
    public void A_rectangle_outside_the_screen_is_refused_not_written()
    {
        var canvas = new RfbCanvas(4, 4);
        Assert.Throws<VncProtocolException>(() => VncDecoders.DecodeRaw(Reader(new byte[200]), canvas, 2, 2, 4, 4));
        Assert.Throws<VncProtocolException>(() => VncDecoders.DecodeRaw(Reader(new byte[200]), canvas, -1, 0, 2, 2));
    }

    [Fact]
    public void CopyRect_copies_inside_the_picture_also_when_the_areas_overlap()
    {
        var canvas = new RfbCanvas(8, 4);
        for (var x = 0; x < 8; x++)
        {
            canvas.Fill(x, 0, 1, 4, (byte)(x + 1), 0, 0);
        }

        // Move columns 0..4 two to the right (source and target overlap)
        VncDecoders.DecodeCopyRect(Reader([0, 0, 0, 0]), canvas, 2, 0, 5, 4);
        for (var x = 0; x < 5; x++)
        {
            Assert.Equal((byte)(x + 1), At(canvas, x + 2, 3).B);
        }

        // And upwards/downwards over rows
        var rows = new RfbCanvas(2, 6);
        for (var y = 0; y < 6; y++)
        {
            rows.Fill(0, y, 2, 1, (byte)(y + 1), 0, 0);
        }

        VncDecoders.CopyWithin(rows, 0, 0, 0, 2, 2, 4);
        Assert.Equal([(byte)1, (byte)2, (byte)1, (byte)2, (byte)3, (byte)4], Enumerable.Range(0, 6).Select(y => At(rows, 0, y).B).ToArray());
        VncDecoders.CopyWithin(rows, 0, 2, 0, 0, 2, 4);
        Assert.Equal([(byte)1, (byte)2, (byte)3, (byte)4, (byte)3, (byte)4], Enumerable.Range(0, 6).Select(y => At(rows, 0, y).B).ToArray());
    }

    [Fact]
    public void RRE_paints_the_background_and_then_its_subrectangles()
    {
        var canvas = new RfbCanvas(10, 6);
        List<byte[]> data =
        [
            [0, 0, 0, 2],                         // two subrectangles
            Px(9, 9, 9),                          // background
            Px(1, 2, 3), [0, 1, 0, 1, 0, 2, 0, 2],     // x 1, y 1, 2 x 2
            Px(4, 5, 6), [0, 6, 0, 0, 0, 1, 0, 3],     // x 6, y 0, 1 x 3
        ];
        VncDecoders.DecodeRre(Reader(data.ToArray()), canvas, 2, 1, 8, 5);
        Assert.Equal(((byte)9, (byte)9, (byte)9), At(canvas, 2, 1));
        Assert.Equal(((byte)1, (byte)2, (byte)3), At(canvas, 3, 2));
        Assert.Equal(((byte)1, (byte)2, (byte)3), At(canvas, 4, 3));
        Assert.Equal(((byte)9, (byte)9, (byte)9), At(canvas, 5, 2));
        Assert.Equal(((byte)4, (byte)5, (byte)6), At(canvas, 8, 1));
        Assert.Equal(((byte)4, (byte)5, (byte)6), At(canvas, 8, 3));
        Assert.Equal(((byte)9, (byte)9, (byte)9), At(canvas, 8, 4));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(canvas, 1, 1));              // outside
    }

    [Fact]
    public void RRE_refuses_a_subrectangle_that_leaves_its_rectangle()
    {
        var canvas = new RfbCanvas(10, 6);
        var data = new byte[][] { [0, 0, 0, 1], Px(0, 0, 0), Px(1, 1, 1), [0, 4, 0, 0, 0, 5, 0, 1] };
        Assert.Throws<VncProtocolException>(() => VncDecoders.DecodeRre(Reader(data), canvas, 0, 0, 8, 4));
    }

    [Fact]
    public void Hextile_covers_raw_tiles_background_foreground_and_coloured_subrectangles()
    {
        // A 20 x 17 rectangle: tiles 16x16, 4x16, 16x1, 4x1
        var canvas = new RfbCanvas(24, 20);
        var parts = new List<byte[]>();
        // tile 1: background and foreground given, two subrectangles in the foreground colour
        parts.Add([0x02 | 0x04 | 0x08]);
        parts.Add(Px(10, 20, 30));       // background
        parts.Add(Px(200, 100, 50));     // foreground
        parts.Add([2]);
        parts.Add([0x00, 0x00]);         // x 0, y 0, 1 x 1
        parts.Add([0x23, 0x11]);         // x 2, y 3, 2 x 2
        // tile 2: nothing given - the background of the tile before carries on, and no subrectangles
        parts.Add([0x00]);
        // tile 3: raw, 16 x 1
        parts.Add([0x01]);
        for (var i = 0; i < 16; i++)
        {
            parts.Add(Px((byte)i, 1, 2));
        }

        // tile 4: coloured subrectangles, one new background
        parts.Add([0x02 | 0x08 | 0x10]);
        parts.Add(Px(77, 77, 77));
        parts.Add([1]);
        parts.Add(Px(5, 6, 7));
        parts.Add([0x20, 0x00]);         // x 2, y 0, 1 x 1
        VncDecoders.DecodeHextile(Reader(parts.ToArray()), canvas, 1, 1, 20, 17);

        Assert.Equal(((byte)200, (byte)100, (byte)50), At(canvas, 1, 1));
        Assert.Equal(((byte)10, (byte)20, (byte)30), At(canvas, 2, 1));
        Assert.Equal(((byte)200, (byte)100, (byte)50), At(canvas, 3, 4));
        Assert.Equal(((byte)200, (byte)100, (byte)50), At(canvas, 4, 5));
        Assert.Equal(((byte)10, (byte)20, (byte)30), At(canvas, 5, 5));
        Assert.Equal(((byte)10, (byte)20, (byte)30), At(canvas, 17, 5));          // tile 2 has the old background
        Assert.Equal(((byte)3, (byte)1, (byte)2), At(canvas, 1 + 3, 17));         // tile 3: raw pixel 3 (x 3 of the row)
        Assert.Equal(((byte)77, (byte)77, (byte)77), At(canvas, 17, 17));
        Assert.Equal(((byte)5, (byte)6, (byte)7), At(canvas, 19, 17));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(canvas, 0, 0));
    }

    [Fact]
    public void Hextile_refuses_a_subrectangle_that_leaves_its_tile()
    {
        var canvas = new RfbCanvas(16, 16);
        var data = new byte[][] { [0x08 | 0x02], Px(0, 0, 0), [1], [0xF0, 0x10] };       // x 15, 2 wide in a 16 wide tile
        Assert.Throws<VncProtocolException>(() => VncDecoders.DecodeHextile(Reader(data), canvas, 0, 0, 16, 16));
    }

    // ---- zlib ----

    // A compressor that lives as long as the connection of a test, cut by a sync flush after each rectangle: what a server does.
    private sealed class Compressor : IDisposable
    {
        private readonly MemoryStream _out = new();
        private readonly ZLibStream _zlib;

        public Compressor() => _zlib = new ZLibStream(_out, CompressionLevel.Optimal, leaveOpen: true);

        public byte[] Rect(byte[] plain)
        {
            _out.SetLength(0);
            _zlib.Write(plain);
            _zlib.Flush();
            var data = _out.ToArray();
            return [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. data];
        }

        public void Dispose() => _zlib.Dispose();
    }

    [Fact]
    public void Zlib_decodes_raw_pixels_from_one_stream_across_rectangles()
    {
        using var compressor = new Compressor();
        using var inflation = new VncDecoders.Inflation();
        var canvas = new RfbCanvas(8, 8);
        var first = Repeat(Px(1, 2, 3), 16);
        VncDecoders.DecodeZlib(Reader(compressor.Rect(first)), canvas, inflation, 0, 0, 4, 4);
        Assert.Equal(((byte)1, (byte)2, (byte)3), At(canvas, 3, 3));

        // The second rectangle goes on in the same zlib stream (it refers back to the first one's data)
        var second = Repeat(Px(1, 2, 3), 8).Concat(Repeat(Px(9, 8, 7), 8)).ToArray();
        VncDecoders.DecodeZlib(Reader(compressor.Rect(second)), canvas, inflation, 4, 4, 4, 4);
        Assert.Equal(((byte)1, (byte)2, (byte)3), At(canvas, 4, 5));
        Assert.Equal(((byte)9, (byte)8, (byte)7), At(canvas, 7, 7));
    }

    // ---- ZRLE ----

    private static byte[] Zrle(Compressor compressor, params byte[][] tiles) => compressor.Rect(tiles.SelectMany(t => t).ToArray());

    [Fact]
    public void Zrle_decodes_every_kind_of_tile()
    {
        using var compressor = new Compressor();
        using var inflation = new VncDecoders.Inflation();
        // 130 x 66: tiles 64x64, 64x64, 2x64, then 64x2, 64x2, 2x2
        var canvas = new RfbCanvas(130, 66);
        List<byte[]> tiles =
        [
            // tile 1: one colour
            [1], Cpx(10, 20, 30),
            // tile 2: packed palette of 2 colours, 1 bit an index, row = 8 bytes; first row 0b10101010, the others 0
            [2], Cpx(1, 1, 1), Cpx(2, 2, 2),
        ];
        for (var row = 0; row < 64; row++)
        {
            tiles.Add(row == 0 ? Enumerable.Repeat((byte)0b10101010, 8).ToArray() : new byte[8]);
        }

        // tile 3 (2 x 64): raw
        tiles.Add([0]);
        for (var i = 0; i < 128; i++)
        {
            tiles.Add(Cpx((byte)i, 0, (byte)(255 - i)));
        }

        // tile 4 (64 x 2): plain RLE: 100 pixels of one colour, then 28 of another (a run of 28 = byte 27; 100 = byte 99)
        tiles.Add([128]);
        tiles.Add(Cpx(5, 5, 5)); tiles.Add([99]);
        tiles.Add(Cpx(6, 6, 6)); tiles.Add([27]);
        // tile 5 (64 x 2): palette RLE with three colours: index 0 single, index 1 run of 300 (255 + 44 + 1 -> bytes 255, 44), rest index 2 (run 127 -> 126)
        tiles.Add([128 + 3]);
        tiles.Add(Cpx(7, 7, 7)); tiles.Add(Cpx(8, 8, 8)); tiles.Add(Cpx(9, 9, 9));
        tiles.Add([0]);
        tiles.Add([128 | 1, 126]);       // 127 pixels of colour 8 -> with the single one: 128 = the tile
        // tile 6 (2 x 2): packed palette with 3 colours (2 bits an index)
        tiles.Add([3]); tiles.Add(Cpx(11, 0, 0)); tiles.Add(Cpx(12, 0, 0)); tiles.Add(Cpx(13, 0, 0));
        tiles.Add([0b00_01_0000]);       // row 0: indexes 0, 1
        tiles.Add([0b10_00_0000]);       // row 1: indexes 2, 0
        VncDecoders.DecodeZrle(Reader(Zrle(compressor, tiles.ToArray())), canvas, inflation, 0, 0, 130, 66);

        Assert.Equal(((byte)10, (byte)20, (byte)30), At(canvas, 5, 5));
        Assert.Equal(((byte)2, (byte)2, (byte)2), At(canvas, 64, 0));
        Assert.Equal(((byte)1, (byte)1, (byte)1), At(canvas, 65, 0));
        Assert.Equal(((byte)2, (byte)2, (byte)2), At(canvas, 66, 0));
        Assert.Equal(((byte)1, (byte)1, (byte)1), At(canvas, 64, 1));
        Assert.Equal(((byte)3, (byte)0, (byte)252), At(canvas, 129, 1));          // raw tile, 2 wide: row 1, column 1 is pixel number 3
        Assert.Equal(((byte)5, (byte)5, (byte)5), At(canvas, 0, 64));
        Assert.Equal(((byte)5, (byte)5, (byte)5), At(canvas, 35, 65));            // 100 pixels of the first colour: all of row 0 and row 1 up to column 35
        Assert.Equal(((byte)6, (byte)6, (byte)6), At(canvas, 36, 65));
        Assert.Equal(((byte)7, (byte)7, (byte)7), At(canvas, 64, 64));
        Assert.Equal(((byte)8, (byte)8, (byte)8), At(canvas, 65, 64));
        Assert.Equal(((byte)8, (byte)8, (byte)8), At(canvas, 127, 65));
        Assert.Equal(((byte)11, (byte)0, (byte)0), At(canvas, 128, 64));
        Assert.Equal(((byte)12, (byte)0, (byte)0), At(canvas, 129, 64));
        Assert.Equal(((byte)13, (byte)0, (byte)0), At(canvas, 128, 65));
        Assert.Equal(((byte)11, (byte)0, (byte)0), At(canvas, 129, 65));
    }

    [Fact]
    public void Zrle_goes_on_in_the_same_zlib_stream_in_the_next_rectangle()
    {
        using var compressor = new Compressor();
        using var inflation = new VncDecoders.Inflation();
        var canvas = new RfbCanvas(16, 16);
        VncDecoders.DecodeZrle(Reader(Zrle(compressor, [1], Cpx(1, 2, 3))), canvas, inflation, 0, 0, 16, 16);
        VncDecoders.DecodeZrle(Reader(Zrle(compressor, [1], Cpx(4, 5, 6))), canvas, inflation, 0, 0, 8, 8);
        VncDecoders.DecodeZrle(Reader(Zrle(compressor, [1], Cpx(7, 8, 9))), canvas, inflation, 8, 8, 8, 8);
        Assert.Equal(((byte)4, (byte)5, (byte)6), At(canvas, 7, 7));
        Assert.Equal(((byte)1, (byte)2, (byte)3), At(canvas, 12, 2));
        Assert.Equal(((byte)7, (byte)8, (byte)9), At(canvas, 15, 15));
    }

    [Fact]
    public void Zrle_refuses_damaged_data()
    {
        using var inflation = new VncDecoders.Inflation();
        var canvas = new RfbCanvas(16, 16);
        // length 5, but not zlib
        Assert.Throws<VncProtocolException>(() => VncDecoders.DecodeZrle(Reader([0, 0, 0, 5, 1, 2, 3, 4, 5]), canvas, inflation, 0, 0, 16, 16));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(100)]
    public void Zrle_refuses_a_palette_that_is_too_big_for_a_packed_tile(byte kind)
    {
        using var compressor = new Compressor();
        using var inflation = new VncDecoders.Inflation();
        var canvas = new RfbCanvas(16, 16);
        var data = Zrle(compressor, [kind], new byte[kind * 3], new byte[300]);
        Assert.Throws<VncProtocolException>(() => VncDecoders.DecodeZrle(Reader(data), canvas, inflation, 0, 0, 16, 16));
    }

    [Fact]
    public void Zrle_refuses_a_run_that_leaves_its_tile()
    {
        using var compressor = new Compressor();
        using var inflation = new VncDecoders.Inflation();
        var canvas = new RfbCanvas(4, 4);
        var data = Zrle(compressor, [128], Cpx(1, 1, 1), [254]);
        Assert.Throws<VncProtocolException>(() => VncDecoders.DecodeZrle(Reader(data), canvas, inflation, 0, 0, 4, 4));
    }
}
