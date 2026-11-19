using System.Text;
using JulOS.Integrations.Ssh;

namespace JulOS.Integrations.Files;

public enum RemoteKind { File, Directory, Link, Other }

/// <summary>
/// Ein Eintrag auf dem Ziel, wie ihn "lstat" zeigt (ein Symlink ist ein Link, nicht sein Ziel). Resolved: wohin ein Link zeigt (Datei, Ordner, Sonstiges), null bei einem kaputten
/// oder nicht gepruefen Link und bei allem, was kein Link ist. Mode: die Rechte als "rwxr-xr-x". Der Pfad des Link-Ziels ist nicht dabei (SSH.NET bietet kein readlink).
/// </summary>
public sealed record RemoteEntry(string Name, string Path, RemoteKind Kind, long Size, DateTimeOffset Modified, string Mode, RemoteKind? Resolved = null)
{
    public bool IsDirectory => Kind == RemoteKind.Directory;
    /// <summary>Ist es ein Ordner oder ein Link auf einen Ordner (also etwas, das sich oeffnen laesst)?</summary>
    public bool IsOpenable => Kind == RemoteKind.Directory || (Kind == RemoteKind.Link && Resolved == RemoteKind.Directory);
}

/// <summary>Ein Ordnerinhalt: hoechstens das Limit, Truncated sagt, dass es mehr gab; Hidden zaehlt Eintraege, die nie angezeigt werden (Namen mit Steuerzeichen).</summary>
public sealed record RemoteListing(IReadOnlyList<RemoteEntry> Entries, bool Truncated, int Hidden);

public enum RemoteFileError { NotFound, Denied, Exists, NotEmpty, NotADirectory, Closed, Failed }

/// <summary>Fehler einer Dateioperation auf dem Ziel. Die Meldung ist nie ein Inhalt und nie ein Passwort.</summary>
public sealed class RemoteFileException(RemoteFileError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public RemoteFileError Error { get; } = error;
}

/// <summary>
/// Eine Regel oder Grenze hat die Operation abgelehnt (kein Fehler des Ziels): zu viele Eintraege, zu tief, zu gross, in sich selbst kopieren, Datei und Ordner verwechselt.
/// Code: too_many_items, too_deep, too_large, into_itself, same_place, type_mismatch, target_is_link, root_forbidden, invalid_name.
/// </summary>
public sealed class FileRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Dateioperationen auf einem Ziel per SFTP. Eine eigene Schnittstelle wie <see cref="ISshSession"/>: die Bibliothek dahinter bleibt austauschbar und die Logik darueber
/// (<see cref="FileTransfer"/>) laesst sich ohne Netz testen. Pfade sind absolut und schon geprueft (<see cref="RemotePath"/>). Wirkung hat nur, was der SSH-Benutzer darf.
/// Fehler der Verbindung sind <see cref="SshException"/>, Fehler einer Operation <see cref="RemoteFileException"/>.
/// </summary>
public interface ISftpFileSession : IAsyncDisposable
{
    /// <summary>Verbindet, prueft den Host-Schluessel (false von <paramref name="trustHostKey"/> bricht ab) und meldet sich mit dem Passwort an.</summary>
    Task ConnectAsync(SftpConnectOptions options, Func<SshHostKey, bool> trustHostKey, CancellationToken ct);

    /// <summary>Der Schluessel, den das Ziel gezeigt hat (auch wenn er abgelehnt wurde).</summary>
    SshHostKey? PresentedHostKey { get; }

    bool IsConnected { get; }

    /// <summary>Das Home-Verzeichnis des Benutzers (absolut), nach dem Verbinden.</summary>
    string HomeDirectory { get; }

    /// <summary>Hoechstens <paramref name="limit"/> Eintraege (ohne "." und ".."), unsortiert, ohne Eintraege mit Steuerzeichen im Namen.</summary>
    Task<RemoteListing> ListAsync(string directory, int limit, CancellationToken ct);

    /// <summary>Wie "lstat" (<paramref name="follow"/> false) oder "stat" (true); null, wenn es nichts gibt.</summary>
    Task<RemoteEntry?> StatAsync(string path, bool follow, CancellationToken ct);

    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    /// <summary>Legt eine NEUE Datei an und oeffnet sie zum Schreiben; gibt es den Pfad schon, ist es <see cref="RemoteFileError.Exists"/>.</summary>
    Task<Stream> CreateAsync(string path, CancellationToken ct);

    /// <summary>
    /// Benennt um oder verschiebt innerhalb des Ziels. Ohne <paramref name="overwrite"/> ist ein vorhandenes Ziel <see cref="RemoteFileError.Exists"/>; mit wird eine Datei ersetzt.
    /// <paramref name="fromIsLink"/>: die Quelle ist ein Link, es wird der Link selbst umbenannt (die Bibliothek folgt sonst dem Link und benennt sein Ziel um). Ein Link als Ziel
    /// ersetzt der Aufrufer nie (<see cref="FileTransfer"/> lehnt es ab).
    /// </summary>
    Task RenameAsync(string from, string to, bool overwrite, bool fromIsLink, CancellationToken ct);

    /// <summary>Loescht eine Datei oder einen Link. <paramref name="isLink"/>: es ist ein Link, es wird der Link selbst entfernt, nie sein Ziel (die Bibliothek folgt sonst dem Link).</summary>
    Task DeleteFileAsync(string path, bool isLink, CancellationToken ct);

    /// <summary>Loescht einen leeren Ordner; sonst <see cref="RemoteFileError.NotEmpty"/>.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken ct);

    /// <summary>Legt einen Ordner an; gibt es den Pfad schon, ist es <see cref="RemoteFileError.Exists"/>.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken ct);
}

/// <summary>Pfadregeln fuer das Ziel (immer "/"): nur absolute Pfade, kein "..", keine Steuerzeichen. Die Rechte des SSH-Benutzers bleiben die eigentliche Grenze; das hier haelt Eingaben sauber.</summary>
public static class RemotePath
{
    public const int MaxLength = 4096, MaxSegments = 128, MaxName = 255;

    /// <summary>
    /// Bringt einen Pfad in die Normalform ("/", "/a/b"): "." und leere Teile fallen weg, "~" und "~/x" beginnen im Home-Ordner, ein leerer Pfad ist das Home.
    /// null = abgelehnt (relativ, "..", Steuerzeichen, zu lang). Gibt es kein Home (<paramref name="home"/> null), sind "~" und leer abgelehnt.
    /// </summary>
    public static string? Normalize(string? input, string? home)
    {
        input ??= "";
        if (input.Length > MaxLength || input.Any(char.IsControl)) return null;
        if (input.Length == 0 || input == "~") return home;
        if (input.StartsWith("~/", StringComparison.Ordinal)) { if (home is null) return null; input = home.TrimEnd('/') + "/" + input[2..]; }
        if (input[0] != '/') return null;
        var parts = new List<string>();
        foreach (var s in input.Split('/'))
        {
            if (s.Length == 0 || s == ".") continue;
            if (s == "..") return null;
            parts.Add(s);
        }
        if (parts.Count > MaxSegments) return null;
        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    /// <summary>Ein neuer Name (anlegen, umbenennen, hochladen): nicht leer, hoechstens 255 Byte, kein "/", kein "." oder "..", keine Steuerzeichen.</summary>
    public static bool ValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name != "." && name != ".." && !name.Contains('/') && !name.Any(char.IsControl) && Encoding.UTF8.GetByteCount(name) <= MaxName && name.Trim().Length > 0;

    public static string Join(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    /// <summary>Der Ordner darueber ("/" bleibt "/").</summary>
    public static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    public static string NameOf(string path) => path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..];

    /// <summary>Liegt <paramref name="path"/> im Ordner <paramref name="root"/> oder ist er es selbst?</summary>
    public static bool IsInside(string root, string path) => path == root || (root == "/" ? path.StartsWith('/') : path.StartsWith(root + "/", StringComparison.Ordinal));

    /// <summary>
    /// Die Reihenfolge in der Liste: Zahlen im Namen zaehlen nach ihrem Wert ("b2" vor "b10"), Gross und Klein sind egal. Eigene Vergleichsfunktion, weil die Kultur-Sortierung der
    /// Laufzeit (invariante Globalisierung im Image) Zahlen nicht numerisch ordnet.
    /// </summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0'); var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                var c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : na.CompareTo(nb, StringComparison.Ordinal);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        var rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }

    /// <summary>Fuer den Verlauf: hoechstens <paramref name="max"/> Zeichen, bei Kuerzung bleibt das Ende (der Name) stehen.</summary>
    public static string Shorten(string? path, int max = 300) => string.IsNullOrEmpty(path) ? "" : path.Length <= max ? path : "…" + path[^(max - 1)..];
}
