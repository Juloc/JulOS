using System.IO.Compression;

namespace JulOS.Integrations.Display.Vnc;

// The pictures a server sends for one rectangle, in the encodings this client asks for. The pixel format is fixed (32 bits, little endian,
// blue in the lowest byte: the same BGRX as the framebuffer), so a pixel is four bytes and a "compressed pixel" of ZRLE three.
internal static class VncDecoders
{
    public const int Raw = 0, CopyRect = 1, Rre = 2, Hextile = 5, Zlib = 6, Zrle = 16;

    public static void DecodeRaw(RfbReader reader, RfbCanvas canvas, int x, int y, int w, int h)
    {
        canvas.Check(x, y, w, h);
        for (var row = 0; row < h; row++)
        {
            reader.ReadExactly(canvas.Pixels.AsSpan((y + row) * canvas.Stride + x * 4, w * 4));
        }

        ForceOpaque(canvas, x, y, w, h);
    }

    // The fourth byte of a pixel is padding; whatever the server put there, the picture is opaque.
    private static void ForceOpaque(RfbCanvas canvas, int x, int y, int w, int h)
    {
        for (var row = 0; row < h; row++)
        {
            var at = (y + row) * canvas.Stride + x * 4 + 3;
            for (var column = 0; column < w; column++, at += 4)
            {
                canvas.Pixels[at] = 0xFF;
            }
        }
    }

    public static void DecodeCopyRect(RfbReader reader, RfbCanvas canvas, int x, int y, int w, int h)
    {
        var sourceX = reader.U16();
        var sourceY = reader.U16();
        CopyWithin(canvas, sourceX, sourceY, x, y, w, h);
    }

    public static void CopyWithin(RfbCanvas canvas, int sourceX, int sourceY, int x, int y, int w, int h)
    {
        canvas.Check(x, y, w, h);
        canvas.Check(sourceX, sourceY, w, h);
        var pixels = canvas.Pixels;
        var stride = canvas.Stride;
        // Overlapping areas: copy the rows in the order that does not overwrite what is still to be read (Span.CopyTo itself handles overlap within a row)
        if (sourceY >= y)
        {
            for (var row = 0; row < h; row++)
            {
                pixels.AsSpan((sourceY + row) * stride + sourceX * 4, w * 4).CopyTo(pixels.AsSpan((y + row) * stride + x * 4));
            }
        }
        else
        {
            for (var row = h - 1; row >= 0; row--)
            {
                pixels.AsSpan((sourceY + row) * stride + sourceX * 4, w * 4).CopyTo(pixels.AsSpan((y + row) * stride + x * 4));
            }
        }
    }

    // A background colour and then some coloured rectangles.
    public static void DecodeRre(RfbReader reader, RfbCanvas canvas, int x, int y, int w, int h)
    {
        canvas.Check(x, y, w, h);
        var count = reader.U32();
        var (b, g, r) = Pixel(reader);
        canvas.Fill(x, y, w, h, b, g, r);
        for (var i = 0u; i < count; i++)
        {
            var (sb, sg, sr) = Pixel(reader);
            var sx = reader.U16();
            var sy = reader.U16();
            var sw = reader.U16();
            var sh = reader.U16();
            canvas.Check(x + sx, y + sy, sw, sh);
            if (sx + sw > w || sy + sh > h)
            {
                throw new VncProtocolException("an RRE subrectangle outside its rectangle");
            }

            canvas.Fill(x + sx, y + sy, sw, sh, sb, sg, sr);
        }
    }

    private static (byte B, byte G, byte R) Pixel(RfbReader reader)
    {
        var b = reader.U8();
        var g = reader.U8();
        var r = reader.U8();
        reader.U8();
        return (b, g, r);
    }

    // 16 x 16 tiles, left to right, top to bottom. Background and foreground colours carry over from one tile to the next.
    public static void DecodeHextile(RfbReader reader, RfbCanvas canvas, int x, int y, int w, int h)
    {
        canvas.Check(x, y, w, h);
        (byte B, byte G, byte R) background = (0, 0, 0), foreground = (0, 0, 0);
        for (var ty = 0; ty < h; ty += 16)
        {
            for (var tx = 0; tx < w; tx += 16)
            {
                var tw = Math.Min(16, w - tx);
                var th = Math.Min(16, h - ty);
                var sub = reader.U8();
                if ((sub & 0x01) != 0)
                {
                    DecodeRaw(reader, canvas, x + tx, y + ty, tw, th);
                    continue;
                }

                if ((sub & 0x02) != 0)
                {
                    background = Pixel(reader);
                }

                if ((sub & 0x04) != 0)
                {
                    foreground = Pixel(reader);
                }

                canvas.Fill(x + tx, y + ty, tw, th, background.B, background.G, background.R);
                if ((sub & 0x08) == 0)
                {
                    continue;
                }

                var count = reader.U8();
                var coloured = (sub & 0x10) != 0;
                for (var i = 0; i < count; i++)
                {
                    var colour = coloured ? Pixel(reader) : foreground;
                    var position = reader.U8();
                    var size = reader.U8();
                    var sx = position >> 4;
                    var sy = position & 15;
                    var sw = (size >> 4) + 1;
                    var sh = (size & 15) + 1;
                    if (sx + sw > tw || sy + sh > th)
                    {
                        throw new VncProtocolException("a Hextile subrectangle outside its tile");
                    }

                    canvas.Fill(x + tx + sx, y + ty + sy, sw, sh, colour.B, colour.G, colour.R);
                }
            }
        }
    }

    // ---- zlib-compressed encodings: one zlib stream for the whole connection, cut into pieces by the rectangles ----

    // The inflater and the bytes of the rectangle it is fed from. Everything of a rectangle is read from the server first (its length comes
    // in front), so the inflater never reads the network itself and a half-sent rectangle cannot make it wait.
    internal sealed class Inflation : IDisposable
    {
        private readonly Feed _feed = new();
        private ZLibStream? _zlib;

        private sealed class Feed : Stream
        {
            private byte[] _data = [];
            private int _at;

            public void Set(byte[] data)
            {
                _data = data;
                _at = 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var take = Math.Min(buffer.Length, _data.Length - _at);
                _data.AsSpan(_at, take).CopyTo(buffer);
                _at += take;
                return take;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // The next piece of the stream; what the caller reads from the result is the decompressed data of this rectangle.
        public Stream Next(byte[] compressed)
        {
            _feed.Set(compressed);
            _zlib ??= new ZLibStream(_feed, CompressionMode.Decompress, leaveOpen: true);
            return _zlib;
        }

        public void Dispose() => _zlib?.Dispose();
    }

    // Reads exactly the bytes from the decompressed data of a rectangle; running out means the data is damaged.
    private sealed class Pieces(Stream source)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _position;
        private int _length;

        private void Fill()
        {
            _length = source.Read(_buffer, 0, _buffer.Length);
            _position = 0;
            if (_length <= 0)
            {
                throw new VncProtocolException("compressed data that ends early");
            }
        }

        public byte U8()
        {
            if (_position == _length)
            {
                Fill();
            }

            return _buffer[_position++];
        }

        public void ReadExactly(Span<byte> destination)
        {
            while (destination.Length > 0)
            {
                if (_position == _length)
                {
                    Fill();
                }

                var take = Math.Min(destination.Length, _length - _position);
                _buffer.AsSpan(_position, take).CopyTo(destination);
                _position += take;
                destination = destination[take..];
            }
        }
    }

    private const int MaxCompressed = 64 * 1024 * 1024;

    private static byte[] ReadCompressed(RfbReader reader)
    {
        var length = reader.U32();
        if (length > MaxCompressed)
        {
            throw new VncProtocolException("a compressed rectangle of " + length + " bytes");
        }

        return reader.Bytes((int)length);
    }

    // Encoding 6: the raw pixels of the rectangle, compressed.
    public static void DecodeZlib(RfbReader reader, RfbCanvas canvas, Inflation inflation, int x, int y, int w, int h)
    {
        canvas.Check(x, y, w, h);
        var pieces = new Pieces(inflation.Next(ReadCompressed(reader)));
        for (var row = 0; row < h; row++)
        {
            pieces.ReadExactly(canvas.Pixels.AsSpan((y + row) * canvas.Stride + x * 4, w * 4));
        }

        ForceOpaque(canvas, x, y, w, h);
    }

    // Encoding 16: 64 x 64 tiles, each as raw, one colour, a palette (packed or run-length coded) or plain run-length coded pixels.
    // Pixels are "CPIXELs" of three bytes: with 24 colour bits in a 32-bit pixel the unused byte is left out.
    public static void DecodeZrle(RfbReader reader, RfbCanvas canvas, Inflation inflation, int x, int y, int w, int h)
    {
        canvas.Check(x, y, w, h);
        var data = new Pieces(inflation.Next(ReadCompressed(reader)));
        Span<byte> palette = stackalloc byte[127 * 3];
        for (var ty = 0; ty < h; ty += 64)
        {
            for (var tx = 0; tx < w; tx += 64)
            {
                var tw = Math.Min(64, w - tx);
                var th = Math.Min(64, h - ty);
                var type = data.U8();
                var paletteSize = type & 0x7F;
                var rle = (type & 0x80) != 0;
                if ((!rle && paletteSize > 16) || (rle && paletteSize == 1))
                {
                    throw new VncProtocolException("a ZRLE tile of unknown kind " + type);
                }

                if (paletteSize > 0)
                {
                    data.ReadExactly(palette[..(paletteSize * 3)]);
                }

                var px = x + tx;
                var py = y + ty;
                if (!rle)
                {
                    ZrleTile(data, canvas, px, py, tw, th, paletteSize, palette);
                }
                else
                {
                    ZrleRuns(data, canvas, px, py, tw, th, paletteSize, palette);
                }
            }
        }
    }

    // Raw pixels (no palette), one colour (palette of one), or indexes packed into 1, 2 or 4 bits each, every row starting on a byte.
    private static void ZrleTile(Pieces data, RfbCanvas canvas, int px, int py, int tw, int th, int paletteSize, ReadOnlySpan<byte> palette)
    {
        if (paletteSize == 1)
        {
            canvas.Fill(px, py, tw, th, palette[0], palette[1], palette[2]);
            return;
        }

        Span<byte> pixel = stackalloc byte[3];
        var bits = paletteSize <= 2 ? 1 : paletteSize <= 4 ? 2 : 4;
        for (var row = 0; row < th; row++)
        {
            var at = (py + row) * canvas.Stride + px * 4;
            var current = 0;
            var left = 0;
            for (var column = 0; column < tw; column++, at += 4)
            {
                if (paletteSize == 0)
                {
                    data.ReadExactly(pixel);
                    SetPixel(canvas.Pixels, at, pixel);
                    continue;
                }

                if (left == 0)
                {
                    current = data.U8();
                    left = 8;
                }

                left -= bits;
                var index = (current >> left) & ((1 << bits) - 1);
                if (index >= paletteSize)
                {
                    throw new VncProtocolException("a ZRLE palette index out of range");
                }

                SetPixel(canvas.Pixels, at, palette.Slice(index * 3, 3));
            }
        }
    }

    // Run-length coded: without a palette a pixel and its run length; with one an index, and with the top bit set a run length after it.
    private static void ZrleRuns(Pieces data, RfbCanvas canvas, int px, int py, int tw, int th, int paletteSize, ReadOnlySpan<byte> palette)
    {
        Span<byte> pixel = stackalloc byte[3];
        var total = tw * th;
        var done = 0;
        while (done < total)
        {
            int run;
            if (paletteSize == 0)
            {
                data.ReadExactly(pixel);
                run = RunLength(data);
                done = Paint(canvas, px, py, tw, th, done, run, pixel);
                continue;
            }

            var entry = data.U8();
            var index = entry & 0x7F;
            if (index >= paletteSize)
            {
                throw new VncProtocolException("a ZRLE palette index out of range");
            }

            run = (entry & 0x80) != 0 ? RunLength(data) : 1;
            done = Paint(canvas, px, py, tw, th, done, run, palette.Slice(index * 3, 3));
        }
    }

    private static void SetPixel(byte[] pixels, int at, ReadOnlySpan<byte> bgr)
    {
        pixels[at] = bgr[0];
        pixels[at + 1] = bgr[1];
        pixels[at + 2] = bgr[2];
        pixels[at + 3] = 0xFF;
    }

    // Each byte adds to the length; 255 says another byte follows. The length is one more than the sum.
    private static int RunLength(Pieces data)
    {
        var length = 1;
        byte next;
        do
        {
            next = data.U8();
            length += next;
        }
        while (next == 255);
        return length;
    }

    // Paints `run` pixels of a tile in reading order, starting with the `done`th; returns the new count.
    private static int Paint(RfbCanvas canvas, int px, int py, int tw, int th, int done, int run, ReadOnlySpan<byte> colour)
    {
        if (done + run > tw * th)
        {
            throw new VncProtocolException("a ZRLE run that leaves its tile");
        }

        for (var i = 0; i < run; i++, done++)
        {
            SetPixel(canvas.Pixels, (py + done / tw) * canvas.Stride + (px + done % tw) * 4, colour);
        }

        return done;
    }
}
