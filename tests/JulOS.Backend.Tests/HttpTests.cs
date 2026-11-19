using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Logic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>Der ganze Server im Speicher (echte Middleware, echte Datenbank in einem eigenen Schema).</summary>
public sealed class HttpTests
{
    private const string Password = "korrektes pferd batterie";
    private const string SetupCode = "TEST-CODE-0000";

    private sealed class Server(TestDb db, string? filesRoot = null) : WebApplicationFactory<Program>
    {
        /// <summary>Ohne eigene Cookie-Verwaltung des Test-Clients: die Tests setzen das Cookie selbst.</summary>
        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            if (filesRoot is not null) builder.UseSetting("JULOS_FILES_ROOT", filesRoot);
            // der Test-Server hat keine Client-Adresse; wie hinter einem Proxy im eigenen Netz tun
            builder.ConfigureServices(s => s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, LoopbackFilter>());
        }
    }

    private sealed class LoopbackFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback; return n(ctx); });
            next(app);
        };
    }

    private static HttpRequestMessage Post(string url, object body, string origin = "http://localhost", bool header = true, string? cookie = null, Action<HttpRequestMessage>? tweak = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (header) req.Headers.Add("X-JulOS", "1");
        if (origin.Length > 0) req.Headers.Add("Origin", origin);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        tweak?.Invoke(req);
        return req;
    }

    private static HttpRequestMessage Get(string url, string? cookie = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string? SetCookie(HttpResponseMessage r) => r.Headers.TryGetValues("Set-Cookie", out var v) ? v.FirstOrDefault() : null;

    private static string CookiePair(string setCookie) => setCookie.Split(';')[0];

    private static async Task<(string Cookie, string Secret, string Set)> SetupAsync(HttpClient c, Action<HttpRequestMessage>? tweak = null, string origin = "http://localhost")
    {
        var begin = await c.SendAsync(Post("/api/setup/begin", new { code = SetupCode, user = "jul", password = Password }, origin, tweak: tweak));
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var secret = (await Json(begin)).GetProperty("secret").GetString()!;
        var code = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var finish = await c.SendAsync(Post("/api/setup/finish", new { code = SetupCode, user = "jul", totp = code }, origin, tweak: tweak));
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        var set = SetCookie(finish)!;
        return (CookiePair(set), secret, set);
    }

    [Fact]
    public async Task Anonymous_visitors_see_only_the_public_endpoints()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Client();

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/health")).StatusCode);
        var me = await Json(await c.GetAsync("/api/auth/me"));
        Assert.Equal(JsonValueKind.Null, me.GetProperty("user").ValueKind);
        Assert.True(me.GetProperty("setupNeeded").GetBoolean());
        Assert.True((await Json(await c.GetAsync("/api/setup/status"))).GetProperty("needed").GetBoolean());

        foreach (var path in new[] { "/api/auth/sessions", "/api/connections", "/api/system/overview", "/api/files/list", "/api/files/search?q=a", "/api/files/download?path=a", "/api/irgendwas" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post("/api/auth/logout", new { }))).StatusCode);

        var page = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(page.Headers.Contains("Server"));
        Assert.Contains("no-store", (await c.GetAsync("/api/health")).Headers.GetValues("Cache-Control").Single());
    }

    [Fact]
    public async Task The_system_overview_is_read_only_and_answers_a_signed_in_user()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Client();
        var (cookie, _, _) = await SetupAsync(c);

        // der Sampler hat beim Start mindestens einmal gemessen; Speicher und Datentraeger kommen aus dem Test-Rechner
        JsonElement o = default;
        for (var i = 0; i < 20; i++)
        {
            var r = await c.SendAsync(Get("/api/system/overview", cookie));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            o = await Json(r);
            if (o.TryGetProperty("memoryTotal", out _)) break;
            await Task.Delay(100);
        }
        Assert.True(o.GetProperty("memoryTotal").GetInt64() > 0);
        Assert.InRange(o.GetProperty("memoryUsed").GetInt64(), 0, o.GetProperty("memoryTotal").GetInt64());
        Assert.True(o.GetProperty("cores").GetInt32() > 0);
        Assert.False(o.TryGetProperty("host", out _));

        // nichts schreibbar
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.SendAsync(Post("/api/system/overview", new { }, cookie: cookie))).StatusCode);
    }

    [Fact]
    public async Task Files_are_read_only_need_a_session_and_stay_inside_the_root()
    {
        var dir = Directory.CreateTempSubdirectory("julos-http-files-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(dir.FullName, "root")).FullName;
            File.WriteAllText(Path.Combine(dir.FullName, "geheim.txt"), "GEHEIM");
            File.WriteAllText(Path.Combine(root, "seite.html"), "<script>alert(1)</script>");
            Directory.CreateDirectory(Path.Combine(root, "Media"));
            File.WriteAllText(Path.Combine(root, "Media", "a b.txt"), "inhalt");
            File.CreateSymbolicLink(Path.Combine(root, "raus.txt"), Path.Combine(dir.FullName, "geheim.txt"));

            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, root);
            var c = app.Client();
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/files/list")).StatusCode);
            var (cookie, _, _) = await SetupAsync(c);

            var list = await Json(await c.SendAsync(Get("/api/files/list", cookie)));
            Assert.Equal(["Media", "seite.html"], list.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()!));
            Assert.Equal("Media/a b.txt", (await Json(await c.SendAsync(Get("/api/files/search?q=a%20b", cookie)))).GetProperty("results")[0].GetProperty("path").GetString());

            var down = await c.SendAsync(Get("/api/files/download?path=Media/a%20b.txt", cookie));
            Assert.Equal("inhalt", await down.Content.ReadAsStringAsync());
            Assert.Equal("attachment", down.Content.Headers.ContentDisposition!.DispositionType);
            // eine Seite wird nie als Seite geliefert
            var html = await c.SendAsync(Get("/api/files/download?path=seite.html", cookie));
            Assert.Equal("application/octet-stream", html.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", html.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("nosniff", html.Headers.GetValues("X-Content-Type-Options").Single());

            foreach (var bad in new[] { "../geheim.txt", "%2e%2e/geheim.txt", "raus.txt", "/etc/passwd", "Media", "gibt-es-nicht" })
                Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Get("/api/files/download?path=" + bad, cookie))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Get("/api/files/list?path=..", cookie))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Get("/api/files/search?q=", cookie))).StatusCode);

            // nur lesen: keine schreibende Methode
            foreach (var path in new[] { "/api/files/list", "/api/files/download?path=seite.html", "/api/files/upload" })
            {
                var r = await c.SendAsync(Post(path, new { }, cookie: cookie));
                Assert.True(r.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, path);
            }
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/files/download?path=seite.html") { Headers = { { "X-JulOS", "1" }, { "Origin", "http://localhost" }, { "Cookie", cookie } } })).StatusCode);
            Assert.True(File.Exists(Path.Combine(root, "seite.html")));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task Files_can_be_switched_off()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "off");
        var c = app.Client();
        var (cookie, _, _) = await SetupAsync(c);
        var r = await c.SendAsync(Get("/api/files/list", cookie));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("abgeschaltet", (await Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task State_changing_requests_need_the_custom_header_and_a_matching_origin()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Client();
        var body = new { user = "jul", password = Password, code = "123456" };

        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post("/api/auth/login", body, header: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post("/api/auth/login", body, origin: ""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post("/api/auth/login", body, origin: "https://boese.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post("/api/auth/login", body))).StatusCode);       // erlaubt, nur die Daten stimmen nicht

        var big = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = new StringContent("{\"user\":\"" + new string('a', 6000) + "\"}", Encoding.UTF8, "application/json") };
        big.Headers.Add("X-JulOS", "1"); big.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await c.SendAsync(big)).StatusCode);
    }

    [Fact]
    public async Task Setup_login_cookie_sessions_and_logout_work_end_to_end()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Client();

        var wrong = await c.SendAsync(Post("/api/setup/begin", new { code = "WRONG-CODE-1111", user = "jul", password = Password }));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Contains("Setup-Code", (await Json(wrong)).GetProperty("error").GetString());

        var (cookie, secret, set) = await SetupAsync(c);
        var lower = set.ToLowerInvariant();
        Assert.StartsWith("julos_sid=", set);                                   // ohne HTTPS kein __Host- Praefix
        Assert.Contains("httponly", lower);
        Assert.Contains("samesite=strict", lower);
        Assert.Contains("path=/", lower);
        Assert.Contains("max-age=2592000", lower);
        Assert.DoesNotContain("secure", lower);

        var me = await Json(await c.SendAsync(Get("/api/auth/me", cookie)));
        Assert.Equal("jul", me.GetProperty("user").GetString());
        Assert.False(me.GetProperty("setupNeeded").GetBoolean());
        Assert.False((await Json(await c.GetAsync("/api/setup/status"))).GetProperty("needed").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Post("/api/setup/begin", new { code = SetupCode, user = "zwei", password = Password }))).StatusCode);

        var sessions = await Json(await c.SendAsync(Get("/api/auth/sessions", cookie)));
        Assert.Equal(1, sessions.GetProperty("sessions").GetArrayLength());
        Assert.True(sessions.GetProperty("sessions")[0].GetProperty("current").GetBoolean());

        // zweite Anmeldung mit dem naechsten Zeitschritt, dann alle anderen abmelden
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1;
        var login = await c.SendAsync(Post("/api/auth/login", new { user = "jul", password = Password, code = Totp.Hotp(Base32.Decode(secret), step) }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie2 = CookiePair(SetCookie(login)!);
        Assert.Equal(2, (await Json(await c.SendAsync(Get("/api/auth/sessions", cookie)))).GetProperty("sessions").GetArrayLength());
        Assert.Equal(1, (await Json(await c.SendAsync(Post("/api/auth/logout-others", new { }, cookie: cookie)))).GetProperty("ended").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get("/api/auth/sessions", cookie2))).StatusCode);

        var logout = await c.SendAsync(Post("/api/auth/logout", new { }, cookie: cookie));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("max-age=0", SetCookie(logout)!.ToLowerInvariant());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get("/api/auth/sessions", cookie))).StatusCode);
    }

    [Fact]
    public async Task Account_changes_need_a_session_the_custom_header_and_the_current_password()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Client();
        var (cookie, _, _) = await SetupAsync(c);
        var body = new { current = Password, next = "ganz anderes passwort 42" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post("/api/auth/password", body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post("/api/auth/totp/begin", new { current = Password }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post("/api/auth/totp/finish", new { code = "123456" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post("/api/auth/password", body, header: false, cookie: cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post("/api/auth/password", body, origin: "http://evil.test", cookie: cookie))).StatusCode);

        var wrong = await c.SendAsync(Post("/api/auth/password", new { current = "falsches passwort 123", next = "ganz anderes passwort 42" }, cookie: cookie));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Contains("aktuelle Passwort", (await Json(wrong)).GetProperty("error").GetString());

        var begin = await c.SendAsync(Post("/api/auth/totp/begin", new { current = Password }, cookie: cookie));
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var fresh = await Json(begin);
        var secret = fresh.GetProperty("secret").GetString()!;
        Assert.StartsWith("otpauth://totp/", fresh.GetProperty("uri").GetString());
        var code = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var finish = await Json(await c.SendAsync(Post("/api/auth/totp/finish", new { code }, cookie: cookie)));
        Assert.Equal(10, finish.GetProperty("recovery").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Post("/api/auth/password", body, cookie: cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Get("/api/auth/sessions", cookie))).StatusCode);   // diese Sitzung bleibt
        var tooBig = Post("/api/auth/password", new { current = Password, next = new string('x', 5000) }, cookie: cookie);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await c.SendAsync(tooBig)).StatusCode);
    }

    [Fact]
    public async Task Behind_an_https_proxy_the_cookie_is_secure_and_the_forwarded_host_and_address_count()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Client();
        void Proxy(HttpRequestMessage r) { r.Headers.Add("X-Forwarded-Proto", "https"); r.Headers.Add("X-Forwarded-Host", "os.example.test"); r.Headers.Add("X-Forwarded-For", "203.0.113.7"); }

        // der Browser schickt den Origin der oeffentlichen Adresse, der Server sieht intern "localhost": nur mit X-Forwarded-Host passt das
        var rejected = await c.SendAsync(Post("/api/setup/begin", new { code = SetupCode, user = "jul", password = Password }, origin: "https://os.example.test"));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

        var (cookie, _, set) = await SetupAsync(c, Proxy, origin: "https://os.example.test");
        var lower = set.ToLowerInvariant();
        Assert.StartsWith("__host-julos_sid=", lower);
        Assert.Contains("secure", lower);
        Assert.Contains("path=/", lower);
        Assert.DoesNotContain("domain=", lower);

        var req = Get("/api/auth/sessions", cookie); Proxy(req);
        var resp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("max-age=31536000", resp.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Equal("203.0.113.7", (await Json(resp)).GetProperty("sessions")[0].GetProperty("ip").GetString());

        // dasselbe Cookie ueber HTTP (ohne Proxy-Kopfzeilen) gilt nicht
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get("/api/auth/sessions", cookie))).StatusCode);
    }

    [Fact]
    public async Task Too_many_wrong_logins_are_answered_with_429_and_retry_after()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Client();
        await SetupAsync(c);
        HttpResponseMessage last = null!;
        for (var i = 0; i < 6; i++) last = await c.SendAsync(Post("/api/auth/login", new { user = "jul", password = "falsch falsch falsch", code = "000000" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.True(last.Headers.Contains("Retry-After"));
        Assert.True((await Json(last)).GetProperty("retryAfter").GetInt32() > 0);
    }

    [Fact]
    public async Task File_search_is_limited_per_user_and_minute()
    {
        var dir = Directory.CreateTempSubdirectory("julos-http-search-");
        try
        {
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, dir.FullName);
            var c = app.Client();
            var (cookie, _, _) = await SetupAsync(c);
            for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Get("/api/files/search?q=x", cookie))).StatusCode);
            var over = await c.SendAsync(Get("/api/files/search?q=x", cookie));
            Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
            // das Auflisten hat sein eigenes Fenster
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Get("/api/files/list", cookie))).StatusCode);
        }
        finally { dir.Delete(true); }
    }
}
