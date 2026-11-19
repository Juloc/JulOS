using System.Collections.Concurrent;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Integrations.Agent;

namespace JulOS.Frontend.Agent;

/// <summary>Ergebnis einer Aktualisierung: Status fuer den Browser, bei Erfolg die neue Version, sonst ein Satz fuer die Anzeige.</summary>
internal sealed record UpdateOutcome(int Status, string? Version, string? Message)
{
    public bool Ok => Status == 200;
}

/// <summary>
/// Schickt dem verbundenen Agenten die Anweisung, sich zu aktualisieren (docs/AGENT_API.md "Update"): nur der feste Pfad der mitgelieferten Datei und ihre SHA-256-Summe,
/// nie eine Adresse aus einer Anfrage. Dient dem Knopf in der App und der Automatik beim Verbinden (Schalter am Agenten, Standard aus).
/// </summary>
internal sealed class AgentUpdater(AgentRelease release, AgentStore store, ILogger<AgentUpdater> log, TimeProvider time)
{
    /// <summary>Schuetzt vor Schleifen: schlug eine Automatik fehl oder bringt sie den Agenten nicht auf die Version, versucht sie es fuer diesen Host erst nach dieser Zeit wieder.</summary>
    public static readonly TimeSpan AutoCooldown = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastAuto = new();

    /// <summary>Aktualisiert den Agenten dieses Links, falls das noetig und moeglich ist; sonst ein Status 409/503 mit Begruendung.</summary>
    public async Task<UpdateOutcome> UpdateAsync(AgentLink link, CancellationToken ct)
    {
        if (release.Latest() is not { } latest) return new UpdateOutcome(503, null, "Auf dem Server liegt keine Agent-Datei.");
        if (!AgentLinks.CanUpdate(link.Version)) return new UpdateOutcome(409, null, "Dieser Agent kann sich noch nicht selbst aktualisieren: einmal neu einrichten (Agent einrichten).");
        if (!AgentLinks.IsNewer(latest.Version, link.Version)) return new UpdateOutcome(409, null, "Der Agent ist schon aktuell.");
        AgentReply reply;
        try { reply = await link.UpdateAsync(AgentRelease.Path, latest.Sha256, latest.Version, ct).ConfigureAwait(false); }
        catch (AgentUnavailableException e) { return new UpdateOutcome(502, null, e.Message); }
        if (reply.Status == 200)
        {
            log.LogInformation("Agent-Update {Host}: {From} auf {To} angewiesen, der Agent startet neu", link.HostId, link.Version ?? "?", latest.Version);
            return new UpdateOutcome(200, latest.Version, null);
        }
        var why = ErrorText(reply.Json) ?? "Der Agent hat die Aktualisierung abgelehnt.";
        log.LogWarning("Agent-Update {Host}: vom Agenten abgelehnt ({Status}): {Why}", link.HostId, reply.Status, why);
        return new UpdateOutcome(reply.Status == 409 ? 409 : 502, null, why);
    }

    /// <summary>Der "error"-Text einer Agent-Antwort, kurz und ohne Steuerzeichen (der Agent ist nicht der Server: Anzeigewerte werden nicht blind uebernommen).</summary>
    private static string? ErrorText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.String) return null;
            var text = new string(e.GetString()!.Where(c => !char.IsControl(c)).Take(120).ToArray()).Trim();
            return text.Length == 0 ? null : "Agent: " + text;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Beim Verbinden: ist die Automatik an und der Agent veraltet, wird er aktualisiert (hoechstens alle <see cref="AutoCooldown"/> je Host).</summary>
    public async Task OnConnectedAsync(AgentLink link)
    {
        var record = await store.GetAsync(link.UserId, link.HostId).ConfigureAwait(false);
        if (record is not { AutoUpdate: true }) return;
        await TryAutoAsync(link).ConfigureAwait(false);
    }

    /// <summary>Wie <see cref="OnConnectedAsync"/>, ohne die Einstellung zu lesen (der Schalter wurde gerade gesetzt).</summary>
    public async Task TryAutoAsync(AgentLink link)
    {
        if (release.Latest() is not { } latest || !AgentLinks.CanUpdate(link.Version) || !AgentLinks.IsNewer(latest.Version, link.Version)) return;
        var now = time.GetUtcNow();
        if (_lastAuto.TryGetValue(link.HostId, out var last) && now - last < AutoCooldown) return;
        _lastAuto[link.HostId] = now;
        var outcome = await UpdateAsync(link, CancellationToken.None).ConfigureAwait(false);
        log.LogInformation("Agent-Update {Host} (automatisch): {Result}", link.HostId, outcome.Ok ? "angewiesen" : outcome.Message);
    }
}
