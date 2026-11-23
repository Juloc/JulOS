using System.Net;
using System.Text.Json;
using JulOS.Backend.Logic;
using Microsoft.IdentityModel.JsonWebTokens;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Der Ablauf der Anmelde-Zentrale (authorize -> Code -> token) mit echter Middleware und Datenbank, und was er alles ablehnt (docs/SIGN-IN.md).</summary>
public sealed class OidcFlowTests
{
    private static async Task<string> Body(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    [Fact]
    public async Task The_whole_flow_gives_a_signed_id_token_that_a_client_can_check_and_the_rest_of_the_surface_is_as_documented()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();

        // Beschreibung und Schluessel: nur Code-Ablauf, nur S256, nur ES256, nur oeffentliche Teile
        var disc = await Json(await rig.Http.GetAsync("/.well-known/openid-configuration"));
        Assert.Equal(OidcServer.Issuer, disc.GetProperty("issuer").GetString());
        Assert.Equal("code", disc.GetProperty("response_types_supported").EnumerateArray().Single().GetString());
        Assert.Equal("authorization_code", disc.GetProperty("grant_types_supported").EnumerateArray().Single().GetString());
        Assert.Equal("S256", disc.GetProperty("code_challenge_methods_supported").EnumerateArray().Single().GetString());
        Assert.Equal("ES256", disc.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Single().GetString());
        Assert.Equal(OidcServer.Issuer + "/oidc/token", disc.GetProperty("token_endpoint").GetString());
        var jwks = await Json(await rig.Http.GetAsync("/oidc/jwks"));
        var jwk = jwks.GetProperty("keys").EnumerateArray().Single();
        Assert.Equal("EC", jwk.GetProperty("kty").GetString());
        Assert.False(jwk.TryGetProperty("d", out _), "der private Teil darf nie in den Schluesseln stehen");

        var tokens = await rig.SignInAsync(app);
        var jwt = tokens.Jwt;
        Assert.Equal("ES256", jwt.Alg);
        Assert.Equal("JWT", jwt.Typ);
        Assert.Equal(jwk.GetProperty("kid").GetString(), jwt.Kid);
        Assert.Equal(OidcServer.Issuer, jwt.Issuer);
        Assert.Equal(app.ClientId, jwt.Audiences.Single());
        Assert.Equal("n-1", OidcRig.Claim(jwt, "nonce"));
        Assert.Equal("admin", OidcRig.Claim(jwt, "role"));                                // der Eigentuemer hat in jeder App admin
        Assert.Equal("jul", OidcRig.Claim(jwt, "preferred_username"));
        Assert.Equal("jul", OidcRig.Claim(jwt, "name"));
        Assert.Equal(["pwd", "otp"], jwt.GetPayloadValue<string[]>("amr"));
        Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromMinutes(5));
        var sub = OidcRig.Claim(jwt, "sub")!;
        Assert.NotEqual("jul", sub);
        Assert.NotEqual(rig.OwnerId.ToString(), sub);                                       // nicht die interne Kennung, nicht der Name
        Assert.Equal(await rig.Db.ScalarAsync<Guid>("select oidc_sub from users"), Guid.Parse(sub));
        var sid = OidcRig.Claim(jwt, "sid")!;
        Assert.False(string.IsNullOrEmpty(sid));
        Assert.DoesNotContain(Cookie2Value(rig.Cookie), sid);                              // nicht der Cookie-Wert der Sitzung
        var authTime = jwt.GetPayloadValue<long>("auth_time");
        Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - authTime, 0, 60);

        // Zugriffs-Token: userinfo, und nur mit dem Token im Kopf
        var info = await rig.Http.SendAsync(Bearer("/oidc/userinfo", tokens.AccessToken));
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.Equal("no-store", info.Headers.CacheControl!.ToString());
        var ij = await Json(info);
        Assert.Equal(sub, ij.GetProperty("sub").GetString());
        Assert.Equal("admin", ij.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Http.SendAsync(Bearer("/oidc/userinfo", "nicht-das-token"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Http.GetAsync("/oidc/userinfo?access_token=" + tokens.AccessToken)).StatusCode);   // nie ueber die Adresse

        // Antworten des Token-Wegs: nie zwischenspeichern, keine CORS-Kopfzeilen
        var verifier = OidcRig.NewVerifier();
        var res = await rig.TokenAsync(app, await rig.CodeAsync(app, verifier), verifier, basic: false);          // client_secret_post geht auch
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl!.ToString());
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
        var body = await Json(res);
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(300, body.GetProperty("expires_in").GetInt32());
        Assert.False(body.TryGetProperty("refresh_token", out _), "es gibt keine Refresh-Token");

        // vom Code steht nur der Hash in der Datenbank, vom Geheimnis ebenso
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_codes where hash not similar to '[a-f0-9]{64}'"));
        var stored = await rig.Db.ScalarAsync<string>("select secret_hash from oidc_apps");
        Assert.NotEqual(app.Secret, stored);
        Assert.Equal(OidcRules.Hash(app.Secret), stored);
    }

    private static string Cookie2Value(string cookie) => cookie[(cookie.IndexOf('=') + 1)..];

    private static HttpRequestMessage Bearer(string url, string token)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Add("Authorization", "Bearer " + token);
        return r;
    }

    [Fact]
    public async Task Authorize_never_redirects_to_an_unregistered_address_and_gives_no_hint_whether_the_app_exists()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var off = await rig.AddAppAsync("Off");
        await rig.Http.SendAsync(Req(HttpMethod.Put, $"/api/security/oidc/apps/{off.Id}/enabled", rig.Cookie, new { enabled = false }));
        var v = OidcRig.NewVerifier();

        var bad = new List<(string What, HttpResponseMessage Res)>
        {
            ("andere Adresse", await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["redirect_uri"] = "https://evil.example/cb")),
            ("Adresse mit Zusatz", await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["redirect_uri"] = OidcRig.Redirect + "/x")),
            ("Adresse mit anderer Gross-/Kleinschreibung", await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["redirect_uri"] = "https://APP.example/cb")),
            ("Adresse fehlt", await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["redirect_uri"] = null)),
            ("unbekannte App", await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["client_id"] = new string('a', 32))),
            ("client_id keine Kennung", await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["client_id"] = "' or 1=1 --")),
            ("gesperrte App", await rig.AuthorizeAsync(off, rig.Cookie, v)),
            ("client_id doppelt", await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["client_id"] = app.ClientId + "&client_id=" + app.ClientId)),
        };
        foreach (var (what, res) in bad)
        {
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{what}: {(int)res.StatusCode}");
            Assert.Null(res.Headers.Location);                                              // eine Fehlerseite in JulOS, keine Weiterleitung
        }
        // dieselbe Seite fuer alle Faelle: sie verraet nicht, ob es die App gibt
        var pages = await Task.WhenAll(bad.Select(b => Body(b.Res)));
        Assert.Single(pages.Distinct());
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_codes"));
    }

    [Fact]
    public async Task Authorize_refuses_requests_that_are_not_the_one_safe_flow_and_reports_it_to_the_registered_address_with_the_state()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var v = OidcRig.NewVerifier();

        async Task<Dictionary<string, string>> Rejected(Action<Dictionary<string, string?>> tweak)
        {
            var res = await rig.AuthorizeAsync(app, rig.Cookie, v, tweak);
            Assert.Equal(HttpStatusCode.Found, res.StatusCode);
            Assert.StartsWith(OidcRig.Redirect + "?", res.Headers.Location!.ToString());
            var q = OidcRig.Query(res);
            Assert.False(q.ContainsKey("code"));
            return q;
        }

        Assert.Equal("unsupported_response_type", (await Rejected(q => q["response_type"] = "token"))["error"]);             // kein implicit
        Assert.Equal("unsupported_response_type", (await Rejected(q => q["response_type"] = "code id_token"))["error"]);
        Assert.Equal("invalid_request", (await Rejected(q => { q["code_challenge"] = null; q["code_challenge_method"] = null; }))["error"]);   // PKCE ist Pflicht
        Assert.Equal("invalid_request", (await Rejected(q => q["code_challenge_method"] = "plain"))["error"]);
        Assert.Equal("invalid_request", (await Rejected(q => q["code_challenge"] = "zu-kurz"))["error"]);
        Assert.Equal("invalid_scope", (await Rejected(q => q["scope"] = "profile"))["error"]);                                // openid fehlt
        Assert.Equal("invalid_request", (await Rejected(q => q["prompt"] = "consent"))["error"]);
        Assert.Equal("invalid_request", (await Rejected(q => q["max_age"] = "-5"))["error"]);
        var noState = await Rejected(q => q["state"] = null);                                                                   // state ist Pflicht
        Assert.Equal("invalid_request", noState["error"]);
        Assert.False(noState.ContainsKey("state"));
        var twice = await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["raw"] = "state=b");                                   // ein Parameter doppelt: nichts davon gilt
        Assert.Equal("invalid_request", OidcRig.Query(twice)["error"]);
        // das state kommt unveraendert und kodiert zurueck (Sonderzeichen, die eine Adresse zerlegen koennten)
        const string odd = "a b&c=d?e#f/ü";
        Assert.Equal(odd, (await Rejected(q => { q["state"] = odd; q["response_type"] = "token"; }))["state"]);
        Assert.Equal(odd, OidcRig.Query(await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["state"] = odd))["state"]);
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_codes"));       // nur die eine gueltige Anfrage hat einen Code ausgestellt
    }

    [Fact]
    public async Task Without_a_session_there_is_a_sign_in_page_or_login_required_and_never_a_code_and_max_age_forces_a_new_sign_in()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var v = OidcRig.NewVerifier();

        // erster Aufruf von einer fremden Seite: das SameSite=Strict-Cookie kam nicht mit; JulOS laedt sich einmal selbst neu (meta refresh) und fragt dann
        var first = await rig.AuthorizeAsync(app, null, v);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var html = await Body(first);
        Assert.Contains("http-equiv=\"refresh\"", html);
        Assert.Contains("jb=1", html);
        var second = await rig.AuthorizeAsync(app, null, v, q => q["jb"] = "1");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var page = await Body(second);
        Assert.Contains("oidcLogin", page);                                                  // das Anmeldeformular
        Assert.Contains("/oidc-login.js", page);
        Assert.DoesNotContain("<script>", page);                                              // kein eingebettetes Skript (CSP)
        Assert.Equal("DENY", second.Headers.GetValues("X-Frame-Options").Single());          // Rahmenschutz der Seite
        Assert.Contains("frame-ancestors 'none'", second.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Null(second.Headers.Location);

        // prompt=none ohne Sitzung: Fehler login_required an die App, mit state
        var silent = await rig.AuthorizeAsync(app, null, v, q => q["prompt"] = "none");
        Assert.Equal(HttpStatusCode.Found, silent.StatusCode);
        var sq = OidcRig.Query(silent);
        Assert.Equal("login_required", sq["error"]);
        Assert.Equal("st-1", sq["state"]);
        // ein abgelaufenes oder erfundenes Cookie ist keine Sitzung
        Assert.Equal("login_required", OidcRig.Query(await rig.AuthorizeAsync(app, "julos_sid=erfunden", v, q => q["prompt"] = "none"))["error"]);
        // mit Sitzung geht prompt=none ohne Formular
        Assert.True((await rig.CodeAsync(app, v, tweak: q => q["prompt"] = "none")).Length > 20);

        // max_age: die Anmeldung ist zwei Stunden her
        await rig.Db.ExecAsync("update sessions set created_at = now() - interval '2 hours'");
        Assert.Contains("oidcLogin", await Body(await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["max_age"] = "600")));
        Assert.Equal("login_required", OidcRig.Query(await rig.AuthorizeAsync(app, rig.Cookie, v, q => { q["max_age"] = "600"; q["prompt"] = "none"; }))["error"]);
        Assert.True((await rig.CodeAsync(app, v, tweak: q => q["max_age"] = "86400")).Length > 20);
        var tokenRes = await rig.TokenAsync(app, await rig.CodeAsync(app, v), v);
        var authTime = (await rig.ValidateAsync((await Json(tokenRes)).GetProperty("id_token").GetString()!, app.ClientId)).GetPayloadValue<long>("auth_time");
        Assert.True(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - authTime > 3600, "auth_time ist der Beginn der JulOS-Sitzung");
    }

    [Fact]
    public async Task A_person_without_a_role_gets_no_token_and_the_roles_decide_what_the_token_says()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var other = await rig.AddAppAsync("Other");
        var (annaCookie, annaId) = await rig.SecondPersonAsync();
        var v = OidcRig.NewVerifier();

        // keine Rolle: eine Fehlerseite in JulOS, keine Weiterleitung, kein Code
        var denied = await rig.AuthorizeAsync(app, annaCookie, v);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(denied.Headers.Location);
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_codes"));
        // still (prompt=none) ist es ein Fehler an die App, nie ein Erfolg
        var silent = OidcRig.Query(await rig.AuthorizeAsync(app, annaCookie, v, q => q["prompt"] = "none"));
        Assert.Equal("access_denied", silent["error"]);
        Assert.False(silent.ContainsKey("code"));

        // eine Rolle in der einen App hilft in der anderen nicht
        await rig.AssignAsync(app, annaId, "viewer");
        var t = await rig.SignInAsync(app, annaCookie);
        Assert.Equal("viewer", OidcRig.Claim(t.Jwt, "role"));
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.AuthorizeAsync(other, annaCookie, v)).StatusCode);

        // eine Rolle, die die App nicht (mehr) kennt, zaehlt nicht
        await rig.Db.ExecAsync("update oidc_apps set roles = array['admin','member'] where id = $1", app.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.AuthorizeAsync(app, annaCookie, v)).StatusCode);

        // die Rolle verschwindet zwischen authorize und token: kein Token
        await rig.AssignAsync(app, annaId, "member");
        var code = await rig.CodeAsync(app, v, annaCookie);
        await rig.Db.ExecAsync("delete from oidc_roles");
        var late = await rig.TokenAsync(app, code, v);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Equal("invalid_grant", (await Json(late)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_token_endpoint_refuses_wrong_secrets_wrong_pkce_reused_foreign_expired_codes_and_other_grants()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var b = await rig.AddAppAsync("Second", redirects: ["https://b.example/cb"]);
        var v = OidcRig.NewVerifier();
        static async Task<string> Err(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetString()!;

        // falsches Geheimnis, unbekannte App: dieselbe Antwort; der Code bleibt unberuehrt
        var code = await rig.CodeAsync(app, v);
        var wrong = await rig.TokenAsync(app, code, v, secret: "falsch");
        var unknown = await rig.TokenAsync(app, code, v, clientId: new string('b', 32));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await Body(wrong), await Body(unknown));
        Assert.Equal("invalid_client", await Err(wrong));
        Assert.NotNull(wrong.Headers.WwwAuthenticate.FirstOrDefault());
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.TokenAsync(app, code, v, basic: false, secret: "falsch")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.TokenAsync(app, code, v)).StatusCode);                                        // der richtige Versuch geht danach noch

        // das Geheimnis einer anderen App ist kein Geheimnis dieser App
        var c2 = await rig.CodeAsync(app, v);
        Assert.Equal("invalid_client", await Err(await rig.TokenAsync(app, c2, v, secret: b.Secret)));

        // falsches PKCE: der Code ist verbraucht, auch der richtige Verifier hilft danach nicht mehr
        var c3 = await rig.CodeAsync(app, v);
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(app, c3, OidcRig.NewVerifier())));
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(app, c3, v)));
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(app, await rig.CodeAsync(app, v), null)));                  // kein Verifier
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(app, await rig.CodeAsync(app, v), "kurz")));

        // andere Rueckkehr-Adresse beim Tausch
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(app, await rig.CodeAsync(app, v), v, redirect: "https://evil.example/cb")));
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(app, await rig.CodeAsync(app, v), v, redirect: null)));

        // der Code einer anderen App (die andere App meldet sich richtig an): ungueltig, und der Code bleibt der anderen App erhalten
        var foreign = await rig.CodeAsync(app, v);
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(b, foreign, v, redirect: "https://b.example/cb")));
        Assert.Equal(HttpStatusCode.OK, (await rig.TokenAsync(app, foreign, v)).StatusCode);

        // abgelaufen (60 Sekunden)
        var old = await rig.CodeAsync(app, v);
        Assert.InRange((double)await rig.Db.ScalarAsync<decimal>("select extract(epoch from max(expires_at - now())) from oidc_codes where used_at is null"), 1, 60);
        await rig.Db.ExecAsync("update oidc_codes set expires_at = now() - interval '1 second' where used_at is null");
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(app, old, v)));

        // andere Arten: kein password, kein refresh_token, kein implicit; nur POST mit Formular
        var fresh = await rig.CodeAsync(app, v);
        Assert.Equal("unsupported_grant_type", await Err(await rig.TokenAsync(app, fresh, v, grant: "password")));
        Assert.Equal("unsupported_grant_type", await Err(await rig.TokenAsync(app, fresh, v, grant: "refresh_token")));
        Assert.Equal("unsupported_grant_type", await Err(await rig.TokenAsync(app, fresh, v, grant: "client_credentials")));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.Http.GetAsync("/oidc/token")).StatusCode);
        var json = new HttpRequestMessage(HttpMethod.Post, "/oidc/token") { Content = JsonContent(new { grant_type = "authorization_code" }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Http.SendAsync(json)).StatusCode);
        // Geheimnis im Kopf UND im Formular: nur ein Weg
        var both = await rig.TokenAsync(app, fresh, v);
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);

        // die Sitzung bei JulOS ist zwischen authorize und token zu Ende: kein Token
        var last = await rig.CodeAsync(app, v);
        await rig.Db.ExecAsync("delete from sessions");
        Assert.Equal("invalid_grant", await Err(await rig.TokenAsync(app, last, v)));
    }

    private static StringContent JsonContent(object o) => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Presenting_a_code_a_second_time_revokes_what_it_gave_and_ends_the_apps_session()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var v = OidcRig.NewVerifier();
        var code = await rig.CodeAsync(app, v);
        var first = await rig.TokenAsync(app, code, v);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var access = (await Json(first)).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.SendAsync(Bearer("/oidc/userinfo", access))).StatusCode);
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_grants"));

        var again = await rig.TokenAsync(app, code, v);                                      // dasselbe noch einmal
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_grant", (await Json(again)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Http.SendAsync(Bearer("/oidc/userinfo", access))).StatusCode);      // das Zugriffs-Token ist widerrufen
        // und die Sitzung der App wird beendet: ein Logout-Token ist eingereiht
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_grants"));
        Assert.Equal("code_reused", await rig.Db.ScalarAsync<string>("select reason from oidc_logouts"));
        var audit = await new JulOS.Backend.Data.SecurityAuditStore(rig.Db.Data, TimeProvider.System).ListAsync(20);
        Assert.Contains(audit, e => e.Event == "oidc.code_reused");
    }

    [Fact]
    public async Task Without_a_server_key_or_a_public_address_the_central_is_off_and_never_guesses_the_address_from_the_host_header()
    {
        await using (var noUrl = await OidcRig.CreateAsync(publicUrl: null))
        {
            var app = await noUrl.AddAppAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await noUrl.Http.GetAsync("/.well-known/openid-configuration")).StatusCode);
            var req = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
            req.Headers.Host = "evil.example";
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await noUrl.Http.SendAsync(req)).StatusCode);                   // der Host-Kopf macht keinen Aussteller
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await noUrl.AuthorizeAsync(app, noUrl.Cookie, OidcRig.NewVerifier())).StatusCode);
        }
        await using var noKey = await OidcRig.CreateAsync(withKey: false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await noKey.Http.GetAsync("/oidc/jwks")).StatusCode);
        // mit gesetzter Adresse steht sie so im Token, auch wenn der Aufruf mit einem anderen Host kommt
        await using var rig = await OidcRig.CreateAsync();
        var req2 = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        req2.Headers.Host = "evil.example";
        Assert.Equal(OidcServer.Issuer, (await Json(await rig.Http.SendAsync(req2))).GetProperty("issuer").GetString());
    }
}
