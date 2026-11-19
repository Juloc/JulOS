using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JulOS.Backend.Logic;
using JulOS.Integrations.Agent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>Der Agent meldet sich von der Ziel-Maschine aus beim Server: Einrichtung, Token, Weg der Aufrufe, Uebertragung von Foto und Datei (echte Middleware und Datenbank, der Agent ist ein Fake im Speicher).</summary>
public sealed class AgentLinkTests
{
    private const string Password = "korrektes pferd batterie";
    private const string SetupCode = "TEST-CODE-0000";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly byte[] PngHead = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private sealed class Server(TestDb db, Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        public HttpClient Http() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            builder.UseSetting("JULOS_GUACD", "127.0.0.1:1");
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
            if (configure is not null) builder.ConfigureTestServices(configure);
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

    private static HttpRequestMessage Post(string url, object body, string? cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-JulOS", "1");
        req.Headers.Add("Origin", "http://localhost");
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static HttpRequestMessage Send(HttpMethod method, string url, string cookie, byte[]? body = null, string contentType = "application/octet-stream", string? fileName = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) { req.Content = new ByteArrayContent(body); req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType); }
        if (method != HttpMethod.Get) { req.Headers.Add("X-JulOS", "1"); req.Headers.Add("Origin", "http://localhost"); }
        if (fileName is not null) req.Headers.Add("X-File-Name", Uri.EscapeDataString(fileName));
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<(string Cookie, Guid UserId)> SignedInAsync(HttpClient c, TestDb db)
    {
        var begin = await c.SendAsync(Post("/api/setup/begin", new { code = SetupCode, user = "jul", password = Password }, null));
        var secret = (await Json(begin)).GetProperty("secret").GetString()!;
        var totp = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var finish = await c.SendAsync(Post("/api/setup/finish", new { code = SetupCode, user = "jul", totp }, null));
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        return (finish.Headers.GetValues("Set-Cookie").First().Split(';')[0], await db.ScalarAsync<Guid>("select id from users limit 1"));
    }

    private static Task<Guid> ConnectionOf(TestDb db, Guid user, string name = "VM", string kind = "desktop") =>
        db.ScalarAsync<Guid>("insert into connections (user_id, name, kind, host, port) values ($1, $2, $3, 'rdp-target', 3389) returning id", user, name, kind);

    private static async Task<string> CodeFor(HttpClient c, string cookie, Guid connection)
    {
        var r = await c.SendAsync(Post($"/api/connections/{connection}/agent/code", new { }, cookie));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("code").GetString()!;
    }

    private static async Task<string> TokenFor(HttpClient c, string code, string version = "0.3.0")
    {
        var r = await c.PostAsJsonAsync("/api/agent-link/enroll", new { code, version, platform = "test-linux" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task The_code_is_single_use_short_lived_and_the_token_is_only_stored_hashed()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var conn = await ConnectionOf(db, user);

        // ohne Sitzung kein Code
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post($"/api/connections/{conn}/agent/code", new { }, null))).StatusCode);

        var code = await CodeFor(c, cookie, conn);
        Assert.Matches("^[A-Z2-7]{5}-[A-Z2-7]{5}$", code);
        Assert.NotEqual(code, await db.ScalarAsync<string>("select code_hash from agent_codes"));      // nur der Hash steht in der Datenbank

        var token = await TokenFor(c, code);
        Assert.StartsWith("jat_", token);
        Assert.Equal(AgentCredentials.HashToken(token), await db.ScalarAsync<string>("select token_hash from agents"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from agent_codes"));

        // der Code gilt nur einmal
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/agent-link/enroll", new { code })).StatusCode);

        // abgelaufen
        var old = await CodeFor(c, cookie, conn);
        await db.ExecAsync("update agent_codes set expires_at = now() - interval '1 minute'");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/agent-link/enroll", new { code = old })).StatusCode);

        // ein neuer Code macht den vorigen ungueltig, und eine neue Einrichtung ersetzt das alte Token
        var first = await CodeFor(c, cookie, conn);
        var second = await CodeFor(c, cookie, conn);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/agent-link/enroll", new { code = first })).StatusCode);
        var token2 = await TokenFor(c, second);
        Assert.NotEqual(token, token2);
        Assert.Null(await new Backend.Data.AgentStore(db.Data, TimeProvider.System).FindByTokenAsync(token));
        Assert.NotNull(await new Backend.Data.AgentStore(db.Data, TimeProvider.System).FindByTokenAsync(token2));
    }

    [Fact]
    public async Task Only_the_owner_gets_a_code_and_trying_codes_is_limited()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var other = await db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreign = await ConnectionOf(db, other, "fremd");
        var terminal = await ConnectionOf(db, user, "Terminal", "terminal");

        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Post($"/api/connections/{foreign}/agent/code", new { }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Post($"/api/connections/{terminal}/agent/code", new { }, cookie))).StatusCode);   // nur Desktops
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{foreign}/agent", cookie))).StatusCode);

        // Raten: die Versuche je Adresse sind begrenzt (10 je Minute), auch mit lauter ungueltigen Codes
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++) statuses.Add((await c.PostAsJsonAsync("/api/agent-link/enroll", new { code = AgentCredentials.NewCode() })).StatusCode);
        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.BadRequest));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task The_link_needs_a_valid_token_and_a_recent_agent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var conn = await ConnectionOf(db, user);

        await Assert.ThrowsAnyAsync<Exception>(() => FakeLinkAgent.ConnectAsync(app.Server, null));
        await Assert.ThrowsAnyAsync<Exception>(() => FakeLinkAgent.ConnectAsync(app.Server, "jat_gibtesnicht"));
        // kein WebSocket-Aufbau: abgewiesen; und das Sitzungs-Cookie des Browsers ist kein Token
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/agent-link"))).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => FakeLinkAgent.ConnectAsync(app.Server, cookie.Split('=')[1]));

        var token = await TokenFor(c, await CodeFor(c, cookie, conn));
        await using (var good = await FakeLinkAgent.ConnectAsync(app.Server, token))
        {
            await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(conn));
            await Eventually(() => good.WelcomeText is not null);
            var status = await Json(await c.SendAsync(Send(HttpMethod.Get, "/api/agents", cookie)));
            var a = status.GetProperty("agents")[0];
            Assert.True(a.GetProperty("online").GetBoolean());
            Assert.Equal("0.3.0", a.GetProperty("version").GetString());
            Assert.Equal("test-linux", a.GetProperty("platform").GetString());
            Assert.False(a.GetProperty("outdated").GetBoolean());
        }

        // ein zu alter Agent wird mit Begruendung abgewiesen und geht nicht online
        await using (var old = await FakeLinkAgent.ConnectAsync(app.Server, token, version: "0.2.0"))
        {
            Assert.True(await old.WaitClosedAsync(Wait));
            Assert.False(app.Services.GetRequiredService<AgentLinks>().IsOnline(conn));
        }
        // seine Version steht trotzdem in der Anzeige
        await Eventually(async () => (await Json(await c.SendAsync(Send(HttpMethod.Get, "/api/agents", cookie)))).GetProperty("agents")[0].GetProperty("outdated").GetBoolean());
    }

    [Fact]
    public async Task Calls_take_the_link_of_the_named_connection_and_never_another_one()
    {
        await using var db = await TestDb.CreateAsync();
        await using var socket = new FakeAgent();
        await using var app = new Server(db, s => s.AddSingleton(new AgentOptions(socket.SocketPath)));
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var withAgent = await ConnectionOf(db, user, "Mit Agent");
        var withoutAgent = await ConnectionOf(db, user, "Lokal");
        var offline = await ConnectionOf(db, user, "Getrennt");
        var other = await db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreign = await ConnectionOf(db, other, "fremd");

        var token = await TokenFor(c, await CodeFor(c, cookie, withAgent));
        await TokenFor(c, await CodeFor(c, cookie, offline));
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, token);
        await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(withAgent));

        // ueber den Link
        var health = await Json(await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/health?c={withAgent}", cookie)));
        Assert.True(health.GetProperty("available").GetBoolean());
        Assert.Equal("link-test", health.GetProperty("platform").GetString());
        var windows = await Json(await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/windows?c={withAgent}", cookie)));
        Assert.Equal("Per Link", windows.GetProperty("windows")[0].GetProperty("title").GetString());
        Assert.Equal("Claude", (await Json(await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/apps?c={withAgent}", cookie)))).GetProperty("apps")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post($"/api/agent/windows/777/activate?c={withAgent}", new { maximize = true }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post($"/api/agent/apps/launch?c={withAgent}", new { id = "claude.desktop" }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post($"/api/agent/mode?c={withAgent}", new { mode = "apps" }, cookie))).StatusCode);
        lock (agent.Requests)
        {
            Assert.Contains(("POST", "/v1/windows/777/activate", """{"maximize":true}"""), agent.Requests);
            Assert.Contains(("POST", "/v1/apps/launch", """{"id":"claude.desktop"}"""), agent.Requests);
            Assert.Contains(("POST", "/v1/mode", """{"mode":"apps"}"""), agent.Requests);
        }
        lock (socket.Requests) Assert.Empty(socket.Requests);               // der Socket wurde nie gefragt

        // ungueltige Werte erreichen den Agenten auch ueber den Link nie
        var before = agent.Requests.Count;
        foreach (var path in new[] { "/api/agent/windows/abc/activate", "/api/agent/windows/1;rm/close", "/api/agent/windows/123/format" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Post($"{path}?c={withAgent}", new { }, cookie))).StatusCode);
        Assert.Equal(before, agent.Requests.Count);

        // eine Verbindung ohne Agent und der Aufruf ohne Angabe nehmen den lokalen Socket, wie bisher
        Assert.Equal("Claude", (await Json(await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/windows?c={withoutAgent}", cookie)))).GetProperty("windows")[0].GetProperty("title").GetString());
        Assert.Equal("Claude", (await Json(await c.SendAsync(Send(HttpMethod.Get, "/api/agent/windows", cookie)))).GetProperty("windows")[0].GetProperty("title").GetString());
        Assert.Equal(before, agent.Requests.Count);

        // eingerichtet, aber gerade getrennt: keine Antwort des lokalen Agenten, sondern "nicht verfuegbar"
        var gone = await Json(await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/health?c={offline}", cookie)));
        Assert.False(gone.GetProperty("available").GetBoolean());
        Assert.True(gone.GetProperty("enrolled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadGateway, (await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/windows?c={offline}", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload?c={offline}", cookie, [.. PngHead, 0, 0, 0, 13], "image/png"))).StatusCode);

        // fremde und kaputte Kennungen
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/windows?c={foreign}", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Send(HttpMethod.Get, "/api/agent/windows?c=nicht-eine-guid", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/windows?c={Guid.NewGuid()}", cookie))).StatusCode);
    }

    [Fact]
    public async Task A_second_agent_for_the_same_connection_replaces_the_first()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var conn = await ConnectionOf(db, user);
        var token = await TokenFor(c, await CodeFor(c, cookie, conn));

        await using var first = await FakeLinkAgent.ConnectAsync(app.Server, token);
        await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(conn));
        await using var second = await FakeLinkAgent.ConnectAsync(app.Server, token);
        Assert.True(await first.WaitClosedAsync(Wait));
        await Eventually(() => second.WelcomeText is not null);
        Assert.Equal("Per Link", (await Json(await c.SendAsync(Send(HttpMethod.Get, $"/api/agent/windows?c={conn}", cookie)))).GetProperty("windows")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Photo_file_and_clipboard_travel_through_the_link_in_pieces()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var conn = await ConnectionOf(db, user);
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, await TokenFor(c, await CodeFor(c, cookie, conn)));
        await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(conn));

        // Foto: 300 kB, also mehrere Stuecke
        var photo = new byte[300_000]; Random.Shared.NextBytes(photo); PngHead.CopyTo(photo, 0);
        var r = await c.SendAsync(Send(HttpMethod.Post, $"/api/upload?c={conn}", cookie, photo, "image/png"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        lock (agent.Finished)
        {
            var put = Assert.Single(agent.Finished);
            Assert.Equal("image", put.Kind);
            Assert.Equal(photo.Length, put.Size);
            Assert.Equal(photo, put.Data.ToArray());
        }

        // Kein Bild, leer, zu gross: der Server weist ab, ohne den Agenten zu fragen
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload?c={conn}", cookie, "kein bild"u8.ToArray(), "image/png"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload?c={conn}", cookie, [], "image/png"))).StatusCode);
        var tooBig = new byte[ClipboardUploader.MaxBytes + 1]; PngHead.CopyTo(tooBig, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload?c={conn}", cookie, tooBig, "image/png"))).StatusCode);

        // Datei: der Name wird bereinigt (kein Pfad), gefaehrliche Endungen kommen nie an
        var file = new byte[150_000]; Random.Shared.NextBytes(file);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload-file?c={conn}", cookie, file, fileName: @"..\..\etc/Bericht Q3?.pdf"))).StatusCode);
        lock (agent.Finished)
        {
            var put = agent.Finished[^1];
            Assert.Equal("file", put.Kind);
            Assert.Equal("Bericht Q3_.pdf", put.Name);
            Assert.Equal(file, put.Data.ToArray());
        }
        var count = agent.Finished.Count;
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload-file?c={conn}", cookie, file, fileName: "start.exe"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload-file?c={conn}", cookie, file, fileName: "../.."))).StatusCode);
        Assert.Equal(count, agent.Finished.Count);

        // der Helfer der Sitzung scheitert oder schweigt: der Fehler kommt beim Browser an
        agent.PutAnswer = (500, """{"error":"Zwischenablage nicht beschreibbar"}""");
        var failed = await c.SendAsync(Send(HttpMethod.Post, $"/api/upload?c={conn}", cookie, photo, "image/png"));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Contains("Zwischenablage nicht beschreibbar", (await Json(failed)).GetProperty("error").GetString());
        agent.PutAnswer = (504, """{"error":"keine Antwort"}""");
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload?c={conn}", cookie, photo, "image/png"))).StatusCode);

        // Bild aus der Zwischenablage der Ziel-Maschine holen
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Send(HttpMethod.Post, $"/api/clipboard/pull?c={conn}", cookie))).StatusCode);
        var png = new byte[90_000]; Random.Shared.NextBytes(png); PngHead.CopyTo(png, 0);
        agent.PullImage = png;
        var pulled = await c.SendAsync(Send(HttpMethod.Post, $"/api/clipboard/pull?c={conn}", cookie));
        Assert.Equal(HttpStatusCode.OK, pulled.StatusCode);
        Assert.Equal("image/png", pulled.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await pulled.Content.ReadAsByteArrayAsync());
        agent.PullImage = "kein png, nur Text"u8.ToArray();                  // der Server glaubt dem Agenten nicht blind
        Assert.Equal(HttpStatusCode.InternalServerError, (await c.SendAsync(Send(HttpMethod.Post, $"/api/clipboard/pull?c={conn}", cookie))).StatusCode);
        agent.PullImage = null; agent.PullError = (500, """{"error":"Fehler im Helfer"}""");
        Assert.Equal(HttpStatusCode.InternalServerError, (await c.SendAsync(Send(HttpMethod.Post, $"/api/clipboard/pull?c={conn}", cookie))).StatusCode);
    }

    [Fact]
    public async Task A_link_that_breaks_during_an_upload_answers_502_and_removing_the_agent_ends_everything()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var conn = await ConnectionOf(db, user);
        var token = await TokenFor(c, await CodeFor(c, cookie, conn));
        await using (var dropping = await FakeLinkAgent.ConnectAsync(app.Server, token))
        {
            dropping.DropDuringPut = true;
            await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(conn));
            var photo = new byte[200_000]; PngHead.CopyTo(photo, 0);
            Assert.Equal(HttpStatusCode.BadGateway, (await c.SendAsync(Send(HttpMethod.Post, $"/api/upload?c={conn}", cookie, photo, "image/png"))).StatusCode);
            await Eventually(() => !app.Services.GetRequiredService<AgentLinks>().IsOnline(conn));
        }

        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, token);
        await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(conn));
        // Agent entfernen: die Verbindung wird getrennt, das Token gilt nicht mehr, der Status ist weg
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{conn}/agent", cookie))).StatusCode);
        Assert.True(await agent.WaitClosedAsync(Wait));
        await Assert.ThrowsAnyAsync<Exception>(() => FakeLinkAgent.ConnectAsync(app.Server, token));
        Assert.Empty((await Json(await c.SendAsync(Send(HttpMethod.Get, "/api/agents", cookie)))).GetProperty("agents").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{conn}/agent", cookie))).StatusCode);

        // wird die Verbindung geloescht, geht der Agent mit
        var token2 = await TokenFor(c, await CodeFor(c, cookie, conn));
        await using var again = await FakeLinkAgent.ConnectAsync(app.Server, token2);
        await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(conn));
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{conn}", cookie))).StatusCode);
        Assert.True(await again.WaitClosedAsync(Wait));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from agents"));
    }

    [Fact]
    public async Task The_server_hands_out_only_the_three_agent_files()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var agent = await c.GetAsync("/agent-files/julos-agent.py");
        Assert.Equal(HttpStatusCode.OK, agent.StatusCode);
        Assert.Contains("AGENT_VERSION", await agent.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/agent-files/install-linux-agent.sh")).StatusCode);
        foreach (var name in new[] { "julos-user.sh", "..%2Fappsettings.json", "%2e%2e/JulOS.dll", "agent.json", "" })
            Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync("/agent-files/" + name)).StatusCode);
    }

    private static Task Eventually(Func<bool> condition) => Eventually(() => Task.FromResult(condition()));

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until) { if (await condition()) return; await Task.Delay(25); }
        Assert.Fail("Bedingung nicht rechtzeitig erfuellt");
    }
}
