using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Quick Connect: eine Sitzung ohne gespeicherte Verbindung (Regeln, Erlaubnisliste, nichts in der Datenbank, nichts im Log).</summary>
public sealed class QuickConnectTests
{
    [Fact]
    public void A_quick_target_has_a_stable_id_per_target_and_user_name()
    {
        var a = QuickConnect.IdOf("terminal", "Ziel.example", 22, "u");
        Assert.Equal(a, QuickConnect.IdOf("terminal", "ziel.example", 22, "u"));
        Assert.NotEqual(a, QuickConnect.IdOf("terminal", "ziel.example", 2222, "u"));
        Assert.NotEqual(a, QuickConnect.IdOf("desktop", "ziel.example", 22, "u"));
        Assert.NotEqual(a, QuickConnect.IdOf("terminal", "ziel.example", 22, "v"));
    }

    [Fact]
    public void Quick_trust_remembers_per_user_and_target_within_a_bound()
    {
        var t = new QuickTrust();
        var me = Guid.NewGuid();
        Assert.Null(t.Get(me, "terminal", "h", 22));
        t.Set(me, "terminal", "H", 22, "SHA256:abc");
        Assert.Equal("SHA256:abc", t.Get(me, "terminal", "h", 22));
        Assert.Null(t.Get(Guid.NewGuid(), "terminal", "h", 22));
        Assert.Null(t.Get(me, "desktop", "h", 22));
        for (var i = 0; i < QuickTrust.MaxEntries + 5; i++) t.Set(me, "terminal", "x" + i, 22, "v");
        Assert.NotNull(t.Get(me, "terminal", "x" + (QuickTrust.MaxEntries + 4), 22));            // es waechst nicht unbegrenzt, das Neueste bleibt
    }

    // ---------------------------------------------------------------- Quick Connect

    private static async Task<HttpResponseMessage> Quick(HttpClient c, string? cookie, object quick, string? password = "geheim-nur-fuer-diese-sitzung") =>
        await c.SendAsync(Req(HttpMethod.Post, "/api/remote/ticket", cookie, new { quick, password, sid = "abcdefgh1234", cols = 80, rows = 24, width = 856, height = 1448 }));

    [Fact]
    public async Task Quick_connect_gives_a_ticket_without_touching_the_database_and_never_logs_the_password()
    {
        await using var db = await TestDb.CreateAsync();
        var log = new List<string>();
        await using var app = new Server(db, log: log);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var hostsBefore = await db.ScalarAsync<long>("select count(*) from hosts");

        var r = await Quick(c, cookie, new { kind = "terminal", host = "ziel.example", port = 22, username = "tester" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("terminal", j.GetProperty("kind").GetString());
        Assert.Equal("ssh", j.GetProperty("engine").GetString());
        Assert.False(string.IsNullOrEmpty(j.GetProperty("ticket").GetString()));

        // nichts gespeichert: keine Verbindung, kein Host, kein Passwort in irgendeiner Tabelle
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from connections"));
        Assert.Equal(hostsBefore, await db.ScalarAsync<long>("select count(*) from hosts"));
        var dump = new StringWriter();
        await DbDump.WriteAsync(db.Data, dump, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);       // jede Tabelle des Schemas
        Assert.DoesNotContain("geheim-nur-fuer-diese-sitzung", dump.ToString());
        Assert.DoesNotContain("ziel.example", dump.ToString());
        string[] lines; lock (log) lines = [.. log];
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, l => l.Contains("geheim-nur-fuer-diese-sitzung"));
    }

    [Fact]
    public async Task Quick_connect_applies_the_same_rules_and_the_allow_list_as_stored_connections()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, allowed: "erlaubt.example");
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);

        Assert.Equal(HttpStatusCode.OK, (await Quick(c, cookie, new { kind = "terminal", host = "ERLAUBT.example", username = "u" })).StatusCode);                 // Standardport 22
        Assert.Equal(HttpStatusCode.Forbidden, (await Quick(c, cookie, new { kind = "terminal", host = "anderes.example", username = "u" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Quick(c, cookie, new { kind = "desktop", host = "10.0.0.7", username = "u" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Quick(c, cookie, new { kind = "vnc", host = "erlaubt.example" })).StatusCode);                               // Art unbekannt
        Assert.Equal(HttpStatusCode.BadRequest, (await Quick(c, cookie, new { kind = "terminal", host = "erlaubt.example/pfad" })).StatusCode);                     // Adresse ungueltig
        Assert.Equal(HttpStatusCode.BadRequest, (await Quick(c, cookie, new { kind = "terminal", host = "erlaubt.example", port = 70000 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Quick(c, cookie, new { kind = "terminal", host = "erlaubt.example", username = new string('u', 65) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Quick(c, cookie, new { kind = "terminal", host = "erlaubt.example" }, password: null)).StatusCode);               // Passwort fehlt
        Assert.Equal(HttpStatusCode.BadRequest, (await Quick(c, cookie, new { kind = "terminal", host = "erlaubt.example" }, password: new string('p', 257))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Post, "/api/remote/ticket", cookie, new { password = "x" }))).StatusCode);         // weder Verbindung noch Quick
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from connections"));
    }

    [Fact]
    public async Task Quick_connect_needs_a_session_and_is_rate_limited()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Quick(c, null, new { kind = "terminal", host = "ziel.example" })).StatusCode);

        var (cookie, _) = await SignedInAsync(c, db);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < QuickConnect.MaxPerMinute + 3; i++) codes.Add((await Quick(c, cookie, new { kind = "terminal", host = "ziel.example", username = "u" })).StatusCode);
        Assert.Equal(QuickConnect.MaxPerMinute, codes.Count(x => x == HttpStatusCode.OK));
        Assert.Equal(3, codes.Count(x => x == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task A_quick_desktop_ticket_works_only_with_the_encoder_and_stores_nothing()
    {
        Assert.SkipUnless(JulOS.Integrations.Display.ScreenStream.IsAvailable, "libopenh264 fehlt: ohne Kodierer gibt es keine Desktop-Verbindung");
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var r = await Quick(c, cookie, new { kind = "desktop", host = "ziel.example", username = "u" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("desktop", j.GetProperty("kind").GetString());
        Assert.Equal((856, 1448), (j.GetProperty("width").GetInt32(), j.GetProperty("height").GetInt32()));
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from connections"));
    }
}
