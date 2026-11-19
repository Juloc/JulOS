using System.Net;
using System.Text.Json;
using JulOS.Backend.Logic;
using JulOS.Integrations.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>
/// Host-Agent (docs/HOST-AGENT.md): Systemwerte, Updates, Dienste und Docker eines Hosts ueber den Link. Der Agent ist ein Fake im Speicher, der Server echt (Middleware, Datenbank).
/// Geprueft wird, was der SERVER tut: Rechte (Sitzung, eigener Host), Zwischenspeicher (hoechstens alle 60 s, gleichzeitige Anfragen teilen sich eine), Offline- und Fehlerverhalten,
/// Allowlist der Faehigkeiten, und dass nichts, was der Agent liefert, ungeprueft beim Browser ankommt.
/// </summary>
public sealed partial class AgentLinkTests
{
    private const string SysinfoBody = """
        {"hostname":"srv-01","os":"Debian GNU/Linux 13 (trixie)","osId":"debian","osVersion":"13","kernel":"6.12.0","arch":"x86_64","uptimeSeconds":93784,"load":[0.52,0.4,0.31],"cpuCount":4,
         "memory":{"totalBytes":2097152000,"availableBytes":1048576000,"swapTotalBytes":0,"swapFreeBytes":0},
         "disks":[{"mount":"/","fs":"ext4","totalBytes":1000000,"usedBytes":600000,"freeBytes":300000,"percent":66.7}],
         "addresses":[{"iface":"eth0","family":4,"address":"192.0.2.10","prefix":24}],
         "network":[{"iface":"eth0","rxBytes":1000,"txBytes":2000}],
         "health":{"smart":{"available":false,"reason":"no_access"},"md":[{"name":"md0","level":"raid1","state":"degraded","active":1,"total":2}],"zfs":{"healthy":true,"pools":[]}},
         "rebootRequired":true,"rebootPackages":["linux-image-6.12"],"virt":{"kind":"vm","name":"kvm"},"proxmox":null}
        """;

    private const string UpdatesBody = """
        {"manager":"apt","count":2,"security":1,"securityKnown":true,"truncated":false,
         "packages":[{"name":"libc6","from":"2.36-9","to":"2.36-10","security":true,"new":false},{"name":"bash","from":"5.2-1","to":"5.2-2","security":false,"new":false}],
         "keptBack":["linux-image-amd64"],"held":[],"removals":0,"listsCheckedAt":1700000000,"listsAgeSeconds":7200,"checkedAt":1700007200}
        """;

    private static readonly string[] AllCaps = ["sysinfo", "updates", "services", "docker"];

    private sealed record HostRig(Server App, HttpClient Http, string Cookie, Guid Host, FakeLinkAgent Agent) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Agent.DisposeAsync();
    }

    private static async Task<HostRig> RigAsync(TestDb db, Server app, object? caps = null, object? allow = null)
    {
        var e = await EnrollAsync(app, db, "Server");
        var agent = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.5.0", platform: "linux-host", hostname: "srv-01", os: "Debian GNU/Linux 13", caps: caps ?? AllCaps, allow: allow);
        await Eventually(() => Online(app, e.Connection));
        agent.HostReplies["/v1/host/sysinfo"] = (200, SysinfoBody);
        agent.HostReplies["/v1/host/updates"] = (200, UpdatesBody);
        agent.HostReplies["/v1/host/services"] = (200, """{"state":"degraded","count":1,"failed":[{"unit":"nginx.service","sub":"failed","description":"Web server"}]}""");
        agent.HostReplies["/v1/host/docker"] = (200, """{"available":true,"running":1,"restarting":1,"total":2,"containers":[{"id":"abcdef123456","name":"web","image":"nginx:1","state":"running","status":"Up 2 hours"}],"truncated":false}""");
        return new HostRig(app, e.Http, e.Cookie, e.Host, agent);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetInfo(HostRig r, string what, string query = "")
    {
        var res = await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/{what}{query}", r.Cookie));
        return (res.StatusCode, await Json(res));
    }

    private static int Hits(FakeLinkAgent a, string path) { lock (a.Requests) return a.Requests.Count(q => q.Path == path); }

    [Fact]
    public async Task The_host_list_shows_kind_capabilities_control_and_what_the_agent_allows_locally()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await RigAsync(db, app, caps: new[] { "updates", "bogus", "sysinfo", "sysinfo", "rm -rf" }, allow: new[] { "updates", "format" });
        var agent = await AgentOf(r.Http, r.Cookie, r.Host);
        Assert.Equal("host", agent.GetProperty("kind").GetString());
        Assert.Equal(["sysinfo", "updates"], agent.GetProperty("caps").EnumerateArray().Select(x => x.GetString()).ToArray());   // nur bekannte Namen, ohne Doppelte, in fester Reihenfolge
        Assert.Equal(["updates"], agent.GetProperty("allow").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("read", agent.GetProperty("control").GetString());
        Assert.Equal("linux-host", agent.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task A_desktop_agent_has_no_capabilities_and_stays_a_desktop_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var e = await EnrollAsync(app, db);
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0", platform: "linux-x11");
        await Eventually(() => Online(app, e.Connection));
        var a = await AgentOf(e.Http, e.Cookie, e.Host);
        Assert.Equal("desktop", a.GetProperty("kind").GetString());
        Assert.Empty(a.GetProperty("caps").EnumerateArray());
        var res = await e.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{e.Host}/status", e.Cookie));
        Assert.Equal("unsupported", (await Json(res)).GetProperty("state").GetString());
        Assert.Empty(agent.Requests);                                       // dem Desktop-Agenten wird nichts Unbekanntes geschickt
    }

    [Fact]
    public async Task Status_updates_services_and_docker_come_through_with_the_fixed_paths_only()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await RigAsync(db, app);

        var (status, s) = await GetInfo(r, "status");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", s.GetProperty("state").GetString());
        Assert.False(s.GetProperty("stale").GetBoolean());
        var info = s.GetProperty("info");
        Assert.Equal("srv-01", info.GetProperty("hostname").GetString());
        Assert.Equal(93784, info.GetProperty("uptimeSeconds").GetInt64());
        Assert.Equal(0.52, info.GetProperty("load")[0].GetDouble());
        Assert.Equal(2097152000, info.GetProperty("memory").GetProperty("totalBytes").GetInt64());
        Assert.Equal(66.7, info.GetProperty("disks")[0].GetProperty("percent").GetDouble());
        Assert.Equal("192.0.2.10", info.GetProperty("addresses")[0].GetProperty("address").GetString());
        Assert.True(info.GetProperty("rebootRequired").GetBoolean());
        Assert.Equal("vm", info.GetProperty("virt").GetProperty("kind").GetString());
        Assert.False(info.TryGetProperty("proxmox", out _));                  // null fehlt
        Assert.Equal(2000, info.GetProperty("network")[0].GetProperty("txBytes").GetInt64());
        var health = info.GetProperty("health");
        Assert.Equal("no_access", health.GetProperty("smart").GetProperty("reason").GetString());
        Assert.Equal("degraded", health.GetProperty("md")[0].GetProperty("state").GetString());
        Assert.True(health.GetProperty("zfs").GetProperty("healthy").GetBoolean());

        var (_, u) = await GetInfo(r, "updates");
        var updates = u.GetProperty("updates");
        Assert.Equal((2, 1, "apt"), (updates.GetProperty("count").GetInt32(), updates.GetProperty("security").GetInt32(), updates.GetProperty("manager").GetString()));
        Assert.True(updates.GetProperty("packages")[0].GetProperty("security").GetBoolean());
        Assert.Equal("linux-image-amd64", updates.GetProperty("keptBack")[0].GetString());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), updates.GetProperty("listsCheckedAt").GetDateTimeOffset());
        Assert.Equal(7200, updates.GetProperty("listsAgeSeconds").GetInt64());

        var (_, sv) = await GetInfo(r, "services");
        Assert.Equal("nginx.service", sv.GetProperty("services").GetProperty("failed")[0].GetProperty("unit").GetString());
        var (_, d) = await GetInfo(r, "docker");
        Assert.Equal((1, 1, 2), (d.GetProperty("docker").GetProperty("running").GetInt32(), d.GetProperty("docker").GetProperty("restarting").GetInt32(), d.GetProperty("docker").GetProperty("total").GetInt32()));

        lock (r.Agent.Requests) Assert.All(r.Agent.Requests, q => { Assert.Equal("GET", q.Method); Assert.StartsWith("/v1/host/", q.Path); });
        Assert.Equal(["/v1/host/docker", "/v1/host/services", "/v1/host/sysinfo", "/v1/host/updates"], r.Agent.Requests.Select(q => q.Path).Order().ToArray());
    }

    [Fact]
    public async Task Answers_are_kept_for_60_seconds_an_explicit_refresh_waits_15_and_nobody_polls()
    {
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton<TimeProvider>(clock));
        await using var r = await RigAsync(db, app);

        await Task.Delay(300);                                                   // nach dem Verbinden fragt der Server von sich aus nichts
        Assert.Empty(r.Agent.Requests);

        await GetInfo(r, "status");
        await GetInfo(r, "status");
        clock.Advance(TimeSpan.FromSeconds(59));
        await GetInfo(r, "status");
        Assert.Equal(1, Hits(r.Agent, "/v1/host/sysinfo"));

        var (_, again) = await GetInfo(r, "status", "?fresh=1");                 // ausdruecklich, aber der Stand ist 59 s alt (> 15 s): neu lesen
        Assert.Equal(2, Hits(r.Agent, "/v1/host/sysinfo"));
        Assert.Equal("ok", again.GetProperty("state").GetString());
        clock.Advance(TimeSpan.FromSeconds(10));
        await GetInfo(r, "status", "?fresh=1");                                  // erst 10 s her: nicht noch einmal
        Assert.Equal(2, Hits(r.Agent, "/v1/host/sysinfo"));
        clock.Advance(TimeSpan.FromSeconds(6));
        await GetInfo(r, "status", "?fresh=1");
        Assert.Equal(3, Hits(r.Agent, "/v1/host/sysinfo"));
        clock.Advance(TimeSpan.FromSeconds(61));
        await GetInfo(r, "status");                                              // ohne fresh gilt 60 s
        Assert.Equal(4, Hits(r.Agent, "/v1/host/sysinfo"));
        Assert.Equal(0, Hits(r.Agent, "/v1/host/updates"));                      // jede Art hat ihren eigenen Speicher
    }

    [Fact]
    public async Task Simultaneous_requests_share_one_question_to_the_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await RigAsync(db, app);
        r.Agent.HostDelay = TimeSpan.FromMilliseconds(300);
        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => GetInfo(r, "updates")));
        Assert.All(all, x => Assert.Equal("ok", x.Body.GetProperty("state").GetString()));
        Assert.Equal(1, Hits(r.Agent, "/v1/host/updates"));
    }

    [Fact]
    public async Task A_missing_capability_is_unsupported_and_the_agent_is_not_asked()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await RigAsync(db, app, caps: new[] { "sysinfo" });
        Assert.Equal("unsupported", (await GetInfo(r, "updates")).Body.GetProperty("state").GetString());
        Assert.Equal("unsupported", (await GetInfo(r, "docker")).Body.GetProperty("state").GetString());
        Assert.Equal("ok", (await GetInfo(r, "status")).Body.GetProperty("state").GetString());
        Assert.Equal(["/v1/host/sysinfo"], r.Agent.Requests.Select(q => q.Path).ToArray());
    }

    [Fact]
    public async Task An_offline_agent_gives_offline_with_the_last_state_marked_stale_and_a_host_without_agent_gives_no_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var r = await RigAsync(db, app);
        await GetInfo(r, "status");

        await r.Agent.DisposeAsync();
        await Eventually(() => !app.Services.GetRequiredService<AgentLinks>().IsOnline(r.Host));
        var (code, off) = await GetInfo(r, "status");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("offline", off.GetProperty("state").GetString());
        Assert.True(off.GetProperty("stale").GetBoolean());
        Assert.Equal("srv-01", off.GetProperty("info").GetProperty("hostname").GetString());
        Assert.True(off.TryGetProperty("at", out _));
        var (_, neverSeen) = await GetInfo(r, "updates");                          // nie gelesen: offline ohne Daten
        Assert.Equal("offline", neverSeen.GetProperty("state").GetString());
        Assert.False(neverSeen.TryGetProperty("updates", out _));

        // Agent entfernt: no_agent und kein alter Stand mehr
        Assert.Equal(HttpStatusCode.NoContent, (await r.Http.SendAsync(Send(HttpMethod.Delete, $"/api/hosts/{r.Host}/agent", r.Cookie))).StatusCode);
        var (_, gone) = await GetInfo(r, "status");
        Assert.Equal("no_agent", gone.GetProperty("state").GetString());
        Assert.False(gone.TryGetProperty("info", out _));
    }

    [Fact]
    public async Task Needs_a_session_and_only_the_own_host_is_visible()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await RigAsync(db, app);
        var anonymous = await r.Http.GetAsync($"/api/hosts/{r.Host}/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var foreign = await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{Guid.NewGuid()}/updates", r.Cookie));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Empty(r.Agent.Requests);
    }

    [Fact]
    public async Task Agent_errors_become_short_codes_and_the_old_state_stays_visible()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton<TimeProvider>(new ManualTime(DateTimeOffset.UtcNow)));
        await using var r = await RigAsync(db, app);
        await GetInfo(r, "updates");

        r.Agent.HostReplies["/v1/host/updates"] = (500, """{"error":"apt_failed","detail":"E: Could not open lock file /var/lib/dpkg/lock"}""");
        var (_, e1) = await GetInfo(r, "updates", "?fresh=1");
        Assert.Equal("ok", e1.GetProperty("state").GetString());                  // der Speicher ist noch frisch: es wird nicht erneut gefragt
        ((ManualTime)app.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(120));
        var (_, e2) = await GetInfo(r, "updates");
        Assert.Equal("error", e2.GetProperty("state").GetString());
        Assert.Equal("apt_failed", e2.GetProperty("error").GetString());
        Assert.DoesNotContain("lock", e2.GetRawText());                           // der freie Text des Agenten geht nicht an den Browser
        Assert.True(e2.GetProperty("stale").GetBoolean());
        Assert.Equal(2, e2.GetProperty("updates").GetProperty("count").GetInt32());

        ((ManualTime)app.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(120));
        r.Agent.HostReplies["/v1/host/updates"] = (500, """{"error":"<script>alert(1)</script>"}""");
        Assert.Equal("agent_error", (await GetInfo(r, "updates")).Body.GetProperty("error").GetString());
        ((ManualTime)app.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(120));
        r.Agent.HostReplies["/v1/host/updates"] = (200, "[1,2,3]");
        Assert.Equal("invalid_answer", (await GetInfo(r, "updates")).Body.GetProperty("error").GetString());
        ((ManualTime)app.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(120));
        r.Agent.HostReplies["/v1/host/updates"] = (404, """{"error":"unsupported"}""");
        Assert.Equal("unsupported", (await GetInfo(r, "updates")).Body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Whatever_the_agent_sends_is_cleaned_cut_and_typed_before_it_reaches_the_browser()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await RigAsync(db, app);
        var manyPackages = string.Join(",", Enumerable.Range(0, 800).Select(i => $$"""{"name":"p{{i}}","from":"1","to":"2","security":false,"new":false}"""));
        r.Agent.HostReplies["/v1/host/updates"] = (200, $$"""{"manager":"apt","count":800,"securityKnown":true,"security":999999,"packages":[{{manyPackages}}],"keptBack":[1,"ok","a\u0000b"],"listsAgeSeconds":-5}""");
        r.Agent.HostReplies["/v1/host/sysinfo"] = (200, $$"""
            {"hostname":"evil\u0000\r\n{{new string('x', 500)}}","os":42,"load":["a",1e999,2],"cpuCount":-3,
             "memory":{"totalBytes":100,"availableBytes":999999999,"swapTotalBytes":-1},
             "disks":[{"mount":"\u001b[31m/","fs":"ext4","totalBytes":10,"usedBytes":1,"freeBytes":9,"percent":250},"nope",{"mount":"/x"}],
             "addresses":[{"address":"not an ip","family":4},{"address":"2001:db8::1","family":6,"prefix":999}],
             "virt":{"kind":"quantum","name":"x"},"proxmox":{"product":"evil"},"uptimeSeconds":"soon","rebootRequired":"yes"}
            """);
        var (_, u) = await GetInfo(r, "updates");
        var updates = u.GetProperty("updates");
        Assert.Equal(500, updates.GetProperty("packages").GetArrayLength());     // hoechstens 500
        Assert.True(updates.GetProperty("truncated").GetBoolean());
        Assert.Equal(800, updates.GetProperty("count").GetInt32());
        Assert.True(updates.GetProperty("security").GetInt32() <= updates.GetProperty("count").GetInt32());
        Assert.Equal(["ok", "ab"], updates.GetProperty("keptBack").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.True(updates.GetProperty("listsAgeSeconds").GetInt64() >= 0);

        var (_, s) = await GetInfo(r, "status");
        var info = s.GetProperty("info");
        var host = info.GetProperty("hostname").GetString()!;
        Assert.DoesNotContain('\0', host);
        Assert.DoesNotContain('\n', host);
        Assert.True(host.Length <= 64);
        Assert.False(info.TryGetProperty("os", out _));                           // falscher Typ: fehlt
        Assert.False(info.TryGetProperty("virt", out _));                         // unbekannte Art
        Assert.False(info.TryGetProperty("proxmox", out _));
        Assert.False(info.TryGetProperty("uptimeSeconds", out _));
        Assert.False(info.GetProperty("rebootRequired").GetBoolean());
        Assert.True(info.GetProperty("cpuCount").GetInt32() >= 0);
        var mem = info.GetProperty("memory");
        Assert.True(mem.GetProperty("availableBytes").GetInt64() <= mem.GetProperty("totalBytes").GetInt64());
        Assert.Equal(2, info.GetProperty("disks").GetArrayLength());              // der Text "nope" ist keine Platte
        Assert.Equal(100, info.GetProperty("disks")[0].GetProperty("percent").GetDouble());
        Assert.DoesNotContain('\u001b', info.GetProperty("disks")[0].GetProperty("mount").GetString()!);
        Assert.Single(info.GetProperty("addresses").EnumerateArray());            // "not an ip" faellt weg
        Assert.True(info.GetProperty("addresses")[0].GetProperty("prefix").GetInt32() <= 128);
        Assert.False(info.TryGetProperty("load", out _) && info.GetProperty("load").GetArrayLength() > 3);
    }

    [Fact]
    public async Task A_new_agent_starts_at_control_level_read_and_the_check_needs_server_and_agent_to_agree()
    {
        await using var db = await TestDb.CreateAsync();
        await db.ExecAsync("insert into users (name, password_hash, totp_secret) values ('u', 'x', 'y')");
        await db.ExecAsync("insert into hosts (user_id, name) select id, 'h' from users");
        await db.ExecAsync("insert into agents (host_id, user_id, token_hash) select h.id, h.user_id, 'x' from hosts h");
        Assert.Equal("read", await db.ScalarAsync<string>("select control from agents"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.ExecAsync("update agents set control = 'root'"));
        // Schreibaktionen gibt es noch nicht; die Pruefung ist fuer Stufe 3 da: lesen braucht nichts, jedes Recht braucht Server UND Agent
        Assert.False(AgentControl.Allows("read", "updates"));
        Assert.True(AgentControl.Allows("updates", "updates"));
        Assert.False(AgentControl.Allows("updates", "reboot"));
        Assert.True(AgentControl.Allows("full", "reboot"));
        Assert.False(AgentControl.Allows("full", "format-disk"));
        Assert.False(AgentControl.Allows(null, "updates"));
        Assert.False(AgentControl.Permitted("full", [], "reboot"));                // der Agent erlaubt es lokal nicht
        Assert.False(AgentControl.Permitted("read", ["updates"], "updates"));      // der Server erlaubt es nicht
        Assert.True(AgentControl.Permitted("updates", ["updates"], "updates"));
        Assert.False(AgentControl.IsLevel("root"));
    }
}
