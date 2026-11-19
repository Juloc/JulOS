using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JulOS.Backend.Logic;

public sealed record FileItem(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset Modified, int? Items);
public sealed record FileListing(string Path, FileItem[] Entries, bool Truncated);
public sealed record FileSearchResult(FileItem[] Results, bool Truncated);

/// <summary>
/// Lesender Zugriff auf genau EINEN Ordner (die Wurzel) und alles darunter. Zusagen:
/// <list type="bullet">
/// <item>Pfade sind immer relativ zur Wurzel ("a/b"); ".." wird abgelehnt, ein fuehrender "/" heisst Wurzel, nicht Dateisystem.</item>
/// <item>Jeder Pfad wird Schritt fuer Schritt aufgeloest, Symlinks eingeschlossen (auch Ketten und relative Ziele). Landet das Ergebnis
/// ausserhalb der echten Wurzel, wird der Eintrag weder aufgelistet noch geoeffnet. Ein Symlink auf ein Ziel innerhalb der Wurzel ist erlaubt.</item>
/// <item>Namen mit Punkt am Anfang (.agent, .env, ...) sind unsichtbar und nicht erreichbar.</item>
/// <item>Beim Oeffnen wird nach dem Oeffnen noch einmal geprueft, wohin der Dateideskriptor wirklich zeigt (schliesst das Zeitfenster zwischen Pruefung und Oeffnen).</item>
/// </list>
/// Nur fuer Linux gedacht (Pfadtrenner "/"). Nicht erkannt werden Hardlinks (eine zweite Verzeichnisstelle derselben Datei).
/// </summary>
public sealed partial class FileBrowser
{
    public const int MaxEntries = 2000, MaxResults = 200, MaxVisited = 50_000, MaxDepth = 16, MaxSegments = 64, MaxPathLength = 4096, MaxLinkHops = 40;

    private readonly string? _root;

    /// <param name="root">Ordner; leer oder "off" schaltet die Dateien ab.</param>
    public FileBrowser(string? root)
    {
        root = root?.Trim();
        if (string.IsNullOrEmpty(root) || root.Equals("off", StringComparison.OrdinalIgnoreCase)) return;
        _root = Path.GetFullPath(root);
    }

    public bool Enabled => _root is not null;

    /// <summary>Zerlegt einen Benutzerpfad. null = abgelehnt (".." , NUL, versteckter Name, zu lang).</summary>
    public static string[]? Segments(string? relative)
    {
        relative ??= "";
        if (relative.Length > MaxPathLength || relative.Contains('\0')) return null;
        var list = new List<string>();
        foreach (var s in relative.Split('/'))
        {
            if (s.Length == 0 || s == ".") continue;
            if (s[0] == '.') return null;   // ".." und alle versteckten Namen
            list.Add(s);
        }
        return list.Count > MaxSegments ? null : [.. list];
    }

    private static string Join(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;
    private static string JoinRel(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    /// <summary>Der echte Pfad ohne Symlinks, oder null bei zu vielen Spruengen. Der Pfad muss nicht existieren.</summary>
    public static string? RealPath(string absolute)
    {
        var pending = new Stack<string>();
        foreach (var s in absolute.Split('/').Reverse()) pending.Push(s);
        var current = "/";
        var hops = 0;
        while (pending.Count > 0)
        {
            var seg = pending.Pop();
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { current = Path.GetDirectoryName(current) ?? "/"; continue; }
            var next = Join(current, seg);
            string? link;
            try { link = new FileInfo(next).LinkTarget; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            if (link is null) { current = next; continue; }
            if (++hops > MaxLinkHops) return null;
            if (link.StartsWith('/')) current = "/";
            foreach (var s in link.Split('/').Reverse()) pending.Push(s);
        }
        return current;
    }

    private static bool Inside(string real, string root) =>
        real == root || (root == "/" ? real.StartsWith('/') : real.StartsWith(root + "/", StringComparison.Ordinal));

    /// <summary>Echte Wurzel, falls es sie gibt.</summary>
    private string? RealRoot()
    {
        if (_root is null) return null;
        var real = RealPath(_root);
        return real is not null && Directory.Exists(real) ? real : null;
    }

    /// <summary>Loest einen Benutzerpfad auf. null, wenn abgelehnt, ausserhalb der Wurzel oder es nichts gibt.</summary>
    public string? Resolve(string? relative)
    {
        var root = RealRoot();
        var parts = Segments(relative);
        if (root is null || parts is null) return null;
        var real = RealPath(parts.Aggregate(root, Join));
        if (real is null || !Inside(real, root) || !(File.Exists(real) || Directory.Exists(real))) return null;
        return real;
    }

    private static string Normalize(string[] parts) => string.Join('/', parts);

    private static string? Safe(FileSystemInfo entry, string root)
    {
        var real = entry.LinkTarget is null ? entry.FullName : RealPath(entry.FullName);
        return real is not null && Inside(real, root) ? real : null;
    }

    private static int CountItems(string real)
    {
        var n = 0;
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(real))
            {
                if (Path.GetFileName(e).StartsWith('.')) continue;
                if (++n >= 999) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        return n;
    }

    private static FileItem? Describe(string name, string relPath, string real)
    {
        try
        {
            if (Directory.Exists(real)) { var d = new DirectoryInfo(real); return new FileItem(name, relPath, true, null, d.LastWriteTimeUtc, CountItems(real)); }
            if (File.Exists(real)) { var f = new FileInfo(real); return new FileItem(name, relPath, false, f.Length, f.LastWriteTimeUtc, null); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* unlesbar: nicht anzeigen */ }
        return null;
    }

    /// <summary>Inhalt eines Ordners: Ordner zuerst, dann nach Namen. null, wenn es ihn nicht gibt oder er nicht erlaubt ist.</summary>
    public FileListing? List(string? relative)
    {
        var root = RealRoot();
        var parts = Segments(relative);
        if (root is null || parts is null) return null;
        var dir = Resolve(relative);
        if (dir is null || !Directory.Exists(dir)) return null;
        var rel = Normalize(parts);
        var items = new List<FileItem>();
        var truncated = false;
        try
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entry.Name.StartsWith('.')) continue;
                if (Safe(entry, root) is not { } real) continue;     // Symlink nach draussen, Schleife oder ins Leere: unsichtbar
                if (Describe(entry.Name, JoinRel(rel, entry.Name), real) is not { } item) continue;
                if (items.Count >= MaxEntries) { truncated = true; break; }
                items.Add(item);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        items.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : string.Compare(a.Name, b.Name, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering));
        return new FileListing(rel, [.. items], truncated);
    }

    /// <summary>Sucht Namen (Teilstueck, Gross/Klein egal) in einem Ordner und allen Unterordnern. Symlinks werden dabei nicht verfolgt.</summary>
    public FileSearchResult? Search(string? relative, string? query, CancellationToken ct = default)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > 100) return null;
        var parts = Segments(relative);
        var start = Resolve(relative);
        if (parts is null || start is null || !Directory.Exists(start)) return null;
        var results = new List<FileItem>();
        var truncated = false;
        var visited = 0;
        var queue = new Queue<(string Real, string Rel, int Depth)>();
        queue.Enqueue((start, Normalize(parts), 0));
        while (queue.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel, depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (entry.Name.StartsWith('.') || entry.LinkTarget is not null) continue;
                if (++visited > MaxVisited || results.Count >= MaxResults) { truncated = true; break; }
                var relPath = JoinRel(rel, entry.Name);
                if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && Describe(entry.Name, relPath, entry.FullName) is { } item) results.Add(item);
                if (entry is DirectoryInfo && depth < MaxDepth) queue.Enqueue((entry.FullName, relPath, depth + 1));
            }
        }
        results.Sort((a, b) => string.Compare(a.Path, b.Path, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering));
        return new FileSearchResult([.. results], truncated);
    }

    /// <summary>Oeffnet eine Datei zum Lesen. null, wenn es keine erlaubte Datei ist. Der Aufrufer schliesst den Strom.</summary>
    public FileStream? OpenRead(string? relative)
    {
        var root = RealRoot();
        var real = Resolve(relative);
        if (root is null || real is null || !File.Exists(real)) return null;
        FileStream? stream = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Mit O_NONBLOCK oeffnen: ein benannter Pipe (FIFO) im Ordner liesse open(2) sonst fuer immer warten und einen Thread blockieren.
                var fd = OpenNonBlocking(real, ReadOnly | NonBlocking | CloseOnExec);
                if (fd < 0) return null;
                var handle = new SafeFileHandle(fd, ownsHandle: true);
                try { stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false); }
                catch { handle.Dispose(); throw; }
                // Nur gewoehnliche Dateien: Pipes lassen sich nicht positionieren
                if (!stream.CanSeek) { stream.Dispose(); return null; }
                // Wohin zeigt der geoeffnete Deskriptor wirklich? Faengt ab, dass ein Ordner zwischen Pruefung und Oeffnen durch einen Symlink ersetzt wurde.
                var actual = new FileInfo("/proc/self/fd/" + stream.SafeFileHandle.DangerousGetHandle()).LinkTarget;
                if (actual is null || !Inside(actual, root)) { stream.Dispose(); return null; }
                return stream;
            }

            stream = new FileStream(real, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite, Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 1 });
            return stream;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stream?.Dispose();
            return null;
        }
    }

    private const int ReadOnly = 0, NonBlocking = 0x800, CloseOnExec = 0x80000;     // Linux (x86-64 und arm64)

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenNonBlocking(string path, int flags);
}
