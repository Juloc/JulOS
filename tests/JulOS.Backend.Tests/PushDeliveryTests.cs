using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Backend.Tests;

/// <summary>Zustellung der Push-Meldungen (docs/PUSH.md) gegen einen Fake-Push-Dienst im Speicher, der wie der Browser entschluesselt: Wiederholungen, tote Abos, Eigentum, Schluessel des Servers, Ablauf der Auswertung.</summary>
public sealed class PushDeliveryTests
{
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Das "Geraet": Schluessel wie ein Browser sie erzeugt.</summary>
    private sealed class Device
    {
        public byte[] Private, Public, Auth = RandomNumberGenerator.GetBytes(16);
        public string Endpoint = "https://push.example.test/v2/" + Guid.NewGuid().ToString("N");
        public Device()
        {
            using var k = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var p = k.ExportParameters(true);
            Private = p.D!;
            Public = [4, .. Pad(p.Q.X!), .. Pad(p.Q.Y!)];
        }
        private static byte[] Pad(byte[] b) => b.Length == 32 ? b : [.. new byte[32 - b.Length], .. b];
    }

    /// <summary>Der Fake-Dienst: antwortet der Reihe nach mit den vorgegebenen Codes (danach 201), merkt sich Anfragen und liest den Inhalt wie der Browser.</summary>
    private sealed class FakePushService(params int[] codes) : HttpMessageHandler
    {
        public List<(Uri Url, string Auth, string Encoding, string Ttl, byte[] Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!, request.Headers.Authorization?.ToString() ?? string.Join("", request.Headers.GetValues("Authorization")), string.Join(",", request.Content!.Headers.ContentEncoding),
                request.Headers.GetValues("TTL").Single(), await request.Content.ReadAsByteArrayAsync(ct)));
            return new HttpResponseMessage((HttpStatusCode)(Calls.Count <= codes.Length ? codes[Calls.Count - 1] : 201));
        }
    }

    private sealed record Rig(TestDb Db, PushStore Store, PushService Push, FakePushService Fake, ManualTime Time, Guid User);

    private static async Task<Rig> RigAsync(params int[] codes)
    {
        var db = await TestDb.CreateAsync();
        var user = await db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('jul', 'x', 'x', true) returning id");
        var time = new ManualTime(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var store = new PushStore(db.Data, new SecretBox(Key()));
        var fake = new FakePushService(codes);
        var push = new PushService(store, new HttpClient(fake), new HitCounter(time), time, NullLogger<PushService>.Instance) { Wait = (_, _) => Task.CompletedTask };
        return new(db, store, push, fake, time, user);
    }

    private static Task<(SubscribeResult Result, Guid Id)> Subscribe(Rig r, Device d, Guid? user = null) => r.Store.SubscribeAsync(user ?? r.User, d.Endpoint, d.Public, d.Auth, "Handy", "https://app.example.test");

    [Fact]
    public async Task A_message_is_encrypted_for_the_device_and_signed_with_the_servers_key()
    {
        var r = await RigAsync();
        await using var _ = r.Db;
        var d = new Device();
        await Subscribe(r, d);
        var payload = PushPayload.Build([new PushItem("Heimserver", "disk", "alert", Guid.NewGuid())]);
        var outcome = await r.Push.SendAsync(r.User, payload, TestContext.Current.CancellationToken);
        Assert.Equal((1, 0, 0, 1), (outcome.Sent, outcome.Failed, outcome.Removed, outcome.Devices));
        var call = Assert.Single(r.Fake.Calls);
        Assert.Equal(d.Endpoint, call.Url.ToString());
        Assert.Equal("aes128gcm", call.Encoding);
        Assert.StartsWith("vapid t=", call.Auth);
        Assert.Equal(payload, WebPushTests.Decrypt(call.Body, d.Private, d.Public, d.Auth));
        Assert.DoesNotContain("Heimserver", System.Text.Encoding.UTF8.GetString(call.Body));        // der Dienst sieht nichts im Klartext
        Assert.Contains("\"k\":\"alert\"", System.Text.Encoding.UTF8.GetString(payload));
        Assert.NotNull((await r.Store.SubscriptionsAsync(r.User, TestContext.Current.CancellationToken))[0].LastOk);
    }

    [Fact]
    public async Task Service_errors_are_retried_at_most_twice_and_a_dead_subscription_is_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var r = await RigAsync(503, 429, 201);
        await using var _ = r.Db;
        await Subscribe(r, new Device());
        Assert.Equal(1, (await r.Push.SendAsync(r.User, [1], ct)).Sent);
        Assert.Equal(3, r.Fake.Calls.Count);                                         // zwei Fehler, dann angenommen

        var always = await RigAsync(500, 500, 500, 500, 500);
        await using var _2 = always.Db;
        await Subscribe(always, new Device());
        var failed = await always.Push.SendAsync(always.User, [1], ct);
        Assert.Equal((0, 1, 0), (failed.Sent, failed.Failed, failed.Removed));
        Assert.Equal(3, always.Fake.Calls.Count);                                    // nicht oefter
        Assert.Single(await always.Store.SubscriptionsAsync(always.User, ct));       // ein Ausfall des Dienstes entfernt das Abo nicht

        var bad = await RigAsync(400);
        await using var _3 = bad.Db;
        await Subscribe(bad, new Device());
        Assert.Equal(1, (await bad.Push.SendAsync(bad.User, [1], ct)).Failed);
        Assert.Single(bad.Fake.Calls);                                               // ein Fehler der Anfrage selbst wird nicht wiederholt

        foreach (var gone in new[] { 404, 410 })
        {
            var dead = await RigAsync(gone);
            await using var _4 = dead.Db;
            await Subscribe(dead, new Device());
            Assert.Equal(1, (await dead.Push.SendAsync(dead.User, [1], ct)).Removed);
            Assert.Empty(await dead.Store.SubscriptionsAsync(dead.User, ct));
        }
    }

    [Fact]
    public async Task Sending_is_limited_per_user_and_hour()
    {
        var ct = TestContext.Current.CancellationToken;
        var r = await RigAsync();
        await using var _ = r.Db;
        await Subscribe(r, new Device());
        for (var i = 0; i < PushService.MaxPerHour; i++) Assert.Equal(1, (await r.Push.SendAsync(r.User, [1], ct)).Sent);
        var over = await r.Push.SendAsync(r.User, [1], ct);
        Assert.True(over.Limited);
        Assert.Equal(PushService.MaxPerHour, r.Fake.Calls.Count);
        r.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, (await r.Push.SendAsync(r.User, [1], ct)).Sent);
    }

    [Fact]
    public async Task A_subscription_belongs_to_its_user_and_the_number_of_devices_is_limited()
    {
        var ct = TestContext.Current.CancellationToken;
        var r = await RigAsync();
        await using var _ = r.Db;
        var other = await r.Db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id");
        var d = new Device();
        var (created, id) = await Subscribe(r, d);
        Assert.Equal(SubscribeResult.Created, created);
        Assert.Equal(SubscribeResult.Updated, (await Subscribe(r, d)).Result);                        // dasselbe Geraet: aktualisiert, nicht doppelt
        Assert.Equal(SubscribeResult.Taken, (await Subscribe(r, d, other)).Result);                    // ein anderer Benutzer kann es nicht uebernehmen
        Assert.Empty(await r.Store.SubscriptionsAsync(other, ct));
        Assert.False(await r.Store.UnsubscribeAsync(other, id, ct));                                   // und nicht entfernen
        Assert.Single(await r.Store.SubscriptionsAsync(r.User, ct));
        Assert.Equal(0, (await r.Push.SendAsync(other, [1], ct)).Devices);                             // eine Sendung an den anderen erreicht das Geraet nicht
        Assert.Empty(r.Fake.Calls);
        for (var i = 1; i < PushStore.MaxSubscriptionsPerUser; i++) Assert.Equal(SubscribeResult.Created, (await Subscribe(r, new Device())).Result);
        Assert.Equal(SubscribeResult.TooMany, (await Subscribe(r, new Device())).Result);
        Assert.True(await r.Store.UnsubscribeAsync(r.User, id, ct));
    }

    [Fact]
    public async Task The_private_vapid_key_is_stored_sealed_and_stays_the_same()
    {
        var ct = TestContext.Current.CancellationToken;
        var r = await RigAsync();
        await using var _ = r.Db;
        var (priv, pub) = (await r.Store.VapidAsync(ct))!.Value;
        var again = (await r.Store.VapidAsync(ct))!.Value;
        Assert.Equal(priv, again.Private);
        Assert.Equal(pub, again.Public);
        Assert.Equal(1, await r.Db.ScalarAsync<long>("select count(*) from push_vapid"));
        var sealedKey = await r.Db.ScalarAsync<byte[]>("select private_sealed from push_vapid");
        Assert.DoesNotContain(Convert.ToBase64String(priv), System.Text.Encoding.UTF8.GetString(sealedKey));
        Assert.False(sealedKey.AsSpan().IndexOf(priv) >= 0);                                           // nicht im Klartext
        Assert.Null(await new PushStore(r.Db.Data, new SecretBox(null)).VapidAsync(ct));               // ohne Schluessel des Servers gibt es keine Meldungen
    }

    private sealed class FakeObserver : IHostObserver
    {
        public HostObservation? Next;
        public Task<HostObservation?> ObserveAsync(Guid userId, HostRecord host, CancellationToken ct) => Task.FromResult(Next is null ? null : Next with { HostId = host.Id, Name = host.Name });
    }

    [Fact]
    public async Task The_runner_reports_the_change_once_holds_it_in_quiet_hours_and_sends_one_collected_message_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var r = await RigAsync();
        await using var _ = r.Db;
        var d = new Device();
        await Subscribe(r, d);
        await r.Db.ExecAsync("insert into hosts (user_id, name) values ($1, 'Heimserver')", r.User);
        await r.Db.ExecAsync("insert into hosts (user_id, name) values ($1, 'Docker-VM')", r.User);
        var observer = new FakeObserver();
        var runner = new AlertRunner(r.Store, new HostStore(r.Db.Data), observer, r.Push, r.Time, NullLogger<AlertRunner>.Instance);
        await r.Store.SaveSettingsAsync(r.User, new AlertSettings(Quiet: true, QuietFrom: 22, QuietTo: 7), ct);

        r.Time.Advance(TimeSpan.FromHours(10));                                      // 22:00 UTC: Ruhezeit
        observer.Next = new HostObservation(Guid.Empty, "", true, Disks: [new DiskFill("/", 50)]);
        await runner.RunOnceAsync(ct);                                               // erster Stand: in Ordnung
        observer.Next = new HostObservation(Guid.Empty, "", true, Disks: [new DiskFill("/", 95)]);
        r.Time.Advance(TimeSpan.FromMinutes(5));
        await runner.RunOnceAsync(ct);
        Assert.Empty(r.Fake.Calls);                                                  // Ruhezeit: nichts geschickt
        Assert.Equal(2, (await r.Store.LogAsync(r.User, "pending", 10, ct)).Count);  // aber vorgemerkt, je Host eine

        r.Time.Advance(TimeSpan.FromHours(9));                                       // 07:05: Ruhezeit vorbei
        await runner.RunOnceAsync(ct);
        var call = Assert.Single(r.Fake.Calls);                                      // eine Sammelnachricht fuer beide
        var json = JsonDocument.Parse(WebPushTests.Decrypt(call.Body, d.Private, d.Public, d.Auth)).RootElement;
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        Assert.Equal(2, json.GetProperty("n").GetInt32());
        Assert.Empty(await r.Store.LogAsync(r.User, "pending", 10, ct));
        Assert.All(await r.Store.LogAsync(r.User, null, 10, ct), row => Assert.Equal("sent", row.Push));

        r.Time.Advance(TimeSpan.FromMinutes(5));
        await runner.RunOnceAsync(ct);                                               // derselbe Zustand: nichts Neues
        Assert.Single(r.Fake.Calls);
    }

    [Fact]
    public async Task Without_a_device_the_runner_only_fills_the_list_in_the_app()
    {
        var ct = TestContext.Current.CancellationToken;
        var r = await RigAsync();
        await using var _ = r.Db;
        await r.Store.SaveSettingsAsync(r.User, new AlertSettings(), ct);
        await r.Db.ExecAsync("insert into hosts (user_id, name) values ($1, 'Heimserver')", r.User);
        var observer = new FakeObserver { Next = new HostObservation(Guid.Empty, "", true, Reboot: false) };
        var runner = new AlertRunner(r.Store, new HostStore(r.Db.Data), observer, r.Push, r.Time, NullLogger<AlertRunner>.Instance);
        await runner.RunOnceAsync(ct);
        observer.Next = new HostObservation(Guid.Empty, "", true, Reboot: true);
        await runner.RunOnceAsync(ct);
        var log = Assert.Single(await r.Store.LogAsync(r.User, null, 10, ct));
        Assert.Equal(("reboot", "alert", "none", "Heimserver"), (log.Rule, log.Kind, log.Push, log.HostName));
        Assert.Empty(r.Fake.Calls);
    }
}
