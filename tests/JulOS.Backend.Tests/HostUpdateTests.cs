using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>
/// Robuste Host-Updates (docs/HOST-AGENT.md): Reparieren als eigene bestaetigte Aktion, Proxmox (nur als Ganzes, dist-upgrade), "full-upgrade" als ausdrueckliche Wahl, Fehlerzusammenfassung und
/// Protokoll im Ergebnis, Live-Zeilen, die Liste der Hintergrund-Aufgaben nach einem Neuladen, und die Platten-Gesundheit (ZFS nicht lesbar, SMART mit Temperatur). Der Agent ist ein Fake.
/// </summary>
public sealed partial class AgentLinkTests
{
    private const string ProxmoxUpdates = """
        {"manager":"apt","count":2,"security":0,"securityKnown":true,"truncated":false,"proxmox":true,"newPackages":1,
         "packages":[{"name":"pve-manager","from":"9.0.1","to":"9.0.11","security":false,"new":false},{"name":"proxmox-kernel-6.17","from":null,"to":"6.17.2-1","security":false,"new":true}],
         "keptBack":["proxmox-ve"],"held":[],"removals":0,"listsCheckedAt":1700000000,"listsAgeSeconds":60,"checkedAt":1700000060}
        """;
    private static readonly string[] FailureLog = [
        "Setting up libpve-common-perl (9.0.11) ...", "dpkg: error processing package pve-manager (--configure):", " dependency problems - leaving unconfigured",
        "Errors were encountered while processing:", " proxmox-mail-forward", " pve-manager", "E: Sub-process /usr/bin/dpkg returned an error code (1)"];

    private static string Body(FakeLinkAgent a, string path) { lock (a.Requests) return a.Requests.Last(q => q.Path == path).Body; }

    private static string[] StrList(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];

    // ------------------------------------------------------------------ Reparieren

    [Fact]
    public async Task Repair_is_its_own_confirmed_action_with_the_updates_word_and_no_typing()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true,"rebootRequired":false,"output":"fertig"}"""));
        var (_, p) = await Prepare(r, "updates.repair");
        Assert.False(p.TryGetProperty("typeToConfirm", out var t) && t.ValueKind == JsonValueKind.String);
        var (rs, run) = await Run(r, p.GetProperty("token").GetString()!, "updates.repair");
        Assert.Equal(HttpStatusCode.Accepted, rs);
        Assert.Equal("ok", (await WaitJob(r, run.GetProperty("job").GetString()!)).GetProperty("state").GetString());
        Assert.Equal("/v1/host/updates/repair", Posts(r.Agent).Single().Path);
        Assert.Contains("updates.repair ok", await Audit(db));
        Assert.Contains("updates.repair started", await Audit(db));
    }

    [Fact]
    public async Task Repair_needs_the_server_level_and_the_agents_word_like_every_update_action()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using (var r = await ActionRigAsync(db, app, control: "read"))
            Assert.Equal("control_off", Code((await Prepare(r, "updates.repair")).Body));
        await using var db2 = await TestDb.CreateAsync();
        await using var app2 = new Server(db2);
        await using var r2 = await ActionRigAsync(db2, app2, allow: new[] { "logs" });
        Assert.Equal("agent_not_permitted", Code((await Prepare(r2, "updates.repair")).Body));
        Assert.Empty(Posts(r2.Agent));
    }

    // ------------------------------------------------------------------ Proxmox und full-upgrade

    [Fact]
    public async Task On_proxmox_all_updates_is_a_full_upgrade_and_single_packages_are_refused()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.HostReplies["/v1/host/updates"] = (200, ProxmoxUpdates);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true,"full":true,"rebootRequired":true,"count":2}"""));

        var (st, single) = await Prepare(r, "updates.install", "pve-manager");
        Assert.Equal((HttpStatusCode.Conflict, "proxmox_whole"), (st, Code(single)));

        var (_, p) = await Prepare(r, "updates.install", "all");
        var opts = p.GetProperty("options");
        Assert.True(opts.GetProperty("proxmox").GetBoolean());
        Assert.True(opts.GetProperty("full").GetBoolean());
        Assert.False(opts.GetProperty("fullChoice").GetBoolean());                                  // auf Proxmox gibt es keine Wahl
        Assert.Equal(["proxmox_full"], StrList(p.GetProperty("warnings")));
        Assert.Equal("srv-01", p.GetProperty("typeToConfirm").GetString());                           // "alle Updates" bleibt mit getipptem Namen

        // auch ohne ausdruecklichen Wunsch (full fehlt in der Anfrage) laeuft dist-upgrade
        var (rs, run) = await Run(r, p.GetProperty("token").GetString()!, "updates.install", "all", typed: "srv-01");
        Assert.Equal(HttpStatusCode.Accepted, rs);
        var done = await WaitJob(r, run.GetProperty("job").GetString()!);
        Assert.True(done.GetProperty("full").GetBoolean());
        using var sent = JsonDocument.Parse(Body(r.Agent, "/v1/host/updates/install"));
        Assert.True(sent.RootElement.GetProperty("all").GetBoolean());
        Assert.True(sent.RootElement.GetProperty("full").GetBoolean());
        Assert.Contains("all (full)", await AuditRows(db, "select coalesce(target, '') from host_audit where event = 'started'"));
        // der Server schickt nie ein Paket auf Proxmox
        Assert.Single(Posts(r.Agent));
    }

    [Fact]
    public async Task Elsewhere_full_upgrade_is_an_explicit_option_that_is_off_by_default()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);                                          // die Beispielliste hat ein zurueckgehaltenes Paket
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true,"count":2}"""));
        var (_, p) = await Prepare(r, "updates.install", "all");
        var opts = p.GetProperty("options");
        Assert.True(opts.GetProperty("fullChoice").GetBoolean());
        Assert.False(opts.GetProperty("full").GetBoolean());
        Assert.False(opts.GetProperty("proxmox").GetBoolean());
        Assert.Equal(["linux-image-amd64"], StrList(opts.GetProperty("keptBack")));

        // ohne Haken: upgrade (kein "full" im Auftrag)
        var res = await r.Http.SendAsync(Post($"/api/hosts/{r.Host}/actions/run", new { token = p.GetProperty("token").GetString(), action = "updates.install", target = "all", typed = "srv-01" }, r.Cookie));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await WaitJob(r, (await Json(res)).GetProperty("job").GetString()!);
        Assert.DoesNotContain("full", Body(r.Agent, "/v1/host/updates/install"));

        // mit Haken: dist-upgrade
        var (_, p2) = await Prepare(r, "updates.install", "all");
        res = await r.Http.SendAsync(Post($"/api/hosts/{r.Host}/actions/run", new { token = p2.GetProperty("token").GetString(), action = "updates.install", target = "all", typed = "srv-01", full = true }, r.Cookie));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var job = await WaitJob(r, (await Json(res)).GetProperty("job").GetString()!);
        Assert.True(job.GetProperty("full").GetBoolean());
        using var sent = JsonDocument.Parse(Body(r.Agent, "/v1/host/updates/install"));
        Assert.True(sent.RootElement.GetProperty("full").GetBoolean());
    }

    [Fact]
    public async Task A_single_package_never_carries_full_and_all_updates_without_kept_back_packages_offers_no_choice()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.HostReplies["/v1/host/updates"] = (200, ProxmoxUpdates.Replace("\"proxmox\":true", "\"proxmox\":false").Replace("\"keptBack\":[\"proxmox-ve\"]", "\"keptBack\":[]"));
        var (_, p) = await Prepare(r, "updates.install", "all");
        Assert.False(p.GetProperty("options").GetProperty("fullChoice").GetBoolean());
        Assert.Empty(p.GetProperty("warnings").EnumerateArray());
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true,"count":1}"""));
        var (_, one) = await Prepare(r, "updates.install", "pve-manager");
        Assert.False(one.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object);
        var res = await r.Http.SendAsync(Post($"/api/hosts/{r.Host}/actions/run", new { token = one.GetProperty("token").GetString(), action = "updates.install", target = "pve-manager", full = true }, r.Cookie));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await WaitJob(r, (await Json(res)).GetProperty("job").GetString()!);
        Assert.DoesNotContain("full", Body(r.Agent, "/v1/host/updates/install"));
    }

    [Fact]
    public async Task Half_configured_packages_come_with_the_updates_and_only_real_package_names_count()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        r.Agent.HostReplies["/v1/host/updates"] = (200, ProxmoxUpdates.Replace("\"proxmox\":true", "\"proxmox\":true,\"broken\":[\"pve-manager\",\"x; rm -rf /\",\"proxmox-mail-forward\"],\"unreadable\":true"));
        var (_, u) = await GetInfo(r, "updates");
        var updates = u.GetProperty("updates");
        Assert.Equal(["pve-manager", "proxmox-mail-forward"], StrList(updates.GetProperty("broken")));
        Assert.True(updates.GetProperty("unreadable").GetBoolean());
        Assert.True(updates.GetProperty("proxmox").GetBoolean());
    }

    // ------------------------------------------------------------------ Fehlerzusammenfassung und Protokoll

    [Fact]
    public async Task A_failed_update_brings_the_summary_the_whole_log_and_the_offer_to_repair()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        var log = JsonSerializer.Serialize(FailureLog);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((500, $$$"""
            {"error":"dpkg_failed","needsRepair":true,"output":"Errors were encountered while processing:\n proxmox-mail-forward\n pve-manager","log":{{{log}}},
             "summary":{"errors":["E: Sub-process /usr/bin/dpkg returned an error code (1)"],"failedPackages":["pve-manager","proxmox-mail-forward","x; rm -rf /","$(id)"],"keptBack":["proxmox-ve"],"configQuestion":false,"locked":false,"installed":1}}
            """));
        var job = await Confirmed(r, "updates.install", "all", typed: "srv-01");
        Assert.Equal(("failed", "dpkg_failed"), (job.GetProperty("state").GetString(), job.GetProperty("error").GetString()));
        var res = job.GetProperty("result");
        Assert.True(res.GetProperty("needsRepair").GetBoolean());
        var sum = res.GetProperty("summary");
        Assert.Equal(["pve-manager", "proxmox-mail-forward"], StrList(sum.GetProperty("failedPackages")));      // nur echte Paketnamen
        Assert.Equal(["E: Sub-process /usr/bin/dpkg returned an error code (1)"], StrList(sum.GetProperty("errors")));
        Assert.Equal(["proxmox-ve"], StrList(sum.GetProperty("keptBack")));
        Assert.Equal(FailureLog, StrList(res.GetProperty("log")));                                         // Einrueckung bleibt
        Assert.Contains("updates.install failed dpkg_failed", await Audit(db));
        Assert.DoesNotContain("pve-manager", string.Join("\n", await Audit(db)));                           // der Verlauf bekommt keinen Inhalt
    }

    [Fact]
    public async Task The_log_is_bounded_and_hostile_text_stays_text()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        var many = Enumerable.Range(0, 2100).Select(i => $"line {i} \u0007 " + new string('x', 260)).ToArray();      // gross, aber unter der Grenze der Antwort (600 kB)
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, JsonSerializer.Serialize(new { ok = true, log = many, summary = new { errors = new[] { "E: <script>alert(1)</script>" }, failedPackages = Array.Empty<string>(), keptBack = Array.Empty<string>(), configQuestion = false, locked = false, installed = 1 } })));
        var job = await Confirmed(r, "updates.refresh");
        var log = StrList(job.GetProperty("result").GetProperty("log"));
        Assert.Equal(2000, log.Length);                                                                     // die ersten 2000 Zeilen
        Assert.All(log, l => { Assert.True(l.Length <= 240); Assert.DoesNotContain('\u0007', l); });
        Assert.Equal("E: <script>alert(1)</script>", StrList(job.GetProperty("result").GetProperty("summary").GetProperty("errors")).Single());      // als Text: der Browser maskiert
    }

    // ------------------------------------------------------------------ Hintergrund-Aufgaben

    [Fact]
    public async Task A_running_job_shows_its_live_lines_and_is_found_again_after_a_reload()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        var release = new TaskCompletionSource();
        r.Agent.ProgressLines = ["Get:1 http://deb.example/pool bash", "Setting up bash (5.2-2) ..."];
        r.Agent.OnHost = async (m, path, body, progress) =>
        {
            await progress("Setting up bash", 1, 2);
            await release.Task;
            return (200, """{"ok":true,"count":2}""");
        };
        var (_, p) = await Prepare(r, "updates.install", "all");
        var (_, run) = await Run(r, p.GetProperty("token").GetString()!, "updates.install", "all", typed: "srv-01");
        var job = run.GetProperty("job").GetString()!;
        await Eventually(async () => { var j = await Json(await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/hosts/{r.Host}/actions/{job}", r.Cookie))); return j.TryGetProperty("tail", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() == 2; });

        // "nach dem Neuladen": die Liste kennt den laufenden Auftrag mit Host, Fortschritt und Zeilen
        var list = await Json(await r.Http.SendAsync(Send(HttpMethod.Get, "/api/host-jobs", r.Cookie)));
        var item = list.GetProperty("jobs").EnumerateArray().Single();
        Assert.Equal((r.Host.ToString(), "Server"), (item.GetProperty("host").GetString(), item.GetProperty("hostName").GetString()));
        var j = item.GetProperty("job");
        Assert.Equal(("running", job, 1, 2), (j.GetProperty("state").GetString(), j.GetProperty("job").GetString(), j.GetProperty("done").GetInt32(), j.GetProperty("total").GetInt32()));
        Assert.Equal(["Get:1 http://deb.example/pool bash", "Setting up bash (5.2-2) ..."], StrList(j.GetProperty("tail")));

        release.SetResult();
        await WaitJob(r, job);
        // beendet: bleibt eine Stunde in der Liste (mit Ergebnis, ohne Zeilen der Live-Ansicht)
        var after = (await Json(await r.Http.SendAsync(Send(HttpMethod.Get, "/api/host-jobs", r.Cookie)))).GetProperty("jobs").EnumerateArray().Single().GetProperty("job");
        Assert.Equal("ok", after.GetProperty("state").GetString());
        Assert.False(after.TryGetProperty("tail", out var tail) && tail.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task The_job_list_needs_a_session_and_shows_only_writing_actions_of_this_user()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await r.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/host-jobs"))).StatusCode);
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true,"lines":["a"]}"""));
        await Confirmed(r, "logs.unit", "nginx.service");                                           // Logs lesen ist keine Aufgabe, die man in der Leiste verfolgt
        Assert.Empty((await Json(await r.Http.SendAsync(Send(HttpMethod.Get, "/api/host-jobs", r.Cookie)))).GetProperty("jobs").EnumerateArray());
    }

    // ------------------------------------------------------------------ Platten und Kernel

    [Fact]
    public async Task Zfs_that_cannot_be_read_is_unknown_with_a_short_message_and_a_real_pool_shows_its_lines()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton<TimeProvider>(new ManualTime(DateTimeOffset.UtcNow)));
        await using var r = await ActionRigAsync(db, app);
        string Sys(string zfs, string smart = """{"available":false,"reason":"no_access"}""") =>
            $$$"""{"hostname":"srv-01","memory":{},"health":{"smart":{{{smart}}},"md":[],"zfs":{{{zfs}}}},"rebootRequired":true,"newKernel":"6.17.2-1-pve"}""";
        async Task<JsonElement> Health(string zfs, string smart = """{"available":false,"reason":"no_access"}""")
        {
            r.Agent.HostReplies["/v1/host/sysinfo"] = (200, Sys(zfs, smart));
            ((ManualTime)app.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(120));         // der Zwischenspeicher gilt 60 s
            return (await GetInfo(r, "status", "?fresh=1")).Body.GetProperty("info");
        }
        var unknown = (await Health("""{"state":"unknown","healthy":false,"pools":[],"message":"The ZFS modules are not loaded."}""")).GetProperty("health").GetProperty("zfs");
        Assert.Equal(("unknown", "The ZFS modules are not loaded."), (unknown.GetProperty("state").GetString(), unknown.GetProperty("message").GetString()));
        Assert.Empty(unknown.GetProperty("pools").EnumerateArray());
        Assert.Equal("none", (await Health("""{"state":"none","healthy":true,"pools":[]}""")).GetProperty("health").GetProperty("zfs").GetProperty("state").GetString());
        // ein aelterer Agent (ohne state): nicht gesund und ohne Pool heisst jetzt "unknown", nicht "Pool ?"
        Assert.Equal("unknown", (await Health("""{"healthy":false,"pools":[]}""")).GetProperty("health").GetProperty("zfs").GetProperty("state").GetString());
        Assert.Equal("healthy", (await Health("""{"healthy":true,"pools":[]}""")).GetProperty("health").GetProperty("zfs").GetProperty("state").GetString());
        var bad = (await Health("""{"state":"problem","healthy":false,"pools":[{"name":"tank","state":"DEGRADED","scan":"scrub repaired 0B","errors":"No known data errors","status":"One or more devices could not be opened."}]}""")).GetProperty("health").GetProperty("zfs");
        var pool = bad.GetProperty("pools").EnumerateArray().Single();
        Assert.Equal(("problem", "tank", "DEGRADED", "scrub repaired 0B", "No known data errors"), (bad.GetProperty("state").GetString(), pool.GetProperty("name").GetString(), pool.GetProperty("state").GetString(), pool.GetProperty("scan").GetString(), pool.GetProperty("errors").GetString()));
    }

    [Fact]
    public async Task Smart_through_the_helper_shows_the_temperature_and_the_new_reasons_and_a_new_kernel_is_passed_on()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton<TimeProvider>(new ManualTime(DateTimeOffset.UtcNow)));
        await using var r = await ActionRigAsync(db, app);
        async Task<JsonElement> Info(string smart)
        {
            r.Agent.HostReplies["/v1/host/sysinfo"] = (200, $$"""{"hostname":"srv-01","memory":{},"health":{"smart":{{smart}},"md":[],"zfs":null},"rebootRequired":true,"newKernel":"6.17.2-1-pve\u0007"}""");
            ((ManualTime)app.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(120));
            var body = (await GetInfo(r, "status", "?fresh=1")).Body;
            Assert.True(body.TryGetProperty("info", out var inf), body.GetRawText());
            return inf;
        }
        var info = await Info("""{"available":true,"devices":[{"name":"/dev/sda","health":"ok","temp":34},{"name":"/dev/sdb","health":"failing","temp":9999},{"name":"/dev/sdc","health":"weird"}]}""");
        var devices = info.GetProperty("health").GetProperty("smart").GetProperty("devices").EnumerateArray().ToArray();
        Assert.Equal(34, devices[0].GetProperty("temp").GetInt32());
        Assert.False(devices[1].TryGetProperty("temp", out var bad) && bad.ValueKind == JsonValueKind.Number);          // unmoegliche Werte fallen weg
        Assert.Equal("unknown", devices[2].GetProperty("health").GetString());
        Assert.Equal("6.17.2-1-pve", info.GetProperty("newKernel").GetString());
        foreach (var reason in new[] { "helper_unavailable", "read_failed", "no_smartctl", "no_access" })
            Assert.Equal(reason, (await Info($$"""{"available":false,"reason":"{{reason}}"}""")).GetProperty("health").GetProperty("smart").GetProperty("reason").GetString());
        Assert.False((await Info("""{"available":false,"reason":"rm -rf"}""")).GetProperty("health").GetProperty("smart").TryGetProperty("reason", out var odd) && odd.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task The_word_disks_is_passed_on_in_allow_like_the_others()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app, allow: new[] { "updates", "disks", "format" });
        var hosts = await Json(await r.Http.SendAsync(Send(HttpMethod.Get, "/api/hosts", r.Cookie)));
        Assert.Equal(["updates", "disks"], hosts.GetProperty("hosts").EnumerateArray().Single().GetProperty("agent").GetProperty("allow").EnumerateArray().Select(x => x.GetString()).ToArray().OrderByDescending(x => x == "updates").ToArray());
    }
}
