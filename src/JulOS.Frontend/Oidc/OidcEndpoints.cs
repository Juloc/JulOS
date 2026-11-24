using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend.Auth;
using Microsoft.Extensions.Primitives;

namespace JulOS.Frontend.Oidc;

/// <summary>
/// Die oeffentlichen Wege der Anmelde-Zentrale (docs/SIGN-IN.md). Sie liegen ausserhalb von /api und damit ausserhalb des ApiGuard: das ist die begruendete Ausnahme von
/// "jeder Weg braucht eine Sitzung", denn eine App ohne Sitzung muss sie erreichen koennen. Jeder Weg schuetzt sich selbst: authorize durch die Sitzung im Cookie (und die exakte
/// Adresse der App), token durch Geheimnis, Code und PKCE, userinfo durch das Zugriffs-Token, end-session durch Hinweis oder Bestaetigung und gleichen Origin. Es gibt kein CORS:
/// ein Browser darf keinen dieser Wege von einer fremden Seite aus lesen.
/// </summary>
internal static class OidcEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static string Ip(HttpContext c) => ClientAddress.Key(c.Connection.RemoteIpAddress);

    private static IResult Json(Action<Utf8JsonWriter> write, int status = 200, string? cache = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); write(w); w.WriteEndObject(); }
        return new CachedJson(Encoding.UTF8.GetString(ms.ToArray()), status, cache);
    }

    /// <summary>JSON mit Status und Cache-Kopfzeile; Token-Antworten sind immer no-store.</summary>
    private sealed class CachedJson(string json, int status, string? cache) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers.CacheControl = cache ?? "no-store";
            if (cache is null) ctx.Response.Headers.Pragma = "no-cache";
            await ctx.Response.WriteAsync(json, Encoding.UTF8);
        }
    }

    private static IResult ProtocolError(string code, string text, int status) => Json(w => { w.WriteString("error", code); w.WriteString("error_description", text); }, status);

    /// <summary>Ein Parameter, der genau einmal vorkommt (mehrfach ist nach RFC 6749 ein Fehler: es wird nichts davon genommen).</summary>
    private static string? One(IEnumerable<KeyValuePair<string, StringValues>> source, string name)
    {
        foreach (var (k, v) in source) if (k == name) return v.Count == 1 ? v[0] : null;
        return null;
    }

    public static void MapOidc(this IEndpointRouteBuilder app)
    {
        // ---- Beschreibung und Schluessel (oeffentlich, nur lesen) ----
        app.MapGet("/.well-known/openid-configuration", (HttpContext ctx, OidcService oidc, OidcIssuer issuer, HitCounter hits) =>
        {
            if (!hits.Allow("oidc-discovery:" + Ip(ctx), 120, Minute)) return ProtocolError("temporarily_unavailable", "Zu viele Anfragen.", 429);
            if (!oidc.Ready) return ProtocolError("temporarily_unavailable", "Die Anmeldung ist nicht eingerichtet.", 503);
            var iss = issuer.Value!;
            return Json(w =>
            {
                w.WriteString("issuer", iss);
                w.WriteString("authorization_endpoint", iss + "/oidc/authorize");
                w.WriteString("token_endpoint", iss + "/oidc/token");
                w.WriteString("userinfo_endpoint", iss + "/oidc/userinfo");
                w.WriteString("jwks_uri", iss + "/oidc/jwks");
                w.WriteString("end_session_endpoint", iss + "/oidc/end-session");
                // Nur der Anzeigename, fuer den Knopf "Mit ... anmelden" der Apps (sie lesen ihn ohne Sitzung). Keine Version, kein Host: /api/about bleibt hinter der Sitzung.
                w.WriteString("julos_display_name", "JulOS");
                static void List(Utf8JsonWriter w, string name, params string[] values) { w.WriteStartArray(name); foreach (var v in values) w.WriteStringValue(v); w.WriteEndArray(); }
                List(w, "response_types_supported", "code");
                List(w, "response_modes_supported", "query");
                List(w, "grant_types_supported", "authorization_code");
                List(w, "subject_types_supported", "public");
                List(w, "id_token_signing_alg_values_supported", OidcTokens.Alg);
                List(w, "scopes_supported", OidcRules.Openid, OidcRules.Profile);
                List(w, "token_endpoint_auth_methods_supported", "client_secret_basic", "client_secret_post");
                List(w, "code_challenge_methods_supported", "S256");
                List(w, "claims_supported", "iss", "sub", "aud", "exp", "iat", "auth_time", "nonce", "sid", "amr", "name", "preferred_username", "role");
                w.WriteBoolean("backchannel_logout_supported", true);
                w.WriteBoolean("backchannel_logout_session_supported", true);
                w.WriteBoolean("authorization_response_iss_parameter_supported", true);
            }, cache: "public, max-age=300");
        });

        app.MapGet("/oidc/jwks", async (OidcService oidc, OidcKeys keys, CancellationToken ct) =>
        {
            if (!oidc.Ready) return ProtocolError("temporarily_unavailable", "Die Anmeldung ist nicht eingerichtet.", 503);
            IReadOnlyList<OidcPublicKey> list;
            try { list = await keys.PublicKeysAsync(ct); }
            catch (OidcKeysUnusableException) { return ProtocolError("temporarily_unavailable", "Die Anmeldung ist nicht eingerichtet.", 503); }
            // nur oeffentliche Teile: kty, crv, x, y. Kurze Zwischenspeicherung: nach einer Rotation sollen Apps den neuen Schluessel bald sehen.
            return Json(w =>
            {
                w.WriteStartArray("keys");
                foreach (var k in list)
                {
                    w.WriteStartObject();
                    w.WriteString("kty", "EC"); w.WriteString("crv", "P-256"); w.WriteString("x", k.X); w.WriteString("y", k.Y);
                    w.WriteString("use", "sig"); w.WriteString("alg", OidcTokens.Alg); w.WriteString("kid", k.Kid);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }, cache: "public, max-age=60");
        });

        // ---- authorize ----
        app.MapGet("/oidc/authorize", async (HttpContext ctx, OidcService oidc, AuthService auth, HitCounter hits) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var req = ctx.Request;
            if (!hits.Allow("oidc-authorize:" + Ip(ctx), 120, Minute)) return OidcPages.TooMany(req);
            var q = req.Query;
            var input = new OidcAuthorizeInput(One(q, "client_id"), One(q, "redirect_uri"), One(q, "response_type"), One(q, "scope"), One(q, "state"), One(q, "nonce"),
                One(q, "code_challenge"), One(q, "code_challenge_method"), One(q, "prompt"), One(q, "max_age"), One(q, "response_mode"));
            var who = await ApiGuard.ResolveAsync(ctx, auth, rotate: true);
            var outcome = await oidc.AuthorizeAsync(input, who, bounced: One(q, "jb") == "1", ctx.RequestAborted);
            switch (outcome)
            {
                case OidcAuthorizeOutcome.Issued c: return Results.Redirect(c.Location);
                case OidcAuthorizeOutcome.Error e:
                    return Results.Redirect(OidcRules.WithQuery(e.RedirectUri, ("error", e.Code), ("error_description", e.Description), ("state", e.State), ("iss", ctx.RequestServices.GetRequiredService<OidcIssuer>().Value)));
                case OidcAuthorizeOutcome.Bounce: return OidcPages.Bounce(req);
                case OidcAuthorizeOutcome.NeedLogin n: return OidcPages.Login(req, n.AppName, req.Path + req.QueryString, fresh: who is not null);
                case OidcAuthorizeOutcome.NoAccess n: return OidcPages.NoAccess(req, n.AppName);
                case OidcAuthorizeOutcome.Unavailable: return OidcPages.Unavailable(req);
                default: return OidcPages.BadRequest(req);
            }
        });

        // ---- token ----
        app.MapPost("/oidc/token", async (HttpContext ctx, OidcService oidc, LoginLimiter limiter, HitCounter hits) =>
        {
            var ip = Ip(ctx);
            var limitKey = "ip:oidc:" + ip;
            // Die Sperre nach falschen Anmeldungen trifft nur falsche: wer das richtige Geheimnis hat, kommt auch dann durch (sonst koennte, wer die Adresse einer App teilt, deren Austausch blockieren)
            var wait = limiter.Wait(limitKey);
            if (!hits.Allow("oidc-token:" + ip, 60, Minute)) return ProtocolError("temporarily_unavailable", "Zu viele Anfragen.", 429);
            if (!ctx.Request.HasFormContentType) return ProtocolError("invalid_request", "Erwartet application/x-www-form-urlencoded.", 400);
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 8 * 1024;
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(ctx.RequestAborted); }
            catch (Exception e) when (e is InvalidDataException or BadHttpRequestException) { return ProtocolError("invalid_request", "Anfrage zu groß oder ungültig.", 400); }

            // Anmeldung der App: client_secret_basic (Authorization: Basic, beide Teile URL-kodiert) oder client_secret_post; nie beides
            string? clientId = One(form, "client_id"), secret = One(form, "client_secret");
            var basic = false;
            if (ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } header)
            {
                if (secret is not null) return ProtocolError("invalid_request", "Nur ein Weg der Anmeldung der App.", 400);
                basic = true; clientId = secret = null;
                if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                        var i = pair.IndexOf(':');
                        if (i > 0) { clientId = Uri.UnescapeDataString(pair[..i].Replace('+', ' ')); secret = Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' ')); }
                    }
                    catch (Exception e) when (e is FormatException or UriFormatException) { /* bleibt leer: invalid_client */ }
                }
                if (One(form, "client_id") is { } bodyId && bodyId != clientId) return ProtocolError("invalid_request", "client_id stimmt nicht.", 400);
            }

            var r = await oidc.ExchangeAsync(clientId, secret, One(form, "grant_type"), One(form, "code"), One(form, "redirect_uri"), One(form, "code_verifier"), ctx.RequestAborted);
            if (r.Body is { } b)
            {
                limiter.Succeed(limitKey);
                return Json(w => { w.WriteString("access_token", b.AccessToken); w.WriteString("token_type", "Bearer"); w.WriteNumber("expires_in", b.ExpiresIn); w.WriteString("id_token", b.IdToken); w.WriteString("scope", b.Scope); });
            }
            // nur falsche Anmeldungen der App zaehlen (ein abgelaufener Code ist kein Angriff: einen 256-Bit-Code kann niemand raten)
            if (r.Error == "invalid_client")
            {
                limiter.Fail(limitKey);
                if (wait > TimeSpan.Zero)
                {
                    ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString();
                    return ProtocolError("temporarily_unavailable", "Zu viele Anfragen.", 429);
                }
            }
            if (r.Error == "invalid_client" && basic) ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"julos\", charset=\"UTF-8\"";
            return ProtocolError(r.Error!, r.Description!, r.Status);
        });

        // ---- userinfo ----
        async Task<IResult> UserInfo(HttpContext ctx, OidcService oidc, HitCounter hits)
        {
            if (!hits.Allow("oidc-userinfo:" + Ip(ctx), 120, Minute)) return ProtocolError("temporarily_unavailable", "Zu viele Anfragen.", 429);
            // das Token nur im Kopf Authorization: Bearer (nie in der Adresse: sie landet in Protokollen)
            var h = ctx.Request.Headers.Authorization.ToString();
            var token = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
            if (await oidc.UserInfoAsync(token, ctx.RequestAborted) is not { } u)
            {
                ctx.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                return Json(w => w.WriteString("error", "invalid_token"), 401);
            }
            return Json(w => { w.WriteString("sub", u.Sub); if (u.Name is not null) { w.WriteString("name", u.Name); w.WriteString("preferred_username", u.Name); } w.WriteString("role", u.Role); });
        }
        app.MapGet("/oidc/userinfo", UserInfo);
        app.MapPost("/oidc/userinfo", UserInfo);

        // ---- end-session (RP-initiated logout) ----
        async Task<IResult> EndSession(HttpContext ctx, OidcService oidc, AuthService auth, OidcFlowStore flow, OidcFormTokens formTokens, Files.SftpPool pool, HitCounter hits)
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var req = ctx.Request;
            if (!hits.Allow("oidc-end:" + Ip(ctx), 60, Minute)) return OidcPages.TooMany(req);
            var post = HttpMethods.IsPost(req.Method);
            // wer abmeldet, kommt von einer eigenen Seite: ein POST von einer fremden Seite wird nicht angenommen. Origin muss dieser Server sein; "null" (Chromium bei einem Formular
            // derselben Herkunft unter Referrer-Policy no-referrer) gilt NUR hier und nur mit Sec-Fetch-Site: same-origin. Dazu kommt unten das Einmal-Zeichen der Seite.
            if (post && !ApiGuard.SameOrigin(req) && !(req.Headers.Origin == "null" && req.Headers["Sec-Fetch-Site"] == "same-origin")) return OidcPages.BadRequest(req);
            IEnumerable<KeyValuePair<string, StringValues>> source;
            if (post)
            {
                if (!req.HasFormContentType) return OidcPages.BadRequest(req);
                if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 16 * 1024;
                try { source = await req.ReadFormAsync(ctx.RequestAborted); }
                catch (Exception e) when (e is InvalidDataException or BadHttpRequestException) { return OidcPages.BadRequest(req); }
            }
            else source = req.Query;

            var who = await ApiGuard.ResolveAsync(ctx, auth, rotate: true);
            if (!post && who is null && One(source, "jb") != "1") return OidcPages.Bounce(req);

            var hintText = One(source, "id_token_hint");
            var hint = await oidc.ReadHintAsync(hintText, ctx.RequestAborted);
            var state = One(source, "state");
            var wanted = One(source, "post_logout_redirect_uri");
            // Zurueck zur App nur mit gueltigem Hinweis und einer EXAKT eingetragenen Adresse (sonst ein offener Redirect)
            var target = hint is not null && wanted is not null && hint.App.PostLogoutUris.Contains(wanted, StringComparer.Ordinal) ? wanted : null;
            async Task<IResult> Done() => target is not null ? Results.Redirect(OidcRules.WithQuery(target, ("state", OidcRules.OpaqueOk(state) ? state : null)))
                : OidcPages.SignedOut(req, await oidc.BackLinkAsync(One(source, "client_id"), ctx.RequestAborted));

            if (who is null) return await Done();                      // nichts zu beenden
            if (post && !formTokens.Take(One(source, "form_token"), who.Id)) return OidcPages.BadRequest(req);   // das Formular dieser Sitzung, einmal

            // Ohne Bestaetigung nur, wenn der Hinweis zur Sitzung passt, die jetzt abgemeldet wird: seine "sid" ist die, die DIESE Sitzung dieser App gegeben hat (und die Person stimmt). Ein
            // altes oder fremdes Token derselben Person reicht nicht; dann fragt eine Seite nach (eine fremde Seite soll niemanden still abmelden koennen).
            var confirmed = post || (hint is { Sid: { Length: > 0 } hintSid } && Guid.TryParse(hint.Sub, out var sub) && await flow.SubOfAsync(who.UserId, ctx.RequestAborted) == sub
                && string.Equals(await flow.GrantSidAsync(who.Id, hint.App.Id, ctx.RequestAborted), hintSid, StringComparison.Ordinal));
            if (!confirmed)
            {
                var fields = new List<KeyValuePair<string, string>>();
                foreach (var name in new[] { "id_token_hint", "post_logout_redirect_uri", "state", "client_id" })
                    if (One(source, name) is { } v) fields.Add(new(name, v));
                return OidcPages.ConfirmLogout(req, fields, formTokens.Issue(who.Id));
            }

            await auth.LogoutAsync(who.Hash, ctx.RequestAborted);
            await pool.InvalidateUserAsync(who.UserId);          // wie beim Abmelden in JulOS: keine SFTP-Sitzung bleibt offen
            SessionCookie.Clear(ctx);
            await oidc.AuditAsync(who.UserName, "oidc.signout", hint?.App.Name);
            return await Done();                                       // das Logout-Token an alle Apps stellt der Hintergrunddienst zu (OidcLogoutWorker)
        }
        app.MapGet("/oidc/end-session", EndSession);
        app.MapPost("/oidc/end-session", EndSession);
    }
}
