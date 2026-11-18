using System.Text;
using JulOS.Integrations.Guacamole;

namespace JulOS.Backend.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Encode_counts_unicode_characters_not_bytes_or_utf16_units()
    {
        Assert.Equal("6.select,3.rdp;", Encoding.UTF8.GetString(GuacProtocol.Encode("select", "rdp")));
        Assert.Equal("5.error,2.ä😀,3.512;", Encoding.UTF8.GetString(GuacProtocol.Encode("error", "ä😀", "512")));
        Assert.Equal("0.,4.abcd;", Encoding.UTF8.GetString(GuacProtocol.Encode("", "abcd")));
        Assert.Equal("5.audio;", Encoding.UTF8.GetString(GuacProtocol.Encode("audio")));
    }

    [Theory]
    [InlineData("select", "rdp")]
    [InlineData("clipboard", "a,b;c.d", "mit ; und , und . drin")]
    [InlineData("x", "", "", "leer davor")]
    [InlineData("blob", "😀😀", "äöü ß 漢字")]
    public void Parse_reverses_Encode(params string[] parts)
    {
        var bytes = GuacProtocol.Encode(parts[0], parts[1..]);
        Assert.Equal(parts, GuacProtocol.Parse(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("6.select,3.rdp")]            // kein Ende
    [InlineData("6.select,3.rdp;x")]          // mehr als eine
    [InlineData("a.select;")]                 // Laenge keine Zahl
    [InlineData("9.select;")]                 // Laenge zu gross
    [InlineData("6.select,3.rdp!")]
    public void Parse_rejects_broken_input(string text) => Assert.Throws<InvalidDataException>(() => GuacProtocol.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Scanner_finds_the_end_of_every_instruction_even_with_semicolons_inside_values()
    {
        var data = new List<byte>();
        data.AddRange(GuacProtocol.Encode("clipboard", "a;b;c"));
        data.AddRange(GuacProtocol.Encode("sync", "123"));
        data.AddRange(GuacProtocol.Encode("text", "漢字;😀"));
        var all = data.ToArray();

        // egal wo der Strom zerschnitten wird: die Grenze der letzten ganzen Anweisung stimmt immer
        for (var cut = 1; cut < all.Length; cut++)
        {
            var s = new InstructionScanner();
            var first = s.Scan(all.AsSpan(0, cut));
            var second = s.Scan(all.AsSpan(cut));
            Assert.Equal(all.Length - 1, second >= 0 ? cut + second : first);
        }
    }

    [Fact]
    public void Scanner_counts_each_instruction_when_fed_byte_by_byte()
    {
        var all = GuacProtocol.Encode("a", "ä;ö").Concat(GuacProtocol.Encode("b")).ToArray();
        var s = new InstructionScanner();
        var ends = new List<int>();
        for (var i = 0; i < all.Length; i++) if (s.Scan(all.AsSpan(i, 1)) >= 0) ends.Add(i);
        Assert.Equal(2, ends.Count);
        Assert.Equal(all.Length - 1, ends[1]);
    }

    [Theory]
    [InlineData("x.a;")]
    [InlineData("1.ab;")]                    // Wert laenger als angegeben
    [InlineData("123456789.a;")]             // zu viele Ziffern
    [InlineData(".a;")]
    public void Scanner_rejects_broken_streams(string text)
    {
        var s = new InstructionScanner();
        Assert.Throws<InvalidDataException>(() => s.Scan(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Internal_instruction_is_recognised()
    {
        Assert.True(GuacProtocol.IsInternal(GuacProtocol.Encode("", "ping", "123")));
        Assert.False(GuacProtocol.IsInternal(GuacProtocol.Encode("mouse", "1", "2", "0")));
    }

    [Fact]
    public async Task Reader_returns_whole_instructions_even_when_the_stream_is_cut_up()
    {
        var all = GuacProtocol.Encode("img", "1", "漢;字").Concat(GuacProtocol.Encode("sync", "5")).Concat(GuacProtocol.Encode("nop")).ToArray();
        var pipe = new ChoppedStream(all, chunk: 3);
        using var reader = new GuacReader(pipe);
        var instructions = new List<string[]>();
        while (true)
        {
            var block = await reader.ReadBlockAsync(CancellationToken.None);
            if (block.IsEmpty) break;
            var text = Encoding.UTF8.GetString(block.Span);
            Assert.EndsWith(";", text);                       // nie mitten in einer Anweisung
            // jeder Block laesst sich vollstaendig lesen
            var s = new InstructionScanner();
            Assert.Equal(block.Length - 1, s.Scan(block.Span));
            instructions.Add(GuacProtocol.Parse(block.Span[..(block.Span.IndexOf((byte)';') >= 0 ? FirstEnd(block.Span) : block.Length)]));
        }
        Assert.Equal(new[] { "img", "sync", "nop" }, instructions.Select(i => i[0]));
    }

    private static int FirstEnd(ReadOnlySpan<byte> data)
    {
        var s = new InstructionScanner();
        for (var i = 0; i < data.Length; i++) if (s.Scan(data.Slice(i, 1)) >= 0) return i + 1;
        return data.Length;
    }

    /// <summary>Gibt die Daten in kleinen Stuecken zurueck, wie ein Netzwerk es tun kann.</summary>
    private sealed class ChoppedStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _pos);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
