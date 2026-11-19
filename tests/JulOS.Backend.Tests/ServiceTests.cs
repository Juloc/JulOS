using System.Net;
using System.Text.Json;
using JulOS.Backend.Modules;

namespace JulOS.Backend.Tests;

/// <summary>Modul "Dienste": Eingaben, Feldpfade, Erlaubnisliste, Abfragen mit Fakes (Zustand, Kennzahlen, Schluessel nur an die richtige Adresse, Fristen, Grenzen).</summary>
public sealed class ServiceTests
{
    private static readonly Func<string, bool> Any = _ => true;

    private static (ServiceInput? Input, string? Error) Validate(string name = "Jularr", string? health = "http://jularr.lan:8080/health/ready", string? metrics = null, IReadOnlyList<ServiceMetric>? list = null,
        int? interval = null, string? header = null, string? icon = null, string? open = "http://jularr.lan:8080/", Func<string, bool>? allowed = null) =>
        ServiceRules.Validate(name, icon, open, health, metrics, list, interval, header, null, allowed ?? Any);

    private static ServiceRecord Rec(string health = "", string metrics = "", ServiceMetric[]? list = null, string header = "", bool hasKey = false, int interval = 60) =>
        new(Guid.NewGuid(), "Test", "globe", "", health, metrics, list ?? [], interval, header, hasKey, "");

    /* ---------- Eingaben ---------- */

    [Fact]
    public void A_valid_service_is_accepted_and_cleaned()
    {
        var (input, error) = Validate(" Jularr ", metrics: "http://jularr.lan:8080/api/v1/x", list: [new("a.b[0].c", " Aktiv ", "", "")], header: "Authorization");
        Assert.Null(error);
        Assert.Equal("Jularr", input!.Name);
        Assert.Equal("globe", input.Icon);
        Assert.Equal(60, input.IntervalSeconds);
        Assert.Equal(new ServiceMetric("a.b[0].c", "Aktiv", "", "plain"), Assert.Single(input.Metrics));
    }

    [Theory]
    [InlineData("", "Der Name muss")]
    [InlineData("ftp://x", "Adresse ist ungültig")]
    [InlineData("http://jul:pw@host/x", "Adresse ist ungültig")]
    [InlineData("javascript:alert(1)", "Adresse ist ungültig")]
    public void Names_and_addresses_are_checked(string value, string message)
    {
        var bad = value.Length == 0 ? Validate(name: value) : Validate(health: value);
        Assert.Contains(message, bad.Error);
    }

    [Fact]
    public void Open_address_is_checked_for_the_scheme_only_and_never_asked_by_the_server()
    {
        Assert.Null(Validate(open: "https://internet.example.test/", allowed: h => h == "jularr.lan").Error);   // nur zum Oeffnen im Browser
        Assert.Contains("Adresse ist ungültig", Validate(open: "ftp://x").Error);
        Assert.Contains("nicht erlaubt", Validate(health: "http://other.example.test/", allowed: h => h == "jularr.lan").Error);
        Assert.Contains("nicht erlaubt", Validate(health: "http://jularr.lan/", metrics: "http://other.example.test/x", allowed: h => h == "jularr.lan").Error);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("content-length")]
    [InlineData("Cookie")]
    [InlineData("X Bad")]
    public void Dangerous_header_names_are_refused(string header) =>
        Assert.Contains("Kopfzeile", Validate(header: header).Error);

    [Fact]
    public void Interval_icon_format_and_counts_are_limited()
    {
        Assert.Contains("Intervall", Validate(interval: 5).Error);
        Assert.Contains("Intervall", Validate(interval: 7200).Error);
        Assert.Null(Validate(interval: 10).Error);
        Assert.Contains("Symbol", Validate(icon: "<script>").Error);
        Assert.Contains("Darstellung", Validate(metrics: "http://a.lan/", list: [new("a", "A", "", "bogus")]).Error);
        Assert.Contains("Feldpfad", Validate(metrics: "http://a.lan/", list: [new("a b", "A", "", "")]).Error);
        Assert.Contains("Feldpfad", Validate(metrics: "http://a.lan/", list: [new("a.b.c.d.e.f.g.h.i.j.k.l.m", "A", "", "")]).Error);
        Assert.Contains("Zu viele Kennzahlen", Validate(metrics: "http://a.lan/", list: [.. Enumerable.Range(0, 13).Select(i => new ServiceMetric("a" + i, "A", "", ""))]).Error);
        Assert.Contains("fehlt die Adresse", Validate(metrics: null, list: [new("a", "A", "", "")]).Error);
    }

    /* ---------- Feldpfade ---------- */

    private static MetricValue? Pick(string json, string path) { using var d = JsonDocument.Parse(json); return ServiceRules.Extract(d.RootElement, new ServiceMetric(path, "X", "u", "plain")); }

    [Fact]
    public void Field_paths_read_numbers_text_lists_and_counts()
    {
        const string doc = """{"a":{"b":[{"c":7.5},{"c":"ok"}],"flag":true,"n":null},"items":[1,2,3],"obj":{"x":1,"y":2}}""";
        Assert.Equal(7.5, Pick(doc, "a.b[0].c")!.Number);
        Assert.Equal(7.5, Pick(doc, "a.b.0.c")!.Number);
        Assert.Equal("ok", Pick(doc, "a.b.1.c")!.Text);
        Assert.Equal("true", Pick(doc, "a.flag")!.Text);
        Assert.Equal(3, Pick(doc, "items.#")!.Number);
        Assert.Equal(2, Pick(doc, "obj.#")!.Number);
        Assert.Null(Pick(doc, "a.missing"));
        Assert.Null(Pick(doc, "items.9"));
        Assert.Null(Pick(doc, "a.n"));        // null ist keine Kennzahl
        Assert.Null(Pick(doc, "obj"));        // ein Objekt auch nicht
        Assert.Equal("u", Pick(doc, "items.0")!.Unit);
    }

    /* ---------- Erlaubnisliste ---------- */

    [Fact]
    public void Without_a_list_only_the_own_network_is_reachable_and_with_a_list_only_its_names()
    {
        var open = new ServiceHosts(null, null);
        Assert.True(open.Allows("jularr.lan"));
        Assert.True(open.Allows("192.168.1.20"));
        Assert.False(open.Allows("8.8.8.8"));
        Assert.True(open.AllowsAddress("jularr.lan", IPAddress.Parse("192.168.1.20")));
        Assert.True(open.AllowsAddress("localhost", IPAddress.Loopback));
        Assert.True(open.AllowsAddress("x", IPAddress.Parse("100.64.0.5")));
        Assert.False(open.AllowsAddress("example.test", IPAddress.Parse("93.184.216.34")));     // ein Name, der oeffentlich aufloest

        var listed = new ServiceHosts("status.example.test, 10.0.0.5", "other.example.test");   // die eigene Liste gilt, die allgemeine nicht
        Assert.True(listed.Restricted);
        Assert.True(listed.Allows("status.example.test"));
        Assert.False(listed.Allows("other.example.test"));
        Assert.True(listed.AllowsAddress("status.example.test", IPAddress.Parse("93.184.216.34")));   // auf der Liste: auch oeffentlich
        Assert.False(listed.AllowsAddress("evil.example.test", IPAddress.Parse("10.0.0.5")));
        Assert.True(new ServiceHosts(null, "general.lan").Allows("general.lan"));                     // ohne eigene Liste gilt JULOS_ALLOWED_HOSTS
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("fe80::1")]
    public void Metadata_and_unspecified_addresses_are_never_reachable_even_when_listed(string address)
    {
        var listed = new ServiceHosts("meta.example.test," + address, null);
        Assert.False(listed.AllowsAddress("meta.example.test", IPAddress.Parse(address)));
        Assert.False(new ServiceHosts(null, null).AllowsAddress("x", IPAddress.Parse(address)));
    }

    /* ---------- Abfragen ---------- */

    private static ServiceChecker Checker(FakeServiceWeb web, TimeProvider? time = null) => new(new ServiceHosts("jularr.lan,other.lan", null), web, time);
    private static Task<string?> NoKey(CancellationToken _) => Task.FromResult<string?>(null);

    [Fact]
    public async Task Health_and_metrics_give_a_green_state_with_values_and_the_key_goes_only_where_it_belongs()
    {
        var web = new FakeServiceWeb(new()
        {
            ["http://jularr.lan/health/ready"] = (HttpStatusCode.OK, "Healthy"),
            ["http://jularr.lan/api/summary"] = (HttpStatusCode.OK, """{"activeCount":3,"bytesPerSecond":1048576,"name":"x"}"""),
        });
        var s = Rec(health: "http://jularr.lan/health/ready", metrics: "http://jularr.lan/api/summary", header: "Authorization", hasKey: true,
            list: [new("activeCount", "Aktiv", "", "plain"), new("bytesPerSecond", "Tempo", "", "rate"), new("fehlt", "Fehlt", "", "plain")]);
        var status = await Checker(web).CheckAsync(s, _ => Task.FromResult<string?>("Bearer wegwerf-wert"), false, CancellationToken.None);

        Assert.Equal("up", status.State);
        Assert.Equal(200, status.HttpStatus);
        Assert.Null(status.Error);
        Assert.Equal(new double?[] { 3, 1048576, null }, status.Metrics.Select(m => m.Number).ToArray());
        Assert.Equal(2, web.Requests.Count);
        Assert.All(web.Requests, r => Assert.Equal("Bearer wegwerf-wert", r.Headers["Authorization"]));      // gleicher Ursprung: beide bekommen ihn
        Assert.DoesNotContain("wegwerf", JsonSerializer.Serialize(status.Metrics.Select(m => m.Text)));
    }

    [Fact]
    public async Task The_key_is_not_sent_to_a_health_address_of_another_origin()
    {
        var web = new FakeServiceWeb(new() { ["http://other.lan/ping"] = (HttpStatusCode.OK, ""), ["http://jularr.lan/m"] = (HttpStatusCode.OK, "{}") });
        var s = Rec(health: "http://other.lan/ping", metrics: "http://jularr.lan/m", header: "X-Api-Key", hasKey: true, list: [new("a", "A", "", "")]);
        await Checker(web).CheckAsync(s, _ => Task.FromResult<string?>("geheim"), false, CancellationToken.None);
        Assert.False(web.Requests.Single(r => r.Url.Contains("other.lan")).Headers.ContainsKey("X-Api-Key"));
        Assert.Equal("geheim", web.Requests.Single(r => r.Url.Contains("jularr.lan")).Headers["X-Api-Key"]);
    }

    [Fact]
    public async Task Only_a_health_address_gets_the_key_when_there_are_no_metrics()
    {
        var web = new FakeServiceWeb(new() { ["http://jularr.lan/h"] = (HttpStatusCode.OK, "") });
        await Checker(web).CheckAsync(Rec(health: "http://jularr.lan/h", header: "X-Api-Key", hasKey: true), _ => Task.FromResult<string?>("k"), false, CancellationToken.None);
        Assert.Equal("k", Assert.Single(web.Requests).Headers["X-Api-Key"]);
    }

    [Fact]
    public async Task A_failing_service_is_red_without_leaking_what_it_said()
    {
        var web = new FakeServiceWeb(new() { ["http://jularr.lan/h"] = (HttpStatusCode.InternalServerError, "Stacktrace mit geheimem Wert") });
        var status = await Checker(web).CheckAsync(Rec(health: "http://jularr.lan/h"), NoKey, false, CancellationToken.None);
        Assert.Equal("down", status.State);
        Assert.Equal("status", status.Error);
        Assert.Equal(500, status.HttpStatus);
        Assert.Null(status.Ms);
        Assert.DoesNotContain("geheim", JsonSerializer.Serialize(status.Error));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("refused")]
    public async Task Timeouts_and_refused_connections_are_red(string kind)
    {
        var web = new FakeServiceWeb([]) { Throw = kind == "timeout" ? new TaskCanceledException() : new HttpRequestException("connect") };
        var status = await Checker(web).CheckAsync(Rec(health: "http://jularr.lan/h"), NoKey, false, CancellationToken.None);
        Assert.Equal(("down", kind), (status.State, status.Error));
    }

    [Fact]
    public async Task Metrics_that_are_too_large_or_not_json_keep_the_state_but_report_the_problem()
    {
        var big = new string('a', ServiceChecker.MaxBytes + 10);
        var web = new FakeServiceWeb(new() { ["http://jularr.lan/big"] = (HttpStatusCode.OK, big), ["http://jularr.lan/text"] = (HttpStatusCode.OK, "kein json"), ["http://jularr.lan/h"] = (HttpStatusCode.OK, "") });
        var tooBig = await Checker(web).CheckAsync(Rec(health: "http://jularr.lan/h", metrics: "http://jularr.lan/big", list: [new("a", "A", "", "plain")]), NoKey, false, CancellationToken.None);
        Assert.Equal(("up", "tooLarge"), (tooBig.State, tooBig.Error));
        var notJson = await Checker(web).CheckAsync(Rec(health: "http://jularr.lan/h", metrics: "http://jularr.lan/text", list: [new("a", "A", "", "plain")]), NoKey, false, CancellationToken.None);
        Assert.Equal(("up", "invalid"), (notJson.State, notJson.Error));
    }

    [Fact]
    public async Task Without_a_health_address_the_metrics_request_decides()
    {
        var web = new FakeServiceWeb(new() { ["http://jularr.lan/m"] = (HttpStatusCode.OK, """{"a":1}""") });
        var up = await Checker(web).CheckAsync(Rec(metrics: "http://jularr.lan/m", list: [new("a", "A", "", "")]), NoKey, false, CancellationToken.None);
        Assert.Equal(("up", 1d), (up.State, up.Metrics[0].Number));
        var down = await Checker(new FakeServiceWeb([])).CheckAsync(Rec(metrics: "http://jularr.lan/m", list: [new("a", "A", "", "")]), NoKey, false, CancellationToken.None);
        Assert.Equal("down", down.State);
    }

    [Fact]
    public async Task A_service_with_no_address_is_grey_and_nothing_is_requested()
    {
        var web = new FakeServiceWeb([]);
        var status = await Checker(web).CheckAsync(Rec(), NoKey, false, CancellationToken.None);
        Assert.Equal("unknown", status.State);
        Assert.Empty(web.Requests);
    }

    [Fact]
    public async Task An_address_off_the_list_is_grey_and_never_requested()
    {
        var web = new FakeServiceWeb([]);
        var status = await Checker(web).CheckAsync(Rec(health: "http://elsewhere.example.test/h"), NoKey, false, CancellationToken.None);
        Assert.Equal(("unknown", "notAllowed"), (status.State, status.Error));
        Assert.Empty(web.Requests);
    }

    [Fact]
    public async Task Results_are_reused_for_the_interval_and_a_forced_check_asks_again()
    {
        var web = new FakeServiceWeb(new() { ["http://jularr.lan/h"] = (HttpStatusCode.OK, "") });
        var time = new TestTime(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var checker = Checker(web, time);
        var s = Rec(health: "http://jularr.lan/h", interval: 30);
        await checker.CheckAsync(s, NoKey, false, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        await checker.CheckAsync(s, NoKey, false, CancellationToken.None);
        Assert.Single(web.Requests);
        await checker.CheckAsync(s, NoKey, true, CancellationToken.None);
        Assert.Equal(2, web.Requests.Count);
        time.Advance(TimeSpan.FromSeconds(31));
        await checker.CheckAsync(s, NoKey, false, CancellationToken.None);
        Assert.Equal(3, web.Requests.Count);
        checker.Forget(s.Id);
        await checker.CheckAsync(s, NoKey, false, CancellationToken.None);
        Assert.Equal(4, web.Requests.Count);
    }

    /* ---------- echte Verbindung auf Loopback (Fake-Dienst ueber einen Socket) ---------- */

    [Fact]
    public async Task A_real_connection_works_on_loopback_and_redirects_are_not_followed()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var n = await stream.ReadAsync(buffer);
                var request = System.Text.Encoding.ASCII.GetString(buffer, 0, n);
                lock (requests) requests.Add(request.Split('\n')[0]);
                var reply = request.StartsWith("GET /ok") ? "HTTP/1.1 200 OK\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{\"value\":4}"
                    : "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply));
            }
        });
        var checker = new ServiceChecker(new ServiceHosts(null, null));      // der echte Handler mit Pruefung beim Verbinden
        var ok = await checker.CheckAsync(Rec(health: $"http://127.0.0.1:{port}/ok", metrics: $"http://127.0.0.1:{port}/ok", list: [new("value", "V", "", "")]), NoKey, false, CancellationToken.None);
        Assert.Equal(("up", 4d), (ok.State, ok.Metrics.SingleOrDefault()?.Number));
        var moved = await checker.CheckAsync(Rec(health: $"http://127.0.0.1:{port}/moved"), NoKey, false, CancellationToken.None);
        Assert.Equal(("down", 302), (moved.State, moved.HttpStatus));
        listener.Stop();
        Assert.Equal(3, requests.Count);      // dem Ziel der Weiterleitung wurde nicht gefolgt
    }

    [Fact]
    public async Task The_real_handler_refuses_a_public_address_before_connecting()
    {
        var checker = new ServiceChecker(new ServiceHosts(null, null));
        var status = await checker.CheckAsync(Rec(health: "http://203.0.113.9:9/h"), NoKey, false, CancellationToken.None);
        Assert.Equal(("unknown", "notAllowed"), (status.State, status.Error));
    }
}
