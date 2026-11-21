using System.Net;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Zweiter Faktor fuer Aktionen (F-04): der Server verlangt eine frische Freigabe mit einem Code aus der Authenticator-App; Zeitfenster, "immer fragen", Wiederverwenden, Sperre.</summary>
public sealed class StepUpTests
{
    private const string Password = "korrektes pferd batterie";

    private sealed class Rig
    {
        public ManualTime Time { get; } = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 * 30 + 1));
        public SessionStore Sessions = null!;
        public SecurityConfig Config = null!;
        public AuthService Auth = null!;

        public static async Task<Rig> CreateAsync(TestDb db)
        {
            var r = new Rig();
            var settings = new InstanceSettings(db.Data);
            await settings.LoadAsync();
            r.Config = new SecurityConfig(settings, _ => null);
            r.Sessions = new SessionStore(db.Data);
            r.Auth = new AuthService(new UserStore(db.Data), r.Sessions, new PasswordHasher(), new LoginLimiter(r.Time), new SetupCode("AAAA-BBBB-CCCC"), r.Time, NullLogger<AuthService>.Instance, r.Config, new SecurityAuditStore(db.Data, r.Time));
            return r;
        }

        public string CodeFor(string secret, int stepOffset = 0) => Totp.Hotp(Base32.Decode(secret), Time.GetUtcNow().ToUnixTimeSeconds() / 30 + stepOffset);

        public async Task<(string Secret, SessionIdentity Who)> SetupAsync()
        {
            var begin = await Auth.BeginSetupAsync("AAAA-BBBB-CCCC", "jul", Password, "1.2.3.4");
            var done = await Auth.FinishSetupAsync("AAAA-BBBB-CCCC", "jul", CodeFor(begin.Data!.Secret), "1.2.3.4", "test");
            return (begin.Data.Secret, (await Auth.ResolveSessionAsync(done.Token!, rotate: false))!);
        }
    }

    [Fact]
    public async Task A_login_opens_the_window_and_it_closes_after_the_set_time_and_a_new_code_opens_it_again_once_per_code()
    {
        await using var db = await TestDb.CreateAsync();
        var r = await Rig.CreateAsync(db);
        var (secret, who) = await r.SetupAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await r.Auth.StepUpFreshAsync(who, ct));                            // die Anmeldung mit Code ist selbst frisch
        r.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await r.Auth.StepUpFreshAsync(who, ct));
        r.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await r.Auth.StepUpFreshAsync(who, ct));                           // 11 Minuten: abgelaufen

        Assert.Equal(401, (await r.Auth.ConfirmStepUpAsync(who, "000000", "1.2.3.4", ct)).Status);
        Assert.False(await r.Auth.StepUpFreshAsync(who, ct));
        var code = r.CodeFor(secret);
        var ok = await r.Auth.ConfirmStepUpAsync(who, code, "1.2.3.4", ct);
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal(10, ok.Minutes);
        Assert.True(await r.Auth.StepUpFreshAsync(who, ct));

        r.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(await r.Auth.StepUpFreshAsync(who, ct));
        Assert.Equal(401, (await r.Auth.ConfirmStepUpAsync(who, code, "1.2.3.4", ct)).Status);   // derselbe Code gilt nur einmal (Replay)
        Assert.False(await r.Auth.StepUpFreshAsync(who, ct));

        var audit = await new SecurityAuditStore(db.Data, r.Time).ListAsync(20, ct);
        Assert.Contains(audit, e => e.Event == "step_up.ok");
        Assert.Contains(audit, e => e.Event == "step_up.failed");
        Assert.DoesNotContain(audit, e => (e.Detail ?? "").Contains(code));                // nie ein Code im Verlauf
    }

    [Fact]
    public async Task A_shorter_window_applies_at_once_to_approvals_already_given()
    {
        await using var db = await TestDb.CreateAsync();
        var r = await Rig.CreateAsync(db);
        var (_, who) = await r.SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await r.Sessions.CapStepUpAsync(r.Time.GetUtcNow(), 60, ct);                    // laenger: nie verlaengert
        r.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await r.Auth.StepUpFreshAsync(who, ct));
        await r.Sessions.CapStepUpAsync(r.Time.GetUtcNow(), 1, ct);                     // jetzt nur noch eine Minute
        r.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await r.Auth.StepUpFreshAsync(who, ct));
        r.Time.Advance(TimeSpan.FromSeconds(40));
        Assert.False(await r.Auth.StepUpFreshAsync(who, ct));
        await r.Sessions.GrantStepUpAsync(who.Id, r.Time.GetUtcNow().AddMinutes(5), false, ct);
        await r.Sessions.CapStepUpAsync(r.Time.GetUtcNow(), 0, ct);                     // "immer fragen": bestehende Freigaben entfallen
        Assert.False(await r.Auth.StepUpFreshAsync(who, ct));
    }

    [Fact]
    public async Task Wrong_codes_lock_the_account_for_a_while()
    {
        await using var db = await TestDb.CreateAsync();
        var r = await Rig.CreateAsync(db);
        var (secret, who) = await r.SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        r.Time.Advance(TimeSpan.FromMinutes(11));
        for (var i = 0; i < 5; i++) Assert.Equal(401, (await r.Auth.ConfirmStepUpAsync(who, "00000" + i, "1.2.3.4", ct)).Status);
        var locked = await r.Auth.ConfirmStepUpAsync(who, r.CodeFor(secret), "1.2.3.4", ct);
        Assert.Equal(429, locked.Status);                                                  // auch der richtige Code wartet
        Assert.True(locked.RetryAfter > 0);
        r.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await r.Auth.ConfirmStepUpAsync(who, r.CodeFor(secret), "1.2.3.4", ct)).Ok);
    }

    [Fact]
    public async Task Always_ask_gives_one_action_per_code_and_off_asks_for_nothing()
    {
        await using var db = await TestDb.CreateAsync();
        var r = await Rig.CreateAsync(db);
        var (secret, who) = await r.SetupAsync();
        var ct = TestContext.Current.CancellationToken;

        await r.Config.SetStepUpAsync(true, 0, ct);
        r.Time.Advance(TimeSpan.FromMinutes(11));                                       // die Freigabe der Anmeldung (noch mit der alten Einstellung) ist abgelaufen
        Assert.False(await r.Auth.StepUpFreshAsync(who, ct));
        Assert.True((await r.Auth.ConfirmStepUpAsync(who, r.CodeFor(secret), "1.2.3.4", ct)).Ok);
        Assert.True(await r.Auth.StepUpFreshAsync(who, ct));                            // genau eine Aktion
        Assert.False(await r.Auth.StepUpFreshAsync(who, ct));

        await r.Config.SetStepUpAsync(false, 10, ct);
        Assert.True(await r.Auth.StepUpFreshAsync(who, ct));                            // ausgeschaltet: nie eine Frage
        Assert.NotNull(SecurityConfig.ValidateStepUp(true, 61));
        Assert.Null(SecurityConfig.ValidateStepUp(true, 0));
    }

    // ---- HTTP: welche Wege der Server absichert ----

    private static readonly (string Method, string Url)[] Guarded =
    [
        ("POST", "/api/hosts/00000000-0000-0000-0000-000000000001/actions/prepare"), ("POST", "/api/files/remote/prepare"),
        ("PUT", "/api/hosts/00000000-0000-0000-0000-000000000001/agent/control"), ("POST", "/api/hosts/00000000-0000-0000-0000-000000000001/agent/code"),
        ("POST", "/api/hosts/00000000-0000-0000-0000-000000000001/agent/update"), ("PUT", "/api/hosts/00000000-0000-0000-0000-000000000001/agent/auto-update"),
        ("DELETE", "/api/hosts/00000000-0000-0000-0000-000000000001/agent"), ("PUT", "/api/hosts/00000000-0000-0000-0000-000000000001/login"),
        ("DELETE", "/api/hosts/00000000-0000-0000-0000-000000000001/login"), ("POST", "/api/auth/password"), ("POST", "/api/auth/totp/begin"),
        ("PUT", "/api/security/proxies"), ("PUT", "/api/security/hosts"), ("DELETE", "/api/security/hosts"), ("PUT", "/api/security/step-up"),
        ("PUT", "/api/security/backup-key"), ("POST", "/api/security/recovery"), ("PUT", "/api/security/files-write"),
    ];

    private static readonly (string Method, string Url)[] Open =
    [
        ("GET", "/api/security"), ("GET", "/api/hosts"), ("GET", "/api/files/remote/list"), ("POST", "/api/security/hosts/preview"),
        ("POST", "/api/hosts/00000000-0000-0000-0000-000000000001/actions/run"), ("POST", "/api/folders"), ("POST", "/api/auth/totp/finish"),
    ];

    private static async Task<(string Cookie, string Secret)> SignInAsync(HttpClient c)
    {
        var begin = await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/setup/begin", null, new { code = ApiKit.SetupCode, user = "jul", password = Password })));
        var secret = begin.GetProperty("secret").GetString()!;
        var totp = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var finish = await c.SendAsync(Req(HttpMethod.Post, "/api/setup/finish", null, new { code = ApiKit.SetupCode, user = "jul", totp }));
        return (finish.Headers.GetValues("Set-Cookie").First().Split(';')[0], secret);
    }

    private static async Task<bool> NeedsStepUp(HttpClient c, string cookie, (string Method, string Url) route)
    {
        var r = await c.SendAsync(Req(new HttpMethod(route.Method), route.Url, cookie, route.Method == "GET" ? null : new { }), TestContext.Current.CancellationToken);
        return r.StatusCode == HttpStatusCode.Forbidden && (await Json(r)).TryGetProperty("code", out var code) && code.GetString() == "step_up_required";
    }

    [Fact]
    public async Task The_server_refuses_guarded_routes_without_a_fresh_approval_and_accepts_them_after_a_code()
    {
        await using var db = await TestDb.CreateAsync();
        await using var server = new Server(db);
        var c = server.Http();
        var (cookie, secret) = await SignInAsync(c);
        var ct = TestContext.Current.CancellationToken;

        foreach (var route in Guarded) Assert.False(await NeedsStepUp(c, cookie, route), "frisch nach der Anmeldung: " + route);

        await db.ExecAsync("update sessions set step_up_until = now() - interval '1 minute'");           // abgelaufen
        foreach (var route in Guarded) Assert.True(await NeedsStepUp(c, cookie, route), "ohne Freigabe: " + route);
        foreach (var route in Open) Assert.False(await NeedsStepUp(c, cookie, route), "Lesen und der zweite Schritt brauchen keine: " + route);

        var wrong = await c.SendAsync(Req(HttpMethod.Post, "/api/auth/step-up", cookie, new { code = "000000" }), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.True(await NeedsStepUp(c, cookie, Guarded[0]));

        var code = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1);    // der Schritt der Anmeldung ist verbraucht: der naechste gilt (Toleranz +-1)
        var ok = await c.SendAsync(Req(HttpMethod.Post, "/api/auth/step-up", cookie, new { code }), ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(10, (await Json(ok)).GetProperty("minutes").GetInt32());
        foreach (var route in Guarded) Assert.False(await NeedsStepUp(c, cookie, route), "nach der Freigabe: " + route);

        await db.ExecAsync("update sessions set step_up_until = now() - interval '1 minute'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/step-up", cookie, new { code }), ct)).StatusCode);   // Replay
        Assert.True(await NeedsStepUp(c, cookie, Guarded[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/step-up", null, new { code }), ct)).StatusCode);        // ohne Sitzung
    }

    [Fact]
    public async Task Turning_it_off_in_the_app_lifts_the_check_and_the_setting_itself_is_guarded()
    {
        await using var db = await TestDb.CreateAsync();
        await using var server = new Server(db);
        var c = server.Http();
        var (cookie, _) = await SignInAsync(c);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, "/api/security/step-up", cookie, new { enabled = true, minutes = 90 }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Put, "/api/security/step-up", cookie, new { enabled = false, minutes = 10 }), ct)).StatusCode);
        await db.ExecAsync("update sessions set step_up_until = null");
        Assert.False(await NeedsStepUp(c, cookie, Guarded[0]));                           // aus: keine Frage
        var state = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/security", cookie), ct));
        Assert.False(state.GetProperty("stepUp").GetProperty("enabled").GetBoolean());
    }
}
