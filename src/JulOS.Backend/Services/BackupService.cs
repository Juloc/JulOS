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
public sealed record BackupOptions(bool Off, bool Required, string Dir, TimeOnly Time, int KeepDays, bool CopyKey, string? DataDir, BackupCopyOptions? Copy = null)
{
    public const string DefaultDir = "/backup";

    /// <summary>Alle Einstellungen des Backups (auch des zweiten Ziels) ueber einen Lesezugriff auf Umgebung oder Konfiguration.</summary>
    public static BackupOptions FromSettings(Func<string, string?> get)
    {
        var o = Parse(get("JULOS_BACKUP"), get("JULOS_BACKUP_DIR"), get("JULOS_BACKUP_TIME"), get("JULOS_BACKUP_KEEP_DAYS"), get("JULOS_BACKUP_KEY"), get("JULOS_DATA"));
        return o with { Copy = BackupCopyOptions.Parse(get, o.Dir, o.DataDir) };
    }

    public static BackupOptions Parse(string? mode, string? dir, string? time, string? keepDays, string? key, string? dataDir, BackupCopyOptions? copy = null)
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
        return new BackupOptions(m == "off", m == "on", string.IsNullOrWhiteSpace(dir) ? DefaultDir : dir.Trim(), at, keep, k != "off", string.IsNullOrWhiteSpace(dataDir) ? null : dataDir, copy);
    }
}

public sealed record BackupFile(string Path, DateTimeOffset When, long Bytes);

/// <summary>Zustand fuer die Anzeige: off, nodir (Ordner fehlt oder nicht beschreibbar), pending (noch keins), ok, failed.</summary>
public sealed record BackupStatus(string State, DateTimeOffset? LastAt = null, long? Bytes = null, string? Message = null, string? Time = null, CopyStatus? Copy = null);

/// <summary>Zustand des zweiten Ziels: off (nicht eingerichtet), pending (noch nicht kopiert), ok (<c>LastAt</c> = letzte erfolgreiche Kopie), failed (<c>Message</c>, <c>LastAt</c> = letzte erfolgreiche Kopie davor).</summary>
public sealed record CopyStatus(string State, DateTimeOffset? LastAt = null, string? Message = null, string? Target = null);

/// <summary>
/// Schreibt Datenbank-Auszug (<c>julos-db-ZEIT.sql.gz</c>) und eine Kopie der Schluesseldatei (<c>secret.key</c>) als getrennte Dateien in den Backup-Ordner,
/// rechte nur fuer den Benutzer (0600; den Ordner legt der Betreiber an, am besten mit 0700, er wird nie vom Server angelegt), und raeumt Auszuege auf, die aelter als die Aufbewahrungszeit sind (den neuesten nie).
/// Ein Auszug entsteht als .tmp und wird erst am Ende umbenannt: eine abgebrochene Sicherung hinterlaesst nie einen Auszug, der echt aussieht.
/// Optional geht danach eine Kopie an ein zweites Ziel ausserhalb des Rechners (<see cref="IBackupTarget"/>, siehe docs/BACKUP.md): ein Fehler dort ist nur eine Warnung
/// im Status, das lokale Backup bleibt gueltig, der naechste Lauf holt Fehlendes nach.
/// </summary>
public sealed class BackupService(NpgsqlDataSource db, BackupOptions options, TimeProvider clock, ILogger<BackupService> log, Func<IBackupTarget>? copyTarget = null)
{
    public const string Prefix = "julos-db-", Suffix = ".sql.gz", KeyName = "secret.key";
    private const string Stamp = "yyyyMMdd'T'HHmmss'Z'";

    private volatile BackupStatus _status = new("pending");
    private volatile CopyStatus _copy = new("off");
    private readonly SemaphoreSlim _copyLock = new(1, 1);
    public BackupOptions Options => options;
    public BackupStatus Status => _status with { Copy = _copy };
    private bool CopyEnabled => copyTarget is not null && options.Copy is { Enabled: true };

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
        _copy = CopyEnabled && !options.Off ? new CopyStatus("pending", Target: options.Copy!.Describe()) : new CopyStatus("off");
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
            await CopyToTargetAsync(ct);                                      // wirft nie (ausser Abbruch): das lokale Backup ist fertig
            return final;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _status = new("failed", _status.LastAt, _status.Bytes, e.Message, options.Time.ToString("HH:mm"));
            log.LogError(e, "Backup fehlgeschlagen.");
            throw;
        }
    }

    /// <summary>
    /// Gleicht das zweite Ziel mit dem lokalen Ordner ab: fehlende Auszuege (innerhalb der Aufbewahrungszeit, neueste zuerst), die Schluesselkopie wenn neu oder geaendert, danach
    /// dieselbe Aufbewahrung wie lokal (der neueste Auszug am Ziel bleibt immer, fremde Dateien bleiben unberuehrt). Fehler stehen im Status und im Log, nie als Ausnahme.
    /// </summary>
    public async Task CopyToTargetAsync(CancellationToken ct = default)
    {
        if (!CopyEnabled) return;
        await _copyLock.WaitAsync(ct);
        try
        {
            var description = options.Copy!.Describe();
            try
            {
                var report = await SyncTargetAsync(ct);
                _copy = new CopyStatus("ok", clock.GetUtcNow(), Target: description);
                log.LogInformation("Zweites Ziel {Target}: {Report}", description, report);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var message = e is InvalidOperationException or IOException or UnauthorizedAccessException ? e.Message : e.GetType().Name + ": " + e.Message;
                _copy = new CopyStatus("failed", _copy.LastAt, message, description);
                log.LogWarning("Zweites Ziel {Target} nicht erreicht oder beschrieben: {Message}. Das lokale Backup ist in Ordnung, der nächste Lauf holt es nach.", description, message);
            }
        }
        finally { _copyLock.Release(); }
    }

    private async Task<string> SyncTargetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var target = copyTarget!();
        await target.OpenAsync(ct);
        var remote = (await target.ListAsync(ct)).ToDictionary(f => f.Name, f => f.Bytes, StringComparer.Ordinal);

        // liegengebliebene Zwischendateien eines abgebrochenen Kopierens aufraeumen (nur unsere Namen)
        foreach (var stale in remote.Keys.Where(n => n.EndsWith(".tmp", StringComparison.Ordinal) && (n.StartsWith(Prefix, StringComparison.Ordinal) || n == KeyName + ".tmp")).ToList())
        {
            await target.DeleteAsync(stale, ct);
            remote.Remove(stale);
        }

        var limit = now.AddDays(-options.KeepDays);
        var local = Listing().ToList();                                    // neueste zuerst
        var uploaded = 0;
        foreach (var f in local.Where((f, i) => i == 0 || f.When >= limit))
        {
            var name = Path.GetFileName(f.Path);
            if (remote.TryGetValue(name, out var size) && size == f.Bytes) continue;
            await using (var fs = File.OpenRead(f.Path)) await target.PutAsync(name, fs, ct);
            remote[name] = f.Bytes;
            uploaded++;
        }

        var keyNote = "";
        var keyPath = Path.Combine(options.Dir, KeyName);
        if (options.CopyKey && File.Exists(keyPath))
        {
            var key = await File.ReadAllBytesAsync(keyPath, ct);
            var there = remote.ContainsKey(KeyName) ? await target.ReadAsync(KeyName, ct) : null;
            if (there is null || !there.AsSpan().SequenceEqual(key))
            {
                await target.PutAsync(KeyName, new MemoryStream(key), ct);
                keyNote = ", Schlüssel kopiert";
            }
        }

        var remoteDumps = remote.Select(kv => (Name: kv.Key, When: ParseDumpTime(kv.Key))).Where(x => x.When is not null).OrderByDescending(x => x.When).ToList();
        var removed = 0;
        foreach (var old in remoteDumps.Skip(1).Where(x => x.When < limit))
        {
            await target.DeleteAsync(old.Name, ct);
            removed++;
        }
        return $"{uploaded} Auszug/Auszüge kopiert{keyNote}{(removed > 0 ? $", {removed} alte(s) entfernt" : "")}";
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
            .Select(p => ParseDumpTime(Path.GetFileName(p)) is { } t ? new BackupFile(p, t, new FileInfo(p).Length) : null)
            .OfType<BackupFile>()
            .OrderByDescending(x => x.When);
    }

    /// <summary>Die Zeit aus dem Dateinamen eines Auszugs, null bei jedem anderen Namen (auch <c>.tmp</c>).</summary>
    public static DateTimeOffset? ParseDumpTime(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal) && fileName.EndsWith(Suffix, StringComparison.Ordinal) && fileName.Length > Prefix.Length + Suffix.Length
        && DateTimeOffset.TryParseExact(fileName[Prefix.Length..^Suffix.Length], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>Naechster Sicherungszeitpunkt nach <paramref name="now"/> (taeglich zur Ortszeit <paramref name="at"/>).</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeZoneInfo zone, TimeOnly at)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = local.Date.Add(at.ToTimeSpan());
        if (new DateTimeOffset(day, zone.GetUtcOffset(day)) <= now) day = day.AddDays(1);
        return new DateTimeOffset(day, zone.GetUtcOffset(day));
    }
}
