using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JulOS.Frontend.Agent;
using JulOS.Integrations.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>
/// Aktualisierung des Agenten: der Server kennt die mitgelieferte Version, schickt dem verbundenen Agenten den festen Pfad und die Pruefsumme, und zeigt an, was der Agent meldet.
/// Der Agent ist ein Fake im Speicher; er verhaelt sich bei "update" wie der echte (tools/julos-agent.py: nur der feste Pfad, Datei vom eigenen Server holen, Summe pruefen),
/// dessen eigene Pruefungen (falsche Summe, fremde Adresse, Weiterleitung, atomarer Ersatz) in tests/agent stehen.
/// </summary>
public sealed partial class AgentLinkTests
{
    private const string FixedPath = "/agent-files/julos-agent.py";

    private sealed class ReleaseDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "julos-release-" + Guid.NewGuid().ToString("N"));
        public ReleaseDir(string version) { Directory.CreateDirectory(Path); Write(version); }
        public void Write(string version, string extra = "") => File.WriteAllText(System.IO.Path.Combine(Path, "julos-agent.py"), $"#!/usr/bin/env python3\nAGENT_VERSION = \"{version}\"\n{extra}");
        public string Sha256() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(System.IO.Path.Combine(Path, "julos-agent.py"))));
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static Server ServerWithRelease(TestDb db, ReleaseDir release) => new(db, s => s.AddSingleton(new AgentRelease(release.Path)));

    /// <summary>Der Agent, wie ihn tools/julos-agent.py verhaelt: fremde Adressen ablehnen, die Datei vom eigenen Server holen, die Summe pruefen.</summary>
    private static Func<FakeLinkAgent.UpdateRequest, Task<(int, string)>> HonestAgent(HttpClient ownServer, Action? beforeDownload = null) => async u =>
    {
        if (u.Path != FixedPath) return (400, """{"error":"Adresse nicht erlaubt"}""");
        beforeDownload?.Invoke();
        var bytes = await ownServer.GetByteArrayAsync(u.Path);
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(u.Sha256, StringComparison.Ordinal)) return (422, """{"error":"Pruefsumme stimmt nicht: Datei verworfen"}""");
        return (200, $$"""{"ok":true,"version":"{{u.Version}}","restarting":true}""");
    };

    private static HttpRequestMessage PutJson(string url, object body, string? cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-JulOS", "1");
        req.Headers.Add("Origin", "http://localhost");
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static HttpRequestMessage PostJson(string url, object body, string cookie) => Post(url, body, cookie);

    private sealed record Enrolled(Server App, HttpClient Http, string Cookie, Guid Connection, Guid Host, string Token);

    private static async Task<Enrolled> EnrollAsync(Server app, TestDb db, string name = "VM")
    {
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var conn = await ConnectionOf(db, user, name);
        var token = await TokenFor(c, await CodeFor(c, cookie, conn));
        return new Enrolled(app, c, cookie, conn, HostOfConnection[conn], token);
    }

    [Fact]
    public async Task An_update_sends_the_fixed_path_and_the_files_checksum_and_the_agent_returns_with_the_new_version()
    {
        using var release = new ReleaseDir("0.9.0");
        await using var db = await TestDb.CreateAsync();
        await using var app = ServerWithRelease(db, release);
        var e = await EnrollAsync(app, db);
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0");
        agent.OnUpdate = HonestAgent(app.Http());
        await Eventually(() => Online(app, e.Connection));

        var before = await AgentOf(e.Http, e.Cookie, e.Host);
        Assert.Equal("0.4.0", before.GetProperty("version").GetString());
        Assert.Equal("0.9.0", before.GetProperty("latest").GetString());
        Assert.True(before.GetProperty("updateAvailable").GetBoolean());
        Assert.True(before.GetProperty("canUpdate").GetBoolean());
        Assert.False(before.TryGetProperty("autoUpdate", out var auto) && auto.GetBoolean());       // Standard: aus

        var r = await e.Http.SendAsync(PostJson($"/api/hosts/{e.Host}/agent/update", new { }, e.Cookie));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("0.9.0", (await Json(r)).GetProperty("version").GetString());

        var sent = Assert.Single(agent.Updates);
        Assert.Equal(FixedPath, sent.Path);                                    // nie eine volle Adresse
        Assert.Equal("0.9.0", sent.Version);
        Assert.Equal(release.Sha256(), sent.Sha256);                           // Summe der Datei, die der Server selbst ausliefert
        Assert.Equal(release.Sha256(), Convert.ToHexStringLower(SHA256.HashData(await e.Http.GetByteArrayAsync(FixedPath))));

        // der Agent startet neu und meldet die neue Version: kein Update mehr offen
        await agent.DisposeAsync();
        await Eventually(() => !Online(app, e.Connection));
        await using var again = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.9.0");
        await Eventually(() => Online(app, e.Connection));
        await Eventually(async () => (await AgentOf(e.Http, e.Cookie, e.Host)).GetProperty("version").GetString() == "0.9.0");
        var after = await AgentOf(e.Http, e.Cookie, e.Host);
        Assert.False(after.GetProperty("updateAvailable").GetBoolean());
        Assert.True(after.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task A_wrong_checksum_is_refused_by_the_agent_and_the_server_reports_it_without_claiming_success()
    {
        using var release = new ReleaseDir("0.9.0");
        await using var db = await TestDb.CreateAsync();
        await using var app = ServerWithRelease(db, release);
        var e = await EnrollAsync(app, db);
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0");
        // zwischen Anweisung und Download aendert sich die Datei (oder jemand schiebt dem Agenten etwas anderes unter): die Summe passt nicht mehr
        agent.OnUpdate = HonestAgent(app.Http(), beforeDownload: () => release.Write("0.9.0", "print('untergeschoben')\n"));
        await Eventually(() => Online(app, e.Connection));

        var r = await e.Http.SendAsync(PostJson($"/api/hosts/{e.Host}/agent/update", new { }, e.Cookie));
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("Pruefsumme", (await Json(r)).GetProperty("error").GetString());
        Assert.Single(agent.Updates);
        Assert.True(Online(app, e.Connection));                               // der Agent bleibt verbunden und in der alten Version
        var a = await AgentOf(e.Http, e.Cookie, e.Host);
        Assert.Equal("0.4.0", a.GetProperty("version").GetString());
        Assert.True(a.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task A_foreign_address_is_never_sent_and_would_be_refused_by_the_agent()
    {
        using var release = new ReleaseDir("0.9.0");
        await using var db = await TestDb.CreateAsync();
        await using var app = ServerWithRelease(db, release);
        var e = await EnrollAsync(app, db);
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0");
        agent.OnUpdate = HonestAgent(app.Http());
        await Eventually(() => Online(app, e.Connection));

        // der Browser kann weder Adresse noch Version noch Summe vorgeben: der Koerper wird nicht gelesen
        var r = await e.Http.SendAsync(PostJson($"/api/hosts/{e.Host}/agent/update", new { path = "https://evil.example/agent.py", url = "https://evil.example/agent.py", sha256 = new string('0', 64), version = "9.9.9" }, e.Cookie));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var sent = Assert.Single(agent.Updates);
        Assert.Equal(FixedPath, sent.Path);
        Assert.Equal("0.9.0", sent.Version);
        Assert.Equal(release.Sha256(), sent.Sha256);

        // Gegenprobe auf der Leitung: eine fremde Adresse wuerde der Agent ablehnen (der echte: tests/agent, test_only_the_own_server_and_the_fixed_path_are_allowed)
        var link = app.Services.GetRequiredService<AgentLinks>().Get(e.Host, await db.ScalarAsync<Guid>("select id from users limit 1"))!;
        var reply = await link.UpdateAsync("https://evil.example" + FixedPath, release.Sha256(), "0.9.0", CancellationToken.None);
        Assert.Equal(400, reply.Status);
    }

    [Fact]
    public async Task An_update_is_refused_when_there_is_no_agent_it_is_offline_too_old_current_or_not_the_users()
    {
        using var release = new ReleaseDir("0.9.0");
        await using var db = await TestDb.CreateAsync();
        await using var app = ServerWithRelease(db, release);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var other = await db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreign = await ConnectionOf(db, other, "fremd");
        var bare = await ConnectionOf(db, user, "ohne Agent");
        var conn = await ConnectionOf(db, user);
        string Url(Guid connection) => $"/api/hosts/{HostOfConnection[connection]}/agent/update";

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post(Url(conn), new { }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Post(Url(foreign), new { }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Post(Url(bare), new { }, cookie))).StatusCode);              // kein Agent eingerichtet

        var token = await TokenFor(c, await CodeFor(c, cookie, conn), version: "0.3.0");
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Post(Url(conn), new { }, cookie))).StatusCode);              // eingerichtet, aber nicht verbunden

        await using (var old = await FakeLinkAgent.ConnectAsync(app.Server, token, version: "0.3.0"))
        {
            await Eventually(() => Online(app, conn));
            var a = await AgentOf(c, cookie, HostOfConnection[conn]);
            Assert.True(a.GetProperty("updateAvailable").GetBoolean());
            Assert.False(a.GetProperty("canUpdate").GetBoolean());                                        // 0.3.0 kennt "update" noch nicht
            var r = await c.SendAsync(Post(Url(conn), new { }, cookie));
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Contains("neu einrichten", (await Json(r)).GetProperty("error").GetString());
            Assert.Empty(old.Updates);
        }
        await Eventually(() => !Online(app, conn));

        await using (var current = await FakeLinkAgent.ConnectAsync(app.Server, token, version: "0.9.0"))
        {
            await Eventually(() => Online(app, conn));
            var r = await c.SendAsync(Post(Url(conn), new { }, cookie));
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Contains("aktuell", (await Json(r)).GetProperty("error").GetString());
            Assert.Empty(current.Updates);
            Assert.False((await AgentOf(c, cookie, HostOfConnection[conn])).GetProperty("updateAvailable").GetBoolean());
        }
    }

    [Fact]
    public async Task Without_an_agent_file_on_the_server_there_is_nothing_to_offer()
    {
        var empty = Path.Combine(Path.GetTempPath(), "julos-release-leer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, s => s.AddSingleton(new AgentRelease(empty)));
            var e = await EnrollAsync(app, db);
            await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0");
            await Eventually(() => Online(app, e.Connection));
            var a = await AgentOf(e.Http, e.Cookie, e.Host);
            Assert.False(a.GetProperty("updateAvailable").GetBoolean());
            Assert.False(a.TryGetProperty("latest", out _));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await e.Http.SendAsync(PostJson($"/api/hosts/{e.Host}/agent/update", new { }, e.Cookie))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await e.Http.GetAsync(FixedPath)).StatusCode);
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public async Task The_automatic_update_is_off_by_default_belongs_to_the_owner_and_runs_on_connect_only_when_switched_on()
    {
        using var release = new ReleaseDir("0.9.0");
        await using var db = await TestDb.CreateAsync();
        await using var app = ServerWithRelease(db, release);
        var e = await EnrollAsync(app, db);
        var other = await db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreign = await ConnectionOf(db, other, "fremd");
        string AutoUrl(Guid host) => $"/api/hosts/{host}/agent/auto-update";

        // aus: ein veralteter Agent bleibt, wie er ist
        await using (var quiet = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0"))
        {
            await Eventually(() => Online(app, e.Connection));
            await Task.Delay(300);
            Assert.Empty(quiet.Updates);
            Assert.False((await AgentOf(e.Http, e.Cookie, e.Host)).TryGetProperty("autoUpdate", out var off) && off.GetBoolean());
        }
        await Eventually(() => !Online(app, e.Connection));

        Assert.Equal(HttpStatusCode.Unauthorized, (await e.Http.SendAsync(PutJson(AutoUrl(e.Host), new { enabled = true }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Http.SendAsync(PutJson(AutoUrl(HostOfConnection[foreign]), new { enabled = true }, e.Cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await e.Http.SendAsync(PutJson(AutoUrl(e.Host), new { enabled = true }, e.Cookie))).StatusCode);
        Assert.True((await AgentOf(e.Http, e.Cookie, e.Host)).GetProperty("autoUpdate").GetBoolean());

        // an: beim Verbinden wird der veraltete Agent aktualisiert (mit derselben Anweisung wie vom Knopf)
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0");
        await Eventually(() => agent.Updates.Count == 1);
        Assert.Equal(FixedPath, agent.Updates[0].Path);
        Assert.Equal(release.Sha256(), agent.Updates[0].Sha256);
        await agent.DisposeAsync();
        await Eventually(() => !Online(app, e.Connection));

        // schlug es fehl und er meldet sich wieder veraltet, versucht der Server es nicht sofort erneut (keine Schleife)
        await using var again = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0");
        await Eventually(() => Online(app, e.Connection));
        await Task.Delay(400);
        Assert.Empty(again.Updates);

        // wieder aus
        Assert.Equal(HttpStatusCode.OK, (await e.Http.SendAsync(PutJson(AutoUrl(e.Host), new { enabled = false }, e.Cookie))).StatusCode);
        Assert.False((await AgentOf(e.Http, e.Cookie, e.Host)).GetProperty("autoUpdate").GetBoolean());
    }

    [Fact]
    public async Task Switching_the_automatic_update_on_updates_an_outdated_connected_agent_right_away()
    {
        using var release = new ReleaseDir("0.9.0");
        await using var db = await TestDb.CreateAsync();
        await using var app = ServerWithRelease(db, release);
        var e = await EnrollAsync(app, db);
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, e.Token, version: "0.4.0");
        await Eventually(() => Online(app, e.Connection));
        await Task.Delay(200);
        Assert.Empty(agent.Updates);
        Assert.Equal(HttpStatusCode.OK, (await e.Http.SendAsync(PutJson($"/api/hosts/{e.Host}/agent/auto-update", new { enabled = true }, e.Cookie))).StatusCode);
        await Eventually(() => agent.Updates.Count == 1);
    }
}
