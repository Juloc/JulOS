using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Integrations.Agent;
using JulOS.Integrations.Reachability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JulOS.Backend.Tests;

/// <summary>Hosts (docs/HOSTS.md): Regeln, Migration der alten Daten, Endpunkte, Zustand aus Agent und Port, Meldungen des Agenten (echte Middleware und Datenbank).</summary>
public sealed class HostTests
{
    private const string Password = "korrektes pferd batterie";
    private const string SetupCode = "TEST-CODE-0000";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---------------------------------------------------------------- Regeln

    [Theory]
    [InlineData("Heimserver", "Heimserver")]
    [InlineData("  Büro-PC  ", "Büro-PC")]
    [InlineData("Ä", "Ä")]
    public void A_host_name_is_trimmed(string input, string expected) => Assert.Equal((expected, null), HostRules.ValidateName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Zeile\neins")]
    [InlineData("Tab\tulator")]
    public void Empty_or_control_characters_are_refused(string? input) => Assert.NotNull(HostRules.ValidateName(input).Error);

    [Fact]
    public void A_host_name_has_at_most_60_characters()
    {
        Assert.Null(HostRules.ValidateName(new string('a', 60)).Error);
        Assert.NotNull(HostRules.ValidateName(new string('a', 61)).Error);
    }

    // ---------------------------------------------------------------- Migration

    private static async Task<Guid> Insert(TestDb db, string sql, params object?[] args) => await db.ScalarAsync<Guid>(sql, args);

    [Fact]
    public async Task Old_data_moves_to_a_default_host_without_losing_connections_or_agents()
    {
        await using var db = await CreateBeforeHostsAsync();
        var asm = typeof(Migrator).Assembly;
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        var ben = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('ben', 'x', 'y') returning id");
        var cleo = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('cleo', 'x', 'y') returning id");
        Task<Guid> Conn(Guid user, string name, string kind = "desktop") => Insert(db, "insert into connections (user_id, name, kind, host, port) values ($1, $2, $3, 'ziel', 22) returning id", user, name, kind);
        var annaPc = await Conn(anna, "Büro-PC");
        var annaVm = await Conn(anna, "VM");
        var annaShell = await Conn(anna, "Shell", "terminal");
        var benPc = await Conn(ben, "Ben-PC");
        // Anna hat zwei Agenten (je Verbindung einen): der zuletzt gesehene bleibt auf dem Standard-Host
        await db.ExecAsync("insert into agents (connection_id, user_id, token_hash, last_seen, version, platform) values ($1, $2, 'hash-pc', now() - interval '2 days', '0.3.0', 'linux-x11')", annaPc, anna);
        await db.ExecAsync("insert into agents (connection_id, user_id, token_hash, last_seen, version, platform) values ($1, $2, 'hash-vm', now() - interval '1 hour', '0.3.0', 'linux-x11')", annaVm, anna);
        await db.ExecAsync("insert into agents (connection_id, user_id, token_hash, last_seen) values ($1, $2, 'hash-ben', null)", benPc, ben);
        await db.ExecAsync("insert into agent_codes (code_hash, connection_id, user_id, expires_at) values ('c1', $1, $2, now() + interval '5 minutes')", annaPc, anna);

        var applied = await new Migrator(db.Data).RunAsync(TestContext.Current.CancellationToken);
        Assert.Contains("008_hosts", applied);

        // nichts geht verloren: alle Verbindungen haben einen Host, alle Agenten ihr Token
        Assert.Equal(4L, await db.ScalarAsync<long>("select count(*) from connections where host_id is not null"));
        Assert.Equal(3L, await db.ScalarAsync<long>("select count(*) from agents"));
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from hosts where user_id = $1", cleo));               // ohne Verbindung kein Host (er entsteht bei Bedarf)

        // Ben: ein Standard-Host mit seiner Verbindung und seinem Agenten
        var benHost = await db.ScalarAsync<Guid>("select host_id from connections where id = $1", benPc);
        Assert.Equal("Mein Rechner", await db.ScalarAsync<string>("select name from hosts where id = $1", benHost));
        Assert.Equal("hash-ben", await db.ScalarAsync<string>("select token_hash from agents where host_id = $1", benHost));

        // Anna: Standard-Host mit dem zuletzt gesehenen Agenten (VM) und den uebrigen Verbindungen, der aeltere Agent hat einen eigenen Host
        var annaHost = await db.ScalarAsync<Guid>("select host_id from connections where id = $1", annaVm);
        Assert.Equal(annaHost, await db.ScalarAsync<Guid>("select host_id from connections where id = $1", annaShell));
        Assert.Equal("Mein Rechner", await db.ScalarAsync<string>("select name from hosts where id = $1", annaHost));
        Assert.Equal("hash-vm", await db.ScalarAsync<string>("select token_hash from agents where host_id = $1", annaHost));
        var pcHost = await db.ScalarAsync<Guid>("select host_id from connections where id = $1", annaPc);
        Assert.NotEqual(annaHost, pcHost);
        Assert.Equal("Büro-PC", await db.ScalarAsync<string>("select name from hosts where id = $1", pcHost));
        Assert.Equal("hash-pc", await db.ScalarAsync<string>("select token_hash from agents where host_id = $1", pcHost));
        Assert.Equal(2L, await db.ScalarAsync<long>("select count(*) from hosts where user_id = $1", anna));
        Assert.Equal("0.3.0", await db.ScalarAsync<string>("select version from agents where host_id = $1", pcHost));

        // offene Codes gelten zehn Minuten und werden nicht uebernommen; ein zweiter Lauf aendert nichts
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from agent_codes"));
        Assert.Empty(await new Migrator(db.Data).RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3L, await db.ScalarAsync<long>("select count(*) from hosts"));

        // und ein Host mit Verbindungen laesst sich nicht nebenbei loeschen? Die Datenbank nimmt die Verbindungen nicht mit (kein Cascade)
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync("delete from hosts where id = $1", annaHost));
    }

    /// <summary>Eine Datenbank im Stand vor den Hosts: alle Migrationen bis 007 (die Migration der Hosts ist die 008).</summary>
    private static async Task<TestDb> CreateBeforeHostsAsync()
    {
        var db = await TestDb.CreateAsync(migrate: false);
        var asm = typeof(Migrator).Assembly;
        await db.ExecAsync("create table schema_migrations (id text primary key, applied_at timestamptz not null default now())");
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("database/", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var id = name["database/".Length..^4];
            if (string.CompareOrdinal(id, "008") >= 0) continue;
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            await db.ExecAsync(await reader.ReadToEndAsync());
            await db.ExecAsync("insert into schema_migrations (id) values ($1)", id);
        }
        return db;
    }

    [Fact]
    public async Task A_backup_from_before_the_hosts_restores_with_default_hosts_and_every_agent()
    {
        await using var old = await CreateBeforeHostsAsync();
        var anna = await Insert(old, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        var pc = await Insert(old, "insert into connections (user_id, name, kind, host, port) values ($1, 'Büro-PC', 'desktop', 'ziel', 3389) returning id", anna);
        var vm = await Insert(old, "insert into connections (user_id, name, kind, host, port) values ($1, 'VM', 'desktop', 'ziel', 3389) returning id", anna);
        await old.ExecAsync("insert into agents (connection_id, user_id, token_hash, last_seen) values ($1, $2, 'hash-pc', now() - interval '2 days')", pc, anna);
        await old.ExecAsync("insert into agents (connection_id, user_id, token_hash, last_seen) values ($1, $2, 'hash-vm', now() - interval '1 hour')", vm, anna);
        await old.ExecAsync("insert into agent_codes (code_hash, connection_id, user_id, expires_at) values ('c1', $1, $2, now() + interval '5 minutes')", pc, anna);
        var dump = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(old.Data, dump, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("008_hosts", dump.ToString().Split('\n').First(l => l.StartsWith("-- migrations ", StringComparison.Ordinal)));

        await using var fresh = await TestDb.CreateAsync();
        await DbDump.RestoreAsync(fresh.Data, new StringReader(dump.ToString()), false, TestContext.Current.CancellationToken);
        Assert.Equal(2L, await fresh.ScalarAsync<long>("select count(*) from connections"));
        Assert.Equal(2L, await fresh.ScalarAsync<long>("select count(*) from hosts"));
        Assert.Equal(2L, await fresh.ScalarAsync<long>("select count(*) from agents"));
        Assert.Equal(0L, await fresh.ScalarAsync<long>("select count(*) from agent_codes"));
        var vmHost = await fresh.ScalarAsync<Guid>("select host_id from connections where id = $1", vm);
        Assert.Equal("Mein Rechner", await fresh.ScalarAsync<string>("select name from hosts where id = $1", vmHost));
        Assert.Equal("hash-vm", await fresh.ScalarAsync<string>("select token_hash from agents where host_id = $1", vmHost));
        var pcHost = await fresh.ScalarAsync<Guid>("select host_id from connections where id = $1", pc);
        Assert.NotEqual(vmHost, pcHost);
        Assert.Equal("hash-pc", await fresh.ScalarAsync<string>("select token_hash from agents where host_id = $1", pcHost));
    }

    // ---------------------------------------------------------------- Store

    [Fact]
    public async Task The_default_host_is_created_once_and_a_first_connection_lands_on_it()
    {
        await using var db = await TestDb.CreateAsync();
        var user = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        var hosts = new HostStore(db.Data);
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => hosts.EnsureDefaultAsync(user)));       // gleichzeitig: nur ein Standard-Host
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await hosts.CountAsync(user));

        var connection = await new ConnectionStore(db.Data).CreateAsync(user, "Büro", "desktop", "ziel", 3389, "u");
        Assert.Equal(ids[0], connection.HostId);
        Assert.Equal(HostRules.DefaultName, (await hosts.GetAsync(user, ids[0]))!.Name);
    }

    [Fact]
    public async Task A_connection_cannot_be_put_on_a_foreign_host_and_a_host_with_connections_is_not_deleted()
    {
        await using var db = await TestDb.CreateAsync();
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        var ben = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('ben', 'x', 'y') returning id");
        var hosts = new HostStore(db.Data);
        var connections = new ConnectionStore(db.Data);
        var annas = await hosts.CreateAsync(anna, "Annas");
        var bens = await hosts.CreateAsync(ben, "Bens");

        await Assert.ThrowsAsync<InvalidOperationException>(() => connections.CreateAsync(anna, "X", "terminal", "ziel", 22, "u", hostId: bens.Id));
        var mine = await connections.CreateAsync(anna, "Y", "terminal", "ziel", 22, "u", hostId: annas.Id);
        Assert.Null(await connections.UpdateAsync(anna, mine.Id, "Y", "terminal", "ziel", 22, "u", hostId: bens.Id));      // umziehen zu fremdem Host: nichts geschieht
        Assert.Equal(annas.Id, (await connections.GetAsync(anna, mine.Id))!.HostId);

        Assert.Equal(HostDelete.HasConnections, await hosts.DeleteAsync(anna, annas.Id));
        Assert.Equal(HostDelete.NotFound, await hosts.DeleteAsync(anna, bens.Id));              // fremder Host: wie nicht vorhanden
        Assert.True(await connections.DeleteAsync(anna, mine.Id));
        Assert.Equal(HostDelete.Deleted, await hosts.DeleteAsync(anna, annas.Id));
        Assert.NotNull(await hosts.GetAsync(ben, bens.Id));                                     // Bens Host blieb
    }

    // ---------------------------------------------------------------- TCP-Pruefung

    [Fact]
    public async Task The_probe_sees_an_open_port_and_a_closed_one_and_remembers_the_answer_briefly()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var probe = new TcpProbe(time);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await probe.IsUpAsync("127.0.0.1", port, TestContext.Current.CancellationToken));

        listener.Stop();                                                                         // Port zu: die gemerkte Antwort gilt noch
        Assert.True(await probe.IsUpAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
        time.Advance(TcpProbe.Lifetime + TimeSpan.FromSeconds(1));
        Assert.False(await probe.IsUpAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
        Assert.False(await probe.IsUpAsync("nicht-vorhanden.invalid", 22, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------- Endpunkte

    private sealed class Server(TestDb db, string? allowedHosts = null, Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        public HttpClient Http() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            builder.UseSetting("JULOS_GUACD", "127.0.0.1:1");
            if (allowedHosts is not null) builder.UseSetting("JULOS_ALLOWED_HOSTS", allowedHosts);
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

    private static HttpRequestMessage Req(HttpMethod method, string url, string? cookie, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (method != HttpMethod.Get) { req.Headers.Add("X-JulOS", "1"); req.Headers.Add("Origin", "http://localhost"); }
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<(string Cookie, Guid UserId)> SignedInAsync(HttpClient c, TestDb db)
    {
        var begin = await c.SendAsync(Req(HttpMethod.Post, "/api/setup/begin", null, new { code = SetupCode, user = "jul", password = Password }));
        var secret = (await Json(begin)).GetProperty("secret").GetString()!;
        var totp = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var finish = await c.SendAsync(Req(HttpMethod.Post, "/api/setup/finish", null, new { code = SetupCode, user = "jul", totp }));
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        return (finish.Headers.GetValues("Set-Cookie").First().Split(';')[0], await db.ScalarAsync<Guid>("select id from users limit 1"));
    }

    private static async Task<JsonElement> HostsOf(HttpClient c, string cookie) => (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/hosts", cookie)))).GetProperty("hosts");

    private static JsonElement Find(JsonElement hosts, Guid id) => hosts.EnumerateArray().First(h => h.GetProperty("id").GetGuid() == id);

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until) { if (condition()) return; await Task.Delay(25); }
        Assert.Fail("Bedingung nicht rechtzeitig erfuellt");
    }

    [Fact]
    public async Task Every_host_route_needs_a_session_and_the_first_visit_creates_the_default_host()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var id = Guid.NewGuid();
        foreach (var (method, url) in new[] { (HttpMethod.Get, "/api/hosts"), (HttpMethod.Post, "/api/hosts"), (HttpMethod.Put, $"/api/hosts/{id}"), (HttpMethod.Delete, $"/api/hosts/{id}"), (HttpMethod.Post, $"/api/hosts/{id}/agent/code"), (HttpMethod.Delete, $"/api/hosts/{id}/agent") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(method, url, null, method == HttpMethod.Get ? null : new { name = "x" }))).StatusCode);
        // ohne die Sonderkopfzeile werden auch Aenderungen mit Sitzung abgelehnt
        var (cookie, _) = await SignedInAsync(c, db);
        var bare = new HttpRequestMessage(HttpMethod.Post, "/api/hosts") { Content = JsonContent.Create(new { name = "x" }) };
        bare.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(bare)).StatusCode);

        var hosts = await HostsOf(c, cookie);
        var first = Assert.Single(hosts.EnumerateArray());
        Assert.Equal(HostRules.DefaultName, first.GetProperty("name").GetString());
        Assert.False(first.GetProperty("online").GetBoolean());
        Assert.False(first.TryGetProperty("agent", out _));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from hosts"));
        await HostsOf(c, cookie);                                                                   // ein zweiter Aufruf legt keinen zweiten an
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from hosts"));
    }

    [Fact]
    public async Task Hosts_are_created_renamed_and_deleted_with_validation_limits_and_ownership()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var other = await Insert(db, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreign = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Fremd') returning id", other);

        var created = await c.SendAsync(Req(HttpMethod.Post, "/api/hosts", cookie, new { name = "  Heimserver  " }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var host = (await Json(created)).GetProperty("id").GetGuid();
        Assert.Equal("NAS", (await Json(await c.SendAsync(Req(HttpMethod.Put, $"/api/hosts/{host}", cookie, new { name = "NAS" })))).GetProperty("name").GetString());

        foreach (var bad in new[] { "", "   ", new string('a', 61), "Zeile\neins" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Post, "/api/hosts", cookie, new { name = bad }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, $"/api/hosts/{host}", cookie, new { name = bad }))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Post, "/api/hosts", cookie, new { }))).StatusCode);

        // fremde und unbekannte Hosts: 404, nichts wird veraendert
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, $"/api/hosts/{foreign}", cookie, new { name = "Meiner" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/hosts/{foreign}", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/hosts/{Guid.NewGuid()}", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, $"/api/hosts/{foreign}/agent/code", cookie, new { }))).StatusCode);
        Assert.Equal("Fremd", await db.ScalarAsync<string>("select name from hosts where id = $1", foreign));
        Assert.DoesNotContain(foreign.ToString(), (await HostsOf(c, cookie)).GetRawText());

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/hosts/{host}", cookie))).StatusCode);

        // die Grenze je Benutzer
        for (var i = 0; i < HostRules.MaxPerUser; i++) await Insert(db, "insert into hosts (user_id, name) values ($1, $2) returning id", user, "h" + i);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Post, "/api/hosts", cookie, new { name = "zu viel" }))).StatusCode);
    }

    [Fact]
    public async Task Connections_belong_to_a_host_and_can_move_but_a_host_with_connections_cannot_be_deleted()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var other = await Insert(db, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreign = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Fremd') returning id", other);

        var a = Guid.Parse((await HostsOf(c, cookie))[0].GetProperty("id").GetString()!);                    // der Standard-Host
        var b = (await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/hosts", cookie, new { name = "Zweiter" })))).GetProperty("id").GetGuid();

        // ohne Angabe: erster Host; mit Angabe: der gewaehlte; ein fremder oder unbekannter Host: 404 und es entsteht nichts
        var first = await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/connections", cookie, new { name = "Eins", kind = "terminal", host = "ziel", port = 22 })));
        Assert.Equal(a, first.GetProperty("hostId").GetGuid());
        var second = await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/connections", cookie, new { name = "Zwei", kind = "terminal", host = "ziel", port = 22, hostId = b })));
        Assert.Equal(b, second.GetProperty("hostId").GetGuid());
        foreach (var bad in new[] { foreign, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, "/api/connections", cookie, new { name = "Drei", kind = "terminal", host = "ziel", port = 22, hostId = bad }))).StatusCode);
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from connections"));

        // umziehen: nur zu eigenen Hosts; ohne Angabe bleibt der Host
        var one = first.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{one}", cookie, new { name = "Eins", kind = "terminal", host = "ziel", port = 22, hostId = foreign }))).StatusCode);
        Assert.Equal(a, (await Json(await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{one}", cookie, new { name = "Eins neu", kind = "terminal", host = "ziel", port = 22 })))).GetProperty("hostId").GetGuid());
        Assert.Equal(b, (await Json(await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{one}", cookie, new { name = "Eins", kind = "terminal", host = "ziel", port = 22, hostId = b })))).GetProperty("hostId").GetGuid());

        // die Liste der Verbindungen nennt den Host, die Liste der Hosts die Dienste
        var list = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/connections", cookie)));
        Assert.All(list.GetProperty("connections").EnumerateArray(), x => Assert.Equal(b, x.GetProperty("hostId").GetGuid()));
        var services = Find(await HostsOf(c, cookie), b).GetProperty("services");
        Assert.Equal(2, services.GetArrayLength());

        // ein Host mit Verbindungen bleibt (409), ohne sie geht er; die Verbindungen sind nie nebenbei weg
        var refused = await c.SendAsync(Req(HttpMethod.Delete, $"/api/hosts/{b}", cookie));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Verbindungen", (await Json(refused)).GetProperty("error").GetString());
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from connections"));
        foreach (var conn in new[] { one, second.GetProperty("id").GetGuid() })
            Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/connections/{conn}", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/hosts/{b}", cookie))).StatusCode);
    }

    [Fact]
    public async Task A_host_is_online_through_its_agent_or_a_reachable_service_and_each_service_shows_its_port()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();

        var up = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Läuft') returning id", user);
        var down = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Aus') returning id", user);
        var agentOnly = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Nur Agent') returning id", user);
        var open = await Insert(db, "insert into connections (user_id, host_id, name, kind, host, port) values ($1, $2, 'Offen', 'terminal', '127.0.0.1', $3) returning id", user, up, openPort);
        var shut = await Insert(db, "insert into connections (user_id, host_id, name, kind, host, port) values ($1, $2, 'Zu', 'terminal', '127.0.0.1', $3) returning id", user, up, closedPort);
        await Insert(db, "insert into connections (user_id, host_id, name, kind, host, port) values ($1, $2, 'Zu', 'desktop', '127.0.0.1', $3) returning id", user, down, closedPort);

        var hosts = await HostsOf(c, cookie);
        var running = Find(hosts, up);
        Assert.True(running.GetProperty("online").GetBoolean());
        var states = running.GetProperty("services").EnumerateArray().ToDictionary(s => s.GetProperty("connection").GetGuid(), s => s.GetProperty("online").GetBoolean());
        Assert.True(states[open]);
        Assert.False(states[shut]);
        Assert.False(Find(hosts, down).GetProperty("online").GetBoolean());
        Assert.False(Find(hosts, agentOnly).GetProperty("online").GetBoolean());

        // der Agent allein genuegt: ein verbundener Agent macht den Host online, ohne dass ein Dienst antwortet
        var code = (await Json(await c.SendAsync(Req(HttpMethod.Post, $"/api/hosts/{agentOnly}/agent/code", cookie, new { })))).GetProperty("code").GetString();
        var token = (await Json(await c.PostAsJsonAsync("/api/agent-link/enroll", new { code, version = "0.3.0", platform = "test-linux" }))).GetProperty("token").GetString();
        var afterEnrol = Find(await HostsOf(c, cookie), agentOnly);
        Assert.False(afterEnrol.GetProperty("online").GetBoolean());                                // eingerichtet, aber noch nicht verbunden
        Assert.False(afterEnrol.GetProperty("agent").GetProperty("online").GetBoolean());
        await using (var agent = await FakeLinkAgent.ConnectAsync(app.Server, token))
        {
            await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(agentOnly));
            var live = Find(await HostsOf(c, cookie), agentOnly);
            Assert.True(live.GetProperty("online").GetBoolean());
            Assert.True(live.GetProperty("agent").GetProperty("online").GetBoolean());
        }
        await Eventually(() => !app.Services.GetRequiredService<AgentLinks>().IsOnline(agentOnly));
        Assert.False(Find(await HostsOf(c, cookie), agentOnly).GetProperty("online").GetBoolean());   // Agent weg und kein Dienst: offline
        listener.Stop();
    }

    [Fact]
    public async Task A_service_outside_the_allowed_targets_is_not_probed()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, allowedHosts: "erlaubt.example");
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var host = await Insert(db, "insert into hosts (user_id, name) values ($1, 'H') returning id", user);
        await Insert(db, "insert into connections (user_id, host_id, name, kind, host, port) values ($1, $2, 'Z', 'terminal', '127.0.0.1', $3) returning id", user, host, ((IPEndPoint)listener.LocalEndpoint).Port);
        var h = Find(await HostsOf(c, cookie), host);
        Assert.False(h.GetProperty("services")[0].TryGetProperty("online", out _));              // nicht geprueft: unbekannt, nicht "aus"
        Assert.False(h.GetProperty("online").GetBoolean());
        listener.Stop();
    }

    [Fact]
    public async Task Hostname_and_system_come_from_the_agent_are_kept_and_unusable_values_are_dropped()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var host = await Insert(db, "insert into hosts (user_id, name) values ($1, 'H') returning id", user);
        var code = (await Json(await c.SendAsync(Req(HttpMethod.Post, $"/api/hosts/{host}/agent/code", cookie, new { })))).GetProperty("code").GetString();
        var token = (await Json(await c.PostAsJsonAsync("/api/agent-link/enroll", new { code, version = "0.3.0", platform = "linux-x11" }))).GetProperty("token").GetString()!;

        await using (var agent = await FakeLinkAgent.ConnectAsync(app.Server, token, hostname: "werkbank-01", os: "Debian GNU/Linux 13 (trixie)"))
        {
            await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(host));
            var live = Find(await HostsOf(c, cookie), host);
            Assert.Equal("werkbank-01", live.GetProperty("hostname").GetString());
            Assert.Equal("Debian GNU/Linux 13 (trixie)", live.GetProperty("os").GetString());
        }
        await Eventually(() => !app.Services.GetRequiredService<AgentLinks>().IsOnline(host));
        // nach dem Trennen bleibt die letzte Meldung stehen
        var offline = Find(await HostsOf(c, cookie), host);
        Assert.Equal("werkbank-01", offline.GetProperty("hostname").GetString());
        Assert.Equal("Debian GNU/Linux 13 (trixie)", offline.GetProperty("os").GetString());

        // Werte mit Sonderzeichen (Markup, zu lang) werden nie gespeichert oder angezeigt; die alten bleiben
        await using (var odd = await FakeLinkAgent.ConnectAsync(app.Server, token, hostname: "<img src=x onerror=alert(1)>", os: new string('x', 200)))
        {
            await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(host));
            var shown = Find(await HostsOf(c, cookie), host);
            Assert.Equal("werkbank-01", shown.GetProperty("hostname").GetString());
            Assert.Equal("Debian GNU/Linux 13 (trixie)", shown.GetProperty("os").GetString());
        }
    }

    [Fact]
    public async Task Agent_calls_find_the_agent_of_the_host_of_the_connection_or_of_the_named_host()
    {
        await using var db = await TestDb.CreateAsync();
        await using var socket = new FakeAgent();
        await using var app = new Server(db, configure: s => s.AddSingleton(new AgentOptions(socket.SocketPath)));
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var other = await Insert(db, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var foreign = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Fremd') returning id", other);

        var withAgent = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Mit Agent') returning id", user);
        var without = await Insert(db, "insert into hosts (user_id, name) values ($1, 'Ohne Agent') returning id", user);
        // zwei Verbindungen desselben Hosts teilen sich seinen Agenten
        var rdp = await Insert(db, "insert into connections (user_id, host_id, name, kind, host, port) values ($1, $2, 'RDP', 'desktop', 'ziel', 3389) returning id", user, withAgent);
        var ssh = await Insert(db, "insert into connections (user_id, host_id, name, kind, host, port) values ($1, $2, 'SSH', 'terminal', 'ziel', 22) returning id", user, withAgent);
        var plain = await Insert(db, "insert into connections (user_id, host_id, name, kind, host, port) values ($1, $2, 'Lokal', 'desktop', 'ziel', 3389) returning id", user, without);

        var code = (await Json(await c.SendAsync(Req(HttpMethod.Post, $"/api/hosts/{withAgent}/agent/code", cookie, new { })))).GetProperty("code").GetString();
        var token = (await Json(await c.PostAsJsonAsync("/api/agent-link/enroll", new { code, version = "0.3.0", platform = "test-linux" }))).GetProperty("token").GetString()!;
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, token);
        await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(withAgent));

        async Task<string> Title(string query) => (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/agent/windows" + query, cookie)))).GetProperty("windows")[0].GetProperty("title").GetString()!;
        Assert.Equal("Per Link", await Title($"?c={rdp}"));
        Assert.Equal("Per Link", await Title($"?c={ssh}"));
        Assert.Equal("Per Link", await Title($"?h={withAgent}"));
        Assert.Equal("Claude", await Title($"?c={plain}"));                                       // Host ohne Agent: lokaler Socket wie bisher
        Assert.Equal("Claude", await Title($"?h={without}"));

        // eine Verbindung auf einen anderen Host ziehen, und ihr Weg folgt dem neuen Host
        var moved = await c.SendAsync(Req(HttpMethod.Put, $"/api/connections/{rdp}", cookie, new { name = "RDP", kind = "desktop", host = "ziel", port = 3389, hostId = without }));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("Claude", await Title($"?c={rdp}"));
        Assert.Equal("Per Link", await Title($"?c={ssh}"));

        // fremde, kaputte, unbekannte Kennungen
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, $"/api/agent/windows?h={foreign}", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, $"/api/agent/windows?h={Guid.NewGuid()}", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Get, "/api/agent/windows?h=nein", cookie))).StatusCode);
        var health = await Json(await c.SendAsync(Req(HttpMethod.Get, $"/api/agent/health?h={withAgent}", cookie)));
        Assert.True(health.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Deleting_a_host_removes_its_agent_and_ends_the_link()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, user) = await SignedInAsync(c, db);
        var host = Guid.Parse((await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/hosts", cookie, new { name = "Weg" })))).GetProperty("id").GetString()!);
        var code = (await Json(await c.SendAsync(Req(HttpMethod.Post, $"/api/hosts/{host}/agent/code", cookie, new { })))).GetProperty("code").GetString();
        var token = (await Json(await c.PostAsJsonAsync("/api/agent-link/enroll", new { code, version = "0.3.0", platform = "test-linux" }))).GetProperty("token").GetString()!;
        await using var agent = await FakeLinkAgent.ConnectAsync(app.Server, token);
        await Eventually(() => app.Services.GetRequiredService<AgentLinks>().IsOnline(host));

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Delete, $"/api/hosts/{host}", cookie))).StatusCode);
        Assert.True(await agent.WaitClosedAsync(Wait));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from agents"));
        await Assert.ThrowsAnyAsync<Exception>(() => FakeLinkAgent.ConnectAsync(app.Server, token));          // das Token gilt nicht mehr
    }

    /// <summary>Die Migration 009 ist rueckwaertskompatibel: bestehende Verbindungen bekommen leere Domaene, leere Notizen und Deutsch (wie bisher fest eingestellt).</summary>
    [Fact]
    public async Task Migration_009_gives_existing_connections_the_old_behaviour()
    {
        await using var old = await TestDb.CreateAsync(migrate: false);
        var asm = typeof(Migrator).Assembly;
        await old.ExecAsync("create table schema_migrations (id text primary key, applied_at timestamptz not null default now())");
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("database/", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var id = name["database/".Length..^4];
            if (string.CompareOrdinal(id, "009") >= 0) continue;
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            await old.ExecAsync(await reader.ReadToEndAsync());
            await old.ExecAsync("insert into schema_migrations (id) values ($1)", id);
        }
        var user = Guid.NewGuid();
        await old.ExecAsync("insert into users (id, name, password_hash, totp_secret, totp_confirmed) values ($1, 'jul', 'x', 'y', true)", user);
        await old.ExecAsync("insert into connections (user_id, name, kind, host, port, username) values ($1, 'Alt', 'desktop', 'ziel', 3389, 'u')", user);

        await new Migrator(old.Data).RunAsync(TestContext.Current.CancellationToken);
        var c = (await new ConnectionStore(old.Data).ListAsync(user)).Single();
        Assert.Equal(("", "", "de"), (c.Domain, c.Notes, c.KeyboardLayout));
    }
}
