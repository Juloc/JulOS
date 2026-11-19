using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using JulOS.Backend.Modules;

namespace JulOS.Backend.Tests;

/// <summary>Caddy und DDNS, nur lesen: Parser, Geheimnisse, feste Abrufe, Pruefung nur gelisteter Ziele, Status. Mit Fakes, ohne echte Systeme.</summary>
public sealed class ModuleTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("julos-modules-");
    public void Dispose() => _dir.Delete(true);

    private const string Sample = """
        # Globale Optionen
        {
            email admin@example.test
            acme_dns cloudflare GEHEIMES-TOKEN-0
        }
        (gemeinsam) {
            header X-Secret GEHEIMES-TOKEN-1
        }
        app.example.test {
            import gemeinsam
            tls {
                dns cloudflare {env.CF_TOKEN}
            }
            basic_auth {
                jul $2a$14$GEHEIMER-HASH-2
            }
            handle /api/* {
                reverse_proxy http://api:8080 {
                    header_up Authorization "Bearer GEHEIMES-TOKEN-3"
                }
            }
            handle {
                reverse_proxy user:GEHEIMES-PASSWORT-4@web:3000?token=GEHEIMES-TOKEN-5
            }
        }
        files.example.test, www.files.example.test {
            root * /srv/files
            file_server
        }
        https://alt.example.test {
            redir https://neu.example.test{uri} permanent
        }
        stille.example.test {
            respond "GEHEIMER-TEXT-6" 503
        }
        wild.example.test {
            @intern path /intern/*
            encode gzip
        }
        """;

    private static string All(IEnumerable<CaddyRoute> routes) => JsonSerializer.Serialize(routes);

    [Fact]
    public void The_caddyfile_gives_domain_and_target_and_nothing_secret()
    {
        var r = CaddyfileParser.Parse(Sample);
        Assert.Contains(r, x => x is { Domain: "app.example.test", Path: "/api/*", Kind: "proxy" } && x.Targets.SequenceEqual(["http://api:8080"]));
        // Benutzer:Passwort@ und Query fallen weg
        Assert.Contains(r, x => x is { Domain: "app.example.test", Path: null, Kind: "proxy" } && x.Targets.SequenceEqual(["web:3000"]));
        Assert.Contains(r, x => x is { Domain: "files.example.test", Kind: "files" } && x.Targets.SequenceEqual(["/srv/files"]));
        Assert.Contains(r, x => x is { Domain: "www.files.example.test", Kind: "files" });
        Assert.Contains(r, x => x is { Domain: "alt.example.test", Kind: "redirect" } && x.Targets.SequenceEqual(["https://neu.example.test{uri}"]));
        Assert.Contains(r, x => x is { Domain: "stille.example.test", Kind: "respond", Detail: "503" });
        Assert.Contains(r, x => x is { Domain: "wild.example.test", Kind: "other" });
        // globale Optionen und Snippets sind keine Domains
        Assert.DoesNotContain(r, x => x.Domain.Contains("gemeinsam") || x.Domain.Contains("example.test}"));
        Assert.Equal(7, r.Length);
        Assert.DoesNotContain("GEHEIM", All(r));
        Assert.DoesNotContain("CF_TOKEN", All(r));
    }

    [Fact]
    public void A_caddyfile_with_one_site_and_no_braces_works_and_junk_does_not_crash()
    {
        var one = CaddyfileParser.Parse("einzeln.example.test\nreverse_proxy 127.0.0.1:9000\n");
        var row = one.Single();
        Assert.Equal(("einzeln.example.test", "proxy"), (row.Domain, row.Kind));
        Assert.Equal(["127.0.0.1:9000"], row.Targets);
        Assert.Empty(CaddyfileParser.Parse(""));
        foreach (var junk in new[] { "}", "{", "a {\n b {\n", "a { } }", "\"unfertig", "reverse_proxy", "a {\n respond \"}\" \n}", string.Concat(Enumerable.Repeat("x {\n", 2000)) })
            _ = CaddyfileParser.Parse(junk);
        // zu tief verschachtelt: kein Absturz, begrenzt
        var deep = "tief.example.test {\n" + string.Concat(Enumerable.Repeat("handle {\n", 40)) + "reverse_proxy x:1\n" + string.Concat(Enumerable.Repeat("}\n", 40)) + "}";
        Assert.DoesNotContain(CaddyfileParser.Parse(deep), x => x.Kind == "proxy");
    }

    private const string AdminJson = """
        {"srv0":{"listen":[":443"],"routes":[
          {"match":[{"host":["app.example.test"]}],"handle":[{"handler":"subroute","routes":[
              {"match":[{"path":["/api/*"]}],"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"api:8080"}],"headers":{"request":{"set":{"Authorization":["Bearer GEHEIMES-TOKEN"]}}}}]},
              {"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"web:3000"},{"dial":"web2:3000"}]}]}]}]},
          {"match":[{"host":["alt.example.test"]}],"handle":[{"handler":"static_response","headers":{"Location":["https://neu.example.test/?k=GEHEIMES-TOKEN"]},"status_code":301}]},
          {"match":[{"host":["f.example.test"]}],"handle":[{"handler":"vars","root":"/srv"},{"handler":"file_server"}]},
          {"handle":[{"handler":"static_response","status_code":404}]}
        ],"tls_connection_policies":[{"client_authentication":{"ca":{"pem":"GEHEIMES-TOKEN"}}}]}}
        """;

    [Fact]
    public void The_admin_config_gives_domain_and_target_and_nothing_secret()
    {
        using var doc = JsonDocument.Parse(AdminJson);
        var r = CaddyAdminConfig.Parse(doc.RootElement);
        Assert.Contains(r, x => x is { Domain: "app.example.test", Path: "/api/*", Kind: "proxy" } && x.Targets.SequenceEqual(["api:8080"]));
        Assert.Contains(r, x => x is { Domain: "app.example.test", Path: null, Kind: "proxy" } && x.Targets.SequenceEqual(["web:3000", "web2:3000"]));
        Assert.Contains(r, x => x is { Domain: "alt.example.test", Kind: "redirect" } && x.Targets.SequenceEqual(["https://neu.example.test/"]));
        Assert.Contains(r, x => x is { Domain: "f.example.test", Kind: "files" });
        Assert.Contains(r, x => x is { Domain: "*", Kind: "respond", Detail: "404" });
        Assert.DoesNotContain("GEHEIM", All(r));
        foreach (var junk in new[] { "[]", "null", "{\"a\":1}", "{\"s\":{\"routes\":5}}", "{\"s\":{\"routes\":[1,{\"handle\":7},{\"handle\":[{\"handler\":3}]}]}}" })
        {
            using var d = JsonDocument.Parse(junk);
            _ = CaddyAdminConfig.Parse(d.RootElement);
        }
    }

    [Theory]
    [InlineData("localhost:3000", "localhost", 3000)]
    [InlineData("web", "web", 80)]
    [InlineData("https://web", "web", 443)]
    [InlineData("http://web:8080/pfad", "web", 8080)]
    [InlineData(":9000", "localhost", 9000)]
    [InlineData("[::1]:8000", "::1", 8000)]
    [InlineData("h2c://dienst:9", "dienst", 9)]
    [InlineData("10.0.0.5:22", "10.0.0.5", 22)]
    public void Upstream_targets_are_split(string text, string host, int port) => Assert.Equal(new UpstreamTarget(host, port), UpstreamTarget.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("unix//run/app.sock")]
    [InlineData("unix+h2c//run/app.sock")]
    [InlineData("{http.request.host}:80")]
    [InlineData("web:0")]
    [InlineData("web:70000")]
    [InlineData("web:80-90")]
    [InlineData("a b:80")]
    public void Unusable_upstream_targets_are_not_probed(string text) => Assert.Null(UpstreamTarget.Parse(text));

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("::", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("::1", false)]
    public void Addresses_that_are_never_a_service_in_the_own_network_are_refused(string ip, bool forbidden) =>
        Assert.Equal(forbidden, UpstreamTarget.IsForbidden(IPAddress.Parse(ip)));

    [Fact]
    public void Secrets_are_removed_from_targets()
    {
        Assert.Equal("https://web:8443/x", CaddyRedact.Target("https://jul:pw@web:8443/x?token=abc#frag"));
        Assert.Equal("web:80", CaddyRedact.Target("jul:pw@web:80"));
        Assert.True(CaddyRedact.Target(new string('a', 500)).Length < 210);
    }

    /* ---------- Einstellungen ---------- */

    private static ModuleSettings Settings(params (string, string)[] kv) => ModuleSettings.From(k => kv.FirstOrDefault(x => x.Item1 == k).Item2);

    [Fact]
    public void Everything_is_off_by_default_and_bad_values_keep_it_off()
    {
        var s = Settings();
        Assert.Equal(ModuleMode.Off, s.Caddy.Mode);
        Assert.Equal(ModuleMode.Off, s.Ddns.Mode);
        Assert.Empty(s.Warnings);
        var bad = Settings(("JULOS_CADDY", "an"), ("JULOS_DDNS", "write"));
        Assert.Equal(ModuleMode.Off, bad.Caddy.Mode);
        Assert.Equal(ModuleMode.Off, bad.Ddns.Mode);
        Assert.Equal(2, bad.Warnings.Length);
        // Einstellungen eines ausgeschalteten Moduls machen keine Meldung
        Assert.Empty(Settings(("JULOS_DDNS_HOSTS", "kein name"), ("JULOS_CADDY_ADMIN_URL", "quatsch")).Warnings);
    }

    [Fact]
    public void Control_exists_only_where_it_is_built_and_elsewhere_counts_as_read()
    {
        var s = Settings(("JULOS_CADDY", "control"), ("JULOS_DDNS", "READ"), ("JULOS_SERVICES", "control"));
        Assert.Equal(ModuleMode.Read, s.Caddy.Mode);      // Caddy schreibt nicht (nur als Entwurf dokumentiert)
        Assert.Equal(ModuleMode.Read, s.Ddns.Mode);
        Assert.Equal(ModuleMode.Control, Settings(("JULOS_DDNS", "control")).Ddns.Mode);
        Assert.Equal(ModuleMode.Control, s.Services.Mode);
        Assert.Contains("Schreiben gibt es hier nicht", Assert.Single(s.Warnings));
        Assert.Equal(ModuleMode.Off, Settings().Services.Mode);
    }

    [Fact]
    public void Caddy_reads_credentials_tls_target_and_log_from_the_settings()
    {
        var s = Settings(("JULOS_CADDY", "read"), ("JULOS_CADDY_ADMIN_URL", "http://caddy:2019"), ("JULOS_CADDY_ADMIN_AUTH_FILE", " /run/secrets/caddy-admin "), ("JULOS_CADDY_TLS_CONNECT", "caddy:443"), ("JULOS_CADDY_LOG_FILE", "/var/log/caddy/access.log"));
        Assert.Equal("/run/secrets/caddy-admin", s.Caddy.AdminAuthFile);
        Assert.Equal(("caddy", 443), (s.Caddy.TlsConnect!.Host, s.Caddy.TlsConnect.Port));
        Assert.Equal("/var/log/caddy/access.log", s.Caddy.LogFile);
        Assert.Empty(s.Warnings);
        var bad = Settings(("JULOS_CADDY", "read"), ("JULOS_CADDY_TLS_CONNECT", "kein host port"));
        Assert.Null(bad.Caddy.TlsConnect);
        Assert.Single(bad.Warnings);
        Assert.Empty(Settings(("JULOS_CADDY_TLS_CONNECT", "kein host port")).Warnings);   // aus: keine Meldung
    }

    [Fact]
    public void Addresses_are_checked_when_the_modules_are_on()
    {
        var s = Settings(("JULOS_CADDY", "read"), ("JULOS_DDNS", "read"),
            ("JULOS_CADDY_ADMIN_URL", "http://user:pw@caddy-filter:2020"), ("JULOS_DDNS_FRITZBOX_URL", "ftp://router"), ("JULOS_DDNS_IP_URL", "https://ip.example.test/"));
        Assert.Null(s.Caddy.AdminUrl);
        Assert.Null(s.Ddns.RouterUrl);
        Assert.NotNull(s.Ddns.LookupUrl);
        Assert.Equal(2, s.Warnings.Length);
        Assert.DoesNotContain(s.Warnings, w => w.Contains("pw"));
        Assert.Null(Settings(("JULOS_CADDY", "read"), ("JULOS_CADDY_ADMIN_URL", "http://caddy:2020/config/")).Caddy.AdminUrl);
        Assert.NotNull(Settings(("JULOS_CADDY", "read"), ("JULOS_CADDY_ADMIN_URL", "http://caddy-filter:2020")).Caddy.AdminUrl);
        // beide Quellen: der Router hat Vorrang
        var both = Settings(("JULOS_DDNS", "read"), ("JULOS_DDNS_FRITZBOX_URL", "http://192.0.2.1:49000"), ("JULOS_DDNS_IP_URL", "https://ip.example.test/"));
        Assert.NotNull(both.Ddns.RouterUrl);
        Assert.Null(both.Ddns.LookupUrl);
    }

    [Fact]
    public void Ddns_host_names_are_validated_and_limited()
    {
        var w = new List<string>();
        var hosts = DdnsOptions.ParseHosts("Home.Example.Test, home.example.test; 192.0.2.1 ../x a_b.example.test kurz", w);
        Assert.Equal(["home.example.test", "a_b.example.test"], hosts);
        Assert.Equal(3, w.Count);
        w.Clear();
        Assert.Equal(DdnsOptions.MaxHosts, DdnsOptions.ParseHosts(string.Join(",", Enumerable.Range(0, 30).Select(i => $"h{i}.example.test")), w).Length);
    }

    /* ---------- Caddy lesen ---------- */

    private string Caddyfile(string text, string name = "Caddyfile") { var p = Path.Combine(_dir.FullName, name); File.WriteAllText(p, text); return p; }
    private static CaddyOptions Opts(string? file = null, string? admin = null) => new(ModuleMode.Read, file, admin is null ? null : new Uri(admin));

    [Fact]
    public async Task The_admin_api_is_read_with_exactly_one_fixed_get_request()
    {
        var fake = new FakeCaddyAdmin(AdminJson);
        var caddy = new CaddyModule(Opts(admin: "http://caddy-filter:2020"), fake);
        var l = await caddy.ReadAsync(CancellationToken.None);
        Assert.Equal("admin", l.Source);
        Assert.Empty(l.Warnings);
        Assert.NotEmpty(l.Routes);
        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://caddy-filter:2020/config/apps/http/servers", request.Url);
    }

    [Fact]
    public async Task When_the_admin_api_fails_the_caddyfile_is_shown_with_a_warning()
    {
        var file = Caddyfile("a.example.test {\n reverse_proxy x:1\n}\n");
        foreach (var fake in new[] { new FakeCaddyAdmin("{}", HttpStatusCode.Forbidden), new FakeCaddyAdmin("kein json"), new FakeCaddyAdmin(new string(' ', CaddyModule.MaxAdminBytes + 10) + "{}") })
        {
            var l = await new CaddyModule(Opts(file, "http://caddy-filter:2020"), fake).ReadAsync(CancellationToken.None);
            Assert.Equal("caddyfile", l.Source);
            Assert.Single(l.Warnings);
            Assert.Equal("a.example.test", l.Routes.Single().Domain);
        }
    }

    [Fact]
    public async Task Nothing_readable_gives_source_none_and_a_reason_without_the_path()
    {
        var none = await new CaddyModule(Opts()).ReadAsync(CancellationToken.None);
        Assert.Equal("none", none.Source);
        Assert.Single(none.Warnings);

        var missing = await new CaddyModule(Opts(Path.Combine(_dir.FullName, "gibt-es-nicht"))).ReadAsync(CancellationToken.None);
        Assert.Equal("none", missing.Source);
        Assert.DoesNotContain(_dir.FullName, string.Concat(missing.Warnings));

        var big = Caddyfile(new string('#', CaddyModule.MaxFileBytes + 1), "gross");
        Assert.Equal("none", (await new CaddyModule(Opts(big)).ReadAsync(CancellationToken.None)).Source);
    }

    [Fact]
    public async Task The_caddyfile_is_only_read()
    {
        var file = Caddyfile("a.example.test {\n reverse_proxy x:1\n}\n");
        var before = File.ReadAllBytes(file);
        var stamp = File.GetLastWriteTimeUtc(file);
        await new CaddyModule(Opts(file)).ReadAsync(CancellationToken.None);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }

    /* ---------- Erreichbarkeit ---------- */

    [Fact]
    public async Task Only_upstreams_from_the_config_are_probed_each_once()
    {
        var file = Caddyfile("""
            a.example.test {
                reverse_proxy web:3000 web:3000 unix//run/x.sock {args}
                handle /x {
                    reverse_proxy db:5432
                }
            }
            b.example.test {
                reverse_proxy web:3000
                file_server
                redir https://woanders.example.test:9/
            }
            """);
        var asked = new List<UpstreamTarget>();
        var caddy = new CaddyModule(Opts(file), connect: (t, _) => { lock (asked) asked.Add(t); return Task.FromResult(t.Port == 5432 ? "down" : "up"); });
        var r = await caddy.CheckAsync(CancellationToken.None);
        // woanders (Weiterleitung) und das Dateiziel sind keine Upstreams: nie geprueft
        Assert.Equal(["db:5432", "unix//run/x.sock", "web:3000", "{args}"], r.Upstreams.Select(u => u.Target).Order());
        Assert.Equal(["db:5432", "web:3000"], asked.Select(a => $"{a.Host}:{a.Port}").Order());
        Assert.Equal("down", r.Upstreams.Single(u => u.Target == "db:5432").State);
        Assert.Equal("up", r.Upstreams.Single(u => u.Target == "web:3000").State);
        Assert.Equal("skipped", r.Upstreams.Single(u => u.Target == "unix//run/x.sock").State);
        Assert.Equal("skipped", r.Upstreams.Single(u => u.Target == "{args}").State);
    }

    [Fact]
    public async Task The_number_of_probes_is_limited()
    {
        var sites = string.Concat(Enumerable.Range(0, 100).Select(i => $"s{i}.example.test {{\n reverse_proxy h{i}:80\n}}\n"));
        var count = 0;
        var r = await new CaddyModule(Opts(Caddyfile(sites)), connect: (_, _) => { Interlocked.Increment(ref count); return Task.FromResult("up"); }).CheckAsync(CancellationToken.None);
        Assert.Equal(CaddyModule.MaxUpstreams, r.Upstreams.Length);
        Assert.Equal(CaddyModule.MaxUpstreams, count);
    }

    [Fact]
    public async Task The_real_connector_only_connects_and_refuses_metadata_addresses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Equal("up", await CaddyModule.TcpConnectAsync(new UpstreamTarget("127.0.0.1", port), CancellationToken.None));
        listener.Stop();
        Assert.Equal("down", await CaddyModule.TcpConnectAsync(new UpstreamTarget("127.0.0.1", port), CancellationToken.None));
        Assert.Equal("skipped", await CaddyModule.TcpConnectAsync(new UpstreamTarget("169.254.169.254", 80), CancellationToken.None));
        Assert.Equal("skipped", await CaddyModule.TcpConnectAsync(new UpstreamTarget("0.0.0.0", 80), CancellationToken.None));
    }

    /* ---------- DDNS ---------- */

    private static DdnsOptions Ddns(params string[] hosts) => new(ModuleMode.Read, hosts, null, null, DdnsOptions.DefaultCache);
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Ddns_is_synced_when_every_name_points_to_the_public_ip()
    {
        var dns = new FakeResolver(new() { ["a.example.test"] = ["203.0.113.7"], ["b.example.test"] = ["198.51.100.1", "203.0.113.7"] });
        var s = await new DdnsMonitor(Ddns("a.example.test", "b.example.test"), new FakeIp("203.0.113.7"), dns, new TestTime(T0)).GetAsync(CancellationToken.None);
        Assert.Equal("synced", s.State);
        Assert.Equal("203.0.113.7", s.PublicIp);
        Assert.Equal(T0, s.CheckedAt);
        Assert.All(s.Hosts, h => Assert.Equal("synced", h.State));
        Assert.Null(s.Message);
    }

    [Fact]
    public async Task Ddns_is_not_synced_when_a_name_points_elsewhere_or_does_not_resolve()
    {
        var dns = new FakeResolver(new() { ["a.example.test"] = ["203.0.113.7"], ["alt.example.test"] = ["203.0.113.99"], ["v6.example.test"] = ["2001:db8::1"] });
        var s = await new DdnsMonitor(Ddns("a.example.test", "alt.example.test", "weg.example.test", "v6.example.test"), new FakeIp("::ffff:203.0.113.7"), dns, new TestTime(T0)).GetAsync(CancellationToken.None);
        Assert.Equal("notSynced", s.State);
        Assert.Equal("203.0.113.7", s.PublicIp);
        Assert.Equal(["synced", "notSynced", "unresolved", "notSynced"], s.Hosts.Select(h => h.State));
        Assert.Equal(["203.0.113.99"], s.Hosts[1].Addresses);
        Assert.Empty(s.Hosts[2].Addresses);
    }

    [Fact]
    public async Task Ddns_without_an_ip_is_unknown_but_still_shows_what_the_names_resolve_to()
    {
        var dns = new FakeResolver(new() { ["a.example.test"] = ["203.0.113.7"] });
        var none = await new DdnsMonitor(Ddns("a.example.test"), null, dns, new TestTime(T0)).GetAsync(CancellationToken.None);
        Assert.Equal("unknown", none.State);
        Assert.Contains("Quelle", none.Message);
        Assert.Equal("unknown", none.Hosts.Single().State);
        Assert.Equal(["203.0.113.7"], none.Hosts.Single().Addresses);

        var down = await new DdnsMonitor(Ddns("a.example.test"), new FakeIp(null), dns, new TestTime(T0)).GetAsync(CancellationToken.None);
        Assert.Equal("unknown", down.State);
        Assert.Null(down.PublicIp);
        Assert.Contains("nicht ermittelt", down.Message);

        var empty = await new DdnsMonitor(Ddns(), new FakeIp("203.0.113.7"), dns, new TestTime(T0)).GetAsync(CancellationToken.None);
        Assert.Equal("unknown", empty.State);
        Assert.Contains("JULOS_DDNS_HOSTS", empty.Message);
    }

    [Fact]
    public async Task A_private_address_from_the_source_is_flagged()
    {
        var dns = new FakeResolver(new() { ["a.example.test"] = ["100.64.0.5"] });
        var s = await new DdnsMonitor(Ddns("a.example.test"), new FakeIp("100.64.0.5"), dns, new TestTime(T0)).GetAsync(CancellationToken.None);
        Assert.Contains("CGNAT", s.Message);
    }

    [Fact]
    public async Task Ddns_measures_at_most_once_per_minute_and_not_before_someone_asks()
    {
        var ip = new FakeIp("203.0.113.7");
        var dns = new FakeResolver(new() { ["a.example.test"] = ["203.0.113.7"] });
        var time = new TestTime(T0);
        var monitor = new DdnsMonitor(Ddns("a.example.test"), ip, dns, time);
        Assert.Equal(0, ip.Calls);
        var first = await monitor.GetAsync(CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => monitor.GetAsync(CancellationToken.None)));
        Assert.Equal(1, ip.Calls);
        Assert.Equal(1, dns.Calls);
        time.Advance(TimeSpan.FromSeconds(61));
        var again = await monitor.GetAsync(CancellationToken.None);
        Assert.Equal(2, ip.Calls);
        Assert.True(again.CheckedAt > first.CheckedAt);
    }

    /* ---------- IP-Quellen ---------- */

    private const string SoapAnswer = """
        <?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
        <u:GetExternalIPAddressResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1"><NewExternalIPAddress>203.0.113.7</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>
        """;

    [Fact]
    public async Task The_router_is_asked_with_one_fixed_upnp_call_and_nothing_is_written()
    {
        var web = new FakeWeb(SoapAnswer);
        var ip = await new RouterIpSource(OutboundHttp.Create(web), new Uri("http://192.0.2.1:49000")).GetAsync(CancellationToken.None);
        Assert.Equal("203.0.113.7", ip.ToString());
        var req = Assert.Single(web.Requests);
        Assert.Equal("http://192.0.2.1:49000/igdupnp/control/WANIPConn1", req.RequestUri!.ToString());
        Assert.Equal("\"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"", req.Headers.GetValues("SOAPAction").Single());
        Assert.Contains("GetExternalIPAddress", web.Bodies[0]);
    }

    [Fact]
    public void Router_answers_that_are_not_an_address_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => RouterIpSource.ParseResponse("<a><NewExternalIPAddress>kein-ip</NewExternalIPAddress></a>"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => RouterIpSource.ParseResponse("<a/>"u8.ToArray()));
        // eine DTD (externe Entitaeten) wird gar nicht erst verarbeitet
        Assert.ThrowsAny<System.Xml.XmlException>(() => RouterIpSource.ParseResponse("""<!DOCTYPE a [<!ENTITY x SYSTEM "file:///etc/passwd">]><a><NewExternalIPAddress>&x;</NewExternalIPAddress></a>"""u8.ToArray()));
    }

    [Fact]
    public async Task The_lookup_service_is_one_get_and_answers_plain_text()
    {
        var web = new FakeWeb("203.0.113.7\n");
        Assert.Equal("203.0.113.7", (await new UrlIpSource(OutboundHttp.Create(web), new Uri("https://ip.example.test/")).GetAsync(CancellationToken.None)).ToString());
        Assert.Equal(HttpMethod.Get, Assert.Single(web.Requests).Method);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UrlIpSource(OutboundHttp.Create(new FakeWeb("<html>Fehler</html>")), new Uri("https://ip.example.test/")).GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UrlIpSource(OutboundHttp.Create(new FakeWeb("203.0.113.7", HttpStatusCode.InternalServerError)), new Uri("https://ip.example.test/")).GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UrlIpSource(OutboundHttp.Create(new FakeWeb(new string('1', 5000))), new Uri("https://ip.example.test/")).GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        // der echte Handler (AllowAutoRedirect=false): eine Weiterleitung ist ein Fehler, kein Abruf einer anderen Adresse
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[4096];
            _ = await s.ReadAsync(buf);
            await s.WriteAsync("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/geheim\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new UrlIpSource(OutboundHttp.Create(), new Uri($"http://127.0.0.1:{port}/")).GetAsync(CancellationToken.None));
        await server;
    }
}
