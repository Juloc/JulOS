using System.Net;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Arbeitsplatz je Geraet (docs/MULTI-SCREEN.md): Regeln des Dokuments, fremde Daten, Versionspruefung, Grenzen. Keine Optik.</summary>
public sealed class WorkspaceTests
{
    private const string Device = "dev-aaaaaaaa-1111";
    private const string Mine = "3f2b1c9e-1111-4222-8333-444455556666";

    private static string Doc(string windows = "[]", string screens = """[{"id":"s1","x":0,"y":0,"w":1920,"h":1080}]""", string extra = "") =>
        $$"""{"screens":{{screens}},"windows":{{windows}}{{extra}}}""";

    private static string Win(string id, string kind = "app", string who = "\"app\":\"files\"", string state = "normal", string extra = "") =>
        $$"""{"id":"{{id}}","kind":"{{kind}}",{{who}},"x":10,"y":20,"w":800,"h":600,"z":1,"state":"{{state}}"{{extra}}}""";

    [Fact]
    public void The_document_keeps_known_fields_rounds_and_limits_numbers()
    {
        var w1 = Win("w1", extra: ",\"password\":\"geheim\",\"title\":\"x\",\"snap\":\"left\",\"restore\":{\"x\":1,\"y\":2,\"w\":300,\"h\":200,\"q\":1}");
        var w2 = Win("w2", "connection", $"\"connectionId\":\"{Mine}\"", "snapped");
        var good = $$"""
            {"screens":[{"id":"s1","x":-1920,"y":0.4,"w":1920,"h":1080,"secret":"x"}],
             "windows":[{{w1}},{{w2}}],
             "focus":"w2","dock":{"collapsed":true,"side":"left","order":["w1","files"],"evil":1},"evil":true}
            """;
        var (doc, error) = WorkspaceRules.Parse(J(good));
        Assert.Null(error);
        var json = WorkspaceRules.Write(doc!);
        Assert.DoesNotContain("geheim", json);
        Assert.DoesNotContain("evil", json);
        Assert.DoesNotContain("title", json);
        Assert.Equal("""{"id":"s1","x":-1920,"y":0,"w":1920,"h":1080}""", JsonDocument.Parse(json).RootElement.GetProperty("screens")[0].GetRawText());
        Assert.Equal("w2", doc!.Focus);
        Assert.Equal(Guid.Parse(Mine), doc.Windows[1].ConnectionId);
        Assert.Equal("left", doc.Windows[0].Snap);
        Assert.Equal(300, doc.Windows[0].Restore!.W);

        // Zahlen ausserhalb des Bereichs werden begrenzt, Bruchteile gerundet
        var (big, _) = WorkspaceRules.Parse(J("""{"windows":[{"id":"w","kind":"app","app":"files","x":-9e9,"y":1.6,"w":0,"h":9e9,"z":-5,"state":"max"}]}"""));
        var r = big!.Windows[0];
        Assert.Equal((-WorkspaceRules.CoordLimit, 2, 1, WorkspaceRules.SizeLimit, 0), (r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H, r.Z));
    }

    [Fact]
    public void Bad_documents_are_refused()
    {
        string Many(int n, Func<int, string> one) => "[" + string.Join(",", Enumerable.Range(0, n).Select(one)) + "]";
        var bad = new[]
        {
            "[1]", "\"x\"", "null",
            Doc(Many(WorkspaceRules.MaxWindows + 1, i => Win("w" + i))),                                              // zu viele Fenster
            Doc(screens: Many(WorkspaceRules.MaxScreens + 1, i => $$"""{"id":"s{{i}}","x":0,"y":0,"w":1,"h":1}""")),   // zu viele Bildschirme
            Doc($"[{Win("w1")},{Win("w1")}]"),                                                                        // doppelte Kennung
            Doc(screens: """[{"id":"s","x":0,"y":0,"w":1,"h":1},{"id":"s","x":0,"y":0,"w":1,"h":1}]"""),
            Doc($"[{Win("w 1")}]"), Doc($"[{Win(new string('a', 65))}]"),                                             // Kennung ungueltig
            Doc($"[{Win("w1", "script")}]"), Doc($"[{Win("w1", state: "full")}]"),                                    // Art, Zustand
            Doc($"[{Win("w1", who: "\"app\":\"Files!\"")}]"), Doc($"[{Win("w1", who: "\"x\":1")}]"),                   // App fehlt oder ungueltig
            Doc($"[{Win("w1", "connection", "\"connectionId\":\"nicht-uuid\"")}]"), Doc($"[{Win("w1", "connection", "\"app\":\"files\"")}]"),
            Doc($"[{Win("w1", extra: ",\"snap\":\"a b\"")}]"),
            Doc("""[{"id":"w1","kind":"app","app":"files","x":"1","y":0,"w":1,"h":1,"z":0,"state":"normal"}]"""),     // Zahl als Text
            Doc("""[{"id":"w1","kind":"app","app":"files","y":0,"w":1,"h":1,"z":0,"state":"normal"}]"""),            // x fehlt
            Doc(extra: ",\"focus\":\"a b\""), Doc(extra: ",\"dock\":{\"side\":\"mitte\"}"), Doc(extra: ",\"dock\":{\"order\":[1]}"),
            Doc(extra: ",\"dock\":{\"order\":" + Many(WorkspaceRules.MaxDockOrder + 1, i => "\"a" + i + "\"") + "}"),
        };
        foreach (var b in bad) Assert.NotNull(WorkspaceRules.Parse(J(b)).Error);
        Assert.Null(WorkspaceRules.Parse(J("{}")).Error);       // leer ist ein gueltiger Arbeitsplatz
    }

    [Fact]
    public void Windows_of_foreign_connections_are_dropped_and_the_focus_follows()
    {
        var other = Guid.NewGuid();
        var (doc, _) = WorkspaceRules.Parse(J(Doc($"[{Win("mine", "connection", $"\"connectionId\":\"{Mine}\"")},{Win("theirs", "connection", $"\"connectionId\":\"{other}\"")},{Win("app")}]", extra: ",\"focus\":\"theirs\"")));
        var kept = WorkspaceRules.Keep(doc!, new HashSet<Guid> { Guid.Parse(Mine) });
        Assert.Equal(["mine", "app"], kept.Windows.Select(w => w.Id));
        Assert.Null(kept.Focus);
        Assert.Equal([Guid.Parse(Mine), other], WorkspaceRules.ConnectionIds(doc!));
    }

    [Fact]
    public void Device_ids_and_names_are_checked()
    {
        foreach (var ok in new[] { "abcdefgh", "3f2b1c9e-1111-4222-8333-444455556666", new string('a', 64) }) Assert.True(WorkspaceRules.IsDeviceId(ok));
        foreach (var bad in new[] { null, "", "short", "has space!", "../../etc/pass", new string('a', 65), "ümlaut-ümlaut" }) Assert.False(WorkspaceRules.IsDeviceId(bad));
        Assert.Equal("Laptop", WorkspaceRules.CleanName(" Lap\u0000top "));
        Assert.Equal(WorkspaceRules.MaxNameLength, WorkspaceRules.CleanName(new string('x', 200))!.Length);
        Assert.Null(WorkspaceRules.CleanName(null));
    }

    private static async Task<(string Cookie, Guid Id)> SecondUserAsync(TestDb db)
    {
        var id = await Insert(db, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var token = await new SessionStore(db.Data).CreateAsync(id, "127.0.0.1", "test");
        return ("julos_sid=" + token, id);
    }

    private static object Save(int version, string doc, string? name = null) => new { version, name, doc = J(doc) };

    [Fact]
    public async Task Every_workspace_route_needs_a_session_and_the_special_header()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var routes = new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/api/workspaces", null), (HttpMethod.Get, "/api/workspaces/" + Device, null),
            (HttpMethod.Put, "/api/workspaces/" + Device, Save(0, "{}")), (HttpMethod.Delete, "/api/workspaces/" + Device, null),
        };
        foreach (var (method, url, body) in routes) Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(method, url, null, body))).StatusCode);

        var (cookie, _) = await SignedInAsync(c, db);
        var bare = new HttpRequestMessage(HttpMethod.Put, "/api/workspaces/" + Device) { Content = System.Net.Http.Json.JsonContent.Create(Save(0, "{}")) };
        bare.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(bare)).StatusCode);
    }

    [Fact]
    public async Task Another_user_never_sees_or_changes_a_device_even_with_its_id()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var (theirs, _) = await SecondUserAsync(db);
        var url = "/api/workspaces/" + Device;

        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Put, url, cookie, Save(0, Doc($"[{Win("w1")}]"), "Mein Laptop")))).StatusCode);

        // der Fremde: nichts zu lesen, in der Liste nichts, Loeschen trifft nichts
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, url, theirs))).StatusCode);
        Assert.Empty((await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/workspaces", theirs)))).GetProperty("devices").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, url, theirs))).StatusCode);
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from workspaces where user_id = $1", user));

        // Schreiben mit derselben Kennung und passender Version legt beim Fremden eine eigene Zeile an und fasst meine nicht an
        var theirSave = await c.SendAsync(Req(HttpMethod.Put, url, theirs, Save(1, Doc($"[{Win("x")}]"))));
        Assert.Equal(HttpStatusCode.Conflict, theirSave.StatusCode);                                         // fuer ihn gibt es nichts mit Version 1
        var json = await Json(theirSave);
        Assert.Equal(0, json.GetProperty("version").GetInt32());
        Assert.False(json.TryGetProperty("doc", out _));                                                      // und es verraet nichts von meinem Stand
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Put, url, theirs, Save(0, Doc($"[{Win("x")}]"))))).StatusCode);
        var mine = await Json(await c.SendAsync(Req(HttpMethod.Get, url, cookie)));
        Assert.Equal("w1", mine.GetProperty("doc").GetProperty("windows")[0].GetProperty("id").GetString());
        Assert.Equal("Mein Laptop", mine.GetProperty("name").GetString());
        Assert.Equal(2L, await db.ScalarAsync<long>("select count(*) from workspaces"));
    }

    [Fact]
    public async Task A_stale_version_is_refused_with_the_current_state_and_can_be_overwritten()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var url = "/api/workspaces/" + Device;
        async Task<HttpResponseMessage> Put(int version, string doc, string? name = null) => await c.SendAsync(Req(HttpMethod.Put, url, cookie, Save(version, doc, name)));

        var first = await Put(0, Doc($"[{Win("a")}]"), "Büro");
        Assert.Equal(1, (await Json(first)).GetProperty("version").GetInt32());
        var second = await Put(1, Doc($"[{Win("a")},{Win("b")}]"));
        Assert.Equal(2, (await Json(second)).GetProperty("version").GetInt32());

        // das zweite Browserfenster baut noch auf Version 1 auf (oder meint, es sei das erste Speichern)
        foreach (var stale in new[] { 1, 0 })
        {
            var conflict = await Put(stale, Doc($"[{Win("zzz")}]"));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var body = await Json(conflict);
            Assert.Equal("workspace_conflict", body.GetProperty("code").GetString());
            Assert.Equal(2, body.GetProperty("version").GetInt32());
            Assert.Equal(2, body.GetProperty("doc").GetProperty("windows").GetArrayLength());
        }
        Assert.Equal(2, (await Json(await c.SendAsync(Req(HttpMethod.Get, url, cookie)))).GetProperty("doc").GetProperty("windows").GetArrayLength());     // nichts wurde ueberschrieben

        // wer den Hinweis gesehen hat und trotzdem schreiben will, sendet mit der aktuellen Version (der letzte gewinnt); der Name bleibt, wenn keiner kommt
        var overwrite = await Put(2, Doc($"[{Win("zzz")}]"));
        Assert.Equal(3, (await Json(overwrite)).GetProperty("version").GetInt32());
        var now = await Json(await c.SendAsync(Req(HttpMethod.Get, url, cookie)));
        Assert.Equal("zzz", now.GetProperty("doc").GetProperty("windows")[0].GetProperty("id").GetString());
        Assert.Equal("Büro", now.GetProperty("name").GetString());

        // zwei Schreiber gleichzeitig mit derselben Version: genau einer gewinnt
        var race = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Put(3, Doc())));
        Assert.Equal(1, race.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, race.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        // vergessen, dann meldet ein Fenster mit alter Version: Konflikt, Version 0, kein Dokument
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, url, cookie))).StatusCode);
        var gone = await Put(4, Doc());
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        Assert.Equal(0, (await Json(gone)).GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, url, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(0, Doc())).StatusCode);
    }

    [Fact]
    public async Task Bad_requests_are_refused_and_foreign_connections_never_stored()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var (_, other) = await SecondUserAsync(db);
        var mine = await Insert(db, "insert into connections (user_id, name, kind, host, port) values ($1, 'VM', 'desktop', 'ziel', 3389) returning id", user);
        var theirs = await Insert(db, "insert into connections (user_id, name, kind, host, port) values ($1, 'Fremd', 'desktop', 'ziel', 3389) returning id", other);
        var url = "/api/workspaces/" + Device;

        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, "/api/workspaces/kurz", cookie, Save(0, "{}")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Get, "/api/workspaces/has%20space%21x", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, url, cookie, new { doc = J("{}") }))).StatusCode);                   // Version fehlt
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, url, cookie, new { version = -1, doc = J("{}") }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, url, cookie, new { version = 0 }))).StatusCode);                      // Dokument fehlt
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, url, cookie, Save(0, Doc($"[{Win("w", state: "x")}]"))))).StatusCode);
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from workspaces"));

        var windows = $"[{Win("a", "connection", $"\"connectionId\":\"{mine}\"")},{Win("b", "connection", $"\"connectionId\":\"{theirs}\"")},{Win("c", "connection", $"\"connectionId\":\"{Guid.NewGuid()}\"")},{Win("d")}]";
        var saved = await Json(await c.SendAsync(Req(HttpMethod.Put, url, cookie, Save(0, Doc(windows, extra: ",\"focus\":\"b\"")))));
        Assert.Equal(2, saved.GetProperty("dropped").GetInt32());
        var stored = await db.ScalarAsync<string>("select doc::text from workspaces");
        Assert.DoesNotContain(theirs.ToString(), stored);
        Assert.Contains(mine.ToString(), stored);
        Assert.DoesNotContain("\"focus\"", stored);

        // die Liste nennt Geraet, Name, Zahl der Fenster und die offenen Verbindungen; wird die Verbindung geloescht, verschwindet das Fenster auch beim Lesen
        var entry = (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/workspaces", cookie)))).GetProperty("devices")[0];
        Assert.Equal(Device, entry.GetProperty("deviceId").GetString());
        Assert.Equal(2, entry.GetProperty("windows").GetInt32());
        Assert.Equal([mine], entry.GetProperty("connections").EnumerateArray().Select(e => e.GetGuid()));
        await db.ExecAsync("delete from connections where id = $1", mine);
        var after = await Json(await c.SendAsync(Req(HttpMethod.Get, url, cookie)));
        Assert.Equal(["d"], after.GetProperty("doc").GetProperty("windows").EnumerateArray().Select(w => w.GetProperty("id").GetString()));
        Assert.Equal(0, (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/workspaces", cookie)))).GetProperty("devices")[0].GetProperty("connections").GetArrayLength());
    }

    [Fact]
    public async Task The_number_of_devices_is_limited_and_the_oldest_makes_room()
    {
        await using var db = await TestDb.CreateAsync();
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        var store = new WorkspaceStore(db.Data);
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 4; i++)
        {
            Assert.NotNull(await store.SaveAsync(anna, $"device-{i:00000}", "", "{}", 0, maxDevices: 3, ct));
            await db.ExecAsync("update workspaces set updated_at = now() + make_interval(secs => $2) where device_id = $1", $"device-{i:00000}", i);
        }
        Assert.Equal(["device-00003", "device-00002", "device-00001"], (await store.ListAsync(anna, ct)).Select(r => r.DeviceId));
    }

    [Fact]
    public async Task Writes_are_rate_limited_per_user()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var (theirs, _) = await SecondUserAsync(db);
        var url = "/api/workspaces/" + Device;
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < JulOS.Frontend.Workspaces.WorkspaceEndpoints.WritesPerMinute + 5; i++) codes.Add((await c.SendAsync(Req(HttpMethod.Put, url, cookie, Save(i, "{}")))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, codes.Take(JulOS.Frontend.Workspaces.WorkspaceEndpoints.WritesPerMinute));
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Put, url, theirs, Save(0, "{}")))).StatusCode);       // der andere Benutzer ist nicht betroffen
    }

    [Fact]
    public async Task Workspaces_go_through_a_backup_and_restore_and_follow_their_user()
    {
        await using var src = await TestDb.CreateAsync();
        var anna = await Insert(src, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        var ct = TestContext.Current.CancellationToken;
        await new WorkspaceStore(src.Data).SaveAsync(anna, Device, "Laptop", Doc($"[{Win("w1")}]"), 0, 16, ct);
        var dump = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(src.Data, dump, DateTimeOffset.UtcNow, ct);
        await using var dst = await TestDb.CreateAsync();
        await DbDump.RestoreAsync(dst.Data, new StringReader(dump.ToString()), false, ct);
        var back = await new WorkspaceStore(dst.Data).GetAsync(anna, Device, ct);
        Assert.Equal("Laptop", back!.Name);
        Assert.Contains("\"w1\"", back.Doc);
        await src.ExecAsync("delete from users");
        Assert.Equal(0L, await src.ScalarAsync<long>("select count(*) from workspaces"));
    }
}
