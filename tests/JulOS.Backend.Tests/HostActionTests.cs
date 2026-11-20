using System.Net;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Hosts;
using JulOS.Integrations.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>
/// Aktionen auf Hosts (Stufe 3, docs/HOST-AGENT.md): die zweistufige Bestaetigung (abgelaufen, wiederverwendet, falsche Sitzung, falscher Host, geaenderte Aktion oder Ziel, getippter Name),
/// beide Schluessel (Stufe am Server, lokale Erlaubnis des Agenten), Pausenschalter, Rate-Limit, Verlauf (Audit), Zielpruefung, eine Aktion je Host, Fortschritt, Fehlercodes und der Weg zurueck nach
/// einem Verbindungsabbruch. Der Agent ist ein Fake im Speicher, der Server echt (Middleware, Datenbank). Der Fake-Agent fuehrt nichts aus; er merkt, was der Server ihm schickt.
/// </summary>
public sealed partial class AgentLinkTests
{
    private const string UnitsBody = """{"units":[{"unit":"nginx.service","active":"active","sub":"running","description":"Web server"},{"unit":"julos-agent.service","active":"active","sub":"running"},{"unit":"backup.service","active":"inactive","sub":"dead"}],"count":3,"truncated":false}""";
    private const string ActionDockerBody = """{"available":true,"running":2,"restarting":0,"total":2,"containers":[{"id":"aaaaaaaaaaaa","name":"web","image":"nginx:1","state":"running","status":"Up"},{"id":"bbbbbbbbbbbb","name":"julos-app","image":"ghcr.io/example/app:1","state":"running","status":"Up","project":"julos"}],"truncated":false}""";
    private static readonly string[] AllRights = ["updates", "reboot", "services", "docker", "logs"];
    private static readonly string[] ActionCaps = ["sysinfo", "updates", "services", "docker"];

    private static async Task<HostRig> ActionRigAsync(TestDb db, Server app, string? control = "full", string version = "0.6.0", object? allow = null, object? caps = null, string platform = "linux-host")
    {
        var r = await RigAsync(db, app, caps ?? ActionCaps, allow ?? AllRights, version, platform);
        r.Agent.HostReplies["/v1/host/units"] = (200, UnitsBody);
        r.Agent.HostReplies["/v1/host/docker"] = (200, ActionDockerBody);
        if (control is not null) Assert.Equal(HttpStatusCode.OK, (await Control(r, control)).StatusCode);
        return r;
    }

    private static Task<HttpResponseMessage> Control(HostRig r, string level) => r.Http.SendAsync(Put($"/api/hosts/{r.Host}/agent/control", new { level }, r.Cookie));

    private static HttpRequestMessage Put(string url, object body, string cookie)
    {
        var req = Post(url, body, cookie);
        req.Method = HttpMethod.Put;
        return req;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Prepare(HostRig r, string action, string? target = null, string? cookie = null, Guid? host = null, int? lines = null)
    {
        var res = await r.Http.SendAsync(Post($"/api/hosts/{host ?? r.Host}/actions/prepare", new { action, target, lines }, cookie ?? r.Cookie));
        return (res.StatusCode, await Json(res));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Run(HostRig r, string token, string action, string? target = null, string? typed = null, string? cookie = null, Guid? host = null, bool acknowledged = true)
    {
        var res = await r.Http.SendAsync(Post($"/api/hosts/{host ?? r.Host}/actions/run", new { token, action, target, typed, acknowledged }, cookie ?? r.Cookie));
        return (res.StatusCode, await Json(res));
    }

    private static async Task<JsonElement> WaitJob(HostRig r, string job, string? cookie = null, Func<string, bool>? done = null)
    {
        done ??= s => s != "running";
        JsonElement last = default;
        await Eventually(async () =>
        {
            var res = await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/actions/{job}", cookie ?? r.Cookie));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            last = await Json(res);
            return done(last.GetProperty("state").GetString()!);
        });
        return last;
    }

    /// <summary>Beide Schritte und das Warten auf das Ergebnis.</summary>
    private static async Task<JsonElement> Confirmed(HostRig r, string action, string? target = null, string? typed = null)
    {
        var (ps, p) = await Prepare(r, action, target);
        Assert.Equal(HttpStatusCode.OK, ps);
        var (rs, run) = await Run(r, p.GetProperty("token").GetString()!, action, target, typed);
        Assert.Equal(HttpStatusCode.Accepted, rs);
        return await WaitJob(r, run.GetProperty("job").GetString()!);
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;
    private static List<(string Method, string Path, string Body)> Posts(FakeLinkAgent a) { lock (a.Requests) return [.. a.Requests.Where(q => q.Method == "POST")]; }

    /// <summary>Der Verlauf als Zeilen "Aktion Ereignis Grund", aelteste zuerst, ohne den Eintrag der Stufe (den schreibt schon der Aufbau jedes Tests).</summary>
    private static async Task<string[]> Audit(TestDb db) => await AuditRows(db, "select action || ' ' || event || ' ' || coalesce(detail, '') from host_audit where action <> 'control.set' order by id");

    private static async Task<string[]> AuditRows(TestDb db, string sql)
    {
        await using var conn = await db.Data.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await rd.ReadAsync()) list.Add(rd.GetString(0).Trim());
        return [.. list];
    }

    // ------------------------------------------------------------------ Standard aus, zwei Schluessel, Pause

    [Fact]
    public async Task Actions_are_off_by_default_and_a_refusal_reaches_neither_the_agent_nor_hides_in_the_history()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app, control: null);
        Assert.Equal("read", await db.ScalarAsync<string>("select control from agents"));
        var (status, body) = await Prepare(r, "service.restart", "nginx.service");
        Assert.Equal((HttpStatusCode.Forbidden, "control_off"), (status, Code(body)));
        Assert.Empty(Posts(r.Agent));
        Assert.Equal(["service.restart denied control_off"], await Audit(db));
        var anonymous = await r.Http.SendAsync(Post($"/api/hosts/{r.Host}/actions/prepare", new { action = "updates.refresh" }, null));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task The_level_decides_which_kinds_of_action_the_server_allows()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app, control: "updates");
        Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "updates.refresh")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "logs.unit", "nginx.service")).Status);                        // Logs gehoeren zur Stufe updates
        foreach (var (action, target) in new[] { ("power.reboot", null), ("service.restart", "nginx.service"), ("docker.restart", "aaaaaaaaaaaa"), ("docker.pull", "aaaaaaaaaaaa") })
            Assert.Equal("control_off", Code((await Prepare(r, action, target)).Body));
        Assert.Equal(HttpStatusCode.BadRequest, (await Control(r, "root")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.SendAsync(Put($"/api/hosts/{Guid.NewGuid()}/agent/control", new { level = "full" }, r.Cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Control(r, "read")).StatusCode);
        Assert.Equal("control_off", Code((await Prepare(r, "updates.refresh")).Body));
        Assert.Empty(Posts(r.Agent));
    }

    [Fact]
    public async Task The_agents_local_permission_is_the_second_key_and_the_agent_must_be_new_enough_and_connected()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using (var r = await ActionRigAsync(db, app, allow: new[] { "updates" }))
        {
            Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "updates.refresh")).Status);
            Assert.Equal((HttpStatusCode.Forbidden, "agent_not_permitted"), ((await Prepare(r, "power.reboot")).Status, Code((await Prepare(r, "power.reboot")).Body)));   // der Server wuerde, der Agent nicht
            Assert.Empty(Posts(r.Agent));
        }
        await using var db2 = await TestDb.CreateAsync();
        await using var app2 = new Server(db2);
        await using (var old = await ActionRigAsync(db2, app2, version: "0.5.0"))
            Assert.Equal("agent_outdated", Code((await Prepare(old, "updates.refresh")).Body));
        await using var db3 = await TestDb.CreateAsync();
        await using var app3 = new Server(db3);
        await using (var nocap = await ActionRigAsync(db3, app3, caps: new[] { "sysinfo" }))
            Assert.Equal("unsupported", Code((await Prepare(nocap, "updates.refresh")).Body));
        await using var db4 = await TestDb.CreateAsync();
        await using var app4 = new Server(db4);
        await using var gone = await ActionRigAsync(db4, app4);
        await gone.Agent.DisposeAsync();
        await Eventually(() => !app4.Services.GetRequiredService<AgentLinks>().IsOnline(gone.Host));
        Assert.Equal("offline", Code((await Prepare(gone, "updates.refresh")).Body));
    }

    [Fact]
    public async Task The_pause_switch_stops_everything_even_a_confirmation_that_was_already_given()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        var (_, p) = await Prepare(r, "service.restart", "nginx.service");
        var paused = await r.Http.SendAsync(Put("/api/host-actions/pause", new { paused = true }, r.Cookie));
        Assert.True((await Json(paused)).GetProperty("paused").GetBoolean());
        Assert.True((await Json(await r.Http.SendAsync(Send(HttpMethod.Get, "/api/hosts", r.Cookie)))).GetProperty("actionsPaused").GetBoolean());
        Assert.Equal("paused", Code((await Prepare(r, "updates.refresh")).Body));
        var (rs, rb) = await Run(r, p.GetProperty("token").GetString()!, "service.restart", "nginx.service");
        Assert.Equal((HttpStatusCode.Forbidden, "paused"), (rs, Code(rb)));
        Assert.Empty(Posts(r.Agent));
        await r.Http.SendAsync(Put("/api/host-actions/pause", new { paused = false }, r.Cookie));
        Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "updates.refresh")).Status);
    }

    [Fact]
    public async Task The_environment_can_force_the_pause()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: new Dictionary<string, string> { ["JULOS_HOST_ACTIONS"] = "off" });
        await using var r = await ActionRigAsync(db, app);
        Assert.Equal("paused", Code((await Prepare(r, "updates.refresh")).Body));
        var again = await r.Http.SendAsync(Put("/api/host-actions/pause", new { paused = false }, r.Cookie));
        Assert.True((await Json(again)).GetProperty("paused").GetBoolean());                  // die Umgebung hat Vorrang vor der Einstellung
    }

    // ------------------------------------------------------------------ die zweistufige Bestaetigung

    [Fact]
    public async Task Two_steps_name_exactly_what_happens_and_only_the_second_one_reaches_the_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true,"unit":"nginx.service"}"""));

        var (status, p) = await Prepare(r, "service.restart", "nginx.service");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("service.restart", "nginx.service", "Server"), (p.GetProperty("action").GetString(), p.GetProperty("target").GetString(), p.GetProperty("host").GetString()));
        Assert.Equal(60, p.GetProperty("expiresIn").GetInt32());
        Assert.False(p.TryGetProperty("typeToConfirm", out _));
        var token = p.GetProperty("token").GetString()!;
        Assert.True(token.Length >= 40);
        Assert.Empty(Posts(r.Agent));                                                          // nach Schritt 1 ist nichts geschehen

        var (rs, run) = await Run(r, token, "service.restart", "nginx.service");
        Assert.Equal(HttpStatusCode.Accepted, rs);
        var job = await WaitJob(r, run.GetProperty("job").GetString()!);
        Assert.Equal("ok", job.GetProperty("state").GetString());
        var posts = Posts(r.Agent);
        var sent = Assert.Single(posts);
        Assert.Equal("/v1/host/service/restart", sent.Path);
        using var doc = JsonDocument.Parse(sent.Body);
        Assert.Equal(["job", "unit"], doc.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(run.GetProperty("job").GetString(), doc.RootElement.GetProperty("job").GetString());
        Assert.Equal(["service.restart prepared", "service.restart started routine:click", "service.restart ok"], (await Audit(db)).Select(x => x.Trim()).ToArray());
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from host_audit where position($1 in action || coalesce(target,'') || event || coalesce(detail,'') || coalesce(job,'')) > 0", token));   // das Token steht nirgends im Verlauf
    }

    [Fact]
    public async Task A_confirmation_works_once_expires_after_60_seconds_and_a_replay_does_nothing()
    {
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton<TimeProvider>(clock));
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true}"""));

        var (_, p1) = await Prepare(r, "updates.refresh");
        var t1 = p1.GetProperty("token").GetString()!;
        clock.Advance(TimeSpan.FromSeconds(61));
        var (expired, eb) = await Run(r, t1, "updates.refresh");
        Assert.Equal((HttpStatusCode.Forbidden, "confirmation_expired"), (expired, Code(eb)));
        Assert.Equal("confirmation_invalid", Code((await Run(r, t1, "updates.refresh")).Body));            // abgelaufen heisst auch: verbraucht

        var (_, p2) = await Prepare(r, "updates.refresh");
        var t2 = p2.GetProperty("token").GetString()!;
        Assert.NotEqual(t1, t2);
        var (ok, first) = await Run(r, t2, "updates.refresh");
        Assert.Equal(HttpStatusCode.Accepted, ok);
        await WaitJob(r, first.GetProperty("job").GetString()!);
        var (replay, rb) = await Run(r, t2, "updates.refresh");                                            // dieselbe Anfrage noch einmal
        Assert.Equal((HttpStatusCode.Forbidden, "confirmation_invalid"), (replay, Code(rb)));
        Assert.Equal("confirmation_invalid", Code((await Run(r, "erfunden", "updates.refresh")).Body));
        Assert.Equal("confirmation_invalid", Code((await Run(r, "", "updates.refresh")).Body));
        Assert.Single(Posts(r.Agent));                                                                     // der Agent bekam genau einen Auftrag
        Assert.Contains("updates.refresh denied confirmation_expired", await Audit(db));
        Assert.Contains("updates.refresh denied confirmation_invalid", await Audit(db));
    }

    [Fact]
    public async Task A_confirmation_belongs_to_one_session_one_host_one_action_and_one_target()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true}"""));
        var user = await db.ScalarAsync<Guid>("select id from users limit 1");
        var otherCookie = "julos_sid=" + await app.Services.GetRequiredService<SessionStore>().CreateAsync(user, "127.0.0.1", "test");     // eine zweite Sitzung desselben Benutzers
        var hostB = (await Json(await r.Http.SendAsync(Post("/api/hosts", new { name = "Zweiter" }, r.Cookie)))).GetProperty("id").GetGuid();

        async Task<string> Fresh(string action = "service.restart", string? target = "nginx.service") => (await Prepare(r, action, target)).Body.GetProperty("token").GetString()!;

        Assert.Equal("confirmation_mismatch", Code((await Run(r, await Fresh(), "service.restart", "nginx.service", cookie: otherCookie)).Body));              // falsche Sitzung
        Assert.Equal("confirmation_mismatch", Code((await Run(r, await Fresh(), "service.restart", "nginx.service", host: hostB)).Body));                  // falscher Host
        Assert.Equal("confirmation_mismatch", Code((await Run(r, await Fresh(), "service.stop", "nginx.service")).Body));                                  // geaenderte Aktion
        Assert.Equal("confirmation_mismatch", Code((await Run(r, await Fresh(), "service.restart", "backup.service")).Body));                              // geaendertes Ziel
        Assert.Equal("confirmation_mismatch", Code((await Run(r, await Fresh(), "service.restart", null)).Body));
        Assert.Empty(Posts(r.Agent));
        // Jeder Fehlversuch hat die Bestaetigung verbraucht: auch mit den richtigen Werten gilt sie nicht mehr
        var t = await Fresh();
        await Run(r, t, "service.stop", "nginx.service");
        Assert.Equal("confirmation_invalid", Code((await Run(r, t, "service.restart", "nginx.service")).Body));
        Assert.Empty(Posts(r.Agent));
    }

    private static Task SetMode(Server app, string mode) => app.Services.GetRequiredService<SecurityConfig>().SetConfirmModeAsync(ConfirmPolicy.ParseMode(mode));

    [Fact]
    public async Task Routine_actions_need_nothing_but_the_token_even_all_updates()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        foreach (var (action, target) in new[] { ("updates.install", "all"), ("updates.install", "libc6"), ("service.restart", "nginx.service"), ("docker.restart", "aaaaaaaaaaaa") })
        {
            var (_, p) = await Prepare(r, action, target);
            Assert.Equal(("routine", "click"), (p.GetProperty("confirmLevel").GetString(), p.GetProperty("confirmMode").GetString()));
            Assert.False(p.TryGetProperty("typeToConfirm", out _));
            var (status, run) = await Run(r, p.GetProperty("token").GetString()!, action, target, acknowledged: false);
            Assert.Equal(HttpStatusCode.Accepted, status);
            await WaitJob(r, run.GetProperty("job").GetString()!);
        }
    }

    [Fact]
    public async Task Dangerous_actions_ask_what_the_mode_says_and_the_server_enforces_it()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, path.Contains("reboot") ? """{"ok":true,"accepted":true,"delay":5}""" : """{"ok":true}"""));

        // Standard: Haekchen. Ohne "acknowledged" 400 confirm_required, nichts geht raus, die Bestaetigung bleibt
        var (_, p) = await Prepare(r, "power.reboot");
        Assert.Equal(("dangerous", "check", "reboot"), (p.GetProperty("confirmLevel").GetString(), p.GetProperty("confirmMode").GetString(), p.GetProperty("confirmWarn").GetString()));
        Assert.False(p.TryGetProperty("typeToConfirm", out _));
        var token = p.GetProperty("token").GetString()!;
        var (ts, tb) = await Run(r, token, "power.reboot", acknowledged: false);
        Assert.Equal((HttpStatusCode.BadRequest, "confirm_required"), (ts, Code(tb)));
        Assert.Empty(Posts(r.Agent));
        var (hs, hb) = await Run(r, token, "power.reboot");                                                    // dieselbe Bestaetigung, jetzt mit Haekchen
        Assert.Equal(HttpStatusCode.Accepted, hs);
        await WaitJob(r, hb.GetProperty("job").GetString()!);
        Assert.Contains("power.reboot started dangerous:check", await Audit(db));

        // Rechnernamen tippen (wie frueher): fehlt der Name 400 confirm_required, falscher Name typed_mismatch, Gross/Klein und Leerraum zaehlen nicht
        await SetMode(app, "type");
        var (_, pt) = await Prepare(r, "power.poweroff");
        Assert.Equal("srv-01", pt.GetProperty("typeToConfirm").GetString());                                   // der Rechnername, den der Agent meldet
        var tk = pt.GetProperty("token").GetString()!;
        Assert.Equal("confirm_required", Code((await Run(r, tk, "power.poweroff", acknowledged: true)).Body));      // das Haekchen ersetzt den Namen nicht
        Assert.Equal("typed_mismatch", Code((await Run(r, tk, "power.poweroff", typed: "anderer-name")).Body));
        Assert.Equal(HttpStatusCode.Accepted, (await Run(r, tk, "power.poweroff", typed: "  SRV-01 ", acknowledged: false)).Status);
        Assert.Contains("power.poweroff started critical:type", await Audit(db));                              // JulOS laeuft auf diesem Host (Container "julos"): sehr gefaehrlich
    }

    [Fact]
    public async Task Click_only_skips_dangerous_but_very_dangerous_falls_back_to_the_checkbox()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true,"accepted":true,"delay":5}"""));
        await SetMode(app, "click");

        var (_, reboot) = await Prepare(r, "power.reboot");
        Assert.Equal(("dangerous", "click"), (reboot.GetProperty("confirmLevel").GetString(), reboot.GetProperty("confirmMode").GetString()));
        var (rs, rb) = await Run(r, reboot.GetProperty("token").GetString()!, "power.reboot", acknowledged: false);
        Assert.Equal(HttpStatusCode.Accepted, rs);
        await WaitJob(r, rb.GetProperty("job").GetString()!);

        // Der Container von JulOS (Compose-Projekt "julos") stoppen: sehr gefaehrlich, auch bei "nur Klick" mit Haekchen
        var (_, stop) = await Prepare(r, "docker.stop", "bbbbbbbbbbbb");
        Assert.Equal(("critical", "check", "julos_self"), (stop.GetProperty("confirmLevel").GetString(), stop.GetProperty("confirmMode").GetString(), stop.GetProperty("confirmCritical").GetString()));
        var tk = stop.GetProperty("token").GetString()!;
        Assert.Equal("confirm_required", Code((await Run(r, tk, "docker.stop", "bbbbbbbbbbbb", acknowledged: false)).Body));
        Assert.Equal(HttpStatusCode.Accepted, (await Run(r, tk, "docker.stop", "bbbbbbbbbbbb")).Status);
        // Ein anderer Container ist Routine
        Assert.Equal("routine", (await Prepare(r, "docker.stop", "aaaaaaaaaaaa")).Body.GetProperty("confirmLevel").GetString());
    }

    [Fact]
    public async Task Too_many_requests_in_a_minute_and_too_many_open_confirmations_are_refused()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        for (var i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "updates.refresh")).Status);       // 20 offene Bestaetigungen sind das Hoechste
        var (open, ob) = await Prepare(r, "updates.refresh");
        Assert.Equal(((HttpStatusCode)429, "too_many"), (open, Code(ob)));
        for (var i = 21; i < HostActionEndpoints.PerMinute; i++) Assert.Equal("too_many", Code((await Prepare(r, "updates.refresh")).Body));   // zaehlt trotzdem als Anfrage
        var (status, body) = await Prepare(r, "updates.refresh");
        Assert.Equal(((HttpStatusCode)429, "rate_limited"), (status, Code(body)));
        Assert.Equal((HttpStatusCode)429, (await Run(r, "x", "updates.refresh")).Status);
        Assert.Empty(Posts(r.Agent));
    }

    // ------------------------------------------------------------------ Ziele

    [Fact]
    public async Task Targets_must_look_right_and_come_from_what_the_agent_itself_reported()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        foreach (var (action, target, status, code) in new (string, string?, HttpStatusCode, string)[]
        {
            ("updates.install", "x; rm -rf /", HttpStatusCode.BadRequest, "invalid_target"),
            ("updates.install", "pkg-", HttpStatusCode.BadRequest, "invalid_target"),
            ("updates.install", "$(id)", HttpStatusCode.BadRequest, "invalid_target"),
            ("updates.install", "openssl", HttpStatusCode.NotFound, "unknown_target"),                          // sauber geschrieben, aber nicht in der Update-Liste
            ("updates.install", null, HttpStatusCode.BadRequest, "invalid_target"),
            ("updates.refresh", "libc6", HttpStatusCode.BadRequest, "invalid_target"),                          // diese Aktion hat kein Ziel
            ("service.restart", "nginx.service; reboot", HttpStatusCode.BadRequest, "invalid_target"),
            ("service.restart", "../nginx.service", HttpStatusCode.BadRequest, "invalid_target"),
            ("service.restart", "unknown.service", HttpStatusCode.NotFound, "unknown_target"),
            ("service.stop", "julos-agent.service", HttpStatusCode.Conflict, "protected_unit"),
            ("service.restart", "julos-agent.service", HttpStatusCode.Conflict, "protected_unit"),
            ("docker.stop", "abc", HttpStatusCode.BadRequest, "invalid_target"),
            ("docker.stop", "AAAAAAAAAAAA", HttpStatusCode.BadRequest, "invalid_target"),
            ("docker.stop", "cccccccccccc", HttpStatusCode.NotFound, "unknown_target"),
            ("format.disk", null, HttpStatusCode.BadRequest, "unknown_action"),
            ("", null, HttpStatusCode.BadRequest, "unknown_action"),
        })
        {
            var (s, b) = await Prepare(r, action, target);
            Assert.True((status, code) == (s, Code(b)), $"{action} {target}: {s} {Code(b)}");
        }
        Assert.Empty(Posts(r.Agent));
        Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "updates.install", "libc6")).Status);                // eine Aktion, ein Paket der Liste
        Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "service.start", "backup.service")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "logs.unit", "julos-agent.service")).Status);        // die Logs des Agenten darf man lesen
    }

    [Fact]
    public async Task Stopping_the_julos_group_warns_but_is_not_blocked()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        var (_, own) = await Prepare(r, "docker.stop", "bbbbbbbbbbbb");
        Assert.Equal(["julos_group"], own.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("julos-app", own.GetProperty("targetName").GetString());
        var (_, web) = await Prepare(r, "docker.stop", "aaaaaaaaaaaa");
        Assert.Empty(web.GetProperty("warnings").EnumerateArray());
        Assert.Empty((await Prepare(r, "docker.start", "bbbbbbbbbbbb")).Body.GetProperty("warnings").EnumerateArray());          // Starten ist harmlos
        var (_, all) = await Prepare(r, "updates.install", "all");
        Assert.Equal(["kept_back"], all.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()).ToArray());         // die Beispielliste hat ein zurueckgehaltenes Paket
    }

    // ------------------------------------------------------------------ Ausfuehrung

    [Fact]
    public async Task Only_one_write_action_runs_per_host_and_progress_is_visible_while_it_runs()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        var release = new TaskCompletionSource();
        r.Agent.OnHost = async (m, path, body, progress) =>
        {
            await progress("Setting up libc6", 1, 2);
            await release.Task;
            return (200, """{"ok":true,"rebootRequired":true,"count":2,"output":"fertig"}""");
        };
        var (_, p) = await Prepare(r, "updates.install", "all");
        var (_, p2) = await Prepare(r, "service.restart", "nginx.service");                              // vor dem Start noch moeglich
        var (rs, run) = await Run(r, p.GetProperty("token").GetString()!, "updates.install", "all", typed: "srv-01");
        Assert.Equal(HttpStatusCode.Accepted, rs);
        var job = run.GetProperty("job").GetString()!;
        await Eventually(() => Posts(r.Agent).Count > 0);
        await Eventually(async () => { var j = await Json(await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/actions/{job}", r.Cookie))); return j.TryGetProperty("progress", out var pr) && pr.GetString() == "Setting up libc6"; });
        var mid = await Json(await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/actions/{job}", r.Cookie)));
        Assert.Equal(("running", 1, 2), (mid.GetProperty("state").GetString(), mid.GetProperty("done").GetInt32(), mid.GetProperty("total").GetInt32()));

        var (busy, bb) = await Run(r, p2.GetProperty("token").GetString()!, "service.restart", "nginx.service");
        Assert.Equal((HttpStatusCode.Conflict, "busy"), (busy, Code(bb)));
        Assert.Equal("busy", Code((await Prepare(r, "updates.refresh")).Body));
        Assert.Single(Posts(r.Agent));

        release.SetResult();
        var done = await WaitJob(r, job);
        Assert.Equal("ok", done.GetProperty("state").GetString());
        Assert.True(done.GetProperty("result").GetProperty("rebootRequired").GetBoolean());                  // der Neustart-Hinweis
        Assert.Equal(2, done.GetProperty("result").GetProperty("count").GetInt32());
        Assert.Contains("updates.install ok reboot_required", await Audit(db));
        Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "updates.refresh")).Status);                       // danach ist der Host wieder frei
    }

    [Fact]
    public async Task Failures_become_short_codes_never_free_text_of_the_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        var reply = (200, "{}");
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult(reply);
        foreach (var (status, body, expected) in new[]
        {
            (500, """{"error":"apt_locked","output":"E: Could not get lock"}""", "apt_locked"),
            (403, """{"error":"not_permitted","need":"updates"}""", "not_permitted"),
            (409, """{"error":"busy"}""", "busy"),
            (500, """{"error":"Rm -rf /; <script>"}""", "agent_error"),
            (200, "[1]", "invalid_answer"),
        })
        {
            reply = (status, body);
            var job = await Confirmed(r, "updates.refresh");
            Assert.Equal(("failed", expected), (job.GetProperty("state").GetString(), job.GetProperty("error").GetString()));
        }
        Assert.Contains("updates.refresh failed apt_locked", await Audit(db));
        Assert.DoesNotContain(await Audit(db), x => x.Contains("script") || x.Contains("rm -rf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Logs_are_read_on_request_cleaned_and_never_written_to_the_history()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true,"unit":"nginx.service","lines":["GEHEIM-ZEILE eins","zwei\u001b[31m"]}"""));
        var (_, p) = await Prepare(r, "logs.unit", "nginx.service", lines: 5000);
        var (_, run) = await Run(r, p.GetProperty("token").GetString()!, "logs.unit", "nginx.service");
        var job = await WaitJob(r, run.GetProperty("job").GetString()!);
        Assert.Equal(["GEHEIM-ZEILE eins", "zwei[31m"], job.GetProperty("result").GetProperty("lines").EnumerateArray().Select(x => x.GetString()).ToArray());
        using var body = JsonDocument.Parse(Assert.Single(Posts(r.Agent)).Body);
        Assert.Equal(200, body.RootElement.GetProperty("lines").GetInt32());                                  // nach oben begrenzt
        Assert.Equal("/v1/host/logs/unit", Posts(r.Agent)[0].Path);
        Assert.DoesNotContain(await Audit(db), x => x.Contains("GEHEIM"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from host_audit where target like '%GEHEIM%' or detail like '%GEHEIM%'"));
    }

    [Fact]
    public async Task Program_output_keeps_its_line_breaks_but_loses_control_characters()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, "{\"ok\":true,\"output\":\"Reading package lists...\\r\\nE: erste\\tZeile\\u001b[31m\\n\\n\\n\\nZeile 3\"}"));
        var job = await Confirmed(r, "updates.refresh");
        Assert.Equal("Reading package lists...\nE: erste Zeile[31m\n\nZeile 3", job.GetProperty("result").GetProperty("output").GetString());
    }

    [Fact]
    public async Task Containers_and_images_use_their_fixed_paths_with_the_id_from_the_list()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, path.EndsWith("pull") ? """{"ok":true,"container":"web","image":"nginx:1","updated":true}""" : """{"ok":true,"container":"web"}"""));
        foreach (var verb in new[] { "start", "stop", "restart", "pull" }) await Confirmed(r, "docker." + verb, "aaaaaaaaaaaa");
        Assert.Equal(["/v1/host/docker/start", "/v1/host/docker/stop", "/v1/host/docker/restart", "/v1/host/docker/pull"], Posts(r.Agent).Select(q => q.Path).ToArray());
        Assert.All(Posts(r.Agent), q => Assert.Equal("""{"job":"%s","id":"aaaaaaaaaaaa"}""".Replace("%s", JsonDocument.Parse(q.Body).RootElement.GetProperty("job").GetString()), q.Body));
        var pull = await Confirmed(r, "docker.pull", "aaaaaaaaaaaa");
        Assert.True(pull.GetProperty("result").GetProperty("updated").GetBoolean());
        Assert.Equal("nginx:1", pull.GetProperty("result").GetProperty("image").GetString());
        Assert.Contains("docker.stop ok", (await Audit(db)).Select(x => x.Trim()));
        Assert.Equal("docker.stop", await db.ScalarAsync<string>("select action from host_audit where event = 'prepared' and action = 'docker.stop' limit 1"));
        Assert.Equal("web (aaaaaaaaaaaa)", await db.ScalarAsync<string>("select target from host_audit where action = 'docker.stop' limit 1"));      // Name und Id
    }

    [Fact]
    public async Task Without_an_entry_in_the_history_nothing_is_executed()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true}"""));
        var (_, p) = await Prepare(r, "updates.refresh");
        await db.ExecAsync("drop table host_audit");
        var (status, body) = await Run(r, p.GetProperty("token").GetString()!, "updates.refresh");
        Assert.Equal((HttpStatusCode.InternalServerError, "audit_failed"), (status, Code(body)));
        Assert.Empty(Posts(r.Agent));
    }

    // ------------------------------------------------------------------ der Weg zurueck

    [Fact]
    public async Task After_a_lost_connection_the_result_is_fetched_from_the_agent_when_it_returns()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton(new HostActionOptions(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100), 100)));
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((-1, ""));                              // die Verbindung reisst mitten in der Aktion ab
        var (_, p) = await Prepare(r, "updates.install", "libc6");
        var (_, run) = await Run(r, p.GetProperty("token").GetString()!, "updates.install", "libc6");
        var job = run.GetProperty("job").GetString()!;
        await Eventually(() => !app.Services.GetRequiredService<AgentLinks>().IsOnline(r.Host));
        var lost = await WaitJob(r, job);
        Assert.Equal("lost", lost.GetProperty("state").GetString());
        await Eventually(async () => (await Audit(db)).Contains("updates.install lost no_answer"));            // der Eintrag folgt dem Zustand um einen Wimpernschlag (unter Last sichtbar)

        // Der Agent ist zurueck und hat die Aktion zu Ende gefuehrt: er kennt den Auftrag mit seinem Ergebnis
        await using var back = await FakeLinkAgent.ConnectAsync(app.Server, r.Token, version: "0.6.0", platform: "linux-host", hostname: "srv-01", caps: ActionCaps, allow: AllRights);
        back.HostReplies["/v1/host/jobs"] = (200, $$$"""{"jobs":[{"job":"{{{job}}}","action":"updates.install","state":"done","status":200,"result":{"ok":true,"rebootRequired":true,"count":1}}]}""");
        var done = await WaitJob(r, job, done: s => s != "lost" && s != "running");
        Assert.Equal("ok", done.GetProperty("state").GetString());
        Assert.True(done.GetProperty("result").GetProperty("rebootRequired").GetBoolean());
        await Eventually(async () => (await Audit(db)).Contains("updates.install ok reboot_required"));
        Assert.Empty(Posts(back));                                                                           // zurueckgeholt, nicht noch einmal ausgefuehrt
        Assert.Equal(HttpStatusCode.OK, (await Prepare(r, "updates.refresh")).Status);                         // und der Host ist wieder frei
    }

    [Fact]
    public async Task An_interrupted_job_on_the_agent_becomes_a_failure_not_a_success()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton(new HostActionOptions(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100), 100)));
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((-1, ""));
        var (_, p) = await Prepare(r, "updates.refresh");
        var (_, run) = await Run(r, p.GetProperty("token").GetString()!, "updates.refresh");
        var job = run.GetProperty("job").GetString()!;
        await WaitJob(r, job);
        await using var back = await FakeLinkAgent.ConnectAsync(app.Server, r.Token, version: "0.6.0", platform: "linux-host", hostname: "srv-01", caps: ActionCaps, allow: AllRights);
        back.HostReplies["/v1/host/jobs"] = (200, $$$"""{"jobs":[{"job":"{{{job}}}","state":"interrupted","status":0,"result":{"error":"interrupted"}}]}""");
        var done = await WaitJob(r, job, done: s => s == "failed");
        Assert.Equal("interrupted", done.GetProperty("error").GetString());
    }

    // ------------------------------------------------------------------ Verlauf, Eigentum, Liste der Dienste

    [Fact]
    public async Task The_history_shows_who_what_when_and_only_for_the_own_host()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true}"""));
        await Prepare(r, "service.start", "nowhere.service");                                                  // abgelehnt
        await Confirmed(r, "service.start", "backup.service");
        var res = await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/audit?limit=50", r.Cookie));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entries = (await Json(res)).GetProperty("entries").EnumerateArray().ToArray();
        entries = [.. entries.Where(e => e.GetProperty("action").GetString() != "control.set")];
        Assert.Equal(["ok", "started", "prepared", "denied"], entries.Select(e => e.GetProperty("event").GetString()).ToArray());      // neueste zuerst
        Assert.All(entries, e => Assert.Equal("jul", e.GetProperty("user").GetString()));
        Assert.Equal("backup.service", entries[0].GetProperty("target").GetString());
        Assert.Equal("unknown_target", entries[3].GetProperty("detail").GetString());
        Assert.True(entries[0].GetProperty("at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(1, (await Json(await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/audit?limit=1", r.Cookie)))).GetProperty("entries").GetArrayLength());

        // ein anderer Benutzer sieht weder den Verlauf noch einen Auftrag dieses Hosts
        var other = await db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('zweiter', 'x', 'x', true) returning id");
        var otherCookie = "julos_sid=" + await app.Services.GetRequiredService<SessionStore>().CreateAsync(other, "127.0.0.1", "test", stepUpUntil: DateTimeOffset.UtcNow.AddMinutes(10));      // freigegeben: es geht um den Besitzer des Hosts, nicht um den zweiten Faktor
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/audit", otherCookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Prepare(r, "updates.refresh", cookie: otherCookie)).Status);
        var job = await db.ScalarAsync<string>("select job from host_audit where event = 'started' limit 1");
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/actions/{job}", otherCookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{Guid.NewGuid()}/audit", r.Cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/actions/{Guid.NewGuid():N}", r.Cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.Http.GetAsync($"/api/hosts/{r.Host}/audit")).StatusCode);
    }

    [Fact]
    public async Task The_history_keeps_only_the_latest_entries_and_survives_deleting_the_host()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        var store = app.Services.GetRequiredService<HostAuditStore>();
        var user = await db.ScalarAsync<Guid>("select id from users limit 1");
        for (var i = 0; i < HostAuditStore.KeepPerHost + 20; i++) await store.AddAsync(user, r.Host, "Server", "updates.refresh", null, "prepared");
        Assert.Equal(HostAuditStore.KeepPerHost, await db.ScalarAsync<long>("select count(*) from host_audit"));
        var hostB = (await Json(await r.Http.SendAsync(Post("/api/hosts", new { name = "B" }, r.Cookie)))).GetProperty("id").GetGuid();
        await store.AddAsync(user, hostB, "B", "updates.refresh", null, "prepared");
        await r.Http.SendAsync(Send(HttpMethod.Delete, $"/api/hosts/{hostB}", r.Cookie));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from host_audit where host_id = $1", hostB));         // der Verlauf bleibt
    }

    [Fact]
    public async Task The_service_list_needs_the_level_and_comes_from_the_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app, control: "updates");
        var denied = await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/units", r.Cookie));
        Assert.Equal((HttpStatusCode.Forbidden, "control_off"), (denied.StatusCode, Code(await Json(denied))));
        Assert.DoesNotContain(r.Agent.Requests, q => q.Path == "/v1/host/units");
        await Control(r, "full");
        var ok = await Json(await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/units", r.Cookie)));
        Assert.Equal("ok", ok.GetProperty("state").GetString());
        Assert.Equal(["nginx.service", "julos-agent.service", "backup.service"], ok.GetProperty("units").GetProperty("units").EnumerateArray().Select(u => u.GetProperty("unit").GetString()).ToArray());
    }

    [Fact]
    public void The_action_table_is_closed_and_the_parser_never_passes_hostile_text_on()
    {
        Assert.Equal(14, HostActions.All.Count);
        Assert.All(HostActions.All, a => Assert.StartsWith("/v1/host/", a.Path));
        Assert.All(HostActions.All, a => Assert.Contains(a.Right, AgentControl.Rights));
        Assert.Equal(HostActions.All.Count, HostActions.All.Select(a => a.Path).Distinct().Count());
        Assert.Null(HostActions.Find("rm -rf /"));
        Assert.False(HostActions.ValidTarget(HostActions.Find("updates.install")!, "pkg\n"));                 // \z, nicht $: ein Zeilenumbruch am Ende zaehlt nicht
        Assert.False(HostActions.ValidTarget(HostActions.Find("service.stop")!, "x.service\n"));
        Assert.True(HostActions.ValidTarget(HostActions.Find("updates.install")!, "libstdc++6"));
        var (res, err) = HostActionService.ParseReply(200, """{"ok":true,"unit":"a\u0000b","lines":["x\ny"],"output":"o","accepted":"yes"}""");
        Assert.Null(err);
        Assert.False(res!.Accepted);                                                                          // nur ein echtes true gilt
        Assert.Equal("ab", res.Name);
        Assert.Equal("xy", Assert.Single(res.Lines!));
        Assert.Equal("agent_error", HostActionService.ParseReply(500, """{"error":["x"]}""").Error);
        Assert.Equal("invalid_answer", HostActionService.ParseReply(200, "[1]").Error);
        Assert.True(AgentControl.Allows("updates", "logs"));
        Assert.False(AgentControl.Allows("updates", "services"));
        Assert.True(AgentControl.Allows("full", "docker"));
        Assert.False(AgentControl.Allows("read", "logs"));
    }
}
