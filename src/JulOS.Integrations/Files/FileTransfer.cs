using System.Text;

namespace JulOS.Integrations.Files;

/// <summary>Grenzen einer Operation (Standard: Listen 2000 Eintraege, Tiefe 20, 10 000 Eintraege je Kopie oder Loeschen, 2 GiB je Datei und je Operation).</summary>
public sealed record FileLimits(int MaxEntries = 2000, int MaxDepth = 20, int MaxItems = 10_000, long MaxBytes = 2L * 1024 * 1024 * 1024, int BufferSize = 128 * 1024, long MaxPreview = 1024 * 1024)
{
    public static readonly FileLimits Default = new();
}

/// <summary>Fortschritt einer laufenden Operation, von mehreren Threads les- und schreibbar (der Auftrag schreibt, die Abfrage der Seite liest).</summary>
public sealed class TransferProgress
{
    private long _bytes, _bytesTotal;
    private int _items, _itemsTotal, _skipped, _skippedExisting;
    private volatile string? _current;

    public long BytesDone => Interlocked.Read(ref _bytes);
    public long BytesTotal => Interlocked.Read(ref _bytesTotal);
    public int ItemsDone => Volatile.Read(ref _items);
    public int ItemsTotal => Volatile.Read(ref _itemsTotal);
    /// <summary>Links und Sonderdateien, die nie verfolgt oder kopiert werden.</summary>
    public int Skipped => Volatile.Read(ref _skipped);
    /// <summary>Dateien, die es am Ziel schon gab und die ohne "ueberschreiben" nicht angefasst wurden.</summary>
    public int SkippedExisting => Volatile.Read(ref _skippedExisting);
    /// <summary>Der Name (nicht der Pfad) dessen, was gerade dran ist.</summary>
    public string? Current { get => _current; set => _current = value; }

    public void SetTotals(int items, long bytes) { Volatile.Write(ref _itemsTotal, items); Interlocked.Exchange(ref _bytesTotal, bytes); }
    public void AddBytes(long n) => Interlocked.Add(ref _bytes, n);
    public void ItemDone() => Interlocked.Increment(ref _items);
    public void Skip() => Interlocked.Increment(ref _skipped);
    public void SkipExisting() => Interlocked.Increment(ref _skippedExisting);
}

/// <summary>Was ein Baum enthaelt (ohne einem Link zu folgen): Dateien, Ordner, Links und Sonderdateien, Summe der Dateigroessen.</summary>
public sealed record TreeCount(int Files, int Directories, int Links, long Bytes)
{
    public int Items => Files + Directories + Links;
}

public sealed record TransferResult(int Files, int Directories, int Skipped, int SkippedExisting);

/// <summary>Eine Textvorschau: Text (nie HTML), ob die Datei laenger ist, und die Groesse.</summary>
public sealed record TextPreview(string Text, bool Truncated, long Size);

/// <summary>
/// Die Logik der Dateioperationen auf <see cref="ISftpFileSession"/>, ohne Netz testbar. Zusagen: einem Link wird bei Rekursion nie gefolgt (er wird gezaehlt, beim Loeschen
/// als Link entfernt, beim Kopieren uebersprungen); Tiefe und Anzahl sind begrenzt, bevor etwas geschieht; eine Datei wird erst unter einem temporaeren Namen geschrieben und dann
/// umbenannt, ein Abbruch oder Fehler raeumt den temporaeren Namen weg; bei Abbruch bleibt, was schon fertig kopiert war.
/// </summary>
public static class FileTransfer
{
    // ---------- Zaehlen ----------

    /// <summary>Zaehlt den Baum unter <paramref name="paths"/> (lstat, ohne Links zu folgen). Wirft <see cref="FileRuleException"/> bei zu vielen Eintraegen, zu grosser Tiefe oder zu grosser Summe.</summary>
    public static async Task<TreeCount> CountAsync(ISftpFileSession s, IReadOnlyList<string> paths, FileLimits limits, CancellationToken ct)
    {
        int files = 0, dirs = 0, links = 0;
        long bytes = 0;
        void Check() { if (files + dirs + links > limits.MaxItems) throw new FileRuleException("too_many_items", "Zu viele Einträge."); }
        async Task Walk(RemoteEntry e, int depth)
        {
            ct.ThrowIfCancellationRequested();
            switch (e.Kind)
            {
                case RemoteKind.Directory:
                    dirs++; Check();
                    if (depth >= limits.MaxDepth) throw new FileRuleException("too_deep", "Der Ordner ist zu tief verschachtelt.");
                    var l = await s.ListAsync(e.Path, limits.MaxItems - (files + dirs + links) + 1, ct);
                    foreach (var c in l.Entries) await Walk(c, depth + 1);
                    if (l.Truncated) throw new FileRuleException("too_many_items", "Zu viele Einträge.");
                    break;
                case RemoteKind.File: files++; bytes += e.Size; Check(); break;
                default: links++; Check(); break;
            }
        }
        foreach (var p in paths)
            await Walk(await s.StatAsync(p, follow: false, ct) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden."), 0);
        if (bytes > limits.MaxBytes) throw new FileRuleException("too_large", "Zu groß.");
        return new TreeCount(files, dirs, links, bytes);
    }

    // ---------- Schreiben ----------

    /// <summary>Der temporaere Name neben dem Ziel; der Zusatz ".part" zeigt bei einem Rest, was es war.</summary>
    internal static string TempName(string finalPath) => RemotePath.Join(RemotePath.Parent(finalPath), ".julos-" + Guid.NewGuid().ToString("N")[..12] + ".part");

    /// <summary>
    /// Schreibt <paramref name="source"/> nach <paramref name="finalPath"/>: erst unter einem temporaeren Namen im selben Ordner (neu angelegt, nie ueberschrieben), dann Umbenennen.
    /// Gibt es das Ziel und ist <paramref name="overwrite"/> falsch, ist es <see cref="RemoteFileError.Exists"/> (vor dem ersten Byte). Mehr als <paramref name="maxBytes"/> bricht ab
    /// und raeumt auf. Gibt die geschriebenen Bytes zurueck.
    /// </summary>
    public static Task<long> WriteAtomicAsync(ISftpFileSession dst, string finalPath, Stream source, bool overwrite, long maxBytes, int bufferSize, Action<long>? onBytes, CancellationToken ct) =>
        WriteAtomicAsync(dst, finalPath, source, overwrite, maxBytes, bufferSize, onBytes, existingKnown: false, existing: null, ct);

    /// <summary>Wie oben; mit <paramref name="existingKnown"/> weiss der Aufrufer schon, ob es das Ziel gibt (<paramref name="existing"/>, aus einer Ordnerliste), und das Ziel wird nicht noch einmal gesucht.</summary>
    internal static async Task<long> WriteAtomicAsync(ISftpFileSession dst, string finalPath, Stream source, bool overwrite, long maxBytes, int bufferSize, Action<long>? onBytes, bool existingKnown, RemoteEntry? existing, CancellationToken ct)
    {
        if (!existingKnown) existing = await dst.StatAsync(finalPath, follow: false, ct);
        if (existing is not null)
        {
            if (existing.Kind == RemoteKind.Directory) throw new FileRuleException("type_mismatch", "Am Ziel gibt es einen Ordner mit diesem Namen.");
            if (existing.Kind == RemoteKind.Link) throw new FileRuleException("target_is_link", "Am Ziel gibt es einen Link mit diesem Namen; er wird nie ersetzt.");
            if (!overwrite) throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.");
        }
        var temp = TempName(finalPath);
        long written = 0;
        try
        {
            await using (var w = await dst.CreateAsync(temp, ct))
            {
                var buffer = new byte[bufferSize];
                int n;
                while ((n = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    written += n;
                    if (written > maxBytes) throw new FileRuleException("too_large", "Die Datei ist zu groß.");
                    await w.WriteAsync(buffer.AsMemory(0, n), ct);
                    onBytes?.Invoke(n);
                }
                await w.FlushAsync(ct);
            }
            // Ist alles angekommen? Ein Ziel, das stillschweigend weniger schreibt, soll die Datei nicht ersetzen
            if ((await dst.StatAsync(temp, follow: false, ct))?.Size != written) throw new RemoteFileException(RemoteFileError.Failed, "Die Datei ist am Ziel unvollständig angekommen.");
            await dst.RenameAsync(temp, finalPath, overwrite, fromIsLink: false, ct);
            return written;
        }
        catch
        {
            await CleanUpAsync(dst, temp);
            throw;
        }
    }

    private static async Task CleanUpAsync(ISftpFileSession s, string path)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { if (await s.StatAsync(path, follow: false, cts.Token) is not null) await s.DeleteFileAsync(path, isLink: false, cts.Token); }
        catch (Exception) { /* beste Muehe; der Rest heisst .julos-*.part und ist sichtbar */ }
    }

    // ---------- Kopieren und Verschieben ----------

    /// <summary>Was beim Kopieren geschah, fuer das Verschieben danach: die Quelldateien, die sicher angekommen sind, und die Quellordner, die besucht wurden.</summary>
    private sealed class Tracker { public readonly List<(string Path, bool IsLink)> Files = []; public readonly List<string> Directories = []; public int Skipped; }

    /// <summary>
    /// Kopiert <paramref name="sources"/> in den Ordner <paramref name="dstDir"/> (auf <paramref name="dst"/>, das ein anderer Host sein kann). Ordner werden zusammengefuehrt, Links und
    /// Sonderdateien uebersprungen (ein Link auf eine Datei auf oberster Ebene wird als Datei kopiert), vorhandene Dateien ohne <paramref name="overwrite"/> nie angefasst
    /// (oberste Ebene: Fehler, tiefer: uebersprungen und gezaehlt). Gleiche Quelle und Ziel (<paramref name="sameHost"/>): ein Ordner in sich selbst ist abgelehnt.
    /// </summary>
    public static Task<TransferResult> CopyAsync(ISftpFileSession src, ISftpFileSession dst, IReadOnlyList<string> sources, string dstDir, bool sameHost, bool overwrite, FileLimits limits, TransferProgress progress, CancellationToken ct) =>
        RunCopyAsync(src, dst, sources, dstDir, sameHost, overwrite, limits, progress, null, ct);

    private static async Task<TransferResult> RunCopyAsync(ISftpFileSession src, ISftpFileSession dst, IReadOnlyList<string> sources, string dstDir, bool sameHost, bool overwrite, FileLimits limits, TransferProgress progress, Tracker? track, CancellationToken ct)
    {
        var target = await dst.StatAsync(dstDir, follow: true, ct) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Der Zielordner gibt es nicht.");
        if (target.Kind != RemoteKind.Directory) throw new RemoteFileException(RemoteFileError.NotADirectory, "Das Ziel ist kein Ordner.");
        var entries = new List<RemoteEntry>();
        foreach (var p in sources)
        {
            var e = await src.StatAsync(p, follow: false, ct) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
            if (sameHost && e.Kind == RemoteKind.Directory && RemotePath.IsInside(p, dstDir)) throw new FileRuleException("into_itself", "Ein Ordner lässt sich nicht in sich selbst kopieren.");
            if (sameHost && RemotePath.Parent(p) == dstDir) throw new FileRuleException("same_place", "Quelle und Ziel sind derselbe Ordner.");
            entries.Add(e);
        }
        var count = await CountAsync(src, sources, limits, ct);
        progress.SetTotals(count.Items, count.Bytes);

        var files = 0; var dirs = 0;
        async Task Copy(RemoteEntry e, string dest, int depth, bool top, Dictionary<string, RemoteEntry>? parentNames = null)
        {
            ct.ThrowIfCancellationRequested();
            progress.Current = e.Name;
            switch (e.Kind)
            {
                case RemoteKind.Directory:
                    if (depth > limits.MaxDepth) throw new FileRuleException("too_deep", "Der Ordner ist zu tief verschachtelt.");
                    var there = parentNames is null ? await dst.StatAsync(dest, follow: false, ct) : parentNames.GetValueOrDefault(e.Name);
                    Dictionary<string, RemoteEntry> names = [];
                    if (there is null) await dst.CreateDirectoryAsync(dest, ct);
                    else if (there.Kind != RemoteKind.Directory) throw new FileRuleException(there.Kind == RemoteKind.Link ? "target_is_link" : "type_mismatch", "Am Ziel gibt es eine Datei oder einen Link mit diesem Namen.");
                    else names = (await dst.ListAsync(dest, limits.MaxItems, ct)).Entries.ToDictionary(x => x.Name);        // einmal je Ordner, nicht je Datei
                    dirs++; progress.ItemDone(); track?.Directories.Add(e.Path);
                    foreach (var c in (await src.ListAsync(e.Path, limits.MaxItems, ct)).Entries) await Copy(c, RemotePath.Join(dest, c.Name), depth + 1, false, names);
                    break;
                case RemoteKind.File:
                case RemoteKind.Link when top && e.Resolved == RemoteKind.File:
                    var existing = parentNames is null ? await dst.StatAsync(dest, follow: false, ct) : parentNames.GetValueOrDefault(e.Name);
                    if (!top && !overwrite && existing is not null) { progress.SkipExisting(); progress.ItemDone(); if (track is not null) track.Skipped++; break; }
                    await using (var r = await src.OpenReadAsync(e.Path, ct))
                        await WriteAtomicAsync(dst, dest, r, overwrite, limits.MaxBytes, limits.BufferSize, progress.AddBytes, existingKnown: true, existing, ct);
                    files++; progress.ItemDone(); track?.Files.Add((e.Path, e.Kind == RemoteKind.Link));
                    break;
                default:
                    progress.Skip(); progress.ItemDone(); if (track is not null) track.Skipped++;
                    break;
            }
        }
        foreach (var e in entries) await Copy(e, RemotePath.Join(dstDir, e.Name), 0, true);
        return new TransferResult(files, dirs, progress.Skipped, progress.SkippedExisting);
    }

    /// <summary>
    /// Verschiebt. Auf demselben Host ist es ein Umbenennen, solange das Ziel frei ist (oder eine Datei ersetzt werden darf); sonst und ueber Hosts hinweg wird kopiert und danach
    /// geloescht, und zwar nur, was sicher angekommen ist: ein Link, den das Kopieren uebersprang, bleibt an seinem Platz (der Quellordner dann auch).
    /// </summary>
    public static async Task<TransferResult> MoveAsync(ISftpFileSession src, ISftpFileSession dst, IReadOnlyList<string> sources, string dstDir, bool sameHost, bool overwrite, FileLimits limits, TransferProgress progress, CancellationToken ct)
    {
        var target = await dst.StatAsync(dstDir, follow: true, ct) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Der Zielordner gibt es nicht.");
        if (target.Kind != RemoteKind.Directory) throw new RemoteFileException(RemoteFileError.NotADirectory, "Das Ziel ist kein Ordner.");
        int files = 0, dirs = 0;
        progress.SetTotals(sources.Count, 0);
        foreach (var p in sources)
        {
            ct.ThrowIfCancellationRequested();
            var e = await src.StatAsync(p, follow: false, ct) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
            progress.Current = e.Name;
            var dest = RemotePath.Join(dstDir, e.Name);
            if (sameHost)
            {
                if (p == dest || RemotePath.Parent(p) == dstDir) throw new FileRuleException("same_place", "Quelle und Ziel sind derselbe Ordner.");
                if (e.Kind == RemoteKind.Directory && RemotePath.IsInside(p, dstDir)) throw new FileRuleException("into_itself", "Ein Ordner lässt sich nicht in sich selbst verschieben.");
                var there = await dst.StatAsync(dest, follow: false, ct);
                if (there is not null && (there.Kind == RemoteKind.Link || e.Kind == RemoteKind.Link)) throw new FileRuleException("target_is_link", "Ein Link wird nie ersetzt und nie als Ziel ueberschrieben.");
                var renamable = there is null || (overwrite && there.Kind != RemoteKind.Directory && e.Kind != RemoteKind.Directory);
                if (there is not null && !overwrite) throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.");
                if (renamable)
                {
                    try { await src.RenameAsync(p, dest, overwrite && there is not null, e.Kind == RemoteKind.Link, ct); if (e.Kind == RemoteKind.Directory) dirs++; else files++; progress.ItemDone(); continue; }
                    catch (RemoteFileException x) when (x.Error == RemoteFileError.Failed) { /* zum Beispiel ein anderes Dateisystem: kopieren und loeschen */ }
                }
            }
            var track = new Tracker();
            var r = await RunCopyAsync(src, dst, [p], dstDir, sameHost, overwrite, limits, progress, track, ct);
            files += r.Files; dirs += r.Directories;
            foreach (var f in track.Files) { ct.ThrowIfCancellationRequested(); await src.DeleteFileAsync(f.Path, f.IsLink, ct); }
            for (var i = track.Directories.Count - 1; i >= 0; i--)                  // die tiefsten zuerst; ein Ordner mit Uebersprungenem bleibt (nicht leer)
            {
                try { await src.DeleteDirectoryAsync(track.Directories[i], ct); }
                catch (RemoteFileException x) when (x.Error is RemoteFileError.NotEmpty or RemoteFileError.Failed) { progress.Skip(); }
            }
            progress.ItemDone();
        }
        return new TransferResult(files, dirs, progress.Skipped, progress.SkippedExisting);
    }

    // ---------- Loeschen ----------

    /// <summary>
    /// Loescht. Eine Datei oder ein Link wird entfernt (bei einem Link nie das Ziel), ein Ordner nur leer, oder mit <paramref name="recursive"/> samt Inhalt (Links darin werden als Links
    /// entfernt, nie verfolgt). Vorher wird gezaehlt: ueber die Grenze passiert nichts. Der Pfad "/" ist immer abgelehnt.
    /// </summary>
    public static async Task<TransferResult> DeleteAsync(ISftpFileSession s, IReadOnlyList<string> paths, bool recursive, FileLimits limits, TransferProgress progress, CancellationToken ct)
    {
        if (paths.Any(p => p == "/")) throw new FileRuleException("root_forbidden", "Das Wurzelverzeichnis wird nie gelöscht.");
        var count = recursive ? await CountAsync(s, paths, limits, ct) : new TreeCount(paths.Count, 0, 0, 0);
        progress.SetTotals(count.Items, 0);
        int files = 0, dirs = 0;
        async Task Delete(RemoteEntry e, int depth)
        {
            ct.ThrowIfCancellationRequested();
            progress.Current = e.Name;
            if (e.Kind != RemoteKind.Directory) { await s.DeleteFileAsync(e.Path, e.Kind == RemoteKind.Link, ct); files++; progress.ItemDone(); return; }
            if (recursive)
            {
                if (depth > limits.MaxDepth) throw new FileRuleException("too_deep", "Der Ordner ist zu tief verschachtelt.");
                foreach (var c in (await s.ListAsync(e.Path, limits.MaxItems, ct)).Entries) await Delete(c, depth + 1);
            }
            await s.DeleteDirectoryAsync(e.Path, ct);                                // nicht leer und nicht rekursiv: NotEmpty
            dirs++; progress.ItemDone();
        }
        foreach (var p in paths)
            await Delete(await s.StatAsync(p, follow: false, ct) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden."), 0);
        return new TransferResult(files, dirs, 0, 0);
    }

    // ---------- Vorschau ----------

    /// <summary>
    /// Die ersten <see cref="FileLimits.MaxPreview"/> Byte einer Datei als UTF-8-Text. Ist es keine Textdatei (ein NUL-Byte in den ersten 8 KiB oder kein gueltiges UTF-8), wird
    /// <see cref="FileRuleException"/> "not_text" geworfen; eine am Ende abgeschnittene Zeichenfolge zaehlt nicht als Fehler.
    /// </summary>
    public static async Task<TextPreview> ReadTextAsync(ISftpFileSession s, string path, FileLimits limits, CancellationToken ct)
    {
        var entry = await s.StatAsync(path, follow: true, ct) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
        if (entry.Kind != RemoteKind.File) throw new FileRuleException("not_text", "Das ist keine Datei, die sich als Text zeigen lässt.");
        await using var r = await s.OpenReadAsync(path, ct);
        var buffer = new byte[limits.MaxPreview + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await r.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        var truncated = total > limits.MaxPreview;
        var length = (int)Math.Min(total, limits.MaxPreview);
        if (buffer.AsSpan(0, Math.Min(length, 8192)).Contains((byte)0)) throw new FileRuleException("not_text", "Das ist keine Textdatei.");
        if (truncated) while (length > 0 && (buffer[length] & 0xC0) == 0x80) length--;               // nicht mitten in einem Zeichen schneiden: das erste weggelassene Byte darf keine Fortsetzung sein
        try { return new TextPreview(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, length), truncated, entry.Size); }
        catch (DecoderFallbackException) { throw new FileRuleException("not_text", "Das ist keine Textdatei."); }
    }
}
