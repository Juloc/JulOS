using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Files;
using JulOS.Integrations.Display;
using JulOS.Integrations.Ssh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Der Login je Host (docs/HOSTS.md): die Auswahl, die Uebernahme der bisherigen Zugangsdaten, die Schnittstelle, und dass Terminal, Dateien und Desktop ihn benutzen.</summary>
public sealed class HostLoginTests
{
    private static readonly string TestKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 11)).ToArray());
    private const string HostPw = "Host-Login-Passwort-1", OwnPw = "Eigenes-Passwort-2", TypedPw = "Getipptes-Passwort-3";

    // ---------- die Auswahl (reine Logik) ----------

    private static HostLoginInfo Login(string? user, bool password, params string[] targets) => new(Guid.NewGuid(), user, password, targets);

    [Fact]
    public void Without_a_host_login_the_connection_keeps_its_own_user_and_password()
    {
        var c = LoginResolver.Choose("jul", "srv-a", false, true, null);
        Assert.Equal(new LoginChoice(LoginSource.Own, "jul", true), c);
        Assert.Equal(new LoginChoice(LoginSource.Own, "jul", false), LoginResolver.Choose("jul", "srv-a", false, false, Login(null, false)));
    }

    [Fact]
    public void The_host_login_wins_unless_the_connection_asks_for_its_own()
    {
        var host = Login("hostuser", true, "srv-a");
        Assert.Equal(new LoginChoice(LoginSource.Host, "hostuser", true), LoginResolver.Choose("other", "SRV-A ", false, true, host));          // Adresse ohne Gross/Klein und Leerraum
        Assert.Equal(new LoginChoice(LoginSource.Own, "other", true), LoginResolver.Choose("other", "srv-a", true, true, host));                 // ausdrueckliche Ueberschreibung
        Assert.Equal(new LoginChoice(LoginSource.Own, "other", false), LoginResolver.Choose("other", "srv-a", true, false, host));               // eigener Login ohne Passwort: fragen
    }

    [Fact]
    public void A_host_login_with_only_a_user_gives_the_user_and_asks_for_the_password()
    {
        var c = LoginResolver.Choose("x", "srv-b", false, true, Login("hostuser", false));
        Assert.Equal(new LoginChoice(LoginSource.Host, "hostuser", false), c);
        // Hat die Verbindung fuer genau diesen Benutzer schon ein eigenes Passwort, geht weiter, was bisher ging
        Assert.Equal(new LoginChoice(LoginSource.Host, "hostuser", true, PasswordFromOwn: true), LoginResolver.Choose("hostuser", "srv-b", false, true, Login("hostuser", false)));
    }

    [Fact]
    public void The_host_password_is_only_for_the_addresses_it_was_set_for()
    {
        var host = Login("hostuser", true, "srv-a");
        var c = LoginResolver.Choose("jul", "fremd.example", false, false, host);
        Assert.Equal(new LoginChoice(LoginSource.Own, "jul", false, AddressNotTrusted: true), c);          // kein Passwort an eine fremde Adresse; die eigenen Daten gelten wie bisher
    }

    // ---------- Aufbau ----------

    private static async Task<(Guid User, Guid Host)> UserWithHost(TestDb db, string name = "anna")
    {
        var user = await Insert(db, "insert into users (name, password_hash, totp_secret) values ($1, 'x', 'y') returning id", name);
        var host = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Heimserver') returning id", user);
        return (user, host);
    }

    private static async Task<Guid> AddConn(TestDb db, SecretBox box, Guid user, Guid host, string name, string kind, string address, string username, string? password)
    {
        var id = await Insert(db, "insert into connections (user_id, host_id, name, kind, host, port, username) values ($1, $2, $3, $4, $5, 22, $6) returning id", user, host, name, kind, address, username);
        if (password is not null) await db.ExecAsync("update connections set secret = $2 where id = $1", id, box.Encrypt(password, id));
        return id;
    }

    private static async Task<(string User, string? Password, LoginSource Source)> Resolve(TestDb db, SecretBox box, Guid user, Guid connection)
    {
        var store = new ConnectionStore(db.Data);
        var c = (await store.GetAsync(user, connection, TestContext.Current.CancellationToken))!;
        var r = await store.ResolveLoginAsync(user, c, box, TestContext.Current.CancellationToken);
        return (r.Username, r.Password, r.Source);
    }

    // ---------- Uebernahme ----------

    [Fact]
    public async Task The_migration_makes_the_common_user_the_host_login_and_changes_nothing_a_connection_can_do()
    {
        await using var db = await TestDb.CreateAsync();
        var box = new SecretBox(TestKey);
        var (user, host) = await UserWithHost(db);
        var term = await AddConn(db, box, user, host, "Terminal", "terminal", "srv-a", "jul", HostPw);
        var rdp = await AddConn(db, box, user, host, "Desktop", "desktop", "srv-a", "jul", HostPw);
        var differentPw = await AddConn(db, box, user, host, "Zweites Terminal", "terminal", "srv-a", "jul", OwnPw);
        var asks = await AddConn(db, box, user, host, "Ohne Passwort", "terminal", "srv-a", "jul", null);
        var otherUser = await AddConn(db, box, user, host, "Als root", "terminal", "srv-a", "root", null);
        var vnc = await AddConn(db, box, user, host, "VNC", "vnc", "srv-a", "", "vnc-pw");
        var before = new Dictionary<Guid, (string, string?, LoginSource)>();
        foreach (var id in new[] { term, rdp, differentPw, asks, otherUser, vnc }) before[id] = await Resolve(db, box, user, id);
        // Vorher gilt, was an der Verbindung steht
        Assert.Equal(("jul", HostPw), (before[term].Item1, before[term].Item2));
        Assert.Equal(("jul", null), (before[asks].Item1, before[asks].Item2));

        var made = await new HostLoginMigration(db.Data, box).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, made);

        // Der Host-Login: der gemeinsame Benutzer, das Passwort der ersten Verbindung, nur verschluesselt in der Datenbank
        var logins = new HostLoginStore(db.Data);
        var info = (await logins.GetAsync(user, host, TestContext.Current.CancellationToken))!;
        Assert.Equal(("jul", true), (info.User, info.HasPassword));
        Assert.Equal(["srv-a"], info.Targets);
        var sealedHost = (await logins.GetSecretAsync(user, host, TestContext.Current.CancellationToken))!;
        Assert.Equal(HostPw, box.Decrypt(sealedHost, host));
        Assert.Null(box.Decrypt(sealedHost, term));                                                    // die Host-Kennung steckt in der Verschluesselung
        Assert.DoesNotContain(HostPw, System.Text.Encoding.UTF8.GetString(sealedHost));

        // Nachher kann jede Verbindung genau dasselbe wie vorher (Benutzer und Passwort); wer gleich ist, folgt dem Host-Login
        foreach (var (id, (u, p, _)) in before)
        {
            var now = await Resolve(db, box, user, id);
            // Einzige Ausnahme von "genau wie vorher": ein Terminal desselben Benutzers, das jedes Mal nach dem Passwort fragte, hat jetzt das des Hosts
            Assert.Equal((u, id == asks ? HostPw : p), (now.User, now.Password));
        }
        Assert.Equal(LoginSource.Host, (await Resolve(db, box, user, term)).Source);
        Assert.Equal(LoginSource.Host, (await Resolve(db, box, user, rdp)).Source);
        Assert.Equal(LoginSource.Host, (await Resolve(db, box, user, asks)).Source);                  // derselbe Benutzer ohne eigenes Passwort bekommt das des Hosts
        Assert.Equal(LoginSource.Own, (await Resolve(db, box, user, differentPw)).Source);           // anderes Passwort: bleibt eigen
        Assert.Equal(LoginSource.Own, (await Resolve(db, box, user, otherUser)).Source);             // anderer Benutzer: bleibt eigen
        Assert.Equal(LoginSource.Own, (await Resolve(db, box, user, vnc)).Source);                   // ohne Benutzername: bleibt eigen
        // Nichts wurde geloescht oder neu verschluesselt: die Passwoerter der Verbindungen liegen unveraendert da
        Assert.Equal(4, await db.ScalarAsync<long>("select count(*) from connections where secret is not null"));
    }

    [Fact]
    public async Task The_migration_is_idempotent_and_leaves_a_host_with_several_users_alone()
    {
        await using var db = await TestDb.CreateAsync();
        var box = new SecretBox(TestKey);
        var (user, host) = await UserWithHost(db);
        var a = await AddConn(db, box, user, host, "Terminal", "terminal", "srv-a", "jul", HostPw);
        var b = await AddConn(db, box, user, host, "Desktop", "desktop", "srv-a", "Administrator", OwnPw);
        var migration = new HostLoginMigration(db.Data, box);
        Assert.Equal(0, await migration.RunAsync(TestContext.Current.CancellationToken));              // zwei Benutzer: kein Host-Login, nichts wird gesetzt
        var info = (await new HostLoginStore(db.Data).GetAsync(user, host, TestContext.Current.CancellationToken))!;
        Assert.Null(info.User);
        Assert.Equal(LoginSource.Own, (await Resolve(db, box, user, a)).Source);
        Assert.Equal(("Administrator", OwnPw), ((await Resolve(db, box, user, b)).User, (await Resolve(db, box, user, b)).Password));
        Assert.True(await db.ScalarAsync<bool>("select login_checked from hosts where id = $1", host));
        // Die Host-Seite schlaegt einen Benutzer vor (der haeufigste, Terminal zuerst), setzt ihn aber nicht
        var conns = await new ConnectionStore(db.Data).ListAsync(user, TestContext.Current.CancellationToken);
        Assert.Equal("jul", JulOS.Frontend.Hosts.HostLoginEndpoints.Suggest(conns));

        // Ein zweiter Lauf aendert nichts, auch nicht, wenn danach ein Login gesetzt wurde
        await new HostLoginStore(db.Data).SetAsync(user, host, "jul", box.Encrypt(HostPw, host), false, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, await migration.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal("jul", (await new HostLoginStore(db.Data).GetAsync(user, host, TestContext.Current.CancellationToken))!.User);
    }

    [Fact]
    public async Task The_migration_waits_when_a_stored_password_cannot_be_read()
    {
        await using var db = await TestDb.CreateAsync();
        var oldBox = new SecretBox(TestKey);
        var (user, host) = await UserWithHost(db);
        await AddConn(db, oldBox, user, host, "Terminal", "terminal", "srv-a", "jul", HostPw);
        var wrongKey = new SecretBox(Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 99)).ToArray()));
        Assert.Equal(0, await new HostLoginMigration(db.Data, wrongKey).RunAsync(TestContext.Current.CancellationToken));
        Assert.False(await db.ScalarAsync<bool>("select login_checked from hosts where id = $1", host));      // beim naechsten Start mit dem richtigen Schluessel wieder
        Assert.Equal(1, await new HostLoginMigration(db.Data, oldBox).RunAsync(TestContext.Current.CancellationToken));
    }

    // ---------- Speicher ----------

    [Fact]
    public async Task Setting_a_host_login_keeps_other_users_and_a_changed_user_drops_the_password()
    {
        await using var db = await TestDb.CreateAsync();
        var box = new SecretBox(TestKey);
        var (user, host) = await UserWithHost(db);
        var other = await AddConn(db, box, user, host, "Als root", "terminal", "srv-a", "root", OwnPw);
        var same = await AddConn(db, box, user, host, "Terminal", "terminal", "srv-a", "jul", null);
        var store = new HostLoginStore(db.Data);
        var ct = TestContext.Current.CancellationToken;
        var first = await store.SetAsync(user, host, "jul", box.Encrypt(HostPw, host), false, ct: ct);
        Assert.Equal((true, true, 1), (first.Found, first.Created, first.Pinned));                      // "root" behaelt seinen eigenen Login
        Assert.Equal(LoginSource.Own, (await Resolve(db, box, user, other)).Source);
        Assert.Equal(("jul", HostPw), ((await Resolve(db, box, user, same)).User, (await Resolve(db, box, user, same)).Password));
        // Nur den Benutzer aendern: das Passwort gehoert zum alten Benutzer und entfaellt
        var changed = await store.SetAsync(user, host, "anna", null, false, ct: ct);
        Assert.False(changed.Created);
        var info = (await store.GetAsync(user, host, ct))!;
        Assert.Equal(("anna", false, 0), (info.User, info.HasPassword, info.Targets.Count));
        // Gleicher Benutzer ohne neues Passwort: es bleibt; "Passwort nicht speichern": es entfaellt
        await store.SetAsync(user, host, "anna", box.Encrypt(HostPw, host), false, ct: ct);
        await store.SetAsync(user, host, "anna", null, false, ct: ct);
        Assert.True((await store.GetAsync(user, host, ct))!.HasPassword);
        await store.SetAsync(user, host, "anna", null, true, ct: ct);
        Assert.False((await store.GetAsync(user, host, ct))!.HasPassword);
        // Entfernen lässt die Passwoerter der Verbindungen in Ruhe
        Assert.True(await store.ClearAsync(user, host, ct));
        Assert.Null((await store.GetAsync(user, host, ct))!.User);
        Assert.Equal(("root", OwnPw), ((await Resolve(db, box, user, other)).User, (await Resolve(db, box, user, other)).Password));
        // Fremde Hosts gibt es nicht
        var (stranger, _) = await UserWithHost(db, "ben");
        Assert.Null(await store.GetAsync(stranger, host, ct));
        Assert.False((await store.SetAsync(stranger, host, "x", null, false, ct: ct)).Found);
        Assert.False(await store.ClearAsync(stranger, host, ct));
    }

    // ---------- der ganze Server ----------

    private sealed class Rig(TestDb db) : WebApplicationFactory<Program>
    {
        public readonly Queue<FakeFileSession> Hosts = new();
        public readonly List<FakeFileSession> Created = [];
        public readonly List<string> Log = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            builder.UseSetting("JULOS_SECRET_KEY", TestKey);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton(new FileSessionFactory(() => { var f = Hosts.Count > 0 ? Hosts.Dequeue() : new FakeFileSession(); Created.Add(f); return f; }));
                s.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace).AddProvider(new Capture(Log)));
            });
        }
        public SftpPool Pool => Services.GetRequiredService<SftpPool>();
    }

    private sealed class Api(HttpClient http, string cookie)
    {
        public async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod m, string url, object? body = null)
        {
            var r = await http.SendAsync(Req(m, url, cookie, body));
            var text = await r.Content.ReadAsStringAsync();
            return (r.StatusCode, text.Length == 0 ? default : J(text));
        }
        public Task<(HttpStatusCode Status, JsonElement Body)> Get(string url) => Send(HttpMethod.Get, url);
        public Task<(HttpStatusCode Status, JsonElement Body)> Put(string url, object body) => Send(HttpMethod.Put, url, body);
        public Task<(HttpStatusCode Status, JsonElement Body)> Post(string url, object body) => Send(HttpMethod.Post, url, body);
        public HttpClient Http => http;
        public string Cookie => cookie;
    }

    private static async Task<(Api Api, Guid User, Guid Host)> SignedIn(Rig app, TestDb db)
    {
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, user) = await SignedInAsync(http, db);
        var api = new Api(http, cookie);
        var (_, hosts) = await api.Get("/api/hosts");
        return (api, user, hosts.GetProperty("hosts")[0].GetProperty("id").GetGuid());
    }

    private static async Task<Guid> NewConn(Api api, Guid host, string name, string kind, string address = "srv-a", string username = "jul", string? password = null, bool? ownLogin = null)
    {
        var (status, body) = await api.Post("/api/connections", new { name, kind, host = address, username, password, hostId = host, ownLogin });
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_api_sets_reads_and_removes_the_login_and_never_returns_a_password()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, user, host) = await SignedIn(app, db);
        var term = await NewConn(api, host, "Terminal", "terminal");

        var (s0, empty) = await api.Get($"/api/hosts/{host}/login");
        Assert.Equal(HttpStatusCode.OK, s0);
        Assert.Equal((false, false, "jul"), (empty.TryGetProperty("user", out _), empty.GetProperty("hasPassword").GetBoolean(), empty.GetProperty("suggestion").GetString()));          // ohne Wert fehlt das Feld

        var (s1, set) = await api.Put($"/api/hosts/{host}/login", new { user = "jul", password = HostPw });
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal(("jul", true), (set.GetProperty("user").GetString(), set.GetProperty("hasPassword").GetBoolean()));
        var row = set.GetProperty("connections")[0];
        Assert.Equal((term, "host", "jul"), (row.GetProperty("id").GetGuid(), row.GetProperty("source").GetString(), row.GetProperty("username").GetString()));

        // Nirgends im Klartext: nicht in den Antworten, nicht in der Datenbank, nicht im Log
        var all = set.ToString() + (await api.Get($"/api/hosts/{host}/login")).Body + (await api.Get("/api/connections")).Body + (await api.Get("/api/hosts")).Body;
        Assert.DoesNotContain(HostPw, all);
        Assert.True(await db.ScalarAsync<bool>("select login_secret is not null and position(convert_to($2, 'UTF8') in login_secret) = 0 from hosts where id = $1", host, HostPw));
        Assert.DoesNotContain(app.Log, l => l.Contains(HostPw));
        var conn = (await api.Get("/api/connections")).Body.GetProperty("connections")[0];
        Assert.Equal(("host", "jul", true, false), (conn.GetProperty("loginSource").GetString(), conn.GetProperty("loginUser").GetString(), conn.GetProperty("loginPassword").GetBoolean(), conn.GetProperty("hasPassword").GetBoolean()));

        // Eintrag im Verlauf, ohne Wert
        var audit = await db.ScalarAsync<string>("select string_agg(action || ':' || event || ':' || coalesce(detail, '') || ':' || coalesce(target, ''), ',') from host_audit where host_id = $1", host);
        Assert.Equal("login.set:ok:user+password:", audit);

        // Nur den Benutzer aendern: Passwort entfaellt; "Passwort nicht speichern"
        var (_, userOnly) = await api.Put($"/api/hosts/{host}/login", new { user = "anna" });
        Assert.False(userOnly.GetProperty("hasPassword").GetBoolean());
        var (_, forgot) = await api.Put($"/api/hosts/{host}/login", new { user = "anna", password = HostPw });
        Assert.True(forgot.GetProperty("hasPassword").GetBoolean());
        var (_, kept) = await api.Put($"/api/hosts/{host}/login", new { user = "anna" });
        Assert.True(kept.GetProperty("hasPassword").GetBoolean());                                      // ohne Passwort in der Anfrage bleibt es
        var (_, dropped) = await api.Put($"/api/hosts/{host}/login", new { user = "anna", forgetPassword = true });
        Assert.False(dropped.GetProperty("hasPassword").GetBoolean());

        // Entfernen
        var (sd, removed) = await api.Send(HttpMethod.Delete, $"/api/hosts/{host}/login");
        Assert.Equal(HttpStatusCode.OK, sd);
        Assert.False(removed.TryGetProperty("user", out _));
        Assert.Contains("login.remove", await db.ScalarAsync<string>("select string_agg(action, ',') from host_audit where host_id = $1", host));

        // Eingaben
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/hosts/{host}/login", new { user = "" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/hosts/{host}/login", new { user = new string('x', 65) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/hosts/{host}/login", new { user = "a\nb" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/hosts/{host}/login", new { user = "jul", password = new string('p', 257) })).Status);
    }

    [Fact]
    public async Task The_login_needs_a_session_the_header_the_origin_and_an_own_host()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, _, host) = await SignedIn(app, db);
        var http = api.Http;
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/api/hosts/{host}/login")).StatusCode);
        foreach (var m in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            var noHeader = Req(m, $"/api/hosts/{host}/login", api.Cookie, new { user = "jul" }); noHeader.Headers.Remove("X-JulOS");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(noHeader)).StatusCode);
            var evil = Req(m, $"/api/hosts/{host}/login", api.Cookie, new { user = "jul" }); evil.Headers.Remove("Origin"); evil.Headers.Add("Origin", "https://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(evil)).StatusCode);
        }
        // Der Host eines anderen Benutzers: 404 fuer alles, nichts aendert sich
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'x') returning id");
        var foreign = await Insert(db, "insert into hosts (user_id, name, login_user) values ($1, 'Fremd', 'secret-user') returning id", anna);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/hosts/{foreign}/login")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Put($"/api/hosts/{foreign}/login", new { user = "jul", password = HostPw })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Send(HttpMethod.Delete, $"/api/hosts/{foreign}/login")).Status);
        Assert.Equal("secret-user", await db.ScalarAsync<string>("select login_user from hosts where id = $1", foreign));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from host_audit where host_id = $1", foreign));
    }

    [Fact]
    public async Task The_terminal_and_the_desktop_ticket_use_the_host_login_and_an_own_login_wins()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, user, host) = await SignedIn(app, db);
        var term = await NewConn(api, host, "Terminal", "terminal", password: "veraltet-1234");
        var own = await NewConn(api, host, "Eigen", "terminal", username: "special", password: OwnPw, ownLogin: true);
        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/hosts/{host}/login", new { user = "jul", password = HostPw })).Status);

        async Task<SshTarget> TerminalTicket(Guid c, string? typed = null)
        {
            var (status, body) = await api.Post("/api/remote/ticket", new { connection = c, password = typed, cols = 80, rows = 24 });
            Assert.Equal(HttpStatusCode.OK, status);
            return app.Services.GetRequiredService<SshTickets>().Take(body.GetProperty("ticket").GetString()!, user)!;
        }
        var t1 = await TerminalTicket(term);
        Assert.Equal(("jul", HostPw), (t1.Options.User, t1.Options.Password));                       // der Host-Login gilt vor dem alten Passwort der Verbindung
        var t2 = await TerminalTicket(own);
        Assert.Equal(("special", OwnPw), (t2.Options.User, t2.Options.Password));                    // Ueberschreibung
        var t3 = await TerminalTicket(term, TypedPw);
        Assert.Equal(("jul", TypedPw), (t3.Options.User, t3.Options.Password));                      // ein getipptes Passwort gilt immer

        if (ScreenStream.IsAvailable)
        {
            var rdp = await NewConn(api, host, "Desktop", "desktop");
            var (status, body) = await api.Post("/api/remote/ticket", new { connection = rdp, width = 856, height = 1448 });
            Assert.Equal(HttpStatusCode.OK, status);
            var target = app.Services.GetRequiredService<ScreenTickets>().Take(body.GetProperty("ticket").GetString()!, user)!;
            Assert.Equal(("jul", HostPw), (target.Options.UserName, target.Options.Password));
        }
        // Ohne Host-Login gilt wieder, was an der Verbindung steht
        await api.Send(HttpMethod.Delete, $"/api/hosts/{host}/login");
        var t4 = await TerminalTicket(term);
        Assert.Equal(("jul", "veraltet-1234"), (t4.Options.User, t4.Options.Password));
    }

    [Fact]
    public async Task A_host_password_never_goes_to_a_connection_with_another_address()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, user, host) = await SignedIn(app, db);
        var known = await NewConn(api, host, "Bekannt", "terminal", address: "srv-a");
        await api.Put($"/api/hosts/{host}/login", new { user = "jul", password = HostPw });
        // Danach legt jemand mit der Sitzung eine Verbindung zu einem fremden Ziel an und verbindet sich
        var evil = await NewConn(api, host, "Fremd", "terminal", address: "angreifer.example");
        var (status, _) = await api.Post("/api/remote/ticket", new { connection = evil, cols = 80, rows = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, status);                                                // Passwort fehlt: der Host-Login gilt dort nicht
        var conn = (await api.Get("/api/connections")).Body.GetProperty("connections").EnumerateArray().First(c => c.GetProperty("id").GetGuid() == evil);
        Assert.Equal(("own", false, true), (conn.GetProperty("loginSource").GetString(), conn.GetProperty("loginPassword").GetBoolean(), conn.GetProperty("loginAddressNew").GetBoolean()));
        // Auch eine bekannte Verbindung verliert das Passwort, wenn ihre Adresse geaendert wird
        var (_, edited) = await api.Send(HttpMethod.Put, $"/api/connections/{known}", new { name = "Bekannt", kind = "terminal", host = "angreifer.example", username = "jul" });
        Assert.False(edited.GetProperty("loginPassword").GetBoolean());
        // Setzt der Benutzer das Passwort neu, gelten die Adressen der Verbindungen von jetzt
        var (_, again) = await api.Put($"/api/hosts/{host}/login", new { user = "jul", password = HostPw });
        Assert.All(again.GetProperty("connections").EnumerateArray(), c => Assert.Equal("host", c.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task Files_open_without_a_question_with_a_host_password_and_ask_only_for_the_password_with_a_user_alone()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, user, host) = await SignedIn(app, db);
        var files = await NewConn(api, host, "Terminal", "terminal", username: "");                 // ohne eigenen Benutzer: der des Hosts gilt
        await api.Put($"/api/hosts/{host}/login", new { user = "hostuser" });                          // nur ein Benutzer

        // Der Dialog bekommt den Benutzer und darf "fuer diesen Host speichern" anbieten
        var (needStatus, need) = await api.Post("/api/files/remote/open", new { connection = files });
        Assert.Equal((HttpStatusCode.Conflict, "password_required", "hostuser", true), (needStatus, need.GetProperty("code").GetString(), need.GetProperty("user").GetString(), need.GetProperty("canSave").GetBoolean()));
        Assert.Empty(app.Created);

        // Getippt, ohne Haken: nichts wird gespeichert
        var first = new FakeFileSession { LogonPassword = TypedPw }; first.Key = first.Key; app.Hosts.Enqueue(first);
        await db.ExecAsync("update connections set host_key = $1 where id = $2", first.Key.Id, files);
        var (s1, o1) = await api.Post("/api/files/remote/open", new { connection = files, password = TypedPw });
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.False(o1.TryGetProperty("hostLoginSaved", out _));
        Assert.Equal(("hostuser", TypedPw), (first.ConnectedUser, first.ConnectedPassword));         // mit dem Benutzer des Hosts
        Assert.False((await new HostLoginStore(db.Data).GetAsync(user, host, TestContext.Current.CancellationToken))!.HasPassword);
        await app.Pool.CloseAsync(user, files);

        // Mit dem Haken: gespeichert, aber erst nach gelungener Anmeldung (ein falsches Passwort wird nie gespeichert)
        var wrong = new FakeFileSession { LogonPassword = "anders" }; app.Hosts.Enqueue(wrong);
        var (sw, _) = await api.Post("/api/files/remote/open", new { connection = files, password = TypedPw, saveForHost = true });
        Assert.NotEqual(HttpStatusCode.OK, sw);
        Assert.False((await new HostLoginStore(db.Data).GetAsync(user, host, TestContext.Current.CancellationToken))!.HasPassword);
        var good = new FakeFileSession { LogonPassword = TypedPw }; app.Hosts.Enqueue(good);
        var (s2, o2) = await api.Post("/api/files/remote/open", new { connection = files, password = TypedPw, saveForHost = true });
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.True(o2.GetProperty("hostLoginSaved").GetBoolean());
        Assert.True((await new HostLoginStore(db.Data).GetAsync(user, host, TestContext.Current.CancellationToken))!.HasPassword);
        Assert.DoesNotContain(app.Log, l => l.Contains(TypedPw));
        await app.Pool.CloseAsync(user, files);

        // Jetzt oeffnet die Dateien-App ohne Frage, mit dem Login des Hosts
        var third = new FakeFileSession { LogonPassword = TypedPw }; app.Hosts.Enqueue(third);
        var (s3, _) = await api.Post("/api/files/remote/open", new { connection = files });
        Assert.Equal(HttpStatusCode.OK, s3);
        Assert.Equal(("hostuser", TypedPw), (third.ConnectedUser, third.ConnectedPassword));
    }

    [Fact]
    public async Task Changing_the_host_login_closes_the_pooled_file_sessions_of_that_host_only()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, user, host) = await SignedIn(app, db);
        var (_, second) = await api.Post("/api/hosts", new { name = "Anderer" });
        var otherHost = second.GetProperty("id").GetGuid();
        var mine = await NewConn(api, host, "Terminal", "terminal");
        var theirs = await NewConn(api, otherHost, "Anderes Terminal", "terminal", password: OwnPw);
        await api.Put($"/api/hosts/{host}/login", new { user = "jul", password = HostPw });
        foreach (var c in new[] { mine, theirs })
        {
            var f = new FakeFileSession(); app.Hosts.Enqueue(f);
            await db.ExecAsync("update connections set host_key = $1 where id = $2", f.Key.Id, c);
            Assert.Equal(HttpStatusCode.OK, (await api.Post("/api/files/remote/open", new { connection = c })).Status);
        }
        Assert.True(app.Pool.IsOpen(user, mine));
        Assert.True(app.Pool.IsOpen(user, theirs));
        await api.Put($"/api/hosts/{host}/login", new { user = "jul", password = "Neues-Passwort-4" });
        Assert.False(app.Pool.IsOpen(user, mine));                                                      // dieser Host: die Sitzung ist zu
        Assert.True(app.Pool.IsOpen(user, theirs));                                                     // ein anderer Host bleibt
        // Der Host-Schluessel bleibt, wie er war (der Abgleich wird nicht zurueckgesetzt)
        Assert.NotNull(await db.ScalarAsync<string>("select host_key from connections where id = $1", mine));
        await api.Send(HttpMethod.Delete, $"/api/hosts/{host}/login");
        Assert.NotNull(await db.ScalarAsync<string>("select host_key from connections where id = $1", mine));
    }

    [Fact]
    public async Task Deleting_the_host_deletes_its_login()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, _, _) = await SignedIn(app, db);
        var (_, created) = await api.Post("/api/hosts", new { name = "Weg" });
        var host = created.GetProperty("id").GetGuid();
        await api.Put($"/api/hosts/{host}/login", new { user = "jul", password = HostPw });
        Assert.Equal(HttpStatusCode.NoContent, (await api.Send(HttpMethod.Delete, $"/api/hosts/{host}")).Status);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from hosts where id = $1", host));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from hosts where login_secret is not null and id = $1", host));
        // Der Verlauf bleibt (ohne Wert), er ueberlebt den Host
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from host_audit where host_id = $1 and action = 'login.set'", host));
    }
}
