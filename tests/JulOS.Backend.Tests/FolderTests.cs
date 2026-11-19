using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Ordner, Favoriten und "Zuletzt benutzt" auf dem Server (docs/FOLDERS.md): Regeln, Migration 010 (alte Daten bleiben), Endpunkte.</summary>
public sealed class FolderTests
{
    [Theory]
    [InlineData("Büro", null, "Büro", "folder")]
    [InlineData("  Heim  ", "home", "Heim", "home")]
    [InlineData("Server", "", "Server", "folder")]
    public void A_folder_is_trimmed_and_gets_a_default_icon(string name, string? icon, string expectedName, string expectedIcon)
    {
        var (n, i, e) = FolderRules.Validate(name, icon);
        Assert.Null(e);
        Assert.Equal((expectedName, expectedIcon), (n, i));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("Zeile\neins", null)]
    [InlineData("ok", "unbekannt")]
    public void Bad_folder_input_is_refused(string? name, string? icon) => Assert.NotNull(FolderRules.Validate(name, icon).Error);

    [Fact]
    public void A_folder_name_has_at_most_40_characters()
    {
        Assert.Null(FolderRules.Validate(new string('a', 40), null).Error);
        Assert.NotNull(FolderRules.Validate(new string('a', 41), null).Error);
    }

    // ---------------------------------------------------------------- Migration

    /// <summary>Eine Datenbank im Stand vor den Ordnern: alle Migrationen bis 009 (die der Ordner ist die 010).</summary>
    private static async Task<TestDb> CreateBeforeFoldersAsync()
    {
        var db = await TestDb.CreateAsync(migrate: false);
        var asm = typeof(Migrator).Assembly;
        await db.ExecAsync("create table schema_migrations (id text primary key, applied_at timestamptz not null default now())");
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("database/", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var id = name["database/".Length..^4];
            if (string.CompareOrdinal(id, "010") >= 0) continue;
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            await db.ExecAsync(await reader.ReadToEndAsync());
            await db.ExecAsync("insert into schema_migrations (id) values ($1)", id);
        }
        return db;
    }

    [Fact]
    public async Task Old_connections_stay_without_a_folder_and_nothing_is_lost()
    {
        await using var db = await CreateBeforeFoldersAsync();
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        var pc = await Insert(db, "insert into connections (user_id, name, kind, host, port, username, secret, drive_share) values ($1, 'Büro-PC', 'desktop', 'ziel', 3389, 'anna', '\\x0102', true) returning id", anna);
        await Insert(db, "insert into connections (user_id, name, kind, host, port) values ($1, 'Shell', 'terminal', 'ziel', 22) returning id", anna);

        var applied = await new Migrator(db.Data).RunAsync(TestContext.Current.CancellationToken);
        Assert.Contains("010_folders_and_settings", applied);

        Assert.Equal(2L, await db.ScalarAsync<long>("select count(*) from connections"));
        Assert.Equal(2L, await db.ScalarAsync<long>("select count(*) from connections where folder_id is null and not favorite and last_used_at is null and use_count = 0"));
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from folders"));
        Assert.Equal("Büro-PC", await db.ScalarAsync<string>("select name from connections where id = $1", pc));
        Assert.True(await db.ScalarAsync<bool>("select secret is not null and drive_share from connections where id = $1", pc));       // bisherige Felder unberuehrt
        Assert.Empty(await new Migrator(db.Data).RunAsync(TestContext.Current.CancellationToken));

        // die alten Verbindungen lassen sich einsortieren, und ein geloeschter Ordner nimmt sie nicht mit
        var store = new FolderStore(db.Data);
        var folder = await store.CreateAsync(anna, "Arbeit", "work", TestContext.Current.CancellationToken);
        Assert.True(await store.SetFolderAsync(anna, pc, folder.Id, TestContext.Current.CancellationToken));
        Assert.True(await store.DeleteAsync(anna, folder.Id, TestContext.Current.CancellationToken));
        Assert.Equal(2L, await db.ScalarAsync<long>("select count(*) from connections where folder_id is null"));
    }

    [Fact]
    public async Task A_backup_from_before_the_folders_restores_into_the_new_schema()
    {
        await using var old = await CreateBeforeFoldersAsync();
        var anna = await Insert(old, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        await Insert(old, "insert into connections (user_id, name, kind, host, port) values ($1, 'Büro-PC', 'desktop', 'ziel', 3389) returning id", anna);
        var dump = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(old.Data, dump, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("010_folders_and_settings", dump.ToString().Split('\n').First(l => l.StartsWith("-- migrations ", StringComparison.Ordinal)));

        await using var fresh = await TestDb.CreateAsync();
        await DbDump.RestoreAsync(fresh.Data, new StringReader(dump.ToString()), false, TestContext.Current.CancellationToken);
        Assert.Equal(1L, await fresh.ScalarAsync<long>("select count(*) from connections where folder_id is null and not favorite and use_count = 0"));

        // und ein Auszug der neuen Version mit Ordnern geht hin und zurueck
        var store = new FolderStore(fresh.Data);
        var folder = await store.CreateAsync(anna, "Heim", "home", TestContext.Current.CancellationToken);
        var conn = await fresh.ScalarAsync<Guid>("select id from connections");
        await store.SetFolderAsync(anna, conn, folder.Id, TestContext.Current.CancellationToken);
        await store.SetFavoriteAsync(anna, conn, true, TestContext.Current.CancellationToken);
        var again = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(fresh.Data, again, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        await using var third = await TestDb.CreateAsync();
        await DbDump.RestoreAsync(third.Data, new StringReader(again.ToString()), false, TestContext.Current.CancellationToken);
        Assert.Equal(1L, await third.ScalarAsync<long>("select count(*) from connections where folder_id is not null and favorite"));
    }

    // ---------------------------------------------------------------- Endpunkte

    private static async Task<Guid> NewFolder(HttpClient c, string cookie, string name, string? icon = null)
    {
        var r = await c.SendAsync(Req(HttpMethod.Post, "/api/folders", cookie, new { name, icon }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Org(HttpClient c, string cookie) => await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/folders", cookie)));

    private static JsonElement PlacementOf(JsonElement org, Guid id) => org.GetProperty("placements").EnumerateArray().First(p => p.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Every_route_needs_a_session_and_the_special_header()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/api/folders", null), (HttpMethod.Post, "/api/folders", new { name = "x" }), (HttpMethod.Put, $"/api/folders/{id}", new { name = "x" }), (HttpMethod.Delete, $"/api/folders/{id}", null),
            (HttpMethod.Put, "/api/folders/order", new { ids = new[] { id } }), (HttpMethod.Put, $"/api/connections/{id}/folder", new { }), (HttpMethod.Put, $"/api/connections/{id}/favorite", new { favorite = true }),
            (HttpMethod.Post, $"/api/connections/{id}/used", new { }), (HttpMethod.Post, $"/api/connections/{id}/move", new { delta = 1 }), (HttpMethod.Post, "/api/folders/import-local", new { }),
        };
        foreach (var (method, url, body) in routes) Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(method, url, null, body))).StatusCode);

        var (cookie, _) = await SignedInAsync(c, db);
        var bare = new HttpRequestMessage(HttpMethod.Post, "/api/folders") { Content = JsonContent.Create(new { name = "x" }) };
        bare.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(bare)).StatusCode);
    }

    [Fact]
    public async Task Folders_are_created_renamed_ordered_and_deleted_with_validation_limits_and_ownership()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var other = await Insert(db, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreign = await Insert(db, "insert into folders (user_id, name) values ($1, 'Fremd') returning id", other);

        var a = await NewFolder(c, cookie, "  Büro  ", "work");
        var b = await NewFolder(c, cookie, "Heim");
        var d = await NewFolder(c, cookie, "Labor", "server");
        var folders = (await Org(c, cookie)).GetProperty("folders");
        Assert.Equal(["Büro", "Heim", "Labor"], folders.EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray());
        Assert.Equal("folder", folders[1].GetProperty("icon").GetString());
        Assert.DoesNotContain(foreign.ToString(), folders.GetRawText());

        var renamed = await c.SendAsync(Req(HttpMethod.Put, $"/api/folders/{b}", cookie, new { name = "Zuhause", icon = "home" }));
        Assert.Equal("Zuhause", (await Json(renamed)).GetProperty("name").GetString());
        foreach (var bad in new object[] { new { name = "" }, new { name = new string('a', 41) }, new { name = "ok", icon = "boese" }, new { } })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Post, "/api/folders", cookie, bad))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, $"/api/folders/{a}", cookie, bad))).StatusCode);
        }

        // Reihenfolge: die genannten zuerst, die uebrigen dahinter; fremde Kennungen aendern nichts
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Put, "/api/folders/order", cookie, new { ids = new[] { d, foreign, b } }))).StatusCode);
        Assert.Equal(["Labor", "Zuhause", "Büro"], (await Org(c, cookie)).GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray());
        Assert.Equal(0, await db.ScalarAsync<int>("select sort from folders where id = $1", foreign));       // das Fremde bleibt, wie es war
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, "/api/folders/order", cookie, new { }))).StatusCode);

        // fremde und unbekannte Ordner: 404
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, $"/api/folders/{foreign}", cookie, new { name = "Meiner" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/folders/{foreign}", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/folders/{Guid.NewGuid()}", cookie))).StatusCode);
        Assert.Equal("Fremd", await db.ScalarAsync<string>("select name from folders where id = $1", foreign));

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/folders/{a}", cookie))).StatusCode);

        // die Grenze je Benutzer
        for (var i = 0; await db.ScalarAsync<long>("select count(*) from folders where user_id = $1", user) < FolderRules.MaxPerUser; i++) await Insert(db, "insert into folders (user_id, name) values ($1, $2) returning id", user, "f" + i);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Post, "/api/folders", cookie, new { name = "zu viel" }))).StatusCode);
    }

    [Fact]
    public async Task Connections_can_be_filed_favoured_used_and_moved_and_only_by_their_owner()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var connections = new ConnectionStore(db.Data);
        var one = (await connections.CreateAsync(user, "Eins", "terminal", "ziel", 22, "u")).Id;
        var two = (await connections.CreateAsync(user, "Zwei", "terminal", "ziel", 22, "u")).Id;
        var three = (await connections.CreateAsync(user, "Drei", "desktop", "ziel", 3389, "u")).Id;
        var other = await Insert(db, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreignConn = (await connections.CreateAsync(other, "Fremd", "terminal", "ziel", 22, "u")).Id;
        var foreignFolder = await Insert(db, "insert into folders (user_id, name) values ($1, 'Fremd') returning id", other);
        var folder = await NewFolder(c, cookie, "Arbeit", "work");

        // neue Verbindungen: ohne Ordner, kein Favorit, nie benutzt
        var org = await Org(c, cookie);
        Assert.Equal(3, org.GetProperty("placements").GetArrayLength());
        var p = PlacementOf(org, one);
        Assert.False(p.TryGetProperty("folderId", out _));
        Assert.False(p.GetProperty("favorite").GetBoolean());
        Assert.False(p.TryGetProperty("lastUsedAt", out _));
        Assert.Equal(0, p.GetProperty("useCount").GetInt32());

        // einsortieren und herausnehmen; fremder Ordner und fremde Verbindung gehen nicht
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{one}/folder", cookie, new { folderId = folder }))).StatusCode);
        Assert.Equal(folder, PlacementOf(await Org(c, cookie), one).GetProperty("folderId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{two}/folder", cookie, new { folderId = foreignFolder }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{foreignConn}/folder", cookie, new { folderId = folder }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{two}/folder", cookie, new { folderId = Guid.NewGuid() }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{one}/folder", cookie, new { folderId = (Guid?)null }))).StatusCode);
        Assert.False(PlacementOf(await Org(c, cookie), one).TryGetProperty("folderId", out _));

        // Favorit
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{two}/favorite", cookie, new { favorite = true }))).StatusCode);
        Assert.True(PlacementOf(await Org(c, cookie), two).GetProperty("favorite").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{two}/favorite", cookie, new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{foreignConn}/favorite", cookie, new { favorite = true }))).StatusCode);
        Assert.False(await db.ScalarAsync<bool>("select favorite from connections where id = $1", foreignConn));

        // zuletzt benutzt
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{three}/used", cookie, new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{three}/used", cookie, new { }))).StatusCode);
        var used = PlacementOf(await Org(c, cookie), three);
        Assert.Equal(2, used.GetProperty("useCount").GetInt32());
        Assert.True(DateTimeOffset.UtcNow - used.GetProperty("lastUsedAt").GetDateTimeOffset() < TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{foreignConn}/used", cookie, new { }))).StatusCode);

        // Reihenfolge innerhalb der Verbindungen ohne Ordner: Eins, Zwei, Drei -> Zwei hoch
        async Task<string[]> Order() => (await Org(c, cookie)).GetProperty("placements").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Select(g => g == one ? "Eins" : g == two ? "Zwei" : "Drei").ToArray();
        Assert.Equal(["Eins", "Zwei", "Drei"], await Order());
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{two}/move", cookie, new { delta = -1 }))).StatusCode);
        Assert.Equal(["Zwei", "Eins", "Drei"], await Order());
        await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{two}/move", cookie, new { delta = -1 }));      // schon ganz oben: bleibt
        Assert.Equal(["Zwei", "Eins", "Drei"], await Order());
        await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{two}/move", cookie, new { delta = 1 }));
        await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{two}/move", cookie, new { delta = 1 }));
        Assert.Equal(["Eins", "Drei", "Zwei"], await Order());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{two}/move", cookie, new { delta = 5 }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{foreignConn}/move", cookie, new { delta = 1 }))).StatusCode);

        // ein geloeschter Ordner laesst seine Verbindungen stehen
        await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{three}/folder", cookie, new { folderId = folder }));
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/folders/{folder}", cookie))).StatusCode);
        var after = await Org(c, cookie);
        Assert.Equal(3, after.GetProperty("placements").GetArrayLength());
        Assert.False(PlacementOf(after, three).TryGetProperty("folderId", out _));
    }

    [Fact]
    public async Task Local_favourites_are_taken_over_once_and_never_lower_the_server_values()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var connections = new ConnectionStore(db.Data);
        var one = (await connections.CreateAsync(user, "Eins", "terminal", "ziel", 22, "u")).Id;
        var two = (await connections.CreateAsync(user, "Zwei", "terminal", "ziel", 22, "u")).Id;
        var other = await Insert(db, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreignConn = (await connections.CreateAsync(other, "Fremd", "terminal", "ziel", 22, "u")).Id;
        await db.ExecAsync("update connections set use_count = 7 where id = $1", two);

        var earlier = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        var body = new { favorites = new[] { one, foreignConn, Guid.NewGuid() }, uses = new Dictionary<string, int> { [one.ToString()] = 4, [two.ToString()] = 2, [foreignConn.ToString()] = 9, ["kein-guid"] = 1 }, last = new Dictionary<string, long> { [one.ToString()] = earlier, [two.ToString()] = DateTimeOffset.UtcNow.AddYears(5).ToUnixTimeMilliseconds() } };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Post, "/api/folders/import-local", cookie, body))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Post, "/api/folders/import-local", cookie, body))).StatusCode);   // zweimal schadet nicht

        var org = await Org(c, cookie);
        Assert.True(PlacementOf(org, one).GetProperty("favorite").GetBoolean());
        Assert.False(PlacementOf(org, two).GetProperty("favorite").GetBoolean());
        Assert.Equal(4, PlacementOf(org, one).GetProperty("useCount").GetInt32());
        Assert.Equal(7, PlacementOf(org, two).GetProperty("useCount").GetInt32());                      // nie weniger als auf dem Server
        Assert.Equal(earlier, PlacementOf(org, one).GetProperty("lastUsedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.True(PlacementOf(org, two).GetProperty("lastUsedAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow.AddMinutes(1));   // nie in der Zukunft
        Assert.False(await db.ScalarAsync<bool>("select favorite from connections where id = $1", foreignConn));                    // fremde Verbindungen bleiben unberuehrt
        Assert.Equal(0, await db.ScalarAsync<int>("select use_count from connections where id = $1", foreignConn));
    }

}
