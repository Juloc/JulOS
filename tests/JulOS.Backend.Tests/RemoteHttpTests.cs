using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Integrations;
using JulOS.Integrations.Guacamole;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using JulOS.Integrations.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>Ticket und WebSocket durch den ganzen Server (echte Middleware und Datenbank, guacd ist ein Fake im Speicher).</summary>
public sealed class RemoteHttpTests
{
    private const string Password = "korrektes pferd batterie";
    private const string SetupCode = "TEST-CODE-0000";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Server(TestDb db, string guacd, string? allowed = null, Action<IServiceCollection>? configure = null, string? secretKey = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            builder.UseSetting("JULOS_GUACD", guacd);
            if (allowed is not null) builder.UseSetting("JULOS_ALLOWED_HOSTS", allowed);
            if (secretKey is not null) builder.UseSetting("JULOS_SECRET_KEY", secretKey);
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

    private static HttpRequestMessage Post(string url, object body, string? cookie = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-JulOS", "1");
        req.Headers.Add("Origin", "http://localhost");
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    /// <summary>Richtet den ersten Benutzer ein und gibt das Sitzungs-Cookie und seine Kennung zurueck.</summary>
    private static async Task<(string Cookie, Guid UserId)> SignedInAsync(Server app, HttpClient c, TestDb db)
    {
        var begin = await c.SendAsync(Post("/api/setup/begin", new { code = SetupCode, user = "jul", password = Password }));
        var secret = JsonDocument.Parse(await begin.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
        var totp = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var finish = await c.SendAsync(Post("/api/setup/finish", new { code = SetupCode, user = "jul", totp }));
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        var cookie = finish.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        return (cookie, await db.ScalarAsync<Guid>("select id from users limit 1"));
    }

    private static async Task<string> TicketAsync(HttpClient c, string cookie, Guid connection, string password = "geheim")
    {
        var r = await c.SendAsync(Post("/api/remote/ticket", new { connection, password, width = 856, height = 1448 }, cookie));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("ticket").GetString()!;
    }

    private static async Task<WebSocket> OpenAsync(Server app, string ticket, string? cookie, string origin = "http://localhost")
    {
        var ws = app.Server.CreateWebSocketClient();
        ws.SubProtocols.Add("guacamole");                 // wie der Tunnel der Guacamole-Bibliothek im Browser
        ws.ConfigureRequest = r =>
        {
            if (cookie is not null) r.Headers["Cookie"] = cookie;
            if (origin.Length > 0) r.Headers["Origin"] = origin;
        };
        return await ws.ConnectAsync(new Uri($"ws://localhost/api/remote/ws?ticket={ticket}"), CancellationToken.None);
    }

    private static async Task<string> ReadAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(Wait);
        var buf = new byte[70000];
        var r = await ws.ReceiveAsync(buf, cts.Token);
        return Encoding.UTF8.GetString(buf, 0, r.Count);
    }

    [Fact]
    public async Task Desktop_and_terminal_connect_through_the_server_with_a_one_time_ticket()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd { Greeting = GuacProtocol.Encode("sync", "7") };
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "Test", "desktop", "ziel", 3389, "tester");

        var ticket = await TicketAsync(c, cookie, connection.Id);
        using var ws = await OpenAsync(app, ticket, cookie);
        Assert.Equal("guacamole", ws.SubProtocol);        // ohne gewaehltes Unterprotokoll bricht der Browser den Aufbau ab
        Assert.StartsWith("0.,", await ReadAsync(ws));
        Assert.Equal("4.sync,1.7;", await ReadAsync(ws));
        Assert.Equal(["select", "rdp"], guacd.Handshake[0]);
        Assert.Equal(["size", "856", "1448", "96"], guacd.Handshake[1]);
        // Zugangsdaten kommen aus der gespeicherten Verbindung und dem getippten Passwort
        var connect = guacd.Handshake[5];
        Assert.Contains("ziel", connect); Assert.Contains("3389", connect); Assert.Contains("tester", connect); Assert.Contains("geheim", connect);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await guacd.Closed.Task.WaitAsync(Wait);

        // dasselbe Ticket ein zweites Mal: abgelehnt
        var again = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(app, ticket, cookie));
        Assert.Contains("403", again.ToString());
    }

    private static async Task<string> SidTicketAsync(HttpClient c, string cookie, Guid connection, string sid)
    {
        var r = await c.SendAsync(Post("/api/remote/ticket", new { connection, password = "geheim", width = 856, height = 1448, sid }, cookie));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("ticket").GetString()!;
    }

    [Fact]
    public async Task Several_sessions_to_one_target_run_side_by_side_a_reload_replaces_only_itself_and_the_limit_is_per_user()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd { AcceptMany = true };
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}", configure: s => s.AddSingleton(new SessionRegistry(2)));
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "Test", "desktop", "ziel", 3389, "tester");

        using var one = await OpenAsync(app, await SidTicketAsync(c, cookie, connection.Id, "sitzung-eins"), cookie);
        Assert.StartsWith("0.,", await ReadAsync(one));
        using var two = await OpenAsync(app, await SidTicketAsync(c, cookie, connection.Id, "sitzung-zwei"), cookie);
        Assert.StartsWith("0.,", await ReadAsync(two));          // zwei Sitzungen zum selben Ziel: keine loest die andere ab

        // dritte: Obergrenze (hier 2), mit Meldung im Klartext
        using var three = await OpenAsync(app, await SidTicketAsync(c, cookie, connection.Id, "sitzung-drei"), cookie);
        await ReadAsync(three);
        var refused = await ReadAsync(three);
        Assert.Contains("Es sind schon 2 Sitzungen offen", refused);
        Assert.EndsWith(",3.797;", refused);

        // Neuladen der ersten Sitzung (gleiche Kennung): loest nur die erste ab, trotz Obergrenze
        using var oneAgain = await OpenAsync(app, await SidTicketAsync(c, cookie, connection.Id, "sitzung-eins"), cookie);
        Assert.StartsWith("0.,", await ReadAsync(oneAgain));
        var told = await ReadAsync(one);
        Assert.Contains("3.777", told);
        Assert.Equal(WebSocketState.Open, two.State);            // die zweite Sitzung ist unberuehrt

        await oneAgain.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await two.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task Ticket_and_socket_need_a_session_and_the_own_origin()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd();
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // ohne Anmeldung
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post("/api/remote/ticket", new { connection = Guid.NewGuid(), password = "x" }))).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(app, "egal", null));

        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "Test", "terminal", "ziel", 22, "tester");

        // fehlende Angaben, fremde Verbindung
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Post("/api/remote/ticket", new { connection = connection.Id }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Post("/api/remote/ticket", new { connection = Guid.NewGuid(), password = "x" }, cookie))).StatusCode);

        // fremde Seite (Origin) darf den WebSocket nicht aufbauen, auch nicht mit gueltigem Ticket
        var ticket = await TicketAsync(c, cookie, connection.Id);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(app, ticket, cookie, origin: "https://boese.example"));
        Assert.Contains("403", ex.ToString());
        // das Ticket wurde dabei nicht verbraucht, die eigene Seite kommt durch
        using var ws = await OpenAsync(app, ticket, cookie);
        Assert.StartsWith("0.,", await ReadAsync(ws));
    }

    [Fact]
    public async Task The_allow_list_limits_the_targets()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd();
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}", allowed: "erlaubt.example, 10.0.0.5");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var store = new ConnectionStore(db.Data);
        var no = await store.CreateAsync(userId, "Nein", "terminal", "anderes.example", 22, "u");
        var yes = await store.CreateAsync(userId, "Ja", "terminal", "ERLAUBT.example", 22, "u");

        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post("/api/remote/ticket", new { connection = no.Id, password = "x" }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post("/api/remote/ticket", new { connection = yes.Id, password = "x" }, cookie))).StatusCode);
    }

    [Fact]
    public async Task Another_users_connection_cannot_be_used()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd();
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);
        await db.ExecAsync("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('anna', 'x', 'x', true)");
        var annaId = await db.ScalarAsync<Guid>("select id from users where name = 'anna'");
        var annas = await new ConnectionStore(db.Data).CreateAsync(annaId, "Annas", "terminal", "ziel", 22, "u");

        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Post("/api/remote/ticket", new { connection = annas.Id, password = "x" }, cookie))).StatusCode);
    }

    [Fact]
    public async Task A_logged_out_session_is_no_longer_alive_for_open_connections()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd();
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "Test", "terminal", "ziel", 22, "u");
        using var ws = await OpenAsync(app, await TicketAsync(c, cookie, connection.Id), cookie);
        await ReadAsync(ws);

        // die Sitzung pruefen wir hier direkt ueber ihre stabile Kennung (der Relay fragt dasselbe alle 60 s)
        var id = await db.ScalarAsync<Guid>("select id from sessions limit 1");
        var sessions = app.Services.GetRequiredService<SessionStore>();
        Assert.True(await sessions.IsAliveAsync(id));
        var logout = await c.SendAsync(Post("/api/auth/logout", new { }, cookie));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False(await sessions.IsAliveAsync(id));
    }

    [Fact]
    public async Task A_terminal_connects_through_the_server_against_real_guacd_and_ssh_and_accepts_keys()
    {
        var real = Environment.GetEnvironmentVariable("JULOS_TEST_GUACD");
        var password = Environment.GetEnvironmentVariable("JULOS_TEST_TARGET_PASSWORD");
        Assert.SkipWhen(real is null || password is null, "JULOS_TEST_GUACD / JULOS_TEST_TARGET_PASSWORD nicht gesetzt (deploy/dev/test.sh benutzen)");
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, real!);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "Terminal", "terminal", "test-ssh", 2222, "tester");

        using var ws = await OpenAsync(app, await TicketAsync(c, cookie, connection.Id, password!), cookie);
        Assert.StartsWith("0.,", await ReadAsync(ws));
        var all = new StringBuilder();
        while (!all.ToString().Contains("4.sync,")) all.Append(await ReadAsync(ws));
        Assert.DoesNotContain("5.error,", all.ToString());
        // Tasten werden angenommen und die Verbindung bleibt offen (die Terminal-Ausgabe kommt als Bild, nicht als Text, und wird hier nicht ausgewertet)
        foreach (var ch in "echo julos-ok\n")
        {
            var key = ch == '\n' ? 65293 : ch;                                     // Enter ist X11-Tastencode 0xFF0D
            await ws.SendAsync(GuacProtocol.Encode("key", key.ToString(), "1"), WebSocketMessageType.Text, true, CancellationToken.None);
            await ws.SendAsync(GuacProtocol.Encode("key", key.ToString(), "0"), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    // ---- Agent, Upload, Log ----

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private static HttpRequestMessage PostRaw(string url, byte[] body, string contentType, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        req.Headers.Add("X-JulOS", "1");
        req.Headers.Add("Origin", "http://localhost");
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static HttpRequestMessage GetWith(string url, string? cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    [Fact]
    public async Task Agent_upload_and_log_need_a_session_and_the_own_page()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "127.0.0.1:1");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        foreach (var path in new[] { "/api/agent/health", "/api/agent/windows", "/api/agent/apps" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(GetWith(path, null))).StatusCode);

        var (cookie, _) = await SignedInAsync(app, c, db);
        foreach (var path in new[] { "/api/agent/mode", "/api/agent/apps/launch", "/api/log" })
        {
            var noHeader = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
            noHeader.Headers.Add("Cookie", cookie);
            noHeader.Headers.Add("Origin", "http://localhost");
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(noHeader)).StatusCode);       // ohne X-JulOS
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post(path, new { }))).StatusCode);  // ohne Sitzung
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(PostRaw("/api/upload", Png, "image/png", "julos_sid=falsch"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(PostRaw("/api/upload-file", Png, "application/octet-stream", "julos_sid=falsch"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(PostRaw("/api/clipboard/pull", [], "application/octet-stream", "julos_sid=falsch"))).StatusCode);
    }

    [Fact]
    public async Task A_file_goes_to_the_helper_and_the_clipboard_pull_returns_the_image()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "julos-upf-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, "127.0.0.1:1", configure: s => s.AddSingleton(new UploadOptions(dir, TimeSpan.FromMilliseconds(500))));
            var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var (cookie, _) = await SignedInAsync(app, c, db);

            var helper = Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var d = Directory.Exists(Path.Combine(dir, "files")) ? Directory.GetDirectories(Path.Combine(dir, "files")).FirstOrDefault(x => !x.EndsWith(".tmp")) : null;
                    if (d is not null) { await File.WriteAllTextAsync(Path.Combine(d, ".ok"), ""); return d; }
                    await Task.Delay(10);
                }
                return null;
            });
            HttpRequestMessage FileReq(string name, byte[] body)
            {
                var req = PostRaw("/api/upload-file", body, "application/octet-stream", cookie);
                req.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
                return req;
            }

            var ok = await c.SendAsync(FileReq("Übersicht Q3.txt", "abc"u8.ToArray()));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var saved = await helper;
            Assert.Equal("abc", await System.IO.File.ReadAllTextAsync(Path.Combine(saved!, "Übersicht Q3.txt")));

            var blocked = await c.SendAsync(FileReq("setup.exe", [1]));
            Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
            Assert.Contains(".exe", (await Json(blocked)).GetProperty("error").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(FileReq("..", [1]))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(FileReq("leer.txt", []))).StatusCode);
            Assert.Equal(HttpStatusCode.GatewayTimeout, (await c.SendAsync(FileReq("a.txt", [1]))).StatusCode);   // kein Helfer
            var noHeader = PostRaw("/api/upload-file", [1], "application/octet-stream", cookie);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(noHeader)).StatusCode);                // kein Name

            // Bild aus der Zwischenablage der VM: Helfer antwortet mit einem PNG, danach mit "nichts"
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9 };
            var pull = Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var req = Directory.Exists(Path.Combine(dir, "pull")) ? Directory.GetFiles(Path.Combine(dir, "pull"), "*.req").FirstOrDefault() : null;
                    if (req is not null) { await System.IO.File.WriteAllBytesAsync(req[..^4] + ".png", png); return; }
                    await Task.Delay(10);
                }
            });
            var got = await c.SendAsync(PostRaw("/api/clipboard/pull", [], "application/octet-stream", cookie));
            await pull;
            Assert.Equal(HttpStatusCode.OK, got.StatusCode);
            Assert.Equal("image/png", got.Content.Headers.ContentType?.MediaType);
            Assert.Equal(png, await got.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.GatewayTimeout, (await c.SendAsync(PostRaw("/api/clipboard/pull", [], "application/octet-stream", cookie))).StatusCode);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task The_agent_api_is_passed_through_with_checked_values_only()
    {
        await using var db = await TestDb.CreateAsync();
        await using var agent = new FakeAgent();
        await using var app = new Server(db, "127.0.0.1:1", configure: s => s.AddSingleton(new AgentOptions(agent.SocketPath)));
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        var health = await Json(await c.SendAsync(GetWith("/api/agent/health", cookie)));
        Assert.True(health.GetProperty("available").GetBoolean());
        Assert.Equal("linux", health.GetProperty("platform").GetString());

        var windows = await Json(await c.SendAsync(GetWith("/api/agent/windows", cookie)));
        Assert.Equal("Claude", windows.GetProperty("windows")[0].GetProperty("title").GetString());
        Assert.Equal("Claude", (await Json(await c.SendAsync(GetWith("/api/agent/apps", cookie)))).GetProperty("apps")[0].GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post("/api/agent/windows/12345/activate", new { maximize = true }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post("/api/agent/windows/12345/minimize", new { }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post("/api/agent/apps/launch", new { id = "claude.desktop" }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post("/api/agent/mode", new { mode = "apps" }, cookie))).StatusCode);
        lock (agent.Requests)
        {
            Assert.Contains(("POST", "/v1/windows/12345/activate", """{"maximize":true}"""), agent.Requests);
            Assert.Contains(("POST", "/v1/apps/launch", """{"id":"claude.desktop"}"""), agent.Requests);
            Assert.Contains(("POST", "/v1/mode", """{"mode":"apps"}"""), agent.Requests);
        }

        // ungueltige Werte erreichen den Agenten nie
        var before = agent.Requests.Count;
        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/agent/windows/abc/activate", new { }), ("/api/agent/windows/1;rm/close", new { }), ("/api/agent/windows/123/format", new { }),
            ("/api/agent/windows/1234567890123/close", new { }), ("/api/agent/mode", new { mode = "x" }), ("/api/agent/apps/launch", new { id = "" }),
            ("/api/agent/apps/launch", new { id = new string('a', 201) }),
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Post(path, body, cookie))).StatusCode);
        Assert.Equal(before, agent.Requests.Count);
    }

    [Fact]
    public async Task Without_an_agent_the_window_bar_is_off_and_the_rest_answers_502()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "127.0.0.1:1", configure: s => s.AddSingleton(new AgentOptions("/tmp/julos-kein-agent.sock")));
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        Assert.False((await Json(await c.SendAsync(GetWith("/api/agent/health", cookie)))).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.BadGateway, (await c.SendAsync(GetWith("/api/agent/windows", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await c.SendAsync(Post("/api/agent/mode", new { mode = "desktop" }, cookie))).StatusCode);
    }

    [Fact]
    public async Task A_photo_goes_to_the_helper_and_bad_uploads_are_refused()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "julos-up-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, "127.0.0.1:1", configure: s => s.AddSingleton(new UploadOptions(dir, TimeSpan.FromMilliseconds(400))));
            var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var (cookie, _) = await SignedInAsync(app, c, db);

            // Helfer in der Sitzung: bestaetigt das Bild
            var helper = Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    var f = Directory.GetFiles(dir).FirstOrDefault(x => x.EndsWith(".png"));
                    if (f is not null) { await File.WriteAllTextAsync(f + ".ok", ""); return; }
                    await Task.Delay(10);
                }
            });
            var ok = await c.SendAsync(PostRaw("/api/upload", Png, "image/png", cookie));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.True((await Json(ok)).GetProperty("ok").GetBoolean());
            await helper;

            // kein Bild (egal was der Absender behauptet), leer, kein Helfer
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(PostRaw("/api/upload", Encoding.UTF8.GetBytes("<html>"), "image/png", cookie))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(PostRaw("/api/upload", [], "image/png", cookie))).StatusCode);
            Assert.Equal(HttpStatusCode.GatewayTimeout, (await c.SendAsync(PostRaw("/api/upload", Png, "image/png", cookie))).StatusCode);

            // zu viele Uploads pro Minute
            HttpStatusCode last = default;
            for (var i = 0; i < 25; i++) last = (await c.SendAsync(PostRaw("/api/upload", [], "image/png", cookie))).StatusCode;
            Assert.Equal(HttpStatusCode.TooManyRequests, last);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string category) => new L(this, category);
        public void Dispose() { }
        private sealed class L(CaptureLogs o, string cat) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f) { lock (o.Lines) o.Lines.Add(cat + ": " + f(state, ex)); }
        }
    }

    [Fact]
    public async Task The_client_log_keeps_only_numbers_flags_and_short_words()
    {
        await using var db = await TestDb.CreateAsync();
        var logs = new CaptureLogs();
        await using var app = new Server(db, "127.0.0.1:1", configure: s => s.AddLogging(l => l.AddProvider(logs)));
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        var body = new
        {
            events = new object[]
            {
                new Dictionary<string, object> { ["evt"] = "firstframe", ["ms"] = 1503, ["ok"] = true, ["text"] = new string('x', 25), ["mit leerzeichen"] = 1, ["verschachtelt"] = new { a = 1 }, ["liste"] = new[] { 1 } },
                "kein objekt",
            },
        };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Post("/api/log", body, cookie))).StatusCode);
        string[] lines;
        lock (logs.Lines) lines = [.. logs.Lines.Where(l => l.StartsWith("Client:"))];
        var line = Assert.Single(lines);
        Assert.Contains("\"evt\":\"firstframe\"", line);
        Assert.Contains("\"ms\":1503", line);
        Assert.Contains("\"ok\":true", line);
        Assert.DoesNotContain("xxxx", line);
        Assert.DoesNotContain("leerzeichen", line);
        Assert.DoesNotContain("verschachtelt", line);
        Assert.DoesNotContain("liste", line);

        // zu gross, kein JSON, zu oft
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await c.SendAsync(Post("/api/log", new { events = new[] { new { x = new string('a', 13000) } } }, cookie))).StatusCode);
        var bad = PostRaw("/api/log", Encoding.UTF8.GetBytes("{kaputt"), "application/json", cookie);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(bad)).StatusCode);
        HttpStatusCode last = default;
        for (var i = 0; i < 65; i++) last = (await c.SendAsync(Post("/api/log", new { events = Array.Empty<object>() }, cookie))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    // ---- Verbindungen verwalten ----

    private static HttpRequestMessage Send(HttpMethod method, string url, object? body, string cookie)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        req.Headers.Add("X-JulOS", "1");
        req.Headers.Add("Origin", "http://localhost");
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static readonly string TestKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());

    [Fact]
    public async Task A_stored_password_is_kept_encrypted_used_by_the_server_and_never_sent_back()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd();
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}", secretKey: TestKey);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);
        const string stored = "Gespeichert-Passwort-123";

        var created = await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "PC", kind = "desktop", host = "ziel", username = "jul", password = stored }, cookie));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(stored, createdText);
        var id = JsonDocument.Parse(createdText).RootElement.GetProperty("id").GetGuid();
        Assert.True(JsonDocument.Parse(createdText).RootElement.GetProperty("hasPassword").GetBoolean());

        // in der Datenbank steht nur Chiffre, nie das Passwort; die Liste sagt nur "ist da"
        var raw = await db.ScalarAsync<byte[]>("select secret from connections where id = $1", id);
        Assert.DoesNotContain(stored, Encoding.UTF8.GetString(raw));
        var listText = await (await c.SendAsync(GetWith("/api/connections", cookie))).Content.ReadAsStringAsync();
        Assert.DoesNotContain(stored, listText);
        var list = JsonDocument.Parse(listText).RootElement;
        Assert.True(list.GetProperty("canStorePasswords").GetBoolean());
        Assert.True(list.GetProperty("connections")[0].GetProperty("hasPassword").GetBoolean());

        // Ticket ohne getipptes Passwort: der Server nimmt das gespeicherte und gibt es an guacd
        var noPassword = await c.SendAsync(Post("/api/remote/ticket", new { connection = id, width = 856, height = 1448 }, cookie));
        Assert.Equal(HttpStatusCode.OK, noPassword.StatusCode);
        var ticket = JsonDocument.Parse(await noPassword.Content.ReadAsStringAsync()).RootElement.GetProperty("ticket").GetString()!;
        Assert.DoesNotContain(stored, await noPassword.Content.ReadAsStringAsync());
        using (var ws = await OpenAsync(app, ticket, cookie)) { await ReadAsync(ws); }
        await guacd.Closed.Task.WaitAsync(Wait);
        Assert.Contains(stored, guacd.Handshake[5]);

        // unveraendertes Ziel behaelt das Passwort, anderes Ziel oder "vergessen" entfernt es
        var same = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "Neuer Name", kind = "desktop", host = "ziel", username = "jul" }, cookie)));
        Assert.True(same.GetProperty("hasPassword").GetBoolean());
        var moved = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "Neuer Name", kind = "desktop", host = "anderes", username = "jul" }, cookie)));
        Assert.False(moved.GetProperty("hasPassword").GetBoolean());
        Assert.True(await db.ScalarAsync<bool>("select secret is null from connections where id = $1", id));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Post("/api/remote/ticket", new { connection = id }, cookie))).StatusCode);

        // neu setzen und mit "vergessen" wieder loeschen
        await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "x", kind = "desktop", host = "anderes", username = "jul", password = "neu" }, cookie));
        Assert.False(await db.ScalarAsync<bool>("select secret is null from connections where id = $1", id));
        var forgotten = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "x", kind = "desktop", host = "anderes", username = "jul", forgetPassword = true }, cookie)));
        Assert.False(forgotten.GetProperty("hasPassword").GetBoolean());
    }

    [Fact]
    public async Task Without_a_secret_key_passwords_cannot_be_stored()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd();
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        var r = await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "PC", kind = "desktop", host = "ziel", username = "jul", password = "x" }, cookie));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from connections"));
        Assert.False((await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("canStorePasswords").GetBoolean());
    }

    [Fact]
    public async Task Connections_can_be_created_listed_changed_and_deleted_by_their_owner_only()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "127.0.0.1:1", allowed: "erlaubt.example, 10.0.0.5, ziel");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);

        Assert.Empty((await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("connections").EnumerateArray());

        var created = await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "  Mein PC ", kind = "desktop", host = "ziel", username = "jul" }, cookie));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var dto = await Json(created);
        Assert.Equal("Mein PC", dto.GetProperty("name").GetString());           // bereinigt
        Assert.Equal(3389, dto.GetProperty("port").GetInt32());                  // Standardport der Art
        var id = dto.GetProperty("id").GetGuid();

        var terminal = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "Shell", kind = "terminal", host = "10.0.0.5" }, cookie)));
        Assert.Equal(22, terminal.GetProperty("port").GetInt32());
        var list = (await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("connections");
        Assert.Equal(new[] { "Mein PC", "Shell" }, list.EnumerateArray().Select(x => x.GetProperty("name").GetString()));   // in der Reihenfolge des Anlegens

        var changed = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "Büro", kind = "desktop", host = "erlaubt.example", port = 3390, username = "jul" }, cookie)));
        Assert.Equal(("Büro", "erlaubt.example", 3390), (changed.GetProperty("name").GetString(), changed.GetProperty("host").GetString(), changed.GetProperty("port").GetInt32()));

        // fremde Verbindungen sind unsichtbar und unveraenderlich
        await db.ExecAsync("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('anna', 'x', 'x', true)");
        var annas = await new ConnectionStore(db.Data).CreateAsync(await db.ScalarAsync<Guid>("select id from users where name = 'anna'"), "Annas", "terminal", "ziel", 22, "a", "guac");
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{annas.Id}", new { name = "x", kind = "terminal", host = "ziel" }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{annas.Id}", null, cookie))).StatusCode);
        Assert.Equal(2, (await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("connections").GetArrayLength());
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from connections where id = $1", annas.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{id}", null, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{id}", null, cookie))).StatusCode);
        Assert.Equal(1, (await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("connections").GetArrayLength());
    }

    [Fact]
    public async Task The_engine_is_saved_per_connection_kept_on_change_and_decides_the_ticket()
    {
        await using var db = await TestDb.CreateAsync();
        await using var guacd = new FakeGuacd();
        await using var app = new Server(db, $"127.0.0.1:{guacd.Port}", allowed: "ziel", secretKey: TestKey);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        // Ohne Angabe: Guacamole. Mit Angabe wird gespeichert, ein Terminal bleibt immer bei Guacamole.
        var plain = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "A", kind = "desktop", host = "ziel", username = "jul" }, cookie)));
        Assert.Equal("guac", plain.GetProperty("engine").GetString());
        var streamed = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "B", kind = "desktop", host = "ziel", username = "jul", engine = "stream", password = "pw" }, cookie)));
        Assert.Equal("stream", streamed.GetProperty("engine").GetString());
        var id = streamed.GetProperty("id").GetGuid();
        var shell = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "C", kind = "terminal", host = "ziel", engine = "stream" }, cookie)));
        Assert.Equal("guac", shell.GetProperty("engine").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "D", kind = "desktop", host = "ziel", engine = "vnc" }, cookie))).StatusCode);

        // Eine Aenderung ohne Engine behaelt die bisherige; mit Engine wechselt sie
        var kept = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "B2", kind = "desktop", host = "ziel", username = "jul" }, cookie)));
        Assert.Equal("stream", kept.GetProperty("engine").GetString());
        var list = (await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("connections");
        Assert.Equal(new[] { "guac", "stream", "guac" }, list.EnumerateArray().Select(x => x.GetProperty("engine").GetString()));

        // Das Ticket folgt der Engine der Verbindung (Stream nur, wenn der Kodierer da ist); die Seite kann sie ausdruecklich ueberstimmen
        var expected = JulOS.Integrations.Display.ScreenStream.IsAvailable ? "stream" : "guac";
        var byConnection = await Json(await c.SendAsync(Post("/api/remote/ticket", new { connection = id, width = 856, height = 1448 }, cookie)));
        Assert.Equal(expected, byConnection.GetProperty("engine").GetString());
        var forcedGuac = await Json(await c.SendAsync(Post("/api/remote/ticket", new { connection = id, width = 856, height = 1448, engine = "guac" }, cookie)));
        Assert.Equal("guac", forcedGuac.GetProperty("engine").GetString());
        var plainId = plain.GetProperty("id").GetGuid();
        var plainForced = await Json(await c.SendAsync(Post("/api/remote/ticket", new { connection = plainId, password = "x", width = 856, height = 1448, engine = "stream" }, cookie)));
        Assert.Equal(expected, plainForced.GetProperty("engine").GetString());
        var shellTicket = await Json(await c.SendAsync(Post("/api/remote/ticket", new { connection = shell.GetProperty("id").GetGuid(), password = "x", engine = "stream" }, cookie)));
        Assert.Equal("guac", shellTicket.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Bad_connection_input_and_forbidden_targets_are_refused_with_a_reason()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, "127.0.0.1:1", allowed: "ziel");
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        foreach (var body in new object[]
        {
            new { name = "", kind = "desktop", host = "ziel" },
            new { name = new string('a', 61), kind = "desktop", host = "ziel" },
            new { name = "x", kind = "screen", host = "ziel" },                     // VNC kommt spaeter
            new { name = "x", kind = "desktop", host = "ziel mit leerzeichen" },
            new { name = "x", kind = "desktop", host = "ziel/pfad" },
            new { name = "x", kind = "desktop", host = "" },
            new { name = "x", kind = "desktop", host = "ziel", port = 0 },
            new { name = "x", kind = "desktop", host = "ziel", port = 70000 },
            new { name = "x", kind = "desktop", host = "ziel", username = new string('u', 65) },
        })
        {
            var r = await c.SendAsync(Send(HttpMethod.Post, "/api/connections", body, cookie));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.False(string.IsNullOrEmpty((await Json(r)).GetProperty("error").GetString()));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "x", kind = "desktop", host = "anderes" }, cookie))).StatusCode);

        for (var i = 0; i < 50; i++) Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "n" + i, kind = "terminal", host = "ziel" }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "zu viel", kind = "terminal", host = "ziel" }, cookie))).StatusCode);
    }
}
