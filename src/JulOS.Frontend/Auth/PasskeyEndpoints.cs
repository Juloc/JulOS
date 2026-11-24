using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;

namespace JulOS.Frontend.Auth;

/// <summary>
/// Passkeys (docs/PASSKEYS.md). Nur die zwei Wege der Anmeldung (login/begin und login/finish) laufen ohne Sitzung, wie die Anmeldung mit Passwort: sie schuetzen sich selbst
/// (Herausforderung, Ratenlimit, Sperre, einheitlicher Fehler). Alles andere liegt hinter ApiGuard. Registrieren und Entfernen verlangen zusaetzlich die frische Freigabe (StepUpPolicy).
/// </summary>
internal static class PasskeyEndpoints
{
    /// <summary>Die Antwort eines Browsers ist ein paar hundert Byte bis wenige KiB gross; mehr als dies wird nicht gelesen.</summary>
    public const int MaxBody = 16 * 1024;

    private static string Ip(HttpContext c) => ClientAddress.Key(c.Connection.RemoteIpAddress);

    private static IResult Refuse(HttpContext ctx, PasskeyResult r)
    {
        if (r.RetryAfter > 0) ctx.Response.Headers.RetryAfter = r.RetryAfter.ToString();
        return Results.Text(JsonSerializer.Serialize(new ErrorBody(r.Error!, r.RetryAfter > 0 ? r.RetryAfter : null), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, r.Status);
    }

    private static IResult Options(PasskeyResult r, HttpContext ctx) => r.Ok ? Results.Text(r.Json!, "application/json", Encoding.UTF8) : Refuse(ctx, r);

    /// <summary>Liest {"name": ..., "credential": {...}} und gibt den Namen und den Text von "credential" (null: unbrauchbar).</summary>
    private static async Task<(string? Name, string? Credential)> ReadAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("credential", out var c) || c.ValueKind != JsonValueKind.Object) return (null, null);
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return (name, c.GetRawText());
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or InvalidOperationException) { return (null, null); }
    }

    public static void MapPasskeys(this IEndpointRouteBuilder app)
    {
        // ---- Anmelden (ohne Sitzung) ----
        app.MapPost("/api/auth/passkey/login/begin", async (HttpContext ctx, PasskeyService pk) => Options(await pk.BeginLoginAsync(Ip(ctx), ctx.RequestAborted), ctx));

        app.MapPost("/api/auth/passkey/login/finish", async (HttpContext ctx, PasskeyService pk) =>
        {
            var (_, credential) = await ReadAsync(ctx);
            var r = credential is null ? new PasskeyResult(false, 400, "Eingabe ungültig.") : await pk.FinishLoginAsync(credential, Ip(ctx), ctx.Request.Headers.UserAgent, ctx.RequestAborted);
            if (!r.Ok) return Refuse(ctx, r);
            SessionCookie.Write(ctx, r.Login!.Token!);
            return Results.Json(new LoginResponse(r.Login.User!, r.Login.RecoveryLeft), AppJson.Default.LoginResponse);
        });

        // ---- Liste und Verwalten (Sitzung) ----
        app.MapGet("/api/auth/passkeys", async (HttpContext ctx, PasskeyService pk) =>
        {
            var who = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var list = await pk.ListAsync(who, ctx.RequestAborted);
            var site = pk.Site();
            return Results.Json(new PasskeysResponse([.. list.Select(p => new PasskeyDto(p.Id, p.Name, p.Aaguid, p.BackupEligible, p.BackedUp, p.CreatedAt, p.LastUsedAt))], site is not null, PasskeyRules.MaxPerUser, site?.RpId), AppJson.Default.PasskeysResponse);
        });

        // Registrieren: begin verlangt die frische Freigabe (StepUpPolicy, auch bei ausgeschaltetem Schalter); finish ist an die Herausforderung gebunden, die begin an diese Sitzung ausgegeben hat
        app.MapPost("/api/auth/passkeys/register/begin", async (HttpContext ctx, PasskeyService pk) =>
            Options(await pk.BeginRegistrationAsync((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!, Ip(ctx), ctx.RequestAborted), ctx));

        app.MapPost("/api/auth/passkeys/register/finish", async (HttpContext ctx, PasskeyService pk) =>
        {
            var (name, credential) = await ReadAsync(ctx);
            var r = credential is null ? new PasskeyResult(false, 400, "Eingabe ungültig.")
                : await pk.FinishRegistrationAsync((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!, name, credential, Ip(ctx), ctx.RequestAborted);
            if (!r.Ok) return Refuse(ctx, r);
            var p = r.Item!;
            return Results.Json(new PasskeyDto(p.Id, p.Name, p.Aaguid, p.BackupEligible, p.BackedUp, p.CreatedAt, p.LastUsedAt), AppJson.Default.PasskeyDto);
        });

        app.MapPut("/api/auth/passkeys/{id:guid}", async (Guid id, PasskeyNameRequest body, HttpContext ctx, PasskeyService pk) =>
        {
            var r = await pk.RenameAsync((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!, id, body.Name, ctx.RequestAborted);
            return r.Ok ? Results.NoContent() : Refuse(ctx, r);
        });

        app.MapDelete("/api/auth/passkeys/{id:guid}", async (Guid id, HttpContext ctx, PasskeyService pk) =>
        {
            var r = await pk.DeleteAsync((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!, id, ctx.RequestAborted);
            return r.Ok ? Results.NoContent() : Refuse(ctx, r);
        });

        // ---- Freigabe fuer Aktionen mit Passkey statt Code ----
        app.MapPost("/api/auth/step-up/passkey/begin", async (HttpContext ctx, PasskeyService pk) =>
            Options(await pk.BeginStepUpAsync((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!, Ip(ctx), ctx.RequestAborted), ctx));

        app.MapPost("/api/auth/step-up/passkey/finish", async (HttpContext ctx, PasskeyService pk) =>
        {
            var (_, credential) = await ReadAsync(ctx);
            var r = credential is null ? new PasskeyResult(false, 400, "Eingabe ungültig.")
                : await pk.FinishStepUpAsync((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!, credential, Ip(ctx), ctx.RequestAborted);
            return r.Ok ? Results.Json(new StepUpResponse(r.StepUp!.Until, r.StepUp.Minutes), AppJson.Default.StepUpResponse) : Refuse(ctx, r);
        });
    }
}
