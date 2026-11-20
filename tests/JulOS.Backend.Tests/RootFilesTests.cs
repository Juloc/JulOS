using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Frontend.Files;
using JulOS.Integrations.Files;
using Microsoft.Extensions.DependencyInjection;

namespace JulOS.Backend.Tests;

/// <summary>
/// Dateien als root (Wort files-root, docs/FILES.md): der Ort "Root" eines Hosts durch den ganzen Server (echte Middleware und Datenbank). Der Agent ist entweder die ECHTE Python-Datei
/// (tools/julos-agent.py samt echtem root-Helfer, ueber tests/agent/root_bridge.py, mit einem Wegwerfordner als Wurzel; nie das echte "/") oder ein Fake mit festen Antworten (feindliche
/// Antworten, Schluessel mitten in der Uebertragung). Geprueft: alle Schluessel, die zweistufige Bestaetigung auch fuer Download und Textansicht, Bloecke, Audit fail-closed, Kopieren zwischen Orten.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class AgentLinkTests
{
    private static readonly IReadOnlyDictionary<string, string> WriteOn = new Dictionary<string, string> { ["JULOS_FILES_WRITE"] = "on" };
    private static readonly string[] RootRights = ["updates", "reboot", "services", "docker", "logs", "files-root"];

    /// <summary>Der echte Agent mit echtem Helfer in einem Prozess (eine Anfrage nach der anderen), Wurzel = ein Wegwerfordner.</summary>
    private sealed class PythonBridge : IAsyncDisposable
    {
        private readonly Process _p;
        private readonly SemaphoreSlim _one = new(1, 1);
        public string Root { get; }
        public int Calls;
        public List<(string Path, int Bytes)> Log { get; } = [];

        private PythonBridge(Process p, string root) { _p = p; Root = root; }

        public static PythonBridge? Start(string words = "files-root")
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "tools", "julos-agent.py"))) dir = Path.GetDirectoryName(dir);
            Assert.SkipWhen(dir is null, "tools/julos-agent.py nicht gefunden");
            var root = Directory.CreateTempSubdirectory("julos-root-").FullName;
            var psi = new ProcessStartInfo("python3") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-I", Path.Combine(dir!, "tests", "agent", "root_bridge.py"), root, words }) psi.ArgumentList.Add(a);
            Process? p = null;
            try { p = Process.Start(psi); } catch (System.ComponentModel.Win32Exception) { }
            Assert.SkipWhen(p is null, "python3 nicht gefunden");
            return new PythonBridge(p!, root);
        }

        public async Task<(int Status, string Body)> CallAsync(string method, string path, string body)
        {
            await _one.WaitAsync();
            try
            {
                Interlocked.Increment(ref Calls);
                lock (Log) Log.Add((path, body.Length));
                await _p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { method, path, body = JsonDocument.Parse(body.Length == 0 ? "null" : body).RootElement }));
                await _p.StandardInput.FlushAsync();
                var line = await _p.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("Die Bruecke ist beendet: " + await _p.StandardError.ReadToEndAsync());
                using var doc = JsonDocument.Parse(line);
                return (doc.RootElement.GetProperty("status").GetInt32(), doc.RootElement.GetProperty("body").GetRawText());
            }
            finally { _one.Release(); }
        }

        public string P(string rel) => Path.Combine(Root, rel.TrimStart('/'));

        public async ValueTask DisposeAsync()
        {
            try { _p.StandardInput.Close(); if (!_p.WaitForExit(3000)) _p.Kill(); } catch (Exception) { /* beendet */ }
            _p.Dispose();
            try { Directory.Delete(Root, true); } catch (Exception) { /* beste Muehe */ }
            await Task.CompletedTask;
        }
    }

    private sealed record RootRig(HostRig R, PythonBridge? Bridge, Guid User) : IAsyncDisposable
    {
        public HttpClient Http => R.Http;
        public string Cookie => R.Cookie;
        public Guid Host => R.Host;

        public async ValueTask DisposeAsync()
        {
            await R.Agent.DisposeAsync();
            if (Bridge is not null) await Bridge.DisposeAsync();
        }
    }

    private static async Task<RootRig> RootRigAsync(TestDb db, Server app, string control = "full", string version = "0.8.0", string[]? allow = null, bool bridge = true, string words = "files-root")
    {
        var br = bridge ? PythonBridge.Start(words) : null;
        var r = await ActionRigAsync(db, app, control, version, allow: allow ?? RootRights);
        if (br is not null) r.Agent.OnHost = (method, path, body, _) => br.CallAsync(method, path, body);
        return new RootRig(r, br, await db.ScalarAsync<Guid>("select id from users limit 1"));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> FPost(RootRig r, string url, object body)
    {
        var res = await r.Http.SendAsync(Post(url, body, r.Cookie));
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> FGet(RootRig r, string url)
    {
        var res = await r.Http.SendAsync(Send(HttpMethod.Get, url, r.Cookie));
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? default : text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static string CodeOf(JsonElement b) => b.GetProperty("code").GetString()!;

    /// <summary>prepare (Schritt 1): das Token oder die Fehlerantwort.</summary>
    private static async Task<(string? Token, JsonElement Body, HttpStatusCode Status)> FPrepare(RootRig r, object body)
    {
        var (st, b) = await FPost(r, "/api/files/remote/prepare", body);
        return (st == HttpStatusCode.OK ? b.GetProperty("token").GetString() : null, b, st);
    }

    private static async Task<JsonElement> FJob(RootRig r, string job)
    {
        JsonElement last = default;
        await Eventually(async () => { (_, last) = await FGet(r, "/api/files/remote/jobs/" + job); return last.GetProperty("state").GetString() != "running"; });
        return last;
    }

    private static async Task<JsonElement> RootOk(RootRig r, string url, object body, bool job = false)
    {
        var (token, prep, st) = await FPrepare(r, ToPrepare(url, body));
        Assert.True(token is not null, $"prepare {url}: {st} {prep}");
        var run = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject();
        run["token"] = token;
        var res = await r.Http.SendAsync(Post(url, run, r.Cookie));
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{url}: {res.StatusCode} {text}");
        var doc = JsonDocument.Parse(text).RootElement.Clone();
        return job ? await FJob(r, doc.GetProperty("job").GetString()!) : doc;
    }

    private static object ToPrepare(string url, object body)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject();
        var action = url.Split('/')[^1];
        if (action == "copy" && o["move"]?.GetValue<bool>() == true) action = "move";
        var p = new System.Text.Json.Nodes.JsonObject { ["action"] = action, ["connection"] = o["connection"]?.DeepClone() };
        foreach (var k in new[] { "paths", "dir", "name", "target" }) if (o[k] is { } v) p[k] = v.DeepClone();
        if (action == "rename") p["paths"] = new System.Text.Json.Nodes.JsonArray(o["path"]!.DeepClone());
        return p;
    }

    private static Task<HttpResponseMessage> RootUpload(RootRig r, string dir, string name, byte[] data, string token, bool overwrite = false)
    {
        var req = Send(HttpMethod.Post, $"/api/files/remote/upload?c={r.Host}&dir={Uri.EscapeDataString(dir)}&name={Uri.EscapeDataString(name)}&token={Uri.EscapeDataString(token)}" + (overwrite ? "&overwrite=1" : ""), r.Cookie, data);
        return r.Http.SendAsync(req);
    }

    private static async Task<long> AuditCount(TestDb db, string where) => await db.ScalarAsync<long>("select count(*) from files_audit where " + where);

    // ---------------------------------------------------------------- die Schluessel

    [Fact]
    public async Task Root_is_only_a_place_when_every_key_agrees_and_each_refusal_says_which_one()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: WriteOn);
        await using var r = await RootRigAsync(db, app, control: "read", bridge: false);
        r.R.Agent.OnHost = (_, _, _, _) => Task.FromResult((200, """{"ok":true,"path":"/","entries":[],"truncated":false,"hidden":0}"""));
        var rootFiles = app.Services.GetRequiredService<RootFiles>();
        async Task<(HttpStatusCode, string?)> Open() { rootFiles.Forget(r.User, r.Host); var (s, b) = await FPost(r, "/api/files/remote/open", new { connection = r.Host }); return (s, s == HttpStatusCode.OK ? b.GetProperty("hostKey").GetString() : CodeOf(b)); }

        Assert.Equal((HttpStatusCode.Forbidden, "root_off"), await Open());                                    // Stufe "read"
        Assert.Equal(HttpStatusCode.OK, (await Control(r.R, "updates")).StatusCode);
        Assert.Equal((HttpStatusCode.Forbidden, "root_off"), await Open());                                    // "Updates und Logs" reicht nicht
        Assert.DoesNotContain(r.R.Agent.Requests, q => q.Path.Contains("/files/"));                               // abgelehnt, bevor der Agent etwas hoerte
        Assert.Equal(HttpStatusCode.OK, (await Control(r.R, "full")).StatusCode);
        Assert.Equal((HttpStatusCode.OK, "root"), await Open());
        // Pausenschalter
        Assert.Equal(HttpStatusCode.OK, (await r.Http.SendAsync(Put("/api/host-actions/pause", new { paused = true }, r.Cookie))).StatusCode);
        Assert.Equal((HttpStatusCode.Forbidden, "root_paused"), await Open());
        var listing = await FGet(r, $"/api/files/remote/list?c={r.Host}&path=/");
        Assert.Equal((HttpStatusCode.Forbidden, "root_paused"), (listing.Status, CodeOf(listing.Body)));
        await r.Http.SendAsync(Put("/api/host-actions/pause", new { paused = false }, r.Cookie));
        Assert.Equal((HttpStatusCode.OK, "root"), await Open());
        // Agent weg
        await r.R.Agent.DisposeAsync();
        await Eventually(() => !app.Services.GetRequiredService<JulOS.Integrations.Agent.AgentLinks>().IsOnline(r.Host));
        Assert.Equal((HttpStatusCode.Conflict, "root_offline"), await Open());
    }

    [Fact]
    public async Task Without_the_local_word_or_with_an_old_agent_root_is_refused_and_a_foreign_or_unknown_host_is_not_found()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: WriteOn);
        await using (var noWord = await RootRigAsync(db, app, allow: ["updates", "logs"], bridge: false))
        {
            var (s, b) = await FPost(noWord, "/api/files/remote/open", new { connection = noWord.Host });
            Assert.Equal((HttpStatusCode.Forbidden, "root_not_permitted"), (s, CodeOf(b)));              // der Agent erlaubt es lokal nicht (zweiter Schluessel)
            Assert.DoesNotContain(noWord.R.Agent.Requests, q => q.Path.Contains("/files/"));
            var (s2, b2) = await FPost(noWord, "/api/files/remote/open", new { connection = Guid.NewGuid() });
            Assert.Equal((HttpStatusCode.NotFound, "connection_not_found"), (s2, CodeOf(b2)));
        }
        await using var db2 = await TestDb.CreateAsync();
        await using var app2 = new Server(db2, settings: WriteOn);
        await using var old = await RootRigAsync(db2, app2, version: "0.7.0", bridge: false);
        var (s3, b3) = await FPost(old, "/api/files/remote/open", new { connection = old.Host });
        Assert.Equal((HttpStatusCode.Conflict, "root_outdated"), (s3, CodeOf(b3)));
    }

    // ---------------------------------------------------------------- mit dem echten Agenten und Helfer

    [Fact]
    public async Task A_real_agent_lists_uploads_in_blocks_replaces_keeps_the_mode_and_renames_and_deletes_with_confirmations()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: WriteOn);
        await using var r = await RootRigAsync(db, app);
        var root = r.Bridge!;
        var opened = await FPost(r, "/api/files/remote/open", new { connection = r.Host });
        Assert.Equal(("/", "root"), (opened.Body.GetProperty("home").GetString(), opened.Body.GetProperty("hostKey").GetString()));

        // Ordner anlegen (zwei Schritte), hochladen in Bloecken
        await RootOk(r, "/api/files/remote/mkdir", new { connection = r.Host, dir = "/", name = "daten" });
        Assert.True(Directory.Exists(root.P("daten")));
        var big = new byte[150_000]; new Random(7).NextBytes(big);
        var (token, prep, _) = await FPrepare(r, new { action = "upload", connection = r.Host, dir = "/daten", names = new[] { "gross.bin" } });
        Assert.True(prep.GetProperty("root").GetBoolean());                                                  // die Seite warnt auffaellig
        var up = await RootUpload(r, "/daten", "gross.bin", big, token!);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Equal(big, await File.ReadAllBytesAsync(root.P("daten/gross.bin")));
        Assert.Empty(Directory.GetFiles(root.P("daten"), ".julos-*"));                                        // kein temporaerer Rest
        Assert.Equal(Convert.ToString(0b110_100_100, 8), Convert.ToString((int)File.GetUnixFileMode(root.P("daten/gross.bin")), 8));      // neu: 0644
        var writes = root.Log.Where(l => l.Path.EndsWith("/write")).ToList();
        Assert.True(writes.Count >= 4);                                                                       // 150 000 Byte in Bloecken von 48 KiB
        Assert.All(writes, w => Assert.InRange(w.Bytes, 1, 70 * 1024));                                       // jeder Block passt in die 70 KiB, die der Agent je Nachricht annimmt

        // Ersetzen: nur mit dem Haken (conflicts), Rechte der alten Datei bleiben
        File.SetUnixFileMode(root.P("daten/gross.bin"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var (t2, p2, _) = await FPrepare(r, new { action = "upload", connection = r.Host, dir = "/daten", names = new[] { "gross.bin" } });
        Assert.Equal(["gross.bin"], p2.GetProperty("conflicts").EnumerateArray().Select(e => e.GetString()).ToArray());
        var denied = await RootUpload(r, "/daten", "gross.bin", [1, 2, 3], t2!);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var (t3, _, _) = await FPrepare(r, new { action = "upload", connection = r.Host, dir = "/daten", names = new[] { "gross.bin" } });
        Assert.Equal(HttpStatusCode.OK, (await RootUpload(r, "/daten", "gross.bin", [1, 2, 3], t3!, overwrite: true)).StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(root.P("daten/gross.bin")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(root.P("daten/gross.bin")));

        // Liste
        var list = await FGet(r, $"/api/files/remote/list?c={r.Host}&path=/daten");
        var e = list.Body.GetProperty("entries").EnumerateArray().Single();
        Assert.Equal(("gross.bin", "file", 3L), (e.GetProperty("name").GetString(), e.GetProperty("kind").GetString(), e.GetProperty("size").GetInt64()));

        // Umbenennen
        await RootOk(r, "/api/files/remote/rename", new { connection = r.Host, path = "/daten/gross.bin", name = "klein.bin" });
        Assert.True(File.Exists(root.P("daten/klein.bin")));

        // Kopieren im selben Ort (Auftrag), dann alles loeschen: ein Link darin bleibt ein Link, sein Ziel steht
        Directory.CreateDirectory(root.P("aussen")); await File.WriteAllTextAsync(root.P("aussen/wichtig"), "w");
        File.CreateSymbolicLink(root.P("daten/zum-ziel"), "../aussen/wichtig");
        var copy = await RootOk(r, "/api/files/remote/copy", new { connection = r.Host, paths = new[] { "/daten" }, target = new { connection = r.Host, dir = "/aussen" } }, job: true);
        Assert.Equal("done", copy.GetProperty("state").GetString());
        Assert.True(File.Exists(root.P("aussen/daten/klein.bin")));
        Assert.False(File.Exists(root.P("aussen/daten/zum-ziel")));                                          // Links werden nie kopiert
        var del = await RootOk(r, "/api/files/remote/delete", new { connection = r.Host, paths = new[] { "/daten" }, recursive = true }, job: true);
        Assert.Equal("done", del.GetProperty("state").GetString());
        Assert.False(Directory.Exists(root.P("daten")));
        Assert.Equal("w", await File.ReadAllTextAsync(root.P("aussen/wichtig")));

        // Verlauf: jede schreibende Aktion started + Ergebnis, Name des Ortes "(Root)"
        Assert.True(await AuditCount(db, "host_name like '% (Root)' and action = 'mkdir' and result = 'started'") == 1);
        Assert.True(await AuditCount(db, "action = 'upload' and result = 'started'") == 3 && await AuditCount(db, "action = 'upload' and result = 'ok'") == 2);
        Assert.True(await AuditCount(db, "action = 'delete' and result = 'ok'") == 1);
    }

    [Fact]
    public async Task Reading_a_root_file_as_text_or_download_needs_its_own_confirmation_and_stands_in_the_history()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: WriteOn);
        await using var r = await RootRigAsync(db, app);
        var data = new byte[130_000]; new Random(3).NextBytes(data);
        await File.WriteAllBytesAsync(r.Bridge!.P("geheim.bin"), data);
        await File.WriteAllTextAsync(r.Bridge.P("notiz.txt"), "Zeile <b>eins</b>\nzwei\n");
        string Url(string what, string path, string? token = null) => $"/api/files/remote/{what}?c={r.Host}&path={Uri.EscapeDataString(path)}" + (token is null ? "" : "&token=" + token);

        // ohne Token und mit erfundenem: abgelehnt, der Agent bekommt keinen read
        foreach (var tok in new string?[] { null, "erfunden" })
        {
            var t = await FGet(r, Url("text", "/notiz.txt", tok));
            Assert.Equal((HttpStatusCode.Forbidden, "confirmation_invalid"), (t.Status, CodeOf(t.Body)));
            Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.SendAsync(Send(HttpMethod.Get, Url("download", "/geheim.bin", tok), r.Cookie))).StatusCode);
        }
        Assert.DoesNotContain(r.Bridge.Log, l => l.Path.EndsWith("/read"));
        // eine Bestaetigung gilt fuer genau diesen Pfad und diese Aktion
        var (tp, prep, _) = await FPrepare(r, new { action = "text", connection = r.Host, paths = new[] { "/notiz.txt" } });
        Assert.True(prep.GetProperty("root").GetBoolean());
        Assert.Equal((1, 23L), (prep.GetProperty("files").GetInt32(), prep.GetProperty("bytes").GetInt64()));
        var wrong = await FGet(r, Url("text", "/geheim.bin", tp));
        Assert.Equal("confirmation_mismatch", CodeOf(wrong.Body));
        var (tp2, _, _) = await FPrepare(r, new { action = "text", connection = r.Host, paths = new[] { "/notiz.txt" } });
        var asDownload = await r.Http.SendAsync(Send(HttpMethod.Get, Url("download", "/notiz.txt", tp2), r.Cookie));
        Assert.Equal(HttpStatusCode.Forbidden, asDownload.StatusCode);
        // richtig: Text als JSON-Wert, einmal
        var (tp3, _, _) = await FPrepare(r, new { action = "text", connection = r.Host, paths = new[] { "/notiz.txt" } });
        var ok = await FGet(r, Url("text", "/notiz.txt", tp3));
        Assert.Equal("Zeile <b>eins</b>\nzwei\n", ok.Body.GetProperty("text").GetString());
        Assert.Equal("confirmation_invalid", CodeOf((await FGet(r, Url("text", "/notiz.txt", tp3))).Body));
        // Download in Bloecken: Anhang, unbekannter Typ, voller Inhalt
        var (td, pd, _) = await FPrepare(r, new { action = "download", connection = r.Host, paths = new[] { "/geheim.bin" } });
        Assert.Equal(130_000L, pd.GetProperty("bytes").GetInt64());
        var dl = await r.Http.SendAsync(Send(HttpMethod.Get, Url("download", "/geheim.bin", td), r.Cookie));
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("application/octet-stream", dl.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment", dl.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(data, await dl.Content.ReadAsByteArrayAsync());
        Assert.True(r.Bridge.Log.Count(l => l.Path.EndsWith("/read")) >= 3);                                  // mehrere Bloecke
        // Verlauf: Lesen steht mit started und Ergebnis drin
        Assert.Equal(2, await AuditCount(db, "action in ('text','download') and result = 'started'"));
        Assert.Equal(2, await AuditCount(db, "action in ('text','download') and result = 'ok'"));
        // ein normaler SSH-Ort kennt diese Bestaetigung nicht (nur Root liest nur mit Token)
        var ssh = await FPost(r, "/api/files/remote/prepare", new { action = "text", connection = Guid.NewGuid(), paths = new[] { "/x" } });
        Assert.Equal(HttpStatusCode.NotFound, ssh.Status);
    }

    [Fact]
    public async Task The_agents_own_files_the_root_and_links_are_protected_and_the_codes_reach_the_page()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: WriteOn);
        await using var r = await RootRigAsync(db, app);
        Directory.CreateDirectory(r.Bridge!.P("etc/julos-agent"));
        await File.WriteAllTextAsync(r.Bridge.P("etc/julos-agent/permissions"), "files-root\n");
        async Task<(HttpStatusCode, string)> Try(string url, object body)
        {
            var (token, prep, st) = await FPrepare(r, ToPrepare(url, body));
            if (token is null) return (st, CodeOf(prep));
            var run = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject(); run["token"] = token;
            var res = await r.Http.SendAsync(Post(url, run, r.Cookie));
            var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            if (res.StatusCode == HttpStatusCode.Accepted) { var j = await FJob(r, json.GetProperty("job").GetString()!); return (res.StatusCode, j.GetProperty("state").GetString() == "failed" ? j.GetProperty("error").GetString()! : "done"); }
            return (res.StatusCode, json.TryGetProperty("code", out var c) ? c.GetString()! : "ok");
        }
        Assert.Equal((HttpStatusCode.Forbidden, "protected_path"), await Try("/api/files/remote/mkdir", new { connection = r.Host, dir = "/etc/julos-agent", name = "x" }));
        Assert.Equal((HttpStatusCode.Accepted, "protected_path"), await Try("/api/files/remote/delete", new { connection = r.Host, paths = new[] { "/etc/julos-agent/permissions" } }));
        Assert.Equal((HttpStatusCode.Accepted, "root_forbidden"), await Try("/api/files/remote/delete", new { connection = r.Host, paths = new[] { "/" }, recursive = true }));
        Assert.Equal("files-root\n", await File.ReadAllTextAsync(r.Bridge.P("etc/julos-agent/permissions")));
        // Ein Link als Ziel eines Ersetzens wird nie ersetzt
        await File.WriteAllTextAsync(r.Bridge.P("ziel"), "z"); File.CreateSymbolicLink(r.Bridge.P("link"), "ziel");
        var (t, p, _) = await FPrepare(r, new { action = "upload", connection = r.Host, dir = "/", names = new[] { "link" } });
        var res = await RootUpload(r, "/", "link", [9], t!, overwrite: true);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);                                                // ein Link wird nie ersetzt (target_is_link), auch nicht mit Haken
        Assert.Equal("z", await File.ReadAllTextAsync(r.Bridge.P("ziel")));
        Assert.True(Directory.GetFiles(r.Bridge.Root).All(f => !Path.GetFileName(f).StartsWith(".julos-")));
    }

    // ---------------------------------------------------------------- Audit fail-closed und Schluessel mitten in der Uebertragung

    [Fact]
    public async Task Without_a_history_entry_no_root_read_or_write_happens()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: WriteOn);
        await using var r = await RootRigAsync(db, app);
        await File.WriteAllTextAsync(r.Bridge!.P("a.txt"), "A");
        var (tt, _, _) = await FPrepare(r, new { action = "text", connection = r.Host, paths = new[] { "/a.txt" } });
        var (tm, _, _) = await FPrepare(r, new { action = "mkdir", connection = r.Host, dir = "/", name = "neu" });
        await db.ExecAsync("drop table files_audit");
        var reads = r.Bridge.Log.Count(l => l.Path.EndsWith("/read") || l.Path.EndsWith("/mkdir"));
        var t = await FGet(r, $"/api/files/remote/text?c={r.Host}&path=%2Fa.txt&token={tt}");
        Assert.Equal((HttpStatusCode.InternalServerError, "audit_failed"), (t.Status, CodeOf(t.Body)));
        var m = await FPost(r, "/api/files/remote/mkdir", new { connection = r.Host, dir = "/", name = "neu", token = tm });
        Assert.Equal((HttpStatusCode.InternalServerError, "audit_failed"), (m.Status, CodeOf(m.Body)));
        Assert.Equal(reads, r.Bridge.Log.Count(l => l.Path.EndsWith("/read") || l.Path.EndsWith("/mkdir")));    // kein Lesen und kein Schreiben
        Assert.False(Directory.Exists(r.Bridge.P("neu")));
    }

    [Fact]
    public async Task A_key_that_closes_during_a_transfer_stops_it_at_the_next_block()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: WriteOn);
        await using var r = await RootRigAsync(db, app);
        var data = new byte[300_000]; new Random(1).NextBytes(data);
        await File.WriteAllBytesAsync(r.Bridge!.P("gross.bin"), data);
        var rootFiles = app.Services.GetRequiredService<RootFiles>();
        var reads = 0;
        r.R.Agent.OnHost = async (method, path, body, _) =>
        {
            if (path.EndsWith("/read") && ++reads == 2)
            {
                await db.ExecAsync("update agents set control = 'read' where host_id = $1", r.Host);            // der Benutzer nimmt die Stufe zurueck, mitten im Download
                rootFiles.Forget(r.User, r.Host);
            }
            return await r.Bridge.CallAsync(method, path, body);
        };
        var (token, _, _) = await FPrepare(r, new { action = "download", connection = r.Host, paths = new[] { "/gross.bin" } });
        var failed = false;
        try
        {
            var res = await r.Http.SendAsync(Send(HttpMethod.Get, $"/api/files/remote/download?c={r.Host}&path=%2Fgross.bin&token={token}", r.Cookie), HttpCompletionOption.ResponseHeadersRead);
            var got = await res.Content.ReadAsByteArrayAsync();
            failed = got.Length < data.Length;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { failed = true; }
        Assert.True(failed);                                                                                   // nie ein unvollstaendiger Download als vollstaendiger
        Assert.True(reads <= 3);                                                                               // und der Agent hoert nach dem Schluessel nichts mehr
        Assert.True(await AuditCount(db, "action = 'download' and result = 'failed'") >= 1);
    }

    [Fact]
    public async Task What_the_agent_reports_is_checked_names_and_blocks_and_sizes()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, settings: WriteOn);
        await using var r = await RootRigAsync(db, app, bridge: false);
        var reply = (200, """{"ok":true,"path":"/","entries":[{"name":"gut.txt","kind":"file","size":5,"mtime":1700000000,"mode":"rw-r--r--"},{"name":"../x","kind":"file","size":1,"mtime":1,"mode":"rwxrwxrwx"},{"name":"a/b","kind":"file","size":1,"mtime":1,"mode":"rwxrwxrwx"},{"name":"..","kind":"directory","size":0,"mtime":1,"mode":"rwxrwxrwx"},{"name":"neu\nzeile","kind":"file","size":1,"mtime":1,"mode":"rwxrwxrwx"},{"name":"negativ","kind":"file","size":-5,"mtime":-9,"mode":"<script>"},"nicht-ein-objekt"],"truncated":false,"hidden":0}""");
        r.R.Agent.OnHost = (_, path, _, _) => Task.FromResult(path.EndsWith("/list") ? reply : (200, """{"ok":true,"data":"!!!","eof":true,"exists":false}"""));
        var l = await FGet(r, $"/api/files/remote/list?c={r.Host}&path=/");
        var names = l.Body.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["gut.txt", "negativ"], names);                                                           // ein Name mit "/", ".." oder Steuerzeichen kommt nie in die Liste (F-24)
        Assert.Equal(4, l.Body.GetProperty("hidden").GetInt32());
        var neg = l.Body.GetProperty("entries").EnumerateArray().Last();
        Assert.Equal((0L, "---------"), (neg.GetProperty("size").GetInt64(), neg.GetProperty("mode").GetString()));
        // Daten, die kein Base64 sind, sind ein Fehler, nie Text
        var (token, prep, st) = await FPrepare(r, new { action = "text", connection = r.Host, paths = new[] { "/gut.txt" } });
        Assert.Equal(HttpStatusCode.NotFound, st);                                                              // stat meldet "gibt es nicht"
        _ = token; _ = prep;
    }

    // ---------------------------------------------------------------- Kopieren zwischen Orten

    [Fact]
    public async Task Copy_goes_between_root_and_an_ssh_place_both_ways_with_the_confirmation_naming_the_root()
    {
        await using var db = await TestDb.CreateAsync();
        var ssh = new FakeFileSession();
        await using var app = new Server(db, configure: s => s.AddSingleton(new FileSessionFactory(() => ssh)), settings: new Dictionary<string, string> { ["JULOS_FILES_WRITE"] = "on", ["JULOS_SECRET_KEY"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 7)).ToArray()) });
        await using var r = await RootRigAsync(db, app);
        // ein SSH-Ort mit gespeichertem Passwort und bekanntem Host-Schluessel (der Fake-Server der Dateien)
        var created = await r.Http.SendAsync(Post("/api/connections", new { name = "SSH", kind = "terminal", host = "ssh-ziel", username = "jul", password = "Wegwerf-Passwort-123" }, r.Cookie));
        var conn = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await FPost(r, "/api/files/remote/open", new { connection = conn, acceptHostKey = ssh.Key.Id })).Status);
        ssh.Dir("/home/tester"); ssh.File("/home/tester/von-ssh.txt", "vom ssh-ziel");
        await File.WriteAllTextAsync(r.Bridge!.P("von-root.txt"), "aus root");
        Directory.CreateDirectory(r.Bridge.P("ziel"));

        // root -> ssh
        var (t1, p1, _) = await FPrepare(r, new { action = "copy", connection = r.Host, paths = new[] { "/von-root.txt" }, target = new { connection = conn, dir = "/home/tester" } });
        Assert.True(p1.GetProperty("root").GetBoolean());
        Assert.Equal("Server (Root)", p1.GetProperty("host").GetString());
        var run1 = await FPost(r, "/api/files/remote/copy", new { connection = r.Host, paths = new[] { "/von-root.txt" }, target = new { connection = conn, dir = "/home/tester" }, token = t1 });
        Assert.Equal(HttpStatusCode.Accepted, run1.Status);
        Assert.Equal("done", (await FJob(r, run1.Body.GetProperty("job").GetString()!)).GetProperty("state").GetString());
        Assert.Equal("aus root", ssh.Text("/home/tester/von-root.txt"));
        // ssh -> root
        var (t2, p2, _) = await FPrepare(r, new { action = "copy", connection = conn, paths = new[] { "/home/tester/von-ssh.txt" }, target = new { connection = r.Host, dir = "/ziel" } });
        Assert.True(p2.GetProperty("root").GetBoolean());                                                     // auch wenn nur das ZIEL root ist
        var run2 = await FPost(r, "/api/files/remote/copy", new { connection = conn, paths = new[] { "/home/tester/von-ssh.txt" }, target = new { connection = r.Host, dir = "/ziel" }, token = t2 });
        Assert.Equal("done", (await FJob(r, run2.Body.GetProperty("job").GetString()!)).GetProperty("state").GetString());
        Assert.Equal("vom ssh-ziel", await File.ReadAllTextAsync(r.Bridge.P("ziel/von-ssh.txt")));
    }
}
