using System.Buffers.Binary;
using System.Text;

namespace JulOS.Integrations.Display;

/// <summary>
/// Schreibt ein ZIP-Archiv (ohne Kompression) als Strom in einen Ausgabestrom, der nur vorwaerts geht (HTTP-Antwort): jede Datei kommt in Stuecken, ihre
/// Pruefsumme und Groesse stehen erst hinter den Daten (Datenbeschreibung, Bit 3). Nichts wird zwischengespeichert; der Speicher ist unabhaengig von der
/// Groesse der Dateien. Gruppen ueber 4 GiB und ueber 65535 Eintraege gehen als ZIP64. Namen sind UTF-8.
/// Der Aufrufer prueft die Namen (relativ, ohne "..", siehe <see cref="ClipboardNames.SafePath"/>); hier werden sie nur in das Archiv geschrieben.
/// </summary>
public sealed class ClipboardZipWriter
{
    private const uint LocalSignature = 0x04034B50, DescriptorSignature = 0x08074B50, CentralSignature = 0x02014B50;
    private const uint EndSignature = 0x06054B50, Zip64EndSignature = 0x06064B50, Zip64LocatorSignature = 0x07064B50;
    private const ushort FlagDescriptor = 0x0008, FlagUtf8 = 0x0800;
    private const long Big = 0xFFFFFFFFL;

    private sealed record Entry(string Name, uint Crc, long CompressedSize, long Size, long Offset, bool IsDirectory, bool Zip64, ushort Time, ushort Date);

    private readonly Stream _out;
    private readonly List<Entry> _entries = [];
    private long _position;
    private bool _finished;

    // der Eintrag, der gerade geschrieben wird
    private string? _name;
    private long _start, _written;
    private uint _crc;
    private bool _zip64;
    private ushort _time, _date;

    public ClipboardZipWriter(Stream output)
    {
        _out = output;
    }

    /// <summary>Die Anzahl der Eintraege bisher.</summary>
    public int Count => _entries.Count;

    /// <summary>Wie viele Bytes bisher ausgegeben wurden.</summary>
    public long Position => _position;

    /// <summary>Ein Ordner (der Name bekommt einen Schraegstrich am Ende).</summary>
    public async Task AddDirectoryAsync(string name, DateTime? time = null, CancellationToken ct = default)
    {
        EnsureIdle();
        var path = name.TrimEnd('/') + "/";
        var (dosTime, dosDate) = Dos(time);
        var offset = _position;
        var nameBytes = Encoding.UTF8.GetBytes(path);
        var header = LocalHeader(nameBytes, FlagUtf8, 0, 0, 0, zip64: false, dosTime, dosDate);
        await WriteAsync(header, ct).ConfigureAwait(false);
        _entries.Add(new Entry(path, 0, 0, 0, offset, true, false, dosTime, dosDate));
    }

    /// <summary>Beginnt eine Datei. size: die Groesse, wenn bekannt (bis 4 GiB ein gewoehnlicher Eintrag), sonst -1 (dann ZIP64, die Groesse darf beliebig sein).</summary>
    public async Task BeginFileAsync(string name, long size, DateTime? time = null, CancellationToken ct = default)
    {
        EnsureIdle();
        (_time, _date) = Dos(time);
        _name = name;
        _start = _position;
        _written = 0;
        _crc = 0;
        _zip64 = size < 0 || size >= Big;
        var header = LocalHeader(Encoding.UTF8.GetBytes(name), (ushort)(FlagUtf8 | FlagDescriptor), 0, 0, 0, _zip64, _time, _date);
        await WriteAsync(header, ct).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_name is null)
        {
            throw new InvalidOperationException("Keine Datei begonnen.");
        }

        _crc = Crc32.Update(_crc, data.Span);
        _written += data.Length;
        await WriteAsync(data, ct).ConfigureAwait(false);
    }

    public async Task EndFileAsync(CancellationToken ct = default)
    {
        if (_name is null)
        {
            throw new InvalidOperationException("Keine Datei begonnen.");
        }

        // Eine Datei, die sich als klein ausgab und dann doch ueber 4 GiB lang wurde, kann nicht mehr auf ZIP64 wechseln: abbrechen statt ein kaputtes Archiv zu liefern
        if (!_zip64 && _written >= Big)
        {
            throw new IOException("Die Datei ist länger als angekündigt.");
        }

        var descriptor = new byte[_zip64 ? 24 : 16];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, DescriptorSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), _crc);
        if (_zip64)
        {
            BinaryPrimitives.WriteInt64LittleEndian(descriptor.AsSpan(8), _written);
            BinaryPrimitives.WriteInt64LittleEndian(descriptor.AsSpan(16), _written);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), (uint)_written);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), (uint)_written);
        }

        await WriteAsync(descriptor, ct).ConfigureAwait(false);
        _entries.Add(new Entry(_name, _crc, _written, _written, _start, false, _zip64, _time, _date));
        _name = null;
    }

    /// <summary>Schreibt das Verzeichnis am Ende; danach ist das Archiv fertig.</summary>
    public async Task FinishAsync(CancellationToken ct = default)
    {
        EnsureIdle();
        if (_finished)
        {
            return;
        }

        _finished = true;
        var directoryStart = _position;
        foreach (var entry in _entries)
        {
            await WriteAsync(CentralHeader(entry), ct).ConfigureAwait(false);
        }

        var directorySize = _position - directoryStart;
        var needZip64 = _entries.Count >= 0xFFFF || directoryStart >= Big || directorySize >= Big;
        if (needZip64)
        {
            var end64Offset = _position;
            var record = new byte[56 + 20];
            BinaryPrimitives.WriteUInt32LittleEndian(record, Zip64EndSignature);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(4), 44);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 45);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 45);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), _entries.Count);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(32), _entries.Count);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), directorySize);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(48), directoryStart);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56), Zip64LocatorSignature);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(64), end64Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(72), 1);
            await WriteAsync(record, ct).ConfigureAwait(false);
        }

        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), needZip64 ? (ushort)0xFFFF : (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), needZip64 ? (ushort)0xFFFF : (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), needZip64 ? 0xFFFFFFFF : (uint)directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), needZip64 ? 0xFFFFFFFF : (uint)directoryStart);
        await WriteAsync(end, ct).ConfigureAwait(false);
        await _out.FlushAsync(ct).ConfigureAwait(false);
    }

    private void EnsureIdle()
    {
        if (_name is not null)
        {
            throw new InvalidOperationException("Die vorige Datei ist noch nicht beendet.");
        }

        if (_finished)
        {
            throw new InvalidOperationException("Das Archiv ist schon fertig.");
        }
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _out.WriteAsync(data, ct).ConfigureAwait(false);
        _position += data.Length;
    }

    private static (ushort Time, ushort Date) Dos(DateTime? time)
    {
        var t = time ?? DateTime.UtcNow;
        if (t.Year < 1980)
        {
            t = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }

    private static byte[] LocalHeader(byte[] name, ushort flags, uint crc, long compressed, long size, bool zip64, ushort time, ushort date)
    {
        var extra = zip64 ? 20 : 0;
        var header = new byte[30 + name.Length + extra];
        BinaryPrimitives.WriteUInt32LittleEndian(header, LocalSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), zip64 ? (ushort)45 : (ushort)20);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 0);       // gespeichert, nicht komprimiert
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), time);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), date);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), zip64 ? 0xFFFFFFFF : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), zip64 ? 0xFFFFFFFF : (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)extra);
        name.CopyTo(header, 30);
        if (zip64)
        {
            var at = 30 + name.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(at), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(at + 2), 16);
            // die Groessen stehen hinter den Daten in der Datenbeschreibung
        }

        return header;
    }

    private static byte[] CentralHeader(Entry e)
    {
        var name = Encoding.UTF8.GetBytes(e.Name);
        var sizeBig = e.Size >= Big || e.CompressedSize >= Big;
        var offsetBig = e.Offset >= Big;
        var extraLength = (sizeBig ? 16 : 0) + (offsetBig ? 8 : 0);
        var extra = extraLength > 0 ? extraLength + 4 : 0;
        var header = new byte[46 + name.Length + extra];
        var zip64 = sizeBig || offsetBig;
        BinaryPrimitives.WriteUInt32LittleEndian(header, CentralSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), zip64 ? (ushort)45 : (ushort)20);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)(e.Zip64 || zip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)(e.IsDirectory ? FlagUtf8 : FlagUtf8 | FlagDescriptor));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), e.Time);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), e.Date);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), e.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), sizeBig ? 0xFFFFFFFF : (uint)e.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), sizeBig ? 0xFFFFFFFF : (uint)e.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), (ushort)extra);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(38), e.IsDirectory ? 0x10u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), offsetBig ? 0xFFFFFFFF : (uint)e.Offset);
        name.CopyTo(header, 46);
        if (extra > 0)
        {
            var at = 46 + name.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(at), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(at + 2), (ushort)extraLength);
            at += 4;
            if (sizeBig)
            {
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(at), e.Size);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(at + 8), e.CompressedSize);
                at += 16;
            }

            if (offsetBig)
            {
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(at), e.Offset);
            }
        }

        return header;
    }

    /// <summary>CRC-32 (IEEE, wie ZIP ihn verlangt), fortlaufend ueber Stuecke.</summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            var c = ~crc;
            foreach (var b in data)
            {
                c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            return ~c;
        }
    }
}
