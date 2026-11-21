using System.Globalization;

namespace JulOS.Backend.Logic;

/// <summary>
/// Einstellungen der Dateien-App fuer Hosts per SFTP (docs/FILES.md). Write: JULOS_FILES_WRITE=on ist nur der RUECKFALL fuer das Schreiben (hochladen, umbenennen, verschieben, kopieren, loeschen, Ordner anlegen); der Schalter lebt in der App (SecurityConfig.FilesWrite, Einstellungen, Sicherheit) und gilt vor ihm. Standard
/// ist aus (Lesen bleibt an). MaxBytes: JULOS_FILES_MAX_MB, die Obergrenze je Datei und je Operation (Standard 2048). MaxSessionsPerUser: JULOS_FILES_MAX_SESSIONS, gleichzeitig offene SFTP-Verbindungen
/// je Benutzer (Standard 4); Idle: so lange bleibt eine unbenutzte Verbindung offen (60 s).
/// </summary>
public sealed record FilesOptions(bool Write, long MaxBytes, int MaxSessionsPerUser, TimeSpan Idle, int MaxJobsPerUser = 2)
{
    public const long DefaultMaxMb = 2048;
    public const int DefaultSessions = 4;

    public static readonly FilesOptions Default = new(false, DefaultMaxMb * 1024 * 1024, DefaultSessions, TimeSpan.FromSeconds(60));

    /// <summary>Liest die Einstellungen; Unbrauchbares faellt auf den Standard zurueck (nie auf "Schreiben an"). <paramref name="warn"/> bekommt einen Satz je Problem.</summary>
    public static FilesOptions Parse(string? write, string? maxMb, string? sessions, Action<string>? warn = null)
    {
        var on = false;
        if (!string.IsNullOrWhiteSpace(write))
        {
            var w = write.Trim().ToLowerInvariant();
            if (w == "on") on = true;
            else if (w != "off") warn?.Invoke($"JULOS_FILES_WRITE={write}: erlaubt sind off und on, es bleibt aus.");
        }
        var mb = DefaultMaxMb;
        if (!string.IsNullOrWhiteSpace(maxMb))
        {
            if (long.TryParse(maxMb.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 65536) mb = v;
            else warn?.Invoke($"JULOS_FILES_MAX_MB={maxMb}: erlaubt sind 1 bis 65536, es bleibt bei {DefaultMaxMb}.");
        }
        var n = DefaultSessions;
        if (!string.IsNullOrWhiteSpace(sessions))
        {
            if (int.TryParse(sessions.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 16) n = v;
            else warn?.Invoke($"JULOS_FILES_MAX_SESSIONS={sessions}: erlaubt sind 1 bis 16, es bleibt bei {DefaultSessions}.");
        }
        return new FilesOptions(on, mb * 1024 * 1024, n, TimeSpan.FromSeconds(60));
    }
}
