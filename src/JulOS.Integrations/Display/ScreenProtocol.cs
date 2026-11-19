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
///   7 Bild           format(1: 1 PNG, 2 BMP) Bytes ...          (der ferne Rechner hat ein Bild kopiert)
///   8 Dateien        stapel(4) anzahl(1) dann je Datei groesse(8, -1 unbekannt) namenlaenge(2) Name (UTF-8)   (anzahl 0: Auswahl weg)
///   9 Dateistueck    stapel(4) nummer(1) flags(1: Bit0 letztes, Bit1 gescheitert) Bytes ...   (bei Scheitern der Grund als Text)
///  10 Ergebnis       nummer(4) code(1: 0 ok, 1 zu gross, 2 nicht erlaubt, 3 keine Zwischenablage in der VM, 4 sonst) Text ...
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
///  10 Senden Anfang  nummer(4) art(1: 1 Bild als BMP, 2 Datei) lfd(1) anzahl(1) groesse(8) Name (UTF-8)   (lfd und anzahl: Stelle in einem Stapel von Dateien)
///  11 Senden Stueck  nummer(4) Bytes ...   (bis ClipboardBridge.MaxChunkBytes)
///  12 Senden Ende    nummer(4)
///  13 Senden Abbruch nummer(4)
///  14 Datei holen    stapel(4) nummer(1)
///  15 Holen Abbruch  stapel(4)
///  16 Pause          an(1: 1 = die Ansicht ist im Hintergrund, es werden keine Bilder kodiert; 0 = weiter)
/// </summary>
public static class ScreenProtocol
{
    public const byte ToBrowserPicture = 1, ToBrowserCursor = 2, ToBrowserSize = 3, ToBrowserError = 4, ToBrowserTaken = 5, ToBrowserClipboard = 6,
        ToBrowserImage = 7, ToBrowserFiles = 8, ToBrowserFileChunk = 9, ToBrowserResult = 10;
    public const byte FromBrowserAck = 1, FromBrowserMove = 2, FromBrowserButton = 3, FromBrowserWheel = 4, FromBrowserKey = 5,
        FromBrowserUnicode = 6, FromBrowserResize = 7, FromBrowserKeyFrame = 8, FromBrowserClipboard = 9,
        FromBrowserSendBegin = 10, FromBrowserSendChunk = 11, FromBrowserSendEnd = 12, FromBrowserSendCancel = 13,
        FromBrowserFileRequest = 14, FromBrowserFileCancel = 15, FromBrowserPause = 16;

    /// <summary>Nachrichten, die laenger als eine Eingabe sein duerfen (Text, Dateiname, Stueck einer Datei).</summary>
    public static bool MayBeLong(byte type) => type is FromBrowserClipboard or FromBrowserSendBegin or FromBrowserSendChunk;

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

    public static byte[] Image(byte format, byte[] data)
    {
        var result = new byte[2 + data.Length];
        result[0] = ToBrowserImage;
        result[1] = format;
        data.CopyTo(result, 2);
        return result;
    }

    public static byte[] Files(uint batch, IReadOnlyList<RemoteFile> files)
    {
        var names = files.Select(f => System.Text.Encoding.UTF8.GetBytes(f.Name)).ToArray();
        var result = new byte[6 + files.Count * 10 + names.Sum(n => n.Length)];
        result[0] = ToBrowserFiles;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1), batch);
        result[5] = (byte)files.Count;
        var at = 6;
        for (var i = 0; i < files.Count; i++)
        {
            BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(at), files[i].Size);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(at + 8), (ushort)names[i].Length);
            names[i].CopyTo(result, at + 10);
            at += 10 + names[i].Length;
        }

        return result;
    }

    public static byte[] FileChunk(uint batch, int index, byte flags, byte[] data)
    {
        var result = new byte[7 + data.Length];
        result[0] = ToBrowserFileChunk;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1), batch);
        result[5] = (byte)index;
        result[6] = flags;
        data.CopyTo(result, 7);
        return result;
    }

    public static byte[] Result(uint id, byte code, string message)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(message.Length > 200 ? message[..200] : message);
        var result = new byte[6 + text.Length];
        result[0] = ToBrowserResult;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1), id);
        result[5] = code;
        text.CopyTo(result, 6);
        return result;
    }

    public static byte[] Taken() => [ToBrowserTaken];
}
