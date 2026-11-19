namespace JulOS.Frontend.Agent;

internal sealed record AgentEnrollRequest(string? Code, string? Version, string? Platform);
internal sealed record AgentEnrollResponse(string Token, Guid Connection);
internal sealed record AgentCodeResponse(string Code, int ExpiresIn);
internal sealed record AgentStatusDto(Guid Connection, bool Online, DateTimeOffset? LastSeen, string? Version, string? Platform, bool Outdated);
internal sealed record AgentsResponse(AgentStatusDto[] Agents);
