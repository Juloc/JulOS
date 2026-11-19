using System.Buffers.Binary;
using JulOS.Integrations.Display.Video;

namespace JulOS.Integrations.Display;

/// <summary>
/// Die Nachrichten zwischen Server und Browser, alle binaer und klein (Zahlen big endian). Bilder und Eingaben sind der heisse Pfad: ohne
/// Text, ohne JSON, ein Byte Art davor.
///
/// Server an Browser:
///   1 Bild       flags(1: Bit0 = Schluesselbild, Bit1 = H.265 statt H.264) folge(4) Annex B ...   (Schluesselbild: mit Parametersaetzen davor, H.265 mit VPS, SPS, PPS; der Browser liest den Codec-Text daraus)
///   2 Zeiger     art(1: 0 Form, 1 versteckt, 2 Standard) hotX(2) hotY(2) breite(2) hoehe(2) RGBA ...
///   3 Groesse    breite(2) hoehe(2)
///   4 Fehler     code(2) UTF-8-Text ...            (Statuscodes: 769 Anmeldung abgelehnt, 770 Anmeldedaten fehlen (der Server verlangt sie, bevor er etwas zeigt): in beiden Faellen fragt der Browser neu und holt ein neues Ticket;
///                                                    515 Verbindung misslungen ...;
///                                                    800 Zertifikat geaendert: Text "<Fingerabdruck jetzt>\n<Fingerabdruck bisher>", es wurde nichts gesendet)
///   5 Uebernommen
///   6 Zwischenablage UTF-8-Text ...          (der ferne Rechner hat Text kopiert; bis ClipboardBridge.MaxTextBytes)
///  20 Ton          kanaele(1) rate(4) zeit(4, ms des Servers) PCM ...   (16 Bit, little endian, verschachtelt; nur wenn der Browser den Ton eingeschaltet hat)
///  21 Ton-Zustand  flags(1: Bit0 der ferne Rechner liefert Ton, Bit1 er bietet ein Mikrofon, Bit2 er nimmt gerade auf) mikrofonrate(4)
///   7 Bild           format(1: 1 PNG, 2 BMP) Bytes ...          (der ferne Rechner hat ein Bild kopiert)
///   8 Eintraege     stapel(4) anzahl(2) dann je Eintrag flags(1: Bit0 Ordner) groesse(8, -1 unbekannt) namenlaenge(2) Pfad (UTF-8, Trenner /, relativ)   (anzahl 0: Auswahl weg; ein Ordner steht vor seinem Inhalt)
///   9 Dateistand     stapel(4) zustand(1: 0 bereit, 1 laeuft, 2 fertig, 3 gescheitert) fertig(8) gesamt(8, -1 unbekannt) Text ...   (bereit: der Text ist das Token fuer GET /api/remote/clip/<Token>, der Download laeuft dann als HTTP-Strom; gescheitert: der Grund)
///  14 Grenzen        maxDatei(8) maxDateien(2) maxEintraege(2)   (einmal nach dem Verbinden: so viel darf der Browser an die Zwischenablage der VM senden)
///  15 Zwischenablage-Ereignis  flags(1: Bit0 Kanal der VM bereit, Bit1 Dateien moeglich) art(1: siehe ClipNote) zahl(4, Zeichen oder Bytes; nie Inhalt)   (Diagnose der Text-Zwischenablage; kommt auch ohne Text, wenn sich der Kanal meldet)
///  10 Ergebnis       nummer(4) code(1: 0 ok, 1 zu gross, 2 nicht erlaubt, 3 keine Zwischenablage in der VM, 4 sonst) Text ...
///  11 Sitzung       sekunden(2) Token (ASCII)   (gleich nach dem Verbinden, wenn der Server Sitzungen erhaelt: so lange wartet die Sitzung ohne Browser; mit dem Token kommt der Browser zurueck, Adresse resume=)
///  12 Nicht wieder  (das Token ist unbekannt, abgelaufen oder fremd: der Browser baut neu auf)
///  30 Kodierer      codec(1: 1 H.264, 2 H.265) flags(1: Bit0 = GPU) Name (UTF-8) ...   (nach jedem Neuaufbau des Kodierers; nur zum Anzeigen)
///  40 Nachbesserung x(2) y(2) breite(2) hoehe(2) PNG ...   (dieses Rechteck verlustfrei, steht still; der Browser legt es ueber das Video)
///  41 Nachbesserung weg anzahl(2) dann je Rechteck x(2) y(2) breite(2) hoehe(2)   (dort hat sich der Bildschirm wieder geaendert: Ueberlagerung entfernen)
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
///  20 Ton ein/aus  an(1)                                  (ohne diese Nachricht kommt kein Ton)
///  21 Mikrofon     rate(4) PCM ...                         (Mono, 16 Bit, little endian; wird verworfen, solange der ferne Rechner keinen Ton verlangt)
/// Die Tonnachrichten haben ihren eigenen Nummernbereich ab 20, damit sie nicht mit anderen Erweiterungen zusammenstossen.
///  10 Senden Anfang  nummer(4) art(1: 1 Bild als BMP, 2 Datei, 3 leerer Ordner) lfd(1) anzahl(1) groesse(8) Pfad (UTF-8, relativ, Trenner /)   (lfd und anzahl: Stelle in einem Stapel von Eintraegen; ein Pfad mit .., ein absoluter Pfad oder ein Laufwerk wird abgelehnt)
///  11 Senden Stueck  nummer(4) Bytes ...   (bis ClipboardBridge.MaxChunkBytes)
///  12 Senden Ende    nummer(4)
///  13 Senden Abbruch nummer(4)
///  14 Datei holen    stapel(4) Pfad (UTF-8, leer = alles)   (Datei: als sie selbst, Ordner oder alles: als ZIP; die Antwort ist Nachricht 9, Zustand bereit)
///  15 Holen Abbruch  stapel(4)
///  16 Pause          an(1: 1 = die Ansicht ist im Hintergrund, es werden keine Bilder kodiert; 0 = weiter)
///  17 Beenden        (der Benutzer schliesst die Sitzung: der Server haelt sie nicht fuer ein Wiederverbinden)
///  30 Codecs         menge(1: Bit0 H.264, Bit1 H.265)   (was der Dekodierer kann; beim Verbinden steht dasselbe in der Adresse als codecs=, und noch einmal
///                                                        mit nur H.264, wenn der H.265-Dekodierer nicht geht: der Server wechselt dann mit einem Schluesselbild)
///  31 Bildrate       stufe(1: 0 automatisch, 1 hoechstens 60 Bilder/s, 2 hoechstens 30, 3 sparsam: 15 Bilder/s und weniger Bitrate; siehe StreamQuality)
///                                                        (je Sitzung, gilt bis zur naechsten Nachricht und bleibt beim Wiederaufnehmen; unbekannte Stufen werden ignoriert;
///                                                        ohne Wirkung, solange die Bilder des Servers durchgereicht werden, RDPGFX)
/// </summary>
public static class ScreenProtocol
{
    public const byte ToBrowserPicture = 1, ToBrowserCursor = 2, ToBrowserSize = 3, ToBrowserError = 4, ToBrowserTaken = 5, ToBrowserClipboard = 6,
        ToBrowserImage = 7, ToBrowserFiles = 8, ToBrowserClipNote = 15, ToBrowserFileState = 9, ToBrowserResult = 10, ToBrowserLimits = 14, ToBrowserSession = 11, ToBrowserResumeFailed = 12, ToBrowserEncoder = 30,
        ToBrowserRefine = 40, ToBrowserRefineClear = 41;
    public const byte FromBrowserAck = 1, FromBrowserMove = 2, FromBrowserButton = 3, FromBrowserWheel = 4, FromBrowserKey = 5,
        FromBrowserUnicode = 6, FromBrowserResize = 7, FromBrowserKeyFrame = 8, FromBrowserClipboard = 9,
        FromBrowserSendBegin = 10, FromBrowserSendChunk = 11, FromBrowserSendEnd = 12, FromBrowserSendCancel = 13,
        FromBrowserFileRequest = 14, FromBrowserFileCancel = 15, FromBrowserPause = 16, FromBrowserEnd = 17, FromBrowserCodecs = 30, FromBrowserQuality = 31;

    /// <summary>Nachrichten, die laenger als eine Eingabe sein duerfen (Text, Dateiname, Stueck einer Datei).</summary>
    public static bool MayBeLong(byte type) => type is FromBrowserClipboard or FromBrowserSendBegin or FromBrowserSendChunk or FromBrowserMicrophone or FromBrowserFileRequest;

    public const byte ToBrowserAudio = 20, ToBrowserAudioStatus = 21;
    public const byte FromBrowserAudio = 20, FromBrowserMicrophone = 21;

    public const int AudioHeader = 10;
    public const int AudioStatusLength = 6;

    /// <summary>Laenger darf eine Mikrofon-Nachricht sein (rund 90 ms bei 48 kHz); der Browser schickt kleinere Stuecke.</summary>
    public const int MaxMicrophoneMessage = 9 + 8192;

    public const int PictureHeader = 6;
    public const int CursorHeader = 10;

    public const byte PictureKey = 1, PictureHevc = 2;

    public static int WritePicture(Span<byte> target, int sequence, bool isKey, ReadOnlySpan<byte> data, VideoCodec codec = VideoCodec.H264)
    {
        target[0] = ToBrowserPicture;
        target[1] = (byte)((isKey ? PictureKey : 0) | (codec == VideoCodec.Hevc ? PictureHevc : 0));
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

    public const int RefineHeader = 9;
    public const int MaxClearRects = 4000;

    public static int WriteRefine(Span<byte> target, ScreenRefine refine)
    {
        target[0] = ToBrowserRefine;
        BinaryPrimitives.WriteUInt16BigEndian(target[1..], (ushort)refine.X);
        BinaryPrimitives.WriteUInt16BigEndian(target[3..], (ushort)refine.Y);
        BinaryPrimitives.WriteUInt16BigEndian(target[5..], (ushort)refine.Width);
        BinaryPrimitives.WriteUInt16BigEndian(target[7..], (ushort)refine.Height);
        refine.Png.CopyTo(target[RefineHeader..]);
        return RefineHeader + refine.Png.Length;
    }

    public static int WriteRefineClear(Span<byte> target, int[] rects)
    {
        // Mehr als MaxClearRects Rechtecke gibt es nur bei riesigen Bildschirmen: dann wird alles gestrichen (besser zu viel als stehengebliebenes Altes)
        if (rects.Length / 4 > MaxClearRects)
        {
            rects = [0, 0, 65535, 65535];
        }

        var count = rects.Length / 4;
        target[0] = ToBrowserRefineClear;
        BinaryPrimitives.WriteUInt16BigEndian(target[1..], (ushort)count);
        for (var i = 0; i < count * 4; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target[(3 + i * 2)..], (ushort)rects[i]);
        }

        return 3 + count * 8;
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

    public const int ErrorCertificateChanged = 800;

    public static byte[] CertificateChanged(string presented, string? stored) => Error(ErrorCertificateChanged, presented + "\n" + (stored ?? ""));

    public static byte[] Clipboard(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var result = new byte[1 + bytes.Length];
        result[0] = ToBrowserClipboard;
        bytes.CopyTo(result, 1);
        return result;
    }

    public static int WriteAudio(Span<byte> target, AudioBlock block)
    {
        target[0] = ToBrowserAudio;
        target[1] = (byte)block.Channels;
        BinaryPrimitives.WriteInt32BigEndian(target[2..], block.Rate);
        BinaryPrimitives.WriteUInt32BigEndian(target[6..], block.TimeMs);
        block.Pcm.Span.CopyTo(target[AudioHeader..]);
        return AudioHeader + block.Pcm.Length;
    }

    public static int WriteAudioStatus(Span<byte> target, AudioStatus status)
    {
        target[0] = ToBrowserAudioStatus;
        target[1] = status.Flags;
        BinaryPrimitives.WriteInt32BigEndian(target[2..], status.MicrophoneRate);
        return AudioStatusLength;
    }

    public static byte[] Image(byte format, byte[] data)
    {
        var result = new byte[2 + data.Length];
        result[0] = ToBrowserImage;
        result[1] = format;
        data.CopyTo(result, 2);
        return result;
    }

    /// <summary>Hoechstens so viele Eintraege in der Liste (zwei Bytes; der Server bietet nicht mehr an, siehe <see cref="ClipboardBridge.MaxRemoteEntries"/>).</summary>
    public const int MaxListed = ushort.MaxValue;

    public static byte[] Files(uint batch, IReadOnlyList<RemoteFile> files)
    {
        var count = Math.Min(files.Count, MaxListed);
        var names = files.Take(count).Select(f => System.Text.Encoding.UTF8.GetBytes(f.Name)).ToArray();
        var result = new byte[7 + count * 11 + names.Sum(n => n.Length)];
        result[0] = ToBrowserFiles;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1), batch);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(5), (ushort)count);
        var at = 7;
        for (var i = 0; i < count; i++)
        {
            result[at] = files[i].IsDirectory ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(at + 1), files[i].Size);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(at + 9), (ushort)names[i].Length);
            names[i].CopyTo(result, at + 11);
            at += 11 + names[i].Length;
        }

        return result;
    }

    public const int ClipNoteLength = 7;

    public static byte[] ClipboardNote(byte flags, ClipNote note, int count)
    {
        var result = new byte[ClipNoteLength];
        result[0] = ToBrowserClipNote;
        result[1] = flags;
        result[2] = (byte)note;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(3), count);
        return result;
    }

    public const int FileStateHeader = 22;

    public static byte[] FileState(uint batch, byte state, long done, long total, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text.Length > 300 ? text[..300] : text);
        var result = new byte[FileStateHeader + bytes.Length];
        result[0] = ToBrowserFileState;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1), batch);
        result[5] = state;
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(6), done);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(14), total);
        bytes.CopyTo(result, FileStateHeader);
        return result;
    }

    public static byte[] Limits(long maxFileBytes, int maxFiles, int maxEntries)
    {
        var result = new byte[13];
        result[0] = ToBrowserLimits;
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(1), maxFileBytes);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(9), (ushort)maxFiles);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(11), (ushort)maxEntries);
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

    public static byte[] ResumeFailed() => [ToBrowserResumeFailed];

    /// <summary>Das Token der Sitzung und wie lange sie ohne Browser wartet.</summary>
    public static byte[] Session(string token, int keepSeconds)
    {
        var text = System.Text.Encoding.ASCII.GetBytes(token);
        var result = new byte[3 + text.Length];
        result[0] = ToBrowserSession;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1), (ushort)Math.Clamp(keepSeconds, 0, ushort.MaxValue));
        text.CopyTo(result, 3);
        return result;
    }

    public static byte[] Encoder(VideoCodec codec, bool hardware, string name)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(name.Length > 120 ? name[..120] : name);
        var result = new byte[3 + text.Length];
        result[0] = ToBrowserEncoder;
        result[1] = (byte)codec;
        result[2] = hardware ? (byte)1 : (byte)0;
        text.CopyTo(result, 3);
        return result;
    }

    /// <summary>Die Codecs aus der Adresse des WebSockets (codecs=Zahl); fehlt sie oder ist sie Unsinn, gilt nur H.264.</summary>
    public static VideoCodecs ParseCodecs(string? value) =>
        byte.TryParse(value, out var number) && (number & 3) != 0 ? (VideoCodecs)(number & 3) : VideoCodecs.H264;
}
