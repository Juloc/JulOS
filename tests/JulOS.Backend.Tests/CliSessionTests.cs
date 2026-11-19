using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using JulOS.Backend.Data;
using JulOS.Integrations.Ssh;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Programm-Sitzungen (docs/CLI-SESSIONS.md) durch Relay, Verbindungs-API und gegen das Test-SSH-Ziel.</summary>
public sealed class CliSessionRelayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly Guid Id = Guid.Parse("0a1b2c3d-1111-2222-3333-444455556666");

    private static SshTarget Target(SshLaunch? launch, string name = "Server")
        => new(Guid.NewGuid(), new SshConnectOptions("ziel", 22, "tester", "geheim", 100, 30), null, null, (_, _) => Task.CompletedTask, null, launch, name, Id);

    /// <summary>Laesst den Relay laufen, liest die Bereit-Nachricht und beendet die Sitzung.</summary>
    private static async Task<(System.Text.Json.JsonElement Ready, FakeSshSession Session, string Written)> RunAsync(FakeSshSession session, SshLaunch? launch, List<string>? log = null)
    {
        var (browser, server) = await SshRelayTests.SocketPairAsync();
        var relay = new SshRelay(log is null ? NullLogger<SshRelay>.Instance : new ListLogger(log), () => session);
        var run = relay.RunAsync(server, Target(launch), SshRelayTests.Alive, CancellationToken.None);
        var ready = SshRelayTests.Body(await SshRelayTests.ReceiveAsync(browser));
        string written;
        lock (session.Written) written = string.Concat(session.Written.Select(Encoding.UTF8.GetString));
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
        return (ready, session, written);
    }

    private sealed class ListLogger(List<string> lines) : ILogger<SshRelay>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { lock (lines) lines.Add(formatter(state, exception)); }
    }

    [Fact]
    public async Task A_plain_shell_opens_no_extra_channel_and_writes_nothing()
    {
        foreach (var launch in new SshLaunch?[] { null, SshLaunch.Shell })
        {
            var (ready, session, written) = await RunAsync(new FakeSshSession(), launch);
            Assert.Empty(session.Execs);
            Assert.Equal("", written);
            Assert.False(ready.TryGetProperty("program", out _));                      // das Protokoll bleibt fuer gewoehnliche Shells, wie es war
            Assert.False(ready.TryGetProperty("notice", out _));
        }
    }

    [Fact]
    public async Task A_program_is_probed_then_started_and_the_browser_is_told_what_runs()
    {
        var launch = new SshLaunch("claude", "~/work", true, "");
        var (ready, session, written) = await RunAsync(new FakeSshSession { ProbeOutput = "julos-probe prog=1 dir=1 tmux=1\n" }, launch);
        Assert.Equal(SshLaunchRules.ProbeCommand(launch), Assert.Single(session.Execs));
        Assert.Equal(SshLaunchRules.Line(launch, true, "Server", Id), written);
        Assert.Equal("claude", ready.GetProperty("program").GetString());
        Assert.Equal("tmux", ready.GetProperty("keep").GetString());
        Assert.False(ready.TryGetProperty("notice", out _));
    }

    [Theory]
    [InlineData("julos-probe prog=0 dir=1 tmux=1", "shell", "program-missing", false)]
    [InlineData("julos-probe prog=1 dir=0 tmux=1", "shell", "dir-missing", false)]
    [InlineData("julos-probe prog=1 dir=1 tmux=0", "claude", "tmux-missing", true)]
    [InlineData("julos-probe prog=0 dir=1 tmux=0", "shell", "program-missing tmux-missing", false)]
    [InlineData(null, "shell", "probe-failed", false)]
    [InlineData("kaputt", "shell", "probe-failed", false)]
    public async Task Whatever_is_missing_gives_a_notice_and_a_fallback_never_silence(string? probe, string program, string notice, bool startsProgram)
    {
        var (ready, _, written) = await RunAsync(new FakeSshSession { ProbeOutput = probe }, new SshLaunch("claude", "~/work", true, ""));
        Assert.Equal(program, ready.GetProperty("program").GetString());
        Assert.Equal(notice, ready.GetProperty("notice").GetString());
        Assert.Equal(startsProgram, written.Contains("exec claude"));
        if (!startsProgram) Assert.DoesNotContain("exec claude", written);
    }

    [Fact]
    public async Task The_custom_command_is_never_logged()
    {
        const string secret = "geheimer-befehl-xyz --token=abc123";
        var log = new List<string>();
        var (_, session, written) = await RunAsync(new FakeSshSession { ProbeOutput = "julos-probe prog=1 dir=1 tmux=1" }, new SshLaunch("custom", "", false, secret), log);
        Assert.Contains(secret, written);                                                  // er geht an das Ziel ...
        Assert.DoesNotContain(secret, string.Concat(session.Execs));                       // ... auch nicht in die Pruefung (die kennt ihn gar nicht)
        Assert.DoesNotContain(log, l => l.Contains("geheimer-befehl", StringComparison.Ordinal) || l.Contains("abc123", StringComparison.Ordinal) || l.Contains("--token"));   // ... aber nie ins Protokoll
        Assert.Contains(log, l => l.Contains("custom"));
    }

    [Fact]
    public void The_ready_message_only_carries_the_extra_fields_that_apply()
    {
        var plain = System.Text.Json.JsonDocument.Parse(SshProtocol.Ready("known", "SHA256:x", new SshStartPlan(null, "shell", false, [])).AsMemory(1)).RootElement;
        Assert.Equal("shell", plain.GetProperty("program").GetString());
        Assert.False(plain.TryGetProperty("keep", out _));
        Assert.False(plain.TryGetProperty("notice", out _));
        var both = System.Text.Json.JsonDocument.Parse(SshProtocol.Ready("new", "SHA256:x", new SshStartPlan("l", "codex", true, ["tmux-missing", "dir-missing"])).AsMemory(1)).RootElement;
        Assert.Equal(("codex", "tmux", "tmux-missing dir-missing"), (both.GetProperty("program").GetString(), both.GetProperty("keep").GetString(), both.GetProperty("notice").GetString()));
    }
}

public sealed class CliSessionHttpTests
{
    private static readonly string Key = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());

    private static async Task<JsonElementResult> Post(HttpClient c, string cookie, object body, string url = "/api/connections")
    {
        var r = await c.SendAsync(Req(HttpMethod.Post, url, cookie, body));
        return new(r.StatusCode, await Json(r));
    }
    private static async Task<JsonElementResult> Put(HttpClient c, string cookie, Guid id, object body)
    {
        var r = await c.SendAsync(Req(HttpMethod.Put, "/api/connections/" + id, cookie, body));
        return new(r.StatusCode, await Json(r));
    }
    private sealed record JsonElementResult(HttpStatusCode Status, System.Text.Json.JsonElement Body);

    private static object Term(string name) => new { name, kind = "terminal", host = "ziel", username = "u" };

    [Fact]
    public async Task Migration_014_gives_existing_connections_a_plain_shell_and_an_old_backup_restores_with_the_defaults()
    {
        await using var old = await TestDb.CreateAsync(migrate: false);
        var asm = typeof(Migrator).Assembly;
        await old.ExecAsync("create table schema_migrations (id text primary key, applied_at timestamptz not null default now())");
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("database/", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var id = name["database/".Length..^4];
            if (string.CompareOrdinal(id, "014") >= 0) continue;
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            await old.ExecAsync(await reader.ReadToEndAsync());
            await old.ExecAsync("insert into schema_migrations (id) values ($1)", id);
        }
        var user = Guid.NewGuid();
        await old.ExecAsync("insert into users (id, name, password_hash, totp_secret, totp_confirmed) values ($1, 'jul', 'x', 'y', true)", user);
        await old.ExecAsync("insert into connections (user_id, name, kind, host, port, username) values ($1, 'Altes Terminal', 'terminal', 'ziel', 22, 'u')", user);
        var dump = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(old.Data, dump, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await new Migrator(old.Data).RunAsync(TestContext.Current.CancellationToken);
        foreach (var c in await new ConnectionStore(old.Data).ListAsync(user)) Assert.Equal(("shell", "", false, ""), (c.Program, c.WorkDir, c.KeepAlive, c.CustomCommand));

        await using var fresh = await TestDb.CreateAsync();
        await DbDump.RestoreAsync(fresh.Data, new StringReader(dump.ToString()), false, TestContext.Current.CancellationToken);
        var restored = await new ConnectionStore(fresh.Data).ListAsync(user);
        Assert.All(restored, c => Assert.Equal(("shell", "", false, ""), (c.Program, c.WorkDir, c.KeepAlive, c.CustomCommand)));
    }

    [Fact]
    public async Task A_connection_without_program_fields_stays_a_plain_shell_and_the_fields_round_trip()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);

        var plain = await Post(c, cookie, new { name = "T", kind = "terminal", host = "ziel", username = "u" });
        Assert.Equal(HttpStatusCode.OK, plain.Status);
        Assert.Equal(("shell", "", false, ""), (plain.Body.GetProperty("program").GetString(), plain.Body.GetProperty("workDir").GetString(), plain.Body.GetProperty("keepAlive").GetBoolean(), plain.Body.GetProperty("customCommand").GetString()));

        var claude = await Post(c, cookie, new { name = "C", kind = "terminal", host = "ziel", username = "u", program = "claude", workDir = "~/projekte/app", keepAlive = true });
        Assert.Equal(HttpStatusCode.OK, claude.Status);
        var id = claude.Body.GetProperty("id").GetGuid();
        var list = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/connections", cookie)));
        var row = list.GetProperty("connections").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        Assert.Equal(("claude", "~/projekte/app", true), (row.GetProperty("program").GetString(), row.GetProperty("workDir").GetString(), row.GetProperty("keepAlive").GetBoolean()));

        // Aendern ohne die Felder: sie bleiben (aeltere Clients kennen sie nicht)
        var renamed = await Put(c, cookie, id, new { name = "C2", kind = "terminal", host = "ziel", username = "u" });
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal(("claude", "~/projekte/app", true), (renamed.Body.GetProperty("program").GetString(), renamed.Body.GetProperty("workDir").GetString(), renamed.Body.GetProperty("keepAlive").GetBoolean()));
        // gezielt zuruecksetzen
        var reset = await Put(c, cookie, id, new { name = "C2", kind = "terminal", host = "ziel", username = "u", program = "shell", workDir = "", keepAlive = false });
        Assert.Equal(("shell", "", false), (reset.Body.GetProperty("program").GetString(), reset.Body.GetProperty("workDir").GetString(), reset.Body.GetProperty("keepAlive").GetBoolean()));

        // Eigener Befehl: gespeichert, dem Besitzer wieder ausgegeben
        var custom = await Post(c, cookie, new { name = "X", kind = "terminal", host = "ziel", username = "u", program = "custom", customCommand = "claude --resume" });
        Assert.Equal("claude --resume", custom.Body.GetProperty("customCommand").GetString());
        // ein Desktop kennt keine Programme: die Felder werden ignoriert
        var desktop = await Post(c, cookie, new { name = "D", kind = "desktop", host = "ziel", username = "u", program = "claude", workDir = "~/x", keepAlive = true });
        Assert.Equal(HttpStatusCode.OK, desktop.Status);
        Assert.Equal(("shell", "", false), (desktop.Body.GetProperty("program").GetString(), desktop.Body.GetProperty("workDir").GetString(), desktop.Body.GetProperty("keepAlive").GetBoolean()));
        // Terminal -> Desktop setzt zurueck
        var toDesktop = await Put(c, cookie, id, new { name = "C2", kind = "desktop", host = "ziel", username = "u" });
        Assert.Equal("shell", toDesktop.Body.GetProperty("program").GetString());
    }

    [Theory]
    [InlineData("program", "bash")]
    [InlineData("program", "claude;ls")]
    [InlineData("workDir", "a;b")]
    [InlineData("workDir", "$(id)")]
    [InlineData("workDir", "a\nb")]
    [InlineData("workDir", "`id`")]
    [InlineData("workDir", "a b")]
    [InlineData("customCommand", "a\nb")]
    public async Task Bad_program_fields_are_refused_with_a_clear_message_and_nothing_is_saved(string field, string value)
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var body = new Dictionary<string, object> { ["name"] = "T", ["kind"] = "terminal", ["host"] = "ziel", ["username"] = "u", ["program"] = field == "customCommand" ? "custom" : "claude", [field] = value };
        var r = await Post(c, cookie, body);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Body.GetProperty("error").GetString()));
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from connections"));
    }

    [Fact]
    public async Task A_custom_program_needs_its_command_and_the_ticket_never_takes_the_program_from_the_request()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, cookie, new { name = "X", kind = "terminal", host = "ziel", username = "u", program = "custom" })).Status);

        // Ein Ticket fuer eine gewoehnliche Verbindung bleibt eines fuer die Shell, auch wenn die Anfrage etwas anderes behauptet
        var plain = await Post(c, cookie, new { name = "T", kind = "terminal", host = "ziel", username = "u" });
        var id = plain.Body.GetProperty("id").GetGuid();
        var ticket = await c.SendAsync(Req(HttpMethod.Post, "/api/remote/ticket", cookie, new { connection = id, password = "geheim", cols = 80, rows = 24, program = "custom", customCommand = "rm -rf /", workDir = "/", keepAlive = true, launch = new { program = "custom" } }));
        Assert.Equal(HttpStatusCode.OK, ticket.StatusCode);
        var tickets = app.Services.GetRequiredService<SshTickets>();
        var target = tickets.Take((await Json(ticket)).GetProperty("ticket").GetString()!, await db.ScalarAsync<Guid>("select id from users limit 1"));
        Assert.NotNull(target);
        Assert.Equal(SshLaunch.Shell, target!.Launch);
        Assert.Equal(id, target.ConnectionId);
    }

    [Fact]
    public async Task The_ticket_carries_the_stored_program_and_quick_connect_always_a_shell()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, userId) = await SignedInAsync(c, db);
        var made = await Post(c, cookie, new { name = "Claude auf Büro", kind = "terminal", host = "ziel", username = "u", program = "claude", workDir = "~/p", keepAlive = true });
        var id = made.Body.GetProperty("id").GetGuid();
        var tickets = app.Services.GetRequiredService<SshTickets>();

        var r = await c.SendAsync(Req(HttpMethod.Post, "/api/remote/ticket", cookie, new { connection = id, password = "geheim", cols = 80, rows = 24 }));
        var target = tickets.Take((await Json(r)).GetProperty("ticket").GetString()!, userId)!;
        Assert.Equal(new SshLaunch("claude", "~/p", true, ""), target.Launch);
        Assert.Equal(("Claude auf Büro", id), (target.ConnectionName, target.ConnectionId));

        var quick = await c.SendAsync(Req(HttpMethod.Post, "/api/remote/ticket", cookie, new { quick = new { kind = "terminal", host = "ziel", username = "u" }, password = "geheim", cols = 80, rows = 24 }));
        Assert.Equal(HttpStatusCode.OK, quick.StatusCode);
        Assert.Equal(SshLaunch.Shell, tickets.Take((await Json(quick)).GetProperty("ticket").GetString()!, userId)!.Launch);
    }

    [Fact]
    public async Task A_program_tile_copies_target_password_and_host_key_and_keeps_the_original()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, secretKey: Key);
        var c = app.Http();
        var (cookie, userId) = await SignedInAsync(c, db);
        var source = await Post(c, cookie, new { name = "Server", kind = "terminal", host = "ziel", port = 2222, username = "u", password = "geheimes-passwort" });
        Assert.Equal(HttpStatusCode.OK, source.Status);
        var sourceId = source.Body.GetProperty("id").GetGuid();
        var store = new ConnectionStore(db.Data);
        await store.SetHostKeyAsync(userId, sourceId, "ssh-ed25519 SHA256:bekannt");

        var tile = await Post(c, cookie, new { program = "claude", workDir = "~/projekte", name = "Claude auf Büro-PC" }, $"/api/connections/{sourceId}/program");
        Assert.Equal(HttpStatusCode.OK, tile.Status);
        var tileId = tile.Body.GetProperty("id").GetGuid();
        Assert.NotEqual(sourceId, tileId);
        Assert.Equal(("Claude auf Büro-PC", "terminal", "claude", "~/projekte", true), (tile.Body.GetProperty("name").GetString(), tile.Body.GetProperty("kind").GetString(), tile.Body.GetProperty("program").GetString(), tile.Body.GetProperty("workDir").GetString(), tile.Body.GetProperty("hasPassword").GetBoolean()));
        Assert.Equal(source.Body.GetProperty("hostId").GetGuid(), tile.Body.GetProperty("hostId").GetGuid());       // derselbe Host (docs/HOSTS.md)

        var made = (await store.GetAsync(userId, tileId))!;
        Assert.Equal(("ziel", 2222, "u", "ssh-ed25519 SHA256:bekannt"), (made.Host, made.Port, made.Username, made.HostKey));
        // das Passwort ist fuer die neue Kennung neu verschluesselt (die Kennung steckt in der Verschluesselung) und liefert dasselbe
        var box = app.Services.GetRequiredService<JulOS.Backend.Logic.SecretBox>();
        Assert.Equal("geheimes-passwort", box.Decrypt((await store.GetSecretAsync(userId, tileId))!, tileId));
        Assert.Null(box.Decrypt((await store.GetSecretAsync(userId, tileId))!, sourceId));
        // das Original ist unveraendert
        var orig = (await store.GetAsync(userId, sourceId))!;
        Assert.Equal(("shell", "", true), (orig.Program, orig.WorkDir, orig.HasPassword));
        // ein zweites Mal: eine weitere Kachel (eine Verbindung ist ein Ziel plus ein Programm, dasselbe Ziel darf mehrere haben)
        var again = await Post(c, cookie, new { program = "codex" }, $"/api/connections/{sourceId}/program");
        Assert.Equal("Codex auf Mein Rechner", again.Body.GetProperty("name").GetString());                            // ohne Name: Programm und Host
        Assert.Equal(3L, await db.ScalarAsync<long>("select count(*) from connections"));
    }

    [Fact]
    public async Task A_program_tile_is_refused_for_desktops_other_programs_bad_input_and_other_users()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, userId) = await SignedInAsync(c, db);
        var term = (await Post(c, cookie, Term("T"))).Body.GetProperty("id").GetGuid();
        var desk = (await Post(c, cookie, new { name = "D", kind = "desktop", host = "ziel", username = "u" })).Body.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, cookie, new { program = "claude" }, $"/api/connections/{desk}/program")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, cookie, new { program = "shell" }, $"/api/connections/{term}/program")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, cookie, new { program = "custom" }, $"/api/connections/{term}/program")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, cookie, new { program = "claude", workDir = "a;b" }, $"/api/connections/{term}/program")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(c, cookie, new { program = "claude" }, $"/api/connections/{Guid.NewGuid()}/program")).Status);
        Assert.Equal(2L, await db.ScalarAsync<long>("select count(*) from connections"));

        // die Verbindung eines anderen Benutzers ist nicht erreichbar
        var other = await ApiKit.Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'y') returning id");
        var theirs = await new ConnectionStore(db.Data).CreateAsync(other, "Fremd", "terminal", "ziel", 22, "u");
        Assert.Equal(HttpStatusCode.NotFound, (await Post(c, cookie, new { program = "claude" }, $"/api/connections/{theirs.Id}/program")).Status);
        Assert.NotEqual(userId, other);

        // ohne Anmeldung nichts
        var anon = await c.SendAsync(Req(HttpMethod.Post, $"/api/connections/{term}/program", null, new { program = "claude" }));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }
}

/// <summary>Gegen das Test-SSH-Ziel der Test-Instanz (JULOS_TEST_SSH, siehe RealSshTests): ein Programm gibt es dort nicht, ein Verzeichnis wie /tmp schon.</summary>
public sealed class CliSessionRealSshTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <param name="after">erst tippen, wenn diese Ausgabe da ist (nach einem exec geht Eingabe verloren, die eine Shell schon vorgelesen hat)</param>
    private static async Task<(string Ready, string Output)> RunAsync(SshLaunch launch, string typed, Func<string, bool> until, string? after = null)
    {
        var (host, port, password) = RealSshTests.Target();
        var session = new SshNetSession();
        var (browser, server) = await SshRelayTests.SocketPairAsync();
        var target = new SshTarget(Guid.NewGuid(), new SshConnectOptions(host, port, "tester", password, 100, 30), null, null, (_, _) => Task.CompletedTask, null, launch, "Test", Guid.NewGuid());
        var run = new SshRelay(NullLogger<SshRelay>.Instance, () => session).RunAsync(server, target, SshRelayTests.Alive, CancellationToken.None);
        using var cts = new CancellationTokenSource(Wait);
        var buf = new byte[70000];
        var r = await browser.ReceiveAsync(buf, cts.Token);
        var ready = Encoding.UTF8.GetString(buf, 1, r.Count - 1);
        var all = new StringBuilder();
        while (after is not null && !all.ToString().Contains(after, StringComparison.Ordinal))
        {
            r = await browser.ReceiveAsync(buf, cts.Token);
            all.Append(Encoding.UTF8.GetString(buf, 1, r.Count - 1));
        }
        await SshRelayTests.SendAsync(browser, [SshProtocol.ToShellData, .. Encoding.UTF8.GetBytes(typed)]);
        while (!until(all.ToString()))
        {
            r = await browser.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) break;
            all.Append(Encoding.UTF8.GetString(buf, 1, r.Count - 1));
        }
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
        return (ready, all.ToString());
    }

    [Fact]
    public async Task A_working_directory_starts_the_shell_there()
    {
        var (ready, output) = await RunAsync(new SshLaunch("shell", "/tmp", false, ""), "echo cwd-$(pwd)-ende\n", o => o.Contains("cwd-/tmp-ende"));
        Assert.Contains("\"program\":\"shell\"", ready);
        Assert.DoesNotContain("notice", ready);
        Assert.Contains("cwd-/tmp-ende", output);
    }

    [Fact]
    public async Task A_missing_program_gives_a_notice_and_a_working_shell()
    {
        var (ready, output) = await RunAsync(new SshLaunch("claude", "", false, ""), "echo shell-$((6*7))\n", o => o.Contains("shell-42"));
        Assert.Contains("\"program\":\"shell\"", ready);
        Assert.Contains("\"notice\":\"program-missing\"", ready);
        Assert.Contains("shell-42", output);
    }

    [Fact]
    public async Task A_missing_directory_gives_a_notice_and_no_program()
    {
        var (ready, output) = await RunAsync(new SshLaunch("claude", "/gibt/es/nicht", false, ""), "echo shell-$((6*7))\n", o => o.Contains("shell-42"));
        Assert.Contains("\"notice\":\"dir-missing\"", ready);
        Assert.Contains("shell-42", output);
    }

    [Fact]
    public async Task A_custom_command_runs_instead_of_the_shell_in_the_working_directory()
    {
        var (ready, output) = await RunAsync(new SshLaunch("custom", "/tmp", false, "echo start-$((6*7)); pwd; cat"), "hallo-eingabe\n", o => o.Split("hallo-eingabe").Length >= 3, after: "start-42");
        Assert.Contains("\"program\":\"custom\"", ready);
        Assert.Contains("start-42", output);
        Assert.Contains("/tmp", output);
        Assert.Contains("hallo-eingabe", output);                                         // cat gibt die Eingabe zurueck (nach dem Echo des Terminals)
    }

    [Fact]
    public async Task The_probe_runs_beside_the_shell_within_the_limit()
    {
        var (host, port, password) = RealSshTests.Target();
        await using var s = new SshNetSession();
        await s.ConnectAsync(new SshConnectOptions(host, port, "tester", password, 80, 24), _ => true, CancellationToken.None);
        var probe = SshLaunchRules.ParseProbe(await s.ExecAsync(SshLaunchRules.ProbeCommand(new SshLaunch("claude", "/tmp", true, "")), TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.NotNull(probe);
        Assert.True(probe!.Dir);
        Assert.False(probe.Program);                                                       // auf dem Wegwerf-Ziel gibt es kein claude
        Assert.Equal("ok", (await s.ExecAsync("echo ok", TimeSpan.FromSeconds(5), CancellationToken.None))?.Trim());
        Assert.Null(await s.ExecAsync("sleep 10", TimeSpan.FromMilliseconds(500), CancellationToken.None));               // Frist abgelaufen: null
    }
}
