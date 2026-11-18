namespace JulOS.Frontend.Auth;

/// <summary>
/// Sitzungs-Cookie: HttpOnly, SameSite=Strict, ueber HTTPS mit Praefix __Host- (nur Secure, nur diese Adresse, kein Domain-Cookie).
/// </summary>
internal static class SessionCookie
{
    public const int Days = 30;

    private static string Name(bool https) => https ? "__Host-julos_sid" : "julos_sid";

    public static string? Read(HttpRequest request) => request.Cookies.TryGetValue(Name(request.IsHttps), out var v) ? v : null;

    public static void Write(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(Name(ctx.Request.IsHttps), token, Options(ctx, TimeSpan.FromDays(Days)));

    public static void Clear(HttpContext ctx) => ctx.Response.Cookies.Append(Name(ctx.Request.IsHttps), "", Options(ctx, TimeSpan.Zero));

    private static CookieOptions Options(HttpContext ctx, TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        Secure = ctx.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = maxAge,
        IsEssential = true,
    };
}
