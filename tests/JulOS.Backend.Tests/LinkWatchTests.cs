using System.Net.WebSockets;
using System.Text;
using JulOS.Integrations;
using JulOS.Integrations.Ssh;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Backend.Tests;

/// <summary>Lebenszeichen des Browsers (<see cref="LinkWatch"/>): halbtote Leitungen werden geschlossen, alte Browser ohne Ping laufen unveraendert weiter.</summary>
public sealed class LinkWatchTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Only_a_browser_that_pinged_once_can_be_declared_dead_and_the_background_gets_the_long_deadline()
    {
        long now = 0;
        var watch = new LinkWatch(() => now, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(90));
        now = 600_000;
        Assert.False(watch.IsDead);                       // an old browser never pings: never dead
        watch.MarkCapable();
        watch.Heard();
        now += 11_000;
        Assert.False(watch.IsDead);
        now += 2_000;
        Assert.True(watch.IsDead);
        watch.Heard();
        Assert.False(watch.IsDead);                       // any message counts as life
        watch.SetRelaxed(true);                           // hidden page: timers are throttled
        now += 60_000;
        Assert.False(watch.IsDead);
        now += 31_000;
        Assert.True(watch.IsDead);
    }

    private static SshRelay Relay(FakeSshSession session, int deadMs) => new(NullLogger<SshRelay>.Instance, () => session, 32, null, TimeSpan.FromMilliseconds(deadMs));

    private static SshTarget Target() => new(Guid.NewGuid(), new SshConnectOptions("ziel", 22, "tester", "geheim", 100, 30), null, null, (_, _) => Task.CompletedTask, null);

    [Fact]
    public async Task A_line_that_stops_answering_is_closed_by_the_server_and_the_shell_ends()
    {
        var session = new FakeSshSession();
        var (browser, server) = await SshRelayTests.SocketPairAsync();
        var run = Relay(session, 400).RunAsync(server, Target(), SshRelayTests.Alive, CancellationToken.None);

        var ready = await SshRelayTests.ReceiveAsync(browser);
        Assert.Equal(SshProtocol.FromReady, ready[0]);
        Assert.Equal(1, SshRelayTests.Body(ready).GetProperty("link").GetInt32());     // the server announces that it can do pings

        await SshRelayTests.SendAsync(browser, SshProtocol.ToShellPing, 0, 0, 0, 7);   // a new browser pings with a number
        var pong = await SshRelayTests.ReceiveAsync(browser);
        Assert.Equal([SshProtocol.FromPong, 0, 0, 0, 7], pong);

        // from now on silence: the server ends the connection by itself (nothing closes it from our side)
        await run.WaitAsync(Wait);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task An_old_browser_that_never_pings_is_left_alone_and_a_ping_without_number_is_harmless()
    {
        var session = new FakeSshSession();
        var (browser, server) = await SshRelayTests.SocketPairAsync();
        var run = Relay(session, 300).RunAsync(server, Target(), SshRelayTests.Alive, CancellationToken.None);
        await SshRelayTests.ReceiveAsync(browser);                                     // ready

        await Task.Delay(1200);                                                        // four times the deadline, in silence
        Assert.False(run.IsCompleted);

        await SshRelayTests.SendAsync(browser, SshProtocol.ToShellPing);               // the old empty ping
        await session.Output.Writer.WriteAsync(Encoding.UTF8.GetBytes("noch da"));
        var data = await SshRelayTests.ReceiveAsync(browser);
        Assert.Equal("noch da", Encoding.UTF8.GetString(data, 1, data.Length - 1));

        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
    }
}
