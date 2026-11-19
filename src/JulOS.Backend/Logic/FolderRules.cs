namespace JulOS.Backend.Logic;

/// <summary>Pruefung der Eingaben fuer einen Ordner (Gruppe von Verbindungen), siehe docs/FOLDERS.md.</summary>
public static class FolderRules
{
    public const int MaxPerUser = 30;

    /// <summary>Die Symbole, aus denen ein Ordner eines waehlen kann (die Oberflaeche zeichnet sie selbst, siehe folders.js).</summary>
    public static readonly IReadOnlyList<string> Icons = ["folder", "star", "home", "work", "server", "cloud", "lock", "bolt", "game", "school", "build", "globe"];

    public const string DefaultIcon = "folder";

    /// <returns>Der bereinigte Name und das Symbol, oder eine verstaendliche Fehlermeldung. Ohne Symbol gilt das Standardsymbol.</returns>
    public static (string? Name, string? Icon, string? Error) Validate(string? name, string? icon)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl)) return (null, null, "Der Name muss 1 bis 40 Zeichen haben.");
        icon = string.IsNullOrWhiteSpace(icon) ? DefaultIcon : icon.Trim();
        if (!Icons.Contains(icon)) return (null, null, "Unbekanntes Symbol.");
        return (name, icon, null);
    }
}
