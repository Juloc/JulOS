using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Services;

namespace JulOS.Frontend.Auth;

internal static class AuthEndpoints
{
    private static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?";

    private static IResult Error(int status, string message, int retryAfter = 0) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message, retryAfter > 0 ? retryAfter : null), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

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

        app.MapPost("/api/auth/logout", async (HttpContext ctx, AuthService auth) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            await auth.LogoutAsync(id.Hash, ctx.RequestAborted);
            SessionCookie.Clear(ctx);
            return Results.NoContent();
        });

        app.MapPost("/api/auth/logout-others", async (HttpContext ctx, AuthService auth) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return new EndedResponse(await auth.LogoutOthersAsync(id.UserId, id.Hash, ctx.RequestAborted));
        });

        app.MapGet("/api/auth/sessions", async (HttpContext ctx, AuthService auth) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var list = await auth.ListSessionsAsync(id.UserId, id.Hash, ctx.RequestAborted);
            return new SessionsResponse([.. list.Select(s => new SessionDto(s.Current, s.CreatedAt, s.LastSeen, s.UserAgent, s.Ip))]);
        });
    }
}
