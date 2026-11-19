using System.Globalization;
using System.Text;

namespace JulOS.Integrations.Display.Rdp.Drive;

/// <summary>
/// Ein Ordner des Servers, der als Laufwerk in die Remote-Sitzung gereicht wird (RDPDR). Die Wurzel steht in der Server-Einstellung
/// (<c>JULOS_DRIVE_ROOT</c>), nie im Browser; je Verbindung entscheidet nur, ob sie freigegeben wird und ob geschrieben werden darf.
/// </summary>
/// <param name="Root">Der Ordner (wird vom <see cref="DriveJail"/> geprueft und aufgeloest).</param>
/// <param name="Writable">Schreiben, Anlegen, Umbenennen und Loeschen erlaubt. Standard: nur Lesen.</param>
/// <param name="Name">Name des Laufwerks in der Sitzung, 1 bis 7 Zeichen aus A-Z, a-z, 0-9, _.</param>
public sealed record SharedDrive(string Root, bool Writable = false, string Name = "JULOS")
{
    public const int MaxNameLength = 7;

    public static bool IsValidName(string? name) =>
        name is { Length: >= 1 and <= MaxNameLength } && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>
/// Der Zaun um das Laufwerk: aus dem, was der ferne Rechner als Pfad schickt, wird ein Pfad unterhalb der Wurzel, oder gar keiner. Zusagen:
/// <list type="bullet">
/// <item>Ein Pfad ist immer relativ zur Wurzel und im Windows-Format (<c>\ordner\datei</c>; ein <c>/</c> trennt ebenso, so schickt es xrdp: <c>/etc</c> meint
/// <c>\etc</c> unterhalb der Wurzel, nie ein Verzeichnis des Servers). Ein Pfad, der nicht mit einem Trenner anfaengt (<c>C:\...</c>, <c>datei</c>), wird abgelehnt.</item>
/// <item>Ein Name wird nie "bereinigt", nur angenommen oder abgelehnt: kein <c>.</c> oder <c>..</c>, nichts mit Punkt am Anfang (versteckt: <c>.env</c>,
/// <c>.ssh</c>), kein Punkt oder Leerzeichen am Ende, keines der Zeichen <c>&lt; &gt; : " | ? * / \</c>, keine Steuerzeichen, keine Format- oder
/// Richtungszeichen (Unicode), keine einzelnen Surrogate, keine Zeichen, die erst nach Unicode-Normalisierung (NFKC) zu einem verbotenen werden
/// (ganzbreiter Punkt oder Schraegstrich), nicht laenger als 255 Bytes (UTF-8).</item>
/// <item>Kein Symlink: Ist ein vorhandener Teil des Pfades ein Symlink (auch auf ein Ziel innerhalb der Wurzel), wird der Pfad abgelehnt.
/// Die Wurzel selbst wird beim Anlegen einmal aufgeloest.</item>
/// <item>Beim Oeffnen prueft <see cref="JailFs"/> noch einmal am geoeffneten Deskriptor (<c>openat2</c> mit <c>RESOLVE_BENEATH</c>, danach
/// <c>/proc/self/fd</c>), damit zwischen Pruefen und Oeffnen nichts ausgetauscht werden kann.</item>
/// </list>
/// Nur fuer Linux. Nicht erkannt werden Hardlinks (eine zweite Verzeichnisstelle derselben Datei), die jemand mit Schreibzugriff auf die
/// Ordner schon vorher angelegt haben muss.
/// </summary>
public sealed class DriveJail
{
    public const int MaxSegments = 32, MaxPathChars = 1024, MaxNameBytes = 255, MaxLinkHops = 40;

    private DriveJail(string root) => Root = root;

    /// <summary>Der echte Pfad der Wurzel (ohne Symlinks).</summary>
    public string Root { get; }

    /// <summary>
    /// Prueft die Wurzel: ein absoluter Pfad zu einem vorhandenen Ordner, nicht das Wurzelverzeichnis des Servers. Gibt null und den Grund zurueck,
    /// wenn nicht (leer oder "off" ist keine Fehlermeldung wert: das Laufwerk ist dann aus, <paramref name="error"/> bleibt leer).
    /// </summary>
    public static DriveJail? TryCreate(string? root, out string error)
    {
        error = "";
        root = root?.Trim();
        if (string.IsNullOrEmpty(root) || root.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (root.Contains('\0') || !root.StartsWith('/'))
        {
            error = "Die Wurzel des Laufwerks muss ein absoluter Pfad sein.";
            return null;
        }

        var real = RealPath(root);
        if (real is null || !Directory.Exists(real))
        {
            error = "Die Wurzel des Laufwerks gibt es nicht (oder sie ist kein Ordner).";
            return null;
        }

        if (real == "/")
        {
            error = "Das Wurzelverzeichnis des Servers darf nicht freigegeben werden.";
            return null;
        }

        return new DriveJail(real);
    }

    /// <summary>Der echte Pfad ohne Symlinks, oder null bei zu vielen Spruengen oder unlesbaren Teilen. Der Pfad muss nicht existieren.</summary>
    public static string? RealPath(string absolute)
    {
        var pending = new Stack<string>();
        foreach (var part in absolute.Split('/').Reverse())
        {
            pending.Push(part);
        }

        var current = "/";
        var hops = 0;
        while (pending.Count > 0)
        {
            var part = pending.Pop();
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }

            var next = current == "/" ? "/" + part : current + "/" + part;
            string? link;
            try { link = new FileInfo(next).LinkTarget; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            if (link is null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                return null;
            }

            if (link.StartsWith('/'))
            {
                current = "/";
            }

            foreach (var linkPart in link.Split('/').Reverse())
            {
                pending.Push(linkPart);
            }
        }

        return current;
    }

    /// <summary>Liegt ein echter Pfad in oder unter der Wurzel?</summary>
    public bool Inside(string real) =>
        real == Root || real.StartsWith(Root + "/", StringComparison.Ordinal);

    // Unicode, das in einem Dateinamen nichts zu suchen hat, auch wenn Linux es annimmt: ein Zeichen, das wie Punkt, Schraegstrich, Doppelpunkt
    // oder Rueckstrich aussieht, aber keiner ist (und nach NFKC einer wird; ein Name, der dann etwas anderes meint, ist ein Trick)
    private static readonly HashSet<char> Lookalikes =
    [
        '\u2024', '\u2044', '\u2215', '\u2216', '\u29F5', '\u29F8', '\u29F9', '\uFE52', '\uFE55', '\uFE68', '\uFF0E', '\uFF0F', '\uFF1A', '\uFF3C', '\uFF5C', '\u01C0',
    ];

    /// <summary>Zerlegt einen Pfad des fernen Rechners (<c>\a\b</c>) in gepruefte Namen. Leere Liste = die Wurzel. null = abgelehnt.</summary>
    public static string[]? Segments(string? path)
    {
        path ??= "";
        if (path.Length > MaxPathChars)
        {
            return null;
        }

        if (path.Length == 0)
        {
            return [];
        }

        // Ein Schraegstrich trennt wie ein Rueckstrich: xrdp (chansrv) schickt Pfade so ("/datei.txt"), Windows-Clients mit Rueckstrich. Danach gelten fuer
        // jeden Namen dieselben strengen Regeln, ein Schraegstrich steckt in keinem Namen.
        path = path.Replace('/', '\\');
        if (path[0] != '\\')
        {
            return null;                // absolut ("C:\...") oder relativ ohne Trenner am Anfang
        }

        var parts = path[1..].Split('\\');
        var names = new List<string>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
            {
                if (i == parts.Length - 1)
                {
                    continue;           // ein Rueckstrich am Ende ("\ordner\") ist ueblich
                }

                return null;            // "\a\\b": leere Teile gibt es nicht
            }

            if (!IsAllowedName(parts[i]))
            {
                return null;
            }

            names.Add(parts[i]);
        }

        return names.Count > MaxSegments ? null : [.. names];
    }

    /// <summary>Ein einzelner Name (ein Teil des Pfades): angenommen oder abgelehnt, nie verbessert.</summary>
    public static bool IsAllowedName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameBytes || !Check(name))
        {
            return false;
        }

        string folded;
        try { folded = name.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return false; }         // ein einzelnes Surrogat

        return (folded == name || Check(folded)) && Encoding.UTF8.GetByteCount(name) <= MaxNameBytes;
    }

    private static bool Check(string name)
    {
        if (name.Length == 0 || name[0] == '.' || name[^1] is '.' or ' ')
        {
            return false;               // versteckt, "." und "..", und was Windows am Ende abschneiden wuerde
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1]))
                {
                    return false;
                }

                if (Forbidden(CharUnicodeInfo.GetUnicodeCategory(name, i)))
                {
                    return false;
                }

                i++;
                continue;
            }

            // U+FFFD is what a broken text turns into on its way (a lone surrogate, bad UTF-8): such a name is not what anybody typed
            if (char.IsLowSurrogate(c) || c < 0x20 || c == 0x7F || c == '\uFFFD' || "<>:\"|?*/\\".Contains(c) || Lookalikes.Contains(c))
            {
                return false;
            }

            if (Forbidden(CharUnicodeInfo.GetUnicodeCategory(c)))
            {
                return false;
            }
        }

        return true;
    }

    // Steuerzeichen, Formatzeichen (Richtungsumkehr U+202E, Nullbreite U+200B bis U+200D, U+FEFF), Surrogate, Privatnutzung, nicht zugewiesen, Zeilen- und Absatztrenner
    private static bool Forbidden(UnicodeCategory category) =>
        category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// Loest die Namen zu einem Pfad unterhalb der Wurzel auf. false bei einem Symlink in einem vorhandenen Teil des Pfades. Der Pfad muss nicht
    /// existieren (zum Anlegen); ob er existiert, fragt der Aufrufer.
    /// </summary>
    public bool TryResolve(string[] segments, out string full)
    {
        full = Root;
        var current = Root;
        var exists = true;
        foreach (var name in segments)
        {
            current = current + "/" + name;
            if (exists)
            {
                string? link;
                try { link = new FileInfo(current).LinkTarget; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
                if (link is not null)
                {
                    return false;                       // auch ein Symlink auf ein Ziel in der Wurzel: nicht verfolgt
                }

                exists = File.Exists(current) || Directory.Exists(current);
            }
        }

        full = current;
        return true;
    }

    /// <summary>Pfad des fernen Rechners -> Namen -> echter Pfad. null, wenn irgendeine Pruefung ablehnt.</summary>
    public string? Resolve(string? remotePath, out string[] segments)
    {
        segments = [];
        var names = Segments(remotePath);
        if (names is null || !TryResolve(names, out var full))
        {
            return null;
        }

        segments = names;
        return full;
    }
}
