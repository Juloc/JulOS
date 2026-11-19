namespace JulOS.Integrations;

/// <summary>
/// Dateinamen, die ueber die Zwischenablage kommen (vom Browser oder vom fernen Rechner), sind nie vertrauenswuerdig. Hier wird aus
/// jedem Namen ein einfacher Name ohne Pfad, der auf Windows und Linux gueltig ist, und es wird gesagt, welche Dateiendungen nicht
/// durchkommen (sie wuerden laufen, sobald jemand sie im Dateimanager der VM oeffnet).
/// </summary>
public static class ClipboardNames
{
    public const int MaxLength = 120;

    private static readonly string[] BlockedExtensions =
        [".desktop", ".lnk", ".scf", ".url", ".bat", ".cmd", ".exe", ".msi", ".com", ".sh", ".ps1", ".vbs", ".js"];

    private static readonly string[] DeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Der letzte Teil des Pfades, ohne Zeichen, die Windows verbietet (und ohne die, mit denen man Endungen vortaeuscht: Steuerzeichen
    /// fuer die Schreibrichtung). Null, wenn nichts Brauchbares uebrig bleibt.
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var cut = raw.LastIndexOfAny(['/', '\\']);
        var name = cut >= 0 ? raw[(cut + 1)..] : raw;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c < ' ' || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\u007F' or '­'
                || (c >= '​' && c <= '‏') || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩') || c == '﻿')
            {
                chars[i] = '_';
            }
        }

        name = new string(chars).TrimEnd(' ', '.').TrimStart(' ');
        if (name.Length == 0 || name.All(c => c == '.' || c == '_'))
        {
            return null;
        }

        if (name.Length > MaxLength)
        {
            var dot = name.LastIndexOf('.');
            var extension = dot > 0 && name.Length - dot <= 20 ? name[dot..] : "";
            name = name[..(MaxLength - extension.Length)].TrimEnd(' ', '.') + extension;
        }

        var stem = name.Split('.')[0].TrimEnd(' ');
        if (DeviceNames.Any(device => string.Equals(stem, device, StringComparison.OrdinalIgnoreCase)))
        {
            name = "_" + name;
        }

        return name;
    }

    /// <summary>So lang darf ein relativer Pfad sein (so viel passt in einen Eintrag einer Dateiliste der Zwischenablage: 259 Zeichen).</summary>
    public const int MaxPathLength = 259;

    /// <summary>So viele Ordner dürfen ineinander liegen.</summary>
    public const int MaxDepth = 16;

    /// <summary>
    /// Ein relativer Pfad (Trenner / oder \) aus dem Browser, der Teile eines Ordners bis zu einer Datei beschreibt, strikt geprueft: kein absoluter Pfad
    /// (fuehrender Trenner, Laufwerk, UNC), kein ".." und kein ".", keine leeren Teile ("a//b", "a/"), nicht laenger als <see cref="MaxPathLength"/>, nicht
    /// tiefer als <see cref="MaxDepth"/>. Jeder Teil wird wie ein Dateiname bereinigt (verbotene Zeichen werden zu "_", Geraetenamen bekommen ein Praefix).
    /// Null bei allem, was ein Pfad-Trick sein koennte: der Pfad wird abgelehnt, nicht stillschweigend zurechtgestutzt.
    /// </summary>
    public static IReadOnlyList<string>? SafePath(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxPathLength || raw[0] is '/' or '\\' || raw.Contains('\0'))
        {
            return null;
        }

        // "C:..." (Laufwerk, auch ohne Trenner dahinter) ist nirgends ein gewoehnlicher Name
        if (raw.Length >= 2 && raw[1] == ':' && char.IsAsciiLetter(raw[0]))
        {
            return null;
        }

        var parts = raw.Split('/', '\\');
        if (parts.Length > MaxDepth)
        {
            return null;
        }

        var clean = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or "..")
            {
                return null;
            }

            // nur Punkte oder Leerzeichen sind keine Namen, auch nicht in Verkleidung ("... ", ". .")
            if (part.All(c => c is '.' or ' '))
            {
                return null;
            }

            if (Sanitize(part) is not { } safe)
            {
                return null;
            }

            clean.Add(safe);
        }

        return clean;
    }

    /// <summary>Die Endung (mit Punkt), wenn Dateien mit ihr nicht angenommen werden, sonst null.</summary>
    public static string? BlockedExtension(string name)
    {
        foreach (var extension in BlockedExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }

        return null;
    }
}
