using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Oidc;

/// <summary>
/// Verwaltung der Anmelde-Zentrale in Einstellungen, Sicherheit: Apps, Geheimnisse, Sperren, Rollen, Schluessel, oeffentliche Adresse. Alles unter /api/security/oidc: hinter ApiGuard
/// (Sitzung, eigener Origin) und, fuer alles ausser Lesen, mit der Freigabe durch den zweiten Faktor (StepUpPolicy gilt fuer api/security/**). Nur der Eigentuemer (das aelteste Konto)
/// darf das: eine Person mit einer Rolle in einer App verwaltet sie nicht. Jede Aenderung steht im Verlauf der Sicherheit (nie ein Geheimnis).
/// </summary>
internal static class OidcAdminEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    // Der Code (code) ist der stabile Schluessel fuer die Oberflaeche; der Text ist deutsch und nur der Rueckfall
    private static IResult Refuse(int status, string code, string text) => Results.Json(new OidcError(text, code), OidcJson.Default.OidcError, statusCode: status);

    private static OidcAppDto ToDto(OidcApp a, IReadOnlyDictionary<Guid, int> sessions) =>
        new(a.Id, a.Name, a.ClientId, a.RedirectUris, a.PostLogoutUris, a.BackchannelUri, a.Roles, a.Enabled, a.CreatedAt, a.SecretChangedAt, sessions.GetValueOrDefault(a.Id));

    public static void MapOidcAdmin(this IEndpointRouteBuilder app)
    {
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Oidc");

        async Task Audit(OidcService oidc, HttpContext ctx, string evt, string? detail = null)
        {
            var who = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            log.LogWarning("Anmelde-Zentrale: {Event} von {User}{Detail}", evt, who.UserName, detail is null ? "" : ": " + detail);
            await oidc.AuditAsync(who.UserName, evt, detail);
        }

        async Task<IResult?> Gate(HttpContext ctx, OidcAppStore apps, HitCounter hits, bool write)
        {
            var who = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (await apps.OwnerIdAsync(ctx.RequestAborted) != who.UserId) return Refuse(403, "owner_only", "Nur der Eigentümer verwaltet die Anmelde-Zentrale.");
            if (write && !hits.Allow("security-write:" + who.UserId, 30, Minute)) return Refuse(429, "rate_limited", "Zu viele Anfragen, bitte kurz warten.");
            return null;
        }

        // ---- Stand ----
        app.MapGet("/api/security/oidc", async (HttpContext ctx, OidcAppStore apps, OidcFlowStore flow, OidcKeys keys, OidcIssuer issuer, OidcService oidc, SecurityConfig cfg, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: false) is { } deny) return deny;
            var ct = ctx.RequestAborted;
            var sessions = await flow.GrantCountsAsync(ct);
            var req = ctx.Request;
            return Results.Json(new OidcOverview(oidc.Ready, keys.Available, issuer.Value, issuer.Source, $"{req.Scheme}://{req.Host}", ConfirmPolicy.Name(cfg.ConfirmMode),
                [.. (await apps.ListAsync(ct)).Select(a => ToDto(a, sessions))],
                [.. (await apps.PeopleAsync(ct)).Select(p => new OidcPersonDto(p.Id, p.Name, p.Owner))],
                [.. (await apps.AssignmentsAsync(ct)).Select(a => new OidcAssignmentDto(a.AppId, a.UserId, a.Role))],
                [.. (await keys.ListAsync(ct)).Select(k => new OidcKeyDto(k.Kid, k.CreatedAt, k.RetiredAt))]), OidcJson.Default.OidcOverview);
        });

        // ---- oeffentliche Adresse (der Aussteller) ----
        app.MapPut("/api/security/oidc/issuer", async (OidcIssuerRequest body, HttpContext ctx, OidcAppStore apps, OidcIssuer issuer, OidcService oidc, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            var (value, error) = OidcRules.CheckIssuer(body.Issuer);
            if (value is null) return Refuse(400, "invalid", error!);
            await issuer.SetAsync(value, ctx.RequestAborted);
            await Audit(oidc, ctx, "oidc.issuer.set", value);
            return Results.Json(new OidcIssuerResponse(issuer.Value, issuer.Source), OidcJson.Default.OidcIssuerResponse);
        });

        app.MapDelete("/api/security/oidc/issuer", async (HttpContext ctx, OidcAppStore apps, OidcIssuer issuer, OidcService oidc, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            await issuer.ResetAsync(ctx.RequestAborted);
            await Audit(oidc, ctx, "oidc.issuer.reset", $"jetzt: {issuer.Source}");
            return Results.Json(new OidcIssuerResponse(issuer.Value, issuer.Source), OidcJson.Default.OidcIssuerResponse);
        });

        // ---- Apps ----
        IResult? Validate(OidcAppRequest b, out string name, out string[] redirects, out string[] postLogout, out string? back, out string[] roles)
        {
            name = (b.Name ?? "").Trim(); redirects = []; postLogout = []; back = null; roles = [];
            if (OidcRules.NameProblem(b.Name) is { } np) return Refuse(400, "invalid_name", np);
            var (r, e1) = OidcRules.CheckUris(b.RedirectUris, required: true, max: 10, "Rückkehr-Adressen");
            if (r is null) return Refuse(400, "invalid_redirect", e1!);
            var (p, e2) = OidcRules.CheckUris(b.PostLogoutUris, required: false, max: 10, "Adressen nach dem Abmelden");
            if (p is null) return Refuse(400, "invalid_post_logout", e2!);
            if (!string.IsNullOrWhiteSpace(b.BackchannelUri))
            {
                back = b.BackchannelUri.Trim();
                if (OidcRules.UriProblem(back) is { } bp) return Refuse(400, "invalid_backchannel", "Adresse für das Abmelden: " + bp);
            }
            var rolesList = (b.Roles ?? []).Select(x => x?.Trim() ?? "").ToArray();
            if (OidcRules.RolesProblem(rolesList) is { } rp) return Refuse(400, "invalid_roles", rp);
            redirects = r; postLogout = p; roles = rolesList;
            return null;
        }

        app.MapPost("/api/security/oidc/apps", async (OidcAppRequest body, HttpContext ctx, OidcAppStore apps, OidcService oidc, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            if (Validate(body, out var name, out var redirects, out var postLogout, out var back, out var roles) is { } bad) return bad;
            if (await apps.CreateAsync(name, redirects, postLogout, back, roles, ctx.RequestAborted) is not { } made) return Refuse(409, "name_taken", "Eine App mit diesem Namen gibt es schon.");
            await Audit(oidc, ctx, "oidc.app.create", name);
            return Results.Json(new OidcCreated(ToDto(made.App, new Dictionary<Guid, int>()), made.Secret), OidcJson.Default.OidcCreated);
        });

        app.MapPut("/api/security/oidc/apps/{id:guid}", async (Guid id, OidcAppRequest body, HttpContext ctx, OidcAppStore apps, OidcFlowStore flow, OidcService oidc, TimeProvider clock, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            if (Validate(body, out var name, out var redirects, out var postLogout, out var back, out var roles) is { } bad) return bad;
            if (await apps.UpdateAsync(id, name, redirects, postLogout, back, roles, ctx.RequestAborted) is not { } done)
                return await apps.FindAsync(id, ctx.RequestAborted) is null ? Refuse(404, "not_found", "App nicht gefunden.") : Refuse(409, "name_taken", "Eine App mit diesem Namen gibt es schon.");
            // wer seine Rolle verloren hat, ist in dieser App abgemeldet
            foreach (var user in done.Dropped) await flow.EnqueueLogoutsAsync(id, user, "role_removed", clock.GetUtcNow(), ctx.RequestAborted);
            await Audit(oidc, ctx, "oidc.app.update", done.Dropped.Count > 0 ? $"{name} ({done.Dropped.Count} Zuweisung(en) entfallen)" : name);
            return Results.Json(ToDto(done.App, await flow.GrantCountsAsync(ctx.RequestAborted)), OidcJson.Default.OidcAppDto);
        });

        app.MapPost("/api/security/oidc/apps/{id:guid}/secret", async (Guid id, HttpContext ctx, OidcAppStore apps, OidcService oidc, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            if (await apps.FindAsync(id, ctx.RequestAborted) is not { } a || await apps.NewSecretAsync(id, ctx.RequestAborted) is not { } secret) return Refuse(404, "not_found", "App nicht gefunden.");
            await Audit(oidc, ctx, "oidc.app.secret", a.Name);
            return Results.Json(new OidcSecretResponse(secret), OidcJson.Default.OidcSecretResponse);
        });

        app.MapPut("/api/security/oidc/apps/{id:guid}/enabled", async (Guid id, OidcEnabledRequest body, HttpContext ctx, OidcAppStore apps, OidcFlowStore flow, OidcService oidc, TimeProvider clock, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            if (body.Enabled is not { } enabled) return Refuse(400, "invalid", "enabled fehlt.");
            if (await apps.FindAsync(id, ctx.RequestAborted) is not { } a || !await apps.SetEnabledAsync(id, enabled, ctx.RequestAborted)) return Refuse(404, "not_found", "App nicht gefunden.");
            // eine gesperrte App bekommt kein Token mehr, und ihre laufenden Sitzungen werden beendet
            if (!enabled) await flow.EnqueueLogoutsAsync(id, null, "app_disabled", clock.GetUtcNow(), ctx.RequestAborted);
            await Audit(oidc, ctx, enabled ? "oidc.app.enable" : "oidc.app.disable", a.Name);
            return Results.Json(ToDto((await apps.FindAsync(id, ctx.RequestAborted))!, await flow.GrantCountsAsync(ctx.RequestAborted)), OidcJson.Default.OidcAppDto);
        });

        app.MapDelete("/api/security/oidc/apps/{id:guid}", async (Guid id, HttpContext ctx, OidcAppStore apps, OidcService oidc, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            if (await apps.FindAsync(id, ctx.RequestAborted) is not { } a || !await apps.DeleteAsync(id, ctx.RequestAborted)) return Refuse(404, "not_found", "App nicht gefunden.");
            await Audit(oidc, ctx, "oidc.app.delete", a.Name);
            return Results.NoContent();
        });

        // ---- Rollen ----
        app.MapPut("/api/security/oidc/apps/{id:guid}/roles/{userId:guid}", async (Guid id, Guid userId, OidcRoleRequest body, HttpContext ctx, OidcAppStore apps, OidcFlowStore flow, OidcService oidc, TimeProvider clock, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            if (await apps.FindAsync(id, ctx.RequestAborted) is not { } a) return Refuse(404, "not_found", "App nicht gefunden.");
            if (!OidcRules.RoleOk(body.Role) || !a.Roles.Contains(body.Role!)) return Refuse(400, "invalid_role", "Diese Rolle kennt die App nicht.");
            var people = await apps.PeopleAsync(ctx.RequestAborted);
            if (people.FirstOrDefault(p => p.Id == userId) is not { } person) return Refuse(404, "not_found", "Person nicht gefunden.");
            if (person.Owner) return Refuse(400, "owner", "Der Eigentümer hat in jeder App admin.");
            if (await apps.SetRoleAsync(id, userId, body.Role, ctx.RequestAborted))
            {
                // eine geaenderte Rolle gilt erst mit einer neuen Anmeldung in der App: die bisherige Sitzung dieser Person dort endet (bei einer neuen Zuweisung gibt es keine)
                await flow.EnqueueLogoutsAsync(id, userId, "role_changed", clock.GetUtcNow(), ctx.RequestAborted);
                await Audit(oidc, ctx, "oidc.role.set", $"{a.Name}: {person.Name} = {body.Role}");
            }
            return Results.NoContent();
        });

        app.MapDelete("/api/security/oidc/apps/{id:guid}/roles/{userId:guid}", async (Guid id, Guid userId, HttpContext ctx, OidcAppStore apps, OidcFlowStore flow, OidcService oidc, TimeProvider clock, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            if (await apps.FindAsync(id, ctx.RequestAborted) is not { } a) return Refuse(404, "not_found", "App nicht gefunden.");
            var person = (await apps.PeopleAsync(ctx.RequestAborted)).FirstOrDefault(p => p.Id == userId);
            if (person is null) return Refuse(404, "not_found", "Person nicht gefunden.");
            if (person.Owner) return Refuse(400, "owner", "Der Eigentümer hat in jeder App admin.");
            if (await apps.SetRoleAsync(id, userId, null, ctx.RequestAborted))
            {
                await flow.EnqueueLogoutsAsync(id, userId, "role_removed", clock.GetUtcNow(), ctx.RequestAborted);       // kein Zugang mehr: die Sitzungen dieser Person in der App enden
                await Audit(oidc, ctx, "oidc.role.remove", $"{a.Name}: {person.Name}");
            }
            return Results.NoContent();
        });

        // ---- Schluessel ----
        app.MapPost("/api/security/oidc/keys/rotate", async (HttpContext ctx, OidcAppStore apps, OidcKeys keys, OidcService oidc, HitCounter hits) =>
        {
            if (await Gate(ctx, apps, hits, write: true) is { } deny) return deny;
            if (!keys.Available) return Refuse(409, "no_key_store", "Ohne Server-Schlüssel gibt es keine Anmelde-Zentrale.");
            var kid = await keys.RotateAsync(ctx.RequestAborted);
            await Audit(oidc, ctx, "oidc.key.rotate", kid);
            return Results.Json(new OidcKeyRotated(kid), OidcJson.Default.OidcKeyRotated);
        });
    }
}
