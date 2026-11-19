using System.Globalization;
using System.IO.Compression;
using System.Text;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JulOS.Backend.Services;

/// <summary>
/// Einstellungen des taeglichen Backups (siehe docs/BACKUP.md). <c>JULOS_BACKUP</c>: leer = an, aber nur wenn der Backup-Ordner eingebunden ist
/// (sonst Warnung im Log und in "Ueber JulOS", nie ein stilles Schreiben in den fluechtigen Container), <c>on</c> = an, ein fehlender Ordner ist ein Fehler, <c>off</c> = aus.
/// </summary>
public sealed record BackupOptions(bool Off, bool Required, string Dir, TimeOnly Time, int KeepDays, bool CopyKey, string? DataDir)
{
    public const string DefaultDir = "/backup";

    public static BackupOptions Parse(string? mode, string? dir, string? time, string? keepDays, string? key, string? dataDir)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        if (m is not ("" or "on" or "off")) throw new InvalidOperationException("JULOS_BACKUP muss on oder off sein (oder leer).");
        var at = new TimeOnly(3, 0);
        if (!string.IsNullOrWhiteSpace(time) && !TimeOnly.TryParseExact(time.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
            throw new InvalidOperationException("JULOS_BACKUP_TIME muss HH:MM sein (zum Beispiel 03:30, Zeit des Servers, im Container ohne TZ ist das UTC).");
        var keep = 14;
        if (!string.IsNullOrWhiteSpace(keepDays) && (!int.TryParse(keepDays.Trim(), CultureInfo.InvariantCulture, out keep) || keep < 1))
            throw new InvalidOperationException("JULOS_BACKUP_KEEP_DAYS muss eine ganze Zahl ab 1 sein.");
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (k is not ("" or "copy" or "off")) throw new InvalidOperationException("JULOS_BACKUP_KEY muss copy oder off sein (oder leer).");
        return new BackupOptions(m == "off", m == "on", string.IsNullOrWhiteSpace(dir) ? DefaultDir : dir.Trim(), at, keep, k != "off", string.IsNullOrWhiteSpace(dataDir) ? null : dataDir);
    }
}

public sealed record BackupFile(string Path, DateTimeOffset When, long Bytes);

/// <summary>Zustand fuer die Anzeige: off, nodir (Ordner fehlt oder nicht beschreibbar), pending (noch keins), ok, failed.</summary>
public sealed record BackupStatus(string State, DateTimeOffset? LastAt = null, long? Bytes = null, string? Message = null, string? Time = null);

/// <summary>
/// Schreibt Datenbank-Auszug (<c>julos-db-ZEIT.sql.gz</c>) und eine Kopie der Schluesseldatei (<c>secret.key</c>) als getrennte Dateien in den Backup-Ordner,
/// rechte nur fuer den Benutzer (0600; den Ordner legt der Betreiber an, am besten mit 0700, er wird nie vom Server angelegt), und raeumt Auszuege auf, die aelter als die Aufbewahrungszeit sind (den neuesten nie).
/// Ein Auszug entsteht als .tmp und wird erst am Ende umbenannt: eine abgebrochene Sicherung hinterlaesst nie einen Auszug, der echt aussieht.
/// </summary>
public sealed class BackupService(NpgsqlDataSource db, BackupOptions options, TimeProvider clock, ILogger<BackupService> log)
{
    public const string Prefix = "julos-db-", Suffix = ".sql.gz", KeyName = "secret.key";
    private const string Stamp = "yyyyMMdd'T'HHmmss'Z'";

    private volatile BackupStatus _status = new("pending");
    public BackupOptions Options => options;
    public BackupStatus Status => _status;

    /// <summary>Warum der Ordner nicht taugt, oder null.</summary>
    public string? TargetProblem()
    {
        if (!Directory.Exists(options.Dir)) return $"Backup-Ordner {options.Dir} fehlt (Volume nicht eingebunden?)";
        var probe = Path.Combine(options.Dir, ".julos-write-test");
        try { File.WriteAllText(probe, ""); File.Delete(probe); return null; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return $"In den Backup-Ordner {options.Dir} darf der Server nicht schreiben (Besitzer muss der Server-Benutzer sein)"; }
    }

    /// <summary>Beim Start: Status aus dem Ordner lesen und Probleme laut melden.</summary>
    public void Initialize()
    {
        if (options.Off) { _status = new("off"); return; }
        if (TargetProblem() is { } problem)
        {
            _status = new("nodir", Message: problem);
            if (options.Required) log.LogError("Backup ist an (JULOS_BACKUP=on), geht aber nicht: {Problem}. Es wird NICHTS gesichert.", problem);
            else log.LogWarning("Kein Backup: {Problem}. Ohne Sicherung sind Verbindungen und gespeicherte Passwörter nach einem Datenverlust weg (siehe docs/BACKUP.md; JULOS_BACKUP=off schaltet diese Meldung ab).", problem);
            return;
        }
        _status = Latest() is { } f ? new("ok", f.When, f.Bytes, Time: options.Time.ToString("HH:mm")) : new("pending", Time: options.Time.ToString("HH:mm"));
    }

    /// <summary>Ob es Zeit fuer einen Nachholer ist: kein Auszug oder der neueste ist aelter als 25 Stunden (der Server war zur Sicherungszeit aus).</summary>
    public bool Overdue() => !Directory.Exists(options.Dir) || (Latest()?.When ?? DateTimeOffset.MinValue) < clock.GetUtcNow().AddHours(-25);

    public bool CanRun => !options.Off && Directory.Exists(options.Dir);

    /// <summary>Eine Sicherung jetzt. Gibt den Pfad des Auszugs zurueck. Der Status steht danach auf ok oder failed.</summary>
    public async Task<string> RunAsync(CancellationToken ct = default)
    {
        try
        {
            if (TargetProblem() is { } problem) throw new InvalidOperationException(problem);
            var now = clock.GetUtcNow();
            var final = Path.Combine(options.Dir, Prefix + now.ToString(Stamp, CultureInfo.InvariantCulture) + Suffix);
            var tmp = final + ".tmp";
            DumpSummary summary;
            try
            {
                await using (var fs = PrivateFile.Create(tmp))
                await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                await using (var text = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" })
                    summary = await DbDump.WriteAsync(db, text, now, ct);
                File.Move(tmp, final, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }

            var keyNote = CopyKey();
            var removed = Prune(now);
            var size = new FileInfo(final).Length;
            _status = new("ok", now, size, Time: options.Time.ToString("HH:mm"));
            log.LogInformation("Backup geschrieben: {File} ({Bytes} Byte, {Rows} Zeilen){Key}{Removed}", Path.GetFileName(final), size, summary.TotalRows, keyNote, removed > 0 ? $", {removed} alte(s) entfernt" : "");
            return final;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _status = new("failed", _status.LastAt, _status.Bytes, e.Message, options.Time.ToString("HH:mm"));
            log.LogError(e, "Backup fehlgeschlagen.");
            throw;
        }
    }

    /// <summary>Kopiert den Schluessel aus dem Datenordner neben die Auszuege (nur wenn neu oder geaendert). Gibt einen Hinweis fuers Log zurueck.</summary>
    private string CopyKey()
    {
        if (!options.CopyKey) return ", Schlüssel nicht kopiert (JULOS_BACKUP_KEY=off)";
        if (options.DataDir is null) return "";
        var src = Path.Combine(options.DataDir, KeyName);
        if (!File.Exists(src)) return ", kein secret.key im Datenordner (Schlüssel kommt aus JULOS_SECRET_KEY: den gesondert sichern)";
        var dest = Path.Combine(options.Dir, KeyName);
        var content = File.ReadAllBytes(src);
        if (File.Exists(dest) && File.ReadAllBytes(dest).AsSpan().SequenceEqual(content)) return ", Schlüssel unverändert";
        var tmp = dest + ".tmp";
        try
        {
            using (var fs = PrivateFile.Create(tmp)) fs.Write(content);
            File.Move(tmp, dest, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        return ", Schlüssel kopiert";
    }

    /// <summary>Entfernt Auszuege, die aelter als die Aufbewahrungszeit sind; der neueste bleibt immer. Fremde Dateien im Ordner bleiben unberuehrt.</summary>
    public int Prune(DateTimeOffset now)
    {
        var files = Listing().ToList();                                  // neueste zuerst
        var limit = now.AddDays(-options.KeepDays);
        var removed = 0;
        foreach (var f in files.Skip(1).Where(f => f.When < limit))
        {
            File.Delete(f.Path);
            removed++;
        }
        return removed;
    }

    public BackupFile? Latest() => Listing().FirstOrDefault();

    /// <summary>Die Auszuege im Ordner, neueste zuerst. Die Zeit steht im Dateinamen (nicht in der Aenderungszeit, die ein Kopieren veraendert).</summary>
    private IEnumerable<BackupFile> Listing()
    {
        if (!Directory.Exists(options.Dir)) return [];
        return Directory.EnumerateFiles(options.Dir, Prefix + "*" + Suffix)
            .Select(p => (Path: p, Name: Path.GetFileName(p)))
            .Select(x => DateTimeOffset.TryParseExact(x.Name[Prefix.Length..^Suffix.Length], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? new BackupFile(x.Path, t, new FileInfo(x.Path).Length) : null)
            .OfType<BackupFile>()
            .OrderByDescending(x => x.When);
    }

    /// <summary>Naechster Sicherungszeitpunkt nach <paramref name="now"/> (taeglich zur Ortszeit <paramref name="at"/>).</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeZoneInfo zone, TimeOnly at)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = local.Date.Add(at.ToTimeSpan());
        if (new DateTimeOffset(day, zone.GetUtcOffset(day)) <= now) day = day.AddDays(1);
        return new DateTimeOffset(day, zone.GetUtcOffset(day));
    }
}
