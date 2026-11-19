using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Hosts;

internal sealed record ActionPrepareRequest(string? Action, string? Target, int? Lines);
internal sealed record ActionRunRequest(string? Token, string? Action, string? Target, string? Typed);
internal sealed record ActionConfirmationResponse(string Token, int ExpiresIn, string Action, string? Target, string? TargetName, string Host, string? TypeToConfirm, string[] Warnings, bool ReadOnly);
internal sealed record ActionStarted(string Job, string State);
internal sealed record ActionResultDto(bool Ok, bool Accepted, bool Already, bool? Updated, bool RebootRequired, int? Count, string[]? Lines, string? Output, string? Name, string? Image);
internal sealed record ActionJobResponse(string Job, string Action, string? Target, string? TargetName, string State, string? Error, string? Progress, int? Done, int? Total, DateTimeOffset Started, DateTimeOffset? Finished, ActionResultDto? Result);
internal sealed record ActionError(string Error, string Code);
internal sealed record AuditEntryDto(long Id, DateTimeOffset At, string User, string Action, string? Target, string Event, string? Detail);
internal sealed record AuditResponse(AuditEntryDto[] Entries);
internal sealed record ControlRequest(string? Level);
internal sealed record ControlResponse(string Level);
internal sealed record PauseRequest(bool Paused);
internal sealed record PauseResponse(bool Paused);

/// <summary>
/// Aktionen auf Hosts (Stufe 3, docs/HOST-AGENT.md): die Bestaetigung (<c>prepare</c>), die Ausfuehrung (<c>run</c>), der Stand eines Auftrags, der Verlauf, die Stufe je Host und der Pausenschalter.
/// Alles braucht eine Sitzung (ApiGuard), ein fremder Host ist 404, und jede Antwort mit Fehler traegt einen kurzen <c>code</c>, den der Browser uebersetzt.
/// </summary>
internal static class HostActionEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Hoechstens so viele Anfragen (Bestaetigung und Ausfuehrung zusammen) je Benutzer und Minute.</summary>
    public const int PerMinute = 30;

    private static readonly Dictionary<string, string> Messages = new()
    {
        ["host_not_found"] = "Host nicht gefunden.", ["no_agent"] = "Kein Agent eingerichtet.", ["unknown_action"] = "Unbekannte Aktion.", ["invalid_target"] = "Das Ziel ist ungültig.",
        ["paused"] = "Aktionen sind pausiert.", ["control_off"] = "Aktionen sind für diesen Host nicht eingeschaltet.", ["offline"] = "Der Agent ist gerade nicht verbunden.",
        ["agent_outdated"] = "Der Agent ist zu alt für Aktionen: bitte aktualisieren.", ["unsupported"] = "Der Agent kann das auf diesem Rechner nicht.",
        ["agent_not_permitted"] = "Der Agent erlaubt diese Aktion auf dem Rechner nicht.", ["busy"] = "Auf diesem Host läuft schon eine Aktion.", ["unknown_target"] = "Das Ziel steht nicht in der Liste des Agenten.",
        ["list_unavailable"] = "Die Liste des Agenten ist gerade nicht lesbar.", ["protected_unit"] = "Der Agent und sein Helfer lassen sich nicht über sich selbst stoppen.", ["too_many"] = "Zu viele offene Bestätigungen.",
        ["confirmation_invalid"] = "Die Bestätigung gilt nicht (mehr).", ["confirmation_expired"] = "Die Bestätigung ist abgelaufen.", ["confirmation_mismatch"] = "Die Bestätigung gehört zu etwas anderem.",
        ["typed_mismatch"] = "Der getippte Name stimmt nicht.", ["audit_failed"] = "Der Verlauf ließ sich nicht schreiben, die Aktion wurde nicht ausgeführt.", ["invalid_level"] = "Ungültige Stufe.",
        ["rate_limited"] = "Zu viele Anfragen, bitte kurz warten.", ["job_not_found"] = "Auftrag nicht gefunden.",
    };

    private static IResult Fail(ActionDenied d) => Fail(d.Status, d.Code);

    private static IResult Fail(int status, string code) =>
        Results.Text(JsonSerializer.Serialize(new ActionError(Messages.GetValueOrDefault(code, "Abgelehnt."), code), AppJson.Default.ActionError), "application/json", Encoding.UTF8, status);

    private static ActionJobResponse Describe(HostActionJob j)
    {
        lock (j)
            return new ActionJobResponse(j.Id, j.Action, j.Target, j.TargetName, j.State, j.Error, j.Progress, j.Done, j.Total, j.Started, j.Finished,
                j.Result is { } r ? new ActionResultDto(r.Ok, r.Accepted, r.Already, r.Updated, r.RebootRequired, r.Count, r.Lines, r.Output, r.Name, r.Image) : null);
    }

    public static void MapHostActions(this IEndpointRouteBuilder app)
    {
        // Schritt 1: pruefen und die Bestaetigung nennen (Host, Aktion, Ziel); noch geschieht nichts auf dem Host
        app.MapPost("/api/hosts/{host:guid}/actions/prepare", async (Guid host, ActionPrepareRequest body, HttpContext ctx, HostActionService svc, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("host-action:" + id.UserId, PerMinute, Minute)) return Fail(429, "rate_limited");
            var (value, denied) = await svc.PrepareAsync(id, host, body.Action, body.Target, body.Lines, ctx.RequestAborted);
            return value is null ? Fail(denied!) : Results.Json(new ActionConfirmationResponse(value.Token, value.ExpiresIn, value.Action, value.Target, value.TargetName, value.Host, value.TypeToConfirm, value.Warnings, value.ReadOnly), AppJson.Default.ActionConfirmationResponse);
        });

        // Schritt 2: mit der Bestaetigung ausfuehren. Antwort sofort (202), der Fortschritt kommt ueber den Auftrag
        app.MapPost("/api/hosts/{host:guid}/actions/run", async (Guid host, ActionRunRequest body, HttpContext ctx, HostActionService svc, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("host-action:" + id.UserId, PerMinute, Minute)) return Fail(429, "rate_limited");
            var (job, denied) = await svc.RunAsync(id, host, body.Token, body.Action, body.Target, body.Typed, ctx.RequestAborted);
            return job is null ? Fail(denied!) : Results.Json(new ActionStarted(job.Id, job.State), AppJson.Default.ActionStarted, statusCode: 202);
        });

        app.MapGet("/api/hosts/{host:guid}/actions/{job}", async (Guid host, string job, HttpContext ctx, HostActionService svc) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return await svc.GetJobAsync(id, host, job, ctx.RequestAborted) is { } j ? Results.Json(Describe(j), AppJson.Default.ActionJobResponse) : Fail(404, "job_not_found");
        });

        // Der Verlauf: wer, was, wann, mit welchem Ergebnis (nur kurze Codes, kein Inhalt)
        app.MapGet("/api/hosts/{host:guid}/audit", async (Guid host, HttpContext ctx, HostActionService svc, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("host-audit:" + id.UserId, 60, Minute)) return Fail(429, "rate_limited");
            var limit = int.TryParse(ctx.Request.Query["limit"], out var n) ? n : 50;
            var list = await svc.HistoryAsync(id.UserId, host, limit, ctx.RequestAborted);
            return list is null ? Fail(404, "host_not_found") : Results.Json(new AuditResponse([.. list.Select(e => new AuditEntryDto(e.Id, e.At, e.UserName, e.Action, e.Target, e.Event, e.Detail))]), AppJson.Default.AuditResponse);
        });

        // Die Dienste des Hosts (Ziele fuer Start/Stopp/Neustart/Logs): nur wenn der Server Dienste fuer diesen Host erlaubt
        app.MapGet("/api/hosts/{host:guid}/units", async (Guid host, HttpContext ctx, HostInfoService info, AgentStore agents, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("host-info:" + id.UserId, 90, Minute)) return Fail(429, "rate_limited");
            if (await agents.GetAsync(id.UserId, host, ctx.RequestAborted) is not { } agent) return Fail(404, "no_agent");
            if (!AgentControl.Allows(agent.Control, "services")) return Fail(403, "control_off");
            var r = await info.GetAsync(id.UserId, host, HostInfoEndpoints.Units, ctx.Request.Query["fresh"] == "1", ctx.RequestAborted);
            return r is null ? Fail(404, "host_not_found") : Results.Json(new HostUnitsResponse(r.State, r.At, r.Stale, r.Error, r.Data), AppJson.Default.HostUnitsResponse);
        });

        // Was der Server diesem Agenten auftragen darf (aus, Updates, voll); der Standard ist "aus" (read)
        app.MapPut("/api/hosts/{host:guid}/agent/control", async (Guid host, ControlRequest body, HttpContext ctx, HostActionService svc) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return await svc.SetControlAsync(id.UserId, host, body.Level, ctx.RequestAborted) is { } denied ? Fail(denied) : Results.Json(new ControlResponse(body.Level!), AppJson.Default.ControlResponse);
        });

        // Pausenschalter: pausiert alle Aktionen dieses Benutzers, egal was sonst erlaubt ist
        app.MapPut("/api/host-actions/pause", async (PauseRequest body, HttpContext ctx, HostActionService svc) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            await svc.SetPausedAsync(id.UserId, body.Paused, ctx.RequestAborted);
            return Results.Json(new PauseResponse(await svc.IsPausedAsync(id.UserId, ctx.RequestAborted)), AppJson.Default.PauseResponse);
        });
    }
}
