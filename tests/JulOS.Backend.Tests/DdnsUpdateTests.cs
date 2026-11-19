using System.Net;
using System.Text.Json;
using JulOS.Backend.Modules;

namespace JulOS.Backend.Tests;

/// <summary>DDNS, Schreibstufe: nur geaenderte, benannte Eintraege; nie anlegen oder loeschen; Pausen, Fehlerpause, Verlauf; Anbieter-Adapter gegen Fakes.</summary>
public sealed class DdnsUpdateTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("julos-ddns-");
    public void Dispose() => _dir.Delete(true);

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-19T12:00:00Z");

    private sealed record Rig(DdnsUpdater Updater, FakeDnsProvider Provider, MemoryHistory History, TestTime Time, FakeIp Ip);

    private static Rig Make(string publicIp, Dictionary<string, string[]> dns, DnsRecord[]? records = null, bool ok = true, ModuleMode mode = ModuleMode.Control, string[]? hosts = null, bool withProvider = true)
    {
        var time = new TestTime(Start);
        var opts = new DdnsOptions(mode, hosts ?? ["home.example.test", "nas.example.test"], null, null, DdnsOptions.DefaultCache);
        var ip = new FakeIp(publicIp);
        var monitor = new DdnsMonitor(opts, ip, new FakeResolver(dns), time);
        var provider = new FakeDnsProvider(records, ok);
        var history = new MemoryHistory();
        return new Rig(new DdnsUpdater(opts, monitor, withProvider ? provider : null, history, time), provider, history, time, ip);
    }

    private static readonly Dictionary<string, string[]> OldHome = new() { ["home.example.test"] = ["203.0.113.1"], ["nas.example.test"] = ["203.0.113.9"] };

    [Fact]
    public async Task Only_the_names_that_differ_are_changed_and_every_change_is_in_the_history()
    {
        var r = Make("203.0.113.9", OldHome);
        var res = await r.Updater.UpdateAsync("button", CancellationToken.None);
        Assert.Equal("updated", res.Outcome);
        Assert.Equal([("home.example.test", "203.0.113.9")], r.Provider.Writes);
        var c = Assert.Single(res.Changes);
        Assert.Equal(("home.example.test", "A", "203.0.113.1", "203.0.113.9", true), (c.Host, c.Type, c.OldIp, c.NewIp, c.Ok));
        var row = Assert.Single(r.History.Rows);
        Assert.Equal(("home.example.test", "203.0.113.1", "203.0.113.9", "button", true, Start), (row.Host, row.OldIp, row.NewIp, row.Trigger, row.Ok, row.At));
    }

    [Fact]
    public async Task Nothing_is_written_when_everything_is_current_or_there_is_no_usable_address()
    {
        var current = Make("203.0.113.1", new() { ["home.example.test"] = ["203.0.113.1"], ["nas.example.test"] = ["203.0.113.1"] });
        Assert.Equal("current", (await current.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);
        Assert.Empty(current.Provider.Writes);

        var privateIp = Make("192.168.1.5", OldHome);
        Assert.Equal("privateIp", (await privateIp.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);
        Assert.Equal("privateIp", (await Make("100.64.0.9", OldHome).Updater.UpdateAsync("auto", CancellationToken.None)).Outcome);
        Assert.Empty(privateIp.Provider.Writes);

        var noIp = Make(null!, OldHome);
        Assert.Equal("noIp", (await noIp.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);
        Assert.Empty(noIp.Provider.Writes);
    }

    [Fact]
    public async Task Without_control_or_without_a_provider_nothing_happens()
    {
        var read = Make("203.0.113.9", OldHome, mode: ModuleMode.Read);
        Assert.False(read.Updater.CanUpdate);
        Assert.Equal("noProvider", (await read.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);
        var none = Make("203.0.113.9", OldHome, withProvider: false);
        Assert.Equal("noProvider", (await none.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);
        Assert.Empty(read.Provider.Writes.Concat(none.Provider.Writes));
        Assert.False(Make("203.0.113.9", OldHome).Updater.AutoOn);    // auto braucht auch den Schalter
    }

    [Fact]
    public async Task A_readable_provider_decides_by_its_own_content_not_by_the_resolver()
    {
        // Der Namensdienst sagt "alt", der Anbieter hat die neue Adresse schon: nichts zu tun
        var done = Make("203.0.113.9", OldHome, records: [new("home.example.test", "A", "203.0.113.9"), new("nas.example.test", "A", "203.0.113.9")]);
        Assert.Equal("current", (await done.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);
        Assert.Empty(done.Provider.Writes);
        // Der Namensdienst sagt "aktuell", der Anbieter hat noch die alte: es wird geschrieben
        var stale = Make("203.0.113.9", OldHome, records: [new("home.example.test", "A", "203.0.113.9"), new("nas.example.test", "A", "203.0.113.2")]);
        var res = await stale.Updater.UpdateAsync("button", CancellationToken.None);
        Assert.Equal([("nas.example.test", "203.0.113.9")], stale.Provider.Writes);
        Assert.Equal("203.0.113.2", res.Changes[0].OldIp);
    }

    [Fact]
    public async Task An_unreadable_provider_is_not_written_again_while_the_resolver_still_answers_old()
    {
        var r = Make("203.0.113.9", OldHome);
        await r.Updater.UpdateAsync("button", CancellationToken.None);
        Assert.Single(r.Provider.Writes);
        r.Time.Advance(TimeSpan.FromMinutes(6));
        var again = await r.Updater.UpdateAsync("auto", CancellationToken.None);       // der Namensdienst antwortet noch alt (Zwischenspeicher)
        Assert.Equal("current", again.Outcome);
        Assert.Single(r.Provider.Writes);
    }

    [Fact]
    public async Task Pauses_apply_30_seconds_after_a_button_and_5_minutes_for_auto()
    {
        var r = Make("203.0.113.9", OldHome, records: [new("home.example.test", "A", "203.0.113.1"), new("nas.example.test", "A", "203.0.113.9")]);
        Assert.Equal("updated", (await r.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);
        Assert.Equal("tooSoon", (await r.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);
        r.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("updated", (await r.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);   // der Anbieter (Fake) nennt weiter "alt"
        r.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("tooSoon", (await r.Updater.UpdateAsync("auto", CancellationToken.None)).Outcome);
        r.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("updated", (await r.Updater.UpdateAsync("auto", CancellationToken.None)).Outcome);
        Assert.Equal(3, r.Provider.Writes.Count);
    }

    [Fact]
    public async Task A_failed_write_is_recorded_and_not_retried_automatically_for_an_hour()
    {
        var r = Make("203.0.113.9", OldHome, ok: false);
        var first = await r.Updater.UpdateAsync("auto", CancellationToken.None);
        Assert.Equal("failed", first.Outcome);
        Assert.False(r.History.Rows[0].Ok);
        Assert.Equal("rejected", r.History.Rows[0].Message);
        r.Time.Advance(TimeSpan.FromMinutes(10));
        var second = await r.Updater.UpdateAsync("auto", CancellationToken.None);
        Assert.Equal("current", second.Outcome);                      // eine Stunde Pause fuer diesen Namen
        Assert.Single(r.Provider.Writes);
        r.Time.Advance(TimeSpan.FromMinutes(55));
        Assert.Equal("failed", (await r.Updater.UpdateAsync("auto", CancellationToken.None)).Outcome);
        Assert.Equal(2, r.Provider.Writes.Count);
        r.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("failed", (await r.Updater.UpdateAsync("button", CancellationToken.None)).Outcome);   // ein Knopfdruck darf immer
    }

    [Fact]
    public async Task Ipv6_changes_only_AAAA_records()
    {
        var r = Make("2001:db8::7", new() { ["home.example.test"] = ["203.0.113.1", "2001:db8::1"], ["nas.example.test"] = ["2001:db8::7"] },
            records: [new("home.example.test", "A", "203.0.113.1"), new("home.example.test", "AAAA", "2001:db8::1"), new("nas.example.test", "AAAA", "2001:db8::7")]);
        var res = await r.Updater.UpdateAsync("button", CancellationToken.None);
        Assert.Equal("AAAA", Assert.Single(res.Changes).Type);
        Assert.Equal([("home.example.test", "2001:db8::7")], r.Provider.Writes);
    }

    [Fact]
    public void Control_info_shows_no_addresses_or_credentials()
    {
        var r = Make("203.0.113.9", OldHome);
        var json = JsonSerializer.Serialize(r.Updater.Info());
        Assert.Contains("\"CanUpdate\":true", json);
        Assert.DoesNotContain("203.0.113", json);
    }

    /* ---------- Anbieter ---------- */

    private static HttpClient Client(FakeProviderWeb web) => new(web) { Timeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task Cloudflare_changes_exactly_one_existing_record_with_content_only_and_never_creates_or_deletes()
    {
        var tokenFile = Path.Combine(_dir.FullName, "cf-token");
        await File.WriteAllTextAsync(tokenFile, "wegwerf-token-xyz\n");
        var web = new FakeProviderWeb((req, _) => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, req.RequestUri!.Query.Contains("home.example.test") ? """{"success":true,"result":[{"id":"rec1","content":"203.0.113.1"}]}"""
               : req.RequestUri.Query.Contains("two.example.test") ? """{"result":[{"id":"a","content":"1.1.1.1"},{"id":"b","content":"2.2.2.2"}]}""" : """{"result":[]}""")
            : (HttpStatusCode.OK, """{"success":true}"""));
        var cf = new CloudflareProvider(Client(web), "0123456789abcdef", tokenFile);

        var read = await cf.ReadAsync(["home.example.test"], "A", CancellationToken.None);
        Assert.Equal("203.0.113.1", read![0].Content);
        Assert.Equal(new DnsWriteResult(true, "ok"), await cf.UpdateAsync("home.example.test", IPAddress.Parse("203.0.113.9"), CancellationToken.None));
        Assert.Equal(new DnsWriteResult(false, "missing"), await cf.UpdateAsync("gone.example.test", IPAddress.Parse("203.0.113.9"), CancellationToken.None));
        Assert.Equal(new DnsWriteResult(false, "ambiguous"), await cf.UpdateAsync("two.example.test", IPAddress.Parse("203.0.113.9"), CancellationToken.None));

        Assert.Equal(["GET", "GET", "PATCH", "GET", "GET"], web.Requests.Select(q => q.Method));             // nie POST, nie DELETE
        var patch = web.Requests.Single(q => q.Method == "PATCH");
        Assert.Equal("https://api.cloudflare.com/client/v4/zones/0123456789abcdef/dns_records/rec1", patch.Url);
        Assert.Equal("""{"content":"203.0.113.9"}""", patch.Body);
        Assert.All(web.Requests, q => { Assert.Equal("Bearer wegwerf-token-xyz", q.Auth); Assert.DoesNotContain("wegwerf", q.Url); });
        Assert.StartsWith("https://api.cloudflare.com/", web.Requests[0].Url);
    }

    [Fact]
    public async Task Cloudflare_without_a_token_file_or_with_an_error_changes_nothing()
    {
        var web = new FakeProviderWeb((_, _) => (HttpStatusCode.Forbidden, "{}"));
        var noToken = new CloudflareProvider(Client(web), "z", Path.Combine(_dir.FullName, "fehlt"));
        Assert.Equal("unreachable", (await noToken.UpdateAsync("a.example.test", IPAddress.Parse("203.0.113.9"), CancellationToken.None)).Message);
        Assert.Null(await noToken.ReadAsync(["a.example.test"], "A", CancellationToken.None));
        var tokenFile = Path.Combine(_dir.FullName, "t"); await File.WriteAllTextAsync(tokenFile, "x");
        var forbidden = new CloudflareProvider(Client(web), "z", tokenFile);
        Assert.Equal("unreachable", (await forbidden.UpdateAsync("a.example.test", IPAddress.Parse("203.0.113.9"), CancellationToken.None)).Message);
        Assert.DoesNotContain(web.Requests, q => q.Method != "GET");
    }

    [Fact]
    public async Task The_generic_provider_fills_host_and_address_and_reads_the_answer()
    {
        var auth = Path.Combine(_dir.FullName, "auth"); await File.WriteAllTextAsync(auth, "jul:wegwerf");
        var web = new FakeProviderWeb((req, _) => req.RequestUri!.Query.Contains("fail") ? (HttpStatusCode.OK, "badauth") : (HttpStatusCode.OK, "good 203.0.113.9"));
        var g = new GenericUrlProvider(Client(web), "https://dyn.example.test/update?name={host}&myip={ip}&key=GEHEIM-IM-LINK", auth);
        Assert.True((await g.UpdateAsync("home.example.test", IPAddress.Parse("203.0.113.9"), CancellationToken.None)).Ok);
        Assert.Equal("https://dyn.example.test/update?name=home.example.test&myip=203.0.113.9&key=GEHEIM-IM-LINK", web.Requests[0].Url);
        Assert.Equal("Basic " + Convert.ToBase64String("jul:wegwerf"u8.ToArray()), web.Requests[0].Auth);
        Assert.Null(await g.ReadAsync(["home.example.test"], "A", CancellationToken.None));
        var bad = new GenericUrlProvider(Client(web), "https://dyn.example.test/update?fail&name={host}&myip={ip}", null);
        Assert.Equal(new DnsWriteResult(false, "rejected"), await bad.UpdateAsync("home.example.test", IPAddress.Parse("203.0.113.9"), CancellationToken.None));
    }

    [Fact]
    public async Task DuckDns_updates_only_its_own_subdomains_with_the_token_from_a_file()
    {
        var tokenFile = Path.Combine(_dir.FullName, "duck"); await File.WriteAllTextAsync(tokenFile, "duck-wegwerf-token");
        var web = new FakeProviderWeb((_, _) => (HttpStatusCode.OK, "OK"));
        var d = new DuckDnsProvider(Client(web), tokenFile);
        Assert.Equal("meinhaus", DuckDnsProvider.Subdomain("meinhaus.duckdns.org"));
        Assert.Null(DuckDnsProvider.Subdomain("a.b.duckdns.org"));
        Assert.Null(DuckDnsProvider.Subdomain("home.example.test"));
        Assert.True((await d.UpdateAsync("meinhaus.duckdns.org", IPAddress.Parse("203.0.113.9"), CancellationToken.None)).Ok);
        Assert.Equal("https://www.duckdns.org/update?domains=meinhaus&token=duck-wegwerf-token&ip=203.0.113.9", web.Requests[0].Url);
        Assert.Equal("missing", (await d.UpdateAsync("home.example.test", IPAddress.Parse("203.0.113.9"), CancellationToken.None)).Message);
        Assert.Single(web.Requests);                                                         // fuer fremde Namen geht nichts raus
        var ko = new DuckDnsProvider(Client(new FakeProviderWeb((_, _) => (HttpStatusCode.OK, "KO"))), tokenFile);
        Assert.False((await ko.UpdateAsync("meinhaus.duckdns.org", IPAddress.Parse("203.0.113.9"), CancellationToken.None)).Ok);
    }

    /* ---------- Einstellungen ---------- */

    private static ModuleSettings Settings(params (string, string)[] kv) => ModuleSettings.From(k => kv.FirstOrDefault(x => x.Item1 == k).Item2);

    [Fact]
    public void Provider_and_auto_come_only_with_control_and_complete_settings()
    {
        var ok = Settings(("JULOS_DDNS", "control"), ("JULOS_DDNS_HOSTS", "home.example.test"), ("JULOS_DDNS_PROVIDER", "cloudflare"), ("JULOS_DDNS_CLOUDFLARE_ZONE", "0123abcd"), ("JULOS_DDNS_TOKEN_FILE", "/run/secrets/cf"), ("JULOS_DDNS_AUTO", "on"), ("JULOS_DDNS_AUTO_SECONDS", "900"));
        Assert.Equal(ModuleMode.Control, ok.Ddns.Mode);
        Assert.Equal("cloudflare", ok.Ddns.Provider!.Kind);
        Assert.True(ok.Ddns.Auto);
        Assert.Equal(TimeSpan.FromSeconds(900), ok.Ddns.AutoEvery);
        Assert.Empty(ok.Warnings);

        Assert.Equal(TimeSpan.FromMinutes(5), Settings(("JULOS_DDNS", "control"), ("JULOS_DDNS_AUTO_SECONDS", "10")).Ddns.AutoEvery);           // nie schneller als alle 5 Minuten
        var read = Settings(("JULOS_DDNS", "read"), ("JULOS_DDNS_HOSTS", "home.example.test"), ("JULOS_DDNS_PROVIDER", "duckdns"), ("JULOS_DDNS_TOKEN_FILE", "/x"), ("JULOS_DDNS_AUTO", "on"));
        Assert.Null(read.Ddns.Provider);                                                                                                         // mit read gibt es keinen Anbieter
        Assert.False(read.Ddns.Auto);
        Assert.Contains(read.Warnings, w => w.Contains("JULOS_DDNS_AUTO"));

        var broken = Settings(("JULOS_DDNS", "control"), ("JULOS_DDNS_HOSTS", "home.example.test"), ("JULOS_DDNS_PROVIDER", "cloudflare"));
        Assert.Null(broken.Ddns.Provider);
        Assert.Contains(broken.Warnings, w => w.Contains("JULOS_DDNS_CLOUDFLARE_ZONE"));
        var generic = Settings(("JULOS_DDNS", "control"), ("JULOS_DDNS_HOSTS", "home.example.test"), ("JULOS_DDNS_PROVIDER", "generic"), ("JULOS_DDNS_UPDATE_URL", "https://dyn.example.test/u?h={host}&i={ip}"));
        Assert.Equal("generic", generic.Ddns.Provider!.Kind);
        Assert.Null(Settings(("JULOS_DDNS", "control"), ("JULOS_DDNS_PROVIDER", "generic"), ("JULOS_DDNS_UPDATE_URL", "https://dyn.example.test/u?h={host}")).Ddns.Provider);   // ohne {ip}
        Assert.Contains(Settings(("JULOS_DDNS", "control"), ("JULOS_DDNS_PROVIDER", "netcup")).Warnings, w => w.Contains("unbekannter Anbieter"));
    }
}
