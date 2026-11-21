using System.Text;
using JulOS.Frontend.Files;

namespace JulOS.Backend.Tests;

/// <summary>Die Logik der Vorschau (docs/FILES-VIEWER.md): welche Typen, ob der Inhalt zur Endung passt, die Grenzen und Range. Sicherheitskritisch, darum geprueft; die Antworten selbst in den HTTP-Tests.</summary>
public sealed class FilePreviewTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Jpg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0, 0, 0];
    private static readonly byte[] Html = Ascii("<!doctype html><script>alert(1)</script>");
    private static byte[] Ftyp(string brand, params string[] compatible) => Cat([[0, 0, 0, (byte)(16 + 4 * compatible.Length)], Ascii("ftyp"), Ascii(brand), [0, 0, 0, 0], .. compatible.Select(Ascii)]);

    /// <summary>Je Endung ein Anfang, der passt (so wie ihn echte Dateien haben).</summary>
    private static readonly (string Name, byte[] Head)[] Good =
    [
        ("a.png", Png), ("a.jpg", Jpg), ("a.jpeg", Jpg), ("a.gif", Ascii("GIF89a....")), ("a.gif", Ascii("GIF87a....")),
        ("a.webp", Cat(Ascii("RIFF"), [1, 2, 3, 4], Ascii("WEBPVP8 "))), ("a.avif", Ftyp("avif")), ("a.avif", Ftyp("mif1", "miaf", "avif")),
        ("a.mp4", Ftyp("isom", "mp42")), ("a.m4a", Ftyp("M4A ")), ("a.webm", [0x1A, 0x45, 0xDF, 0xA3, 0x9F]),
        ("a.mp3", Ascii("ID3\x04")), ("a.mp3", [0xFF, 0xFB, 0x90, 0x00]), ("a.ogg", Ascii("OggS\0")), ("a.wav", Cat(Ascii("RIFF"), [1, 2, 3, 4], Ascii("WAVEfmt "))),
        ("a.flac", Ascii("fLaC\0")), ("a.pdf", Ascii("%PDF-1.7\n")), ("a.pdf", Cat(new byte[500], Ascii("%PDF-1.4"))),
        ("a.docx", Zip), ("a.xlsx", Zip), ("a.csv", Ascii("a;b\n1;2\n")), ("a.csv", Cat([0xFF, 0xFE], [(byte)'a', 0, (byte)',', 0, (byte)'b', 0])), ("a.csv", Cat([0xEF, 0xBB, 0xBF], Ascii("Name,Alter\n"))),
    ];

    [Fact]
    public void Only_the_fixed_list_of_extensions_is_shown_and_never_pages_or_unknown_types()
    {
        foreach (var (name, _) in Good) Assert.NotNull(FilePreview.ByName(name));
        Assert.Equal("image/png", FilePreview.ByName("Bild.PNG")!.ContentType);                        // Gross/Klein egal, der Typ steht in der Liste
        Assert.Equal(["image/jpeg", "image/jpeg"], new[] { FilePreview.ByName("a.jpg")!.ContentType, FilePreview.ByName("a.jpeg")!.ContentType });
        Assert.Equal("audio/mp4", FilePreview.ByName("a.m4a")!.ContentType);
        Assert.Equal("application/pdf", FilePreview.ByName("a.pdf")!.ContentType);
        // Word, Excel und csv gehen nur fuer den eigenen Abruf der Seite: als unbekannter Typ, nie als Dokument (die Seite zeigt sie selbst)
        foreach (var n in new[] { "a.docx", "a.XLSX", "a.csv" }) Assert.Equal("application/octet-stream", FilePreview.ByName(n)!.ContentType);
        Assert.Equal((PreviewKind.Word, PreviewKind.Sheet, PreviewKind.Sheet), (FilePreview.ByName("a.docx")!.Kind, FilePreview.ByName("a.xlsx")!.Kind, FilePreview.ByName("a.csv")!.Kind));
        // HTML, SVG, XML, Skripte, alte Office-Dateien (OLE), Makros, Archive, ohne oder mit leerer Endung: nie
        foreach (var bad in new[] { "a.html", "a.htm", "a.xhtml", "a.svg", "a.svgz", "a.xml", "a.xsl", "a.js", "a.mjs", "a.json", "a.txt", "a.doc", "a.xls", "a.docm", "a.xlsm", "a.dotx", "a.zip", "a.exe", "a.sh", "a.php",
                     "a", "a.", ".", "", "png", "a.png.html", "a.html.", "a.png ", "a.pn\0g" })
            Assert.Null(FilePreview.ByName(bad));
        Assert.Null(FilePreview.ByName(null));
        Assert.Equal("png", FilePreview.ByName("evil.html.png")!.Extension);                           // zaehlt die letzte Endung; der Inhalt wird dann gegen png geprueft
    }

    [Fact]
    public void The_start_of_the_file_must_match_its_extension_and_a_page_never_passes()
    {
        foreach (var (name, head) in Good) Assert.True(FilePreview.MatchesContent(FilePreview.ByName(name)!, head), name);
        // HTML, Skript, SVG und Text unter jeder Endung der Liste: nein
        var pages = new[] { Html, Ascii("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>"), Ascii("<?xml version='1.0'?><svg/>"), Ascii("alert(1)"), [], [0] };
        foreach (var ext in new[] { "png", "jpg", "gif", "webp", "avif", "mp4", "webm", "mp3", "ogg", "wav", "flac", "m4a", "pdf", "docx", "xlsx" })
            foreach (var page in pages) Assert.False(FilePreview.MatchesContent(FilePreview.ByName("a." + ext)!, page), ext + ": " + Encoding.ASCII.GetString(page));
        // ein Typ unter fremder Endung: nein (ein PNG heisst nicht .jpg)
        Assert.False(FilePreview.MatchesContent(FilePreview.ByName("a.jpg")!, Png));
        Assert.False(FilePreview.MatchesContent(FilePreview.ByName("a.png")!, Jpg));
        Assert.False(FilePreview.MatchesContent(FilePreview.ByName("a.wav")!, Cat(Ascii("RIFF"), [1, 2, 3, 4], Ascii("WEBP"))));
        Assert.False(FilePreview.MatchesContent(FilePreview.ByName("a.avif")!, Ftyp("isom", "mp42")));                         // ein mp4 ist kein avif
        Assert.False(FilePreview.MatchesContent(FilePreview.ByName("a.mp4")!, Png));
        Assert.False(FilePreview.MatchesContent(FilePreview.ByName("a.pdf")!, Cat(new byte[HeadHole], Ascii("%PDF-1.4"))));    // der PDF-Kopf liegt hoechstens im ersten KiB
        Assert.False(FilePreview.MatchesContent(FilePreview.ByName("a.mp3")!, [0xFF, 0x1F]));
    }

    private const int HeadHole = 1100;

    [Fact]
    public void Word_and_Excel_must_be_zip_files_and_csv_must_be_text_and_never_the_other_way_round()
    {
        // ein ZIP unter docx/xlsx ja; alles andere nicht: ein altes OLE-Dokument (.doc/.xls unter neuer Endung), HTML, ein PDF, ein leeres ZIP ohne Dateien (PK 05 06), Text
        foreach (var ext in new[] { "docx", "xlsx" })
        {
            var t = FilePreview.ByName("a." + ext)!;
            Assert.True(FilePreview.MatchesContent(t, Zip));
            foreach (var bad in new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, Html, Ascii("%PDF-1.7"), [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0], Ascii("a,b\n1,2"), [] })
                Assert.False(FilePreview.MatchesContent(t, bad), ext);
        }
        // csv: Text ja (auch mit UTF-8-Kennung, auch UTF-16 mit Kennung), Binaeres nein; ein ZIP oder PNG mit Nullbytes ist kein csv
        var csv = FilePreview.ByName("a.csv")!;
        Assert.False(FilePreview.MatchesContent(csv, Zip));
        Assert.False(FilePreview.MatchesContent(csv, Png));
        Assert.False(FilePreview.MatchesContent(csv, [(byte)'a', 0, (byte)'b']));       // UTF-16 ohne Kennung: unklar, nicht gezeigt
        Assert.True(FilePreview.MatchesContent(csv, Html));                              // Text ist harmlos: textContent, und die Antwort ist nie ein Dokument (HTTP-Test)
        // Groessen wie bei Bild und PDF; auch auf Root-Orten (der Abruf ist kein Strom)
        const long mib = 1024 * 1024;
        Assert.Equal("preview_too_large", FilePreview.Refuse("a.docx", 51 * mib, false, 50 * mib, 2048 * mib));
        Assert.Null(FilePreview.Refuse("a.xlsx", 50 * mib, true, 50 * mib, 2048 * mib));
        Assert.Null(FilePreview.Refuse("a.csv", 1, true, 50 * mib, 2048 * mib));
    }

    [Fact]
    public void Size_limits_apply_to_images_and_pdf_and_root_places_only_show_images_and_pdf()
    {
        const long mib = 1024 * 1024, preview = 50 * mib, hard = 2048 * mib;
        Assert.Null(FilePreview.Refuse("a.png", 50 * mib, false, preview, hard));
        Assert.Equal("preview_too_large", FilePreview.Refuse("a.png", 50 * mib + 1, false, preview, hard));
        Assert.Equal("preview_too_large", FilePreview.Refuse("a.pdf", 51 * mib, false, preview, hard));
        Assert.Null(FilePreview.Refuse("a.mp4", 500 * mib, false, preview, hard));                                           // kommt in Teilen, nur die Grenze fuer jede Datei
        Assert.Equal("preview_too_large", FilePreview.Refuse("a.mp4", hard + 1, false, preview, hard));
        Assert.Equal("preview_too_large", FilePreview.Refuse("a.png", 3 * mib, false, 10 * mib, 2 * mib));                    // nie ueber der Grenze fuer jede Datei
        Assert.Equal("preview_type", FilePreview.Refuse("a.svg", 1, false, preview, hard));
        Assert.Equal("preview_type", FilePreview.Refuse("a.html", 1, true, preview, hard));
        Assert.Equal("preview_root_type", FilePreview.Refuse("a.mp4", 1, true, preview, hard));
        Assert.Equal("preview_root_type", FilePreview.Refuse("a.mp3", 1, true, preview, hard));
        Assert.Null(FilePreview.Refuse("a.png", 1, true, preview, hard));
        Assert.Null(FilePreview.Refuse("a.pdf", 1, true, preview, hard));
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99, true, false)]
    [InlineData("bytes=100-", 1000, 100, 999, true, false)]
    [InlineData("bytes=900-5000", 1000, 900, 999, true, false)]                         // das Ende wird gekappt
    [InlineData("bytes=-100", 1000, 900, 999, true, false)]                             // die letzten 100
    [InlineData("bytes=-5000", 1000, 0, 999, true, false)]                              // mehr als es gibt: alles
    [InlineData("bytes=999-999", 1000, 999, 999, true, false)]
    [InlineData("BYTES=5-6", 1000, 5, 6, true, false)]
    [InlineData("bytes=1000-", 1000, 0, 0, false, true)]                                // hinter dem Ende: 416
    [InlineData("bytes=5000-6000", 1000, 0, 0, false, true)]
    [InlineData("bytes=-0", 1000, 0, 0, false, true)]
    [InlineData("bytes=0-1,5-6", 1000, 0, 999, false, false)]                           // mehrere Bereiche: die ganze Datei
    [InlineData("bytes=9-3", 1000, 0, 999, false, false)]                               // unsinnig: ignoriert
    [InlineData("bytes=a-b", 1000, 0, 999, false, false)]
    [InlineData("bytes=-", 1000, 0, 999, false, false)]
    [InlineData("items=0-5", 1000, 0, 999, false, false)]
    [InlineData("", 1000, 0, 999, false, false)]
    public void Range_headers_are_answered_like_http_says(string header, long length, long start, long end, bool partial, bool unsatisfiable)
    {
        var r = FilePreview.ResolveRange(header, ifRange: false, length, allowed: true);
        Assert.Equal((start, end, partial, unsatisfiable), (r.Start, r.End, r.Partial, r.Unsatisfiable));
    }

    [Fact]
    public void Range_is_ignored_where_it_is_not_allowed_or_a_validator_is_asked_for()
    {
        Assert.False(FilePreview.ResolveRange("bytes=5-6", false, 1000, allowed: false).Partial);                          // Root-Ort: einmal, ganz
        Assert.False(FilePreview.ResolveRange("bytes=5-6", ifRange: true, 1000, allowed: true).Partial);                   // If-Range ohne Validator von uns: ganze Datei
        Assert.False(FilePreview.ResolveRange("bytes=5000-", false, 1000, allowed: false).Unsatisfiable);
    }
}
