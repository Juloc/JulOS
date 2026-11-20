namespace JulOS.Backend.Logic;

/// <summary>
/// Optionale Erlaubnisliste fuer Verbindungsziele (Einstellungen, Sicherheit; Rueckfall JULOS_ALLOWED_HOSTS): Namen, Adressen oder Netze (CIDR).
/// Leer heisst: der angemeldete Benutzer darf jedes Ziel angeben, das der Server erreicht. Die Liste wird bei jeder Pruefung neu gelesen,
/// eine Aenderung in der App gilt also ohne Neustart.
/// </summary>
public sealed class HostPolicy(Func<IReadOnlyList<string>> entries)
{
    /// <summary>Feste Liste (kommagetrennt), zum Beispiel fuer Tests.</summary>
    public HostPolicy(string? allowed) : this(() => (allowed ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { }

    public IReadOnlyList<string> Entries => entries();

    public bool Allows(string host) => Allows(entries(), host);

    public static bool Allows(IReadOnlyList<string> list, string host) => list.Count == 0 || AddressList.MatchesHost(list, host);
}
