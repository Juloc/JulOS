using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace JulOS.Backend.Logic;

/// <summary>
/// Listen von Adressen fuer die Einstellungen der Sicherheit (vertraute Proxys, erlaubte Zielhosts): Pruefung der Eingabe und der Vergleich mit einer Adresse.
/// Ein Eintrag ist eine IP-Adresse, ein Netz in CIDR-Schreibweise (192.0.2.0/24) oder, bei den Zielhosts, ein Name. Namen werden klein geschrieben abgelegt.
/// </summary>
public static partial class AddressList
{
    public const int MaxEntries = 50;

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._\-]{0,251}[A-Za-z0-9])?$")] private static partial Regex NamePattern();

    /// <summary>Ein Eintrag als Netz (eine einzelne Adresse als /32 oder /128), null wenn es keine Adresse und kein Netz ist.</summary>
    public static IPNetwork? ParseNetwork(string entry)
    {
        entry = entry.Trim();
        if (entry.Length == 0 || entry.Length > 64) return null;
        if (IPAddress.TryParse(entry, out var ip) && !entry.Contains('/')) return new IPNetwork(ip, ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        return entry.Contains('/') && IPNetwork.TryParse(entry, out var net) ? net : null;
    }

    /// <summary>Prueft und bereinigt eine Liste. Leere Eintraege entfallen, doppelte auch. <paramref name="names"/>: auch Hostnamen sind erlaubt.</summary>
    public static (string[]? Entries, string? Error) Validate(IEnumerable<string>? input, bool names)
    {
        var list = new List<string>();
        foreach (var raw in input ?? [])
        {
            var e = (raw ?? "").Trim();
            if (e.Length == 0) continue;
            string clean;
            if (ParseNetwork(e) is { } net) clean = e.Contains('/') ? net.ToString() : IPAddress.Parse(e).ToString();
            else if (names && NamePattern().IsMatch(e) && !LooksLikeBadAddress(e)) clean = e.ToLowerInvariant();
            else return (null, $"„{Shorten(e)}“ ist keine gültige Adresse{(names ? ", kein gültiges Netz und kein gültiger Name" : " und kein gültiges Netz (zum Beispiel 192.0.2.10 oder 192.0.2.0/24)")}.");
            if (!list.Contains(clean, StringComparer.OrdinalIgnoreCase)) list.Add(clean);
        }
        if (list.Count > MaxEntries) return (null, $"Höchstens {MaxEntries} Einträge.");
        return ([.. list], null);
    }

    // "300.1.1.1" oder "1.2.3" sind nach dem Namensmuster erlaubt, sind aber ein Tippfehler bei einer Adresse: nur Ziffern und Punkte heisst Adresse
    private static bool LooksLikeBadAddress(string e) => e.All(c => char.IsAsciiDigit(c) || c == '.');

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>Ob <paramref name="address"/> in einem der Eintraege liegt (nur Adressen und Netze, Namen zaehlen nicht). Eine IPv4-Adresse in IPv6-Schreibweise gilt als IPv4.</summary>
    public static bool ContainsAddress(IEnumerable<string> entries, IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var e in entries)
            if (ParseNetwork(e) is { } net && net.Contains(address)) return true;
        return false;
    }

    /// <summary>Ob der Zielhost eines Eintrags gilt: gleicher Name (Gross/Klein egal), gleiche Adresse oder eine Adresse im Netz.</summary>
    public static bool MatchesHost(IEnumerable<string> entries, string host)
    {
        var isIp = IPAddress.TryParse(host, out var ip);
        foreach (var e in entries)
        {
            if (string.Equals(e, host, StringComparison.OrdinalIgnoreCase)) return true;
            if (isIp && ParseNetwork(e) is { } net && net.Contains(ip!.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) return true;
        }
        return false;
    }
}
