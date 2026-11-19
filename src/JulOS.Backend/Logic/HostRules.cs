namespace JulOS.Backend.Logic;

/// <summary>Pruefung der Eingaben fuer einen Host (einen Rechner oder Server mit eigenen Verbindungen und eigenem Agenten).</summary>
public static class HostRules
{
    public const int MaxPerUser = 20;

    /// <summary>Name des Hosts, den ein Benutzer ohne eigene Angabe bekommt (Migration und erste Verbindung).</summary>
    public const string DefaultName = "Mein Rechner";

    /// <returns>Der bereinigte Name, oder eine verstaendliche Fehlermeldung.</returns>
    public static (string? Name, string? Error) ValidateName(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) return (null, "Der Name muss 1 bis 60 Zeichen haben.");
        return (name, null);
    }
}
