namespace JulOS.Backend.Logic;

/// <summary>
/// Optionale Erlaubnisliste fuer Verbindungsziele (JULOS_ALLOWED_HOSTS, kommagetrennte Namen oder Adressen).
/// Leer heisst: der angemeldete Benutzer darf jedes Ziel angeben, das der Server erreicht.
/// </summary>
public sealed class HostPolicy(string? allowed)
{
    private readonly HashSet<string> _hosts = new(
        (allowed ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    public bool Allows(string host) => _hosts.Count == 0 || _hosts.Contains(host);
}
