using System.Net;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Abmelden ueberall: Back-Channel Logout an jede App, die ein Token bekam, und RP-initiiertes Abmelden (/oidc/end-session). docs/SIGN-IN.md.</summary>
public sealed class OidcLogoutTests
{
    /// <summary>Statt des Netzes: merkt sich, was JulOS an die Adressen der Apps schicken will, und antwortet mit einem festen Status (oder einer Liste davon).</summary>
    private sealed class Recorder(params HttpStatusCode[] answers) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body, string? ContentType)> Posts { get; } = [];
        private int _n;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Posts) Posts.Add((request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType));
            var status = answers.Length == 0 ? HttpStatusCode.NoContent : answers[Math.Min(_n++, answers.Length - 1)];
            return new HttpResponseMessage(status);
        }
    }

    private static string LogoutTokenOf((Uri Uri, string Body, string? ContentType) post)
    {
        Assert.Equal("application/x-www-form-urlencoded", post.ContentType);
        var form = System.Web.HttpUtility.ParseQueryString(post.Body);
        Assert.Equal(["logout_token"], form.AllKeys);
        return form["logout_token"]!;
    }

    private static void Retry(OidcRig rig) => rig.Db.ExecAsync("update oidc_logouts set next_at = now() - interval '1 second' where done_at is null").GetAwaiter().GetResult();

    [Fact]
    public async Task Every_way_a_session_can_end_sends_the_app_a_signed_logout_token_in_the_form_the_standard_asks_for_and_only_to_the_registered_address()
    {
        await using var rig = await OidcRig.CreateAsync();
        var rec = new Recorder();
        rig.Outbound.Create = () => new HttpClient(rec, disposeHandler: false);
        var a = await rig.AddAppAsync("Notes");
        var noUri = await rig.AddAppAsync("Quiet", backchannel: null, redirects: ["https://quiet.example/cb"]);
        var ct = TestContext.Current.CancellationToken;

        // vier Personen-Sitzungen, jede endet auf einem anderen Weg; eine fuenfte bekommt nie ein Token und darum auch kein Logout-Token
        var anna = await rig.SecondPersonAsync("anna");
        await rig.AssignAsync(a, anna.UserId, "member");
        await rig.AssignAsync(noUri, anna.UserId, "member");
        var sessions = new List<(string Cookie, string Sid, string Sub, string How)>();
        for (var i = 0; i < 4; i++)
        {
            var token = await new SessionStore(rig.Db.Data).CreateAsync(anna.UserId, "127.0.0.1", "test");
            var cookie = "julos_sid=" + token;
            var t = await rig.SignInAsync(a, cookie);
            await rig.SignInAsync(noUri, cookie);
            sessions.Add((cookie, OidcRig.Claim(t.Jwt, "sid")!, OidcRig.Claim(t.Jwt, "sub")!, new[] { "logout", "device removed", "expired", "password changed" }[i]));
        }
        var neverUsed = "julos_sid=" + await new SessionStore(rig.Db.Data).CreateAsync(anna.UserId, "127.0.0.1", "test");
        Assert.Equal(8, await rig.Db.ScalarAsync<long>("select count(*) from oidc_grants"));
        Assert.Equal(4, sessions.Select(s => s.Sid).Distinct().Count());                    // je Sitzung eine eigene sid, nicht der Cookie-Wert
        Assert.DoesNotContain(sessions, s => s.Cookie.Contains(s.Sid));

        // solange alle Sitzungen leben, wird nichts geschickt
        await rig.Logout.RunOnceAsync(ct);
        Assert.Empty(rec.Posts);

        // 1. Abmelden in JulOS
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Http.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", sessions[0].Cookie, new { }))).StatusCode);
        // 2. Geraet entfernt (die Zeile ist weg)
        await rig.Db.ExecAsync("delete from sessions where hash = $1", SessionTokens.Hash(sessions[1].Cookie["julos_sid=".Length..]));
        // 3. abgelaufen (30 Tage ohne Nutzung)
        await rig.Db.ExecAsync("update sessions set last_seen = now() - interval '31 days' where hash = $1", SessionTokens.Hash(sessions[2].Cookie["julos_sid=".Length..]));
        // 4. Passwort geaendert (alle Sitzungen ausser einer enden): die vierte Sitzung geht, "neverUsed" bleibt
        await new UserStore(rig.Db.Data).SetPasswordAsync(anna.UserId, "neu", endSessions: true, ct, keepSession: await rig.Db.ScalarAsync<Guid>("select id from sessions where hash = $1", SessionTokens.Hash(neverUsed["julos_sid=".Length..])));

        await rig.Logout.RunOnceAsync(ct);

        // genau vier Logout-Token, alle an die eingetragene Adresse der einen App; die App ohne Adresse bekommt nichts
        Assert.Equal(4, rec.Posts.Count);
        Assert.All(rec.Posts, p => Assert.Equal(OidcRig.BackchannelUri, p.Uri.ToString()));
        var seen = new HashSet<string>();
        foreach (var post in rec.Posts)
        {
            var jwt = await rig.ValidateAsync(LogoutTokenOf(post), a.ClientId);                // Signatur, iss, aud, Laufzeit
            Assert.Equal("logout+jwt", jwt.Typ);
            Assert.Equal("ES256", jwt.Alg);
            Assert.Equal(OidcServer.Issuer, jwt.Issuer);
            Assert.False(jwt.TryGetPayloadValue<string>("nonce", out _), "ein Logout-Token hat kein nonce");
            Assert.False(string.IsNullOrEmpty(OidcRig.Claim(jwt, "jti")));
            Assert.True(jwt.IssuedAt > DateTime.UtcNow.AddMinutes(-1));
            Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromMinutes(5));
            using var events = JsonDocument.Parse(jwt.GetPayloadValue<JsonElement>("events").GetRawText());
            Assert.True(events.RootElement.TryGetProperty("http://schemas.openid.net/event/backchannel-logout", out var ev) && ev.ValueKind == JsonValueKind.Object);
            var sid = OidcRig.Claim(jwt, "sid")!;
            Assert.Equal(sessions.Single(s => s.Sid == sid).Sub, OidcRig.Claim(jwt, "sub"));
            Assert.True(seen.Add(sid));
        }
        Assert.Equal(sessions.Select(s => s.Sid).Order(), seen.Order());
        Assert.Equal(4, rec.Posts.Select(p => OidcRig.Claim(new JsonWebToken(LogoutTokenOf(p)), "jti")).Distinct().Count());   // jedes Token hat eine eigene jti
        // die lebende Sitzung ist unberuehrt, Zugestelltes ist erledigt und steht im Verlauf
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_grants"));
        Assert.Equal(4, await rig.Db.ScalarAsync<long>("select count(*) from oidc_logouts where result = 'ok'"));
        Assert.Equal(4, await rig.Db.ScalarAsync<long>("select count(*) from oidc_logouts where result = 'no_uri'"));
        var audit = await new SecurityAuditStore(rig.Db.Data, TimeProvider.System).ListAsync(50, ct);
        Assert.Equal(4, audit.Count(e => e.Event == "oidc.logout.ok"));
        Assert.DoesNotContain(audit, e => (e.Detail ?? "").Contains("ey"));                // kein Token im Verlauf
        await rig.Logout.RunOnceAsync(ct);
        Assert.Equal(4, rec.Posts.Count);                                                  // nichts doppelt
    }

    [Fact]
    public async Task A_failed_delivery_is_repeated_a_few_times_and_the_end_result_is_in_the_audit_trail()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var ct = TestContext.Current.CancellationToken;
        var flaky = new Recorder(HttpStatusCode.InternalServerError, HttpStatusCode.Redirect, HttpStatusCode.OK);
        rig.Outbound.Create = () => new HttpClient(flaky, disposeHandler: false);
        await rig.SignInAsync(app);
        await rig.Http.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", rig.Cookie, new { }));

        await rig.Logout.RunOnceAsync(ct);
        Assert.Single(flaky.Posts);
        await rig.Logout.RunOnceAsync(ct);
        Assert.Single(flaky.Posts);                                                         // die Wiederholung wartet (10 s), sie kommt nicht sofort
        Retry(rig); await rig.Logout.RunOnceAsync(ct);                                      // 3xx ist kein Erfolg und wird nicht verfolgt
        Assert.Equal(2, flaky.Posts.Count);
        Retry(rig); await rig.Logout.RunOnceAsync(ct);
        Assert.Equal(3, flaky.Posts.Count);
        Assert.Equal("ok", await rig.Db.ScalarAsync<string>("select result from oidc_logouts"));
        Assert.Equal(3, await rig.Db.ScalarAsync<int>("select attempts from oidc_logouts"));

        // dauerhaft kaputt: fuenf Versuche, dann Schluss und ein Eintrag im Verlauf
        rig.Cookie = "julos_sid=" + await new SessionStore(rig.Db.Data).CreateAsync(rig.OwnerId, "127.0.0.1", "test", ct, DateTimeOffset.UtcNow.AddMinutes(10));     // die erste Sitzung des Eigentuemers ist eben beendet worden
        var app2 = await rig.AddAppAsync("Dead", backchannel: "https://dead.example/logout", redirects: ["https://dead.example/cb"]);
        var dead = new Recorder(HttpStatusCode.BadGateway);
        rig.Outbound.Create = () => new HttpClient(dead, disposeHandler: false);
        var anna = await rig.SecondPersonAsync();
        await rig.AssignAsync(app2, anna.UserId, "member");
        await rig.SignInAsync(app2, anna.Cookie);
        await rig.Db.ExecAsync("delete from sessions where user_id = $1", anna.UserId);
        for (var i = 0; i < 8; i++) { await rig.Logout.RunOnceAsync(ct); Retry(rig); }
        Assert.Equal(5, dead.Posts.Count);
        Assert.Equal("failed: http 502", await rig.Db.ScalarAsync<string>("select result from oidc_logouts where app_id = $1", app2.Id));
        var audit = await new SecurityAuditStore(rig.Db.Data, TimeProvider.System).ListAsync(50, ct);
        Assert.Contains(audit, e => e.Event == "oidc.logout.failed" && e.Detail!.Contains("Dead"));
    }

    [Fact]
    public async Task A_grant_outlives_the_account_so_the_app_still_hears_that_the_session_is_gone()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var rec = new Recorder();
        rig.Outbound.Create = () => new HttpClient(rec, disposeHandler: false);
        var anna = await rig.SecondPersonAsync();
        await rig.AssignAsync(app, anna.UserId, "viewer");
        var t = await rig.SignInAsync(app, anna.Cookie);
        await rig.Db.ExecAsync("delete from users where id = $1", anna.UserId);                  // Konto geloescht: Sitzungen und Rollen verschwinden mit
        await rig.Logout.RunOnceAsync(TestContext.Current.CancellationToken);
        var post = Assert.Single(rec.Posts);
        Assert.Equal(OidcRig.Claim(t.Jwt, "sid"), OidcRig.Claim(new JsonWebToken(LogoutTokenOf(post)), "sid"));
    }

    // ---- RP-initiated logout ----

    private static string EndUrl(string? hint, string? redirect = OidcRig.PostLogout, string? state = "bye-1", bool jb = true) =>
        "/oidc/end-session?" + string.Join('&', new (string K, string? V)[] { ("id_token_hint", hint), ("post_logout_redirect_uri", redirect), ("state", state), ("jb", jb ? "1" : null) }
            .Where(p => p.V is not null).Select(p => p.K + "=" + Uri.EscapeDataString(p.V!)));

    private static HttpRequestMessage Get(string url, string? cookie) { var r = new HttpRequestMessage(HttpMethod.Get, url); if (cookie is not null) r.Headers.Add("Cookie", cookie); return r; }

    private static HttpRequestMessage Form(string url, string? cookie, string? origin, params (string K, string V)[] fields)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.K, f.V))) };
        if (cookie is not null) r.Headers.Add("Cookie", cookie);
        if (origin is not null) r.Headers.Add("Origin", origin);
        return r;
    }

    [Fact]
    public async Task End_session_signs_out_only_for_a_valid_hint_or_a_confirmation_and_goes_back_only_to_a_registered_address()
    {
        await using var rig = await OidcRig.CreateAsync();
        var rec = new Recorder();
        rig.Outbound.Create = () => new HttpClient(rec, disposeHandler: false);
        var app = await rig.AddAppAsync();
        var ct = TestContext.Current.CancellationToken;
        var alive = async () => await rig.Db.ScalarAsync<long>("select count(*) from sessions where user_id = $1", rig.OwnerId) > 0;
        var t = await rig.SignInAsync(app);

        // von einer fremden Seite ohne Cookie: erst die Seite, die sich selbst neu laedt; mit jb=1 ohne Sitzung gibt es nichts zu beenden
        var bounce = await rig.Http.SendAsync(Get(EndUrl(t.IdToken, jb: false), null));
        Assert.Equal(HttpStatusCode.OK, bounce.StatusCode);
        Assert.Contains("http-equiv=\"refresh\"", await bounce.Content.ReadAsStringAsync(ct));
        Assert.True(await alive());

        // kein Hinweis, falscher Hinweis, Hinweis einer fremden Signatur, Hinweis fuer eine andere Person: Rueckfrage, nichts passiert
        var forgedKey = new ECDsaSecurityKey(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)) { KeyId = t.Jwt.Kid };
        var forged = OidcTokens.CreateIdToken(new IdTokenClaims(OidcServer.Issuer, app.ClientId, OidcRig.Claim(t.Jwt, "sub")!, "s", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "admin"), forgedKey, TimeSpan.FromMinutes(5));
        var anna = await rig.SecondPersonAsync();
        await rig.AssignAsync(app, anna.UserId, "viewer");
        var annasToken = (await rig.SignInAsync(app, anna.Cookie)).IdToken;
        foreach (var hint in new string?[] { null, "kein.token.hier", t.IdToken[..^3] + "abc", forged, annasToken })
        {
            var res = await rig.Http.SendAsync(Get(EndUrl(hint), rig.Cookie));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Null(res.Headers.Location);
            Assert.Contains("method=\"post\"", await res.Content.ReadAsStringAsync(ct));        // die Rueckfrage
            Assert.True(await alive());
        }
        // die Rueckfrage: ein POST von einer fremden Seite zaehlt nicht, ohne Origin auch nicht
        foreach (var origin in new string?[] { "https://evil.example", "http://localhost.evil.example", null })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await rig.Http.SendAsync(Form("/oidc/end-session", rig.Cookie, origin))).StatusCode);
            Assert.True(await alive());
        }

        // eine nicht eingetragene Adresse: abgemeldet, aber keine Weiterleitung dorthin
        var again = await rig.SignInAsync(app);
        var noRedirect = await rig.Http.SendAsync(Get(EndUrl(again.IdToken, redirect: "https://evil.example/bye"), rig.Cookie));
        Assert.Equal(HttpStatusCode.OK, noRedirect.StatusCode);
        Assert.Null(noRedirect.Headers.Location);
        Assert.False(await alive());
        Assert.Contains(noRedirect.Headers.GetValues("Set-Cookie"), c => c.StartsWith("julos_sid=;") || c.Contains("expires=Thu, 01 Jan 1970"));

        // der gute Weg: gueltiger (auch abgelaufener) Hinweis, eingetragene Adresse, state kommt zurueck
        var (cookie2, _) = await NewOwnerSessionAsync(rig);
        var t2 = await rig.SignInAsync(app, cookie2);
        var oldHint = OidcTokens.CreateIdToken(new IdTokenClaims(OidcServer.Issuer, app.ClientId, OidcRig.Claim(t2.Jwt, "sub")!, OidcRig.Claim(t2.Jwt, "sid")!, DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-3), null, null, "admin"),
            await rig.Keys.ActiveAsync(ct), TimeSpan.FromMinutes(5));
        var ok = await rig.Http.SendAsync(Get(EndUrl(oldHint), cookie2));
        Assert.Equal(HttpStatusCode.Found, ok.StatusCode);
        Assert.Equal(OidcRig.PostLogout + "?state=bye-1", ok.Headers.Location!.ToString());
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from sessions where hash = $1", SessionTokens.Hash(cookie2["julos_sid=".Length..])));
        // ohne Hinweis aber mit Bestaetigung (POST vom eigenen Origin)
        var (cookie3, _) = await NewOwnerSessionAsync(rig);
        var confirmed = await rig.Http.SendAsync(Form("/oidc/end-session", cookie3, "http://localhost"));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from sessions where hash = $1", SessionTokens.Hash(cookie3["julos_sid=".Length..])));

        // und das Abmelden hat die Apps benachrichtigt (der Hintergrunddienst tut das ebenso)
        await rig.Logout.RunOnceAsync(ct);
        Assert.Equal(2, rec.Posts.Count);                                                  // die beiden Sitzungen, die der App ein Token gaben (die dritte nie)
        Assert.All(rec.Posts, p => Assert.Equal(OidcRig.BackchannelUri, p.Uri.ToString()));
    }

    private static async Task<(string Cookie, string Token)> NewOwnerSessionAsync(OidcRig rig)
    {
        var token = await new SessionStore(rig.Db.Data).CreateAsync(rig.OwnerId, "127.0.0.1", "test");
        return ("julos_sid=" + token, token);
    }
}
