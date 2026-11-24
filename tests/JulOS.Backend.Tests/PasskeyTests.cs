using System.Net;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>
/// Passkeys (docs/PASSKEYS.md, sicherheitskritisch): der ganze Ablauf mit einem Authenticator in Software (PasskeyKit.cs) gegen den echten Server und die echte Datenbank.
/// Jeder Fehlerfall muss abgelehnt werden und dabei gleich aussehen; kein Fall darf eine Sitzung oder eine Freigabe hinterlassen.
/// </summary>
public sealed class PasskeyTests
{
    private const string Origin = OidcServer.Issuer;      // http://localhost, RP-Id: localhost

    private sealed class Rig : IAsyncDisposable
    {
        public TestDb Db = null!;
        public OidcServer Server = null!;
        public HttpClient Http = null!;
        public string Cookie = "";
        public Guid UserId;
        public ManualTime Time = new(DateTimeOffset.UtcNow);
        public SoftAuthenticator Device = new();

        public static async Task<Rig> CreateAsync()
        {
            var r = new Rig { Db = await TestDb.CreateAsync() };
            r.Server = new OidcServer(r.Db, time: r.Time);
            r.Http = r.Server.Http();
            (r.Cookie, r.UserId) = await SignedInAsync(r.Http, r.Db);
            return r;
        }

        public async Task<HttpResponseMessage> Send(HttpMethod m, string url, object? body = null, string? cookie = null) =>
            await Http.SendAsync(Req(m, url, cookie ?? Cookie, body ?? (m == HttpMethod.Get ? null : new { })));

        public static object Cred(string credentialJson, string? name = null) => new { name, credential = JsonDocument.Parse(credentialJson).RootElement };

        /// <summary>Registriert einen Passkey (Optionen holen, Authenticator antwortet lassen, senden). Gibt die Antwort des Servers.</summary>
        public async Task<HttpResponseMessage> RegisterAsync(string name = "Handy", SoftAuthenticator.Behaviour? how = null, string? cookie = null)
        {
            var begin = await Send(HttpMethod.Post, "/api/auth/passkeys/register/begin", cookie: cookie);
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            var options = await begin.Content.ReadAsStringAsync();
            return await Send(HttpMethod.Post, "/api/auth/passkeys/register/finish", Cred(Device.Register(options, Origin, how), name), cookie);
        }

        public async Task<string> LoginOptionsAsync()
        {
            var begin = await Http.SendAsync(Req(HttpMethod.Post, "/api/auth/passkey/login/begin", null, new { }));
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            return await begin.Content.ReadAsStringAsync();
        }

        public Task<HttpResponseMessage> LoginFinishAsync(string credentialJson) =>
            Http.SendAsync(Req(HttpMethod.Post, "/api/auth/passkey/login/finish", null, Cred(credentialJson)));

        public async Task<HttpResponseMessage> LoginAsync(SoftAuthenticator.Behaviour? how = null, SoftAuthenticator.Cred? pick = null) =>
            await LoginFinishAsync(Device.Assert(await LoginOptionsAsync(), Origin, how, pick));

        public async Task<HttpResponseMessage> StepUpAsync(SoftAuthenticator.Behaviour? how = null, string? cookie = null)
        {
            var begin = await Send(HttpMethod.Post, "/api/auth/step-up/passkey/begin", cookie: cookie);
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            return await Send(HttpMethod.Post, "/api/auth/step-up/passkey/finish", Cred(Device.Assert(await begin.Content.ReadAsStringAsync(), Origin, how)), cookie);
        }

        public Task ClearGrant() => Db.ExecAsync("update sessions set step_up_until = null, step_up_once = false");
        public Task<long> AuditCount(string evt) => Db.ScalarAsync<long>("select count(*) from security_audit where event = $1", evt);
        public Task<long> Sessions() => Db.ScalarAsync<long>("select count(*) from sessions");

        public static string CookieOf(HttpResponseMessage r) => r.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        public async ValueTask DisposeAsync() { Http.Dispose(); await Server.DisposeAsync(); await Db.DisposeAsync(); }
    }

    private static async Task<string> Error(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetString()!;

    // ---- Erfolg ----

    [Fact]
    public async Task Register_then_sign_in_without_a_user_name_gives_a_passkey_session_that_is_fresh_for_actions()
    {
        await using var r = await Rig.CreateAsync();
        var begin = await r.Send(HttpMethod.Post, "/api/auth/passkeys/register/begin");
        var options = await begin.Content.ReadAsStringAsync();
        var o = JsonDocument.Parse(options).RootElement;
        // die Vorgaben des Plans: Resident Key und Nutzerpruefung verlangt, keine Attestation, 32 Byte Herausforderung, RP-Id aus der oeffentlichen Adresse
        Assert.Equal("localhost", o.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("required", o.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());
        Assert.Equal("required", o.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        Assert.Equal("none", o.GetProperty("attestation").GetString());
        Assert.Equal(32, SoftAuthenticator.Unb64(o.GetProperty("challenge").GetString()!).Length);
        Assert.Empty(o.GetProperty("excludeCredentials").EnumerateArray());

        var fin = await r.Send(HttpMethod.Post, "/api/auth/passkeys/register/finish", Rig.Cred(r.Device.Register(options, Origin), "Mein Handy"));
        Assert.Equal(HttpStatusCode.OK, fin.StatusCode);
        var item = await Json(fin);
        Assert.Equal("Mein Handy", item.GetProperty("name").GetString());
        Assert.DoesNotContain("credential", await fin.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);     // nie Credential-Id oder Schluessel in der Antwort
        Assert.Equal(1L, await r.Db.ScalarAsync<long>("select count(*) from passkeys"));
        Assert.Equal(1L, await r.AuditCount("passkey.added"));

        // anmelden: ohne Benutzername, leere Erlaubnisliste, Nutzerpruefung verlangt
        var loginOptions = await r.LoginOptionsAsync();
        var lo = JsonDocument.Parse(loginOptions).RootElement;
        Assert.Equal("required", lo.GetProperty("userVerification").GetString());
        Assert.True(!lo.TryGetProperty("allowCredentials", out var allow) || allow.ValueKind == JsonValueKind.Null || allow.GetArrayLength() == 0);
        var login = await r.LoginFinishAsync(r.Device.Assert(loginOptions, Origin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("jul", (await Json(login)).GetProperty("user").GetString());
        var cookie = Rig.CookieOf(login);
        var me = await r.Http.SendAsync(Req(HttpMethod.Get, "/api/auth/me", cookie));
        Assert.Equal("jul", (await Json(me)).GetProperty("user").GetString());

        // die Sitzung hat die Staerke passkey und ist fuer Aktionen freigegeben (eine Anmeldung mit Passkey ist ein frischer Nachweis)
        Assert.Equal(1L, await r.Db.ScalarAsync<long>("select count(*) from sessions where strength = 'passkey' and step_up_until > now()"));
        Assert.Equal(1L, await r.AuditCount("passkey.login"));
        Assert.NotNull(await r.Db.ScalarAsync<object>("select last_used_at from passkeys"));
    }

    [Fact]
    public async Task A_counter_of_zero_is_allowed_for_synced_passkeys_and_a_growing_one_is_stored()
    {
        await using var r = await Rig.CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await r.RegisterAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await r.LoginAsync(new(Counter: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await r.LoginAsync(new(Counter: 0))).StatusCode);        // wieder 0: erlaubt
        Assert.Equal(HttpStatusCode.OK, (await r.LoginAsync(new(Counter: 7))).StatusCode);
        Assert.Equal(7L, await r.Db.ScalarAsync<long>("select sign_count from passkeys"));
    }

    // ---- Ablehnen: jeder Fall 401 mit demselben Text, keine Sitzung ----

    [Fact]
    public async Task Every_wrong_assertion_is_refused_the_same_way_and_leaves_no_session()
    {
        await using var r = await Rig.CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await r.RegisterAsync()).StatusCode);
        var sessionsBefore = await r.Sessions();

        var cases = new Dictionary<string, SoftAuthenticator.Behaviour>
        {
            ["falscher Ursprung"] = new(Origin: "https://evil.example"),
            ["falscher RP-Hash"] = new(RpId: "evil.example"),
            ["Nutzerpruefung fehlt"] = new(UserVerified: false),
            ["Anwesenheit fehlt"] = new(UserPresent: false),
            ["falsche Signatur"] = new(BadSignature: true),
            ["unbekannte Credential-Id"] = new(CredentialIdOverride: new byte[32]),
            ["User-Handle einer anderen Person"] = new(UserHandle: Guid.NewGuid().ToByteArray()),
            ["User-Handle fehlt"] = new(OmitUserHandle: true),
            ["fremde Herausforderung"] = new(Challenge: SoftAuthenticator.B64(new byte[32])),
        };
        string? text = null;
        foreach (var (name, how) in cases)
        {
            r.Time.Advance(TimeSpan.FromSeconds(61));                                                  // das Ratenlimit (20 je Minute) soll hier nicht zaehlen, nur die Pruefung
            var res = await r.LoginAsync(how);
            Assert.True(res.StatusCode == HttpStatusCode.Unauthorized, $"{name}: {(int)res.StatusCode}");
            var msg = await Error(res);
            text ??= msg;
            Assert.Equal(text, msg);                                                                   // einheitlicher Fehler
            Assert.False(res.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(sessionsBefore, await r.Sessions());
        // und nach all dem geht der richtige Passkey (die Zaehler stehen unveraendert)
        Assert.Equal(HttpStatusCode.OK, (await r.LoginAsync()).StatusCode);
    }

    [Fact]
    public async Task A_counter_that_goes_backwards_is_refused_and_audited_as_a_suspected_clone()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, (await r.LoginAsync(new(Counter: 10))).StatusCode);
        var before = await r.Sessions();
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.LoginAsync(new(Counter: 5))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.LoginAsync(new(Counter: 10))).StatusCode);   // derselbe Stand ist eine Wiederholung
        Assert.Equal(before, await r.Sessions());
        Assert.True(await r.AuditCount("passkey.clone_suspected") >= 1);
        Assert.Equal(10L, await r.Db.ScalarAsync<long>("select sign_count from passkeys"));              // der gespeicherte Zaehler bleibt
    }

    [Fact]
    public async Task A_challenge_works_once_and_not_after_five_minutes()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync();
        var options = await r.LoginOptionsAsync();
        var answer = r.Device.Assert(options, Origin);
        Assert.Equal(HttpStatusCode.OK, (await r.LoginFinishAsync(answer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.LoginFinishAsync(answer)).StatusCode);       // dieselbe Antwort noch einmal (Wiederholung)

        var late = await r.LoginOptionsAsync();
        r.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.LoginFinishAsync(r.Device.Assert(late, Origin))).StatusCode);
        Assert.Equal(0L, await r.Db.ScalarAsync<long>("select count(*) from passkey_challenges where purpose = 'login'"));   // verbraucht, auch wenn abgelehnt
    }

    [Fact]
    public async Task The_challenge_is_stored_only_as_a_hash()
    {
        await using var r = await Rig.CreateAsync();
        var options = await r.LoginOptionsAsync();
        var challenge = SoftAuthenticator.Unb64(JsonDocument.Parse(options).RootElement.GetProperty("challenge").GetString()!);
        Assert.Equal(PasskeyRules.ChallengeBytes, challenge.Length);
        Assert.Equal(1L, await r.Db.ScalarAsync<long>("select count(*) from passkey_challenges where hash = $1", SHA256.HashData(challenge)));
        Assert.Equal(0L, await r.Db.ScalarAsync<long>("select count(*) from passkey_challenges where hash = $1", challenge));
    }

    [Fact]
    public async Task A_credential_that_belongs_to_another_person_cannot_sign_in_or_confirm()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync();
        var other = await r.Db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('other', 'x', 'x', true) returning id");
        await r.Db.ExecAsync("update passkeys set user_id = $1", other);                               // die Credential-Id gehoert jetzt der anderen Person; der Handle im Passkey ist der alte

        Assert.Equal(HttpStatusCode.Unauthorized, (await r.LoginAsync()).StatusCode);
        await r.ClearGrant();
        // Freigabe: begin findet keinen Passkey der eigenen Person
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Send(HttpMethod.Post, "/api/auth/step-up/passkey/begin")).StatusCode);
    }

    // ---- Registrieren ----

    [Fact]
    public async Task Registering_needs_a_fresh_proof_and_not_just_a_session()
    {
        await using var r = await Rig.CreateAsync();
        await r.ClearGrant();
        var res = await r.Send(HttpMethod.Post, "/api/auth/passkeys/register/begin");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("step_up_required", (await Json(res)).GetProperty("code").GetString());
        Assert.Equal(0L, await r.Db.ScalarAsync<long>("select count(*) from passkey_challenges"));
    }

    [Fact]
    public async Task A_second_registration_of_the_same_authenticator_is_refused_and_the_options_exclude_it()
    {
        await using var r = await Rig.CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await r.RegisterAsync("Handy")).StatusCode);
        var begin = await r.Send(HttpMethod.Post, "/api/auth/passkeys/register/begin");
        var options = await begin.Content.ReadAsStringAsync();
        var exclude = JsonDocument.Parse(options).RootElement.GetProperty("excludeCredentials");
        Assert.Equal(1, exclude.GetArrayLength());
        Assert.True(SoftAuthenticator.Unb64(exclude[0].GetProperty("id").GetString()!).AsSpan().SequenceEqual(r.Device.Creds[0].Id));
        // ein Authenticator, der die Ausschlussliste missachtet und dieselbe Credential-Id noch einmal meldet
        var again = r.Device.Register(options, Origin, new(ReuseCredentialId: r.Device.Creds[0].Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Send(HttpMethod.Post, "/api/auth/passkeys/register/finish", Rig.Cred(again, "Doppelt"))).StatusCode);
        Assert.Equal(1L, await r.Db.ScalarAsync<long>("select count(*) from passkeys"));
    }

    [Fact]
    public async Task A_registration_with_a_wrong_origin_or_without_user_verification_is_refused()
    {
        await using var r = await Rig.CreateAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await r.RegisterAsync(how: new(Origin: "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.RegisterAsync(how: new(RpId: "evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.RegisterAsync(how: new(UserVerified: false))).StatusCode);
        Assert.Equal(0L, await r.Db.ScalarAsync<long>("select count(*) from passkeys"));
    }

    [Fact]
    public async Task A_registration_challenge_belongs_to_the_session_that_asked_for_it()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync("Erster");
        var second = Rig.CookieOf(await r.LoginAsync());                                               // zweite Sitzung derselben Person
        var begin = await r.Send(HttpMethod.Post, "/api/auth/passkeys/register/begin");                // angefordert in der ersten
        var answer = r.Device.Register(await begin.Content.ReadAsStringAsync(), Origin);
        var res = await r.Send(HttpMethod.Post, "/api/auth/passkeys/register/finish", Rig.Cred(answer, "Fremd"), second);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(1L, await r.Db.ScalarAsync<long>("select count(*) from passkeys"));
    }

    [Fact]
    public async Task At_most_twenty_passkeys_per_person()
    {
        await using var r = await Rig.CreateAsync();
        var store = r.Server.Services.GetRequiredService<PasskeyStore>();
        for (var i = 0; i < PasskeyRules.MaxPerUser; i++)
            Assert.NotNull(await store.AddAsync(r.UserId, "k" + i, new PasskeyNew(RandomNumberGenerator.GetBytes(32), [1], 0, Guid.Empty, false, false), DateTimeOffset.UtcNow));
        var res = await r.Send(HttpMethod.Post, "/api/auth/passkeys/register/begin");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Null(await store.AddAsync(r.UserId, "21", new PasskeyNew(RandomNumberGenerator.GetBytes(32), [1], 0, Guid.Empty, false, false), DateTimeOffset.UtcNow));
    }

    // ---- Liste, Umbenennen, Loeschen ----

    [Fact]
    public async Task Rename_and_delete_work_and_delete_needs_a_fresh_proof_and_ends_other_passkey_sessions()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync("Handy");
        var list = await Json(await r.Send(HttpMethod.Get, "/api/auth/passkeys"));
        var id = list.GetProperty("passkeys")[0].GetProperty("id").GetString()!;
        Assert.True(list.GetProperty("available").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await r.Send(HttpMethod.Put, "/api/auth/passkeys/" + id, new { name = "  Laptop\t privat " })).StatusCode);
        Assert.Equal("Laptop privat", (await Json(await r.Send(HttpMethod.Get, "/api/auth/passkeys"))).GetProperty("passkeys")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Send(HttpMethod.Put, "/api/auth/passkeys/" + id, new { name = "" })).StatusCode);

        var viaPasskey = Rig.CookieOf(await r.LoginAsync());                                           // eine zweite Sitzung, begonnen mit dem Passkey
        await r.ClearGrant();
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Send(HttpMethod.Delete, "/api/auth/passkeys/" + id)).StatusCode);
        Assert.Equal(1L, await r.Db.ScalarAsync<long>("select count(*) from passkeys"));
        // Freigabe mit dem Passkey selbst, dann geht es
        Assert.Equal(HttpStatusCode.OK, (await r.StepUpAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await r.Send(HttpMethod.Delete, "/api/auth/passkeys/" + id)).StatusCode);
        Assert.Equal(0L, await r.Db.ScalarAsync<long>("select count(*) from passkeys"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.Http.SendAsync(Req(HttpMethod.Get, "/api/auth/passkeys", viaPasskey))).StatusCode);   // die Passkey-Sitzung endete mit dem Passkey
        Assert.Equal(HttpStatusCode.OK, (await r.Send(HttpMethod.Get, "/api/auth/passkeys")).StatusCode);                                         // die eigene bleibt
        Assert.Equal(1L, await r.AuditCount("passkey.deleted"));
    }

    [Fact]
    public async Task Another_person_cannot_rename_or_delete_a_passkey()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync();
        var id = await r.Db.ScalarAsync<Guid>("select id from passkeys");
        var other = await r.Db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('other', 'x', 'x', true) returning id");
        await r.Db.ExecAsync("update passkeys set user_id = $1", other);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Send(HttpMethod.Put, "/api/auth/passkeys/" + id, new { name = "meins" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Send(HttpMethod.Delete, "/api/auth/passkeys/" + id)).StatusCode);
        Assert.Equal(1L, await r.Db.ScalarAsync<long>("select count(*) from passkeys"));
    }

    [Fact]
    public async Task Deleting_the_last_passkey_keeps_the_password_way_and_the_last_way_can_never_go()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync();
        var id = await r.Db.ScalarAsync<Guid>("select id from passkeys");
        Assert.Equal(HttpStatusCode.NoContent, (await r.Send(HttpMethod.Delete, "/api/auth/passkeys/" + id)).StatusCode);
        Assert.Equal(1L, await r.Db.ScalarAsync<long>("select count(*) from users where password_hash <> '' and totp_confirmed"));
        // die Regel selbst: ohne anderen Passkey und ohne Passwort-Weg darf nichts entfernt werden
        Assert.False(PasskeyRules.CanRemove(otherPasskeys: 0, passwordLogin: false));
        Assert.True(PasskeyRules.CanRemove(otherPasskeys: 1, passwordLogin: false));
        Assert.True(PasskeyRules.CanRemove(otherPasskeys: 0, passwordLogin: true));
    }

    // ---- Freigabe (Step-up) ----

    [Fact]
    public async Task A_passkey_opens_the_window_for_actions_like_a_code_and_a_wrong_one_does_not()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync();
        await r.ClearGrant();
        var action = () => r.Send(HttpMethod.Delete, $"/api/hosts/{Guid.NewGuid()}/agent");
        Assert.Equal("step_up_required", (await Json(await action())).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await r.StepUpAsync(new(UserVerified: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.StepUpAsync(new(Origin: "https://evil.example"))).StatusCode);
        Assert.Equal("step_up_required", (await Json(await action())).GetProperty("code").GetString());     // beide Fehlversuche haben nichts freigegeben
        Assert.True(await r.AuditCount("step_up.failed") >= 2);

        var ok = await r.StepUpAsync();
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await action()).StatusCode);                                      // die Freigabe gilt: der Weg erreicht seine eigene Antwort
    }

    [Fact]
    public async Task A_step_up_challenge_cannot_be_used_from_another_session_or_as_a_sign_in()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync();
        var second = Rig.CookieOf(await r.LoginAsync());
        var begin = await r.Send(HttpMethod.Post, "/api/auth/step-up/passkey/begin");
        var answer = r.Device.Assert(await begin.Content.ReadAsStringAsync(), Origin);
        // aus der anderen Sitzung eingeloest: abgelehnt
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.Send(HttpMethod.Post, "/api/auth/step-up/passkey/finish", Rig.Cred(answer), second)).StatusCode);
        // und als Anmeldung eingeloest: die Herausforderung war nicht dafuer (und ist ohnehin verbraucht)
        var begin2 = await r.Send(HttpMethod.Post, "/api/auth/step-up/passkey/begin");
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.LoginFinishAsync(r.Device.Assert(await begin2.Content.ReadAsStringAsync(), Origin))).StatusCode);
    }

    // ---- Schutz ----

    [Fact]
    public async Task Option_requests_are_rate_limited_per_address()
    {
        await using var r = await Rig.CreateAsync();
        var codes = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < PasskeyRules.RequestsPerMinute + 3; i++)
        {
            last = await r.Http.SendAsync(Req(HttpMethod.Post, "/api/auth/passkey/login/begin", null, new { }));
            codes.Add(last.StatusCode);
        }
        Assert.Equal(PasskeyRules.RequestsPerMinute, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(last.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task Failed_sign_ins_lock_the_address_like_wrong_passwords_but_never_the_account()
    {
        await using var r = await Rig.CreateAsync();
        await r.RegisterAsync();
        for (var i = 0; i < 10; i++)
        {
            r.Time.Advance(TimeSpan.FromSeconds(61));
            Assert.Equal(HttpStatusCode.Unauthorized, (await r.LoginFinishAsync(r.Device.Assert(await r.LoginOptionsAsync(), Origin, new(Origin: "https://evil.example")))).StatusCode);
        }
        var locked = await r.Http.SendAsync(Req(HttpMethod.Post, "/api/auth/passkey/login/begin", null, new { }));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        // gesperrt ist die Adresse; das Konto hat keinen Zaehler, den ein Fremder hochtreiben koennte
        var limiter = r.Server.Services.GetRequiredService<LoginLimiter>();
        Assert.Equal(TimeSpan.Zero, limiter.Wait("acct:" + r.UserId));
        Assert.Equal(TimeSpan.Zero, limiter.Wait("user:jul"));
    }

    [Fact]
    public async Task Garbage_bodies_are_refused_without_a_crash()
    {
        await using var r = await Rig.CreateAsync();
        foreach (var body in new object[] { new { }, new { credential = "x" }, new { credential = new { } }, new { credential = new { rawId = "!!", response = new { clientDataJSON = "e30" } } } })
        {
            var res = await r.Http.SendAsync(Req(HttpMethod.Post, "/api/auth/passkey/login/finish", null, body));
            Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, ((int)res.StatusCode).ToString());
        }
    }

    // ---- reine Regeln ----

    [Fact]
    public void The_site_comes_from_the_public_address_and_never_from_an_ip_address()
    {
        Assert.Equal(new PasskeySite("julos.example", "https://julos.example"), PasskeyRules.SiteFrom("https://julos.example"));
        Assert.Equal(new PasskeySite("julos.example", "https://julos.example:8443"), PasskeyRules.SiteFrom("https://julos.example:8443/pfad"));
        Assert.Equal(new PasskeySite("localhost", "http://localhost:5080"), PasskeyRules.SiteFrom("http://localhost:5080"));
        Assert.Null(PasskeyRules.SiteFrom("http://127.0.0.1:5080"));
        Assert.Null(PasskeyRules.SiteFrom("https://[::1]"));
        Assert.Null(PasskeyRules.SiteFrom(null));
    }

    [Fact]
    public async Task Without_a_public_address_there_are_no_passkeys()
    {
        await using var db = await TestDb.CreateAsync();
        await using var server = new OidcServer(db, publicUrl: null);
        var http = server.Http();
        var (cookie, _) = await SignedInAsync(http, db);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.SendAsync(Req(HttpMethod.Post, "/api/auth/passkey/login/begin", null, new { }))).StatusCode);
        Assert.False((await Json(await http.SendAsync(Req(HttpMethod.Get, "/api/auth/passkeys", cookie)))).GetProperty("available").GetBoolean());
    }

    [Fact]
    public void Names_handles_and_challenges_follow_the_rules()
    {
        Assert.Equal("Mein Handy", PasskeyRules.CleanName("  Mein \n Handy\u0007 "));
        Assert.Null(PasskeyRules.CleanName("   "));
        Assert.Null(PasskeyRules.CleanName(new string('x', PasskeyRules.NameMax + 1)));
        var id = Guid.NewGuid();
        Assert.True(PasskeyRules.HandleMatches(PasskeyRules.UserHandle(id), id));
        Assert.False(PasskeyRules.HandleMatches(PasskeyRules.UserHandle(Guid.NewGuid()), id));
        Assert.False(PasskeyRules.HandleMatches(null, id));
        var cd = System.Text.Encoding.UTF8.GetBytes("""{"type":"webauthn.get","challenge":"AQID","origin":"x"}""");
        Assert.Equal(new byte[] { 1, 2, 3 }, PasskeyRules.ChallengeOf(PasskeyRules.ToBase64Url(cd)));
        Assert.Null(PasskeyRules.ChallengeOf("not base64 !!"));
        Assert.Null(PasskeyRules.ChallengeOf(PasskeyRules.ToBase64Url("[1]"u8)));
    }

    [Fact]
    public void The_throttle_counts_and_forgets_after_a_minute()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var t = new PasskeyThrottle(time);
        for (var i = 0; i < PasskeyRules.RequestsPerMinute; i++) Assert.True(t.Allow("a", out _));
        Assert.False(t.Allow("a", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.True(t.Allow("b", out _));                                          // je Adresse
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(t.Allow("a", out _));
    }

    [Fact]
    public void The_id_token_says_how_the_session_began()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new ECDsaSecurityKey(ec) { KeyId = "k" };
        string[] AmrOf(string strength)
        {
            var jwt = new JsonWebToken(OidcTokens.CreateIdToken(new IdTokenClaims("http://localhost", "c", "s", "sid", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "admin", strength), key, TimeSpan.FromMinutes(5)));
            return [.. jwt.GetPayloadValue<JsonElement>("amr").EnumerateArray().Select(e => e.GetString()!)];
        }
        Assert.Equal(["pwd", "otp"], AmrOf(PasskeyRules.Password));
        Assert.Equal(["hwk", "user"], AmrOf(PasskeyRules.Passkey));
    }
}
