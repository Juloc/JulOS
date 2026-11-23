using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JulOS.Frontend.Ai;
using JulOS.Integrations.Ai;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>
/// Der KI-Zugang durch die echte Middleware und die Datenbank, mit dem Fake-Anbieter (nie ein echter Anbieter, kein Netz): Zugang nur mit gueltigem, aktivem Schluessel, Budget, Ratenlimit,
/// Modell-Erlaubnis, kein Inhalt in der Datenbank, Schluessel nie in Antworten, Abbruch, Verwaltung nur mit Sitzung und Freigabe.
/// </summary>
public sealed class AiGatewayTests
{
    private const string Secret = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string Forty = "0123456789012345678901234567890123456789";      // 40 Zeichen: der Fake zaehlt 10 Tokens rein und 12 raus

    private sealed class Env : IAsyncDisposable
    {
        public required TestDb Db; public required Server Server; public required WebApplicationFactory<Program> Factory; public required HttpClient Http; public required string Cookie; public required List<string> Log;
        public FakeAiProvider Fake => (FakeAiProvider)Factory.Services.GetRequiredService<AiRegistry>().Get("fake")!;

        public async ValueTask DisposeAsync() { Http.Dispose(); await Factory.DisposeAsync(); await Server.DisposeAsync(); await Db.DisposeAsync(); }

        public Task<HttpResponseMessage> Admin(HttpMethod method, string url, object? body = null) => Http.SendAsync(Req(method, url, Cookie, body), TestContext.Current.CancellationToken);

        public async Task<(Guid Id, string Key)> NewApp(string name = "Notizen", string[]? allowed = null, long? daily = null, long? monthly = null, int rate = 60, bool files = false)
        {
            var r = await Admin(HttpMethod.Post, "/api/ai/apps", new { name, allowed = allowed ?? ["fake"], dailyTokens = daily, monthlyTokens = monthly, ratePerMinute = rate, allowFiles = files });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            var j = await Json(r);
            return (j.GetProperty("app").GetProperty("id").GetGuid(), j.GetProperty("key").GetString()!);
        }

        public Task<HttpResponseMessage> Call(HttpMethod method, string url, string? key, object? body = null, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead, CancellationToken ct = default)
        {
            // so ruft eine App: nur der Schluessel, kein Cookie, keine Sonderkopfzeile, kein Origin
            var req = new HttpRequestMessage(method, url);
            if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (body is not null) req.Content = JsonContent.Create(body);
            return Http.SendAsync(req, option, ct == default ? TestContext.Current.CancellationToken : ct);
        }

        public Task<HttpResponseMessage> Chat(string? key, string text, string model = "fake/echo", bool stream = false, object? extra = null) =>
            Call(HttpMethod.Post, "/api/ai/v1/chat", key, new { model, messages = new[] { new { role = "user", content = text } }, stream, extra });

        public async Task<string> DumpAsync()
        {
            var sb = new StringBuilder();
            await using var tables = Db.Data.CreateCommand("select table_name from information_schema.tables where table_schema = current_schema()");
            var names = new List<string>();
            await using (var r = await tables.ExecuteReaderAsync(TestContext.Current.CancellationToken)) while (await r.ReadAsync(TestContext.Current.CancellationToken)) names.Add(r.GetString(0));
            foreach (var n in names)
            {
                await using var cmd = Db.Data.CreateCommand($"select row_to_json(t)::text from \"{n}\" t");
                await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await r.ReadAsync(TestContext.Current.CancellationToken)) sb.AppendLine(n + ": " + r.GetString(0));
            }
            return sb.ToString();
        }
    }

    private static async Task<Env> Start(bool enabled = true)
    {
        var db = await TestDb.CreateAsync();
        var log = new List<string>();
        var server = new Server(db, log: log, secretKey: Secret);
        var factory = server.WithWebHostBuilder(b => b.UseSetting("JULOS_AI_FAKE", "on"));
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (cookie, _) = await SignedInAsync(http, db);
        var env = new Env { Db = db, Server = server, Factory = factory, Http = http, Cookie = cookie, Log = log };
        if (enabled) Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Put, "/api/ai/enabled", new { enabled = true })).StatusCode);
        return env;
    }

    private static async Task<string> Code(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task Access_needs_a_valid_active_key_and_a_key_opens_nothing_else_in_JulOS()
    {
        await using var env = await Start();
        var (id, key) = await env.NewApp();

        Assert.Equal("missing_key", await Code(await env.Chat(null, "hi")));
        Assert.Equal("invalid_key", await Code(await env.Chat("jos_ai_falsch", "hi")));
        var basic = new HttpRequestMessage(HttpMethod.Post, "/api/ai/v1/chat") { Content = JsonContent.Create(new { model = "fake/echo", messages = new[] { new { role = "user", content = "hi" } } }) };
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await env.Http.SendAsync(basic, TestContext.Current.CancellationToken)).StatusCode);
        // eine Sitzung (Cookie) ist hier kein Zugang
        var cookieOnly = new HttpRequestMessage(HttpMethod.Post, "/api/ai/v1/chat") { Content = JsonContent.Create(new { model = "fake/echo", messages = new[] { new { role = "user", content = "hi" } } }) };
        cookieOnly.Headers.Add("Cookie", env.Cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await env.Http.SendAsync(cookieOnly, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(0, env.Fake.Calls);

        var ok = await env.Chat(key, "hi");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("echo: hi", (await Json(ok)).GetProperty("text").GetString());

        // der Schluessel gibt Zugang zu den drei Wegen und zu sonst nichts: nicht zu den Einstellungen, nicht zu Hosts, nicht zu Aenderungen (auch nicht mit den Kopfzeilen der Seite)
        foreach (var url in new[] { "/api/ai", "/api/hosts", "/api/connections", "/api/security", "/api/auth/sessions" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await env.Call(HttpMethod.Get, url, key)).StatusCode);
        var write = new HttpRequestMessage(HttpMethod.Post, "/api/ai/apps") { Content = JsonContent.Create(new { name = "Eigene", allowed = new[] { "fake" } }) };
        write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); write.Headers.Add("X-JulOS", "1"); write.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, (await env.Http.SendAsync(write, TestContext.Current.CancellationToken)).StatusCode);

        // gesperrt: sofort kein Zugang; wieder frei: geht; neuer Schluessel: der alte gilt nicht mehr
        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Put, $"/api/ai/apps/{id}/active", new { active = false })).StatusCode);
        Assert.Equal("app_disabled", await Code(await env.Chat(key, "hi")));
        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Put, $"/api/ai/apps/{id}/active", new { active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await env.Chat(key, "hi")).StatusCode);
        var fresh = (await Json(await env.Admin(HttpMethod.Post, $"/api/ai/apps/{id}/key"))).GetProperty("key").GetString()!;
        Assert.Equal("invalid_key", await Code(await env.Chat(key, "hi")));
        Assert.Equal(HttpStatusCode.OK, (await env.Chat(fresh, "hi")).StatusCode);

        // gestrichene App: der Schluessel ist ungueltig
        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Delete, $"/api/ai/apps/{id}")).StatusCode);
        Assert.Equal("invalid_key", await Code(await env.Chat(fresh, "hi")));
    }

    [Fact]
    public async Task Wrong_keys_are_rate_limited_and_the_block_holds_even_for_the_right_key()
    {
        await using var env = await Start();
        var (_, key) = await env.NewApp();
        for (var i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await env.Chat("jos_ai_falsch" + i, "hi")).StatusCode);
        var blocked = await env.Chat(key, "hi");                     // jetzt auch der richtige Schluessel: erst warten
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("rate_limited", await Code(blocked));
        Assert.True(blocked.Headers.Contains("Retry-After"));
        Assert.Equal(0, env.Fake.Calls);
    }

    [Fact]
    public async Task The_json_context_of_AI_is_registered_because_the_real_server_runs_without_reflection()
    {
        await using var env = await Start(enabled: false);
        var options = env.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        Assert.Contains(AiJson.Default, options.TypeInfoResolverChain);
    }

    [Fact]
    public async Task With_AI_off_nothing_is_called_even_with_a_valid_key()
    {
        await using var env = await Start(enabled: false);          // aus ist der Anfang
        var (_, key) = await env.NewApp();
        var chat = await env.Chat(key, "hi");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, chat.StatusCode); Assert.Equal("ai_disabled", await Code(chat));
        Assert.Equal("ai_disabled", await Code(await env.Call(HttpMethod.Get, "/api/ai/v1/models", key)));
        Assert.Equal(0, env.Fake.Calls);

        var state = await Json(await env.Admin(HttpMethod.Get, "/api/ai"));
        Assert.False(state.GetProperty("enabled").GetBoolean());
        Assert.Equal("off", state.GetProperty("providers").EnumerateArray().First(p => p.GetProperty("id").GetString() == "codex").GetProperty("status").GetString());
        Assert.False(env.Factory.Services.GetRequiredService<AiRegistry>().Installer.IsDownloading);       // nichts wird geladen
        Assert.False(env.Factory.Services.GetRequiredService<AiRegistry>().Codex.Running);
    }

    [Fact]
    public async Task A_chat_works_as_json_and_as_a_stream_counts_tokens_and_stores_no_content()
    {
        await using var env = await Start();
        var (id, key) = await env.NewApp(daily: 1000);
        var marker = "GEHEIMER-INHALT-" + Guid.NewGuid().ToString("N");

        var json = await Json(await env.Chat(key, marker));
        Assert.Equal("echo: " + marker, json.GetProperty("text").GetString());
        Assert.Equal("fake/echo", json.GetProperty("model").GetString());
        Assert.True(json.GetProperty("usage").GetProperty("inputTokens").GetInt64() > 0);
        Assert.Equal(1000, json.GetProperty("budget").GetProperty("day").GetProperty("limit").GetInt64());

        var sse = await env.Chat(key, marker, stream: true);
        Assert.Equal("text/event-stream", sse.Content.Headers.ContentType!.MediaType);
        var body = await sse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var events = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(b => b.Split('\n')).Select(l => (Event: l[0][7..], Data: JsonDocument.Parse(l[1][6..]).RootElement)).ToList();
        Assert.Equal("start", events[0].Event); Assert.Equal("done", events[^1].Event);
        Assert.Equal("echo: " + marker, string.Concat(events.Where(e => e.Event == "text").Select(e => e.Data.GetProperty("text").GetString())));
        Assert.True(events.Count(e => e.Event == "text") >= 2);                                   // in Stuecken
        Assert.True(events[^1].Data.GetProperty("usage").GetProperty("outputTokens").GetInt64() > 0);

        var usage = await Json(await env.Call(HttpMethod.Get, "/api/ai/v1/usage", key));
        Assert.Equal("Notizen", usage.GetProperty("app").GetString());
        Assert.Equal(2, usage.GetProperty("recent")[0].GetProperty("requests").GetInt32());
        Assert.True(usage.GetProperty("budget").GetProperty("day").GetProperty("used").GetInt64() > 0);

        // gespeichert sind nur Zaehler: der Text steht nirgends in der Datenbank, nicht im Verlauf und nicht im Log
        var dump = await env.DumpAsync();
        Assert.DoesNotContain(marker, dump); Assert.DoesNotContain(marker, string.Join('\n', env.Log));
        Assert.Contains("ai_usage:", dump); Assert.DoesNotContain(key, dump); Assert.DoesNotContain(key, string.Join('\n', env.Log));
        Assert.Equal(2, await env.Db.ScalarAsync<int>("select sum(requests)::int from ai_usage where app_id = $1", id));
        Assert.Equal(2, await env.Db.ScalarAsync<int>("select sum(ok)::int from ai_usage"));

        // Fehler des Anbieters: ein richtiger HTTP-Fehler mit festem Code, gebucht als failed
        var failed = await env.Chat(key, "FAIL");
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode); Assert.Equal("provider_error", await Code(failed));
        Assert.Equal(1, await env.Db.ScalarAsync<int>("select sum(failed)::int from ai_usage"));
    }

    [Fact]
    public async Task Models_follow_the_permissions_of_the_app_and_a_refused_call_never_reaches_the_provider()
    {
        await using var env = await Start();
        var (_, key) = await env.NewApp(allowed: ["fake/echo"]);

        var models = await Json(await env.Call(HttpMethod.Get, "/api/ai/v1/models", key));
        Assert.Equal(["fake/echo"], models.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToArray());

        var denied = await env.Chat(key, "hi", model: "fake/echo-big");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal("model_not_allowed", await Code(denied));
        Assert.Equal("model_not_allowed", await Code(await env.Chat(key, "hi", model: "openai/gpt-4.1")));
        Assert.Equal("no_default_model", await Code(await env.Chat(key, "hi", model: "default")));       // keins eingestellt
        Assert.Equal("bad_request", await Code(await env.Chat(key, "hi", model: "ohne-anbieter")));
        Assert.Equal(0, env.Fake.Calls);

        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Put, "/api/ai/default-model", new { model = "fake/echo" })).StatusCode);
        var viaDefault = await Json(await env.Chat(key, "hi", model: "default"));
        Assert.Equal("fake/echo", viaDefault.GetProperty("model").GetString());
        Assert.Equal("fake/echo", (await Json(await env.Call(HttpMethod.Get, "/api/ai/v1/models", key))).GetProperty("default").GetString());
        Assert.Equal(1, env.Fake.Calls);

        // Eingaben werden geprueft, bevor der Anbieter etwas sieht
        async Task<string> Bad(object body) => await Code(await env.Call(HttpMethod.Post, "/api/ai/v1/chat", key, body));
        Assert.Equal("bad_request", await Bad(new { model = "fake/echo", messages = Array.Empty<object>() }));
        Assert.Equal("bad_request", await Bad(new { model = "fake/echo", messages = new[] { new { role = "system", content = "x" } } }));
        Assert.Equal("bad_request", await Bad(new { model = "fake/echo", messages = new[] { new { role = "root", content = "x" } } }));
        Assert.Equal("bad_request", await Bad(new { model = "fake/echo", effort = "extreme", messages = new[] { new { role = "user", content = "x" } } }));
        Assert.Equal("bad_request", await Bad(new { model = "fake/echo", maxTokens = 0, messages = new[] { new { role = "user", content = "x" } } }));
        Assert.Equal(1, env.Fake.Calls);
    }

    [Fact]
    public async Task The_budget_warns_at_80_percent_and_refuses_at_100_before_the_provider_is_called()
    {
        await using var env = await Start();
        var (_, key) = await env.NewApp(daily: 100);                           // jede Anfrage kostet 22 Tokens
        for (var i = 1; i <= 3; i++)
        {
            var r = await Json(await env.Chat(key, Forty));
            Assert.False(r.TryGetProperty("warning", out _), $"Anfrage {i}: noch kein Hinweis");
        }
        var fourth = await env.Chat(key, Forty);                               // 88 von 100
        Assert.Equal("day", (await Json(fourth)).GetProperty("warning").GetString());
        var fifth = await env.Chat(key, Forty);                                // erlaubt (88 < 100), der Hinweis steht schon in der Kopfzeile
        Assert.Equal("day", fifth.Headers.GetValues("X-AI-Budget-Warning").Single());
        Assert.Equal(5, env.Fake.Calls);

        var refused = await env.Chat(key, Forty);                              // 110 von 100
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var error = (await Json(refused)).GetProperty("error");
        Assert.Equal("budget_exceeded", error.GetProperty("code").GetString()); Assert.Equal("day", error.GetProperty("scope").GetString()); Assert.True(error.TryGetProperty("resetsAt", out _));
        Assert.Equal(5, env.Fake.Calls);                                       // der Anbieter wurde nicht mehr gerufen
        var usage = await Json(await env.Call(HttpMethod.Get, "/api/ai/v1/usage", key));
        Assert.Equal("day", usage.GetProperty("warning").GetString());            // das Feld nennt, welche Grenze erreicht ist

        // ein Monatsbudget stoppt ebenso, und es gilt je App
        var (_, other) = await env.NewApp("Andere", monthly: 20);
        Assert.Equal(HttpStatusCode.OK, (await env.Chat(other, Forty)).StatusCode);
        var monthly = await env.Chat(other, Forty);
        Assert.Equal("month", (await Json(monthly)).GetProperty("error").GetProperty("scope").GetString());
    }

    [Fact]
    public async Task The_rate_limit_and_the_number_of_parallel_calls_are_limited_per_app_and_aborting_frees_the_slot()
    {
        await using var env = await Start();
        var (_, limited) = await env.NewApp("Schnell", rate: 2);
        Assert.Equal(HttpStatusCode.OK, (await env.Chat(limited, "a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await env.Chat(limited, "b")).StatusCode);
        var third = await env.Chat(limited, "c");
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode); Assert.Equal("rate_limited", await Code(third));
        var (_, free) = await env.NewApp("Frei", rate: 60);
        Assert.Equal(HttpStatusCode.OK, (await env.Chat(free, "a")).StatusCode);                  // eine andere App ist nicht betroffen

        // zwei Anfragen, die haengen (der Fake wartet), belegen die beiden Plaetze der App
        var (id, key) = await env.NewApp("Langsam", rate: 60);
        using var cts1 = new CancellationTokenSource(); using var cts2 = new CancellationTokenSource();
        var slow = new object[] { new { model = "fake/echo", messages = new[] { new { role = "user", content = "SLOW" } }, stream = true } };
        var r1 = await env.Call(HttpMethod.Post, "/api/ai/v1/chat", key, slow[0], HttpCompletionOption.ResponseHeadersRead, cts1.Token);
        var r2 = await env.Call(HttpMethod.Post, "/api/ai/v1/chat", key, slow[0], HttpCompletionOption.ResponseHeadersRead, cts2.Token);
        var busy = await env.Chat(key, "hi");
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode); Assert.Equal("too_many_requests", await Code(busy));

        // der Aufrufer bricht ab: der Anbieter-Aufruf wird abgebrochen, der Platz wird frei, es wird als abgebrochen gebucht
        r1.Dispose(); r2.Dispose(); cts1.Cancel(); cts2.Cancel();
        for (var i = 0; i < 100 && env.Fake.Aborted < 2; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, env.Fake.Aborted);
        for (var i = 0; i < 100 && await env.Db.ScalarAsync<long>("select coalesce(sum(aborted), 0) from ai_usage where app_id = $1", id) < 2; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, await env.Db.ScalarAsync<long>("select sum(aborted) from ai_usage where app_id = $1", id));
        Assert.Equal(HttpStatusCode.OK, (await env.Chat(key, "hi")).StatusCode);
    }

    [Fact]
    public async Task Images_need_the_permission_of_the_app_and_a_request_has_a_size_limit()
    {
        await using var env = await Start();
        var (_, plain) = await env.NewApp("Ohne");
        var (_, withFiles) = await env.NewApp("Mit", files: true);
        var image = new { model = "fake/echo", messages = new[] { new { role = "user", content = new object[] { new { type = "text", text = "Was ist das?" }, new { type = "image", mediaType = "image/png", data = "AAAA" } } } } };
        var no = await env.Call(HttpMethod.Post, "/api/ai/v1/chat", plain, image);
        Assert.Equal(HttpStatusCode.Forbidden, no.StatusCode); Assert.Equal("files_not_allowed", await Code(no));
        Assert.Equal(0, env.Fake.Calls);
        Assert.Equal(HttpStatusCode.OK, (await env.Call(HttpMethod.Post, "/api/ai/v1/chat", withFiles, image)).StatusCode);
        var badType = new { model = "fake/echo", messages = new[] { new { role = "user", content = new object[] { new { type = "image", mediaType = "text/html", data = "AAAA" } } } } };
        Assert.Equal("bad_request", await Code(await env.Call(HttpMethod.Post, "/api/ai/v1/chat", withFiles, badType)));

        var huge = await env.Chat(plain, new string('x', 400 * 1024));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode); Assert.Equal("request_too_large", await Code(huge));
        Assert.Equal(1, env.Fake.Calls);
    }

    [Fact]
    public async Task Keys_are_never_in_an_answer_provider_keys_are_stored_sealed_and_every_change_is_audited_without_a_secret()
    {
        await using var env = await Start(enabled: false);              // aus: der echte Anbieter wird nie angesprochen
        const string providerKey = "sk-test-NIEMALS-ECHT-0123456789abcdef";
        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Put, "/api/ai/providers/openai", new { key = providerKey })).StatusCode);
        var (_, appKey) = await env.NewApp("Notizen");

        var all = await (await env.Admin(HttpMethod.Get, "/api/ai")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(providerKey, all); Assert.DoesNotContain(appKey, all); Assert.DoesNotContain("sk-test", all);
        var state = JsonDocument.Parse(all).RootElement;
        Assert.True(state.GetProperty("providers").EnumerateArray().First(p => p.GetProperty("id").GetString() == "openai").GetProperty("keySet").GetBoolean());
        Assert.Equal(4, state.GetProperty("apps")[0].GetProperty("keyHint").GetString()!.Length);           // nur ein Hinweis zum Wiedererkennen

        var dump = await env.DumpAsync();
        Assert.DoesNotContain(providerKey, dump); Assert.DoesNotContain("NIEMALS-ECHT", dump);          // verschluesselt, nicht im Klartext
        Assert.DoesNotContain(appKey, dump);                                                           // von der App nur der Hash
        Assert.Contains("ai.app.create", dump); Assert.Contains("ai.key.set", dump);
        Assert.DoesNotContain(providerKey, string.Join('\n', env.Log)); Assert.DoesNotContain(appKey, string.Join('\n', env.Log));
        Assert.Equal(1, await env.Db.ScalarAsync<int>("select count(*)::int from ai_providers where id = 'openai' and sealed_key is not null and length(sealed_key) > 28"));

        // ein ungueltiger Schluessel wird abgelehnt; der Schluessel des Anbieters laesst sich entfernen
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Admin(HttpMethod.Put, "/api/ai/providers/openai", new { key = "zu kurz" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Delete, "/api/ai/providers/openai/key")).StatusCode);
        Assert.Equal(0, await env.Db.ScalarAsync<int>("select count(*)::int from ai_providers where sealed_key is not null"));
    }

    [Fact]
    public async Task The_settings_of_AI_need_a_session_and_the_rules_for_the_second_factor_cover_every_change()
    {
        await using var env = await Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await env.Http.SendAsync(Req(HttpMethod.Get, "/api/ai", null), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await env.Http.SendAsync(Req(HttpMethod.Put, "/api/ai/enabled", null, new { enabled = true }), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await env.Http.SendAsync(Req(HttpMethod.Post, "/api/ai/apps", null, new { name = "x", allowed = new[] { "fake" } }), TestContext.Current.CancellationToken)).StatusCode);

        foreach (var (m, p) in new[] { ("PUT", "/api/ai/enabled"), ("PUT", "/api/ai/providers/openai"), ("DELETE", "/api/ai/providers/openai/key"), ("POST", "/api/ai/providers/codex/login"), ("POST", "/api/ai/providers/codex/logout"),
                                       ("POST", "/api/ai/apps"), ("PUT", "/api/ai/apps/x"), ("PUT", "/api/ai/apps/x/active"), ("POST", "/api/ai/apps/x/key"), ("DELETE", "/api/ai/apps/x"), ("PUT", "/api/ai/default-model") })
            Assert.True(JulOS.Frontend.Auth.StepUpPolicy.Requires(m, p), m + " " + p);
        Assert.False(JulOS.Frontend.Auth.StepUpPolicy.Requires("GET", "/api/ai"));
        Assert.False(JulOS.Frontend.Auth.StepUpPolicy.Requires("GET", "/api/ai/providers/openai/test"));

        // Namen sind je App eindeutig; Eingaben werden geprueft
        await env.NewApp("Notizen");
        Assert.Equal(HttpStatusCode.Conflict, (await env.Admin(HttpMethod.Post, "/api/ai/apps", new { name = "notizen", allowed = new[] { "fake" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Admin(HttpMethod.Post, "/api/ai/apps", new { name = "Neu", allowed = new[] { "evil" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Admin(HttpMethod.Post, "/api/ai/apps", new { name = "Neu", allowed = new[] { "fake" }, dailyTokens = 500, monthlyTokens = 100 })).StatusCode);
    }

    // ---------------------------------------------------------------- Pruefbefunde (F3 bis F6, Codex-Freigabe, Pfade)

    [Fact]
    public async Task A_missing_or_zero_usage_report_is_estimated_and_an_abort_is_booked_with_the_maximum()
    {
        await using var env = await Start();
        var (id, key) = await env.NewApp(daily: 100000);
        foreach (var word in new[] { "NOUSAGE", "ZERO" })
        {
            var r = await Json(await env.Chat(key, word + " " + Forty));
            Assert.True(r.GetProperty("usage").GetProperty("inputTokens").GetInt64() > 0, word);
            Assert.True(r.GetProperty("usage").GetProperty("outputTokens").GetInt64() > 0, word);
        }
        Assert.True(await env.Db.ScalarAsync<long>("select sum(tokens_in)::bigint from ai_usage where app_id = $1", id) > 0);
        Assert.True(await env.Db.ScalarAsync<long>("select sum(tokens_out)::bigint from ai_usage where app_id = $1", id) > 0);

        // Abbruch ohne Angabe: mindestens maxTokens als Ausgabe (ein Denkmodell rechnet auch ohne sichtbaren Text)
        using var cts = new CancellationTokenSource();
        var slow = await env.Call(HttpMethod.Post, "/api/ai/v1/chat", key, new { model = "fake/echo", maxTokens = 700, stream = true, messages = new[] { new { role = "user", content = "SLOW" } } }, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        slow.Dispose(); cts.Cancel();
        for (var i = 0; i < 100 && await env.Db.ScalarAsync<long>("select coalesce(sum(aborted), 0) from ai_usage where app_id = $1", id) < 1; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(await env.Db.ScalarAsync<long>("select sum(tokens_out)::bigint from ai_usage where app_id = $1 and aborted > 0", id) >= 700);
    }

    [Fact]
    public async Task Models_and_usage_have_the_rate_limit_of_chat_and_the_model_list_is_not_asked_again_and_again()
    {
        await using var env = await Start();
        var (_, key) = await env.NewApp(rate: 5);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await env.Call(HttpMethod.Get, i % 2 == 0 ? "/api/ai/v1/models" : "/api/ai/v1/usage", key)).StatusCode);
        foreach (var url in new[] { "/api/ai/v1/models", "/api/ai/v1/usage", "/api/ai/v1/models" })
        {
            var r = await env.Call(HttpMethod.Get, url, key);
            Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode); Assert.Equal("rate_limited", await Code(r));
        }
        Assert.Equal(1, env.Fake.ModelCalls);                          // die Liste ist kurz gemerkt
        Assert.Equal(0, env.Fake.Calls);
    }

    [Fact]
    public async Task Only_models_the_provider_lists_are_called_or_booked_so_made_up_names_fill_no_table()
    {
        await using var env = await Start();
        var (id, key) = await env.NewApp();
        for (var i = 0; i < 40; i++)
        {
            var r = await env.Chat(key, "hi", model: "fake/erfunden-" + i);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Assert.Equal("unknown_model", await Code(r));
        }
        Assert.Equal(0, env.Fake.Calls);
        Assert.Equal(0, await env.Db.ScalarAsync<int>("select count(*)::int from ai_usage where app_id = $1", id));
        Assert.Equal(HttpStatusCode.OK, (await env.Chat(key, "hi", model: "fake/echo-big")).StatusCode);
        Assert.Equal(1, await env.Db.ScalarAsync<int>("select count(*)::int from ai_usage where app_id = $1", id));
    }

    [Fact]
    public async Task The_Codex_bridge_serves_no_app_until_the_owner_confirmed_the_manual_check_for_this_version()
    {
        await using var env = await Start();
        var (_, key) = await env.NewApp("Notizen", allowed: ["codex", "fake"]);
        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Put, "/api/ai/providers/codex", new { enabled = true })).StatusCode);

        async Task<bool> Confirmed() => (await Json(await env.Admin(HttpMethod.Get, "/api/ai"))).GetProperty("providers").EnumerateArray().First(p => p.GetProperty("id").GetString() == "codex").GetProperty("codex").GetProperty("confirmed").GetBoolean();
        Assert.False(await Confirmed());
        var refused = await env.Chat(key, "hi", model: "codex/gpt-x");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode); Assert.Equal("provider_unverified", await Code(refused));
        var models = await Json(await env.Call(HttpMethod.Get, "/api/ai/v1/models", key));
        Assert.DoesNotContain(models.GetProperty("models").EnumerateArray(), m => m.GetProperty("provider").GetString() == "codex");

        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Put, "/api/ai/providers/codex/confirm", new { confirmed = true })).StatusCode);
        Assert.True(await Confirmed());
        Assert.NotEqual("provider_unverified", await Code(await env.Chat(key, "hi", model: "codex/gpt-x")));    // jetzt scheitert es erst am fehlenden Programm
        Assert.Equal(HttpStatusCode.NoContent, (await env.Admin(HttpMethod.Put, "/api/ai/providers/codex/confirm", new { confirmed = false })).StatusCode);
        Assert.False(await Confirmed());
        Assert.True(JulOS.Frontend.Auth.StepUpPolicy.Requires("PUT", "/api/ai/providers/codex/confirm"));
        Assert.Contains("ai.codex.confirm", await env.DumpAsync());
    }

    [Fact]
    public async Task Path_tricks_never_lead_a_key_to_the_settings_even_on_a_real_Kestrel()
    {
        await using var env = await Start();                         // Einrichtung ueber den Speicher-Server, Pruefung gegen ein echtes Kestrel mit derselben Datenbank
        var (_, key) = await env.NewApp();
        await using var kestrelServer = new Server(env.Db, secretKey: Secret);
        await using var factory = kestrelServer.WithWebHostBuilder(b => b.UseSetting("JULOS_AI_FAKE", "on"));
        factory.UseKestrel(0);
        factory.StartServer();
        var address = new Uri(factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());

        async Task<(int Status, string Body)> Raw(string target, string? bearer)
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(address.Host, address.Port);
            var stream = tcp.GetStream();
            var head = $"GET {target} HTTP/1.1\r\nHost: {address.Authority}\r\nConnection: close\r\n" + (bearer is null ? "" : $"Authorization: Bearer {bearer}\r\n") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            var status = text.StartsWith("HTTP/1.1 ") ? int.Parse(text.Substring(9, 3)) : 0;
            return (status, text);
        }

        // Alles, was nach den Einstellungen riecht, bleibt mit dem Schluessel genauso verschlossen wie ohne
        foreach (var target in new[] { "/api/ai/v1/../apps", "/api/ai/v1/%2e%2e/apps", "/api/ai/v1/%2E%2E/%2E%2E/hosts", "//api/ai/apps", "/api/ai/v1;/../apps", "/api/ai/v1/..%2fapps", "/API/AI/apps", "/api/ai/v1%2f..%2fapps", "/api/ai" })
        {
            var (status, body) = await Raw(target, key);
            Assert.True(status is 401 or 404 or 400, $"{target} -> {status}");
            Assert.DoesNotContain("keyHint", body); Assert.DoesNotContain("\"apps\"", body);
        }
        // Schreibweise und doppelte Schraegstriche am Anfang: ohne Schluessel nie Zugang, mit Schluessel hoechstens die drei Wege der App
        Assert.Equal(401, (await Raw("/API/AI/V1/models", null)).Status);
        Assert.Equal(401, (await Raw("/api/ai/v1/models", "jos_ai_falsch")).Status);
        Assert.Equal(200, (await Raw("/API/AI/V1/models", key)).Status);
    }
}
