using System.Globalization;
using System.Text;
using JulOS.Frontend.Remote;
using JulOS.Integrations.Files;

namespace JulOS.Frontend.Files;

internal enum PreviewKind { Image, Video, Audio, Pdf, Word, Sheet }

/// <summary>Ein Typ, den die Vorschau zeigen darf: die Endung (klein), die Art und der Content-Type, den DER SERVER setzt (nie einer aus dem Dateiinhalt).</summary>
internal sealed record PreviewType(string Extension, PreviewKind Kind, string ContentType);

/// <summary>Was aus einem Range-Kopf wird: <c>Start</c> und <c>End</c> (einschliesslich) der Bytes, die gesendet werden; <c>Partial</c> = 206, sonst die ganze Datei (200); <c>Unsatisfiable</c> = 416.</summary>
internal readonly record struct PreviewRange(long Start, long End, bool Partial, bool Unsatisfiable);

/// <summary>
/// Die Vorschau der Dateien-App (docs/FILES-VIEWER.md, Teil C): ein enges, eigenes Antwortmodell neben dem Download, der nie eine Seite zeigt. Gezeigt wird nur, was auf einer FESTEN Liste steht
/// (Endung) UND dessen Anfang zu dieser Endung passt (Magic Bytes); der Content-Type kommt aus der Liste, die Antwort traegt <c>nosniff</c>, eine strenge CSP (<c>sandbox</c>, kein Skript) und ist
/// <c>inline</c> nur fuer diese Liste. HTML, SVG, XML, Skripte und alles Unbekannte wird verweigert, nie ausgeliefert. Die Datei kommt im Strom, nie ganz in den Speicher; Range gibt es fuer Dateien,
/// die sich positionieren lassen (lokaler Ordner, SFTP), nicht auf Root-Orten (deren Bestaetigung gilt einmal).
/// </summary>
internal static class FilePreview
{
    /// <summary>So viele Bytes vom Anfang prueft der Abgleich (ein PDF darf seinen Kopf bis 1024 Bytes verschoben haben).</summary>
    public const int HeadSize = 1024;

    /// <summary>Gleichzeitig offene Vorschau-Stroeme je Benutzer auf Hosts: ein Video fragt mit mehreren Range-Anfragen, Downloads (3) wuerden es abwuergen.</summary>
    public const int MaxStreams = 6;

    private static readonly Dictionary<string, PreviewType> Types = Build(
        ("png", PreviewKind.Image, "image/png"), ("jpg", PreviewKind.Image, "image/jpeg"), ("jpeg", PreviewKind.Image, "image/jpeg"), ("gif", PreviewKind.Image, "image/gif"),
        ("webp", PreviewKind.Image, "image/webp"), ("avif", PreviewKind.Image, "image/avif"),
        ("mp4", PreviewKind.Video, "video/mp4"), ("webm", PreviewKind.Video, "video/webm"),
        ("mp3", PreviewKind.Audio, "audio/mpeg"), ("ogg", PreviewKind.Audio, "audio/ogg"), ("wav", PreviewKind.Audio, "audio/wav"), ("flac", PreviewKind.Audio, "audio/flac"), ("m4a", PreviewKind.Audio, "audio/mp4"),
        ("pdf", PreviewKind.Pdf, "application/pdf"),
        // Word und Excel (und csv) zeigt die Seite selbst aus dem Abruf (fetch): der Server liefert sie als unbekannten Typ, nie als Dokument; entpackt wird nur im Browser, mit eigener Grenze (files-viewer.js)
        ("docx", PreviewKind.Word, "application/octet-stream"), ("xlsx", PreviewKind.Sheet, "application/octet-stream"), ("csv", PreviewKind.Sheet, "application/octet-stream"));

    private static Dictionary<string, PreviewType> Build(params (string Ext, PreviewKind Kind, string Type)[] all) => all.ToDictionary(t => t.Ext, t => new PreviewType(t.Ext, t.Kind, t.Type), StringComparer.Ordinal);

    /// <summary>Der Typ nach der Endung des Namens; null, wenn sie nicht auf der Liste steht (SVG, HTML, XML, JS, keine Endung, alles Unbekannte).</summary>
    public static PreviewType? ByName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? null : Types.GetValueOrDefault(name[(dot + 1)..].ToLowerInvariant());
    }

    /// <summary>Der Fehlercode, wenn diese Datei nicht gezeigt wird (sonst null): Typ nicht auf der Liste, Video/Audio auf einem Root-Ort, zu gross. Nur Namen und Groesse, nichts wird gelesen.</summary>
    public static string? Refuse(string? name, long size, bool root, long previewMax, long hardMax)
    {
        if (ByName(name) is not { } type) return "preview_type";
        if (root && type.Kind is PreviewKind.Video or PreviewKind.Audio) return "preview_root_type";
        // Bild und PDF bis zur eingestellten Grenze (50 MiB); Video und Audio kommen in Teilen (Range) und stehen nur unter der Grenze fuer jede Datei (JULOS_FILES_MAX_MB)
        var limit = type.Kind is PreviewKind.Video or PreviewKind.Audio ? hardMax : Math.Min(previewMax, hardMax);
        return size > limit ? "preview_too_large" : null;
    }

    // ---- der Abgleich des Anfangs mit der Endung ----

    private static bool Starts(ReadOnlySpan<byte> head, ReadOnlySpan<byte> prefix) => head.StartsWith(prefix);
    private static bool At(ReadOnlySpan<byte> head, int offset, string ascii) => head.Length >= offset + ascii.Length && head.Slice(offset, ascii.Length).SequenceEqual(Encoding.ASCII.GetBytes(ascii));

    /// <summary>Eine ISO-Basismedien-Datei (mp4, m4a, avif): die Box "ftyp" steht gleich am Anfang; <paramref name="brand"/> prueft die Marken (null: jede).</summary>
    private static bool Ftyp(ReadOnlySpan<byte> head, Func<string, bool>? brand)
    {
        if (!At(head, 4, "ftyp") || head.Length < 12) return false;
        if (brand is null) return true;
        var size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head);
        var end = (int)Math.Clamp(size, 12, Math.Min(head.Length, 64));
        // Hauptmarke (Byte 8) und die vertraeglichen Marken (ab Byte 16, je vier Bytes)
        if (brand(Encoding.ASCII.GetString(head.Slice(8, 4)))) return true;
        for (var i = 16; i + 4 <= end; i += 4) if (brand(Encoding.ASCII.GetString(head.Slice(i, 4)))) return true;
        return false;
    }

    /// <summary>Sieht der Anfang nach Text aus (csv)? Kein Nullbyte, ausser bei UTF-16 mit Kennung (Excels "Unicode-Text"). Ein Text ist harmlos: er geht nur als textContent in die Seite, die Antwort ist nie ein Dokument.</summary>
    private static bool IsText(ReadOnlySpan<byte> head) => Starts(head, [0xFF, 0xFE]) || Starts(head, [0xFE, 0xFF]) || head.IndexOf((byte)0) < 0;

    /// <summary>Passt der Anfang der Datei zur Endung? Wie ein Browser den Inhalt erkennen wuerde, aber streng: stimmt es nicht, wird die Datei nicht ausgeliefert.</summary>
    public static bool MatchesContent(PreviewType type, ReadOnlySpan<byte> head) => type.Extension switch
    {
        "png" => Starts(head, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "jpg" or "jpeg" => Starts(head, [0xFF, 0xD8, 0xFF]),
        "gif" => At(head, 0, "GIF87a") || At(head, 0, "GIF89a"),
        "webp" => At(head, 0, "RIFF") && At(head, 8, "WEBP"),
        "avif" => Ftyp(head, b => b is "avif" or "avis"),
        "mp4" => Ftyp(head, null),
        "m4a" => Ftyp(head, null),
        "webm" => Starts(head, [0x1A, 0x45, 0xDF, 0xA3]),
        "mp3" => At(head, 0, "ID3") || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),
        "ogg" => At(head, 0, "OggS"),
        "wav" => At(head, 0, "RIFF") && At(head, 8, "WAVE"),
        "flac" => At(head, 0, "fLaC"),
        "pdf" => head.IndexOf("%PDF-"u8) is >= 0 and < HeadSize,
        // docx und xlsx sind ZIP-Dateien (Kopf eines lokalen Eintrags); die Pruefung des Inhalts und die Grenze fuer das Entpackte macht der Browser, der Server entpackt nie
        "docx" or "xlsx" => Starts(head, [0x50, 0x4B, 0x03, 0x04]),
        "csv" => IsText(head),
        _ => false,
    };

    /// <summary>Liest den Anfang des Stroms (hoechstens <see cref="HeadSize"/> Bytes) und gleicht ihn mit der Endung ab. Code <c>preview_content</c>, wenn es nicht passt (oder die Datei leer ist).</summary>
    public static async Task<(string? Code, byte[] Head)> ReadHeadAsync(Stream stream, PreviewType type, long length, CancellationToken ct)
    {
        var buffer = new byte[(int)Math.Min(HeadSize, length)];
        var n = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        var head = n == buffer.Length ? buffer : buffer[..n];
        return (n > 0 && MatchesContent(type, head) ? null : "preview_content", head);
    }

    // ---- Range ----

    /// <summary>
    /// Wertet den Range-Kopf aus (<c>bytes=a-b</c>, <c>bytes=a-</c>, <c>bytes=-n</c>). Mehrere Bereiche, ein unverstaendlicher Kopf und <c>If-Range</c> (wir senden keinen Validator) gelten als nicht vorhanden: die ganze Datei, 200.
    /// Ein Anfang hinter dem Ende ist 416. <paramref name="allowed"/> false (Root-Ort): immer die ganze Datei.
    /// </summary>
    public static PreviewRange ResolveRange(string? range, bool ifRange, long length, bool allowed)
    {
        var whole = new PreviewRange(0, length - 1, false, false);
        if (!allowed || ifRange || string.IsNullOrEmpty(range) || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || range.Contains(',')) return whole;
        var spec = range[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return whole;
        var (from, to) = (spec[..dash].Trim(), spec[(dash + 1)..].Trim());
        static bool Num(string s, out long v) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
        if (from.Length == 0)
        {
            // "-n": die letzten n Bytes
            if (!Num(to, out var last)) return whole;
            return last == 0 || length == 0 ? new PreviewRange(0, 0, false, true) : new PreviewRange(Math.Max(0, length - last), length - 1, true, false);
        }
        if (!Num(from, out var start) || (to.Length > 0 && !Num(to, out _))) return whole;
        long end = length - 1;
        if (to.Length > 0) { Num(to, out var e); if (e < start) return whole; end = Math.Min(e, length - 1); }
        return start >= length ? new PreviewRange(0, 0, false, true) : new PreviewRange(start, end, true, false);
    }

    // ---- die Antwort ----

    /// <summary>Die Kopfzeilen jeder Vorschau-Antwort: fester Typ, nosniff, strenge CSP, nie im Cache, nur dieselbe Herkunft, inline nur mit dem sauberen Namen.</summary>
    public static void ApplyHeaders(HttpResponse response, PreviewType type, string name, bool ranges)
    {
        var h = response.Headers;
        response.ContentType = type.ContentType;
        h["X-Content-Type-Options"] = "nosniff";
        // Ein Aufruf der Adresse selbst laeuft nichts: sandbox ohne allow-scripts, nichts wird geladen ausser Bild und Medien der eigenen Herkunft (die Seite rahmt nie: frame-ancestors steht in der allgemeinen CSP)
        h["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'";
        h.CacheControl = "no-store";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h.ContentDisposition = RemoteEndpoints.ContentDisposition(name, "inline");
        h.AcceptRanges = ranges ? "bytes" : "none";
    }

    /// <summary>Die Antwort 416 (der Bereich liegt hinter dem Ende der Datei).</summary>
    public static IResult NotSatisfiable(HttpContext ctx, long length)
    {
        ctx.Response.Headers["Content-Range"] = $"bytes */{length}";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
    }

    /// <summary>
    /// Sendet die Bytes <paramref name="range"/> im Strom (128-KiB-Stuecke, nie die ganze Datei im Speicher). <paramref name="head"/> sind die schon gelesenen ersten Bytes. Der Strom wird positioniert, wenn er es kann;
    /// sonst werden die Bytes davor gelesen und verworfen. Bricht es mitten im Senden ab, wird die Verbindung abgebrochen (eine unvollstaendige Datei soll nie vollstaendig aussehen) und der Code zurueckgegeben;
    /// ein Abbruch durch den Browser ist eine OperationCanceledException (der Benutzer hat das Video geschlossen).
    /// </summary>
    public static async Task<string?> WriteAsync(HttpContext ctx, Stream stream, byte[] head, PreviewType type, string name, PreviewRange range, long length, bool ranges, int bufferSize)
    {
        var count = range.End - range.Start + 1;
        var ct = ctx.RequestAborted;
        long sent = 0;
        try
        {
            long position = head.Length;                                  // so weit ist der Strom gelesen
            var buffer = new byte[bufferSize];
            var seeked = range.Start > 0 && stream.CanSeek;
            if (seeked) { stream.Seek(range.Start, SeekOrigin.Begin); position = range.Start; }
            while (position < range.Start)                                // ein Strom ohne Seek: davor Liegendes lesen und verwerfen
            {
                var skipped = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, range.Start - position)), ct);
                if (skipped == 0) break;
                position += skipped;
            }
            ApplyHeaders(ctx.Response, type, name, ranges);
            if (range.Partial)
            {
                ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                ctx.Response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{length}";
            }
            ctx.Response.ContentLength = count;
            if (!seeked && range.Start < head.Length)
            {
                var part = (int)Math.Min(head.Length - range.Start, count);
                await ctx.Response.Body.WriteAsync(head.AsMemory((int)range.Start, part), ct);
                sent += part;
            }
            while (sent < count)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - sent)), ct);
                if (n == 0) break;
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                sent += n;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (ctx.Response.HasStarted) ctx.Abort();
            else ResetHeaders(ctx.Response);                               // noch nichts gesendet: der Aufrufer antwortet mit dem Fehler
            return FileErrors.Code(e);
        }
        if (sent < count) { ctx.Abort(); return "incomplete"; }          // die Datei war beim Lesen kuerzer als beim Messen
        return null;
    }

    private static void ResetHeaders(HttpResponse r)
    {
        r.StatusCode = StatusCodes.Status200OK;
        r.ContentLength = null;
        foreach (var h in new[] { "Content-Disposition", "Content-Range", "Accept-Ranges" }) r.Headers.Remove(h);
    }
}
