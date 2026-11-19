namespace JulOS.Frontend.Hosts;

/// <summary>Der Agent eines Hosts. Kind: desktop oder host (Agent ohne Desktop); Caps: was ein Host-Agent kann (nur solange verbunden); Allow: was er lokal an Schreibrechten erlaubt; Control: die Stufe am Server (read, updates, full). Latest ist die Version, die dieser Server mitliefert; UpdateAvailable gilt auch fuer einen getrennten Agenten (letzte bekannte Version), CanUpdate sagt, ob er sich selbst aktualisieren kann.</summary>
internal sealed record HostAgentDto(bool Online, DateTimeOffset? LastSeen, string? Version, string? Platform, bool Outdated, string? Latest = null, bool UpdateAvailable = false, bool CanUpdate = false, bool AutoUpdate = false,
    string? Kind = null, string[]? Caps = null, string[]? Allow = null, string? Control = null);
/// <summary>Ein Dienst eines Hosts = eine gespeicherte Verbindung. Online ist leer, wenn nicht geprueft werden durfte (Erlaubnisliste).</summary>
internal sealed record HostServiceDto(Guid Connection, string Kind, bool? Online);
internal sealed record HostDto(Guid Id, string Name, bool Online, string? Hostname, string? Os, HostAgentDto? Agent, HostServiceDto[] Services);
internal sealed record HostsResponse(HostDto[] Hosts);
internal sealed record HostRequest(string? Name);
