using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>Die Module Caddy und DDNS ueber HTTP: Sitzung noetig, aus = 404, nur lesen, keine Eingaben. Die Quellen sind Fakes.</summary>
public sealed class ModuleHttpTests
{
    private const string Password = "korrektes pferd batterie";
    private const string SetupCode = "TEST-CODE-0000";
    private static readonly string[] Paths = ["/api/modules/caddy/routes", "/api/modules/caddy/reachability", "/api/modules/ddns/status",
        "/api/modules/caddy/health", "/api/modules/caddy/certificates", "/api/modules/caddy/config", "/api/modules/caddy/log",
        "/api/modules/ddns/control", "/api/modules/ddns/history"];

    private sealed class Server(TestDb db, Action<IServiceCollection>? services = null, params (string, string)[] settings) : WebApplicationFactory<Program>
    {
        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
            // ConfigureTestServices laeuft nach den Registrierungen von Program.cs: die Fakes ersetzen die echten Module
            builder.ConfigureTestServices(s => services?.Invoke(s));
        }
    }

    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback; return n(ctx); });
            next(app);
        };
    }

    private static HttpRequestMessage Get(string url, string? cookie = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static HttpRequestMessage Post(string url, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
        req.Headers.Add("X-JulOS", "1");
        req.Headers.Add("Origin", "http://localhost");
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> SignInAsync(HttpClient c)
    {
        static HttpRequestMessage P(string url, object body) { var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) }; r.Headers.Add("X-JulOS", "1"); r.Headers.Add("Origin", "http://localhost"); return r; }
        var begin = await c.SendAsync(P("/api/setup/begin", new { code = SetupCode, user = "jul", password = Password }));
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var secret = (await Json(begin)).GetProperty("secret").GetString()!;
        var totp = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var finish = await c.SendAsync(P("/api/setup/finish", new { code = SetupCode, user = "jul", totp }));
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        return finish.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    [Fact]
    public async Task Without_a_session_every_module_path_is_refused_whatever_the_switch()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: [("JULOS_CADDY", "read"), ("JULOS_DDNS", "read")]);
        var c = app.Client();
        foreach (var path in Paths.Append("/api/modules"))
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task By_default_everything_is_off_and_the_paths_answer_404()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Client();
        var cookie = await SignInAsync(c);
        var modules = await Json(await c.SendAsync(Get("/api/modules", cookie)));
        Assert.Equal("off", modules.GetProperty("caddy").GetString());
        Assert.Equal("off", modules.GetProperty("ddns").GetString());
        foreach (var path in Paths)
        {
            var r = await c.SendAsync(Get(path, cookie));
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Contains("nicht eingeschaltet", (await Json(r)).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Caddy_lists_routes_checks_only_listed_upstreams_and_is_read_only()
    {
        {
            var admin = new FakeCaddyAdmin("""{"srv0":{"routes":[{"match":[{"host":["app.example.test"]}],"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"web:3000"}]}]}]}}""");
            var probed = new List<string>();
            var opts = new CaddyOptions(ModuleMode.Read, null, new Uri("http://caddy-filter:2020"));
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, s => s.AddSingleton(new CaddyModule(opts, admin, (t, _) => { lock (probed) probed.Add($"{t.Host}:{t.Port}"); return Task.FromResult("up"); })),
                ("JULOS_CADDY", "read"));
            var c = app.Client();
            var cookie = await SignInAsync(c);

            Assert.Equal("read", (await Json(await c.SendAsync(Get("/api/modules", cookie)))).GetProperty("caddy").GetString());
            var routes = await Json(await c.SendAsync(Get("/api/modules/caddy/routes", cookie)));
            Assert.Equal("admin", routes.GetProperty("source").GetString());
            var row = routes.GetProperty("routes")[0];
            Assert.Equal("app.example.test", row.GetProperty("domain").GetString());
            Assert.Equal("proxy", row.GetProperty("kind").GetString());
            Assert.Equal("web:3000", row.GetProperty("targets")[0].GetString());

            // die Eingabe des Browsers zaehlt nicht: weder Query noch Pfad bestimmen, was geprueft wird
            var check = await Json(await c.SendAsync(Get("/api/modules/caddy/reachability?host=evil&target=127.0.0.1:22", cookie)));
            Assert.Equal("up", check.GetProperty("upstreams")[0].GetProperty("state").GetString());
            Assert.Equal(["web:3000"], probed);
            Assert.All(admin.Requests, r => Assert.Equal(("GET", "http://caddy-filter:2020/config/apps/http/servers"), (r.Method.Method, r.Url)));

            // nur lesen
            foreach (var path in Paths.Take(2))
                Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.SendAsync(Post(path, cookie))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Get("/api/modules/ddns/status", cookie))).StatusCode);   // DDNS bleibt aus
        }
    }

    [Fact]
    public async Task Caddy_health_certificates_config_and_log_answer_without_secrets_and_without_input()
    {
        var log = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(log, "{\"ts\":1700000000,\"request\":{\"remote_ip\":\"198.51.100.7\",\"method\":\"GET\",\"host\":\"app.example.test\",\"uri\":\"/a?token=GEHEIM\"},\"status\":200,\"duration\":0.01,\"size\":5}\n");
            var web = new FakeServiceWeb(new()
            {
                ["http://caddy:2019/config/apps/http/servers"] = (HttpStatusCode.OK, """{"srv0":{"routes":[{"match":[{"host":["app.example.test"]}],"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"web:3000"}]}]}]}}"""),
                ["http://caddy:2019/config/"] = (HttpStatusCode.OK, """{"apps":{"tls":{"automation":{"policies":[{"issuers":[{"email":"admin@example.test","api_token":"GEHEIM"}]}]}}},"routes":"web:3000"}"""),
                ["http://caddy:2019/reverse_proxy/upstreams"] = (HttpStatusCode.OK, "[]"),
            });
            var opts = new CaddyOptions(ModuleMode.Read, null, new Uri("http://caddy:2019"), LogFile: log);
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, s => s.AddSingleton(new CaddyModule(opts, web, tls: (d, _, _) => Task.FromResult(new CertInfo(d, "ok", "Wegwerf-CA", null, 80, true, null)))), ("JULOS_CADDY", "read"));
            var c = app.Client();
            var cookie = await SignInAsync(c);

            Assert.Equal("up", (await Json(await c.SendAsync(Get("/api/modules/caddy/health", cookie)))).GetProperty("state").GetString());
            var certs = await Json(await c.SendAsync(Get("/api/modules/caddy/certificates?host=evil.example.test", cookie)));
            Assert.Equal("app.example.test", certs.GetProperty("certificates")[0].GetProperty("domain").GetString());
            var config = await c.SendAsync(Get("/api/modules/caddy/config", cookie));
            var configText = await config.Content.ReadAsStringAsync();
            Assert.DoesNotContain("GEHEIM", configText);
            Assert.DoesNotContain("admin@example.test", configText);
            var logText = await (await c.SendAsync(Get("/api/modules/caddy/log", cookie))).Content.ReadAsStringAsync();
            Assert.Contains("app.example.test", logText);
            Assert.DoesNotContain("GEHEIM", logText);
            Assert.DoesNotContain("198.51.100.7", logText);
            foreach (var path in Paths.Skip(3).Take(4))
                Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.SendAsync(Post(path, cookie))).StatusCode);
            Assert.All(web.Requests, r => Assert.Equal("http://caddy:2019", new Uri(r.Url).GetLeftPart(UriPartial.Authority)));
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public async Task Ddns_reports_the_status_and_hides_nothing_it_should_not_have()
    {
        var ip = new FakeIp("203.0.113.7");
        var dns = new FakeResolver(new() { ["home.example.test"] = ["203.0.113.7"], ["alt.example.test"] = ["203.0.113.8"] });
        var opts = new DdnsOptions(ModuleMode.Read, ["home.example.test", "alt.example.test"], null, null, DdnsOptions.DefaultCache);
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton(new DdnsMonitor(opts, ip, dns)), ("JULOS_DDNS", "read"));
        var c = app.Client();
        var cookie = await SignInAsync(c);

        var r = await c.SendAsync(Get("/api/modules/ddns/status", cookie));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = await Json(r);
        Assert.Equal("notSynced", s.GetProperty("state").GetString());
        Assert.Equal("203.0.113.7", s.GetProperty("publicIp").GetString());
        Assert.Equal(["synced", "notSynced"], s.GetProperty("hosts").EnumerateArray().Select(h => h.GetProperty("state").GetString()!));
        Assert.True(s.TryGetProperty("checkedAt", out _));
        // kein Token, nichts zum Schreiben
        Assert.DoesNotContain("token", (await r.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.SendAsync(Post("/api/modules/ddns/status", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Get("/api/modules/caddy/routes", cookie))).StatusCode);   // Caddy bleibt aus
        // schnelle Wiederholung kommt aus dem Zwischenspeicher, der Lookup wird nicht je Aufruf befragt
        await c.SendAsync(Get("/api/modules/ddns/status", cookie));
        Assert.Equal(1, ip.Calls);
    }

    private static (DdnsOptions, DdnsMonitor, FakeDnsProvider) DdnsRig(ModuleMode mode)
    {
        var opts = new DdnsOptions(mode, ["home.example.test"], null, null, DdnsOptions.DefaultCache);
        return (opts, new DdnsMonitor(opts, new FakeIp("203.0.113.9"), new FakeResolver(new() { ["home.example.test"] = ["203.0.113.1"] })), new FakeDnsProvider(null));
    }

    [Fact]
    public async Task Ddns_control_updates_by_button_only_with_the_switch_and_keeps_a_history()
    {
        var (opts, monitor, provider) = DdnsRig(ModuleMode.Control);
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s =>
        {
            s.AddSingleton(monitor);
            s.AddSingleton(sp => new DdnsUpdater(opts, monitor, provider, sp.GetRequiredService<IDdnsHistory>()));
        }, ("JULOS_DDNS", "control"));
        var c = app.Client();
        var cookie = await SignInAsync(c);

        var info = await Json(await c.SendAsync(Get("/api/modules/ddns/control", cookie)));
        Assert.True(info.GetProperty("canUpdate").GetBoolean());
        Assert.Equal("Fake", info.GetProperty("provider").GetString());
        Assert.Equal("control", (await Json(await c.SendAsync(Get("/api/modules", cookie)))).GetProperty("ddns").GetString());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.SendAsync(Post("/api/modules/ddns/control", cookie))).StatusCode);

        // Eingaben des Browsers zaehlen nicht: der Koerper nennt weder Name noch Adresse
        var req = Post("/api/modules/ddns/update?host=evil.example.test&ip=198.51.100.1", cookie);
        req.Content = JsonContent.Create(new { host = "evil.example.test", ip = "198.51.100.1" });
        var res = await Json(await c.SendAsync(req));
        Assert.Equal("updated", res.GetProperty("outcome").GetString());
        Assert.Equal([("home.example.test", "203.0.113.9")], provider.Writes);

        var history = await Json(await c.SendAsync(Get("/api/modules/ddns/history", cookie)));
        var row = history.GetProperty("entries")[0];
        Assert.Equal(("home.example.test", "203.0.113.1", "203.0.113.9", "button"), (row.GetProperty("host").GetString(), row.GetProperty("oldIp").GetString(), row.GetProperty("newIp").GetString(), row.GetProperty("trigger").GetString()));
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from ddns_history"));
        // der Knopf ist begrenzt: eine Pause nach der Aenderung
        Assert.Equal("tooSoon", (await Json(await c.SendAsync(Post("/api/modules/ddns/update", cookie)))).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Ddns_read_cannot_update_and_control_without_a_provider_says_so()
    {
        var (opts, monitor, _) = DdnsRig(ModuleMode.Read);
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s =>
        {
            s.AddSingleton(monitor);
            s.AddSingleton(sp => new DdnsUpdater(opts, monitor, new FakeDnsProvider(null), sp.GetRequiredService<IDdnsHistory>()));
        }, ("JULOS_DDNS", "read"));
        var c = app.Client();
        var cookie = await SignInAsync(c);
        var r = await c.SendAsync(Post("/api/modules/ddns/update", cookie));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.False((await Json(await c.SendAsync(Get("/api/modules/ddns/control", cookie)))).GetProperty("canUpdate").GetBoolean());

        var (copts, cmonitor, _) = DdnsRig(ModuleMode.Control);
        await using var db2 = await TestDb.CreateAsync();
        await using var app2 = new Server(db2, s =>
        {
            s.AddSingleton(cmonitor);
            s.AddSingleton(sp => new DdnsUpdater(copts, cmonitor, null, sp.GetRequiredService<IDdnsHistory>()));
        }, ("JULOS_DDNS", "control"));
        var c2 = app2.Client();
        var cookie2 = await SignInAsync(c2);
        Assert.Equal(HttpStatusCode.Conflict, (await c2.SendAsync(Post("/api/modules/ddns/update", cookie2))).StatusCode);
    }

    [Fact]
    public async Task The_paths_are_rate_limited_per_user()
    {
        var opts = new DdnsOptions(ModuleMode.Read, ["home.example.test"], null, null, DdnsOptions.DefaultCache);
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, s => s.AddSingleton(new DdnsMonitor(opts, new FakeIp("203.0.113.7"), new FakeResolver(new() { ["home.example.test"] = ["203.0.113.7"] }))), ("JULOS_DDNS", "read"));
        var c = app.Client();
        var cookie = await SignInAsync(c);
        HttpStatusCode last = default;
        for (var i = 0; i < 40; i++) last = (await c.SendAsync(Get("/api/modules/ddns/status", cookie))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
