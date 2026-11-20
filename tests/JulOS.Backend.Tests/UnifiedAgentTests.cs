using System.Net;
using System.Text.Json;
using JulOS.Backend.Logic;
using JulOS.Integrations.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>
/// Der EINE Agent (0.7.0, docs/HOST-AGENT.md): ein Desktop-Agent (Plattform linux-x11) meldet zusaetzlich "host" und die Host-Faehigkeiten und bekommt dann genau dieselben Anfragen und Aktionen wie ein
/// Host-Agent: der Server richtet sich nach den gemeldeten Faehigkeiten, nicht nach dem Plattformnamen. Ein alter Desktop-Agent (0.5.x/0.6.0, ohne Woerter) bleibt gueltig, liefert keine Systemwerte
/// und der Server nennt den Grund (hostSupport "update"). Der Agent ist ein Fake im Speicher, der Server echt.
/// </summary>
public sealed partial class AgentLinkTests
{
    private static readonly string[] DesktopHostCaps = ["desktop", "host", "sysinfo", "updates", "services", "docker"];

    private static string[] Strings(JsonElement list) => [.. list.EnumerateArray().Select(x => x.GetString()!)];

    [Fact]
    public async Task A_desktop_agent_with_host_capabilities_shows_system_values_updates_services_and_docker_like_a_host_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await RigAsync(db, app, DesktopHostCaps, new[] { "updates" }, version: "0.7.0", platform: "linux-x11");

        var agent = await AgentOf(r.Http, r.Cookie, r.Host);
        Assert.Equal("desktop", agent.GetProperty("kind").GetString());                  // die Plattform bleibt linux-x11
        Assert.Equal("linux-x11", agent.GetProperty("platform").GetString());
        Assert.Equal(DesktopHostCaps, Strings(agent.GetProperty("caps")));
        Assert.Equal("yes", agent.GetProperty("hostSupport").GetString());
        Assert.Equal(["updates"], Strings(agent.GetProperty("allow")));

        var (_, s) = await GetInfo(r, "status");
        Assert.Equal("ok", s.GetProperty("state").GetString());
        Assert.Equal("srv-01", s.GetProperty("info").GetProperty("hostname").GetString());
        var (_, u) = await GetInfo(r, "updates");
        Assert.Equal(2, u.GetProperty("updates").GetProperty("count").GetInt32());
        var (_, sv) = await GetInfo(r, "services");
        Assert.Equal("nginx.service", sv.GetProperty("services").GetProperty("failed")[0].GetProperty("unit").GetString());
        var (_, d) = await GetInfo(r, "docker");
        Assert.Equal(2, d.GetProperty("docker").GetProperty("total").GetInt32());
        lock (r.Agent.Requests) Assert.All(r.Agent.Requests, q => { Assert.Equal("GET", q.Method); Assert.StartsWith("/v1/host/", q.Path); });     // dieselben festen Pfade
    }

    [Fact]
    public async Task A_desktop_agent_with_host_capabilities_allows_actions_with_the_same_keys_as_a_host_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await ActionRigAsync(db, app, version: "0.7.0", allow: new[] { "updates", "logs" }, caps: DesktopHostCaps, platform: "linux-x11");
        r.Agent.OnHost = (m, path, body, progress) => Task.FromResult((200, """{"ok":true}"""));

        var (status, p) = await Prepare(r, "updates.refresh");
        Assert.Equal(HttpStatusCode.OK, status);
        var (rs, run) = await Run(r, p.GetProperty("token").GetString()!, "updates.refresh");
        Assert.Equal(HttpStatusCode.Accepted, rs);
        Assert.Equal("ok", (await WaitJob(r, run.GetProperty("job").GetString()!)).GetProperty("state").GetString());
        Assert.Single(Posts(r.Agent), q => q.Path == "/v1/host/updates/refresh");

        // zweiter Schluessel: was der Desktop-Agent lokal nicht erlaubt, geht auch hier nicht
        var (denied, body) = await Prepare(r, "power.reboot");
        Assert.Equal((HttpStatusCode.Forbidden, "agent_not_permitted"), (denied, Code(body)));
        Assert.Single(Posts(r.Agent));

        // erster Schluessel: Stufe am Server
        Assert.Equal(HttpStatusCode.OK, (await Control(r, "read")).StatusCode);
        Assert.Equal("control_off", Code((await Prepare(r, "updates.refresh")).Body));
    }

    [Fact]
    public async Task An_old_desktop_agent_stays_valid_gets_no_host_requests_and_the_server_names_the_reason()
    {
        foreach (var version in new[] { "0.5.1", "0.6.0" })
        {
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db);
            await using var r = await RigAsync(db, app, caps: Array.Empty<string>(), version: version, platform: "linux-x11");
            var agent = await AgentOf(r.Http, r.Cookie, r.Host);
            Assert.Equal(["desktop"], Strings(agent.GetProperty("caps")));
            Assert.Equal("update", agent.GetProperty("hostSupport").GetString());
            Assert.True(agent.GetProperty("canUpdate").GetBoolean());                       // ein Selbst-Update ist moeglich (ab 0.4.0)
            Assert.False(agent.GetProperty("outdated").GetBoolean());                       // und der Agent wird weiter angenommen
            foreach (var what in new[] { "status", "updates", "services", "docker" })
            {
                var (code, body) = await GetInfo(r, what);
                Assert.Equal(HttpStatusCode.OK, code);                                       // kein Fehler: der klare Zustand "unsupported"
                Assert.Equal("unsupported", body.GetProperty("state").GetString());
            }
            Assert.Empty(r.Agent.Requests);                                                  // der alte Agent bekommt nichts Unbekanntes
            await Assert_actions_are_refused(r);
        }
    }

    private static async Task Assert_actions_are_refused(HostRig r)
    {
        await Control(r, "full");
        var (status, body) = await Prepare(r, "updates.refresh");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains(Code(body), new[] { "agent_outdated", "unsupported" });
        Assert.Empty(Posts(r.Agent));
    }

    [Fact]
    public async Task A_new_desktop_agent_without_host_abilities_shows_none()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        // neu genug, aber nichts davon gemeldet (zum Beispiel kein Linux): none
        await using var r = await RigAsync(db, app, caps: new[] { "desktop" }, version: "0.7.0", platform: "linux-x11");
        Assert.Equal("none", (await AgentOf(r.Http, r.Cookie, r.Host)).GetProperty("hostSupport").GetString());
    }

    [Fact]
    public async Task A_host_agent_from_before_0_7_0_gets_the_host_word_added_and_no_desktop_word()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        await using var r = await RigAsync(db, app, caps: AllCaps, version: "0.6.0", platform: "linux-host");
        var a = await AgentOf(r.Http, r.Cookie, r.Host);
        Assert.Equal("yes", a.GetProperty("hostSupport").GetString());
        Assert.Equal("host", a.GetProperty("kind").GetString());
        Assert.Equal(["host", "sysinfo", "updates", "services", "docker"], Strings(a.GetProperty("caps")));
    }

    [Fact]
    public async Task An_offline_agent_shows_unknown_or_update_from_its_last_known_version()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var old = await RigAsync(db, app, caps: Array.Empty<string>(), version: "0.6.0", platform: "linux-x11");
        await old.Agent.DisposeAsync();
        await Eventually(() => !app.Services.GetRequiredService<AgentLinks>().IsOnline(old.Host));
        var a = await AgentOf(old.Http, old.Cookie, old.Host);
        Assert.False(a.GetProperty("online").GetBoolean());
        Assert.Equal("update", a.GetProperty("hostSupport").GetString());
        Assert.Empty(a.GetProperty("caps").EnumerateArray());
    }

    [Fact]
    public void Capabilities_are_normalised_and_the_support_rule_is_stable()
    {
        Assert.Equal(["desktop"], AgentLabels.NormalizeCaps("linux-x11", []));
        Assert.Equal(["host", "sysinfo"], AgentLabels.NormalizeCaps("linux-host", ["sysinfo"]));
        Assert.Equal(["desktop", "host", "sysinfo", "updates"], AgentLabels.NormalizeCaps("linux-x11", ["updates", "sysinfo", "host", "desktop"]));
        Assert.Equal([], AgentLabels.NormalizeCaps("linux-host", []));
        Assert.Equal("yes", AgentLabels.HostSupport("linux-x11", "0.7.0", ["desktop", "host"]));
        Assert.Equal("update", AgentLabels.HostSupport("linux-x11", "0.6.0", ["desktop"]));
        Assert.Equal("update", AgentLabels.HostSupport("linux-x11", "0.5.2", null));
        Assert.Equal("unknown", AgentLabels.HostSupport("linux-x11", "0.7.0", null));
        Assert.Equal("none", AgentLabels.HostSupport("linux-x11", "0.7.0", ["desktop"]));
        Assert.Equal("unknown", AgentLabels.HostSupport("linux-host", "0.5.0", null));
        Assert.Equal("unknown", AgentLabels.HostSupport("linux-x11", null, null));
    }

    [Fact]
    public void An_agent_from_0_4_on_can_update_itself_to_0_7_0_and_the_update_is_offered()
    {
        foreach (var v in new[] { "0.4.0", "0.5.0", "0.5.9", "0.6.0" })
        {
            Assert.True(AgentLinks.CanUpdate(v), v);
            Assert.True(AgentLinks.VersionOk(v), v);
            Assert.True(AgentLinks.IsNewer("0.7.0", v), v);
        }
        Assert.False(AgentLinks.CanUpdate("0.3.0"));
        Assert.Equal(new Version(0, 7, 0), AgentLabels.MinDesktopHostVersion);
        // die mitgelieferte Datei ist genau diese Version
        Assert.Equal("0.9.0", new JulOS.Frontend.Agent.AgentRelease().Latest()?.Version);
        Assert.Equal(new Version(0, 6, 0), HostActions.MinAgentVersion);                    // Aktionen gibt es weiterhin ab 0.6.0, wenn der Agent die Woerter meldet
    }

    [Fact]
    public async Task The_installer_is_served_with_a_checksum_and_the_old_ones_still_are()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var installer = await c.GetStringAsync("/agent-files/install-agent.sh");
        Assert.StartsWith("#!/bin/sh", installer);
        foreach (var name in new[] { "install-agent.sh", "install-host-agent.sh", "install-linux-agent.sh", "julos-agent.py", "julos-agent-helper.py", "julos-clip.py" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/agent-files/" + name)).StatusCode);
        var sums = (await c.GetStringAsync("/agent-files/SHA256SUMS")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split("  ")).ToDictionary(p => p[1], p => p[0]);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(installer))), sums["install-agent.sh"]);
    }
}
