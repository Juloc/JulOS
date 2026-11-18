namespace JulOS.Backend.Logic;

/// <summary>Base32 nach RFC 4648 (ohne Auffuellzeichen), wie Authenticator-Apps es erwarten.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
            value &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var result = new List<byte>(text.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var ch in text)
        {
            if (ch is ' ' or '=' or '-') continue;
            var i = Alphabet.IndexOf(char.ToUpperInvariant(ch), StringComparison.Ordinal);
            if (i < 0) throw new FormatException("Ungueltiges Base32-Zeichen.");
            value = (value << 5) | i;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((value >> (bits - 8)) & 255));
                bits -= 8;
            }
            value &= (1 << bits) - 1;
        }
        return [.. result];
    }
}
