using System.Net;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Das Modul "Dienste" ueber HTTP: Sitzung, Schalter (off/read/control), Erlaubnisliste, verschluesselter Schluessel, nur gespeicherte Adressen, Trennung der Benutzer.</summary>
public sealed class ServiceHttpTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private sealed class Server(TestDb db, string? mode, FakeServiceWeb? web = null, string? hosts = null, bool withKey = true) : WebApplicationFactory<Program>
    {
        public HttpClient Http() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            if (mode is not null) builder.UseSetting("JULOS_SERVICES", mode);
            if (hosts is not null) builder.UseSetting("JULOS_SERVICES_HOSTS", hosts);
            if (withKey) builder.UseSetting("JULOS_SECRET_KEY", Key);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
            if (web is not null) builder.ConfigureTestServices(s => s.AddSingleton(new ServiceChecker(new ServiceHosts(hosts ?? "jularr.lan", null), web)));
        }
    }

    private static object Body(string name = "Jularr", string? apiKey = null, string? header = "Authorization", string host = "jularr.lan") => new
    {
        name, icon = "server", openUrl = $"http://{host}:8080/", healthUrl = $"http://{host}:8080/health/ready", metricsUrl = $"http://{host}:8080/api/summary",
        metrics = new[] { new { path = "activeCount", label = "Aktiv", unit = "", format = "plain" } }, intervalSeconds = 30, headerName = header, apiKey,
    };

    private static FakeServiceWeb Web() => new(new()
    {
        ["http://jularr.lan:8080/health/ready"] = (HttpStatusCode.OK, "Healthy"),
        ["http://jularr.lan:8080/api/summary"] = (HttpStatusCode.OK, """{"activeCount":5}"""),
    });

    [Fact]
    public async Task Without_a_session_every_service_path_is_refused()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "control");
        var c = app.Http();
        var id = Guid.NewGuid();
        foreach (var (method, url) in new[] { (HttpMethod.Get, "/api/modules/services"), (HttpMethod.Get, "/api/modules/services/status"), (HttpMethod.Post, "/api/modules/services"),
            (HttpMethod.Post, $"/api/modules/services/{id}/check"), (HttpMethod.Put, $"/api/modules/services/{id}"), (HttpMethod.Delete, $"/api/modules/services/{id}") })
        {
            var r = await c.SendAsync(Req(method, url, null, method == HttpMethod.Get ? null : Body()));
            Assert.Equal(method == HttpMethod.Get ? HttpStatusCode.Unauthorized : HttpStatusCode.Unauthorized, r.StatusCode);
        }
    }

    [Fact]
    public async Task By_default_the_module_is_off_and_every_path_answers_404()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, null);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        Assert.Equal("off", (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/modules", cookie)))).GetProperty("services").GetString());
        foreach (var (method, url) in new[] { (HttpMethod.Get, "/api/modules/services"), (HttpMethod.Get, "/api/modules/services/status"), (HttpMethod.Post, "/api/modules/services") })
            Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(method, url, cookie, method == HttpMethod.Get ? null : Body()))).StatusCode);
    }

    [Fact]
    public async Task Read_shows_and_checks_but_does_not_change()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "read", Web());
        var c = app.Http();
        var (cookie, userId) = await SignedInAsync(c, db);
        await db.ExecAsync("insert into services (user_id, name, health_url) values ($1, 'Vorhanden', 'http://jularr.lan:8080/health/ready')", userId);

        Assert.Equal("read", (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/modules", cookie)))).GetProperty("services").GetString());
        var list = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/modules/services", cookie)));
        Assert.False(list.GetProperty("canEdit").GetBoolean());
        Assert.Equal("Vorhanden", list.GetProperty("services")[0].GetProperty("name").GetString());
        var status = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/modules/services/status", cookie)));
        Assert.Equal("up", status.GetProperty("statuses")[0].GetProperty("state").GetString());

        var id = list.GetProperty("services")[0].GetProperty("id").GetString();
        foreach (var (method, url) in new[] { (HttpMethod.Post, "/api/modules/services"), (HttpMethod.Put, $"/api/modules/services/{id}"), (HttpMethod.Delete, $"/api/modules/services/{id}") })
        {
            var r = await c.SendAsync(Req(method, url, cookie, method == HttpMethod.Delete ? null : Body()));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from services"));
    }

    [Fact]
    public async Task The_key_is_stored_encrypted_and_never_comes_back()
    {
        const string Secret = "Bearer wegwerf-test-wert-123";
        var web = Web();
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "control", web);
        var c = app.Http();
        var (cookie, userId) = await SignedInAsync(c, db);

        var created = await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body(apiKey: Secret)));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var text = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("wegwerf", text);
        var dto = System.Text.Json.JsonDocument.Parse(text).RootElement;
        Assert.True(dto.GetProperty("hasKey").GetBoolean());
        var id = dto.GetProperty("id").GetGuid();

        // in der Datenbank: Chiffre, nicht der Klartext; sie laesst sich nur mit dem Schluessel des Servers und der Dienst-Id oeffnen
        var sealedKey = await db.ScalarAsync<byte[]>("select secret from services where id = $1", id);
        Assert.DoesNotContain("wegwerf", System.Text.Encoding.UTF8.GetString(sealedKey));
        var box = new SecretBox(Key);
        Assert.Equal(Secret, box.Decrypt(sealedKey, id));
        Assert.Null(box.Decrypt(sealedKey, Guid.NewGuid()));

        // jede Antwort ist frei davon
        foreach (var url in new[] { "/api/modules/services", "/api/modules/services/status" })
            Assert.DoesNotContain("wegwerf", await (await c.SendAsync(Req(HttpMethod.Get, url, cookie))).Content.ReadAsStringAsync());
        var check = await c.SendAsync(Req(HttpMethod.Post, $"/api/modules/services/{id}/check", cookie, new { }));
        Assert.DoesNotContain("wegwerf", await check.Content.ReadAsStringAsync());
        // nur der Server schickt ihn, als Kopfzeile, an die gespeicherte Adresse
        Assert.All(web.Requests, r => Assert.Equal(Secret, r.Headers["Authorization"]));
        Assert.NotEmpty(web.Requests);

        // null laesst ihn, "" entfernt ihn
        var keep = await Json(await c.SendAsync(Req(HttpMethod.Put, $"/api/modules/services/{id}", cookie, Body("Umbenannt", apiKey: null))));
        Assert.True(keep.GetProperty("hasKey").GetBoolean());
        Assert.Equal("Umbenannt", keep.GetProperty("name").GetString());
        var gone = await Json(await c.SendAsync(Req(HttpMethod.Put, $"/api/modules/services/{id}", cookie, Body("Umbenannt", apiKey: ""))));
        Assert.False(gone.GetProperty("hasKey").GetBoolean());
        Assert.Null(await db.Data.CreateCommand($"select secret from services where id = '{id}'").ExecuteScalarAsync() as byte[]);
    }

    [Fact]
    public async Task Without_a_server_key_no_api_key_is_stored_but_the_service_still_can_be()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "control", withKey: false);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var list = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/modules/services", cookie)));
        Assert.False(list.GetProperty("keyStore").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body(apiKey: "x")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body(header: null)))).StatusCode);
    }

    [Fact]
    public async Task The_allow_list_applies_when_saving_and_a_key_needs_a_header_name()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "control", hosts: "jularr.lan");
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var refused = await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body(host: "evil.example.test")));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("nicht erlaubt", (await Json(refused)).GetProperty("error").GetString());
        var noHeader = await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body(apiKey: "k", header: "")));
        Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from services"));
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body()))).StatusCode);
    }

    [Fact]
    public async Task Services_of_other_users_are_invisible_and_cannot_be_changed_or_asked()
    {
        var web = Web();
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "control", web);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var other = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'x') returning id");
        var foreign = await Insert(db, "insert into services (user_id, name, health_url) values ($1, 'Fremd', 'http://jularr.lan:8080/health/ready') returning id", other);

        Assert.Empty((await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/modules/services", cookie)))).GetProperty("services").EnumerateArray());
        Assert.Empty((await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/modules/services/status", cookie)))).GetProperty("statuses").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, $"/api/modules/services/{foreign}/check", cookie, new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, $"/api/modules/services/{foreign}", cookie, Body()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/modules/services/{foreign}", cookie))).StatusCode);
        Assert.Empty(web.Requests);
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from services"));
    }

    [Fact]
    public async Task Only_saved_addresses_are_asked_and_the_browser_cannot_name_one()
    {
        var web = Web();
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "control", web);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var created = await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body())));
        var id = created.GetProperty("id").GetString();
        web.Requests.Clear();

        // Query und Koerper des Browsers zaehlen nicht
        var r = await c.SendAsync(Req(HttpMethod.Post, $"/api/modules/services/{id}/check?url=http://169.254.169.254/", cookie, new { url = "http://169.254.169.254/", healthUrl = "http://evil.example.test/" }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.All(web.Requests, q => Assert.StartsWith("http://jularr.lan:8080/", q.Url));
        Assert.Equal(2, web.Requests.Count);
    }

    [Fact]
    public async Task Writing_is_limited_per_user_and_by_count()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "control");
        var c = app.Http();
        var (cookie, userId) = await SignedInAsync(c, db);
        for (var i = 0; i < ServiceRules.MaxPerUser; i++) await db.ExecAsync("insert into services (user_id, name) values ($1, $2)", userId, "s" + i);
        var r = await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body()));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Zu viele Dienste", (await Json(r)).GetProperty("error").GetString());
        HttpStatusCode last = default;
        for (var i = 0; i < 35; i++) last = (await c.SendAsync(Req(HttpMethod.Post, "/api/modules/services", cookie, Body()))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
