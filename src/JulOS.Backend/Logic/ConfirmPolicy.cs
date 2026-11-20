namespace JulOS.Backend.Logic;

/// <summary>Wie schlimm eine Aktion ist: Routine (ein Klick auf "Ausfuehren"), Dangerous (Warnung), Critical (Warnung und eine zweite, rote Zeile).</summary>
public enum ConfirmLevel { Routine, Dangerous, Critical }

/// <summary>Die Einstellung "Bestaetigung bei gefaehrlichen Aktionen": Check (Haekchen, Standard), Type (Rechnernamen tippen), Click (nur Klick).</summary>
public enum ConfirmMode { Check, Type, Click }

/// <summary>Was der zweite Schritt (<c>run</c>) vom Benutzer zusaetzlich verlangt: nichts, das Haekchen oder den getippten Rechnernamen. Der Server prueft es, nicht der Browser.</summary>
public enum ConfirmDemand { None, Check, Type }

/// <summary>Das Ergebnis der Stufen-Berechnung. Warn: Schluessel des Warntextes ("was geschieht"), Critical: Schluessel der roten Zeile (oder null).</summary>
public sealed record ConfirmInfo(ConfirmLevel Level, ConfirmDemand Demand, string? Warn, string? Critical)
{
    public string LevelName => ConfirmPolicy.Name(Level);
    public string ModeName => ConfirmPolicy.Name(Demand);
    /// <summary>Kurz fuer den Verlauf: "dangerous:check". Nie ein Inhalt.</summary>
    public string Audit => $"{LevelName}:{ModeName}";
}

/// <summary>Was die Dateien-App fuer die Stufe weiss: Aktion, ob die Quelle oder das Ziel ein Root-Ort ist (der Agent arbeitet als root), die Quellpfade, ob Namen ersetzt wuerden, ob ein Ordner mit Inhalt geloescht wird.</summary>
public sealed record FileFacts(string Action, bool SourceRoot, bool TargetRoot, IReadOnlyList<string> Paths, bool Overwrites, bool NonEmpty);

/// <summary>Was die Pruefung eines Hosts ergeben hat: JulOS laeuft auf diesem Host (Container oder Dienst von JulOS), oder das Ziel der Aktion gehoert zu JulOS selbst.</summary>
public sealed record HostFacts(bool RunsJulOs = false, bool TargetIsJulOs = false);

/// <summary>
/// Die Stufe jeder Aktion und was der Server dafuer verlangt (docs/HOST-AGENT.md "Bestaetigung", docs/FILES.md). Eine reine Funktion fuer Host-Aktionen und Dateien zugleich: die Zuordnung steht
/// fest im Code, nicht im Browser und nicht in der Datenbank. Einstellbar ist nur der Modus (Einstellung der Instanz). "Sehr gefaehrlich" faellt bei "nur Klick" auf das Haekchen zurueck: nie ganz ohne Huerde.
/// </summary>
public static class ConfirmPolicy
{
    public static string Name(ConfirmLevel l) => l switch { ConfirmLevel.Dangerous => "dangerous", ConfirmLevel.Critical => "critical", _ => "routine" };
    public static string Name(ConfirmDemand d) => d switch { ConfirmDemand.Check => "check", ConfirmDemand.Type => "type", _ => "click" };
    public static string Name(ConfirmMode m) => m switch { ConfirmMode.Type => "type", ConfirmMode.Click => "click", _ => "check" };

    /// <summary>Der gespeicherte Text als Modus; alles Unbekannte ist der Standard (Haekchen).</summary>
    public static ConfirmMode ParseMode(string? s) => s switch { "type" => ConfirmMode.Type, "click" => ConfirmMode.Click, _ => ConfirmMode.Check };

    public static bool IsMode(string? s) => s is "check" or "type" or "click";

    /// <summary>Was der zweite Schritt verlangt. Routine: nichts. Gefaehrlich: je nach Modus. Sehr gefaehrlich: wie gefaehrlich, aber "nur Klick" gilt nicht (dann das Haekchen).</summary>
    public static ConfirmDemand DemandFor(ConfirmLevel level, ConfirmMode mode) => level switch
    {
        ConfirmLevel.Routine => ConfirmDemand.None,
        ConfirmLevel.Dangerous => mode switch { ConfirmMode.Type => ConfirmDemand.Type, ConfirmMode.Click => ConfirmDemand.None, _ => ConfirmDemand.Check },
        _ => mode == ConfirmMode.Type ? ConfirmDemand.Type : ConfirmDemand.Check,
    };

    private static ConfirmInfo Make(ConfirmMode mode, ConfirmLevel level, string? warn = null, string? critical = null) => new(level, DemandFor(level, mode), warn, critical);

    // ------------------------------------------------------------ Host-Aktionen

    /// <summary>Neustart und Ausschalten sind gefaehrlich. Ausschalten eines Hosts, auf dem JulOS laeuft, und Stoppen oder Neustarten von JulOS selbst sind sehr gefaehrlich. Alles andere ist Routine.</summary>
    public static ConfirmInfo ForHostAction(string action, ConfirmMode mode, HostFacts facts)
    {
        if (action is "docker.stop" or "docker.restart" or "service.stop" or "service.restart" && facts.TargetIsJulOs)
            return Make(mode, ConfirmLevel.Critical, action.EndsWith("stop", StringComparison.Ordinal) ? "stop_julos" : "restart_julos", "julos_self");
        return action switch
        {
            "power.poweroff" when facts.RunsJulOs => Make(mode, ConfirmLevel.Critical, "poweroff", "julos_host"),
            "power.poweroff" => Make(mode, ConfirmLevel.Dangerous, "poweroff"),
            "power.reboot" => Make(mode, ConfirmLevel.Dangerous, "reboot"),
            _ => Make(mode, ConfirmLevel.Routine),
        };
    }

    // ------------------------------------------------------------ Dateien

    /// <summary>Ein Pfad direkt unter der Wurzel ("/", "/etc", "/usr", "/var", ...) oder ein Ordner unter "/home": wer ihn als root loescht oder verschiebt, zerstoert das System oder ein ganzes Benutzerverzeichnis.</summary>
    public static bool IsSystemNear(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') return false;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 || (parts.Length == 2 && parts[0] == "home");
    }

    /// <summary>
    /// Mehrere Dateien oder ein Ordner mit Inhalt loeschen, jedes Loeschen als root und das Ersetzen vorhandener Namen auf einem Root-Ort sind gefaehrlich (das Ersetzen auf einem gewoehnlichen SSH-Zugang zeigt der Dialog nur an); auf einem Root-Ort ein Pfad nahe der Wurzel loeschen,
    /// umbenennen oder verschieben ist sehr gefaehrlich. Einzelne Dateien hochladen, umbenennen, anlegen, kopieren und verschieben sowie Lesen sind Routine.
    /// </summary>
    public static ConfirmInfo ForFiles(FileFacts f, ConfirmMode mode)
    {
        if (f.SourceRoot && f.Action is "delete" or "rename" or "move" && f.Paths.Any(IsSystemNear))
            return Make(mode, ConfirmLevel.Critical, f.Action == "delete" ? "delete_root" : "move_root", "system_path");
        if (f.Action == "delete")
        {
            if (f.SourceRoot) return Make(mode, ConfirmLevel.Dangerous, "delete_root");
            if (f.NonEmpty) return Make(mode, ConfirmLevel.Dangerous, "delete_recursive");
            if (f.Paths.Count > 1) return Make(mode, ConfirmLevel.Dangerous, "delete_many");
        }
        if (f.Overwrites && (f.SourceRoot || f.TargetRoot) && f.Action is "upload" or "rename" or "copy" or "move") return Make(mode, ConfirmLevel.Dangerous, "overwrite_root");
        return Make(mode, ConfirmLevel.Routine);
    }
}
