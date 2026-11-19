using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using JulOS.Backend.Data;
using JulOS.Integrations.Agent;

namespace JulOS.Frontend.Hosts;

/// <summary>Eine feste, lesende Anfrage an einen Host-Agenten: Name, die Faehigkeit, die der Agent dafuer gemeldet haben muss, der Pfad (nur dieser wird je gesendet), die Frist und der Parser seiner Antwort.</summary>
internal sealed record HostQuery<T>(string Kind, string Cap, string Path, TimeSpan Timeout, Func<JsonElement, T?> Parse) where T : class;

/// <summary>State: ok, offline (Agent eingerichtet, nicht verbunden), no_agent, unsupported (kann der Agent nicht), error (Agent hat nicht oder falsch geantwortet). Data gibt es auch aus dem Zwischenspeicher (dann Stale).</summary>
internal sealed record HostInfoResult<T>(string State, DateTimeOffset? At, bool Stale, string? Error, T? Data) where T : class;

/// <summary>
/// Fragt Host-Agenten (docs/HOST-AGENT.md) nach Systemwerten, Updates, Diensten und Containern, hoechstens alle <see cref="Ttl"/> je Host und Art: die Antwort wird im Speicher
/// gehalten, gleichzeitige Anfragen teilen sich eine Abfrage. Der Server fragt nie von selbst nach, nur wenn der Browser es verlangt (also jemand hinsieht). Ein ausdruecklicher
/// Knopf ("jetzt neu lesen") darf frueher, aber nicht oefter als alle <see cref="FreshMin"/>. Ist der Agent weg oder antwortet nicht, zeigt der Server den letzten Stand als Stale.
/// </summary>
internal sealed partial class HostInfoService(AgentLinks links, HostStore hosts, TimeProvider time, ILogger<HostInfoService> log)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FreshMin = TimeSpan.FromSeconds(15);

    private sealed record Entry(DateTimeOffset At, object Data);
    private readonly ConcurrentDictionary<(Guid Host, string Kind), Entry> _cache = new();
    private readonly ConcurrentDictionary<(Guid Host, string Kind), SemaphoreSlim> _gates = new();

    [GeneratedRegex("^[a-z_]{1,32}$")] private static partial Regex ErrorCode();

    /// <summary>Null, wenn der Host nicht dem Benutzer gehoert (404).</summary>
    public async Task<HostInfoResult<T>?> GetAsync<T>(Guid userId, Guid hostId, HostQuery<T> query, bool fresh, CancellationToken ct) where T : class
    {
        if (await hosts.AgentEnrolledAsync(userId, hostId, ct) is not { } enrolled) return null;
        var key = (hostId, query.Kind);
        _cache.TryGetValue(key, out var cached);
        HostInfoResult<T> Old(string state, string? error = null) => new(state, cached?.At, cached is not null, error, cached?.Data as T);

        if (!enrolled) { _cache.TryRemove(key, out _); return new("no_agent", null, false, null, null); }
        if (links.Get(hostId, userId) is not { } link) return Old("offline");
        if (!link.Caps.Contains(query.Cap)) return new("unsupported", null, false, null, null);

        if (Fresh(cached, fresh) is { } hit) return hit;
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cache.TryGetValue(key, out cached);
            if (Fresh(cached, fresh) is { } again) return again;       // ein anderer Aufruf war schneller: seine Antwort gilt auch hier

            AgentReply reply;
            try { reply = await link.RequestAsync(HttpMethod.Get, query.Path, null, query.Timeout, ct).ConfigureAwait(false); }
            catch (AgentUnavailableException e)
            {
                log.LogInformation("Host-Info {Host} {Kind}: Agent antwortet nicht ({Why})", hostId, query.Kind, e.Message);
                return links.Get(hostId, userId) is null ? Old("offline") : Old("error", "timeout");
            }
            return Evaluate(reply, hostId, query, cached);
        }
        finally { gate.Release(); }

        HostInfoResult<T>? Fresh(Entry? e, bool explicitRefresh) =>
            e is not null && time.GetUtcNow() - e.At < (explicitRefresh ? FreshMin : Ttl) && e.Data is T data ? new HostInfoResult<T>("ok", e.At, false, null, data) : null;
    }

    private HostInfoResult<T> Evaluate<T>(AgentReply reply, Guid hostId, HostQuery<T> query, Entry? cached) where T : class
    {
        HostInfoResult<T> Old(string state, string? error) => new(state, cached?.At, cached is not null, error, cached?.Data as T);
        try
        {
            using var doc = JsonDocument.Parse(reply.Json);
            if (reply.Status == 200)
            {
                if (query.Parse(doc.RootElement) is { } data)
                {
                    var now = time.GetUtcNow();
                    _cache[(hostId, query.Kind)] = new Entry(now, data);
                    return new("ok", now, false, null, data);
                }
                return Old("error", "invalid_answer");
            }
            if (reply.Status == 404) return new("unsupported", null, false, null, null);
            var code = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { } c && ErrorCode().IsMatch(c) ? c : "agent_error";
            log.LogInformation("Host-Info {Host} {Kind}: Agent antwortet mit {Status} ({Code})", hostId, query.Kind, reply.Status, code);
            return Old("error", code);
        }
        catch (JsonException) { return Old("error", "invalid_answer"); }
    }
}
