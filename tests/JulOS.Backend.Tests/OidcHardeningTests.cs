using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>
/// Die Nachbesserungen nach dem Sicherheits-Review (docs/SIGN-IN.md, "Review"): Zielfilter der Logout-Zustellung (F3), Zustellung je App parallel und ohne die Antwort zu lesen (F4),
/// spaeter erneut bei fehlendem Schluessel (F5), Einloesen eines Codes in einer Transaktion (F6), Sperre trifft nur falsche Anmeldungen (F7), Eigentuemer wechselt nur ausdruecklich (F8).
/// </summary>
public sealed class OidcHardeningTests
{
    // ---- F3: wohin Logout-Token gehen duerfen ----

    [Theory]
    [InlineData("8.8.8.8", false, false, true)]
    [InlineData("2606:4700:4700::1111", false, false, true)]
    [InlineData("127.0.0.1", false, false, false)]
    [InlineData("::1", false, false, false)]
    [InlineData("127.0.0.1", false, true, true)]                  // Entwicklung (Aussteller ist http-localhost)
    [InlineData("169.254.169.254", true, false, false)]           // Metadaten: nie, auch nicht mit dem Haken
    [InlineData("169.254.169.254", true, true, false)]            // auch in der Entwicklung nicht
    [InlineData("fe80::1", true, false, false)]
    [InlineData("fd00:ec2::254", true, false, false)]
    [InlineData("10.0.0.1", false, false, false)]                 // lokales Netz nur mit Haken
    [InlineData("10.0.0.1", true, false, true)]
    [InlineData("172.16.5.5", false, false, false)]
    [InlineData("172.16.5.5", true, false, true)]
    [InlineData("172.32.5.5", false, false, true)]                // schon nicht mehr privat
    [InlineData("192.168.1.10", false, false, false)]
    [InlineData("192.168.1.10", true, false, true)]
    [InlineData("100.100.1.1", false, false, false)]              // CGNAT / VPN-Netze
    [InlineData("fd12:3456::1", false, false, false)]
    [InlineData("fd12:3456::1", true, false, true)]
    [InlineData("::ffff:10.0.0.1", false, false, false)]          // IPv4 in IPv6-Schreibweise zaehlt wie IPv4
    [InlineData("::ffff:169.254.169.254", true, false, false)]
    [InlineData("0.0.0.0", true, true, false)]
    [InlineData("::", true, true, false)]
    [InlineData("224.0.0.1", true, true, false)]
    [InlineData("255.255.255.255", true, true, false)]
    public void Target_addresses_are_filtered_loopback_and_link_local_never_the_local_network_only_with_the_tick(string ip, bool allowPrivate, bool dev, bool expected) =>
        Assert.Equal(expected, OidcNet.Allowed(IPAddress.Parse(ip), new OidcNetPolicy(allowPrivate, dev)));

    [Fact]
    public async Task Registering_a_logout_address_rejects_http_loopback_metadata_and_the_local_network_without_the_tick()
    {
        await using var rig = await OidcRig.CreateAsync(publicUrl: "https://julos.example");           // eine Instanz im Betrieb: der Aussteller ist https
        var ct = TestContext.Current.CancellationToken;
        async Task<string> Create(string uri, bool? priv = null)
        {
            var r = await rig.Http.SendAsync(Req(HttpMethod.Post, "/api/security/oidc/apps", rig.Cookie,
                new { name = "A" + Guid.NewGuid().ToString("N")[..6], redirectUris = new[] { OidcRig.Redirect }, backchannelUri = uri, backchannelPrivate = priv, roles = new[] { "admin" } }), ct);
            return r.IsSuccessStatusCode ? "ok" : (await ApiKit.Json(r)).GetProperty("code").GetString()!;
        }
        Assert.Equal("ok", await Create("https://app.example/logout"));
        Assert.Equal("invalid_backchannel", await Create("http://app.example/logout"));                   // http nie, ausser der Aussteller ist selbst http-localhost
        Assert.Equal("invalid_backchannel", await Create("http://localhost:5000/logout"));
        Assert.Equal("invalid_backchannel", await Create("https://localhost/logout"));
        Assert.Equal("invalid_backchannel", await Create("https://app.localhost/logout"));
        Assert.Equal("invalid_backchannel", await Create("https://127.0.0.1/logout"));
        Assert.Equal("invalid_backchannel", await Create("https://[::1]/logout"));
        Assert.Equal("invalid_backchannel", await Create("https://169.254.169.254/latest/meta-data"));
        Assert.Equal("invalid_backchannel", await Create("https://169.254.169.254/latest/meta-data", priv: true));   // auch mit dem Haken nicht
        Assert.Equal("invalid_backchannel", await Create("https://10.0.0.1/logout"));                     // lokales Netz: nur mit dem Haken der App
        Assert.Equal("invalid_backchannel", await Create("https://192.168.1.10/logout", priv: false));
        Assert.Equal("ok", await Create("https://10.0.0.1/logout", priv: true));
        Assert.Equal("ok", await Create("https://nas.lan/logout", priv: true));                           // Namen werden erst beim Verbinden geprueft

        // in der Entwicklung (Aussteller http-localhost) geht http zu localhost
        await using var dev = await OidcRig.CreateAsync();
        var d = await dev.Http.SendAsync(Req(HttpMethod.Post, "/api/security/oidc/apps", dev.Cookie,
            new { name = "Dev", redirectUris = new[] { OidcRig.Redirect }, backchannelUri = "http://localhost:5000/logout", roles = new[] { "admin" } }), ct);
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.False((await ApiKit.Json(d)).GetProperty("app").GetProperty("backchannelPrivate").GetBoolean());
    }

    /// <summary>Ein kleiner Server, der jede Anfrage mit einer festen Antwort beendet (statt des Netzes).</summary>
    private static (TcpListener Listener, Task Serve) Serve(string raw, Func<NetworkStream, Task>? after = null)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var serve = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await l.AcceptTcpClientAsync();
                    var s = c.GetStream();
                    var buf = new byte[8192];
                    _ = await s.ReadAsync(buf);
                    await s.WriteAsync(Encoding.ASCII.GetBytes(raw));
                    if (after is not null) await after(s);
                }
            }
            catch { /* beendet */ }
        });
        return (l, serve);
    }

    [Fact]
    public async Task The_real_client_checks_the_target_after_the_name_is_resolved_so_a_name_pointing_inward_is_never_connected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (l, _) = Serve("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
        try
        {
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            using var body = new FormUrlEncodedContent([new("logout_token", "x")]);
            // Entwicklung: Loopback erlaubt
            using (var dev = OidcHttp.Guarded(new OidcNetPolicy(false, true)))
                Assert.Equal(HttpStatusCode.NoContent, (await dev.PostAsync($"http://127.0.0.1:{port}/logout", body, ct)).StatusCode);
            // Betrieb: die Adresse selbst und ein Name, der darauf aufloest (der Name besteht die Pruefung beim Eintragen, nicht aber beim Verbinden)
            using var prod = OidcHttp.Guarded(new OidcNetPolicy(false, false));
            await Assert.ThrowsAsync<HttpRequestException>(() => prod.PostAsync($"http://127.0.0.1:{port}/logout", new FormUrlEncodedContent([new("a", "b")]), ct));
            var viaName = await Assert.ThrowsAsync<HttpRequestException>(() => prod.PostAsync($"http://localhost:{port}/logout", new FormUrlEncodedContent([new("a", "b")]), ct));
            Assert.Contains("nicht erlaubt", viaName.Message + viaName.InnerException?.Message);
            // Metadaten und lokales Netz ohne Haken: gar kein Verbindungsversuch
            await Assert.ThrowsAsync<HttpRequestException>(() => prod.PostAsync("http://169.254.169.254/latest/meta-data", new FormUrlEncodedContent([new("a", "b")]), ct));
            await Assert.ThrowsAsync<HttpRequestException>(() => prod.PostAsync("http://10.255.255.1/", new FormUrlEncodedContent([new("a", "b")]), ct));
        }
        finally { l.Stop(); }
    }

    // ---- F4/F5: Zustellung ----

    private sealed class Script(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<Uri> Posts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Posts) Posts.Add(request.RequestUri!);
            return await answer(request, ct);
        }
    }

    /// <summary>Eine Antwort, deren Koerper nie zu Ende geht und beim Lesen einen Fehler wirft: wer ihn liest, scheitert (die Zustellung liest ihn nicht).</summary>
    private sealed class EndlessContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("der Koerper wurde gelesen");
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new IOException("der Koerper wurde gelesen");
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => throw new IOException("der Koerper wurde gelesen");
    }

    [Fact]
    public async Task A_dead_app_does_not_hold_up_the_healthy_ones_and_the_body_of_the_answer_is_never_read()
    {
        await using var rig = await OidcRig.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var dead = await rig.AddAppAsync("Dead", backchannel: "https://dead.example/logout", redirects: ["https://dead.example/cb"]);
        var healthy = await rig.AddAppAsync("Healthy", backchannel: "https://healthy.example/logout", redirects: ["https://healthy.example/cb"]);
        var healthyDone = new TaskCompletionSource();
        var script = new Script(async (req, token) =>
        {
            if (req.RequestUri!.Host == "dead.example")
            {
                // die tote App antwortet erst, wenn die gesunde schon bedient ist; wuerde nacheinander zugestellt, kaeme die gesunde nie dran
                await healthyDone.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                throw new HttpRequestException("tot");
            }
            healthyDone.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new EndlessContent() };      // Kopf 200, Koerper endlos: der Koerper darf nicht gelesen werden
        });
        rig.Outbound.Create = _ => new HttpClient(script, disposeHandler: false);

        var people = new List<(string Cookie, Guid Id)>();
        for (var i = 0; i < 3; i++) people.Add(await rig.SecondPersonAsync("p" + i));
        foreach (var (cookie, id) in people) { await rig.AssignAsync(dead, id, "member"); await rig.AssignAsync(healthy, id, "member"); await rig.SignInAsync(dead, cookie); await rig.SignInAsync(healthy, cookie); }
        await rig.Db.ExecAsync("delete from sessions where user_id = any($1)", people.Select(p => p.Id).ToArray());

        await rig.Logout.RunOnceAsync(ct);
        Assert.Equal(3, await rig.Db.ScalarAsync<long>("select count(*) from oidc_logouts where app_id = $1 and result = 'ok'", healthy.Id));      // alle gesunden zugestellt
        Assert.Equal(1, script.Posts.Count(u => u.Host == "dead.example"));                  // die tote App: ein Versuch, die uebrigen warten bis zum naechsten Durchgang
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_logouts where app_id = $1 and attempts = 1", dead.Id));
        Assert.Equal("failed", await rig.Db.ScalarAsync<string>("select result from oidc_logouts where app_id = $1 and attempts = 1", dead.Id));   // kein Status der Zieladresse
    }

    [Fact]
    public async Task While_the_signing_key_is_missing_logout_tokens_wait_and_are_delivered_later_without_using_up_attempts()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var rig = await OidcRig.CreateAsync(time: time);
        var ct = TestContext.Current.CancellationToken;
        var app = await rig.AddAppAsync();
        var script = new Script((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        rig.Outbound.Create = _ => new HttpClient(script, disposeHandler: false);
        await rig.SignInAsync(app);
        var original = await rig.Db.ScalarAsync<byte[]>("select private_sealed from oidc_keys");
        await rig.Db.ExecAsync("update oidc_keys set private_sealed = $1", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 });
        time.Advance(TimeSpan.FromMinutes(1));
        await rig.Http.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", rig.Cookie, new { }), ct);

        for (var i = 0; i < 3; i++) { await rig.Logout.RunOnceAsync(ct); time.Advance(OidcLogoutService.NotReadyWait + TimeSpan.FromSeconds(1)); }
        Assert.Empty(script.Posts);
        Assert.Equal(0, await rig.Db.ScalarAsync<int>("select attempts from oidc_logouts"));                // kein Versuch verbraucht
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_logouts where done_at is not null"));   // und nichts endgueltig verworfen

        await rig.Db.ExecAsync("update oidc_keys set private_sealed = $1", original);
        time.Advance(TimeSpan.FromMinutes(1));
        await rig.Logout.RunOnceAsync(ct);
        Assert.Single(script.Posts);
        Assert.Equal("ok", await rig.Db.ScalarAsync<string>("select result from oidc_logouts"));
    }

    [Fact]
    public async Task A_stored_address_that_is_no_longer_allowed_is_never_posted_to()
    {
        await using var rig = await OidcRig.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var app = await rig.AddAppAsync();
        var script = new Script((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        rig.Outbound.Create = _ => new HttpClient(script, disposeHandler: false);
        await rig.SignInAsync(app);
        await rig.Db.ExecAsync("update oidc_apps set backchannel_uri = 'https://169.254.169.254/x'");        // zum Beispiel von Hand in die Datenbank geschrieben
        await rig.Http.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", rig.Cookie, new { }), ct);
        await rig.Logout.RunOnceAsync(ct);
        Assert.Empty(script.Posts);
        Assert.Equal("failed: not allowed", await rig.Db.ScalarAsync<string>("select result from oidc_logouts"));
    }

    // ---- F6: ein Code ist atomar, und der Widerruf bei Wiederverwendung auch ----

    [Fact]
    public async Task Presenting_one_code_many_times_at_once_gives_exactly_one_token_and_the_reuse_revokes_everything_that_came_of_it()
    {
        await using var rig = await OidcRig.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var app = await rig.AddAppAsync();
        var v = OidcRig.NewVerifier();
        var code = await rig.CodeAsync(app, v);
        var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => rig.TokenAsync(app, code, v)));
        var winners = results.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        Assert.Single(winners);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        // die Verlierer haben gewartet, bis der Gewinner fertig war, und dann widerrufen: kein Zugriffs-Token, kein Grant uebrig, ein Logout fuer die App
        var access = (await ApiKit.Json(winners[0])).GetProperty("access_token").GetString()!;
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_tokens"));
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_grants"));
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_logouts where reason = 'code_reused'"));
        var info = new HttpRequestMessage(HttpMethod.Get, "/oidc/userinfo");
        info.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Http.SendAsync(info, ct)).StatusCode);
    }

    // ---- F7: die Sperre trifft nur falsche Anmeldungen ----

    [Fact]
    public async Task The_lock_after_wrong_app_logins_never_turns_away_the_right_secret_but_wrong_ones_keep_being_refused()
    {
        await using var rig = await OidcRig.CreateAsync();
        var app = await rig.AddAppAsync();
        var v = OidcRig.NewVerifier();
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await rig.TokenAsync(app, "x", v, secret: "falsch")).StatusCode);
        var wrong = await rig.TokenAsync(app, "x", v, secret: "falsch");
        Assert.Equal((HttpStatusCode)429, wrong.StatusCode);
        Assert.NotNull(wrong.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await rig.TokenAsync(app, await rig.CodeAsync(app, v), v)).StatusCode);       // wer das Geheimnis hat, kommt durch
    }

    // ---- Protokoll: Randfaelle aus dem Review ----

    [Fact]
    public async Task Two_ways_of_logging_in_at_once_and_repeated_or_oddly_written_parameters_are_refused()
    {
        await using var rig = await OidcRig.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var app = await rig.AddAppAsync();
        var v = OidcRig.NewVerifier();
        var code = await rig.CodeAsync(app, v);

        // Basic UND client_secret im Formular: abgelehnt, der Code bleibt unberuehrt
        var both = new HttpRequestMessage(HttpMethod.Post, "/oidc/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = app.RedirectUri, ["code_verifier"] = v, ["client_secret"] = app.Secret }),
        };
        both.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(app.ClientId + ":" + app.Secret)));
        var res = await rig.Http.SendAsync(both, ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_request", (await ApiKit.Json(res)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, (await rig.TokenAsync(app, code, v)).StatusCode);

        // doppelte client_id und redirect_uri in authorize: nichts geht an eine Adresse, es gibt einen Fehler ohne Weiterleitung
        var other = Uri.EscapeDataString("https://evil.example/cb");
        foreach (var raw in new[] { "client_id=" + app.ClientId, "redirect_uri=" + other })
        {
            var r = await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["raw"] = raw);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest || (r.Headers.Location is { } l && !l.ToString().Contains("code=")), "doppelter Parameter: " + (int)r.StatusCode + " " + r.Headers.Location);
            Assert.DoesNotContain("evil.example", r.Headers.Location?.ToString() ?? "");
        }

        // andere Schreibweisen des Pfads geben nie einen Code an einen Dritten
        foreach (var path in new[] { "//oidc/authorize", "/oidc/authorize/", "/OIDC/authorize" })
        {
            var q = "?response_type=code&client_id=" + app.ClientId + "&redirect_uri=" + Uri.EscapeDataString(app.RedirectUri) + "&scope=openid&state=s&code_challenge=" + OidcRules.S256(v) + "&code_challenge_method=S256";
            var req = new HttpRequestMessage(HttpMethod.Get, path + q);
            req.Headers.Add("Cookie", rig.Cookie);
            var r = await rig.Http.SendAsync(req, ct);
            Assert.DoesNotContain("evil", r.Headers.Location?.ToString() ?? "");
            Assert.True(r.StatusCode is HttpStatusCode.Found or HttpStatusCode.NotFound or HttpStatusCode.OK or HttpStatusCode.BadRequest, path + " " + (int)r.StatusCode);
            if (r.StatusCode == HttpStatusCode.Found) Assert.StartsWith(app.RedirectUri + "?", r.Headers.Location!.ToString());      // ein Code geht nur an die eingetragene Adresse
        }

        // state und nonce: Grenzen
        Assert.Contains("error=invalid_request", (await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["state"] = new string('a', 513))).Headers.Location!.ToString());
        Assert.Contains("error=invalid_request", (await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["state"] = "a\u0001b")).Headers.Location!.ToString());
        Assert.Contains("code=", (await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["state"] = new string('a', 512))).Headers.Location!.ToString());
        Assert.Contains("error=invalid_request", (await rig.AuthorizeAsync(app, rig.Cookie, v, q => q["nonce"] = new string('n', 513))).Headers.Location!.ToString());
    }

    // ---- F8: der Eigentuemer wechselt nur ausdruecklich ----

    [Fact]
    public async Task The_owner_is_pinned_and_when_the_account_is_gone_nobody_inherits_the_admin_role_until_the_owner_is_set_explicitly()
    {
        await using var rig = await OidcRig.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var app = await rig.AddAppAsync();                                       // das erste Mal wird der Eigentuemer festgehalten
        var anna = await rig.SecondPersonAsync("anna");
        var store = new OidcAppStore(rig.Db.Data, TimeProvider.System);
        Assert.Equal(rig.OwnerId, await store.OwnerIdAsync(ct));

        // das Konto des Eigentuemers faellt weg: Anna (nun das aelteste) wird NICHT still Eigentuemerin
        await rig.Db.ExecAsync("delete from users where id = $1", rig.OwnerId);
        Assert.Null(await store.OwnerIdAsync(ct));
        var forbidden = await rig.Http.SendAsync(Req(HttpMethod.Get, "/api/security/oidc", anna.Cookie), ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("owner_only", (await ApiKit.Json(forbidden)).GetProperty("code").GetString());
        var res = await rig.AuthorizeAsync(app, anna.Cookie, OidcRig.NewVerifier());
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);                  // keine Rolle in der App, also kein Zugang (die Seite "kein Zugang")
        Assert.Null(await store.RoleOfAsync((await store.FindAsync(app.Id, ct))!, anna.UserId, ct));

        // ausdrueckliche Wiederherstellung (docs/SIGN-IN.md): den Eintrag loeschen, dann wird wieder das aelteste Konto festgehalten
        await rig.Db.ExecAsync("delete from instance_settings where key = 'oidc.owner'");
        Assert.Equal(anna.UserId, await store.OwnerIdAsync(ct));
    }

    [Fact]
    public async Task The_owner_can_hand_over_the_role_explicitly_with_the_second_factor_and_it_is_in_the_audit_trail()
    {
        await using var rig = await OidcRig.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var anna = await rig.SecondPersonAsync("anna");
        var url = "/api/security/oidc/owner";
        // nur der Eigentuemer, nur eine bestehende bestaetigte Person
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Http.SendAsync(Req(HttpMethod.Put, url, anna.Cookie, new { userId = anna.UserId }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.SendAsync(Req(HttpMethod.Put, url, rig.Cookie, new { userId = Guid.NewGuid() }), ct)).StatusCode);
        // ohne Freigabe geht es nicht (api/security/**)
        await rig.Db.ExecAsync("update sessions set step_up_until = null");
        Assert.Equal("step_up_required", (await ApiKit.Json(await rig.Http.SendAsync(Req(HttpMethod.Put, url, rig.Cookie, new { userId = anna.UserId }), ct))).GetProperty("code").GetString());
        await rig.Db.ExecAsync("update sessions set step_up_until = now() + interval '5 minutes'");
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Http.SendAsync(Req(HttpMethod.Put, url, rig.Cookie, new { userId = anna.UserId }), ct)).StatusCode);

        Assert.Equal(anna.UserId, await new OidcAppStore(rig.Db.Data, TimeProvider.System).OwnerIdAsync(ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Http.SendAsync(Req(HttpMethod.Get, "/api/security/oidc", rig.Cookie), ct)).StatusCode);   // der bisherige verwaltet nichts mehr
        await rig.Db.ExecAsync("update sessions set step_up_until = now() + interval '5 minutes'");
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.SendAsync(Req(HttpMethod.Get, "/api/security/oidc", anna.Cookie), ct)).StatusCode);
        var audit = await new SecurityAuditStore(rig.Db.Data, TimeProvider.System).ListAsync(50, ct);
        Assert.Contains(audit, e => e.Event == "oidc.owner.set" && e.Detail == "anna");
    }
}
