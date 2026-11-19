namespace JulOS.Frontend.Hosts;

internal sealed record HostAgentDto(bool Online, DateTimeOffset? LastSeen, string? Version, string? Platform, bool Outdated);
/// <summary>Ein Dienst eines Hosts = eine gespeicherte Verbindung. Online ist leer, wenn nicht geprueft werden durfte (Erlaubnisliste).</summary>
internal sealed record HostServiceDto(Guid Connection, string Kind, bool? Online);
internal sealed record HostDto(Guid Id, string Name, bool Online, string? Hostname, string? Os, HostAgentDto? Agent, HostServiceDto[] Services);
internal sealed record HostsResponse(HostDto[] Hosts);
internal sealed record HostRequest(string? Name);
