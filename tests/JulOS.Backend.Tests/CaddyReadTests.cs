using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using JulOS.Backend.Modules;

namespace JulOS.Backend.Tests;

/// <summary>Caddy lesen: Konfiguration ohne Geheimnisse, Zugriffsprotokoll, Zertifikate aus TLS-Handshakes, Zustand und feste Abrufe der Admin-API. Alles gegen Fakes und Loopback.</summary>
public sealed class CaddyReadTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("julos-caddy-");
    public void Dispose() => _dir.Delete(true);

    private static readonly Uri Admin = new("http://caddy:2019");

    private const string ConfigJson = """
        {"admin":{"listen":"0.0.0.0:2019"},
         "apps":{"http":{"servers":{"srv0":{"routes":[
           {"match":[{"host":["app.example.test"]}],"handle":[
             {"handler":"headers","request":{"set":{"Authorization":["Bearer GEHEIMER-TOKEN-1"]}}},
             {"handler":"authentication","providers":{"http_basic":{"accounts":[{"username":"jul","password":"$2a$14$GEHEIMER-HASH-2"}]}}},
             {"handler":"reverse_proxy","headers":{"request":{"set":{"X-Intern":["GEHEIMER-WERT-3"]}}},"upstreams":[{"dial":"web:3000"}]}]}]}}},
         "tls":{"automation":{"policies":[{"issuers":[{"module":"acme","email":"admin@example.test","challenges":{"dns":{"provider":{"name":"x","api_token":"GEHEIMER-TOKEN-4"}}}}]}]}}}}
        """;

    /* ---------- Konfiguration ohne Geheimnisse ---------- */

    [Fact]
    public void The_json_config_keeps_the_structure_and_hides_everything_that_sounds_secret()
    {
        using var doc = JsonDocument.Parse(ConfigJson);
        var text = CaddyRedactor.Json(doc.RootElement);
        Assert.DoesNotContain("GEHEIM", text);
        Assert.DoesNotContain("admin@example.test", text);
        Assert.Contains("app.example.test", text);
        Assert.Contains("web:3000", text);
        Assert.Contains("reverse_proxy", text);
        Assert.Contains("***", text);
        using var parsed = JsonDocument.Parse(text);   // bleibt gueltiges JSON
        Assert.Equal("0.0.0.0:2019", parsed.RootElement.GetProperty("admin").GetProperty("listen").GetString());
    }

    [Fact]
    public void The_caddyfile_text_loses_values_and_whole_auth_blocks()
    {
        const string File = """
            {
                email admin@example.test
                acme_dns cloudflare GEHEIMER-TOKEN-0
            }
            app.example.test {
                basic_auth {
                    jul $2a$14$GEHEIMER-HASH-2
                    anna $2a$14$GEHEIMER-HASH-3
                }
                header X-Secret-Key GEHEIMER-WERT-4
                tls {
                    dns cloudflare {env.CF_TOKEN}
                }
                reverse_proxy web:3000
            }
            """;
        var text = CaddyRedactor.Caddyfile(File);
        Assert.DoesNotContain("GEHEIM", text);
        Assert.DoesNotContain("admin@example.test", text);
        Assert.Contains("app.example.test {", text);
        Assert.Contains("reverse_proxy web:3000", text);
        Assert.Contains("basic_auth {", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void Long_config_text_is_cut()
    {
        var big = JsonSerializer.Serialize(new { items = Enumerable.Range(0, 60000).Select(i => "eintrag-" + i).ToArray() });
        using var doc = JsonDocument.Parse(big);
        var text = CaddyRedactor.Json(doc.RootElement);
        Assert.True(text.Length <= CaddyRedactor.MaxText + 3);
        Assert.EndsWith("…", text);
    }

    /* ---------- Zugriffsprotokoll ---------- */

    private static string Line(string host, string uri, int status, double ts = 1_700_000_000, string ip = "198.51.100.7") =>
        "{\"level\":\"info\",\"ts\":" + ts.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"msg\":\"handled request\",\"request\":{\"remote_ip\":\"" + ip + "\",\"method\":\"GET\",\"host\":\"" + host
        + "\",\"uri\":\"" + uri + "\",\"headers\":{\"Authorization\":[\"Bearer GEHEIM\"]}},\"status\":" + status + ",\"duration\":0.0123,\"size\":512}";

    [Fact]
    public void The_access_log_gives_entries_and_counters_without_visitors_addresses_or_queries()
    {
        var text = string.Join('\n', Line("a.example.test", "/x?token=GEHEIM", 200, 1_700_000_001), "kein json", Line("a.example.test", "/y", 404, 1_700_000_002), Line("b.example.test", "/z", 502, 1_700_000_003), "{\"kaputt\"");
        var log = CaddyAccessLog.Parse(text);
        Assert.True(log.Available);
        Assert.Equal(3, log.Total);
        Assert.Equal(["/z", "/y", "/x"], log.Entries.Select(e => e.Path));       // neueste zuerst
        var a = log.Hosts.Single(h => h.Host == "a.example.test");
        Assert.Equal((2, 1, 0), (a.Requests, a.Errors4xx, a.Errors5xx));
        Assert.Equal(1, log.Hosts.Single(h => h.Host == "b.example.test").Errors5xx);
        var json = JsonSerializer.Serialize(log);
        Assert.DoesNotContain("GEHEIM", json);
        Assert.DoesNotContain("198.51.100.7", json);
        Assert.Equal(12, log.Entries[0].Ms);
    }

    [Fact]
    public async Task The_log_file_is_read_from_the_tail_and_a_missing_file_is_just_unavailable()
    {
        var path = Path.Combine(_dir.FullName, "access.log");
        var lines = Enumerable.Range(0, 20000).Select(i => Line("h.example.test", "/p" + i, 200, 1_700_000_000 + i));
        await File.WriteAllLinesAsync(path, lines);
        Assert.True(new FileInfo(path).Length > CaddyAccessLog.TailBytes);
        var log = await CaddyAccessLog.ReadAsync(path, CancellationToken.None);
        Assert.True(log.Available);
        Assert.Equal(CaddyAccessLog.MaxEntries, log.Entries.Length);
        Assert.Equal("/p19999", log.Entries[0].Path);
        Assert.True(log.Total < 20000);                                              // nur der Schwanz wurde gelesen

        Assert.False((await CaddyAccessLog.ReadAsync(Path.Combine(_dir.FullName, "gibt-es-nicht.log"), CancellationToken.None)).Available);
        Assert.False((await CaddyAccessLog.ReadAsync(null, CancellationToken.None)).Available);
    }

    /* ---------- Zertifikate ---------- */

    [Fact]
    public void Only_names_that_can_be_probed_are_probed()
    {
        var routes = new[] { "app.example.test", "*.example.test", "localhost", "192.0.2.1", "nas.local", "app.example.test", "{$DOMAIN}", "http://x.example.test", "Wiki.Example.Test" }
            .Select(d => new CaddyRoute(d, null, "proxy", ["web:80"]));
        Assert.Equal(["app.example.test", "wiki.example.test"], CaddyCerts.Domains(routes));
    }

    [Fact]
    public void Expiry_warns_from_14_days()
    {
        var now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        Assert.Equal("ok", CaddyCerts.Classify("a.test", "CA", now.AddDays(15.5), true, now).State);
        Assert.Equal("warn", CaddyCerts.Classify("a.test", "CA", now.AddDays(14.5), true, now).State);
        Assert.Equal("warn", CaddyCerts.Classify("a.test", "CA", now.AddDays(1), true, now).State);
        Assert.Equal("expired", CaddyCerts.Classify("a.test", "CA", now.AddDays(-1), true, now).State);
        Assert.Equal(14, CaddyCerts.Classify("a.test", "CA", now.AddDays(14.5), true, now).DaysLeft);
    }

    private static X509Certificate2 SelfSigned(string name, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var cert = req.CreateSelfSigned(notAfter.AddDays(-30), notAfter);
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    [Fact]
    public async Task A_real_handshake_on_loopback_reads_issuer_and_expiry_and_says_it_is_not_trusted()
    {
        using var cert = SelfSigned("Test-CA-Wegwerf", DateTimeOffset.UtcNow.AddDays(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert });
            await Task.Delay(200);
        });
        var info = await CaddyCerts.ProbeAsync("app.example.test", new UpstreamTarget("127.0.0.1", port), TimeProvider.System, CancellationToken.None);
        await server;
        Assert.Equal("warn", info.State);
        Assert.Equal("Test-CA-Wegwerf", info.Issuer);
        Assert.False(info.Trusted);
        Assert.InRange(info.DaysLeft ?? 0, 3, 5);
    }

    [Fact]
    public async Task A_refused_connection_and_a_forbidden_address_are_reported_without_probing()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        Assert.Equal("refused", (await CaddyCerts.ProbeAsync("a.example.test", new UpstreamTarget("127.0.0.1", port), TimeProvider.System, CancellationToken.None)).Error);
        var meta = await CaddyCerts.ProbeAsync("a.example.test", new UpstreamTarget("169.254.169.254", 443), TimeProvider.System, CancellationToken.None);
        Assert.Equal(("skipped", "notAllowed"), (meta.State, meta.Error));
    }

    [Fact]
    public void The_internal_ca_gives_name_and_expiry_but_never_the_certificate()
    {
        using var cert = SelfSigned("Caddy Local Authority - ECC Intermediate", DateTimeOffset.UtcNow.AddDays(100));
        var pem = cert.ExportCertificatePem();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "local", name = "Local CA", intermediate_common_name = "Caddy Local Authority - ECC Intermediate", intermediate_certificate = pem, root_certificate = pem }));
        var ca = CaddyCerts.ParseCa(doc.RootElement, DateTimeOffset.UtcNow)!;
        Assert.Equal("Local CA", ca.Name);
        Assert.InRange(ca.DaysLeft ?? 0, 98, 100);
        Assert.DoesNotContain("BEGIN CERTIFICATE", JsonSerializer.Serialize(ca));
    }

    /* ---------- Modul: feste Abrufe, Zugangsdaten, Zustand, Zwischenspeicher ---------- */

    private static FakeServiceWeb AdminWeb() => new(new()
    {
        ["http://caddy:2019/config/apps/http/servers"] = (HttpStatusCode.OK, """{"srv0":{"routes":[{"match":[{"host":["app.example.test"]}],"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"web:3000"}]}]},{"match":[{"host":["*.example.test"]}],"handle":[{"handler":"static_response","status_code":404}]}]}}"""),
        ["http://caddy:2019/config/"] = (HttpStatusCode.OK, ConfigJson),
        ["http://caddy:2019/reverse_proxy/upstreams"] = (HttpStatusCode.OK, """[{"address":"web:3000","num_requests":2,"fails":1}]"""),
        ["http://caddy:2019/pki/ca/local"] = (HttpStatusCode.NotFound, ""),
    });

    [Fact]
    public async Task Health_says_whether_the_admin_api_answers_and_what_caddy_counts()
    {
        var web = AdminWeb();
        var caddy = new CaddyModule(new CaddyOptions(ModuleMode.Read, null, Admin), web);
        var h = await caddy.HealthAsync(CancellationToken.None);
        Assert.Equal("up", h.State);
        Assert.NotNull(h.Ms);
        Assert.Equal(("web:3000", 2, 1), (h.Upstreams[0].Address, h.Upstreams[0].Requests, h.Upstreams[0].Fails));
        Assert.All(web.Requests, r => Assert.StartsWith("http://caddy:2019/", r.Url));

        var down = await new CaddyModule(new CaddyOptions(ModuleMode.Read, null, Admin), new FakeServiceWeb([]) { Throw = new HttpRequestException("x") }).HealthAsync(CancellationToken.None);
        Assert.Equal("down", down.State);
        var none = await new CaddyModule(new CaddyOptions(ModuleMode.Read, null, null)).HealthAsync(CancellationToken.None);
        Assert.Equal("unknown", none.State);
    }

    [Fact]
    public async Task Only_four_fixed_paths_are_asked_and_the_credentials_come_from_the_file()
    {
        var web = AdminWeb();
        var auth = Path.Combine(_dir.FullName, "admin-auth");
        await File.WriteAllTextAsync(auth, "jul:wegwerf-passwort\n");
        var caddy = new CaddyModule(new CaddyOptions(ModuleMode.Read, null, Admin, auth), web, tls: (d, _, _) => Task.FromResult(new CertInfo(d, "ok", "CA", null, 60, true, null)));
        await caddy.ReadAsync(CancellationToken.None);
        await caddy.HealthAsync(CancellationToken.None);
        await caddy.ConfigAsync(CancellationToken.None);
        await caddy.CertificatesAsync(CancellationToken.None);
        var paths = web.Requests.Select(r => new Uri(r.Url).AbsolutePath).Distinct().Order().ToArray();
        Assert.Equal(["/config/", "/config/apps/http/servers", "/pki/ca/local", "/reverse_proxy/upstreams"], paths);
        var expected = "Basic " + Convert.ToBase64String("jul:wegwerf-passwort"u8.ToArray());
        Assert.All(web.Requests, r => Assert.Equal(expected, r.Headers["Authorization"]));

        await File.WriteAllTextAsync(auth, "Bearer abc");
        web.Requests.Clear();
        await caddy.ReadAsync(CancellationToken.None);
        Assert.Equal("Bearer abc", web.Requests[0].Headers["Authorization"]);
        await File.WriteAllTextAsync(auth, "nur-ein-token");
        web.Requests.Clear();
        await caddy.ReadAsync(CancellationToken.None);
        Assert.Equal("Bearer nur-ein-token", web.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task An_unreadable_credentials_file_makes_the_admin_api_unavailable_not_the_server_crash()
    {
        var caddy = new CaddyModule(new CaddyOptions(ModuleMode.Read, null, Admin, Path.Combine(_dir.FullName, "fehlt")), AdminWeb());
        var l = await caddy.ReadAsync(CancellationToken.None);
        Assert.Equal("none", l.Source);
        Assert.NotEmpty(l.Warnings);
        Assert.Equal("down", (await caddy.HealthAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task The_config_text_comes_from_the_admin_api_redacted_and_falls_back_to_the_caddyfile()
    {
        var caddy = new CaddyModule(new CaddyOptions(ModuleMode.Read, null, Admin), AdminWeb());
        var c = await caddy.ConfigAsync(CancellationToken.None);
        Assert.Equal("admin", c.Source);
        Assert.DoesNotContain("GEHEIM", c.Text);

        var file = Path.Combine(_dir.FullName, "Caddyfile");
        await File.WriteAllTextAsync(file, "app.example.test {\n  reverse_proxy web:3000\n  header Authorization \"Bearer GEHEIM\"\n}\n");
        var fallback = await new CaddyModule(new CaddyOptions(ModuleMode.Read, file, Admin), new FakeServiceWeb([]) { Throw = new HttpRequestException("x") }).ConfigAsync(CancellationToken.None);
        Assert.Equal("caddyfile", fallback.Source);
        Assert.NotNull(fallback.Warning);
        Assert.DoesNotContain("GEHEIM", fallback.Text);
    }

    [Fact]
    public async Task Certificates_are_probed_for_listed_domains_only_and_remembered_for_five_minutes()
    {
        var probed = new List<string>();
        var time = new TestTime(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var caddy = new CaddyModule(new CaddyOptions(ModuleMode.Read, null, Admin, TlsConnect: new UpstreamTarget("caddy", 443)), AdminWeb(), time: time,
            tls: (d, c, _) => { lock (probed) probed.Add($"{d}@{c?.Host}:{c?.Port}"); return Task.FromResult(new CertInfo(d, "warn", "Let's Encrypt", time.GetUtcNow().AddDays(9), 9, true, null)); });
        var certs = await caddy.CertificatesAsync(CancellationToken.None);
        Assert.Equal(["app.example.test@caddy:443"], probed);                 // der Platzhalter wird nicht geprueft
        Assert.Equal("warn", certs.Certificates[0].State);
        Assert.Null(certs.Ca);                                                  // die interne CA gibt es hier nicht (404)
        await caddy.CertificatesAsync(CancellationToken.None);
        Assert.Single(probed);
        time.Advance(TimeSpan.FromMinutes(6));
        await caddy.CertificatesAsync(CancellationToken.None);
        Assert.Equal(2, probed.Count);
    }
}
