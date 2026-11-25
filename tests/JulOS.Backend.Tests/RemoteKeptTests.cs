using System.Net;
using System.Net.WebSockets;
using JulOS.Backend.Data;
using JulOS.Integrations;
using JulOS.Integrations.Display;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>
/// Erhaltene Sitzungen (docs/SESSION-KEEP.md, Schritt 1): wer sie sehen, nehmen und beenden darf, das Einmal-Ticket, der Verlauf der Sicherheit und das Ende mit der Abmeldung.
/// Die Sitzungen sind angelegt ohne echtes Ziel (ScreenRelay.AddKeptForTests); das Verhalten gegen einen echten Desktop pruefen die Tests in JulOS.Display.Tests.
/// </summary>
public sealed class RemoteKeptTests
{
    private sealed record Who(string Cookie, Guid User, Guid App);

    private static async Task<Who> SecondSessionAsync(Server app, Guid user)
    {
        var sessions = app.Services.GetRequiredService<SessionStore>();
        var token = await sessions.CreateAsync(user, "127.0.0.1", "test");
        return new Who("julos_sid=" + token, user, (await sessions.ResolveAsync(token, false))!.Id);
    }

    private static async Task<Who> FirstAsync(Server app, HttpClient c, TestDb db)
    {
        var (cookie, user) = await SignedInAsync(c, db);
        var resolved = await app.Services.GetRequiredService<SessionStore>().ResolveAsync(cookie["julos_sid=".Length..], false);
        return new Who(cookie, user, resolved!.Id);
    }

    private static async Task<Who> StrangerAsync(Server app, TestDb db) =>
        await SecondSessionAsync(app, await db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('fremd', 'x', 'x', true) returning id"));

    private static string Add(Server app, Who owner, Guid conn, bool watched, Guid? appSession = null, string name = "Server") =>
        app.Services.GetRequiredService<ScreenRelay>().AddKeptForTests(owner.User, new KeptInfo(conn, name, "Firefox, Linux", "de"), appSession ?? owner.App, watched);

    private static HttpRequestMessage Get(string url, string? cookie) => Req(HttpMethod.Get, url, cookie);

    [Fact]
    public async Task The_three_routes_need_a_session_the_header_and_the_own_origin()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var me = await FirstAsync(app, c, db);
        var conn = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get("/api/remote/kept?conn=" + conn, null))).StatusCode);
        foreach (var path in new[] { "/api/remote/kept/take", "/api/remote/kept/end" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Post, path, null, new { id = "x" }))).StatusCode);
            var noHeader = Req(HttpMethod.Post, path, me.Cookie, new { id = "x" }); noHeader.Headers.Remove("X-JulOS");
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(noHeader)).StatusCode);
            var foreign = Req(HttpMethod.Post, path, me.Cookie, new { id = "x" }); foreign.Headers.Remove("Origin"); foreign.Headers.Add("Origin", "https://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(foreign)).StatusCode);
        }
    }

    [Fact]
    public async Task A_person_lists_only_their_own_sessions_of_that_connection_and_only_the_named_fields()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var me = await FirstAsync(app, c, db);
        var stranger = await StrangerAsync(app, db);
        var conn = Guid.NewGuid();
        var mine = Add(app, me, conn, watched: false);
        Add(app, me, Guid.NewGuid(), watched: false);          // andere Verbindung derselben Person
        Add(app, stranger, conn, watched: false);              // dieselbe Verbindungskennung bei einer fremden Person

        var res = await c.SendAsync(Get("/api/remote/kept?conn=" + conn, me.Cookie));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        var list = (await Json(res)).GetProperty("sessions");
        Assert.Equal(1, list.GetArrayLength());
        var item = list[0];
        Assert.Equal(mine, item.GetProperty("id").GetString());
        Assert.Equal(["device", "id", "name", "own", "since", "unwatchedSince"], item.EnumerateObject().Select(p => p.Name).Order().ToArray());      // nie Host, Adresse, Ziel-Benutzer oder Token
        Assert.True(item.GetProperty("own").GetBoolean());

        // die fremde Person sieht von mir nichts, nicht einmal eine Zahl
        var theirs = (await Json(await c.SendAsync(Get("/api/remote/kept?conn=" + conn, stranger.Cookie)))).GetProperty("sessions");
        Assert.Equal(1, theirs.GetArrayLength());
        Assert.NotEqual(mine, theirs[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Foreign_and_unknown_handles_get_the_same_answer_and_a_foreign_session_stays_untouched()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var me = await FirstAsync(app, c, db);
        var stranger = await StrangerAsync(app, db);
        var theirs = Add(app, stranger, Guid.NewGuid(), watched: false);
        var relay = app.Services.GetRequiredService<ScreenRelay>();

        async Task<(HttpStatusCode, string)> Post(string path, string id)
        {
            var r = await c.SendAsync(Req(HttpMethod.Post, path, me.Cookie, new { id }));
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        Assert.Equal(await Post("/api/remote/kept/take", "unbekannt1234567890123"), await Post("/api/remote/kept/take", theirs));
        Assert.Equal(await Post("/api/remote/kept/end", "unbekannt1234567890123"), await Post("/api/remote/kept/end", theirs));
        Assert.NotNull(relay.FindKept(stranger.User, theirs));             // nicht beendet
        Assert.Null(relay.FindKept(me.User, theirs));
    }

    [Fact]
    public async Task A_take_ticket_works_once_only_for_the_asking_app_session_and_a_used_ticket_is_dead()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var me = await FirstAsync(app, c, db);
        var other = await SecondSessionAsync(app, me.User);                 // ein zweites Geraet derselben Person
        var tickets = app.Services.GetRequiredService<ScreenTickets>();
        var handle = Add(app, me, Guid.NewGuid(), watched: false);

        async Task<string> Ticket(Who who)
        {
            var r = await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", who.Cookie, new { id = handle }));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
            return (await Json(r)).GetProperty("ticket").GetString()!;
        }

        var t1 = await Ticket(me);
        Assert.Null(tickets.Redeem(t1, me.User, other.App));                 // anderes Geraet: nein ...
        Assert.Null(tickets.Redeem(t1, me.User, me.App));                    // ... und das Ticket ist damit verbraucht
        var t2 = await Ticket(me);
        Assert.Null(tickets.Redeem(t2, (await StrangerAsync(app, db)).User, me.App));   // andere Person: nein
        var t3 = await Ticket(me);
        Assert.Equal(handle, tickets.Redeem(t3, me.User, me.App)!.Kept!.Handle);        // richtige Person, richtige App-Sitzung: einmal
        Assert.Null(tickets.Redeem(t3, me.User, me.App));
        // das Ticket der neuen Art ist fuer den Weg neuer Sitzungen ungueltig (und verbraucht): es bekommt dort nie ein Ziel, auch kein leeres
        Assert.Null(tickets.Take(await Ticket(me), me.User));
        var t5 = await Ticket(me);
        Assert.Null(tickets.Take(t5, me.User));
        Assert.Null(tickets.Redeem(t5, me.User, me.App));
    }

    [Fact]
    public void A_take_ticket_lives_20_seconds()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-11-24T10:00:00Z"));
        var tickets = new ScreenTickets(time);
        var user = Guid.NewGuid(); var app = Guid.NewGuid();
        var fresh = tickets.CreateKept(user, new KeptTake("h", app))!;
        var late = tickets.CreateKept(user, new KeptTake("h", app))!;
        time.Advance(TimeSpan.FromSeconds(19));
        Assert.NotNull(tickets.Redeem(fresh, user, app));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(tickets.Redeem(late, user, app));
    }

    [Fact]
    public async Task Only_a_take_from_another_app_session_is_written_to_the_security_history_and_nothing_secret_is_in_it()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var me = await FirstAsync(app, c, db);
        var phone = await SecondSessionAsync(app, me.User);
        var handle = Add(app, me, Guid.NewGuid(), watched: false, name: "Buero");
        Task<long> Count(string evt) => db.ScalarAsync<long>("select count(*) from security_audit where event = $1", evt);

        await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", me.Cookie, new { id = handle }));         // Neuladen am selben Geraet
        Assert.Equal(0, await Count("kept.take"));
        await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", phone.Cookie, new { id = handle }));      // anderes Geraet
        Assert.Equal(1, await Count("kept.take"));
        await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", me.Cookie, new { id = "gibtsnicht0123456789" }));
        Assert.Equal(1, await Count("kept.take_denied"));
        await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/end", me.Cookie, new { id = handle }));
        Assert.Equal(1, await Count("kept.end"));
        Assert.Null(app.Services.GetRequiredService<ScreenRelay>().FindKept(me.User, handle));

        var details = await db.ScalarAsync<string>("select string_agg(coalesce(detail, ''), ' | ') from security_audit where event like 'kept.%'");
        Assert.Contains("Buero", details);
        Assert.DoesNotContain(handle, details);                       // keine Kennung, kein Token im Verlauf
    }

    [Fact]
    public async Task Signing_out_ends_the_desktops_that_device_had_last_and_only_those()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var laptop = await FirstAsync(app, c, db);
        var phone = await SecondSessionAsync(app, laptop.User);
        var relay = app.Services.GetRequiredService<ScreenRelay>();
        var onLaptop = Add(app, laptop, Guid.NewGuid(), watched: false);
        var onPhone = Add(app, laptop, Guid.NewGuid(), watched: false, appSession: phone.App);

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", laptop.Cookie, new { }))).StatusCode);
        Assert.Null(relay.FindKept(laptop.User, onLaptop));
        Assert.NotNull(relay.FindKept(laptop.User, onPhone));        // das Telefon hat es zuletzt gehabt, es bleibt
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from security_audit where event = 'kept.end'"));
    }

    [Fact]
    public async Task Taking_over_a_session_makes_the_taker_the_one_whose_sign_out_ends_it()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var laptop = await FirstAsync(app, c, db);
        var phone = await SecondSessionAsync(app, laptop.User);
        var relay = app.Services.GetRequiredService<ScreenRelay>();
        var handle = Add(app, laptop, Guid.NewGuid(), watched: false);

        var ticket = (await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", phone.Cookie, new { id = handle })))).GetProperty("ticket").GetString()!;
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => { r.Headers["Cookie"] = phone.Cookie; r.Headers["Origin"] = "http://localhost"; };
        using var socket = await ws.ConnectAsync(new Uri("ws://localhost/api/remote/screen?ticket=" + ticket), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var buffer = new byte[512];
        var got = await socket.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(ScreenProtocol.ToBrowserSession, buffer[0]);          // die Sitzung ist wieder aufgenommen: Token und Kennung kommen
        Assert.Equal(phone.App, relay.FindKept(laptop.User, handle)!.AppSession);
        Assert.Null(relay.FindKept(laptop.User, handle)!.UnwatchedSince);

        // dasselbe Ticket ein zweites Mal geht nicht, auch nicht fuer dieselbe App-Sitzung
        var again = app.Server.CreateWebSocketClient();
        again.ConfigureRequest = ws.ConfigureRequest;
        await Assert.ThrowsAnyAsync<Exception>(() => again.ConnectAsync(new Uri("ws://localhost/api/remote/screen?ticket=" + ticket), CancellationToken.None));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        _ = got;
    }

    [Fact]
    public async Task A_session_that_is_watched_is_taken_from_another_app_session_only_with_confirmation_and_never_by_a_stranger()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var laptop = await FirstAsync(app, c, db);
        var thief = await SecondSessionAsync(app, laptop.User);
        var relay = app.Services.GetRequiredService<ScreenRelay>();
        var watched = Add(app, laptop, Guid.NewGuid(), watched: true);

        var denied = await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", thief.Cookie, new { id = watched }));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var unknown = await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", thief.Cookie, new { id = "gibtsnicht0123456789" }));
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await denied.Content.ReadAsStringAsync());      // wie unbekannt
        Assert.Equal(AppOf(laptop), relay.FindKept(laptop.User, watched)!.AppSession);                         // der Betrachter bleibt
        Assert.Null(relay.FindKept(laptop.User, watched)!.UnwatchedSince);
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from security_audit where event = 'kept.take_denied'"));

        // dieselbe App-Sitzung (ein Neuladen, dessen alter Socket noch nicht als weg erkannt ist) darf
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", laptop.Cookie, new { id = watched }))).StatusCode);

        // Uebergabe (Schritt 3): das andere Geraet darf, wenn die Person die Rueckfrage bestaetigt hat; aufgeschrieben als Nehmen einer angesehenen Sitzung
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", thief.Cookie, new { id = watched, confirm = true }))).StatusCode);
        Assert.Contains("watched", await db.ScalarAsync<string>("select detail from security_audit where event = 'kept.take'"));
        // ... eine fremde Person auch mit Bestaetigung nie
        var stranger = await StrangerAsync(app, db);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", stranger.Cookie, new { id = watched, confirm = true }))).StatusCode);

        static Guid AppOf(Who w) => w.App;
    }

    [Fact]
    public async Task Denied_takes_write_only_a_few_history_entries_per_minute()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var me = await FirstAsync(app, c, db);
        for (var i = 0; i < 12; i++) await c.SendAsync(Req(HttpMethod.Post, "/api/remote/kept/take", me.Cookie, new { id = "gibtsnicht" + i }));
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from security_audit where event = 'kept.take_denied'"));
    }

    [Fact]
    public async Task Two_parallel_redemptions_of_one_ticket_let_exactly_one_through()
    {
        var tickets = new ScreenTickets(TimeProvider.System);
        var user = Guid.NewGuid(); var app = Guid.NewGuid();
        for (var round = 0; round < 20; round++)
        {
            var ticket = tickets.CreateKept(user, new KeptTake("h", app))!;
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => tickets.Redeem(ticket, user, app))));
            Assert.Equal(1, results.Count(r => r is not null));
        }
    }

    [Fact]
    public async Task A_failing_teardown_of_a_kept_desktop_never_keeps_the_cookie_alive_on_sign_out()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var me = await FirstAsync(app, c, db);
        app.Services.GetRequiredService<ScreenRelay>().AddKeptForTests(me.User, new KeptInfo(Guid.NewGuid(), "S", "d"), me.App, false, run: Task.FromException(new InvalidCastException("Abbau kaputt")));

        var res = await c.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", me.Cookie, new { }));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Contains(res.Headers.GetValues("Set-Cookie"), v => v.StartsWith("julos_sid=;") || v.StartsWith("julos_sid=,") || v.Contains("julos_sid=; "));      // das Cookie wird geloescht
        Assert.False(await app.Services.GetRequiredService<SessionStore>().IsAliveAsync(me.App));                                                      // und die Sitzung ist weg
    }

    [Fact]
    public async Task Without_room_the_oldest_waiting_session_ends_instead_of_refusing_the_new_one_and_watched_ones_are_never_evicted()
    {
        var owner = Guid.NewGuid(); var info = new KeptInfo(Guid.NewGuid(), "S", "d");
        var reasons = new List<string>();
        using var relay = new ScreenRelay(NullLogger<ScreenRelay>.Instance, registry: new SessionRegistry(3), keep: TimeSpan.FromSeconds(60)) { KeptEnded = (k, why) => { reasons.Add(why); return Task.CompletedTask; } };
        var watched = relay.AddKeptForTests(owner, info, Guid.NewGuid(), watched: true);
        var oldest = relay.AddKeptForTests(owner, info, Guid.NewGuid(), watched: false);
        await Task.Delay(30);
        var newer = relay.AddKeptForTests(owner, info, Guid.NewGuid(), watched: false);

        var lease = await relay.OpenLeaseAsync(owner, "slot-new", () => { });
        Assert.NotNull(lease);
        Assert.Null(relay.FindKept(owner, oldest));                 // die am laengsten wartende musste gehen
        Assert.NotNull(relay.FindKept(owner, newer));
        Assert.NotNull(relay.FindKept(owner, watched));
        Assert.Equal(["limit"], reasons);

        // jetzt ist ausser der wartenden `newer` alles belegt: sie geht auch; danach gibt es nur noch die angesehene, und die wird nie verdraengt
        Assert.NotNull(await relay.OpenLeaseAsync(owner, "slot-new-2", () => { }));
        Assert.Null(await relay.OpenLeaseAsync(owner, "slot-new-3", () => { }));
        Assert.NotNull(relay.FindKept(owner, watched));
    }

    [Fact]
    public async Task At_most_four_sessions_per_person_wait_the_oldest_goes_first()
    {
        var owner = Guid.NewGuid(); var info = new KeptInfo(Guid.NewGuid(), "S", "d");
        using var relay = new ScreenRelay(NullLogger<ScreenRelay>.Instance, keep: TimeSpan.FromSeconds(60));
        var handles = new List<string>();
        for (var i = 0; i < 5; i++) { handles.Add(relay.AddKeptForTests(owner, info, Guid.NewGuid(), watched: false)); await Task.Delay(30); }
        var until = DateTime.UtcNow.AddSeconds(10);
        while (relay.FindKept(owner, handles[0]) is not null && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.Null(relay.FindKept(owner, handles[0]));
        Assert.All(handles.Skip(1), h => Assert.NotNull(relay.FindKept(owner, h)));
        Assert.Equal(ScreenRelay.MaxUnwatchedPerUser, relay.DetachedCount);
    }

    [Fact]
    public async Task Signing_out_everywhere_ends_a_waiting_desktop_within_the_ten_second_check()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var me = await FirstAsync(app, c, db);
        var sessions = app.Services.GetRequiredService<SessionStore>();
        var relay = app.Services.GetRequiredService<ScreenRelay>();
        var handle = relay.AddKeptForTests(me.User, new KeptInfo(Guid.NewGuid(), "S", "d"), me.App, false, alive: ct => sessions.IsAliveAsync(me.App, ct));

        await sessions.DeleteAllAsync(me.User);                     // "ueberall abmelden", Passwortwechsel, Authenticator ersetzt: alle loeschen die Zeilen
        var until = DateTime.UtcNow.AddSeconds(15);
        while (relay.FindKept(me.User, handle) is not null && DateTime.UtcNow < until) await Task.Delay(100);
        Assert.Null(relay.FindKept(me.User, handle));
    }
}
