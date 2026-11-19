namespace JulOS.Integrations.Display.Vnc;

// Keys as the VNC protocol names them: X11 keysyms. A character is its Unicode value for Latin-1 and 0x01000000 plus the value for everything else;
// the keys without a character have numbers of their own (0xFFxx). The scancode table is the German layout, which is the one the browser assumes
// for scancodes (stream.js: "de"); typed characters do not go through it at all, they arrive as keysyms or code points.
public static class VncKeys
{
    public const uint Return = 0xFF0D, Tab = 0xFF09, BackSpace = 0xFF08, Escape = 0xFF1B, ShiftL = 0xFFE1, ControlL = 0xFFE3, AltL = 0xFFE9, DeleteKey = 0xFFFF;

    // The keysym of a character. Control characters that a keyboard has a key for (Enter, Tab, Backspace) become that key; other controls have none (0).
    public static uint FromCodePoint(int codePoint)
    {
        if (codePoint is '\r' or '\n')
        {
            return Return;
        }

        if (codePoint == '\t')
        {
            return Tab;
        }

        if (codePoint == '\b')
        {
            return BackSpace;
        }

        if (codePoint == 0x7F)
        {
            return DeleteKey;
        }

        if (codePoint is < 0x20 or > 0x10FFFF or (>= 0x80 and < 0xA0) or (>= 0xD800 and <= 0xDFFF))
        {
            return 0;
        }

        return codePoint is < 0x7F or (>= 0xA0 and <= 0xFF) ? (uint)codePoint : 0x01000000u + (uint)codePoint;
    }

    // Keys with a character on them: scancode -> [plain, with Shift, with AltGr] (0: none). German layout.
    private static readonly Dictionary<int, (string Plain, string Shift, string AltGr)> Characters = new()
    {
        [0x02] = ("1", "!", ""), [0x03] = ("2", "\"", "²"), [0x04] = ("3", "§", "³"), [0x05] = ("4", "$", ""), [0x06] = ("5", "%", ""),
        [0x07] = ("6", "&", ""), [0x08] = ("7", "/", "{"), [0x09] = ("8", "(", "["), [0x0A] = ("9", ")", "]"), [0x0B] = ("0", "=", "}"),
        [0x0C] = ("ß", "?", "\\"), [0x10] = ("q", "Q", "@"), [0x11] = ("w", "W", ""), [0x12] = ("e", "E", "€"), [0x13] = ("r", "R", ""),
        [0x14] = ("t", "T", ""), [0x15] = ("z", "Z", ""), [0x16] = ("u", "U", ""), [0x17] = ("i", "I", ""), [0x18] = ("o", "O", ""),
        [0x19] = ("p", "P", ""), [0x1A] = ("ü", "Ü", ""), [0x1B] = ("+", "*", "~"), [0x1E] = ("a", "A", ""), [0x1F] = ("s", "S", ""),
        [0x20] = ("d", "D", ""), [0x21] = ("f", "F", ""), [0x22] = ("g", "G", ""), [0x23] = ("h", "H", ""), [0x24] = ("j", "J", ""),
        [0x25] = ("k", "K", ""), [0x26] = ("l", "L", ""), [0x27] = ("ö", "Ö", ""), [0x28] = ("ä", "Ä", ""), [0x2B] = ("#", "'", ""),
        [0x2C] = ("y", "Y", ""), [0x2D] = ("x", "X", ""), [0x2E] = ("c", "C", ""), [0x2F] = ("v", "V", ""), [0x30] = ("b", "B", ""),
        [0x31] = ("n", "N", ""), [0x32] = ("m", "M", "µ"), [0x33] = (",", ";", ""), [0x34] = (".", ":", ""), [0x35] = ("-", "_", ""),
        [0x39] = (" ", " ", " "), [0x56] = ("<", ">", "|"),
    };

    // Keys that are the same in every layout: scancode -> keysym; extended keys (the grey block, right-hand modifiers) apart.
    private static readonly Dictionary<int, uint> Plain = new()
    {
        [0x01] = Escape, [0x0E] = BackSpace, [0x0F] = Tab, [0x1C] = Return, [0x1D] = ControlL, [0x2A] = ShiftL, [0x36] = 0xFFE2, [0x38] = AltL,
        [0x3A] = 0xFFE5, [0x45] = 0xFF7F, [0x46] = 0xFF14, [0x57] = 0xFFC8, [0x58] = 0xFFC9, [0x37] = 0xFFAA,
        [0x47] = 0xFFB7, [0x48] = 0xFFB8, [0x49] = 0xFFB9, [0x4A] = 0xFFAD, [0x4B] = 0xFFB4, [0x4C] = 0xFFB5, [0x4D] = 0xFFB6, [0x4E] = 0xFFAB,
        [0x4F] = 0xFFB1, [0x50] = 0xFFB2, [0x51] = 0xFFB3, [0x52] = 0xFFB0, [0x53] = 0xFFAE,
    };

    private static readonly Dictionary<int, uint> Extended = new()
    {
        [0x1C] = 0xFF8D, [0x1D] = 0xFFE4, [0x35] = 0xFFAF, [0x37] = 0xFF61, [0x38] = 0xFE03, [0x47] = 0xFF50, [0x48] = 0xFF52, [0x49] = 0xFF55,
        [0x4B] = 0xFF51, [0x4D] = 0xFF53, [0x4F] = 0xFF57, [0x50] = 0xFF54, [0x51] = 0xFF56, [0x52] = 0xFF63, [0x53] = DeleteKey,
        [0x5B] = 0xFFEB, [0x5C] = 0xFFEC, [0x5D] = 0xFF67,
    };

    // The dead keys of the German layout: the key left of the 1 (circumflex, with Shift the degree sign) and the one right of the ß (acute, with Shift grave).
    private const uint DeadCircumflex = 0xFE52, DeadAcute = 0xFE51, DeadGrave = 0xFE50;

    /// <summary>The keysym of a key by its scancode (set 1) in the German layout; 0 when the key is unknown.</summary>
    public static uint FromScancode(int scancode, bool extended, bool shift, bool altGr)
    {
        scancode &= 0xFF;
        if (extended)
        {
            return Extended.GetValueOrDefault(scancode);
        }

        if (scancode is >= 0x3B and <= 0x44)
        {
            return 0xFFBEu + (uint)(scancode - 0x3B);
        }

        switch (scancode)
        {
            case 0x29:
                return shift ? 0xB0 : DeadCircumflex;
            case 0x0D:
                return shift ? DeadGrave : DeadAcute;
        }

        if (Plain.TryGetValue(scancode, out var key))
        {
            return key;
        }

        if (!Characters.TryGetValue(scancode, out var set))
        {
            return 0;
        }

        var text = altGr && set.AltGr.Length > 0 ? set.AltGr : shift ? set.Shift : set.Plain;
        return FromCodePoint(char.ConvertToUtf32(text, 0));
    }
}
