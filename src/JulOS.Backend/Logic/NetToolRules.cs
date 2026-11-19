namespace JulOS.Backend.Logic;

/// <summary>
/// Regeln fuer die Netzwerkwerkzeuge (Ping, DNS-Abfrage, Port-Test). Ein Werkzeug darf nur gegen Ziele laufen, die der Benutzer als Verbindung gespeichert hat
/// UND die zur Erlaubnisliste des Servers passen (<see cref="HostPolicy"/>): kein freies Eingabefeld, also kein Weg, den Server als Port-Scanner oder
/// DNS-Fragesteller fuer beliebige Ziele zu benutzen (docs/SECURITY-REVIEW.md Nr. 1).
/// </summary>
public static class NetToolRules
{
    public const int MaxPorts = 10;
    public const int DefaultPingCount = 4;
    public const int MaxPingCount = 8;

    /// <summary>Die Ziele, die ein Werkzeug nehmen darf: Adressen der gespeicherten Verbindungen, ohne Doppelte (Gross/Klein egal), nach Erlaubnisliste, sortiert.</summary>
    public static IReadOnlyList<string> Targets(IEnumerable<string> savedHosts, HostPolicy policy) =>
        [.. savedHosts.Where(policy.Allows).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Das angefragte Ziel in der Schreibweise der gespeicherten Verbindung, oder null, wenn es nicht erlaubt ist.</summary>
    public static string? Resolve(string? requested, IEnumerable<string> savedHosts, HostPolicy policy)
    {
        var wanted = requested?.Trim();
        if (string.IsNullOrEmpty(wanted)) return null;
        return Targets(savedHosts, policy).FirstOrDefault(h => string.Equals(h, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <returns>Die Ports (ohne Doppelte, in der Reihenfolge der Anfrage) oder eine Fehlermeldung.</returns>
    public static (int[]? Ports, string? Error) Ports(IEnumerable<int>? requested)
    {
        var list = (requested ?? []).Distinct().ToArray();
        if (list.Length == 0) return (null, "Mindestens ein Port nötig.");
        if (list.Length > MaxPorts) return (null, $"Höchstens {MaxPorts} Ports auf einmal.");
        if (list.Any(p => p is < 1 or > 65535)) return (null, "Ports müssen zwischen 1 und 65535 liegen.");
        return (list, null);
    }

    public static int PingCount(int? requested) => Math.Clamp(requested ?? DefaultPingCount, 1, MaxPingCount);
}
