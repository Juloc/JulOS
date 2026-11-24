using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Services;

namespace JulOS.Frontend.Auth;

/// <summary>
/// Schuetzt alles unter /api: Aenderungen nur von der eigenen Seite (Sonderkopfzeile und passender Origin), alles ausser den
/// oeffentlichen Pfaden nur mit gueltiger Sitzung. Statische Dateien und das Manifest bleiben offen, damit die App installierbar ist.
/// </summary>
internal sealed class ApiGuard(RequestDelegate next, AuthService auth)
{
    public const string IdentityKey = "julos.identity";

    private static readonly HashSet<string> PublicPaths = new(StringComparer.Ordinal)
    {
        "/api/health", "/api/auth/me", "/api/auth/login", "/api/setup/status", "/api/setup/begin", "/api/setup/finish",
    };

    public const long MaxSmallBody = 64 * 1024;

    /// <summary>Die Wege, die grosse Koerper annehmen (Foto und Datei fuer die Zwischenablage, Upload der Dateien-App); jeder prueft seine Grenze selbst.</summary>
    private static readonly HashSet<string> LargeBodyPaths = new(StringComparer.Ordinal) { "/api/upload", "/api/upload-file", "/api/files/remote/upload" };

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(ctx); return; }

        // Der Agent auf der Ziel-Maschine ist kein Browser: er hat weder Cookie noch Origin. Seine Wege pruefen Code und Token selbst (Agent/AgentLinkEndpoints.cs).
        if (Agent.AgentLinkEndpoints.IsAgentPath(ctx.Request.Path)) { await next(ctx); return; }

        // Die KI-API der Apps der Familie hat ebenfalls kein Cookie: sie braucht den Schluessel der App (Authorization: Bearer), den das Tor pruefen muss (Ai/AiGateway.cs). Alles andere unter /api/ai bleibt hinter der Sitzung.
        if (Ai.AiEndpoints.IsAppPath(ctx.Request.Path)) { await next(ctx); return; }

        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            if (ctx.Request.Headers["X-JulOS"] != "1" || !SameOrigin(ctx.Request)) { await Reject(ctx, 403, "Anfrage abgelehnt."); return; }
            var small = ctx.Request.Path.StartsWithSegments("/api/auth") || ctx.Request.Path.StartsWithSegments("/api/setup");
            if (small && ctx.Request.ContentLength is null or > 4096) { await Reject(ctx, 413, "Anfrage zu groß."); return; }
            // Alles ausser den Uploads (die setzen ihre eigene Grenze) sind kleine JSON-Koerper; Kestrel erlaubt sonst 30 MB je Anfrage
            if (!LargeBodyPaths.Contains(ctx.Request.Path.Value ?? "") && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxSmallBody;
        }

        if (PublicPaths.Contains(ctx.Request.Path.Value ?? "")) { await next(ctx); return; }

        // Beim WebSocket-Aufbau gibt es keine Antwort, die ein neues Cookie tragen koennte
        var identity = await ResolveAsync(ctx, auth, rotate: !ctx.Request.Path.Equals(Remote.RemoteEndpoints.ScreenWebSocketPath));
        if (identity is null) { await Reject(ctx, 401, "Nicht angemeldet."); return; }
        ctx.Items[IdentityKey] = identity;
        // Aktionen, die Hosts, Dateien oder die Sicherheit aendern, brauchen eine frische Freigabe mit dem zweiten Faktor (Tabelle in StepUpPolicy, F-04)
        if (StepUpPolicy.Requires(ctx.Request.Method, ctx.Request.Path) && !await auth.StepUpFreshAsync(identity, ctx.RequestAborted))
        {
            await Reject(ctx, 403, "Bitte mit dem Code aus der Authenticator-App bestätigen.", StepUpPolicy.Code);
            return;
        }
        await next(ctx);
    }

    /// <summary>Liest das Cookie und prueft die Sitzung. Bei einer Erneuerung wird das neue Cookie gleich gesetzt.</summary>
    public static async Task<SessionIdentity?> ResolveAsync(HttpContext ctx, AuthService auth, bool rotate)
    {
        var token = SessionCookie.Read(ctx.Request);
        if (token is null) return null;
        var identity = await auth.ResolveSessionAsync(token, rotate, ctx.RequestAborted);
        if (identity?.NewToken is { } fresh) SessionCookie.Write(ctx, fresh);
        return identity;
    }

    public static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task Reject(HttpContext ctx, int status, string message, string? code = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ErrorBody(message, Code: code), AppJson.Default.ErrorBody), Encoding.UTF8);
    }
}
