using System.Net;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.Extensions.DependencyInjection;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Die Verwaltung der Apps und Rollen in Einstellungen, Sicherheit: nur der Eigentuemer, mit zweitem Faktor, Geheimnis nur einmal, jede Aenderung im Verlauf.</summary>
public sealed class OidcAdminTests
{
    private static HttpRequestMessage Put(string url, string cookie, object? body = null) => Req(HttpMethod.Put, url, cookie, body ?? new { });

    [Fact]
    public async Task Apps_are_validated_the_secret_is_shown_once_and_stored_only_as_a_hash_and_every_change_is_in_the_audit_trail()
    {
        await using var rig = await OidcRig.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        async Task<(HttpStatusCode Status, string Code)> Create(object body)
        {
            var r = await rig.Http.SendAsync(Req(HttpMethod.Post, "/api/security/oidc/apps", rig.Cookie, body), ct);
            return (r.StatusCode, r.IsSuccessStatusCode ? "" : (await Json(r)).GetProperty("code").GetString()!);
        }
        string[] roles = ["admin", "member"];

        // was nicht eingetragen werden darf
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_redirect"), await Create(new { name = "X", redirectUris = new[] { "http://app.example/cb" }, roles }));                 // http nur zu localhost
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_redirect"), await Create(new { name = "X", redirectUris = new[] { "https://*.example/cb" }, roles }));                // keine Platzhalter
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_redirect"), await Create(new { name = "X", redirectUris = new[] { "https://app.example/cb#frag" }, roles }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_redirect"), await Create(new { name = "X", redirectUris = new[] { "https://user:pw@app.example/cb" }, roles }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_redirect"), await Create(new { name = "X", redirectUris = new[] { "javascript:alert(1)" }, roles }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_redirect"), await Create(new { name = "X", redirectUris = Array.Empty<string>(), roles }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_backchannel"), await Create(new { name = "X", redirectUris = new[] { OidcRig.Redirect }, backchannelUri = "ftp://app.example/x", roles }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_roles"), await Create(new { name = "X", redirectUris = new[] { OidcRig.Redirect }, roles = new[] { "Admin" } }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_roles"), await Create(new { name = "X", redirectUris = new[] { OidcRig.Redirect }, roles = new[] { "a", "a" } }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_roles"), await Create(new { name = "X", redirectUris = new[] { OidcRig.Redirect }, roles = Array.Empty<string>() }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_name"), await Create(new { name = " ", redirectUris = new[] { OidcRig.Redirect }, roles }));
        // http zu localhost (Entwicklung) ist erlaubt
        Assert.Equal(HttpStatusCode.OK, (await Create(new { name = "Dev", redirectUris = new[] { "http://localhost:5000/cb", "http://127.0.0.1:5000/cb" }, roles })).Status);

        var app = await rig.AddAppAsync("Notes");
        Assert.Equal((HttpStatusCode.Conflict, "name_taken"), await Create(new { name = "notes", redirectUris = new[] { OidcRig.Redirect }, roles }));

        // das Geheimnis: 256 Bit, nur der Hash in der Datenbank, nirgends sonst
        Assert.True(app.Secret.Length >= 43);
        Assert.Equal(OidcRules.Hash(app.Secret), await rig.Db.ScalarAsync<string>("select secret_hash from oidc_apps where id = $1", app.Id));
        var overview = await (await rig.Http.SendAsync(Req(HttpMethod.Get, "/api/security/oidc", rig.Cookie), ct)).Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(app.Secret, overview);
        Assert.DoesNotContain(OidcRules.Hash(app.Secret), overview);
        Assert.DoesNotContain("secretHash", overview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientSecret", overview);

        // neues Geheimnis: das alte gilt sofort nicht mehr
        var v = OidcRig.NewVerifier();
        var reissue = await rig.Http.SendAsync(Req(HttpMethod.Post, $"/api/security/oidc/apps/{app.Id}/secret", rig.Cookie, new { }), ct);
        var fresh = (await Json(reissue)).GetProperty("clientSecret").GetString()!;
        Assert.NotEqual(app.Secret, fresh);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.TokenAsync(app, await rig.CodeAsync(app, v), v)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.TokenAsync(app, await rig.CodeAsync(app, v), v, secret: fresh)).StatusCode);

        // sperren: kein Code, kein Token mehr (auch nicht mit dem richtigen Geheimnis); entsperren geht
        var pending = await rig.CodeAsync(app, v);
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.SendAsync(Put($"/api/security/oidc/apps/{app.Id}/enabled", rig.Cookie, new { enabled = false }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.AuthorizeAsync(app, rig.Cookie, v)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.TokenAsync(app, pending, v, secret: fresh)).StatusCode);
        await rig.Http.SendAsync(Put($"/api/security/oidc/apps/{app.Id}/enabled", rig.Cookie, new { enabled = true }), ct);
        Assert.Equal(HttpStatusCode.OK, (await rig.TokenAsync(app, pending, v, secret: fresh)).StatusCode);

        // loeschen: weg samt allem Zugehoerigen
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Http.SendAsync(Req(HttpMethod.Delete, $"/api/security/oidc/apps/{app.Id}", rig.Cookie), ct)).StatusCode);
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_codes where app_id = $1", app.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.AuthorizeAsync(app, rig.Cookie, v)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.SendAsync(Req(HttpMethod.Delete, $"/api/security/oidc/apps/{app.Id}", rig.Cookie), ct)).StatusCode);

        // Verlauf: Ereignisse mit Namen, nie ein Geheimnis
        var audit = await new SecurityAuditStore(rig.Db.Data, TimeProvider.System).ListAsync(100, ct);
        foreach (var e in new[] { "oidc.app.create", "oidc.app.secret", "oidc.app.disable", "oidc.app.enable", "oidc.app.delete" }) Assert.Contains(audit, x => x.Event == e);
        Assert.DoesNotContain(audit, x => (x.Detail ?? "").Contains(app.Secret) || (x.Detail ?? "").Contains(fresh));
    }

    [Fact]
    public async Task Only_the_owner_manages_and_every_change_needs_the_second_factor_and_the_public_address_is_checked()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var anna = await rig.SecondPersonAsync();
        await rig.AssignAsync(app, anna.UserId, "admin");                      // auch mit der Rolle admin in der App: die App verwalten heisst nicht JulOS verwalten
        var ct = TestContext.Current.CancellationToken;

        // ohne Sitzung 401, mit Sitzung einer anderen Person 403 (lesen wie schreiben)
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Http.SendAsync(Req(HttpMethod.Get, "/api/security/oidc", null), ct)).StatusCode);
        foreach (var (method, url) in new[] { (HttpMethod.Get, "/api/security/oidc"), (HttpMethod.Post, "/api/security/oidc/apps"), (HttpMethod.Post, $"/api/security/oidc/apps/{app.Id}/secret"),
            (HttpMethod.Put, $"/api/security/oidc/apps/{app.Id}/enabled"), (HttpMethod.Delete, $"/api/security/oidc/apps/{app.Id}"), (HttpMethod.Post, "/api/security/oidc/keys/rotate"),
            (HttpMethod.Put, "/api/security/oidc/issuer"), (HttpMethod.Put, $"/api/security/oidc/apps/{app.Id}/roles/{anna.UserId}") })
        {
            await rig.Db.ExecAsync("update sessions set step_up_until = now() + interval '5 minutes'");     // der zweite Faktor ist nicht das, was hier abweist
            var res = await rig.Http.SendAsync(Req(method, url, anna.Cookie, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { enabled = false, issuer = "https://x.example" }), ct);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Equal("owner_only", (await Json(res)).GetProperty("code").GetString());
        }
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_apps"));

        // der zweite Faktor: ohne frische Freigabe lehnt der Server jede Aenderung ab, auch fuer den Eigentuemer; Lesen geht
        await rig.Db.ExecAsync("update sessions set step_up_until = null");
        foreach (var (method, url) in new[] { (HttpMethod.Post, "/api/security/oidc/apps"), (HttpMethod.Post, $"/api/security/oidc/apps/{app.Id}/secret"), (HttpMethod.Put, $"/api/security/oidc/apps/{app.Id}/enabled"),
            (HttpMethod.Delete, $"/api/security/oidc/apps/{app.Id}"), (HttpMethod.Post, "/api/security/oidc/keys/rotate"), (HttpMethod.Put, "/api/security/oidc/issuer") })
        {
            var res = await rig.Http.SendAsync(Req(method, url, rig.Cookie, method == HttpMethod.Delete ? null : new { }), ct);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Equal("step_up_required", (await Json(res)).GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.SendAsync(Req(HttpMethod.Get, "/api/security/oidc", rig.Cookie), ct)).StatusCode);
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_apps"));

        // die oeffentliche Adresse: nur eine gueltige, kein Pfad mit Suchteil, kein http ausser localhost
        await rig.Db.ExecAsync("update sessions set step_up_until = now() + interval '5 minutes'");
        foreach (var bad in new[] { "http://julos.example", "julos.example", "https://julos.example/?x=1", "https://user@julos.example", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await rig.Http.SendAsync(Put("/api/security/oidc/issuer", rig.Cookie, new { issuer = bad }), ct)).StatusCode);
        var set = await rig.Http.SendAsync(Put("/api/security/oidc/issuer", rig.Cookie, new { issuer = "https://julos.example/" }), ct);
        Assert.Equal("https://julos.example", (await Json(set)).GetProperty("issuer").GetString());            // der abschliessende Schraegstrich entfaellt
        Assert.Equal("app", (await Json(set)).GetProperty("source").GetString());
        Assert.Equal("https://julos.example", (await Json(await rig.Http.GetAsync("/.well-known/openid-configuration", ct))).GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task Management_writes_need_a_fresh_second_factor_even_when_the_step_up_switch_is_off()
    {
        // F2: ein gestohlenes Eigentuemer-Cookie darf ohne Code keine App umbiegen (Geheimnis erneuern, Rueckkehr-Adresse aendern), auch wenn der Schalter aus ist
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var ct = TestContext.Current.CancellationToken;
        await rig.Server.Services.GetRequiredService<SecurityConfig>().SetStepUpAsync(false, StepUpConfig.DefaultMinutes, ct);
        await rig.Db.ExecAsync("update sessions set step_up_until = null");

        var change = new { name = "Notes", redirectUris = new[] { "https://evil.example/cb" }, postLogoutUris = Array.Empty<string>(), roles = new[] { "admin" } };
        foreach (var (method, url, body) in new (HttpMethod, string, object?)[] { (HttpMethod.Post, $"/api/security/oidc/apps/{app.Id}/secret", new { }), (HttpMethod.Put, $"/api/security/oidc/apps/{app.Id}", change),
            (HttpMethod.Put, $"/api/security/oidc/apps/{app.Id}/enabled", new { enabled = false }), (HttpMethod.Delete, $"/api/security/oidc/apps/{app.Id}", null), (HttpMethod.Post, "/api/security/oidc/keys/rotate", new { }),
            (HttpMethod.Put, "/api/security/oidc/issuer", new { issuer = "https://x.example" }) })
        {
            var res = await rig.Http.SendAsync(Req(method, url, rig.Cookie, body), ct);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Equal("step_up_required", (await Json(res)).GetProperty("code").GetString());
        }
        Assert.Equal(OidcRules.Hash(app.Secret), await rig.Db.ScalarAsync<string>("select secret_hash from oidc_apps where id = $1", app.Id));        // nichts hat sich geaendert
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.SendAsync(Req(HttpMethod.Get, "/api/security/oidc", rig.Cookie), ct)).StatusCode);                // Lesen braucht sie nicht

        // mit frischer Freigabe geht es; andere Sicherheits-Wege bleiben beim Schalter (aus = keine Freigabe noetig)
        await rig.Db.ExecAsync("update sessions set step_up_until = now() + interval '5 minutes'");
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.SendAsync(Req(HttpMethod.Post, $"/api/security/oidc/apps/{app.Id}/secret", rig.Cookie, new { }), ct)).StatusCode);
        await rig.Db.ExecAsync("update sessions set step_up_until = null");
        Assert.NotEqual(HttpStatusCode.Forbidden, (await rig.Http.SendAsync(Req(HttpMethod.Put, "/api/security/step-up", rig.Cookie, new { enabled = false, minutes = 10 }), ct)).StatusCode);
    }

    [Fact]
    public async Task Roles_belong_to_the_app_and_a_changed_or_removed_role_or_a_blocked_app_ends_the_sessions_in_that_app()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var anna = await rig.SecondPersonAsync();
        var ct = TestContext.Current.CancellationToken;
        var role = $"/api/security/oidc/apps/{app.Id}/roles/{anna.UserId}";
        async Task<long> Queued(string reason) => await rig.Db.ScalarAsync<long>("select count(*) from oidc_logouts where reason = $1", reason);
        async Task SignIn() { await rig.SignInAsync(app, anna.Cookie); }

        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Http.SendAsync(Put(role, rig.Cookie, new { role = "root" }), ct)).StatusCode);          // eine Rolle, die die App nicht kennt
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Http.SendAsync(Put($"/api/security/oidc/apps/{app.Id}/roles/{rig.OwnerId}", rig.Cookie, new { role = "viewer" }), ct)).StatusCode);   // der Eigentuemer hat immer admin
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.SendAsync(Put($"/api/security/oidc/apps/{app.Id}/roles/{Guid.NewGuid()}", rig.Cookie, new { role = "viewer" }), ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await rig.Http.SendAsync(Put(role, rig.Cookie, new { role = "member" }), ct)).StatusCode);
        await SignIn();
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Http.SendAsync(Put(role, rig.Cookie, new { role = "viewer" }), ct)).StatusCode);    // Rolle geaendert: die App-Sitzung endet, die naechste Anmeldung bringt die neue
        Assert.Equal(1, await Queued("role_changed"));
        Assert.Equal("viewer", OidcRig.Claim((await rig.SignInAsync(app, anna.Cookie)).Jwt, "role"));

        Assert.Equal(HttpStatusCode.NoContent, (await rig.Http.SendAsync(Req(HttpMethod.Delete, role, rig.Cookie), ct)).StatusCode);              // Rolle genommen: kein Zugang mehr
        Assert.Equal(1, await Queued("role_removed"));
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.AuthorizeAsync(app, anna.Cookie, OidcRig.NewVerifier())).StatusCode);

        // die Rolle aus der Liste der App streichen: alle Zuweisungen dieser Rolle entfallen und die Betroffenen sind dort abgemeldet
        await rig.Http.SendAsync(Put(role, rig.Cookie, new { role = "member" }), ct);
        await SignIn();
        var shorter = await rig.Http.SendAsync(Put($"/api/security/oidc/apps/{app.Id}", rig.Cookie,
            new { name = "Notes", redirectUris = new[] { OidcRig.Redirect }, postLogoutUris = Array.Empty<string>(), backchannelUri = OidcRig.BackchannelUri, roles = new[] { "admin", "viewer" } }), ct);
        Assert.Equal(HttpStatusCode.OK, shorter.StatusCode);
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_roles"));
        Assert.Equal(2, await Queued("role_removed"));

        // eine gesperrte App beendet die Sitzungen in ihr
        await rig.AssignAsync(app, anna.UserId, "viewer");
        await SignIn();
        await rig.Http.SendAsync(Put($"/api/security/oidc/apps/{app.Id}/enabled", rig.Cookie, new { enabled = false }), ct);
        Assert.Equal(1, await Queued("app_disabled"));
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_grants"));

        var audit = await new SecurityAuditStore(rig.Db.Data, TimeProvider.System).ListAsync(100, ct);
        Assert.Contains(audit, e => e.Event == "oidc.role.set" && e.Detail!.Contains("anna"));
        Assert.Contains(audit, e => e.Event == "oidc.role.remove");
    }

    [Fact]
    public async Task Too_many_wrong_app_logins_lock_the_address_for_a_while_and_authorize_is_rate_limited()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var v = OidcRig.NewVerifier();
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await rig.TokenAsync(app, "x", v, secret: "falsch")).StatusCode);
        var locked = await rig.TokenAsync(app, await rig.CodeAsync(app, v), v);                           // auch mit dem richtigen Geheimnis: die Adresse wartet
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter is not null);
        Assert.Equal("temporarily_unavailable", (await Json(locked)).GetProperty("error").GetString());

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 125; i++) statuses.Add((await rig.AuthorizeAsync(app, null, v, q => q["jb"] = "1")).StatusCode);
        Assert.Contains((HttpStatusCode)429, statuses);
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
    }
}
