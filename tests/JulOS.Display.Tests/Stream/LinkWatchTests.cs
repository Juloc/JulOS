using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Lebenszeichen auf der Stream-Leitung: Pings werden beantwortet, ein verstummter neuer Browser wird als tot erkannt, ein alter nicht.</summary>
public sealed class StreamLinkWatchTests
{
    private static async Task<(WebSocket Browser, WebSocket Server)> PairAsync()
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

    [Fact]
    public async Task A_ping_is_answered_through_the_events_and_marks_the_browser_as_able_to_ping()
    {
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        await using var stream = new ScreenStream(new RdpOptions { Host = "rdp.invalid" });
        var link = new LinkWatch();
        var (browser, server) = await PairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receive = ScreenRelay.ReceiveFromBrowserAsync(server, stream, link, cts.Token);

        await browser.SendAsync(new byte[] { ScreenProtocol.FromBrowserPing, 0, 0, 1, 2 }, WebSocketMessageType.Binary, true, cts.Token);
        var evt = await stream.Events.ReadAsync(cts.Token);
        Assert.Equal(new ScreenLinkPong(0x0102), evt);
        Assert.True(link.Capable);
        Assert.Equal([ScreenProtocol.ToBrowserPong, 0, 0, 1, 2], ScreenProtocol.Pong(0x0102));

        await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        Assert.False(await receive);
    }

    [Fact]
    public async Task A_browser_that_stops_after_pinging_is_dead_but_one_that_never_pinged_is_not()
    {
        var old = new LinkWatch(null, TimeSpan.FromMilliseconds(300));
        var modern = new LinkWatch(null, TimeSpan.FromMilliseconds(300));
        modern.MarkCapable();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await modern.WaitDeadAsync(cts.Token);            // ends by itself
        var oldWait = old.WaitDeadAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(1000)).Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWait);   // runs until cancelled: never declared dead
    }
}
