using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JulOS.Frontend.Files;
using JulOS.Integrations.Files;
using JulOS.Integrations.Ssh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Die Dateien-App fuer Hosts durch den ganzen Server (echte Middleware und Datenbank), mit Dateisystemen im Speicher statt SFTP-Zielen.</summary>
public sealed class RemoteFilesHttpTests
{
    private static readonly string TestKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 7)).ToArray());
    private const string StoredPassword = "Gespeichert-Passwort-123", TypedPassword = "Getippt-Passwort-456";

    private sealed class Rig(TestDb db, string? write = "on", string? maxMb = null, string? allowed = null, List<string>? log = null) : WebApplicationFactory<Program>
    {
        public readonly Queue<FakeFileSession> Hosts = new();
        public readonly List<FakeFileSession> Created = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            builder.UseSetting("JULOS_SECRET_KEY", TestKey);
            if (write is not null) builder.UseSetting("JULOS_FILES_WRITE", write);
            if (maxMb is not null) builder.UseSetting("JULOS_FILES_MAX_MB", maxMb);
            if (allowed is not null) builder.UseSetting("JULOS_ALLOWED_HOSTS", allowed);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton(new FileSessionFactory(() => { var f = Hosts.Count > 0 ? Hosts.Dequeue() : new FakeFileSession(); Created.Add(f); return f; }));
                if (log is not null) s.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace).AddProvider(new Capture(log)));
            });
        }

        public SftpPool Pool => Services.GetRequiredService<SftpPool>();
    }

    private sealed class Client(HttpClient http, string cookie)
    {
        public HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
        {
            return ApiKit.Req(m, url, cookie, body);
        }
        public async Task<(HttpStatusCode Status, JsonElement Body)> Json(HttpMethod m, string url, object? body = null)
        {
            var r = await http.SendAsync(Req(m, url, body));
            var text = await r.Content.ReadAsStringAsync();
            return (r.StatusCode, text.Length == 0 ? default : ApiKit.J(text));
        }
        public Task<(HttpStatusCode Status, JsonElement Body)> Get(string url) => Json(HttpMethod.Get, url);
        public Task<(HttpStatusCode Status, JsonElement Body)> Post(string url, object body) => Json(HttpMethod.Post, url, body);
        public Task<HttpResponseMessage> Raw(HttpRequestMessage r) => http.SendAsync(r);
        public HttpRequestMessage Authed(HttpMethod m, string url) { var r = new HttpRequestMessage(m, url); r.Headers.Add("Cookie", cookie); if (m != HttpMethod.Get) { r.Headers.Add("X-JulOS", "1"); r.Headers.Add("Origin", "http://localhost"); } return r; }
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static async Task<(Client Api, Guid UserId, HttpClient Http)> SignedIn(Rig app, TestDb db)
    {
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, user) = await SignedInAsync(http, db);
        return (new Client(http, cookie), user, http);
    }

    private static async Task<Guid> Connection(Client api, string name, string kind = "terminal", string? password = StoredPassword, string host = "srv-a")
    {
        var (status, body) = await api.Post("/api/connections", new { name, kind, host, username = "jul", password });
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> WaitJob(Client api, string job)
    {
        for (var i = 0; i < 200; i++)
        {
            var (_, b) = await api.Get("/api/files/remote/jobs/" + job);
            if (b.GetProperty("state").GetString() != "running") return b;
            await Task.Delay(25);
        }
        throw new TimeoutException("Der Auftrag wurde nicht fertig.");
    }

    private static string FingerprintOf(FakeFileSession f) => f.Key.Id;

    // ---------- Schutz ----------

    [Fact]
    public async Task Everything_needs_a_session_and_changes_need_the_header_and_the_origin()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        foreach (var url in new[] { "/api/files/info", "/api/files/remote/list?c=" + Guid.NewGuid(), "/api/files/remote/text?c=" + Guid.NewGuid() + "&path=/x", "/api/files/remote/download?c=" + Guid.NewGuid() + "&path=/x", "/api/files/audit" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(url)).StatusCode);
        var (api, _, _) = await SignedIn(app, db);
        var id = await Connection(api, "A");
        foreach (var url in new[] { "/api/files/remote/open", "/api/files/remote/mkdir", "/api/files/remote/rename", "/api/files/remote/delete", "/api/files/remote/copy", "/api/files/remote/plan", "/api/files/remote/upload?c=" + id + "&dir=/&name=x&confirm=1" })
        {
            var noHeader = api.Authed(HttpMethod.Post, url); noHeader.Headers.Remove("X-JulOS"); noHeader.Content = JsonContent.Create(new { connection = id });
            Assert.Equal(HttpStatusCode.Forbidden, (await api.Raw(noHeader)).StatusCode);
            var wrongOrigin = api.Authed(HttpMethod.Post, url); wrongOrigin.Headers.Remove("Origin"); wrongOrigin.Headers.Add("Origin", "https://evil.example"); wrongOrigin.Content = JsonContent.Create(new { connection = id });
            Assert.Equal(HttpStatusCode.Forbidden, (await api.Raw(wrongOrigin)).StatusCode);
        }
        Assert.Empty(app.Created);                                                        // abgelehnt, bevor irgendetwas verbunden wurde
    }

    [Fact]
    public async Task Info_says_whether_writing_is_on_and_it_is_off_by_default()
    {
        await using var db = await TestDb.CreateAsync();
        await using var off = new Rig(db, write: null);
        var (api, _, _) = await SignedIn(off, db);
        var (_, info) = await api.Get("/api/files/info");
        Assert.False(info.GetProperty("write").GetBoolean());
        Assert.Equal(2048L * 1024 * 1024, info.GetProperty("maxBytes").GetInt64());
        Assert.True(info.GetProperty("local").GetBoolean());
        await using var db2 = await TestDb.CreateAsync();
        await using var on = new Rig(db2, write: "on", maxMb: "5");
        var (api2, _, _) = await SignedIn(on, db2);
        var (_, info2) = await api2.Get("/api/files/info");
        Assert.True(info2.GetProperty("write").GetBoolean());
        Assert.Equal(5L * 1024 * 1024, info2.GetProperty("maxBytes").GetInt64());
    }

    [Fact]
    public async Task Only_ssh_connections_of_the_user_and_allowed_targets_are_places()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db, allowed: "srv-a");
        var (api, _, _) = await SignedIn(app, db);
        var rdp = await Connection(api, "PC", "desktop");
        var blocked = await Insert(db, "insert into connections (user_id, name, kind, host, port, username, host_id) values ((select id from users where name = 'jul'), 'B', 'terminal', 'srv-b', 22, 'x', (select id from hosts limit 1)) returning id");
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'x') returning id");
        var theirs = await Insert(db, "insert into connections (user_id, name, kind, host, port, username, host_id) values ($1, 'T', 'terminal', 'srv-a', 22, 'x', (select id from hosts limit 1)) returning id", anna);
        Assert.Equal(("not_ssh", HttpStatusCode.BadRequest), (Code((await api.Post("/api/files/remote/open", new { connection = rdp })).Body), (await api.Post("/api/files/remote/open", new { connection = rdp })).Status));
        var b = await api.Post("/api/files/remote/open", new { connection = blocked });
        Assert.Equal((HttpStatusCode.Forbidden, "target_not_allowed"), (b.Status, Code(b.Body)));
        var t = await api.Post("/api/files/remote/open", new { connection = theirs });
        Assert.Equal((HttpStatusCode.NotFound, "connection_not_found"), (t.Status, Code(t.Body)));
        var l = await api.Get("/api/files/remote/list?c=" + theirs);
        Assert.Equal(HttpStatusCode.NotFound, l.Status);
        Assert.Empty(app.Created);
    }

    // ---------- Host-Schluessel und Passwort ----------

    [Fact]
    public async Task The_first_host_key_must_be_confirmed_and_then_it_is_remembered_a_changed_one_needs_a_new_confirmation()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, user, _) = await SignedIn(app, db);
        var c = await Connection(api, "A");
        var first = new FakeFileSession(); app.Hosts.Enqueue(first);

        var refused = await api.Post("/api/files/remote/open", new { connection = c });
        Assert.Equal((HttpStatusCode.Conflict, "host_key_new"), (refused.Status, Code(refused.Body)));
        Assert.Equal(first.Key.Fingerprint, refused.Body.GetProperty("fingerprint").GetString());
        Assert.Equal("ssh-ed25519", refused.Body.GetProperty("algorithm").GetString());
        Assert.True(await db.ScalarAsync<bool>("select host_key is null from connections where id = $1", c));       // nichts gemerkt ohne Bestaetigung
        Assert.Equal(0, app.Pool.CountFor(user));

        // Eine falsche Bestaetigung (ein anderer Schluessel) gilt nicht
        app.Hosts.Enqueue(new FakeFileSession());
        Assert.Equal("host_key_new", Code((await api.Post("/api/files/remote/open", new { connection = c, acceptHostKey = "ssh-ed25519 SHA256:other" })).Body));

        var second = new FakeFileSession(); app.Hosts.Enqueue(second);
        var ok = await api.Post("/api/files/remote/open", new { connection = c, acceptHostKey = FingerprintOf(second) });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("remembered", ok.Body.GetProperty("hostKey").GetString());
        Assert.Equal(second.Home, ok.Body.GetProperty("home").GetString());
        Assert.Equal(FingerprintOf(second), await db.ScalarAsync<string>("select host_key from connections where id = $1", c));
        Assert.Equal("known", (await api.Post("/api/files/remote/open", new { connection = c })).Body.GetProperty("hostKey").GetString());      // die offene Sitzung

        // Neue Sitzung, derselbe Schluessel: ohne Nachfrage
        await app.Pool.CloseAsync(user, c);
        var third = new FakeFileSession(); app.Hosts.Enqueue(third);
        Assert.Equal(HttpStatusCode.OK, (await api.Post("/api/files/remote/open", new { connection = c })).Status);

        // Der Schluessel aendert sich: Warnung mit beiden Schluesseln, Abbruch, nichts wird ueberschrieben
        await app.Pool.CloseAsync(user, c);
        var changed = new FakeFileSession { Key = new SshHostKey("ssh-ed25519", "SHA256:" + new string('Z', 43)) }; app.Hosts.Enqueue(changed);
        var warn = await api.Post("/api/files/remote/open", new { connection = c });
        Assert.Equal((HttpStatusCode.Conflict, "host_key_changed"), (warn.Status, Code(warn.Body)));
        Assert.Equal(changed.Key.Fingerprint, warn.Body.GetProperty("fingerprint").GetString());
        Assert.Equal(FingerprintOf(second), warn.Body.GetProperty("known").GetString());
        Assert.Equal(FingerprintOf(second), await db.ScalarAsync<string>("select host_key from connections where id = $1", c));
        Assert.Equal(0, app.Pool.CountFor(user));
        // Auch ein anderer Weg, den Pool zu fuellen, geht nie am Schluessel vorbei
        app.Hosts.Enqueue(new FakeFileSession { Key = changed.Key });
        Assert.Equal("host_key_changed", Code((await api.Get("/api/files/remote/list?c=" + c)).Body));

        var again = new FakeFileSession { Key = changed.Key }; app.Hosts.Enqueue(again);
        var replaced = await api.Post("/api/files/remote/open", new { connection = c, acceptHostKey = changed.Key.Id });
        Assert.Equal("replaced", replaced.Body.GetProperty("hostKey").GetString());
        Assert.Equal(changed.Key.Id, await db.ScalarAsync<string>("select host_key from connections where id = $1", c));
        // Der Fehler steht im Verlauf (ohne Schluessel oder Passwort)
        Assert.True(await db.ScalarAsync<long>("select count(*) from files_audit where result = 'failed' and detail = 'host_key_changed'") >= 1);
    }

    [Fact]
    public async Task The_terminal_and_the_files_share_the_remembered_host_key()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Rig(db);
        var (api, _, _) = await SignedIn(app, db);
        var c = await Connection(api, "A");
        var host = new FakeFileSession();
        await db.ExecAsync("update connections set host_key = $1 where id = $2", host.Key.Id, c);        // das Terminal hat ihn schon gemerkt
        app.Hosts.Enqueue(host);
        var ok = await api.Post("/api/files/remote/open", new { connection = c });
        Assert.Equal(("known", HttpStatusCode.OK), (ok.Body.GetProperty("hostKey").GetString(), ok.Status));
    }

    [Fact]
    public async Task A_stored_password_is_used_a_typed_one_is_never_stored_and_neither_leaves_the_server()
    {
        await using var db = await TestDb.CreateAsync();
        var log = new List<string>();
        await using var app = new Rig(db, log: log);
        var (api, user, _) = await SignedIn(app, db);
        var stored = await Connection(api, "mit Passwort");
        var typed = await Connection(api, "ohne", password: null);
        var host = new FakeFileSession { LogonPassword = StoredPassword }; host.Nodes.Remove("/"); host = new FakeFileSession { LogonPassword = StoredPassword };
        await db.ExecAsync("update connections set host_key = $1", host.Key.Id);

        app.Hosts.Enqueue(host);
        var open = await api.Post("/api/files/remote/open", new { connection = stored });
        Assert.Equal(HttpStatusCode.OK, open.Status);
        Assert.Equal(StoredPassword, host.ConnectedPassword);

        // Kein gespeichertes Passwort und keine offene Sitzung: die Seite muss es abfragen
        var need = await api.Post("/api/files/remote/open", new { connection = typed });
        Assert.Equal((HttpStatusCode.Conflict, "password_required"), (need.Status, Code(need.Body)));
        Assert.Equal((HttpStatusCode.Conflict, "password_required"), ((await api.Get("/api/files/remote/list?c=" + typed)).Status, Code((await api.Get("/api/files/remote/list?c=" + typed)).Body)));
        var wrong = new FakeFileSession { LogonPassword = "anders" }; await db.ExecAsync("update connections set host_key = $1 where id = $2", wrong.Key.Id, typed); app.Hosts.Enqueue(wrong);
        var bad = await api.Post("/api/files/remote/open", new { connection = typed, password = TypedPassword });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "logon_failed"), (bad.Status, Code(bad.Body)));
        Assert.Equal(0, app.Pool.CountFor(user) - 1);                                     // nur die erste Verbindung ist offen

        var good = new FakeFileSession { LogonPassword = TypedPassword }; await db.ExecAsync("update connections set host_key = $1 where id = $2", good.Key.Id, typed); app.Hosts.Enqueue(good);
        Assert.Equal(HttpStatusCode.OK, (await api.Post("/api/files/remote/open", new { connection = typed, password = TypedPassword })).Status);
        // Danach geht es ohne erneutes Tippen, solange die Sitzung offen ist
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/files/remote/list?c=" + typed)).Status);
        // Nie gespeichert
        Assert.True(await db.ScalarAsync<bool>("select secret is null from connections where id = $1", typed));
        // Ist sie geschlossen, fragt der Server wieder
        await app.Pool.CloseAsync(user, typed);
        Assert.Equal("password_required", Code((await api.Get("/api/files/remote/list?c=" + typed)).Body));

        // Weder in einer Antwort noch im Log noch im Verlauf
        Assert.DoesNotContain(log, l => l.Contains(TypedPassword) || l.Contains(StoredPassword));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from files_audit where coalesce(path,'') || coalesce(detail,'') || host_name like '%Passwort-%'"));
        Assert.DoesNotContain(TypedPassword, bad.Body.ToString());
    }

    [Fact]
    public async Task An_unreachable_or_sftpless_target_gives_clear_codes_and_no_leak()
    {
        await using var db = await TestDb.CreateAsync();
        var app = new Rig(db);
        await using var rigGuard = app;
        var (api, user, _) = await SignedIn(app, db);
        var c = await Connection(api, "A");
        // Eine Fabrik, die nicht verbinden kann: der Pool bleibt leer
        var dead = new FakeFileSession();
        app.Hosts.Enqueue(dead);
        var r = await api.Post("/api/files/remote/open", new { connection = c });
        Assert.Equal("host_key_new", Code(r.Body));
        Assert.Equal(0, app.Pool.CountFor(user));
        Assert.Equal(1, dead.Disposed);                                                   // die halb offene Sitzung wurde freigegeben
    }

    // ---------- Lesen ----------

    private static async Task<(Rig App, Client Api, Guid C, FakeFileSession Host, Guid User)> OpenHost(TestDb db, Action<FakeFileSession>? seed = null, string? write = "on", string? maxMb = null)
    {
        var app = new Rig(db, write, maxMb);
        var (api, user, _) = await SignedIn(app, db);
        var c = await Connection(api, "A");
        var host = new FakeFileSession();
        seed?.Invoke(host);
        await db.ExecAsync("update connections set host_key = $1 where id = $2", host.Key.Id, c);
        app.Hosts.Enqueue(host);
        Assert.Equal(HttpStatusCode.OK, (await api.Post("/api/files/remote/open", new { connection = c })).Status);
        return (app, api, c, host, user);
    }

    [Fact]
    public async Task Listing_starts_at_home_sorts_folders_first_shows_links_and_hides_names_with_control_characters()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, h => h.Dir("/home/tester/Zeta").Dir("/home/tester/alpha").File("/home/tester/b10.txt", "x").File("/home/tester/b2.txt", "xx")
            .Link("/home/tester/ln-dir", "/home/tester/alpha").Link("/home/tester/ln-file", "/home/tester/b2.txt").Link("/home/tester/broken", "/nowhere").File("/home/tester/bad\nname", "x"));
        await using var rigGuard = app;
        var (status, body) = await api.Get($"/api/files/remote/list?c={c}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("/home/tester", body.GetProperty("path").GetString());
        var names = body.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["alpha", "ln-dir", "Zeta", "b2.txt", "b10.txt", "broken", "ln-file"], names);            // Ordner (auch Links auf Ordner) zuerst, Zahlen natuerlich
        Assert.Equal(1, body.GetProperty("hidden").GetInt32());
        var link = body.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("name").GetString() == "ln-dir");
        Assert.Equal(("link", "directory", true), (link.GetProperty("kind").GetString(), link.GetProperty("resolved").GetString(), link.GetProperty("openable").GetBoolean()));
        var broken = body.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("name").GetString() == "broken");
        Assert.False(broken.TryGetProperty("resolved", out _));
        Assert.Equal("/home/tester/b2.txt", body.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("name").GetString() == "b2.txt").GetProperty("path").GetString());
        Assert.Equal("/home/tester", (await api.Get($"/api/files/remote/list?c={c}&path=~")).Body.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("relativ/pfad")]
    [InlineData("/a/../etc")]
    [InlineData("/a%00b")]
    public async Task Bad_paths_are_refused_before_anything_reaches_the_target(string path)
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db);
        await using var rigGuard = app;
        var before = host.Log.Count;
        var r = await api.Get($"/api/files/remote/list?c={c}&path={path}");
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_path"), (r.Status, Code(r.Body)));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_path"), ((await api.Get($"/api/files/remote/text?c={c}&path={path}")).Status, "invalid_path"));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Raw(api.Authed(HttpMethod.Get, $"/api/files/remote/download?c={c}&path={path}"))).StatusCode);
        Assert.Equal(before, host.Log.Count);
    }

    [Fact]
    public async Task A_listing_is_capped_at_2000_entries_and_marked()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, _, _) = await OpenHost(db, h => { for (var i = 0; i < 2100; i++) h.File($"/big/f{i:0000}", "x"); });
        await using var rigGuard = app;
        var (_, body) = await api.Get($"/api/files/remote/list?c={c}&path=/big");
        Assert.Equal(2000, body.GetProperty("entries").GetArrayLength());
        Assert.True(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Missing_and_denied_paths_give_clear_codes_and_land_in_the_history()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, h => { h.Dir("/secret"); h.Denied.Add("/secret"); h.File("/f", "x"); });
        await using var rigGuard = app;
        var nf = await api.Get($"/api/files/remote/list?c={c}&path=/nope");
        Assert.Equal((HttpStatusCode.NotFound, "not_found"), (nf.Status, Code(nf.Body)));
        var denied = await api.Get($"/api/files/remote/list?c={c}&path=/secret");
        Assert.Equal((HttpStatusCode.Forbidden, "denied"), (denied.Status, Code(denied.Body)));
        var notDir = await api.Get($"/api/files/remote/list?c={c}&path=/f");
        Assert.Equal((HttpStatusCode.BadRequest, "not_a_directory"), (notDir.Status, Code(notDir.Body)));
        var audit = (await api.Get("/api/files/audit")).Body.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("action").GetString(), e.GetProperty("result").GetString(), e.GetProperty("detail").GetString())).ToArray();
        Assert.Contains(("list", "failed", "denied"), audit);
        Assert.Contains(("list", "failed", "not_found"), audit);
    }

    [Fact]
    public async Task The_text_view_returns_text_as_a_json_value_refuses_binary_and_cuts_at_one_megabyte()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, _, _) = await OpenHost(db, h => h.File("/t.html", "<script>alert(1)</script>").File("/bin", [0, 1, 2]).File("/big.txt", new string('a', 1_500_000)).Dir("/d"));
        await using var rigGuard = app;
        var r = await api.Raw(api.Authed(HttpMethod.Get, $"/api/files/remote/text?c={c}&path=/t.html"));
        Assert.StartsWith("application/json", r.Content.Headers.ContentType!.ToString());
        Assert.Equal("<script>alert(1)</script>", JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("text").GetString());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        var bin = await api.Get($"/api/files/remote/text?c={c}&path=/bin");
        Assert.Equal((HttpStatusCode.UnsupportedMediaType, "not_text"), (bin.Status, Code(bin.Body)));
        Assert.Equal("not_text", Code((await api.Get($"/api/files/remote/text?c={c}&path=/d")).Body));
        var big = (await api.Get($"/api/files/remote/text?c={c}&path=/big.txt")).Body;
        Assert.True(big.GetProperty("truncated").GetBoolean());
        Assert.Equal(1024 * 1024, big.GetProperty("text").GetString()!.Length);
        Assert.Equal(1_500_000, big.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task A_download_streams_the_file_as_an_attachment_never_as_a_page_and_never_beyond_the_limit()
    {
        await using var db = await TestDb.CreateAsync();
        var content = Encoding.UTF8.GetBytes("<html><svg onload=alert(1)>");
        var (app, api, c, host, _) = await OpenHost(db, h => h.File("/pub/seite.html", content).File("/pub/gross.bin", new byte[2 * 1024 * 1024]).Dir("/pub/ordner").File("/pub/ä \"x\".txt", "u"), maxMb: "1");
        await using var rigGuard = app;
        var r = await api.Raw(api.Authed(HttpMethod.Get, $"/api/files/remote/download?c={c}&path=/pub/seite.html"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/octet-stream", r.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment;", r.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(content.Length, r.Content.Headers.ContentLength);
        Assert.Equal(content, await r.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, host.OpenStreams);

        var big = await api.Raw(api.Authed(HttpMethod.Get, $"/api/files/remote/download?c={c}&path=/pub/gross.bin"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.Equal("too_large", Code(ApiKit.J(await big.Content.ReadAsStringAsync())));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Raw(api.Authed(HttpMethod.Get, $"/api/files/remote/download?c={c}&path=/pub/ordner"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Raw(api.Authed(HttpMethod.Get, $"/api/files/remote/download?c={c}&path=/nope"))).StatusCode);
        var odd = await api.Raw(api.Authed(HttpMethod.Get, $"/api/files/remote/download?c={c}&path=" + Uri.EscapeDataString("/pub/ä \"x\".txt")));
        Assert.DoesNotContain("\"x\"", string.Join(";", odd.Content.Headers.ContentDisposition!.ToString().Split(';').Skip(1).Where(p => p.Contains("filename=\""))).Replace("filename=\"_", ""));
    }

    // ---------- Schreiben ----------

    [Fact]
    public async Task With_writing_off_every_write_is_refused_changes_nothing_and_is_written_to_the_history()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, h => h.File("/home/tester/a.txt", "A"), write: null);
        await using var rigGuard = app;
        var calls = new (string Url, object Body)[]
        {
            ("/api/files/remote/mkdir", new { connection = c, dir = "/home/tester", name = "n", confirm = true }),
            ("/api/files/remote/rename", new { connection = c, path = "/home/tester/a.txt", name = "b.txt", confirm = true }),
            ("/api/files/remote/delete", new { connection = c, paths = new[] { "/home/tester/a.txt" }, confirm = true }),
            ("/api/files/remote/copy", new { connection = c, paths = new[] { "/home/tester/a.txt" }, target = new { connection = c, dir = "/" }, confirm = true }),
        };
        foreach (var (url, body) in calls)
        {
            var r = await api.Post(url, body);
            Assert.Equal((HttpStatusCode.Forbidden, "write_off"), (r.Status, Code(r.Body)));
        }
        var up = api.Authed(HttpMethod.Post, $"/api/files/remote/upload?c={c}&dir=/home/tester&name=u.txt&confirm=1"); up.Content = new ByteArrayContent([1, 2, 3]);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Raw(up)).StatusCode);
        Assert.DoesNotContain(host.Log, l => l.StartsWith("create ") || l.StartsWith("mkdir ") || l.StartsWith("rename ") || l.StartsWith("delete ") || l.StartsWith("rmdir "));
        Assert.Equal(["a.txt"], host.Under("/home/tester").Select(RemotePath.NameOf).ToArray());
        Assert.Equal(5, await db.ScalarAsync<long>("select count(*) from files_audit where result = 'denied' and detail = 'write_off'"));
        // Lesen bleibt an
        Assert.Equal(HttpStatusCode.OK, (await api.Get($"/api/files/remote/list?c={c}")).Status);
    }

    [Fact]
    public async Task Without_the_confirmation_of_the_page_nothing_is_written()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, h => h.File("/home/tester/a.txt", "A"));
        await using var rigGuard = app;
        foreach (var (url, body) in new (string, object)[]
        {
            ("/api/files/remote/mkdir", new { connection = c, dir = "/home/tester", name = "n" }),
            ("/api/files/remote/rename", new { connection = c, path = "/home/tester/a.txt", name = "b.txt", confirm = false }),
            ("/api/files/remote/delete", new { connection = c, paths = new[] { "/home/tester/a.txt" } }),
            ("/api/files/remote/copy", new { connection = c, paths = new[] { "/home/tester/a.txt" }, target = new { connection = c, dir = "/" } }),
        })
            Assert.Equal("confirm_required", Code((await api.Post(url, body)).Body));
        var up = api.Authed(HttpMethod.Post, $"/api/files/remote/upload?c={c}&dir=/home/tester&name=u.txt"); up.Content = new ByteArrayContent([1]);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Raw(up)).StatusCode);
        Assert.Equal(["a.txt"], host.Under("/home/tester").Select(RemotePath.NameOf).ToArray());
    }

    private static async Task<HttpResponseMessage> Upload(Client api, Guid c, string dir, string name, byte[] data, bool overwrite = false, bool stream = false)
    {
        var r = api.Authed(HttpMethod.Post, $"/api/files/remote/upload?c={c}&dir={Uri.EscapeDataString(dir)}&name={Uri.EscapeDataString(name)}&confirm=1" + (overwrite ? "&overwrite=1" : ""));
        r.Content = stream ? new StreamContent(new MemoryStream(data)) : new ByteArrayContent(data);
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await api.Raw(r);
    }

    [Fact]
    public async Task Upload_writes_atomically_refuses_existing_files_without_overwrite_and_records_each_result()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, h => h.File("/home/tester/vorhanden.txt", "alt").Dir("/home/tester/ordner"));
        await using var rigGuard = app;
        var ok = await Upload(api, c, "/home/tester", "neu.txt", Encoding.UTF8.GetBytes("inhalt"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("inhalt", host.Text("/home/tester/neu.txt"));
        Assert.DoesNotContain(host.Nodes.Keys, k => k.EndsWith(".part"));
        Assert.StartsWith("create /home/tester/.julos-", host.Log.First(l => l.StartsWith("create ")));

        var exists = await Upload(api, c, "/home/tester", "vorhanden.txt", [1, 2], stream: true);
        Assert.Equal((HttpStatusCode.Conflict, "exists"), (exists.StatusCode, Code(ApiKit.J(await exists.Content.ReadAsStringAsync()))));
        Assert.Equal("alt", host.Text("/home/tester/vorhanden.txt"));
        var over = await Upload(api, c, "/home/tester", "vorhanden.txt", Encoding.UTF8.GetBytes("neu"), overwrite: true);
        Assert.Equal(HttpStatusCode.OK, over.StatusCode);
        Assert.True(ApiKit.J(await over.Content.ReadAsStringAsync()).GetProperty("replaced").GetBoolean());
        Assert.Equal("neu", host.Text("/home/tester/vorhanden.txt"));

        foreach (var bad in new[] { "", "..", "a/b", "x\ny" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Upload(api, c, "/home/tester", bad, [1])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(api, c, "/home/tester/gibtsnicht", "x", [1])).StatusCode);
        Assert.Equal("not_a_directory", Code(ApiKit.J(await (await Upload(api, c, "/home/tester/neu.txt", "x", [1])).Content.ReadAsStringAsync())));
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(api, c, "relativ", "x", [1])).StatusCode);

        var rows = (await api.Get("/api/files/audit")).Body.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("action").GetString(), e.GetProperty("result").GetString(), e.TryGetProperty("detail", out var d) ? d.GetString() : null)).ToArray();
        Assert.Contains(("upload", "ok", null), rows);
        Assert.Contains(("upload", "ok", "overwrite"), rows);
        Assert.Contains(("upload", "failed", "exists"), rows);
        Assert.Equal(0, host.OpenStreams);
    }

    [Fact]
    public async Task An_upload_beyond_the_limit_is_cut_in_the_stream_and_leaves_nothing_behind()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, maxMb: "1");
        await using var rigGuard = app;
        var big = await Upload(api, c, "/home/tester", "gross.bin", new byte[1024 * 1024 + 1], stream: true);          // ohne Content-Length-Pruefung vorab: der Strom zaehlt
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.Equal("too_large", Code(ApiKit.J(await big.Content.ReadAsStringAsync())));
        Assert.Empty(host.Under("/home/tester"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(api, c, "/home/tester", "gross2.bin", new byte[1024 * 1024 + 1])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Upload(api, c, "/home/tester", "klein.bin", new byte[1024 * 1024])).StatusCode);
    }

    [Fact]
    public async Task Mkdir_and_rename_check_names_and_never_replace_a_folder()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, h => h.File("/home/tester/a.txt", "A").File("/home/tester/b.txt", "B").Dir("/home/tester/d").Dir("/home/tester/e"));
        await using var rigGuard = app;
        Assert.Equal(HttpStatusCode.OK, (await api.Post("/api/files/remote/mkdir", new { connection = c, dir = "/home/tester", name = "neu", confirm = true })).Status);
        Assert.True(host.Has("/home/tester/neu"));
        Assert.Equal("exists", Code((await api.Post("/api/files/remote/mkdir", new { connection = c, dir = "/home/tester", name = "neu", confirm = true })).Body));
        Assert.Equal("invalid_name", Code((await api.Post("/api/files/remote/mkdir", new { connection = c, dir = "/home/tester", name = "a/b", confirm = true })).Body));

        Assert.Equal("exists", Code((await api.Post("/api/files/remote/rename", new { connection = c, path = "/home/tester/a.txt", name = "b.txt", confirm = true })).Body));
        Assert.Equal("B", host.Text("/home/tester/b.txt"));
        var ok = await api.Post("/api/files/remote/rename", new { connection = c, path = "/home/tester/a.txt", name = "b.txt", overwrite = true, confirm = true });
        Assert.Equal(("/home/tester/b.txt", HttpStatusCode.OK), (ok.Body.GetProperty("path").GetString(), ok.Status));
        Assert.Equal("A", host.Text("/home/tester/b.txt"));
        Assert.False(host.Has("/home/tester/a.txt"));
        Assert.Equal("type_mismatch", Code((await api.Post("/api/files/remote/rename", new { connection = c, path = "/home/tester/d", name = "e", overwrite = true, confirm = true })).Body));
        Assert.True(host.Has("/home/tester/e") && host.Has("/home/tester/d"));
        Assert.Equal("same_place", Code((await api.Post("/api/files/remote/rename", new { connection = c, path = "/home/tester/d", name = "d", confirm = true })).Body));
        Assert.Equal("invalid_name", Code((await api.Post("/api/files/remote/rename", new { connection = c, path = "/home/tester/d", name = "..", confirm = true })).Body));
        Assert.Equal("not_found", Code((await api.Post("/api/files/remote/rename", new { connection = c, path = "/home/tester/zz", name = "q", confirm = true })).Body));
        Assert.Equal("invalid_path", Code((await api.Post("/api/files/remote/rename", new { connection = c, path = "/", name = "q", confirm = true })).Body));
    }

    [Fact]
    public async Task Plan_counts_what_a_delete_or_copy_would_touch_and_names_the_conflicts()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, h => h.Dir("/p/d/sub").File("/p/d/a", "123").File("/p/d/sub/b", "45").Link("/p/d/l", "/p/d/a").File("/p/x", "9").Dir("/p/leer").Dir("/z").File("/z/x", "alt"));
        await using var rigGuard = app;
        var del = (await api.Post("/api/files/remote/plan", new { action = "delete", connection = c, paths = new[] { "/p/d", "/p/x", "/p/leer" } })).Body;
        Assert.Equal((3, 3, 1, 6L, true), (del.GetProperty("files").GetInt32(), del.GetProperty("directories").GetInt32(), del.GetProperty("links").GetInt32(), del.GetProperty("bytes").GetInt64(), del.GetProperty("nonEmpty").GetBoolean()));
        var leer = (await api.Post("/api/files/remote/plan", new { action = "delete", connection = c, paths = new[] { "/p/leer" } })).Body;
        Assert.False(leer.GetProperty("nonEmpty").GetBoolean());
        var copy = (await api.Post("/api/files/remote/plan", new { action = "copy", connection = c, paths = new[] { "/p/x", "/p/d" }, target = new { connection = c, dir = "/z" } })).Body;
        Assert.Equal(["x"], copy.GetProperty("conflicts").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("invalid_request", Code((await api.Post("/api/files/remote/plan", new { action = "format", connection = c, paths = new[] { "/p" } })).Body));
        Assert.Equal("invalid_request", Code((await api.Post("/api/files/remote/plan", new { action = "copy", connection = c, paths = new[] { "/p" } })).Body));
        Assert.Equal("not_found", Code((await api.Post("/api/files/remote/plan", new { action = "delete", connection = c, paths = new[] { "/p/nope" } })).Body));
        Assert.DoesNotContain(host.Log, l => l.StartsWith("delete ") || l.StartsWith("create "));                  // der Plan aendert nichts
    }

    [Fact]
    public async Task Delete_runs_as_a_job_a_folder_with_content_needs_the_explicit_recursive_step()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, _) = await OpenHost(db, h => h.Dir("/p/d/sub").File("/p/d/a", "1").File("/p/d/sub/b", "2").Link("/p/d/out", "/keep").Dir("/keep").File("/keep/wichtig", "w").File("/p/einzeln", "x"));
        await using var rigGuard = app;
        var first = await api.Post("/api/files/remote/delete", new { connection = c, paths = new[] { "/p/d" }, confirm = true });
        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        var j1 = await WaitJob(api, first.Body.GetProperty("job").GetString()!);
        Assert.Equal(("failed", "not_empty"), (j1.GetProperty("state").GetString(), j1.GetProperty("error").GetString()));
        Assert.True(host.Has("/p/d/sub/b"));

        var second = await api.Post("/api/files/remote/delete", new { connection = c, paths = new[] { "/p/d", "/p/einzeln" }, recursive = true, confirm = true });
        var j2 = await WaitJob(api, second.Body.GetProperty("job").GetString()!);
        Assert.Equal("done", j2.GetProperty("state").GetString());
        Assert.False(host.Has("/p/d")); Assert.False(host.Has("/p/einzeln"));
        Assert.True(host.Has("/keep/wichtig"));                                           // nicht durch den Link hindurch
        Assert.Equal((4, 2), (j2.GetProperty("result").GetProperty("files").GetInt32(), j2.GetProperty("result").GetProperty("directories").GetInt32()));

        var rows = (await api.Get("/api/files/audit")).Body.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("action").GetString() == "delete").Select(e => (e.GetProperty("result").GetString(), e.GetProperty("path").GetString())).ToArray();
        Assert.Contains(("failed", "/p/d"), rows);
        Assert.Contains(("ok", "/p/d (+1)"), rows);
        Assert.Equal(0, host.OpenStreams);
        Assert.Equal("job_not_found", Code((await api.Get("/api/files/remote/jobs/" + Guid.NewGuid())).Body));
    }

    [Fact]
    public async Task Copy_and_move_between_two_hosts_stream_through_the_server_and_a_job_can_be_cancelled()
    {
        await using var db = await TestDb.CreateAsync();
        var app = new Rig(db);
        await using var rigGuard = app;
        var (api, user, _) = await SignedIn(app, db);
        var a = await Connection(api, "Quelle"); var b = await Connection(api, "Ziel", host: "srv-b");
        var src = new FakeFileSession(); src.Dir("/s/proj/sub").File("/s/proj/x.txt", "XXX").File("/s/proj/sub/y.txt", "YY").Link("/s/proj/l", "/s/proj/x.txt").File("/s/zwei", new byte[3000]);
        var dst = new FakeFileSession(); dst.Dir("/dst");
        foreach (var (id, f) in new[] { (a, src), (b, dst) }) { await db.ExecAsync("update connections set host_key = $1 where id = $2", f.Key.Id, id); app.Hosts.Enqueue(f); Assert.Equal(HttpStatusCode.OK, (await api.Post("/api/files/remote/open", new { connection = id })).Status); }

        var plan = (await api.Post("/api/files/remote/plan", new { action = "copy", connection = a, paths = new[] { "/s/proj" }, target = new { connection = b, dir = "/dst" } })).Body;
        Assert.Empty(plan.GetProperty("conflicts").EnumerateArray());
        var started = await api.Post("/api/files/remote/copy", new { connection = a, paths = new[] { "/s/proj" }, target = new { connection = b, dir = "/dst" }, confirm = true });
        Assert.Equal(HttpStatusCode.Accepted, started.Status);
        var job = await WaitJob(api, started.Body.GetProperty("job").GetString()!);
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal("YY", dst.Text("/dst/proj/sub/y.txt"));
        Assert.False(dst.Has("/dst/proj/l"));                                             // Links werden nie kopiert
        Assert.Equal(1, job.GetProperty("skipped").GetInt32());
        Assert.Equal(5, job.GetProperty("bytes").GetInt64());
        Assert.True(src.Has("/s/proj/x.txt"));
        Assert.DoesNotContain(dst.Nodes.Keys, k => k.EndsWith(".part"));

        // Ein vorhandenes Ziel nur mit overwrite
        var again = await api.Post("/api/files/remote/copy", new { connection = a, paths = new[] { "/s/proj/x.txt" }, target = new { connection = b, dir = "/dst/proj" }, confirm = true });
        Assert.Equal("failed", (await WaitJob(api, again.Body.GetProperty("job").GetString()!)).GetProperty("state").GetString());

        // Verschieben ueber Hosts: erst kopiert, dann an der Quelle geloescht (der Link bleibt)
        var move = await api.Post("/api/files/remote/copy", new { connection = a, paths = new[] { "/s/proj" }, target = new { connection = b, dir = "/dst" }, move = true, overwrite = true, confirm = true });
        var moved = await WaitJob(api, move.Body.GetProperty("job").GetString()!);
        Assert.Equal("done", moved.GetProperty("state").GetString());
        Assert.False(src.Has("/s/proj/x.txt")); Assert.False(src.Has("/s/proj/sub"));
        Assert.True(src.Has("/s/proj/l"));

        // Abbrechen: der Auftrag haelt mitten in der Datei an, raeumt auf und meldet cancelled
        var gate = new TaskCompletionSource(); var reading = new TaskCompletionSource();
        src.BeforeRead = async (p, ct) => { if (p == "/s/zwei") { reading.TrySetResult(); await gate.Task.WaitAsync(ct); } };
        var slow = await api.Post("/api/files/remote/copy", new { connection = a, paths = new[] { "/s/zwei" }, target = new { connection = b, dir = "/dst" }, confirm = true });
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var jobId = slow.Body.GetProperty("job").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await api.Raw(api.Authed(HttpMethod.Post, $"/api/files/remote/jobs/{jobId}/cancel"))).StatusCode);
        var cancelled = await WaitJob(api, jobId);
        Assert.Equal("cancelled", cancelled.GetProperty("state").GetString());
        Assert.False(dst.Has("/dst/zwei"));
        Assert.DoesNotContain(dst.Nodes.Keys, k => k.EndsWith(".part"));
        Assert.Equal(0, src.OpenStreams + dst.OpenStreams);

        var rows = (await api.Get("/api/files/audit")).Body.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("action").GetString(), e.GetProperty("result").GetString(), e.GetProperty("host").GetString())).ToArray();
        Assert.Contains(("copy", "ok", "Quelle → Ziel"), rows);
        Assert.Contains(("move", "ok", "Quelle → Ziel"), rows);
        Assert.Contains(("copy", "cancelled", "Quelle → Ziel"), rows);
        Assert.Contains(("copy", "failed", "Quelle → Ziel"), rows);
    }

    [Fact]
    public async Task Jobs_belong_to_their_user_and_a_user_runs_at_most_two_at_once()
    {
        await using var db = await TestDb.CreateAsync();
        var gate = new TaskCompletionSource();
        var (app, api, c, host, _) = await OpenHost(db, h => h.File("/s/a", "1").File("/s/b", "1").File("/s/c", "1").Dir("/dst"));
        await using var rigGuard = app;
        host.BeforeRead = (_, ct) => gate.Task.WaitAsync(ct);
        async Task<string> Start(string name) => (await api.Post("/api/files/remote/copy", new { connection = c, paths = new[] { "/s/" + name }, target = new { connection = c, dir = "/dst" }, confirm = true })).Body.GetProperty("job").GetString()!;
        var j1 = await Start("a"); var j2 = await Start("b");
        var third = await api.Post("/api/files/remote/copy", new { connection = c, paths = new[] { "/s/c" }, target = new { connection = c, dir = "/dst" }, confirm = true });
        Assert.Equal((HttpStatusCode.TooManyRequests, "busy"), (third.Status, Code(third.Body)));
        // Ein anderer Benutzer sieht den Auftrag nicht
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'x') returning id");
        Assert.Equal("job_not_found", Code((await api.Get("/api/files/remote/jobs/" + Guid.NewGuid())).Body));
        gate.SetResult();
        Assert.Equal("done", (await WaitJob(api, j1)).GetProperty("state").GetString());
        Assert.Equal("done", (await WaitJob(api, j2)).GetProperty("state").GetString());
        Assert.Equal(0, host.OpenStreams);
        Assert.NotEqual(Guid.Empty, anna);
    }

    [Fact]
    public async Task A_lost_connection_in_the_middle_reports_connection_lost_and_the_next_request_reconnects()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, host, user) = await OpenHost(db, h => h.File("/home/tester/a", "x"));
        await using var rigGuard = app;
        host.Drop();
        var next = new FakeFileSession(); next.File("/home/tester/neu", "y"); app.Hosts.Enqueue(next);
        // Eine abgerissene Sitzung wird nie weitergereicht: die naechste Anfrage verbindet neu (Schluessel ist gemerkt, Passwort gespeichert)
        await db.ExecAsync("update connections set host_key = $1 where id = $2", next.Key.Id, c);
        var r = await api.Get($"/api/files/remote/list?c={c}");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(["neu"], r.Body.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray());
        Assert.Equal(1, host.Disposed + (host.Connected ? 0 : 0));
        Assert.Equal(1, app.Pool.CountFor(user));
    }

    [Fact]
    public async Task The_history_lists_the_users_own_rows_newest_first_without_contents()
    {
        await using var db = await TestDb.CreateAsync();
        var (app, api, c, _, user) = await OpenHost(db);
        await using var rigGuard = app;
        await api.Post("/api/files/remote/mkdir", new { connection = c, dir = "/home/tester", name = "eins", confirm = true });
        await api.Post("/api/files/remote/mkdir", new { connection = c, dir = "/home/tester", name = "zwei", confirm = true });
        var anna = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('anna', 'x', 'x') returning id");
        await db.ExecAsync("insert into files_audit (at, user_id, host_name, action, path, result) values (now(), $1, 'fremd', 'delete', '/geheim', 'ok')", anna);
        var entries = (await api.Get("/api/files/audit?limit=10")).Body.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(["/home/tester/zwei", "/home/tester/eins"], entries.Select(e => e.GetProperty("path").GetString()).ToArray());
        Assert.All(entries, e => Assert.Equal("A", e.GetProperty("host").GetString()));
        Assert.DoesNotContain("geheim", await (await api.Raw(api.Authed(HttpMethod.Get, "/api/files/audit"))).Content.ReadAsStringAsync());
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from files_audit where user_id = $1", user));
    }

    [Fact]
    public async Task Long_paths_in_the_history_are_cut_at_the_end_and_control_characters_become_spaces()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new JulOS.Backend.Data.FileAuditStore(db.Data, TimeProvider.System);
        var user = await Insert(db, "insert into users (name, password_hash, totp_secret) values ('u', 'x', 'x') returning id");
        await store.AddAsync(user, null, "H", "delete", "/" + new string('a', 400) + "/ende\nzeile", "ok");
        var e = (await store.ListAsync(user, 5)).Single();
        Assert.Equal(JulOS.Backend.Data.FileAuditStore.MaxPath, e.Path!.Length);
        Assert.StartsWith("…", e.Path);
        Assert.EndsWith("/ende zeile", e.Path);
        for (var i = 0; i < JulOS.Backend.Data.FileAuditStore.KeepPerUser + 5; i++) if (i % 500 == 0) await store.AddAsync(user, null, "H", "x", "/p", "ok");
        Assert.True(await db.ScalarAsync<long>("select count(*) from files_audit where user_id = $1", user) <= JulOS.Backend.Data.FileAuditStore.KeepPerUser);
    }

    [Fact]
    public void Options_default_to_read_only_and_bad_values_never_switch_writing_on()
    {
        var warnings = new List<string>();
        Assert.False(JulOS.Backend.Logic.FilesOptions.Parse(null, null, null).Write);
        Assert.False(JulOS.Backend.Logic.FilesOptions.Parse("", "", "").Write);
        Assert.True(JulOS.Backend.Logic.FilesOptions.Parse(" ON ", null, null).Write);
        Assert.False(JulOS.Backend.Logic.FilesOptions.Parse("true", null, null, warnings.Add).Write);
        Assert.False(JulOS.Backend.Logic.FilesOptions.Parse("1", null, null, warnings.Add).Write);
        Assert.Equal(2, warnings.Count);
        var o = JulOS.Backend.Logic.FilesOptions.Parse("off", "0", "99", warnings.Add);
        Assert.Equal((2048L * 1024 * 1024, 4), (o.MaxBytes, o.MaxSessionsPerUser));
        Assert.Equal(4, warnings.Count);
        var set = JulOS.Backend.Logic.FilesOptions.Parse("on", "100", "2");
        Assert.Equal((100L * 1024 * 1024, 2, TimeSpan.FromSeconds(60)), (set.MaxBytes, set.MaxSessionsPerUser, set.Idle));
    }
}
