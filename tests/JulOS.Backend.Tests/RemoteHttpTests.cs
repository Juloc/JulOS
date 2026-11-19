using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using JulOS.Integrations.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>Ticket und WebSocket durch den ganzen Server (echte Middleware und Datenbank; der Desktop-Strom verbindet gegen einen geschlossenen Port, sodass der Fehlerweg geprueft wird).</summary>
public sealed class RemoteHttpTests
{
    private const string Password = "korrektes pferd batterie";
    private const string SetupCode = "TEST-CODE-0000";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Server(TestDb db, string? allowed = null, Action<IServiceCollection>? configure = null, string? secretKey = null, string? driveRoot = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            if (allowed is not null) builder.UseSetting("JULOS_ALLOWED_HOSTS", allowed);
            if (secretKey is not null) builder.UseSetting("JULOS_SECRET_KEY", secretKey);
            if (driveRoot is not null) builder.UseSetting("JULOS_DRIVE_ROOT", driveRoot);
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
        ws.ConfigureRequest = r =>
        {
            if (cookie is not null) r.Headers["Cookie"] = cookie;
            if (origin.Length > 0) r.Headers["Origin"] = origin;
        };
        return await ws.ConnectAsync(new Uri($"ws://localhost/api/remote/screen?ticket={ticket}"), CancellationToken.None);
    }

    /// <summary>
    /// Liest Nachrichten des Bildschirm-Stroms bis zur ersten Fehlermeldung (Typ 4: Code, UTF-8-Text). Schliesst der Server vorher (das Ziel ist
    /// sofort weg, und manchmal ist der Schliessen-Rahmen schneller als die Meldung), ist der Code 0.
    /// </summary>
    private static async Task<(int Code, string Text)> ReadErrorAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(Wait);
        var buf = new byte[70000];
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return (0, "");
            if (buf[0] == 4) return (buf[1] << 8 | buf[2], Encoding.UTF8.GetString(buf, 3, r.Count - 3));
        }
    }

    [Fact]
    public async Task A_desktop_ticket_opens_the_screen_socket_once_and_a_failed_connection_is_reported()
    {
        Assert.SkipUnless(JulOS.Integrations.Display.ScreenStream.IsAvailable, "libopenh264 fehlt: ohne Kodierer gibt es keine Desktop-Verbindung");
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "Test", "desktop", "127.0.0.1", 1, "tester");

        var response = await c.SendAsync(Post("/api/remote/ticket", new { connection = connection.Id, password = "geheim", width = 858, height = 1449 }, cookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var j = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("stream", j.GetProperty("engine").GetString());
        Assert.Equal("desktop", j.GetProperty("kind").GetString());
        Assert.Equal((856, 1448), (j.GetProperty("width").GetInt32(), j.GetProperty("height").GetInt32()));     // Breite durch 2, Hoehe gerade
        var ticket = j.GetProperty("ticket").GetString()!;

        // Das Ziel ist nicht erreichbar: der Server sagt es im Klartext und schliesst
        using var ws = await OpenAsync(app, ticket, cookie);
        var (code, text) = await ReadErrorAsync(ws);
        Assert.True(code is 0 or 515 or 520, $"unexpected status {code}: {text}");
        Assert.Equal(code == 0, string.IsNullOrEmpty(text));

        // dasselbe Ticket ein zweites Mal: abgelehnt
        var again = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(app, ticket, cookie));
        Assert.Contains("403", again.ToString());
    }

    [Fact]
    public async Task Ticket_and_socket_need_a_session_and_the_own_origin()
    {
        Assert.SkipUnless(JulOS.Integrations.Display.ScreenStream.IsAvailable, "libopenh264 fehlt: ohne Kodierer gibt es keine Desktop-Verbindung");
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // ohne Anmeldung
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post("/api/remote/ticket", new { connection = Guid.NewGuid(), password = "x" }))).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(app, "egal", null));

        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "Test", "desktop", "127.0.0.1", 1, "tester");

        // fehlende Angaben, fremde Verbindung
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Post("/api/remote/ticket", new { connection = connection.Id }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Post("/api/remote/ticket", new { connection = Guid.NewGuid(), password = "x" }, cookie))).StatusCode);

        // fremde Seite (Origin) darf den WebSocket nicht aufbauen, auch nicht mit gueltigem Ticket
        var ticket = await TicketAsync(c, cookie, connection.Id);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(app, ticket, cookie, origin: "https://boese.example"));
        Assert.Contains("403", ex.ToString());
        // das Ticket wurde dabei nicht verbraucht, die eigene Seite kommt durch
        using var ws = await OpenAsync(app, ticket, cookie);
        await ReadErrorAsync(ws);                       // das Ticket gilt: der Server nimmt den Socket an und meldet, dass das Ziel nicht da ist
    }

    [Fact]
    public async Task The_allow_list_limits_the_targets()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, allowed: "erlaubt.example, 10.0.0.5");
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
        await using var app = new Server(db);
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
        Assert.SkipUnless(JulOS.Integrations.Display.ScreenStream.IsAvailable, "libopenh264 fehlt: ohne Kodierer gibt es keine Desktop-Verbindung");
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "Test", "desktop", "127.0.0.1", 1, "u");
        using var ws = await OpenAsync(app, await TicketAsync(c, cookie, connection.Id), cookie);
        await ReadErrorAsync(ws);

        // die Sitzung pruefen wir hier direkt ueber ihre stabile Kennung (der Relay fragt dasselbe alle 60 s)
        var id = await db.ScalarAsync<Guid>("select id from sessions limit 1");
        var sessions = app.Services.GetRequiredService<SessionStore>();
        Assert.True(await sessions.IsAliveAsync(id));
        var logout = await c.SendAsync(Post("/api/auth/logout", new { }, cookie));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False(await sessions.IsAliveAsync(id));
    }

    // ---- Terminal (eigener SSH-Client) ----

    private static async Task<WebSocket> OpenSshAsync(Server app, string ticket, string cookie, string origin = "http://localhost")
    {
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => { r.Headers["Cookie"] = cookie; if (origin.Length > 0) r.Headers["Origin"] = origin; };
        return await ws.ConnectAsync(new Uri($"ws://localhost/api/remote/ssh?ticket={ticket}"), CancellationToken.None);
    }

    private static async Task<(byte Kind, JsonElement Body)> ReadMessageAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buf = new byte[70000];
        var r = await ws.ReceiveAsync(buf, cts.Token);
        return buf[0] == 1 ? (1, default) : (buf[0], JsonDocument.Parse(buf.AsMemory(1, r.Count - 1)).RootElement.Clone());
    }

    private static async Task<string> ReadShellUntilAsync(WebSocket ws, string needle)
    {
        var all = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buf = new byte[70000];
        while (!all.ToString().Contains(needle))
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close || buf[0] != 1) break;
            all.Append(Encoding.UTF8.GetString(buf, 1, r.Count - 1));
        }
        return all.ToString();
    }

    [Fact]
    public async Task A_terminal_runs_through_the_server_with_trust_on_first_use_and_a_changed_key_needs_confirmation()
    {
        var (sshHost, sshPort, targetPassword) = RealSshTests.Target();
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var store = new ConnectionStore(db.Data);
        var connection = await store.CreateAsync(userId, "Terminal", "terminal", sshHost, sshPort, "tester");

        async Task<string> TicketFor(object body)
        {
            var r = await c.SendAsync(Post("/api/remote/ticket", body, cookie));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("ssh", j.GetProperty("engine").GetString());
            Assert.Equal("terminal", j.GetProperty("kind").GetString());
            return j.GetProperty("ticket").GetString()!;
        }

        // 1. erste Verbindung: Anmeldung, Shell, Eingabe/Ausgabe, Schluessel wird gemerkt
        var ticket = await TicketFor(new { connection = connection.Id, password = targetPassword, cols = 90, rows = 25 });
        string fingerprint;
        using (var ws = await OpenSshAsync(app, ticket, cookie))
        {
            var (kind, ready) = await ReadMessageAsync(ws);
            Assert.Equal(2, kind);
            Assert.Equal("new", ready.GetProperty("hostKey").GetString());
            fingerprint = ready.GetProperty("fingerprint").GetString()!;
            await ws.SendAsync(Encoding.UTF8.GetBytes("\u0001stty size; echo ok$((20+22))\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
            var output = await ReadShellUntilAsync(ws, "ok42");
            Assert.Contains("25 90", output);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        var stored = (await store.GetAsync(userId, connection.Id))!.HostKey;
        Assert.EndsWith(fingerprint, stored);
        Assert.StartsWith("ssh-", stored);
        // dasselbe Ticket ein zweites Mal: abgelehnt
        Assert.Contains("403", (await Assert.ThrowsAnyAsync<Exception>(() => OpenSshAsync(app, ticket, cookie))).ToString());

        // 2. zweite Verbindung: bekannt
        using (var ws = await OpenSshAsync(app, await TicketFor(new { connection = connection.Id, password = targetPassword }), cookie))
        {
            Assert.Equal("known", (await ReadMessageAsync(ws)).Body.GetProperty("hostKey").GetString());
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        // 3. gespeicherter Schluessel weicht ab: Warnung mit beiden Fingerabdruecken, keine Shell, nichts ueberschrieben
        await store.SetHostKeyAsync(userId, connection.Id, "ssh-ed25519 SHA256:alterSchluessel");
        using (var ws = await OpenSshAsync(app, await TicketFor(new { connection = connection.Id, password = targetPassword }), cookie))
        {
            var (kind, end) = await ReadMessageAsync(ws);
            Assert.Equal(3, kind);
            Assert.Equal(800, end.GetProperty("code").GetInt32());
            Assert.Equal("ssh-ed25519 SHA256:alterSchluessel", end.GetProperty("previous").GetString());
            Assert.EndsWith(fingerprint, end.GetProperty("fingerprint").GetString());
            Assert.DoesNotContain(targetPassword, end.ToString());
        }
        Assert.Equal("ssh-ed25519 SHA256:alterSchluessel", (await store.GetAsync(userId, connection.Id))!.HostKey);

        // 4. eine Bestaetigung fuer einen anderen Schluessel hilft nicht; nur genau der gezeigte gilt
        using (var ws = await OpenSshAsync(app, await TicketFor(new { connection = connection.Id, password = targetPassword, acceptHostKey = "ssh-ed25519 SHA256:irgendein" }), cookie))
            Assert.Equal(800, (await ReadMessageAsync(ws)).Body.GetProperty("code").GetInt32());
        using (var ws = await OpenSshAsync(app, await TicketFor(new { connection = connection.Id, password = targetPassword, acceptHostKey = stored }), cookie))
        {
            var (kind, ready) = await ReadMessageAsync(ws);
            Assert.Equal(2, kind);
            Assert.Equal("changed-accepted", ready.GetProperty("hostKey").GetString());
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        Assert.Equal(stored, (await store.GetAsync(userId, connection.Id))!.HostKey);

        // 5. wrong password: Fehlertext, der Schluessel bleibt
        using (var ws = await OpenSshAsync(app, await TicketFor(new { connection = connection.Id, password = "falsch-falsch-1" }), cookie))
            Assert.Equal(769, (await ReadMessageAsync(ws)).Body.GetProperty("code").GetInt32());

        // 6. Ziel geaendert: der Schluessel wird vergessen (gehoert zum alten Ziel); Benutzer geaendert: bleibt
        var rename = await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{connection.Id}", new { name = "Terminal", kind = "terminal", host = sshHost, port = sshPort, username = "anderer" }, cookie));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal(stored, (await store.GetAsync(userId, connection.Id))!.HostKey);
        var move = await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{connection.Id}", new { name = "Terminal", kind = "terminal", host = "anderes-ziel", port = sshPort, username = "tester" }, cookie));
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.Null((await store.GetAsync(userId, connection.Id))!.HostKey);
    }

    [Fact]
    public async Task The_remembered_rdp_certificate_is_never_sent_to_the_browser_and_is_forgotten_when_the_target_changes()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var store = new ConnectionStore(db.Data);
        var connection = await store.CreateAsync(userId, "Desktop", "desktop", "ziel", 3389, "jul");
        const string fingerprint = "SHA256:AB:CD:EF:01";
        await store.SetCertFingerprintAsync(userId, connection.Id, fingerprint);
        Assert.Equal(fingerprint, (await store.GetAsync(userId, connection.Id))!.CertFingerprint);

        // Weder die Liste noch die Antwort einer Aenderung enthalten den Fingerabdruck oder ein Feld dafuer
        var list = await c.SendAsync(Send(HttpMethod.Get, "/api/connections", null, cookie));
        var text = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ABCDEF", text.Replace(":", ""));
        Assert.DoesNotContain("cert", text, StringComparison.OrdinalIgnoreCase);

        // Anderer Benutzername: das Zertifikat gehoert zum Ziel, es bleibt; anderes Ziel: weg
        var rename = await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{connection.Id}", new { name = "Desktop", kind = "desktop", host = "ziel", port = 3389, username = "anderer" }, cookie));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.DoesNotContain("cert", (await rename.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fingerprint, (await store.GetAsync(userId, connection.Id))!.CertFingerprint);
        var move = await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{connection.Id}", new { name = "Desktop", kind = "desktop", host = "anderes-ziel", port = 3389, username = "anderer" }, cookie));
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.Null((await store.GetAsync(userId, connection.Id))!.CertFingerprint);

        // und nach einem Port-Wechsel ebenfalls
        await store.SetCertFingerprintAsync(userId, connection.Id, fingerprint);
        var port = await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{connection.Id}", new { name = "Desktop", kind = "desktop", host = "anderes-ziel", port = 3390, username = "anderer" }, cookie));
        Assert.Equal(HttpStatusCode.OK, port.StatusCode);
        Assert.Null((await store.GetAsync(userId, connection.Id))!.CertFingerprint);
    }

    [Fact]
    public async Task The_terminal_socket_needs_a_session_a_ticket_and_the_own_origin()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var connection = await new ConnectionStore(db.Data).CreateAsync(userId, "T", "terminal", "ziel", 22, "u");
        var r = await c.SendAsync(Post("/api/remote/ticket", new { connection = connection.Id, password = "x" }, cookie));
        var ticket = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("ticket").GetString()!;

        Assert.Contains("401", (await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var ws = app.Server.CreateWebSocketClient();
            ws.ConfigureRequest = h => h.Headers["Origin"] = "http://localhost";
            await ws.ConnectAsync(new Uri($"ws://localhost/api/remote/ssh?ticket={ticket}"), CancellationToken.None);
        })).ToString());
        Assert.Contains("403", (await Assert.ThrowsAnyAsync<Exception>(() => OpenSshAsync(app, ticket, cookie, "http://boese.example"))).ToString());
        Assert.Contains("403", (await Assert.ThrowsAnyAsync<Exception>(() => OpenSshAsync(app, "falsch", cookie))).ToString());
        // das Ticket gilt fuer einen anderen Weg (Bildschirm-Strom) nicht
        Assert.Contains("403", (await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(app, ticket, cookie))).ToString());
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
        await using var app = new Server(db);
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
            await using var app = new Server(db, configure: s => s.AddSingleton(new UploadOptions(dir, TimeSpan.FromMilliseconds(500))));
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
        await using var app = new Server(db, configure: s => s.AddSingleton(new AgentOptions(agent.SocketPath)));
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
        await using var app = new Server(db, configure: s => s.AddSingleton(new AgentOptions("/tmp/julos-kein-agent.sock")));
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
            await using var app = new Server(db, configure: s => s.AddSingleton(new UploadOptions(dir, TimeSpan.FromMilliseconds(400))));
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
        await using var app = new Server(db, configure: s => s.AddLogging(l => l.AddProvider(logs)));
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
        Assert.SkipUnless(JulOS.Integrations.Display.ScreenStream.IsAvailable, "libopenh264 fehlt: ohne Kodierer gibt es keine Desktop-Verbindung");
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, secretKey: TestKey);
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

        // Ticket ohne getipptes Passwort: der Server nimmt das gespeicherte und reicht es an den RDP-Client weiter
        var noPassword = await c.SendAsync(Post("/api/remote/ticket", new { connection = id, width = 856, height = 1448 }, cookie));
        Assert.Equal(HttpStatusCode.OK, noPassword.StatusCode);
        var ticket = JsonDocument.Parse(await noPassword.Content.ReadAsStringAsync()).RootElement.GetProperty("ticket").GetString()!;
        Assert.DoesNotContain(stored, await noPassword.Content.ReadAsStringAsync());

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
        await using var app = new Server(db);
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
        await using var app = new Server(db, allowed: "erlaubt.example, 10.0.0.5, ziel");
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
        var annas = await new ConnectionStore(db.Data).CreateAsync(await db.ScalarAsync<Guid>("select id from users where name = 'anna'"), "Annas", "terminal", "ziel", 22, "a");
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{annas.Id}", new { name = "x", kind = "terminal", host = "ziel" }, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{annas.Id}", null, cookie))).StatusCode);
        Assert.Equal(2, (await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("connections").GetArrayLength());
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from connections where id = $1", annas.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{id}", null, cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Send(HttpMethod.Delete, $"/api/connections/{id}", null, cookie))).StatusCode);
        Assert.Equal(1, (await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("connections").GetArrayLength());
    }

    [Fact]
    public async Task The_stream_is_the_only_engine_old_engine_values_are_ignored_and_a_ticket_always_comes_from_the_stream()
    {
        Assert.SkipUnless(JulOS.Integrations.Display.ScreenStream.IsAvailable, "libopenh264 fehlt: ohne Kodierer gibt es keine Desktop-Verbindung");
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, allowed: "ziel", secretKey: TestKey);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        // Die API kennt keine Engine mehr: weder Antworten noch Listen tragen ein Feld dafuer, ein altes Feld in der Anfrage wird ueberhoert
        var plain = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "A", kind = "desktop", host = "ziel", username = "jul", password = "pw", engine = "guac" }, cookie)));
        Assert.False(plain.TryGetProperty("engine", out _));
        var id = plain.GetProperty("id").GetGuid();
        var list = await (await c.SendAsync(GetWith("/api/connections", cookie))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("engine", list);

        // Zeilen aus der Zeit mit zwei Engines ('guac' war der Standard der Spalte) werden wie alle anderen behandelt
        await db.ExecAsync("update connections set engine = 'guac' where id = $1", id);
        var ticket = await Json(await c.SendAsync(Post("/api/remote/ticket", new { connection = id, width = 856, height = 1448, engine = "guac" }, cookie)));
        Assert.Equal("stream", ticket.GetProperty("engine").GetString());
        var renamed = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "A2", kind = "desktop", host = "ziel", username = "jul" }, cookie)));
        Assert.Equal("A2", renamed.GetProperty("name").GetString());
        Assert.True(renamed.GetProperty("hasPassword").GetBoolean());

        // Ein Terminal nutzt weiter den eigenen SSH-Client
        var shell = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "C", kind = "terminal", host = "ziel" }, cookie)));
        var shellTicket = await Json(await c.SendAsync(Post("/api/remote/ticket", new { connection = shell.GetProperty("id").GetGuid(), password = "x" }, cookie)));
        Assert.Equal("ssh", shellTicket.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Bad_connection_input_and_forbidden_targets_are_refused_with_a_reason()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, allowed: "ziel");
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

    // ---- Laufwerksumleitung: Schalter je Verbindung, beide standardmaessig aus; die Wurzel nur aus der Server-Einstellung ----

    [Fact]
    public async Task Drive_switches_are_off_by_default_and_the_server_without_a_root_refuses_to_turn_them_on()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        var created = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "PC", kind = "desktop", host = "ziel", username = "jul" }, cookie)));
        Assert.False(created.GetProperty("driveShare").GetBoolean());
        Assert.False(created.GetProperty("driveWrite").GetBoolean());
        var list = await Json(await c.SendAsync(GetWith("/api/connections", cookie)));
        Assert.False(list.GetProperty("driveAvailable").GetBoolean());

        var refused = await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "PC2", kind = "desktop", host = "ziel", username = "jul", driveShare = true }, cookie));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("JULOS_DRIVE_ROOT", await refused.Content.ReadAsStringAsync());
        var id = created.GetProperty("id").GetGuid();
        var update = await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "PC", kind = "desktop", host = "ziel", username = "jul", driveShare = true }, cookie));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Drive_switches_only_apply_to_desktops_and_writing_needs_the_share()
    {
        var root = Path.Combine(Path.GetTempPath(), "julos-driveroot-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, driveRoot: root);
            var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var (cookie, _) = await SignedInAsync(app, c, db);
            Assert.True((await Json(await c.SendAsync(GetWith("/api/connections", cookie)))).GetProperty("driveAvailable").GetBoolean());

            async Task<JsonElement> Create(object body) => await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", body, cookie)));

            var rw = await Create(new { name = "RW", kind = "desktop", host = "ziel", driveShare = true, driveWrite = true });
            Assert.True(rw.GetProperty("driveShare").GetBoolean());
            Assert.True(rw.GetProperty("driveWrite").GetBoolean());
            var ro = await Create(new { name = "RO", kind = "desktop", host = "ziel", driveShare = true });
            Assert.True(ro.GetProperty("driveShare").GetBoolean());
            Assert.False(ro.GetProperty("driveWrite").GetBoolean());
            var writeOnly = await Create(new { name = "W", kind = "desktop", host = "ziel", driveWrite = true });
            Assert.False(writeOnly.GetProperty("driveShare").GetBoolean());
            Assert.False(writeOnly.GetProperty("driveWrite").GetBoolean());                       // writing without the drive does not exist
            var terminal = await Create(new { name = "T", kind = "terminal", host = "ziel", driveShare = true });
            Assert.False(terminal.GetProperty("driveShare").GetBoolean());

            // switching the read-only drive off keeps nothing; changing only the name keeps the switches as they were
            var id = rw.GetProperty("id").GetGuid();
            var renamed = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "RW2", kind = "desktop", host = "ziel" }, cookie)));
            Assert.True(renamed.GetProperty("driveShare").GetBoolean());
            Assert.True(renamed.GetProperty("driveWrite").GetBoolean());
            var off = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "RW2", kind = "desktop", host = "ziel", driveShare = false }, cookie)));
            Assert.False(off.GetProperty("driveShare").GetBoolean());
            Assert.False(off.GetProperty("driveWrite").GetBoolean());

            // the root never reaches the browser, in no response
            var text = await (await c.SendAsync(GetWith("/api/connections", cookie))).Content.ReadAsStringAsync();
            Assert.DoesNotContain(root, text);
            Assert.DoesNotContain("julos-driveroot", text);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task A_stream_ticket_carries_the_drive_only_when_the_connection_asks_for_it()
    {
        Assert.SkipUnless(JulOS.Integrations.Display.ScreenStream.IsAvailable, "libopenh264 fehlt: ohne Kodierer gibt es keine Desktop-Verbindung");
        var root = Path.Combine(Path.GetTempPath(), "julos-driveroot-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            await using var db = await TestDb.CreateAsync();
            await using var app = new Server(db, driveRoot: root);
            var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var (cookie, userId) = await SignedInAsync(app, c, db);
            var store = new ConnectionStore(db.Data);

            async Task<JsonElement> Ticket(Guid connection)
            {
                var response = await c.SendAsync(Post("/api/remote/ticket", new { connection, password = "x", width = 800, height = 600 }, cookie));
                Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                return await Json(response);
            }

            var plain = await store.CreateAsync(userId, "plain", "desktop", "ziel", 3389, "u");
            Assert.False((await Ticket(plain.Id)).TryGetProperty("drive", out _));                     // no drive: the field is left out (null is not written)
            var ro = await store.CreateAsync(userId, "ro", "desktop", "ziel", 3389, "u", driveShare: true);
            Assert.Equal("ro", (await Ticket(ro.Id)).GetProperty("drive").GetString());
            var rw = await store.CreateAsync(userId, "rw", "desktop", "ziel", 3389, "u", driveShare: true, driveWrite: true);
            Assert.Equal("rw", (await Ticket(rw.Id)).GetProperty("drive").GetString());

            // a request cannot ask for more than the connection has: the body has no drive field at all
            var sneaky = await c.SendAsync(Post("/api/remote/ticket", new { connection = plain.Id, password = "x", drive = "rw", driveShare = true, driveWrite = true, driveRoot = "/" }, cookie));
            Assert.False((await Json(sneaky)).TryGetProperty("drive", out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Domain_notes_and_keyboard_layout_are_saved_checked_and_kept_when_not_sent()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(app, c, db);

        var plain = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "Alt", kind = "desktop", host = "ziel", port = 3389, username = "u" }, cookie)));
        Assert.Equal("", plain.GetProperty("domain").GetString() ?? "");           // old clients send none of the new fields: empty, empty, German
        Assert.Equal("de", plain.GetProperty("layout").GetString());

        var made = await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "Firma", kind = "desktop", host = "ziel", port = 3389, username = "u", domain = "CORP", notes = "Zeile 1\r\nZeile 2", layout = "fr" }, cookie));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var created = await Json(made);
        Assert.Equal("CORP", created.GetProperty("domain").GetString());
        Assert.Equal("Zeile 1\nZeile 2", created.GetProperty("notes").GetString());
        Assert.Equal("fr", created.GetProperty("layout").GetString());
        var id = created.GetProperty("id").GetGuid();

        // a change that does not mention them keeps them (a client from before the fields must not wipe them)
        var rename = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "Firma 2", kind = "desktop", host = "ziel", port = 3389, username = "u" }, cookie)));
        Assert.Equal(("CORP", "Zeile 1\nZeile 2", "fr"), (rename.GetProperty("domain").GetString(), rename.GetProperty("notes").GetString(), rename.GetProperty("layout").GetString()));
        var cleared = await Json(await c.SendAsync(Send(HttpMethod.Put, $"/api/connections/{id}", new { name = "Firma 2", kind = "desktop", host = "ziel", port = 3389, username = "u", domain = "", notes = "", layout = "de" }, cookie)));
        Assert.Equal("", cleared.GetProperty("domain").GetString() ?? "");
        Assert.Equal("de", cleared.GetProperty("layout").GetString());

        foreach (var bad in new object[]
        {
            new { name = "x", kind = "desktop", host = "ziel", username = "u", layout = "klingon" },
            new { name = "x", kind = "desktop", host = "ziel", username = "u", domain = "CORP\\user" },
            new { name = "x", kind = "desktop", host = "ziel", username = "u", domain = new string('d', 65) },
            new { name = "x", kind = "desktop", host = "ziel", username = "u", notes = new string('n', 501) },
            new { name = "x", kind = "desktop", host = "ziel", username = "u", notes = "a\u0007b" },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Send(HttpMethod.Post, "/api/connections", bad, cookie))).StatusCode);

        // domain and layout belong to the desktop: a terminal drops them, notes stay
        var term = await Json(await c.SendAsync(Send(HttpMethod.Post, "/api/connections", new { name = "T", kind = "terminal", host = "ziel", username = "u", domain = "CORP", layout = "fr", notes = "ssh" }, cookie)));
        Assert.Equal(("", "de", "ssh"), (term.GetProperty("domain").GetString() ?? "", term.GetProperty("layout").GetString(), term.GetProperty("notes").GetString()));
    }

    [Fact]
    public async Task A_desktop_ticket_carries_the_domain_and_the_layout_of_the_connection()
    {
        Assert.SkipUnless(JulOS.Integrations.Display.ScreenStream.IsAvailable, "libopenh264 fehlt: ohne Kodierer gibt es keine Desktop-Verbindung");
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, userId) = await SignedInAsync(app, c, db);
        var store = new ConnectionStore(db.Data);
        async Task<(string Layout, JulOS.Integrations.Display.ScreenTarget Target)> Ticket(Guid connection)
        {
            var response = await c.SendAsync(Post("/api/remote/ticket", new { connection, password = "x", width = 800, height = 600 }, cookie));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await Json(response);
            var target = app.Services.GetRequiredService<JulOS.Integrations.Display.ScreenTickets>().Take(json.GetProperty("ticket").GetString()!, userId)!;
            return (json.GetProperty("layout").GetString()!, target);
        }
        var german = await Ticket((await store.CreateAsync(userId, "de", "desktop", "ziel", 3389, "u")).Id);
        Assert.Equal(("de", 0x407u, ""), (german.Layout, german.Target.Options.KeyboardLayout, german.Target.Options.Domain));
        var french = await Ticket((await store.CreateAsync(userId, "fr", "desktop", "ziel", 3389, "u", domain: "CORP", layout: "fr")).Id);
        Assert.Equal(("azerty", 0x40Cu, "CORP"), (french.Layout, french.Target.Options.KeyboardLayout, french.Target.Options.Domain));
    }

}
