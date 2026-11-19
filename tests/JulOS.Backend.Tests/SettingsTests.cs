using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Einstellungen je Benutzer auf dem Server (Tastenleiste, Themenfarbe): Regeln und Endpunkte.</summary>
public sealed class SettingsTests
{
    [Fact]
    public void The_theme_setting_is_checked_and_rewritten()
    {
        var (ok, error) = SettingsRules.Validate("theme", J("""{"seed":"#9A67EA","scheme":"dark","evil":"x"}"""));
        Assert.Null(error);
        Assert.Equal("""{"seed":"#9a67ea","scheme":"dark"}""", ok);
        Assert.Equal("""{"seed":"system","scheme":"auto"}""", SettingsRules.Validate("theme", J("""{"seed":"system","scheme":"auto"}""")).Json);
        foreach (var bad in new[] { """{"seed":"red","scheme":"dark"}""", """{"seed":"#fff","scheme":"dark"}""", """{"seed":"#9a67ea","scheme":"neon"}""", """{"scheme":"dark"}""", "[1]", "\"x\"" })
            Assert.NotNull(SettingsRules.Validate("theme", J(bad)).Error);
        Assert.NotNull(SettingsRules.Validate("sonstwas", J("{}")).Error);
    }

    [Fact]
    public void The_language_setting_accepts_only_de_en_system()
    {
        foreach (var good in new[] { "de", "en", "system" })
            Assert.Equal($$"""{"lang":"{{good}}"}""", SettingsRules.Validate("lang", J($$"""{"lang":"{{good}}","evil":"x"}""")).Json);
        foreach (var bad in new[] { """{"lang":"fr"}""", """{"lang":""}""", """{"lang":"EN"}""", "{}", "[1]", "\"en\"" })
            Assert.NotNull(SettingsRules.Validate("lang", J(bad)).Error);
    }

    [Fact]
    public void The_keybar_setting_keeps_only_known_fields_and_limits_sizes()
    {
        var good = """{"rows":[{"id":"keys","title":"Tasten","icon":"keyboard","extra":1,"items":[{"label":"Esc","type":"chord","value":"esc","scope":"both"},{"label":"Strg+C","type":"chord","value":"ctrl+c","scope":"terminal","x":1}]},{"id":"bar","title":"Leiste","fixed":true,"items":[]}]}""";
        var (json, error) = SettingsRules.Validate("keybar", J(good));
        Assert.Null(error);
        Assert.Equal("""{"rows":[{"id":"keys","title":"Tasten","icon":"keyboard","items":[{"label":"Esc","type":"chord","value":"esc"},{"label":"Strg+C","type":"chord","value":"ctrl+c","scope":"terminal"}]},{"id":"bar","title":"Leiste","fixed":true,"items":[]}]}""", json);

        string Row(string id, string items) => $$"""{"id":"{{id}}","title":"t","items":[{{items}}]}""";
        string Item(string label = "a", string type = "chord", string value = "x", string? scope = null) => $$"""{"label":"{{label}}","type":"{{type}}","value":"{{value}}"{{(scope is null ? "" : $",\"scope\":\"{scope}\"")}}}""";
        var many = string.Join(",", Enumerable.Range(0, SettingsRules.MaxRows + 1).Select(i => Row("r" + i, "")));
        var bad = new[]
        {
            $$"""{"rows":[{{many}}]}""",                                                             // zu viele Zeilen
            """{"rows":[]}""",                                                                      // keine Zeile
            $$"""{"rows":[{{Row("a", Item(type: "script"))}}]}""",                                    // unbekannte Art
            $$"""{"rows":[{{Row("a", Item(scope: "alle"))}}]}""",                                     // unbekannter Bereich
            $$"""{"rows":[{{Row("a", Item(label: new string('x', 25)))}}]}""",                        // Beschriftung zu lang
            $$"""{"rows":[{{Row("a", Item(value: new string('x', 201)))}}]}""",                       // Wert zu lang
            $$"""{"rows":[{{Row("A B", "")}}]}""",                                                    // Kennung ungueltig
            $$"""{"rows":[{{Row("a", "")}},{{Row("a", "")}}]}""",                                      // doppelte Kennung
            $$"""{"rows":[{{Row("a", string.Join(",", Enumerable.Repeat(Item(), SettingsRules.MaxItemsPerRow + 1)))}}]}""", // zu viele Tasten
            """{"nope":1}""",
        };
        foreach (var b in bad) Assert.NotNull(SettingsRules.Validate("keybar", J(b)).Error);
    }

    [Fact]
    public async Task The_settings_table_comes_with_the_migration_and_old_data_is_untouched()
    {
        await using var db = await FolderTests.CreateBeforeFoldersAsync();
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        await db.ExecAsync("insert into connections (user_id, name, kind, host, port) values ($1, 'Büro-PC', 'desktop', 'ziel', 3389)", anna);
        Assert.Contains("010_folders_and_settings", await new Migrator(db.Data).RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from user_settings"));
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from connections"));

        var store = new SettingsStore(db.Data);
        await store.SetAsync(anna, "theme", """{"seed":"system","scheme":"auto"}""", TestContext.Current.CancellationToken);
        await store.SetAsync(anna, "theme", """{"seed":"#9a67ea","scheme":"dark"}""", TestContext.Current.CancellationToken);     // ueberschreibt, kein zweiter Eintrag
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from user_settings"));
        await db.ExecAsync("delete from users");                                                                                      // die Einstellungen folgen ihrem Benutzer
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from user_settings"));
    }

    [Fact]
    public async Task Settings_go_through_a_backup_and_restore()
    {
        await using var src = await TestDb.CreateAsync();
        var anna = await Insert(src, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        await new SettingsStore(src.Data).SetAsync(anna, "keybar", """{"rows":[{"id":"keys","title":"Tasten","items":[]}]}""", TestContext.Current.CancellationToken);
        var dump = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(src.Data, dump, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        await using var dst = await TestDb.CreateAsync();
        await DbDump.RestoreAsync(dst.Data, new StringReader(dump.ToString()), false, TestContext.Current.CancellationToken);
        var back = await new SettingsStore(dst.Data).GetAllAsync(anna, TestContext.Current.CancellationToken);
        Assert.Contains("\"Tasten\"", back["keybar"]);
    }

    [Fact]
    public async Task Every_settings_route_needs_a_session_and_the_special_header()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var routes = new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/api/settings", null), (HttpMethod.Put, "/api/settings/theme", new { value = new { seed = "system", scheme = "auto" } }), (HttpMethod.Delete, "/api/settings/theme", null),
        };
        foreach (var (method, url, body) in routes) Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(method, url, null, body))).StatusCode);

        var (cookie, _) = await SignedInAsync(c, db);
        var bare = new HttpRequestMessage(HttpMethod.Put, "/api/settings/theme") { Content = JsonContent.Create(new { value = new { seed = "system", scheme = "auto" } }) };
        bare.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(bare)).StatusCode);
    }

    [Fact]
    public async Task Settings_are_saved_per_user_checked_and_can_be_reset()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var other = await Insert(db, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        await new SettingsStore(db.Data).SetAsync(other, "theme", """{"seed":"#112233","scheme":"dark"}""");

        Assert.Empty((await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/settings", cookie)))).GetProperty("settings").EnumerateObject());

        var theme = new { value = new { seed = "#9A67EA", scheme = "light", evil = "x" } };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Put, "/api/settings/theme", cookie, theme))).StatusCode);
        var keybar = new { value = new { rows = new[] { new { id = "keys", title = "Tasten", items = new[] { new { label = "Esc", type = "chord", value = "esc", scope = "desktop" } } } } } };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Put, "/api/settings/keybar", cookie, keybar))).StatusCode);

        var all = (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/settings", cookie)))).GetProperty("settings");
        Assert.Equal("#9a67ea", all.GetProperty("theme").GetProperty("seed").GetString());
        Assert.False(all.GetProperty("theme").TryGetProperty("evil", out _));
        Assert.Equal("desktop", all.GetProperty("keybar").GetProperty("rows")[0].GetProperty("items")[0].GetProperty("scope").GetString());
        Assert.Equal("""{"seed":"#112233","scheme":"dark"}""", (await new SettingsStore(db.Data).GetAllAsync(other))["theme"].Replace(" ", ""));      // der andere Benutzer ist unberuehrt

        // ueberschreiben, ungueltig, unbekannt, ohne Wert
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Put, "/api/settings/theme", cookie, new { value = new { seed = "system", scheme = "auto" } }))).StatusCode);
        Assert.Equal("system", (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/settings", cookie)))).GetProperty("settings").GetProperty("theme").GetProperty("seed").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, "/api/settings/theme", cookie, new { value = new { seed = "rot", scheme = "light" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, "/api/settings/theme", cookie, new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, "/api/settings/sonstwas", cookie, theme))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Delete, "/api/settings/sonstwas", cookie))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, "/api/settings/keybar", cookie))).StatusCode);
        Assert.False((await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/settings", cookie)))).GetProperty("settings").TryGetProperty("keybar", out _));
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from user_settings where user_id = $1", user));
    }

}
