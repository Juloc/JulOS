using System.Net;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Security;

internal sealed record ListDto(string[] Entries, string Source);
internal sealed record ClientDto(string Address, string Peer, bool Forwarded);
internal sealed record StepUpDto(bool Enabled, int Minutes);
internal sealed record RecoveryDto(string State, DateTimeOffset? CreatedAt, DateTimeOffset? ConfirmedAt, bool Replacing);
internal sealed record BackupKeyDto(bool CopyKey, string Source, bool Wrapped, bool CanProtect, RecoveryDto Recovery);
internal sealed record SecurityResponse(ListDto Proxies, ListDto Hosts, bool ProxiesUnset, ClientDto Client, StepUpDto StepUp, BackupKeyDto BackupKey);
internal sealed record ListRequest(string[]? Entries, bool? Confirm = null);
internal sealed record BlockedDto(string Name, string Host);
internal sealed record HostsPreviewResponse(BlockedDto[] Blocked);
internal sealed record SecurityError(string Error, string Code, BlockedDto[]? Blocked = null, string? Peer = null);
internal sealed record StepUpSettingRequest(bool? Enabled, int? Minutes);
internal sealed record BackupKeyRequest(bool? CopyKey);
internal sealed record RecoveryResponse(string Key);
internal sealed record SecurityAuditDto(long Id, DateTimeOffset At, string User, string Event, string? Detail);
internal sealed record SecurityAuditResponse(SecurityAuditDto[] Entries);

/// <summary>
/// Einstellungen, Sicherheit (docs/SECURITY-REVIEW.md, F-01 bis F-04): vertraute Proxy-Adressen, erlaubte Zielhosts, zweiter Faktor fuer Aktionen, Schluessel im Backup und Wiederherstellungsschluessel.
/// Alles hinter ApiGuard (Sitzung, Origin); jede Aenderung verlangt ausserdem eine frische Freigabe (StepUpPolicy), steht im Verlauf (security_audit, nie ein Geheimnis) und gilt ohne Neustart.
/// </summary>
internal static class SecurityEndpoints
{
    public const string PeerItem = "julos.peer";
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static IResult Error(int status, string message, string code = "invalid", BlockedDto[]? blocked = null, string? peer = null) =>
        Results.Text(JsonSerializer.Serialize(new SecurityError(message, code, blocked, peer), AppJson.Default.SecurityError), "application/json", Encoding.UTF8, status);

    /// <summary>Die Adresse, von der die Verbindung zum Server kam (der Proxy, falls einer davor steht), und die des Clients nach den X-Forwarded-Kopfzeilen.</summary>
    private static ClientDto Client(HttpContext ctx)
    {
        static string Show(IPAddress? a) => a is null ? "?" : (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();
        var client = ctx.Connection.RemoteIpAddress;
        var peer = ctx.Items[PeerItem] as IPAddress ?? client;
        var forwarded = peer is not null && client is not null && !Equals(peer, client);
        return new ClientDto(Show(client), Show(peer), forwarded);
    }

    private static string Join(IEnumerable<string> entries) => entries.Any() ? string.Join(", ", entries) : "(leer)";

    public static void MapSecurity(this IEndpointRouteBuilder app)
    {
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Security");

        async Task Audit(SecurityAuditStore audit, HttpContext ctx, string evt, string? detail = null)
        {
            var who = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            log.LogWarning("Sicherheit: {Event} von {User}{Detail}", evt, who.UserName, detail is null ? "" : ": " + detail);
            try { await audit.AddAsync(who.UserName, evt, detail, CancellationToken.None); }
            catch (Exception e) { log.LogError(e, "Verlauf der Sicherheit konnte nicht geschrieben werden"); }
        }

        bool Limited(HttpContext ctx, HitCounter hits) => !hits.Allow("security-write:" + ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId, 30, Minute);

        app.MapGet("/api/security", (HttpContext ctx, SecurityConfig cfg, BackupKeys keys, SecretBox box, BackupService backup) =>
        {
            var plan = keys.Plan();
            var r = keys.Recovery();
            return Results.Json(new SecurityResponse(new ListDto(cfg.Proxies.Entries, cfg.Proxies.Source), new ListDto(cfg.Hosts.Entries, cfg.Hosts.Source), cfg.ProxiesUnset, Client(ctx),
                new StepUpDto(cfg.StepUp.Enabled, cfg.StepUp.Minutes),
                new BackupKeyDto(plan.CopyKey, plan.Source, plan.Wrapped is not null, box.Enabled, new RecoveryDto(r.State, r.CreatedAt, r.ConfirmedAt, r.Replacing))),
                AppJson.Default.SecurityResponse);
        });

        app.MapGet("/api/security/audit", async (HttpContext ctx, SecurityAuditStore audit) =>
        {
            var limit = int.TryParse(ctx.Request.Query["limit"], out var n) ? n : 30;
            var list = await audit.ListAsync(limit, ctx.RequestAborted);
            return Results.Json(new SecurityAuditResponse([.. list.Select(e => new SecurityAuditDto(e.Id, e.At, e.UserName, e.Event, e.Detail))]), AppJson.Default.SecurityAuditResponse);
        });

        // ---- vertraute Proxy-Adressen (F-03) ----
        app.MapPut("/api/security/proxies", async (ListRequest body, HttpContext ctx, SecurityConfig cfg, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return Error(429, "Zu viele Anfragen, bitte kurz warten.", "rate_limited");
            var (entries, problem) = AddressList.Validate(body.Entries, names: false);
            if (entries is null) return Error(400, problem!);
            if (entries.Length == 0) return Error(400, "Mindestens ein Eintrag. Um die Liste zu leeren, „Auf Vorgabe zurücksetzen“ wählen.");
            // Kommt dieser Zugriff gerade ueber einen Proxy, der danach nicht mehr vertraut waere, stuende die Adresse des Proxys als Adresse des Clients im Log und in der Sperre: erst nach Bestaetigung
            var client = Client(ctx);
            if (client.Forwarded && !AddressList.ContainsAddress(entries, ctx.Items[PeerItem] as IPAddress) && body.Confirm != true)
                return Error(409, $"Dein Zugriff kommt gerade über {client.Peer}, und diese Adresse steht nicht in der Liste. Dann würden die X-Forwarded-Kopfzeilen nicht mehr geglaubt.", "proxy_not_included", peer: client.Peer);
            await cfg.SetProxiesAsync(entries, ctx.RequestAborted);
            await Audit(audit, ctx, "proxies.set", Join(entries));
            return Results.Json(new ListDto(cfg.Proxies.Entries, cfg.Proxies.Source), AppJson.Default.ListDto);
        });

        app.MapDelete("/api/security/proxies", async (HttpContext ctx, SecurityConfig cfg, SecurityAuditStore audit) =>
        {
            await cfg.ResetProxiesAsync(ctx.RequestAborted);
            await Audit(audit, ctx, "proxies.reset", $"jetzt: {cfg.Proxies.Source}");
            return Results.Json(new ListDto(cfg.Proxies.Entries, cfg.Proxies.Source), AppJson.Default.ListDto);
        });

        // ---- erlaubte Zielhosts (F-02) ----
        async Task<(string[]? Entries, BlockedDto[]? Blocked, IResult? Fail)> CheckHosts(ListRequest body, HttpContext ctx, ConnectionStore connections)
        {
            var (entries, problem) = AddressList.Validate(body.Entries, names: true);
            if (entries is null) return (null, null, Error(400, problem!));
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var blocked = (await connections.ListAsync(id.UserId, ctx.RequestAborted)).Where(c => !HostPolicy.Allows(entries, c.Host)).Select(c => new BlockedDto(c.Name, c.Host)).ToArray();
            return (entries, blocked, null);
        }

        // Vorschau ohne Wirkung: welche gespeicherten Verbindungen waeren danach gesperrt
        app.MapPost("/api/security/hosts/preview", async (ListRequest body, HttpContext ctx, ConnectionStore connections) =>
        {
            var (_, blocked, fail) = await CheckHosts(body, ctx, connections);
            return fail ?? Results.Json(new HostsPreviewResponse(blocked!), AppJson.Default.HostsPreviewResponse);
        });

        app.MapPut("/api/security/hosts", async (ListRequest body, HttpContext ctx, SecurityConfig cfg, ConnectionStore connections, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return Error(429, "Zu viele Anfragen, bitte kurz warten.", "rate_limited");
            var (entries, blocked, fail) = await CheckHosts(body, ctx, connections);
            if (fail is not null) return fail;
            if (blocked!.Length > 0 && body.Confirm != true)
                return Error(409, $"{blocked.Length} gespeicherte Verbindung(en) wären danach gesperrt.", "blocked_connections", blocked);
            await cfg.SetHostsAsync(entries!, ctx.RequestAborted);
            await Audit(audit, ctx, "hosts.set", $"{Join(entries!)}; gesperrte Verbindungen: {blocked.Length}");
            return Results.Json(new ListDto(cfg.Hosts.Entries, cfg.Hosts.Source), AppJson.Default.ListDto);
        });

        app.MapDelete("/api/security/hosts", async (HttpContext ctx, SecurityConfig cfg, SecurityAuditStore audit) =>
        {
            await cfg.ResetHostsAsync(ctx.RequestAborted);
            await Audit(audit, ctx, "hosts.reset", $"jetzt: {cfg.Hosts.Source}");
            return Results.Json(new ListDto(cfg.Hosts.Entries, cfg.Hosts.Source), AppJson.Default.ListDto);
        });

        // ---- zweiter Faktor fuer Aktionen (F-04) ----
        app.MapPut("/api/security/step-up", async (StepUpSettingRequest body, HttpContext ctx, SecurityConfig cfg, SessionStore sessions, TimeProvider clock, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return Error(429, "Zu viele Anfragen, bitte kurz warten.", "rate_limited");
            if (body.Enabled is not { } enabled || body.Minutes is not { } minutes) return Error(400, "Angaben fehlen.");
            if (SecurityConfig.ValidateStepUp(enabled, minutes) is { } problem) return Error(400, problem);
            await cfg.SetStepUpAsync(enabled, minutes, ctx.RequestAborted);
            await sessions.CapStepUpAsync(clock.GetUtcNow(), minutes, ctx.RequestAborted);        // eine kuerzere Dauer gilt sofort auch fuer Freigaben, die schon erteilt sind
            await Audit(audit, ctx, "step_up.set", enabled ? (minutes == 0 ? "immer fragen" : minutes + " min") : "aus");
            return Results.Json(new StepUpDto(cfg.StepUp.Enabled, cfg.StepUp.Minutes), AppJson.Default.StepUpDto);
        });

        // ---- Schluessel im Backup und Wiederherstellungsschluessel (F-01) ----
        app.MapPut("/api/security/backup-key", async (BackupKeyRequest body, HttpContext ctx, BackupKeys keys, BackupService backup, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return Error(429, "Zu viele Anfragen, bitte kurz warten.", "rate_limited");
            // CopyKey null: die Einstellung der App vergessen, dann gilt die Umgebungsvariable oder der Standard
            if (body.CopyKey is { } copy) await keys.SetCopyKeyAsync(copy, ctx.RequestAborted); else await keys.ResetCopyKeyAsync(ctx.RequestAborted);
            var plan = keys.Plan();
            await Audit(audit, ctx, "backup_key.set", $"Kopie neben dem Auszug: {(plan.CopyKey ? "an" : "aus")} ({plan.Source})");
            // Ausgeschaltet: alte Kopien sofort entfernen, nicht erst nach der naechsten nachtlichen Sicherung
            if (plan.RemoveCopies && backup.CanRun) _ = Task.Run(async () => { try { await backup.RunAsync(); } catch (Exception e) { log.LogWarning(e, "Sicherung nach dem Ausschalten der Schlüsselkopie fehlgeschlagen"); } });
            return Results.Json(new BackupKeyDto(plan.CopyKey, plan.Source, plan.Wrapped is not null, true, new RecoveryDto(keys.Recovery().State, keys.Recovery().CreatedAt, keys.Recovery().ConfirmedAt, keys.Recovery().Replacing)), AppJson.Default.BackupKeyDto);
        });

        // Erzeugt einen neuen Wiederherstellungsschluessel; der Text steht NUR in dieser Antwort, der Server behaelt ihn nicht
        app.MapPost("/api/security/recovery", async (HttpContext ctx, BackupKeys keys, SecretBox box, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return Error(429, "Zu viele Anfragen, bitte kurz warten.", "rate_limited");
            if (!box.Enabled) return Error(409, "Es gibt keinen Schlüssel für gespeicherte Passwörter, also ist auch nichts zu sichern.", "no_key");
            var text = await keys.NewRecoveryAsync(ctx.RequestAborted);
            await Audit(audit, ctx, "recovery.new");
            return Results.Json(new RecoveryResponse(text), AppJson.Default.RecoveryResponse);
        });

        app.MapPost("/api/security/recovery/confirm", async (HttpContext ctx, BackupKeys keys, BackupService backup, SecurityAuditStore audit) =>
        {
            if (!await keys.ConfirmRecoveryAsync(ctx.RequestAborted)) return Error(409, "Es wartet kein neuer Wiederherstellungsschlüssel.", "no_pending");
            await Audit(audit, ctx, "recovery.confirm");
            if (backup.CanRun) _ = Task.Run(async () => { try { await backup.RunAsync(); } catch (Exception e) { log.LogWarning(e, "Sicherung nach dem Bestätigen des Wiederherstellungsschlüssels fehlgeschlagen"); } });
            return Results.NoContent();
        });
    }
}
