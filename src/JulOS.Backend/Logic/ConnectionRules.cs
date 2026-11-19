using System.Text.RegularExpressions;

namespace JulOS.Backend.Logic;

public sealed record ConnectionInput(string Name, string Kind, string Host, int Port, string Username, string Engine);

/// <summary>Pruefung und Bereinigung der Eingaben fuer eine gespeicherte Verbindung.</summary>
public static partial class ConnectionRules
{
    public const int MaxPerUser = 50;

    public const string EngineGuac = "guac", EngineStream = "stream";

    // Namen, IPv4/IPv6-Adressen: Buchstaben, Ziffern, Punkt, Bindestrich, Unterstrich, Doppelpunkt. Keine Leerzeichen, keine Pfade.
    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,253}$")] private static partial Regex HostPattern();

    /// <returns>Die bereinigte Eingabe, oder eine verstaendliche Fehlermeldung.</returns>
    public static (ConnectionInput? Value, string? Error) Validate(string? name, string? kind, string? host, int? port, string? username, string? engine = null, string? currentEngine = null)
    {
        name = name?.Trim() ?? "";
        host = host?.Trim() ?? "";
        username = username?.Trim() ?? "";
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) return (null, "Der Name muss 1 bis 60 Zeichen haben.");
        if (kind is not ("desktop" or "terminal")) return (null, "Art muss Desktop oder Terminal sein.");
        if (!HostPattern().IsMatch(host)) return (null, "Adresse ungültig (Name oder IP-Adresse, ohne Leerzeichen).");
        var p = port ?? (kind == "terminal" ? 22 : 3389);
        if (p is < 1 or > 65535) return (null, "Port muss zwischen 1 und 65535 liegen.");
        if (username.Length > 64 || username.Any(char.IsControl)) return (null, "Benutzername ist zu lang.");
        // Ohne Angabe bleibt die bisherige Engine (neue Verbindung: Guacamole); nur Desktops koennen den Stream waehlen; ein Terminal nutzt den eigenen SSH-Client, die Engine hat dort keine Bedeutung
        engine ??= currentEngine ?? EngineGuac;
        if (engine is not (EngineGuac or EngineStream)) return (null, "Die Engine muss „guac“ oder „stream“ sein.");
        if (kind != "desktop") engine = EngineGuac;
        return (new ConnectionInput(name, kind, host, p, username, engine), null);
    }
}
