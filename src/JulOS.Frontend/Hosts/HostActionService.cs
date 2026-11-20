using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Integrations.Agent;

namespace JulOS.Frontend.Hosts;

/// <summary>Zeiten des Aktionsdienstes: wie lange eine Bestaetigung gilt und wie oft der Server nach einem Verbindungsabbruch das Ergebnis beim Agenten abholt.</summary>
internal sealed record HostActionOptions(TimeSpan ConfirmLifetime, TimeSpan ReconcileInterval, int ReconcileTries)
{
    public static HostActionOptions Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 360);
}

/// <summary>Ein Nein des Dienstes: HTTP-Status und ein kurzer Code (der Browser uebersetzt ihn; der Code steht auch im Verlauf).</summary>
internal sealed record ActionDenied(int Status, string Code);

/// <summary>Was der Benutzer bestaetigen soll: genau Host, Aktion, Ziel; bei Neustart, Ausschalten und "alle Updates" muss er <see cref="TypeToConfirm"/> (den Rechnernamen) tippen.</summary>
internal sealed record ActionConfirmation(string Token, int ExpiresIn, string Action, string? Target, string? TargetName, string Host, string? TypeToConfirm, string[] Warnings, bool ReadOnly);

/// <summary>Das Ergebnis, das der Agent meldet, Feld fuer Feld gelesen (er ist nicht der Server): kurze Texte, begrenzte Listen.</summary>
internal sealed record HostActionResult(bool Ok, bool Accepted, bool Already, bool? Updated, bool RebootRequired, int? Count, string[]? Lines, string? Output, string? Name, string? Image);

/// <summary>Ein laufender oder fertiger Auftrag, im Speicher des Servers. State: running, ok, failed, accepted (Neustart angenommen), lost (Verbindung weg, Ergebnis wird beim Agenten abgeholt).</summary>
internal sealed class HostActionJob(string id, Guid hostId, Guid userId, string action, string? target, string? targetName, DateTimeOffset started)
{
    public string Id { get; } = id;
    public Guid HostId { get; } = hostId;
    public Guid UserId { get; } = userId;
    public string Action { get; } = action;
    public string? Target { get; } = target;
    public string? TargetName { get; } = targetName;
    public DateTimeOffset Started { get; } = started;
    public string State { get; set; } = "running";
    public string? Error { get; set; }
    public string? Progress { get; set; }
    public int? Done { get; set; }
    public int? Total { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public HostActionResult? Result { get; set; }
    public bool Settling { get; set; }                         // das Ergebnis wird gerade festgehalten (Verlauf schreiben), der Endzustand folgt gleich
    public bool Final => State is "ok" or "failed" or "accepted";
}

/// <summary>
/// Schreibende Aktionen auf einem Host (Stufe 3, docs/HOST-AGENT.md), immer in zwei Schritten: <see cref="PrepareAsync"/> prueft alles und gibt eine kurzlebige, einmalige Bestaetigung
/// zurueck, die genau benennt, was geschieht (nur ihr Hash wird gemerkt, sie gilt fuer diese Sitzung, diesen Host, diese Aktion und dieses Ziel); erst <see cref="RunAsync"/> mit ihr fuehrt aus.
/// Nie durch einen Zeitplan, nie durch den Agenten. Zwei Schluessel: der Server prueft Pausenschalter, Stufe (agents.control), Verbindung, Version und das lokal gemeldete Recht des Agenten,
/// der Agent und sein Helfer pruefen lokal noch einmal. Jede Anfrage, Ablehnung und jedes Ergebnis steht im Verlauf (host_audit). Pro Host laeuft nur eine schreibende Aktion.
/// </summary>
internal sealed class HostActionService(AgentLinks links, AgentStore agents, HostStore hosts, HostAuditStore audit, HostInfoService info, SettingsStore settings, IConfiguration config,
    TimeProvider time, HostActionOptions options, ILogger<HostActionService> log)
{
    private sealed record Pending(Guid UserId, Guid SessionId, Guid HostId, string Action, string? Target, string? Expected, int Lines, DateTimeOffset Expires);
    private sealed record Gate(HostRecord Host, AgentRecord Agent, AgentLink Link, HostActionDef Def);

    private readonly ConcurrentDictionary<string, Pending> _confirmations = new();      // Hash der Bestaetigung -> was sie erlaubt
    private readonly ConcurrentDictionary<string, HostActionJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, string> _busy = new();                  // Host -> Auftrag, der gerade laeuft

    private const int MaxPendingPerUser = 20;
    private const int MaxJobs = 200;

    // ---------------------------------------------------------------- Pausenschalter

    /// <summary>Der globale Pausenschalter: JULOS_HOST_ACTIONS=off (Umgebung, hat Vorrang) oder die Einstellung des Benutzers. Pausiert: keine Aktion, egal was sonst erlaubt ist.</summary>
    public async Task<bool> IsPausedAsync(Guid userId, CancellationToken ct)
    {
        if (config["JULOS_HOST_ACTIONS"]?.Trim().ToLowerInvariant() is "off" or "paused" or "false" or "0") return true;
        var all = await settings.GetAllAsync(userId, ct);
        if (!all.TryGetValue("hostActions", out var json)) return false;
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }

    public Task SetPausedAsync(Guid userId, bool paused, CancellationToken ct) => settings.SetAsync(userId, "hostActions", paused ? """{"paused":true}""" : """{"paused":false}""", ct);

    // ---------------------------------------------------------------- Stufe am Server

    public async Task<ActionDenied?> SetControlAsync(Guid userId, Guid hostId, string? level, CancellationToken ct)
    {
        if (!AgentControl.IsLevel(level)) return new(400, "invalid_level");
        if (await hosts.GetAsync(userId, hostId, ct) is not { } host) return new(404, "host_not_found");
        if (!await agents.SetControlAsync(userId, hostId, level!, ct)) return new(404, "no_agent");
        await TryAudit(userId, host, "control.set", level, "ok", null, null);
        log.LogInformation("Aktionen am Host {Host}: Stufe {Level}, Benutzer {User}", hostId, level, userId);
        return null;
    }

    // ---------------------------------------------------------------- Pruefungen

    private async Task TryAudit(Guid userId, HostRecord host, string action, string? target, string evt, string? detail, string? job, CancellationToken ct = default)
    {
        try { await audit.AddAsync(userId, host.Id, host.Name, action, target, evt, detail, job, ct); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException) { log.LogWarning("Verlauf nicht geschrieben ({Type})", e.GetType().Name); }
    }

    private async Task<(Gate? Ok, ActionDenied? Denied)> GateAsync(SessionIdentity who, Guid hostId, string? actionName, string? target, CancellationToken ct)
    {
        if (await hosts.GetAsync(who.UserId, hostId, ct) is not { } host) return (null, new(404, "host_not_found"));
        async Task<(Gate?, ActionDenied?)> No(int status, string code, string action = "?", string? t = null)
        {
            await TryAudit(who.UserId, host, action, t ?? target, "denied", code, null, ct);
            return (null, new ActionDenied(status, code));
        }
        if (await agents.GetAsync(who.UserId, hostId, ct) is not { } agent) return await No(404, "no_agent");
        if (HostActions.Find(actionName) is not { } def) return await No(400, "unknown_action", actionName is null ? "?" : actionName[..Math.Min(actionName.Length, 40)]);
        if (!HostActions.ValidTarget(def, target)) return await No(400, "invalid_target", def.Name);
        if (await IsPausedAsync(who.UserId, ct)) return await No(403, "paused", def.Name);
        if (!AgentControl.Allows(agent.Control, def.Right)) return await No(403, "control_off", def.Name);
        if (links.Get(hostId, who.UserId) is not { } link) return await No(409, "offline", def.Name);
        if (AgentLinks.TryParseVersion(link.Version) is not { } v || v < HostActions.MinAgentVersion) return await No(409, "agent_outdated", def.Name);
        if (!link.Caps.Contains(def.Cap)) return await No(409, "unsupported", def.Name);
        if (!link.Allow.Contains(def.Right)) return await No(403, "agent_not_permitted", def.Name);       // der Agent erlaubt es lokal nicht (zweiter Schluessel)
        if (_busy.ContainsKey(hostId)) return await No(409, "busy", def.Name);
        return (new Gate(host, agent, link, def), null);
    }

    /// <summary>Das Ziel muss in der Liste stehen, die der Agent selbst gemeldet hat (Paket der Update-Liste, Dienst der Dienstliste, Container der Containerliste). Gibt den Anzeigenamen und Warnungen zurueck.</summary>
    private async Task<(string? Name, string[] Warnings, ActionDenied? Denied)> CheckTargetAsync(Guid userId, Gate g, string? target, CancellationToken ct)
    {
        var warnings = new List<string>();
        switch (g.Def.Target)
        {
            case HostTarget.Package:
            {
                var r = await info.GetAsync(userId, g.Host.Id, HostInfoEndpoints.Updates, false, ct);
                if (r?.Data is not { } u || r.State != "ok") return (null, [], new(409, "list_unavailable"));
                if (target == HostActions.AllPackages)
                {
                    if (u.KeptBack.Length > 0) warnings.Add("kept_back");
                    return ("all", [.. warnings], null);
                }
                return u.Packages.Any(p => p.Name == target) ? (target, [], null) : (null, [], new(404, "unknown_target"));
            }
            case HostTarget.Unit:
            {
                if (g.Def.Name is "service.stop" or "service.restart" && target!.StartsWith("julos-agent", StringComparison.Ordinal)) return (null, [], new(409, "protected_unit"));
                var r = await info.GetAsync(userId, g.Host.Id, HostInfoEndpoints.Units, true, ct);
                if (r?.Data is not { } list || r.State != "ok") return (null, [], new(409, "list_unavailable"));
                return list.Units.Any(x => x.Unit == target) ? (target, [], null) : (null, [], new(404, "unknown_target"));
            }
            case HostTarget.Container:
            {
                var r = await info.GetAsync(userId, g.Host.Id, HostInfoEndpoints.Docker, true, ct);
                if (r?.Data is not { } d || r.State != "ok") return (null, [], new(409, "list_unavailable"));
                if (d.Containers.FirstOrDefault(c => c.Id == target) is not { } c) return (null, [], new(404, "unknown_target"));
                if (g.Def.Name is "docker.stop" or "docker.restart" && HostActions.LooksLikeJulOs(c.Project, c.Image)) warnings.Add("julos_group");
                return (c.Name.Length > 0 ? c.Name : target, [.. warnings], null);
            }
            default: return (null, [], null);
        }
    }

    private static string AuditTarget(string? target, string? name) => name is not null && name != target && target is not null ? $"{name} ({target})" : target ?? "";

    // ---------------------------------------------------------------- Schritt 1: Bestaetigung

    public async Task<(ActionConfirmation? Value, ActionDenied? Denied)> PrepareAsync(SessionIdentity who, Guid hostId, string? actionName, string? target, int? lines, CancellationToken ct)
    {
        target = string.IsNullOrWhiteSpace(target) ? null : target;
        var (gate, denied) = await GateAsync(who, hostId, actionName, target, ct);
        if (gate is null) return (null, denied);
        var (name, warnings, bad) = await CheckTargetAsync(who.UserId, gate, target, ct);
        if (bad is not null) { await TryAudit(who.UserId, gate.Host, gate.Def.Name, target, "denied", bad.Code, null, ct); return (null, bad); }

        var now = time.GetUtcNow();
        foreach (var (key, p) in _confirmations) if (p.Expires <= now) _confirmations.TryRemove(key, out _);       // abgelaufene vergessen
        if (_confirmations.Count(c => c.Value.UserId == who.UserId) >= MaxPendingPerUser) return (null, new(429, "too_many"));

        var typed = gate.Def.RequiresTyping(target) ? HostName(gate) : null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = now + options.ConfirmLifetime;
        var n = Math.Clamp(lines ?? 100, 10, 200);
        _confirmations[Hash(token)] = new Pending(who.UserId, who.Id, hostId, gate.Def.Name, target, typed, n, expires);
        await TryAudit(who.UserId, gate.Host, gate.Def.Name, AuditTarget(target, name), "prepared", null, null, ct);
        return (new ActionConfirmation(token, (int)options.ConfirmLifetime.TotalSeconds, gate.Def.Name, target, name, gate.Host.Name, typed, warnings, gate.Def.ReadOnly), null);
    }

    /// <summary>Der Name, den der Benutzer zur Bestaetigung tippen soll: der Rechnername, den der Agent meldet, sonst der Name des Hosts in JulOS.</summary>
    private static string HostName(Gate g) => g.Link.Hostname ?? g.Host.Name;

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ---------------------------------------------------------------- Schritt 2: ausfuehren

    public async Task<(HostActionJob? Job, ActionDenied? Denied)> RunAsync(SessionIdentity who, Guid hostId, string? token, string? actionName, string? target, string? typed, CancellationToken ct)
    {
        target = string.IsNullOrWhiteSpace(target) ? null : target;
        if (await hosts.GetAsync(who.UserId, hostId, ct) is not { } host) return (null, new(404, "host_not_found"));
        async Task<(HostActionJob?, ActionDenied?)> No(int status, string code)
        {
            await TryAudit(who.UserId, host, actionName is null ? "?" : actionName[..Math.Min(actionName.Length, 40)], target, "denied", code, null, ct);
            return (null, new ActionDenied(status, code));
        }
        if (string.IsNullOrEmpty(token) || token.Length > 100 || !_confirmations.TryRemove(Hash(token), out var p)) return await No(403, "confirmation_invalid");   // unbekannt oder schon benutzt: die Bestaetigung gilt einmal
        if (p.Expires <= time.GetUtcNow()) return await No(403, "confirmation_expired");
        if (p.UserId != who.UserId || p.SessionId != who.Id || p.HostId != hostId || p.Action != actionName || p.Target != target) return await No(403, "confirmation_mismatch");
        if (p.Expected is not null && !string.Equals(typed?.Trim(), p.Expected, StringComparison.OrdinalIgnoreCase))
        {
            _confirmations[Hash(token)] = p;                    // nur das Tippen war falsch: die Bestaetigung bleibt (sie gilt weiter bis zum Ablauf)
            return await No(400, "typed_mismatch");
        }

        // Alles noch einmal: zwischen Schritt 1 und 2 kann sich etwas geaendert haben (Pause, Stufe, Verbindung)
        var (gate, denied) = await GateAsync(who, hostId, p.Action, p.Target, ct);
        if (gate is null) return (null, denied);
        var (name, _, bad) = await CheckTargetAsync(who.UserId, gate, p.Target, ct);
        if (bad is not null) { await TryAudit(who.UserId, host, gate.Def.Name, p.Target, "denied", bad.Code, null, ct); return (null, bad); }

        var id = Guid.NewGuid().ToString("N");
        if (!_busy.TryAdd(hostId, id)) return await No(409, "busy");
        var job = new HostActionJob(id, hostId, who.UserId, gate.Def.Name, p.Target, name, time.GetUtcNow());
        try { await audit.AddAsync(who.UserId, hostId, host.Name, gate.Def.Name, AuditTarget(p.Target, name), "started", null, id, ct); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException)
        {
            _busy.TryRemove(hostId, out _);                      // ohne Eintrag im Verlauf geschieht nichts
            log.LogError("Verlauf nicht geschrieben ({Type}): Aktion {Action} nicht ausgefuehrt", e.GetType().Name, gate.Def.Name);
            return (null, new(500, "audit_failed"));
        }
        if (_jobs.Count >= MaxJobs) foreach (var (k, j) in _jobs) if (j.Final && j.Finished < time.GetUtcNow() - TimeSpan.FromHours(1)) _jobs.TryRemove(k, out _);
        _jobs[id] = job;
        log.LogInformation("Aktion {Action} auf Host {Host} gestartet, Benutzer {User}, Auftrag {Job}", gate.Def.Name, hostId, who.UserName, id);
        _ = Task.Run(() => ExecuteAsync(gate, job, p.Lines));
        return (job, null);
    }

    private static string BodyFor(HostActionDef def, string job, string? target, int lines)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("job", job);
            switch (def.Target)
            {
                case HostTarget.Package when target == HostActions.AllPackages: w.WriteBoolean("all", true); break;
                case HostTarget.Package: w.WriteStartArray("packages"); w.WriteStringValue(target); w.WriteEndArray(); break;
                case HostTarget.Unit: w.WriteString("unit", target); break;
                case HostTarget.Container: w.WriteString("id", target); break;
            }
            if (def.Name.StartsWith("logs.", StringComparison.Ordinal)) w.WriteNumber("lines", lines);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task ExecuteAsync(Gate g, HostActionJob job, int lines)
    {
        try
        {
            var reply = await g.Link.RequestAsync(HttpMethod.Post, g.Def.Path, BodyFor(g.Def, job.Id, job.Target, lines), g.Def.Timeout, CancellationToken.None, p =>
            {
                lock (job) { job.Progress = p.Text; job.Done = p.Done; job.Total = p.Total; }
            });
            await FinishAsync(g.Host, g.Def, job, reply.Status, reply.Json);
        }
        catch (AgentUnavailableException)
        {
            // Verbindung weg oder keine Antwort: die Aktion kann trotzdem laufen oder fertig sein. Das Ergebnis holt der Server beim Agenten ab, sobald er wieder da ist.
            lock (job) job.State = "lost";
            _busy.TryRemove(job.HostId, out _);
            await TryAudit(job.UserId, g.Host, job.Action, AuditTarget(job.Target, job.TargetName), "lost", "no_answer", job.Id);
            _ = Task.Run(() => ReconcileAsync(g.Host, g.Def, job));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.LogError("Aktion {Action} auf Host {Host}: unerwarteter Fehler ({Type})", job.Action, job.HostId, e.GetType().Name);
            await FinishAsync(g.Host, g.Def, job, 500, """{"error":"failed"}""");
        }
    }

    /// <summary>Haelt das Ergebnis fest: Zustand des Auftrags, Eintrag im Verlauf, Sperre des Hosts frei.</summary>
    private async Task FinishAsync(HostRecord host, HostActionDef def, HostActionJob job, int status, string json)
    {
        var (result, error) = ParseReply(status, json);
        lock (job)
        {
            if (job.Final || job.Settling) return;               // das Ergebnis kam schon (Abfrage und Hintergrundlauf zugleich): nur einmal festhalten
            job.Settling = true;
        }
        var state = error is not null ? "failed" : result?.Accepted == true ? "accepted" : "ok";
        _busy.TryRemove(job.HostId, out _);
        var detail = error ?? (result?.RebootRequired == true ? "reboot_required" : result?.Already == true ? "already" : null);
        // Erst der Verlauf, dann der sichtbare Endzustand: wer "fertig" sieht, findet das Ergebnis auch im Verlauf
        await TryAudit(job.UserId, host, job.Action, AuditTarget(job.Target, job.TargetName), state, detail, job.Id);
        lock (job)
        {
            job.Result = result;
            job.Error = error;
            job.Finished = time.GetUtcNow();
            job.State = state;
        }
        if (!def.ReadOnly) info.Forget(job.HostId);              // Updates, Dienste und Container haben sich geaendert: neu lesen
    }

    /// <summary>Die Antwort des Agenten als (Ergebnis, Fehlercode). Ein Fehler ist ein kurzer Code aus Kleinbuchstaben, nie freier Text des Agenten.</summary>
    internal static (HostActionResult? Result, string? Error) ParseReply(int status, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return (null, "invalid_answer");
            string? Text(string name, int max) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? AgentLabels.Printable(v.GetString(), max) is { Length: > 0 } s ? s : null : null;
            string? Multiline(string name, int max) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? AgentLabels.PrintableLines(v.GetString(), max) is { Length: > 0 } s ? s : null : null;
            bool Bool(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            if (status != 200)
            {
                var code = Text("error", 32);
                var safe = code is not null && code.All(c => c is >= 'a' and <= 'z' or '_') ? code : "agent_error";
                var output = Multiline("output", 4000);
                return (output is null ? null : new HostActionResult(false, false, false, null, false, null, null, output, null, null), safe);
            }
            string[]? lines = o.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array
                ? [.. l.EnumerateArray().Take(200).Where(x => x.ValueKind == JsonValueKind.String).Select(x => AgentLabels.Printable(x.GetString(), 400))] : null;
            int? count = o.TryGetProperty("count", out var c2) && c2.ValueKind == JsonValueKind.Number && c2.TryGetInt32(out var cn) ? Math.Clamp(cn, 0, 1_000_000) : null;
            bool? updated = o.TryGetProperty("updated", out var u) && u.ValueKind is JsonValueKind.True or JsonValueKind.False ? u.ValueKind == JsonValueKind.True : null;
            var name = Text("unit", 100) ?? Text("container", 64);
            return (new HostActionResult(true, Bool("accepted"), Bool("already"), updated, Bool("rebootRequired"), count, lines, Multiline("output", 4000), name, Text("image", 200)), null);
        }
        catch (JsonException) { return (null, "invalid_answer"); }
    }

    /// <summary>Nach einem Verbindungsabbruch: wartet, bis der Agent wieder da ist, und fragt seine letzten Auftraege ab (GET /v1/host/jobs). Er hat die Aktion zu Ende gefuehrt und das Ergebnis gemerkt.</summary>
    private async Task ReconcileAsync(HostRecord host, HostActionDef def, HostActionJob job)
    {
        for (var i = 0; i < options.ReconcileTries; i++)
        {
            await Task.Delay(options.ReconcileInterval);
            if (job.Final) return;
            if (await TryReconcileOnceAsync(host, def, job)) return;
        }
    }

    /// <summary>Ein Versuch, das Ergebnis eines verlorenen Auftrags beim Agenten abzuholen; true, wenn der Auftrag abgeschlossen ist (oder beim Agenten unbekannt, dann bleibt er "lost").</summary>
    public async Task<bool> TryReconcileOnceAsync(HostRecord host, HostActionDef def, HostActionJob job)
    {
        if (links.Get(job.HostId, job.UserId) is not { } link) return false;
        try
        {
            var reply = await link.RequestAsync(HttpMethod.Get, "/v1/host/jobs", null, TimeSpan.FromSeconds(15), CancellationToken.None);
            if (reply.Status != 200) return false;
            using var doc = JsonDocument.Parse(reply.Json);
            if (!doc.RootElement.TryGetProperty("jobs", out var list) || list.ValueKind != JsonValueKind.Array) return false;
            foreach (var j in list.EnumerateArray())
            {
                if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("job", out var id) || id.GetString() != job.Id) continue;
                var state = j.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (state == "running")
                {
                    lock (job)
                    {
                        job.State = "lost";
                        job.Progress = j.TryGetProperty("progress", out var pr) && pr.ValueKind == JsonValueKind.String ? AgentLabels.Printable(pr.GetString(), 160) : job.Progress;
                        job.Done = j.TryGetProperty("done", out var d) && d.TryGetInt32(out var dn) ? dn : job.Done;
                        job.Total = j.TryGetProperty("total", out var t) && t.TryGetInt32(out var tn) ? tn : job.Total;
                    }
                    return false;
                }
                var body = j.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r.GetRawText() : """{"error":"interrupted"}""";
                await FinishAsync(host, def, job, state is "done" or "accepted" ? 200 : 500, body);
                return true;
            }
            return false;
        }
        catch (Exception e) when (e is AgentUnavailableException or JsonException) { return false; }
    }

    // ---------------------------------------------------------------- Auftrag abfragen, Verlauf

    /// <summary>Der Auftrag, wenn er diesem Benutzer und diesem Host gehoert. Ein verlorener Auftrag versucht sofort, sein Ergebnis beim Agenten abzuholen.</summary>
    public async Task<HostActionJob?> GetJobAsync(SessionIdentity who, Guid hostId, string job, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(job, out var j) || j.UserId != who.UserId || j.HostId != hostId) return null;
        if (j.State == "lost" && await hosts.GetAsync(who.UserId, hostId, ct) is { } host && HostActions.Find(j.Action) is { } def) await TryReconcileOnceAsync(host, def, j);
        return j;
    }

    public async Task<IReadOnlyList<AuditEntry>?> HistoryAsync(Guid userId, Guid hostId, int limit, CancellationToken ct)
        => await hosts.GetAsync(userId, hostId, ct) is null ? null : await audit.ListAsync(userId, hostId, limit, ct);
}
