using System.Text.RegularExpressions;

namespace JulOS.Backend.Logic;

public sealed record ConnectionInput(string Name, string Kind, string Host, int Port, string Username, bool DriveShare = false, bool DriveWrite = false, string Domain = "", string Notes = "", string Layout = KeyboardLayouts.Default);

/// <summary>Pruefung und Bereinigung der Eingaben fuer eine gespeicherte Verbindung.</summary>
public static partial class ConnectionRules
{
    public const int MaxPerUser = 50;
    public const int MaxNotes = 500;

    // Namen, IPv4/IPv6-Adressen: Buchstaben, Ziffern, Punkt, Bindestrich, Unterstrich, Doppelpunkt. Keine Leerzeichen, keine Pfade.
    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,253}$")] private static partial Regex HostPattern();

    // Domaene: NetBIOS- oder DNS-Name der Windows-Domaene
    [GeneratedRegex(@"^[A-Za-z0-9._\-]{0,64}$")] private static partial Regex DomainPattern();

    /// <returns>Die bereinigte Eingabe, oder eine verstaendliche Fehlermeldung.</returns>
    public static (ConnectionInput? Value, string? Error) Validate(string? name, string? kind, string? host, int? port, string? username, bool? driveShare = null, bool? driveWrite = null, bool currentDriveShare = false, bool currentDriveWrite = false,
        string? domain = null, string? notes = null, string? layout = null, string currentDomain = "", string currentNotes = "", string currentLayout = KeyboardLayouts.Default)
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

        // Laufwerksumleitung: nur Desktops, beides standardmaessig aus; Schreiben gibt es nur mit Laufwerk. Ohne Angabe bleibt der bisherige Wert.
        var share = driveShare ?? currentDriveShare;
        var write = driveWrite ?? currentDriveWrite;
        if (kind != "desktop") { share = false; write = false; }
        if (!share) write = false;

        // Domaene und Tastaturlayout gibt es nur beim Desktop (RDP); ohne Angabe bleibt der bisherige Wert (beim Anlegen: leer und Deutsch)
        var dom = (domain ?? currentDomain).Trim();
        if (!DomainPattern().IsMatch(dom)) return (null, "Domäne ungültig (bis 64 Zeichen: Buchstaben, Ziffern, Punkt, Strich, Unterstrich).");
        var lay = layout ?? currentLayout;
        if (!KeyboardLayouts.IsKnown(lay)) return (null, "Tastaturlayout unbekannt.");
        if (kind != "desktop") { dom = ""; lay = KeyboardLayouts.Default; }

        // Notizen: freier Text fuer die Person selbst, mit Zeilenumbruechen, ohne andere Steuerzeichen
        var note = (notes ?? currentNotes).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (note.Length > MaxNotes) return (null, $"Die Notizen sind zu lang (höchstens {MaxNotes} Zeichen).");
        if (note.Any(c => char.IsControl(c) && c != '\n' && c != '\t')) return (null, "Die Notizen enthalten ungültige Zeichen.");
        return (new ConnectionInput(name, kind, host, p, username, share, write, dom, note, lay), null);
    }
}
