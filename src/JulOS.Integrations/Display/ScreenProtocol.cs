using System.Buffers.Binary;

namespace JulOS.Integrations.Display;

/// <summary>
/// Die Nachrichten zwischen Server und Browser, alle binaer und klein (Zahlen big endian). Bilder und Eingaben sind der heisse Pfad: ohne
/// Text, ohne JSON, ein Byte Art davor.
///
/// Server an Browser:
///   1 Bild       flags(1: Bit0 = Schluesselbild) folge(4) H.264 Annex B ...
///   2 Zeiger     art(1: 0 Form, 1 versteckt, 2 Standard) hotX(2) hotY(2) breite(2) hoehe(2) RGBA ...
///   3 Groesse    breite(2) hoehe(2)
///   4 Fehler     code(2) UTF-8-Text ...            (Codes wie bei Guacamole: 769 Anmeldung abgelehnt, 515 Verbindung misslungen ...)
///   5 Uebernommen
///   6 Zwischenablage UTF-8-Text ...          (der ferne Rechner hat Text kopiert; bis ClipboardBridge.MaxTextBytes)
/// Browser an Server:
///   1 Bestaetigung   folge(4)   (alle Bilder bis zu dieser Folgenummer sind dekodiert oder verworfen; ohne Nummer: ein Bild)
///   2 Maus bewegt    x(2) y(2)
///   3 Maustaste      taste(1: 0 links, 1 mitte, 2 rechts) unten(1)
///   4 Mausrad        delta(2, mit Vorzeichen) waagerecht(1)
///   5 Taste          scancode(1) flags(1: Bit0 erweitert, Bit1 unten)
///   6 Zeichen        utf16-einheit(2)
///   7 Fenstergroesse breite(2) hoehe(2)
///   8 Schluesselbild bitte
///   9 Zwischenablage UTF-8-Text ...          (der Browser legt Text ab; der ferne Rechner holt ihn beim Einfuegen)
/// </summary>
public static class ScreenProtocol
{
    public const byte ToBrowserPicture = 1, ToBrowserCursor = 2, ToBrowserSize = 3, ToBrowserError = 4, ToBrowserTaken = 5, ToBrowserClipboard = 6;
    public const byte FromBrowserAck = 1, FromBrowserMove = 2, FromBrowserButton = 3, FromBrowserWheel = 4, FromBrowserKey = 5,
        FromBrowserUnicode = 6, FromBrowserResize = 7, FromBrowserKeyFrame = 8, FromBrowserClipboard = 9;

    public const int PictureHeader = 6;
    public const int CursorHeader = 10;

    public static int WritePicture(Span<byte> target, int sequence, bool isKey, ReadOnlySpan<byte> data)
    {
        target[0] = ToBrowserPicture;
        target[1] = isKey ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(target[2..], sequence);
        data.CopyTo(target[PictureHeader..]);
        return PictureHeader + data.Length;
    }

    public static int WriteCursor(Span<byte> target, ScreenCursor cursor)
    {
        target[0] = ToBrowserCursor;
        target[1] = (byte)cursor.Kind;
        BinaryPrimitives.WriteUInt16BigEndian(target[2..], (ushort)cursor.HotX);
        BinaryPrimitives.WriteUInt16BigEndian(target[4..], (ushort)cursor.HotY);
        BinaryPrimitives.WriteUInt16BigEndian(target[6..], (ushort)cursor.Width);
        BinaryPrimitives.WriteUInt16BigEndian(target[8..], (ushort)cursor.Height);
        var length = CursorHeader;
        if (cursor.Rgba is { } rgba)
        {
            rgba.CopyTo(target[CursorHeader..]);
            length += rgba.Length;
        }

        return length;
    }

    public static int WriteSize(Span<byte> target, int width, int height)
    {
        target[0] = ToBrowserSize;
        BinaryPrimitives.WriteUInt16BigEndian(target[1..], (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(target[3..], (ushort)height);
        return 5;
    }

    public static byte[] Error(int code, string message)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(message.Length > 400 ? message[..400] : message);
        var result = new byte[3 + text.Length];
        result[0] = ToBrowserError;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1), (ushort)code);
        text.CopyTo(result, 3);
        return result;
    }

    public static byte[] Clipboard(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var result = new byte[1 + bytes.Length];
        result[0] = ToBrowserClipboard;
        bytes.CopyTo(result, 1);
        return result;
    }

    public static byte[] Taken() => [ToBrowserTaken];
}
