using System.IO.Compression;

namespace JulOS.Integrations.Display.Refine;

/// <summary>
/// Ein Rechteck des Bildschirms (BGRX) als PNG mit Palette: ohne Verlust, und fuer Text, Rahmen und Flaechen winzig. Hat das Rechteck
/// mehr als 256 Farben (Foto, Video, Verlauf), gibt es keines: so etwas gehoert dem H.264-Strom, ein PNG waere gross und brachte nichts.
/// </summary>
internal static class TilePng
{
    public const int MaxColors = 256;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="bgrx">Pixel, 4 Byte je Pixel (B, G, R, X), Zeile fuer Zeile ohne Luecke (Breite * 4 je Zeile).</param>
    public static byte[]? Encode(ReadOnlySpan<byte> bgrx, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgrx.Length < width * height * 4)
        {
            throw new ArgumentException("Bild passt nicht zur Groesse.");
        }

        // Farbtabelle: offene Adressierung, 1024 Plaetze (hoechstens 256 Farben, also nie voll)
        Span<uint> keys = stackalloc uint[1024];
        Span<short> slots = stackalloc short[1024];
        slots.Fill(-1);
        Span<uint> palette = stackalloc uint[MaxColors];
        var colors = 0;
        var indices = new byte[(width + 1) * height];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            indices[at++] = 0;                                  // Filter "keiner"
            var row = bgrx.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var color = (uint)(row[x * 4] | (row[x * 4 + 1] << 8) | (row[x * 4 + 2] << 16));
                var slot = (int)((color * 2654435761u) >> 22);   // 10 Bit
                while (true)
                {
                    var found = slots[slot];
                    if (found < 0)
                    {
                        if (colors == MaxColors)
                        {
                            return null;
                        }

                        keys[slot] = color;
                        slots[slot] = (short)colors;
                        palette[colors] = color;
                        indices[at++] = (byte)colors;
                        colors++;
                        break;
                    }

                    if (keys[slot] == color)
                    {
                        indices[at++] = (byte)found;
                        break;
                    }

                    slot = (slot + 1) & 1023;
                }
            }
        }

        using var output = new MemoryStream(64 + colors * 3 + indices.Length / 4);
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8;      // Tiefe
        header[9] = 3;      // Farbart: Palette
        WriteChunk(output, "IHDR"u8, header);

        var plte = new byte[colors * 3];
        for (var i = 0; i < colors; i++)
        {
            plte[i * 3] = (byte)(palette[i] >> 16);
            plte[i * 3 + 1] = (byte)(palette[i] >> 8);
            plte[i * 3 + 2] = (byte)palette[i];
        }

        WriteChunk(output, "PLTE"u8, plte);

        using (var packed = new MemoryStream())
        {
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(indices);
            }

            WriteChunk(output, "IDAT"u8, packed.GetBuffer().AsSpan(0, (int)packed.Length));
        }

        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteInt(Span<byte> target, int at, int value)
    {
        target[at] = (byte)(value >> 24);
        target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8);
        target[at + 3] = (byte)value;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        WriteInt(number, 0, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        WriteInt(number, 0, (int)~crc);
        output.Write(number);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
