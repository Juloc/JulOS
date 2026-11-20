using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;

namespace JulOS.Frontend.Auth;

internal static class AuthEndpoints
{
    private static string Ip(HttpContext c) => ClientAddress.Key(c.Connection.RemoteIpAddress);

    private static IResult Error(int status, string message, int retryAfter = 0) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message, retryAfter > 0 ? retryAfter : null), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static IResult Fail(HttpContext ctx, int status, string message, int retryAfter)
    {
        if (retryAfter > 0) ctx.Response.Headers.RetryAfter = retryAfter.ToString();
        return Error(status, message, retryAfter);
    }

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/setup/status", async (AuthService auth, CancellationToken ct) => new SetupStatus(await auth.SetupNeededAsync(ct)));

        app.MapPost("/api/setup/begin", async (SetupBeginRequest body, HttpContext ctx, AuthService auth) =>
        {
            var r = await auth.BeginSetupAsync(body.Code, body.User, body.Password, Ip(ctx), ctx.RequestAborted);
            return r.Ok ? Results.Json(new SetupBeginResponse(r.Data!.Secret, r.Data.Uri, r.Data.Recovery), AppJson.Default.SetupBeginResponse)
                        : Error(r.Status, r.Error!, r.RetryAfter);
        });

        app.MapPost("/api/setup/finish", async (SetupFinishRequest body, HttpContext ctx, AuthService auth) =>
        {
            var r = await auth.FinishSetupAsync(body.Code, body.User, body.Totp, Ip(ctx), ctx.Request.Headers.UserAgent, ctx.RequestAborted);
            if (!r.Ok) return Error(r.Status, r.Error!, r.RetryAfter);
            SessionCookie.Write(ctx, r.Token!);
            return Results.Json(new LoginResponse(r.User!, r.RecoveryLeft), AppJson.Default.LoginResponse);
        });

        app.MapGet("/api/auth/me", async (HttpContext ctx, AuthService auth) =>
        {
            var identity = await ApiGuard.ResolveAsync(ctx, auth, rotate: true);
            return new MeResponse(identity?.UserName, identity is null && await auth.SetupNeededAsync(ctx.RequestAborted));
        });

        app.MapPost("/api/auth/login", async (LoginRequest body, HttpContext ctx, AuthService auth) =>
        {
            var r = await auth.LoginAsync(body.User, body.Password, body.Code, Ip(ctx), ctx.Request.Headers.UserAgent, ctx.RequestAborted);
            if (!r.Ok)
            {
                if (r.RetryAfter > 0) ctx.Response.Headers.RetryAfter = r.RetryAfter.ToString();
                return Error(r.Status, r.Error!, r.RetryAfter);
            }
            SessionCookie.Write(ctx, r.Token!);
            return Results.Json(new LoginResponse(r.User!, r.RecoveryLeft), AppJson.Default.LoginResponse);
        });

        app.MapPost("/api/auth/logout", async (HttpContext ctx, AuthService auth, Files.SftpPool pool) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            await auth.LogoutAsync(id.Hash, ctx.RequestAborted);
            await pool.InvalidateUserAsync(id.UserId);          // die Dateien-App haelt nach dem Abmelden keine SFTP-Sitzung offen
            SessionCookie.Clear(ctx);
            return Results.NoContent();
        });

        app.MapPost("/api/auth/logout-others", async (HttpContext ctx, AuthService auth, Files.SftpPool pool) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var ended = await auth.LogoutOthersAsync(id.UserId, id.Hash, ctx.RequestAborted);
            await pool.InvalidateUserAsync(id.UserId);          // die anderen Geraete sind weg: ihre geoeffneten Orte auch
            return new EndedResponse(ended);
        });

        // Konto: Passwort aendern und Authenticator neu einrichten. Hinter ApiGuard (Sitzung, Origin, 4 KiB); das aktuelle Passwort wird jedes Mal verlangt.
        app.MapPost("/api/auth/password", async (PasswordChangeRequest body, HttpContext ctx, AuthService auth, Files.SftpPool pool) =>
        {
            var who = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var r = await auth.ChangePasswordAsync(who, body.Current, body.Next, Ip(ctx), ctx.RequestAborted);
            if (r.Ok) await pool.InvalidateUserAsync(who.UserId);          // andere Sitzungen enden mit dem neuen Passwort, ihre geoeffneten Orte auch
            return r.Ok ? Results.NoContent() : Fail(ctx, r.Status, r.Error!, r.RetryAfter);
        });

        app.MapPost("/api/auth/totp/begin", async (TotpBeginRequest body, HttpContext ctx, AuthService auth) =>
        {
            var r = await auth.BeginTotpChangeAsync((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!, body.Current, Ip(ctx), ctx.RequestAborted);
            return r.Ok ? Results.Json(new TotpBeginResponse(r.Data!.Secret, r.Data.Uri), AppJson.Default.TotpBeginResponse) : Fail(ctx, r.Status, r.Error!, r.RetryAfter);
        });

        app.MapPost("/api/auth/totp/finish", async (TotpFinishRequest body, HttpContext ctx, AuthService auth) =>
        {
            var r = await auth.FinishTotpChangeAsync((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!, body.Code, Ip(ctx), ctx.RequestAborted);
            return r.Ok ? Results.Json(new TotpFinishResponse(r.Recovery!), AppJson.Default.TotpFinishResponse) : Fail(ctx, r.Status, r.Error!, r.RetryAfter);
        });

        app.MapGet("/api/auth/sessions", async (HttpContext ctx, AuthService auth) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var list = await auth.ListSessionsAsync(id.UserId, id.Hash, ctx.RequestAborted);
            return new SessionsResponse([.. list.Select(s => new SessionDto(s.Current, s.CreatedAt, s.LastSeen, s.UserAgent, s.Ip))]);
        });
    }
}
