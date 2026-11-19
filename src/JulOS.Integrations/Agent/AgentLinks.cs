using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Agent;

/// <summary>
/// Alle Agenten, die gerade mit dem Server verbunden sind, je Host hoechstens einer (ein neuer ersetzt den alten). Gefunden wird ein Agent nur
/// ueber die Kennung des Hosts und den Benutzer, dem er gehoert: wer die Kennung eines fremden Hosts kennt, bekommt nichts.
/// </summary>
public sealed class AgentLinks(ILogger<AgentLinks> log)
{
    /// <summary>Aelter als diese Version spricht der Agent nicht mehr mit diesem Server.</summary>
    public static readonly Version MinAgentVersion = new(0, 3, 0);

    private readonly ConcurrentDictionary<Guid, AgentLink> _links = new();

    public static bool VersionOk(string? version) => TryParseVersion(version) is { } v && v >= MinAgentVersion;

    public static Version? TryParseVersion(string? text)
    {
        if (text is null) return null;
        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;
        return Version.TryParse(text[..end], out var v) ? v : null;
    }

    /// <summary>Der verbundene Agent dieses Hosts, wenn der Host dem Benutzer gehoert; sonst null.</summary>
    public AgentLink? Get(Guid hostId, Guid userId) => _links.TryGetValue(hostId, out var l) && l.UserId == userId ? l : null;

    public bool IsOnline(Guid hostId) => _links.ContainsKey(hostId);

    public AgentLinkStatus? Status(Guid hostId) => _links.TryGetValue(hostId, out var l) ? new AgentLinkStatus(l.Version, l.Platform, l.Hostname, l.Os, l.Since) : null;

    /// <summary>Trennt den Agenten eines Hosts (Agent entfernt, Host geloescht).</summary>
    public void Disconnect(Guid hostId)
    {
        if (_links.TryRemove(hostId, out var l)) l.Disconnect();
    }

    /// <summary>Nimmt eine aufgebaute WebSocket-Verbindung entgegen und bedient sie, bis sie endet. <paramref name="touch"/> merkt, was der Agent von sich meldet.</summary>
    public async Task RunAsync(WebSocket ws, Guid hostId, Guid userId, string serverVersion, Func<AgentInfo, Task> touch, CancellationToken ct)
    {
        var link = new AgentLink(ws, hostId, userId, log);
        AgentHello? hello;
        try { hello = await link.HandshakeAsync(VersionOk, serverVersion, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { link.Disconnect(); return; }
        if (hello is null) return;
        if (!hello.Accepted)
        {
            log.LogWarning("Agent-Link {Host}: Agent {Version} ist zu alt (mindestens {Min}), abgewiesen", hostId, hello.Version ?? "?", MinAgentVersion);
            await touch(hello.Info).ConfigureAwait(false);
            return;
        }

        if (_links.TryGetValue(hostId, out var old) && _links.TryUpdate(hostId, link, old)) old.Disconnect();
        else _links[hostId] = link;
        log.LogInformation("Agent-Link {Host}: verbunden (Agent {Version}, {Platform})", hostId, hello.Version ?? "?", hello.Info.Platform ?? "?");
        await touch(hello.Info).ConfigureAwait(false);
        try { await link.LoopAsync(ct).ConfigureAwait(false); }
        finally
        {
            _links.TryRemove(new KeyValuePair<Guid, AgentLink>(hostId, link));
            log.LogInformation("Agent-Link {Host}: getrennt", hostId);
            try { await touch(new AgentInfo(null, null)).ConfigureAwait(false); } catch (Exception e) when (e is not OutOfMemoryException) { /* nur Anzeigewert */ }
        }
    }
}

public sealed record AgentLinkStatus(string? Version, string? Platform, string? Hostname, string? Os, DateTimeOffset Since);
