using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JulOS.Integrations.Ssh;

/// <summary>
/// Was eine Terminal-Verbindung startet (docs/CLI-SESSIONS.md): "shell" (wie bisher), "claude", "codex" oder "custom" (eigener Befehl des Besitzers),
/// dazu ein Arbeitsverzeichnis und "am Leben halten" (tmux). Das Ergebnis der Pruefung, nie roher Text aus einer Anfrage.
/// </summary>
public sealed record SshLaunch(string Program, string WorkDir, bool KeepAlive, string Custom)
{
    public static readonly SshLaunch Shell = new("shell", "", false, "");

    /// <summary>Eine gewoehnliche Shell ohne alles: so verhaelt sich die Verbindung wie vor dieser Funktion (kein Kanal, keine Pruefung).</summary>
    public bool IsPlain => Program == "shell" && WorkDir.Length == 0 && !KeepAlive;
}

/// <summary>Das Ergebnis der Pruefung auf dem Ziel (siehe <see cref="SshLaunchRules.ProbeCommand"/>).</summary>
public sealed record SshProbe(bool Program, bool Dir, bool Tmux);

/// <summary>
/// Der Startplan nach der Pruefung. Line = die Zeile, die in die Shell geschrieben wird (null: nichts, es bleibt die gewoehnliche Shell);
/// Program = was wirklich laeuft ("shell", wenn auf den Rueckfall zurueckgegriffen wurde); Tmux = laeuft in tmux;
/// Notices = Hinweise fuer den Browser, nie still (program-missing, dir-missing, tmux-missing, probe-failed).
/// </summary>
public sealed record SshStartPlan(string? Line, string Program, bool Tmux, IReadOnlyList<string> Notices)
{
    public string NoticeText => string.Join(' ', Notices);
}

/// <summary>
/// Pruefung der Eingaben und Bau der Kommandozeilen. Alles, was in eine Zeile fuer die Shell des Ziels kommt, stammt aus festen Vorlagen und geprueften
/// Feldern und wird mit Einfach-Anfuehrungszeichen gequotet; Ausnahme ist der eigene Befehl, freier Text des Besitzers der Verbindung (eine Zeile, keine
/// Steuerzeichen), der nur aus der gespeicherten Verbindung kommt, nie aus einer Anfrage, und nirgends geloggt wird.
/// </summary>
public static partial class SshLaunchRules
{
    public static readonly string[] Programs = ["shell", "claude", "codex", "custom"];
    public const int MaxWorkDir = 200, MaxCustom = 500;

    // Arbeitsverzeichnis: absolut (/...), im Home-Ordner (~ oder ~/...) oder relativ zum Home (ohne fuehrenden Strich). Nur Buchstaben, Ziffern und . _ - / + @ % : , =
    // (keine Leerzeichen, keine Anfuehrungszeichen, kein $, Backtick, Semikolon, Klammern, Zeilenumbrueche).
    [GeneratedRegex(@"^(~|~/[A-Za-z0-9._+@%:,=/-]*|/[A-Za-z0-9._+@%:,=/-]*|[A-Za-z0-9._+@%:,=][A-Za-z0-9._+@%:,=/-]*)\z")]
    private static partial Regex WorkDirPattern();

    public static bool IsTemplate(string program) => program is "claude" or "codex";

    /// <summary>Der Programmname einer Vorlage (so heisst er auf dem Ziel).</summary>
    public static string ProgramName(string program) => program switch { "claude" => "claude", "codex" => "codex", _ => throw new ArgumentException("Keine Vorlage.", nameof(program)) };

    /// <summary>Prueft die Felder einer Verbindung (null = nicht angegeben: der bisherige Wert bleibt, beim Anlegen der Standard).</summary>
    public static (SshLaunch? Value, string? Error) Validate(string? program, string? workDir, bool? keepAlive, string? custom, SshLaunch? current = null)
    {
        current ??= SshLaunch.Shell;
        var p = (program ?? current.Program).Trim().ToLowerInvariant();
        if (!Programs.Contains(p)) return (null, "Das Startprogramm muss Shell, Claude Code, Codex oder ein eigener Befehl sein.");

        var dir = (workDir ?? current.WorkDir).Trim();
        if (dir.Length > MaxWorkDir) return (null, $"Das Arbeitsverzeichnis ist zu lang (höchstens {MaxWorkDir} Zeichen).");
        if (dir.Length > 0 && !WorkDirPattern().IsMatch(dir)) return (null, "Das Arbeitsverzeichnis darf nur Buchstaben, Ziffern und . _ - / + @ % : , = enthalten (ein ~ nur am Anfang, keine Leerzeichen).");

        var command = (custom ?? current.Custom).Trim();
        if (command.Length > MaxCustom) return (null, $"Der Befehl ist zu lang (höchstens {MaxCustom} Zeichen).");
        if (command.Any(char.IsControl)) return (null, "Der Befehl muss eine Zeile ohne Steuerzeichen sein.");
        if (p == "custom" && command.Length == 0) return (null, "Für „Eigener Befehl“ fehlt der Befehl.");
        if (p != "custom") command = "";                           // ohne eigenen Befehl bleibt nichts davon stehen

        return (new SshLaunch(p, dir, keepAlive ?? current.KeepAlive, command), null);
    }

    /// <summary>Einfach-Anfuehrungszeichen fuer eine POSIX-Shell: ' wird zu '\'' (gilt auch in fish und zsh).</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Das Verzeichnis als Wort fuer die Shell: ~ wird zu "$HOME" (eine Tilde in Anfuehrungszeichen wird nicht aufgeloest), alles andere gequotet.</summary>
    public static string DirWord(string dir)
    {
        if (!WorkDirPattern().IsMatch(dir)) throw new ArgumentException("Ungültiges Arbeitsverzeichnis.", nameof(dir));
        if (dir == "~") return "\"$HOME\"";
        if (dir.StartsWith("~/", StringComparison.Ordinal)) return "\"$HOME\"" + Quote(dir[1..]);
        return Quote(dir);
    }

    /// <summary>
    /// Ein Name fuer die tmux-Sitzung, nur [a-z0-9-]: aus Programm und Name der Verbindung, dazu das Anfangsstueck der Kennung (zwei Verbindungen
    /// mit gleichem Namen teilen sich nie eine Sitzung). Gleiche Eingabe, gleicher Name: so findet "new-session -A" die Sitzung wieder.
    /// </summary>
    public static string SessionName(Guid connectionId, string connectionName, string program)
    {
        var slug = new StringBuilder();
        foreach (var c in connectionName.ToLowerInvariant().Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;      // Akzente fallen weg: "müller" wird "muller"
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var name = slug.ToString().Trim('-');
        if (name.Length > 24) name = name[..24].TrimEnd('-');
        var id = connectionId.ToString("N")[..6];
        var prog = Programs.Contains(program) ? program : "shell";
        return name.Length == 0 ? $"julos-{prog}-{id}" : $"julos-{prog}-{name}-{id}";
    }

    /// <summary>
    /// Der Befehl fuer den kurzen Exec-Kanal (Frist 5 s): gibt eine Zeile "julos-probe prog=1 dir=1 tmux=1" aus. Die Programme werden erst so, dann in einer
    /// Login-Shell gesucht (dort steht oft ein erweiterter PATH, zum Beispiel ~/.local/bin). Nur Felder, die gebraucht werden, werden geprueft (der Rest steht auf 1).
    /// </summary>
    public static string ProbeCommand(SshLaunch l)
    {
        var script = new StringBuilder("p=1; d=1; t=1; ");
        static string Find(string name) => $"{{ command -v {name} >/dev/null 2>&1 || \"${{SHELL:-sh}}\" -lc 'command -v {name}' >/dev/null 2>&1; }}";
        if (IsTemplate(l.Program)) script.Append(Find(ProgramName(l.Program))).Append(" || p=0; ");
        if (l.WorkDir.Length > 0) script.Append($"cd {DirWord(l.WorkDir)} >/dev/null 2>&1 || d=0; ");
        if (l.KeepAlive) script.Append(Find("tmux")).Append(" || t=0; ");
        script.Append("echo \"julos-probe prog=$p dir=$d tmux=$t\"");
        return "sh -c " + Quote(script.ToString());
    }

    [GeneratedRegex(@"julos-probe prog=([01]) dir=([01]) tmux=([01])")]
    private static partial Regex ProbeLine();

    /// <summary>Liest die Ausgabe der Pruefung; null, wenn keine passende Zeile da ist (Pruefung fehlgeschlagen oder abgelaufen).</summary>
    public static SshProbe? ParseProbe(string? output)
    {
        if (output is null) return null;
        var m = ProbeLine().Match(output);
        return m.Success ? new SshProbe(m.Groups[1].Value == "1", m.Groups[2].Value == "1", m.Groups[3].Value == "1") : null;
    }

    /// <summary>
    /// Plan nach der Pruefung. Fehlt etwas, startet eine gewoehnliche Shell und der Browser bekommt einen Hinweis; nichts davon geschieht still.
    /// Fehlt das Verzeichnis, startet auch das Programm nicht (ein Programm im falschen Ordner waere gefaehrlich). Fehlt tmux: Sitzung ohne tmux, mit Hinweis.
    /// </summary>
    public static SshStartPlan Plan(SshLaunch l, string connectionName, Guid connectionId, SshProbe? probe)
    {
        if (l.IsPlain) return new SshStartPlan(null, "shell", false, []);
        if (probe is null) return new SshStartPlan(null, "shell", false, ["probe-failed"]);
        if (!probe.Dir) return new SshStartPlan(null, "shell", false, ["dir-missing"]);

        var notices = new List<string>();
        var program = l.Program;
        if (IsTemplate(program) && !probe.Program) { notices.Add("program-missing"); program = "shell"; }
        var tmux = l.KeepAlive;
        if (tmux && !probe.Tmux) { notices.Add("tmux-missing"); tmux = false; }
        return new SshStartPlan(Line(l with { Program = program }, tmux, connectionName, connectionId), program, tmux, notices);
    }

    /// <summary>Die Zeile fuer die Shell des Ziels (beginnt mit einem Leerzeichen: viele Shells nehmen solche Zeilen nicht in den Verlauf; endet mit Zeilenvorschub). null: nichts zu tun.</summary>
    public static string? Line(SshLaunch l, bool tmux, string connectionName, Guid connectionId)
    {
        string? inner = l.Program switch { "claude" or "codex" => ProgramName(l.Program), "custom" => l.Custom, _ => null };
        if (inner is not null && inner.Any(char.IsControl)) throw new ArgumentException("Der Befehl enthält Steuerzeichen.", nameof(l));
        var cd = l.WorkDir.Length > 0 ? "cd " + DirWord(l.WorkDir) : null;

        string? start;
        if (tmux)
        {
            // new-session -A: gibt es die Sitzung schon, haengt sich der Client wieder an (der Befehl gilt dann nicht noch einmal). Maus an: Wischen scrollt den Verlauf von tmux.
            // Die Statuszeile bleibt aus (am Handy ist jede Zeile kostbar). "\;" trennt die tmux-Befehle (die Shell reicht es als ; weiter).
            var name = SessionName(connectionId, connectionName, l.Program);
            start = $"exec tmux new-session -A -s {name}" + (inner is null ? "" : " " + Quote(inner)) + " \\; set-option mouse on \\; set-option status off";
        }
        else if (inner is null) start = null;
        else if (l.Program == "custom") start = "exec sh -c " + Quote(inner);
        else start = "exec " + inner;

        var line = cd is null ? start : start is null ? cd : cd + " && " + start;
        return line is null ? null : " " + line + "\n";
    }
}
