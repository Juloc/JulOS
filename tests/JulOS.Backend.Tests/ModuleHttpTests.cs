using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    private static readonly string[] Paths = ["/api/modules/caddy/routes", "/api/modules/caddy/reachability", "/api/modules/ddns/status"];

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
