using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using JulOS.Integrations.Guacamole;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Backend.Tests;

public sealed class TicketTests
{
    private static readonly GuacSpec Spec = GuacSpec.Terminal("h", 22, "u", "p", 800, 600, 96);

    [Fact]
    public void A_ticket_works_once_and_only_for_its_user()
    {
        var tickets = new GuacTickets(TimeProvider.System);
        var me = Guid.NewGuid();
        var t = tickets.Create(me, Spec)!;
        Assert.Null(tickets.Take(t, Guid.NewGuid()));        // fremder Benutzer: verbraucht das Ticket auch
        Assert.Null(tickets.Take(t, me));

        var t2 = tickets.Create(me, Spec)!;
        Assert.Same(Spec, tickets.Take(t2, me));
        Assert.Null(tickets.Take(t2, me));                   // zweites Mal nicht
        Assert.Null(tickets.Take("gibt-es-nicht", me));
    }

    [Fact]
    public void A_ticket_expires_after_30_seconds_and_is_swept()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var tickets = new GuacTickets(time);
        var me = Guid.NewGuid();
        var a = tickets.Create(me, Spec)!;
        var b = tickets.Create(me, Spec)!;
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.NotNull(tickets.Take(a, me));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(tickets.Take(b, me));

        var c = tickets.Create(me, Spec)!;
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, tickets.Sweep());
        Assert.Null(tickets.Take(c, me));
    }

    [Fact]
    public void One_user_cannot_hold_unlimited_tickets()
    {
        var tickets = new GuacTickets(TimeProvider.System);
        var me = Guid.NewGuid();
        for (var i = 0; i < GuacTickets.MaxPerUser; i++) Assert.NotNull(tickets.Create(me, Spec));
        Assert.Null(tickets.Create(me, Spec));
        Assert.NotNull(tickets.Create(Guid.NewGuid(), Spec));
    }
}

public sealed class RelayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Zwei verbundene WebSockets ohne HTTP: (Browser-Seite, Server-Seite).</summary>
    private static async Task<(WebSocket Browser, WebSocket Server)> SocketPairAsync()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var accept = l.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
        var server = await accept;
        l.Stop();
        return (WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false }),
                WebSocket.CreateFromStream(server.GetStream(), new WebSocketCreationOptions { IsServer = true }));
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(Wait);
        var buf = new byte[70000];
        var r = await ws.ReceiveAsync(buf, cts.Token);
        Assert.Equal(WebSocketMessageType.Text, r.MessageType);
        return Encoding.UTF8.GetString(buf, 0, r.Count);
    }

    private static Task SendTextAsync(WebSocket ws, byte[] data) => ws.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);

    private static GuacRelay NewRelay(FakeGuacd g) => new(new GuacdOptions("127.0.0.1", g.Port), NullLogger<GuacRelay>.Instance);
    private static Task<bool> Alive(CancellationToken _) => Task.FromResult(true);

    [Fact]
    public async Task Handshake_uses_the_values_guacd_asks_for_and_the_browser_gets_its_internal_id()
    {
        await using var guacd = new FakeGuacd { Greeting = GuacProtocol.Encode("sync", "1") };
        var (browser, server) = await SocketPairAsync();
        var spec = GuacSpec.Terminal("ziel", 2222, "tester", "geheim", 856, 1448, 96);
        var run = NewRelay(guacd).RunAsync(server, spec, Alive, CancellationToken.None);

        Assert.Equal("0.,18.fake-connection-id;", await ReceiveTextAsync(browser));       // interne Kennung zuerst
        Assert.Equal("4.sync,1.1;", await ReceiveTextAsync(browser));

        Assert.Equal(["select", "ssh"], guacd.Handshake[0]);
        Assert.Equal(["size", "856", "1448", "96"], guacd.Handshake[1]);
        Assert.Equal(["audio"], guacd.Handshake[2]);
        Assert.Equal(["video"], guacd.Handshake[3]);
        Assert.Equal("image", guacd.Handshake[4][0]);
        // Werte in der Reihenfolge der Namen, die guacd gemeldet hat; unbekannte Namen leer, die Version wird zurueckgegeben
        Assert.Equal(["connect", "VERSION_1_5_0", "ziel", "2222", "tester", "geheim", ""], guacd.Handshake[5]);

        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task Instructions_flow_both_ways_but_the_tunnel_ping_stays_at_the_server()
    {
        await using var guacd = new FakeGuacd();
        var (browser, server) = await SocketPairAsync();
        var run = NewRelay(guacd).RunAsync(server, GuacSpec.Terminal("h", 22, "u", "p", 800, 600, 96), Alive, CancellationToken.None);
        await ReceiveTextAsync(browser);

        await SendTextAsync(browser, GuacProtocol.Encode("", "ping", "123"));            // darf nicht bei guacd ankommen
        await SendTextAsync(browser, GuacProtocol.Encode("mouse", "10", "20", "1"));
        await SendTextAsync(browser, GuacProtocol.Encode("key", "65", "1").Concat(GuacProtocol.Encode("key", "65", "0")).ToArray());  // mehrere in einer Nachricht
        await SendTextAsync(browser, GuacProtocol.Encode("clipboard", "a;b"));
        await WaitUntilAsync(() => { lock (guacd.AfterReady) return guacd.AfterReady.Count >= 4; });

        lock (guacd.AfterReady)
            Assert.Equal(new[] { "mouse", "key", "key", "clipboard" }, guacd.AfterReady.Select(i => i[0]));

        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
        await guacd.Closed.Task.WaitAsync(Wait);                                         // Browser weg => guacd-Verbindung zu
    }

    [Fact]
    public async Task Broken_messages_from_the_browser_end_the_connection()
    {
        await using var guacd = new FakeGuacd();
        var (browser, server) = await SocketPairAsync();
        var run = NewRelay(guacd).RunAsync(server, GuacSpec.Terminal("h", 22, "u", "p", 800, 600, 96), Alive, CancellationToken.None);
        await ReceiveTextAsync(browser);

        await SendTextAsync(browser, Encoding.UTF8.GetBytes("6.select,3.rd"));            // abgeschnitten
        await run.WaitAsync(Wait);
        await guacd.Closed.Task.WaitAsync(Wait);
        lock (guacd.AfterReady) Assert.Empty(guacd.AfterReady);
    }

    [Fact]
    public async Task A_big_burst_from_guacd_arrives_in_whole_instructions()
    {
        // viele Anweisungen mit Semikolons im Text; der Browser darf nie eine halbe Anweisung bekommen
        var burst = new List<byte>();
        for (var i = 0; i < 2000; i++) burst.AddRange(GuacProtocol.Encode("blob", i.ToString(), new string('x', 200 + i % 50) + ";漢"));
        await using var guacd = new FakeGuacd { Greeting = [.. burst] };
        var (browser, server) = await SocketPairAsync();
        var run = NewRelay(guacd).RunAsync(server, GuacSpec.Terminal("h", 22, "u", "p", 800, 600, 96), Alive, CancellationToken.None);
        await ReceiveTextAsync(browser);

        var seen = 0;
        while (seen < 2000)
        {
            var text = await ReceiveTextAsync(browser);
            var s = new InstructionScanner();
            var bytes = Encoding.UTF8.GetBytes(text);
            Assert.Equal(bytes.Length - 1, s.Scan(bytes));
            seen += text.Split("4.blob").Length - 1;
        }
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task A_refusal_from_guacd_reaches_the_browser_as_an_error_instruction()
    {
        await using var guacd = new FakeGuacd { Reject = ("Anmeldung abgelehnt", 769) };
        var (browser, server) = await SocketPairAsync();
        var run = NewRelay(guacd).RunAsync(server, GuacSpec.Desktop("h", 3389, "u", "falsch", 800, 600, 96), Alive, CancellationToken.None);

        await ReceiveTextAsync(browser);                                                   // interne Kennung, damit der Tunnel den Fehler lesen kann
        Assert.Equal("5.error,19.Anmeldung abgelehnt,3.769;", await ReceiveTextAsync(browser));
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task An_unreachable_guacd_is_reported_as_error_not_as_a_crash()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();                          // Port, auf dem niemand lauscht
        var (browser, server) = await SocketPairAsync();
        var run = new GuacRelay(new GuacdOptions("127.0.0.1", port), NullLogger<GuacRelay>.Instance)
            .RunAsync(server, GuacSpec.Terminal("h", 22, "u", "p", 800, 600, 96), Alive, CancellationToken.None);
        await ReceiveTextAsync(browser);
        var error = await ReceiveTextAsync(browser);
        Assert.StartsWith("5.error,", error);
        Assert.EndsWith(",3.520;", error);
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task The_connection_ends_when_the_session_is_no_longer_valid()
    {
        await using var guacd = new FakeGuacd();
        var (browser, server) = await SocketPairAsync();
        var alive = true;
        var relay = new GuacRelay(new GuacdOptions("127.0.0.1", guacd.Port), NullLogger<GuacRelay>.Instance);
        var run = relay.RunAsync(server, GuacSpec.Terminal("h", 22, "u", "p", 800, 600, 96), _ => Task.FromResult(alive), CancellationToken.None, sessionCheck: TimeSpan.FromMilliseconds(100));
        await ReceiveTextAsync(browser);
        alive = false;
        await run.WaitAsync(Wait);
        await guacd.Closed.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Too_many_connections_are_refused_politely()
    {
        await using var first = new FakeGuacd();
        var relay = new GuacRelay(new GuacdOptions("127.0.0.1", first.Port, MaxConnections: 1), NullLogger<GuacRelay>.Instance);
        var (b1, s1) = await SocketPairAsync();
        var run1 = relay.RunAsync(s1, GuacSpec.Terminal("h", 22, "u", "p", 800, 600, 96), Alive, CancellationToken.None);
        await ReceiveTextAsync(b1);

        var (b2, s2) = await SocketPairAsync();
        await relay.RunAsync(s2, GuacSpec.Terminal("h", 22, "u", "p", 800, 600, 96), Alive, CancellationToken.None);
        await ReceiveTextAsync(b2);
        Assert.EndsWith(",3.513;", await ReceiveTextAsync(b2));

        await b1.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run1.WaitAsync(Wait);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition()) { Assert.True(DateTime.UtcNow < until, "Zeit abgelaufen"); await Task.Delay(20); }
    }
}
