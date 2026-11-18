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
