using System.Text;

namespace JulOS.Integrations.Guacamole;

/// <summary>
/// Das Guacamole-Protokoll: eine Anweisung ist <c>LAENGE.Wert,LAENGE.Wert;</c>. Die Laenge zaehlt Unicode-Zeichen (nicht Bytes, nicht UTF-16-Einheiten).
/// </summary>
public static class GuacProtocol
{
    /// <summary>Baut eine Anweisung als UTF-8. Ein leerer Opcode ist die interne Anweisung zwischen Server und Browser-Tunnel.</summary>
    public static byte[] Encode(string opcode, params string[] args)
    {
        var sb = new StringBuilder();
        Element(sb, opcode);
        foreach (var a in args) { sb.Append(','); Element(sb, a); }
        sb.Append(';');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Element(StringBuilder sb, string value) => sb.Append(CodePoints(value)).Append('.').Append(value);

    public static int CodePoints(string s)
    {
        var n = 0;
        foreach (var c in s) if (!char.IsLowSurrogate(c)) n++;
        return n;
    }

    /// <summary>Zerlegt genau eine vollstaendige Anweisung (mit Semikolon am Ende) in Opcode und Argumente.</summary>
    public static string[] Parse(ReadOnlySpan<byte> instruction)
    {
        var text = Encoding.UTF8.GetString(instruction);
        var result = new List<string>();
        var i = 0;
        while (true)
        {
            var dot = text.IndexOf('.', i);
            if (dot < 0 || !int.TryParse(text.AsSpan(i, dot - i), out var count) || count < 0) throw new InvalidDataException("Ungültige Anweisung.");
            var j = dot + 1;
            for (var k = 0; k < count; k++)
            {
                if (j >= text.Length) throw new InvalidDataException("Ungültige Anweisung.");
                j += char.IsHighSurrogate(text[j]) ? 2 : 1;
            }
            if (j > text.Length) throw new InvalidDataException("Ungültige Anweisung.");
            result.Add(text.Substring(dot + 1, j - dot - 1));
            if (j >= text.Length) throw new InvalidDataException("Ungültige Anweisung.");
            var sep = text[j];
            i = j + 1;
            if (sep == ';') return i == text.Length ? [.. result] : throw new InvalidDataException("Mehr als eine Anweisung.");
            if (sep != ',') throw new InvalidDataException("Ungültige Anweisung.");
        }
    }

    /// <summary>Ob die Nachricht die interne Anweisung des Browser-Tunnels ist (leerer Opcode, zum Beispiel der Ping).</summary>
    public static bool IsInternal(ReadOnlySpan<byte> message) => message.StartsWith("0.,"u8);
}

/// <summary>
/// Findet in einem Bytestrom die Grenzen vollstaendiger Anweisungen, ohne zu kopieren. Der Zustand bleibt zwischen den Aufrufen erhalten,
/// ein Aufruf darf also mitten in einer Anweisung enden. Wichtig, weil der Browser nur ganze Anweisungen pro Nachricht verarbeitet
/// und ein Semikolon auch innerhalb eines Werts vorkommen kann (zum Beispiel in kopiertem Text).
/// </summary>
public struct InstructionScanner
{
    private const int MaxLengthDigits = 8;
    private enum State : byte { Digits, Value }
    private State _state;
    private int _length;      // Zeichen, die im aktuellen Wert noch kommen
    private int _digits;

    /// <returns>Index des letzten Semikolons einer fertigen Anweisung in <paramref name="data"/>, oder -1 wenn keine endete.</returns>
    /// <exception cref="InvalidDataException">Der Strom ist kein gueltiges Guacamole-Protokoll.</exception>
    public int Scan(ReadOnlySpan<byte> data)
    {
        var last = -1;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (_state == State.Digits)
            {
                if (b >= (byte)'0' && b <= (byte)'9')
                {
                    if (++_digits > MaxLengthDigits) throw new InvalidDataException("Elementlänge zu groß.");
                    _length = _length * 10 + (b - '0');
                }
                else if (b == (byte)'.' && _digits > 0) { _state = State.Value; _digits = 0; }
                else throw new InvalidDataException("Ungültige Anweisung.");
                continue;
            }

            // Wert: Fortsetzungsbytes (10xxxxxx) gehoeren zum vorigen Zeichen; ein anderes Byte beginnt ein Zeichen
            if ((b & 0xC0) == 0x80) continue;
            if (_length > 0) { _length--; continue; }
            // Wert ist vollstaendig, dieses Byte muss das Trennzeichen sein
            if (b == (byte)',') { _state = State.Digits; _length = 0; }
            else if (b == (byte)';') { _state = State.Digits; _length = 0; last = i; }
            else throw new InvalidDataException("Ungültige Anweisung.");
        }
        return last;
    }
}
