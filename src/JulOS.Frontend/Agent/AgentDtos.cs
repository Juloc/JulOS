namespace JulOS.Frontend.Agent;

internal sealed record AgentEnrollRequest(string? Code, string? Version, string? Platform, string? Hostname = null, string? Os = null);
internal sealed record AgentEnrollResponse(string Token, Guid Host);
internal sealed record AgentCodeResponse(string Code, int ExpiresIn);
