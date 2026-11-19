namespace JulOS.Integrations.Display.Video.Vaapi;

// Writes the bits of a parameter set or a slice header: fixed-width fields and Exp-Golomb codes, most significant bit first. Used for the
// "packed headers" a hardware encoder takes from the caller. ToNal() adds the start code, the NAL header and the emulation prevention bytes.
internal sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _current;
    private int _filled;

    public int BitLength => _bytes.Count * 8 + _filled;

    public bool IsAligned => _filled == 0;

    public void Bit(bool value) => Bits(value ? 1u : 0u, 1);

    public void Bits(uint value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            _current = (_current << 1) | (int)((value >> i) & 1);
            if (++_filled == 8)
            {
                _bytes.Add((byte)_current);
                _current = 0;
                _filled = 0;
            }
        }
    }

    // ue(v)
    public void Ue(uint value)
    {
        var x = (ulong)value + 1;
        var length = 64 - System.Numerics.BitOperations.LeadingZeroCount(x);
        Bits(0, length - 1);
        for (var i = length - 1; i >= 0; i--)
        {
            Bit(((x >> i) & 1) != 0);
        }
    }

    // se(v)
    public void Se(int value) => Ue(value > 0 ? (uint)(2 * value - 1) : (uint)(-2 * (long)value));

    // rbsp_trailing_bits(): a one, then zeros up to the byte boundary
    public void Trailing()
    {
        Bit(true);
        while (_filled != 0)
        {
            Bit(false);
        }
    }

    /// <summary>The RBSP so far, padded with zero bits to whole bytes.</summary>
    public byte[] ToArray()
    {
        var copy = new List<byte>(_bytes);
        if (_filled != 0)
        {
            copy.Add((byte)(_current << (8 - _filled)));
        }

        return [.. copy];
    }

    /// <summary>
    /// A NAL unit: start code 00 00 00 01, the NAL header (<paramref name="header"/>, one byte for H.264, two for HEVC), then the RBSP. With
    /// <paramref name="emulationPrevention"/> the RBSP gets its emulation prevention bytes (this is what goes into a stream); without, it is
    /// the raw form a packed header takes (the driver inserts them: has_emulation_bytes = 0). The bit length is exact, so a slice header
    /// that does not end on a byte boundary says so.
    /// </summary>
    public (byte[] Data, int Bits) ToNal(ReadOnlySpan<byte> header, bool emulationPrevention)
    {
        var rbsp = ToArray();
        var result = new List<byte>(rbsp.Length + 8) { 0, 0, 0, 1 };
        result.AddRange(header.ToArray());
        if (!emulationPrevention)
        {
            result.AddRange(rbsp);
            return ([.. result], 32 + header.Length * 8 + BitLength);
        }

        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                result.Add(3);
                zeros = 0;
            }

            result.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return ([.. result], result.Count * 8);
    }
}
