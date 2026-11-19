namespace JulOS.Integrations.Display;

/// <summary>
/// Grenzen und Ablage der Dateien in der Zwischenablage (siehe <see cref="ClipboardBridge"/>). Vom Browser kommende Dateien liegen bis zum Einfuegen im
/// fernen Rechner als Dateien in einem Ordner des Servers (nicht im Speicher): <see cref="MaxFileBytes"/> gilt je Datei und fuer alle Dateien einer
/// Ablage zusammen. JULOS_CLIP_MAX_MB stellt die Grenze ein (Standard 2048, also 2 GiB), JULOS_CLIP_DIR den Ordner (Standard ein Unterordner des
/// temporaeren Ordners des Servers). Dateien vom fernen Rechner zum Browser fliessen durch den Server, ohne dass er sie ablegt: dort gilt keine Grenze.
/// </summary>
public sealed record ClipboardOptions(long MaxFileBytes, string TempRoot)
{
    /// <summary>Hoechstens so viele Dateien (ohne Ordner) in einer Ablage.</summary>
    public const int MaxFiles = 200;

    /// <summary>Hoechstens so viele Eintraege (Dateien und leere Ordner) in einer Ablage: die Nummer eines Eintrags ist ein Byte.</summary>
    public const int MaxEntries = 255;

    public const long DefaultMegabytes = 2048;
    public const long LargestMegabytes = 65536;

    /// <summary>So viel Platz soll auf dem Datentraeger nach dem Ablegen noch frei sein; sonst wird die Ablage abgelehnt.</summary>
    public const long FreeSpaceReserve = 256L * 1024 * 1024;

    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "julos-clip");

    /// <summary>Die Einstellung des Servers; <see cref="Configure"/> setzt sie beim Start, ohne das gilt der Standard.</summary>
    public static ClipboardOptions Default { get; private set; } = new(DefaultMegabytes * 1024 * 1024, DefaultRoot);

    /// <summary>Liest JULOS_CLIP_MAX_MB und JULOS_CLIP_DIR: leer oder Unsinn ergibt den Standard, die Zahl wird auf 1 bis 65536 begrenzt.</summary>
    public static ClipboardOptions Parse(string? megabytes, string? directory)
    {
        var mb = long.TryParse(megabytes?.Trim(), out var value) && value > 0 ? Math.Min(value, LargestMegabytes) : DefaultMegabytes;
        var root = string.IsNullOrWhiteSpace(directory) ? DefaultRoot : directory.Trim();
        return new ClipboardOptions(mb * 1024 * 1024, root);
    }

    public static void Configure(ClipboardOptions options) => Default = options;
}
