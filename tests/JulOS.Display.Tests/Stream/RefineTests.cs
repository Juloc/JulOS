using System.IO.Compression;
using JulOS.Integrations.Display.Refine;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Die verlustfreie Nachbesserung ruhender Teile: PNG-Kodierung und Buchfuehrung der Kacheln, ohne Server und ohne Browser.</summary>
public sealed class RefineTests
{
    // Minimaler PNG-Leser nur fuer das, was TilePng schreibt (Palette, 8 Bit, Filter 0): gibt BGRX zurueck
    private static (int Width, int Height, byte[] Bgrx) Decode(byte[] png)
    {
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        int width = 0, height = 0;
        byte[] palette = [];
        using var idat = new MemoryStream();
        var at = 8;
        while (at < png.Length)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                Assert.Equal(8, data[8]);
                Assert.Equal(3, data[9]);
            }
            else if (type == "PLTE")
            {
                palette = data.ToArray();
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            // Die Pruefsumme stimmt (sonst lehnt der Browser das Bild ab)
            var crc = Crc(png.AsSpan(at + 4, 4 + length));
            Assert.Equal([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc], png[(at + 8 + length)..(at + 12 + length)]);
            at += 12 + length;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var indices = raw.ToArray();
        var bgrx = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, indices[y * (width + 1)]);
            for (var x = 0; x < width; x++)
            {
                var i = indices[y * (width + 1) + 1 + x] * 3;
                var o = (y * width + x) * 4;
                bgrx[o] = palette[i + 2];
                bgrx[o + 1] = palette[i + 1];
                bgrx[o + 2] = palette[i];
            }
        }

        return (width, height, bgrx);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] Text(int width, int height)
    {
        // Weisser Grund, ein farbiger "Strich" mit weichen Kanten: wenige Farben wie Text
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var v = (byte)(255 - Math.Clamp(255 - Math.Abs(x - y) * 40, 0, 255));
                pixels[o] = 255;
                pixels[o + 1] = v;
                pixels[o + 2] = v;
            }
        }

        return pixels;
    }

    [Fact]
    public void A_picture_with_few_colors_survives_the_png_exactly()
    {
        var pixels = Text(48, 32);
        var png = TilePng.Encode(pixels, 48, 32);
        Assert.NotNull(png);
        var (width, height, decoded) = Decode(png!);
        Assert.Equal((48, 32), (width, height));
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(pixels[i], decoded[i]);
            Assert.Equal(pixels[i + 1], decoded[i + 1]);
            Assert.Equal(pixels[i + 2], decoded[i + 2]);
        }

        Assert.True(png!.Length < pixels.Length / 8, $"{png.Length} Bytes fuer {pixels.Length} Rohbytes");
    }

    [Fact]
    public void A_picture_with_more_than_256_colors_gets_no_png()
    {
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < 32 * 32; i++)
        {
            pixels[i * 4] = (byte)i;
            pixels[i * 4 + 1] = (byte)(i >> 2);
            pixels[i * 4 + 2] = (byte)(i * 7);
        }

        Assert.Null(TilePng.Encode(pixels, 32, 32));
    }

    [Fact]
    public void A_tile_is_refined_only_after_it_was_quiet_and_then_only_once()
    {
        var refiner = new TileRefiner(128, 64, quietMilliseconds: 100);
        refiner.Mark(0, 0, 40, 10, now: 1000);
        Assert.Equal(2, refiner.DirtyTiles);            // 40 Pixel Breite: zwei Kacheln
        Assert.Empty(refiner.TakeQuiet(1050, 100));     // noch nicht ruhig
        Assert.Equal(1100, refiner.NextDue());

        var run = Assert.Single(refiner.TakeQuiet(1100, 100));
        Assert.Equal(new TileRect(0, 0, 64, 32), run);
        Assert.Equal(0, refiner.DirtyTiles);
        Assert.Null(refiner.NextDue());
        Assert.Empty(refiner.TakeQuiet(5000, 100));
    }

    [Fact]
    public void A_change_that_keeps_coming_keeps_the_tile_waiting()
    {
        var refiner = new TileRefiner(64, 64, quietMilliseconds: 100);
        for (var t = 0; t < 1000; t += 30)
        {
            refiner.Mark(0, 0, 10, 10, 1000 + t);
            Assert.Empty(refiner.TakeQuiet(1000 + t + 30, 100));
        }
    }

    [Fact]
    public void A_refined_tile_that_changes_again_has_to_be_cleared_before_the_next_picture_and_is_refined_again()
    {
        var refiner = new TileRefiner(128, 64, quietMilliseconds: 100);
        refiner.Mark(0, 0, 128, 64, 1000);
        Assert.Equal(2, refiner.TakeQuiet(1200, 100).Count);             // zwei Zeilen zu je vier Kacheln
        Assert.Empty(refiner.TakeInvalidations());                        // nichts hat sich seitdem geaendert

        refiner.Mark(40, 40, 4, 4, 1300);                                 // eine Kachel (Spalte 1, Zeile 1)
        var clear = Assert.Single(refiner.TakeInvalidations());
        Assert.Equal(new TileRect(32, 32, 32, 32), clear);
        Assert.Empty(refiner.TakeInvalidations());                        // schon gemeldet

        var again = Assert.Single(refiner.TakeQuiet(1400, 100));          // und sie wird wieder nachgebessert
        Assert.Equal(new TileRect(32, 32, 32, 32), again);
    }

    [Fact]
    public void Tiles_in_a_row_are_joined_up_to_the_longest_run_and_the_tile_budget_is_kept()
    {
        var refiner = new TileRefiner(32 * 30, 32, quietMilliseconds: 10);
        refiner.Mark(0, 0, 32 * 30, 32, 1);
        var runs = refiner.TakeQuiet(100, maxTiles: 25, maxRun: 20);
        Assert.Equal([20, 5], runs.Select(r => r.Width / 32).ToArray());
        Assert.Equal(5, refiner.DirtyTiles);
    }

    [Fact]
    public void A_picture_the_encoder_cannot_use_is_marked_as_not_refined()
    {
        var refiner = new TileRefiner(64, 32, quietMilliseconds: 10);
        refiner.Mark(0, 0, 64, 32, 1);
        var run = Assert.Single(refiner.TakeQuiet(100, 10));
        refiner.Forget(run);                                              // zu viele Farben: das Video bleibt, nichts ist zu streichen
        refiner.Mark(0, 0, 64, 32, 200);
        Assert.Empty(refiner.TakeInvalidations());
    }

    [Fact]
    public void The_messages_have_the_documented_layout()
    {
        var target = new byte[64];
        var length = ScreenProtocol.WriteRefine(target, new ScreenRefine(0x0102, 0x0304, 0x0506, 0x0708, [9, 8, 7]));
        Assert.Equal([40, 1, 2, 3, 4, 5, 6, 7, 8, 9, 8, 7], target[..length]);

        length = ScreenProtocol.WriteRefineClear(target, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal([41, 0, 2, 0, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 7, 0, 8], target[..length]);
    }
}
