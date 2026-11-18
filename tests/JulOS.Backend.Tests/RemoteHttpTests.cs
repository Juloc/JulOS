using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Integrations.Guacamole;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>Ticket und WebSocket durch den ganzen Server (echte Middleware und Datenbank, guacd ist ein Fake im Speicher).</summary>
public sealed class RemoteHttpTests
{
    private const string Password = "korrektes pferd batterie";
    private const string SetupCode = "TEST-CODE-0000";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Server(TestDb db, string guacd, string? allowed = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            builder.UseSetting("JULOS_GUACD", guacd);
            if (allowed is not null) builder.UseSetting("JULOS_ALLOWED_HOSTS", allowed);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
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
}
