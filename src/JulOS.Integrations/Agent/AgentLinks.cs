using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Agent;

/// <summary>
/// Alle Agenten, die gerade mit dem Server verbunden sind, je Verbindung hoechstens einer (ein neuer ersetzt den alten). Gefunden wird ein Agent nur
/// ueber die Kennung der Verbindung und den Benutzer, dem sie gehoert: wer die Kennung einer fremden Verbindung kennt, bekommt nichts.
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

    /// <summary>Der verbundene Agent dieser Verbindung, wenn die Verbindung dem Benutzer gehoert; sonst null.</summary>
    public AgentLink? Get(Guid connectionId, Guid userId) => _links.TryGetValue(connectionId, out var l) && l.UserId == userId ? l : null;

    public bool IsOnline(Guid connectionId) => _links.ContainsKey(connectionId);

    public AgentLinkStatus? Status(Guid connectionId) => _links.TryGetValue(connectionId, out var l) ? new AgentLinkStatus(l.Version, l.Platform, l.Since) : null;

    /// <summary>Trennt den Agenten einer Verbindung (Agent entfernt, Verbindung geloescht).</summary>
    public void Disconnect(Guid connectionId)
    {
        if (_links.TryRemove(connectionId, out var l)) l.Disconnect();
    }

    /// <summary>Nimmt eine aufgebaute WebSocket-Verbindung entgegen und bedient sie, bis sie endet. <paramref name="touch"/> merkt Version und Plattform.</summary>
    public async Task RunAsync(WebSocket ws, Guid connectionId, Guid userId, string serverVersion, Func<string?, string?, Task> touch, CancellationToken ct)
    {
        var link = new AgentLink(ws, connectionId, userId, log);
        AgentHello? hello;
        try { hello = await link.HandshakeAsync(VersionOk, serverVersion, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { link.Disconnect(); return; }
        if (hello is null) return;
        if (!hello.Accepted)
        {
            log.LogWarning("Agent-Link {Connection}: Agent {Version} ist zu alt (mindestens {Min}), abgewiesen", connectionId, hello.Version ?? "?", MinAgentVersion);
            await touch(hello.Version, hello.Platform).ConfigureAwait(false);
            return;
        }

        if (_links.TryGetValue(connectionId, out var old) && _links.TryUpdate(connectionId, link, old)) old.Disconnect();
        else _links[connectionId] = link;
        log.LogInformation("Agent-Link {Connection}: verbunden (Agent {Version}, {Platform})", connectionId, hello.Version ?? "?", hello.Platform ?? "?");
        await touch(hello.Version, hello.Platform).ConfigureAwait(false);
        try { await link.LoopAsync(ct).ConfigureAwait(false); }
        finally
        {
            _links.TryRemove(new KeyValuePair<Guid, AgentLink>(connectionId, link));
            log.LogInformation("Agent-Link {Connection}: getrennt", connectionId);
            try { await touch(null, null).ConfigureAwait(false); } catch (Exception e) when (e is not OutOfMemoryException) { /* nur Anzeigewert */ }
        }
    }
}

public sealed record AgentLinkStatus(string? Version, string? Platform, DateTimeOffset Since);
