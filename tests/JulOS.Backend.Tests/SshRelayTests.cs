using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JulOS.Integrations;
using JulOS.Integrations.Ssh;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Backend.Tests;

/// <summary>Eine Sitzung im Speicher: Eingaben kommen im Kanal an, Ausgabe wird vom Test geschrieben.</summary>
internal sealed class FakeSshSession : ISshSession
{
    public SshHostKey? Key { get; init; } = new("ssh-ed25519", "SHA256:neu");
    public SshFailure? Fail { get; init; }
    public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();
    public List<byte[]> Written { get; } = [];
    public List<(int Cols, int Rows)> Resizes { get; } = [];
    public SshConnectOptions? Options { get; private set; }
    public bool Disposed { get; private set; }
    public SshHostKey? PresentedHostKey { get; private set; }
    private byte[]? _rest;

    public Task ConnectAsync(SshConnectOptions options, Func<SshHostKey, bool> trustHostKey, CancellationToken ct)
    {
        Options = options;
        PresentedHostKey = Key;
        if (Key is not null && !trustHostKey(Key)) throw new SshException(SshFailure.HostKeyRejected, "nicht vertraut");
        if (Fail is { } f) throw new SshException(f, "Fehler");
        return Task.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_rest is null)
        {
            if (!await Output.Reader.WaitToReadAsync(ct) || !Output.Reader.TryRead(out _rest)) return 0;
        }
        var n = Math.Min(buffer.Length, _rest.Length);
        _rest.AsMemory(0, n).CopyTo(buffer);
        _rest = n == _rest.Length ? null : _rest[n..];
        return n;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) { lock (Written) Written.Add(data.ToArray()); return ValueTask.CompletedTask; }
    public void Resize(int cols, int rows) { lock (Resizes) Resizes.Add((cols, rows)); }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

public sealed class SshProtocolTests
{
    [Fact]
    public void Resize_is_read_big_endian_and_limited()
    {
        Assert.True(SshProtocol.TryReadResize([2, 0, 80, 0, 24], out var c, out var r));
        Assert.Equal((80, 24), (c, r));
        Assert.True(SshProtocol.TryReadResize([2, 0xFF, 0xFF, 0, 0], out c, out r));
        Assert.Equal((SshProtocol.MaxCols, SshProtocol.MinRows), (c, r));         // nie 0 oder riesig
        Assert.False(SshProtocol.TryReadResize([2, 0, 80], out _, out _));
        Assert.False(SshProtocol.TryReadResize([2, 0, 80, 0, 24, 9], out _, out _));
    }

    [Fact]
    public void Messages_are_json_with_a_kind_byte()
    {
        var end = SshProtocol.End(800, "Schlüssel", "SHA256:a", "ssh-ed25519 SHA256:b");
        Assert.Equal(SshProtocol.FromEnd, end[0]);
        var d = JsonDocument.Parse(end.AsMemory(1)).RootElement;
        Assert.Equal(800, d.GetProperty("code").GetInt32());
        Assert.Equal("SHA256:a", d.GetProperty("fingerprint").GetString());
        Assert.Equal("ssh-ed25519 SHA256:b", d.GetProperty("previous").GetString());
        Assert.Equal("new", JsonDocument.Parse(SshProtocol.Ready("new", "SHA256:x").AsMemory(1)).RootElement.GetProperty("hostKey").GetString());
    }
}

public sealed class HostKeyPolicyTests
{
    private static readonly SshHostKey Key = new("ssh-ed25519", "SHA256:aaa");

    [Fact]
    public void First_use_trusts_and_the_same_key_is_known()
    {
        Assert.Equal(HostKeyDecision.FirstUse, HostKeyPolicy.Decide(null, null, Key));
        Assert.Equal(HostKeyDecision.FirstUse, HostKeyPolicy.Decide("", null, Key));
        Assert.Equal(HostKeyDecision.Known, HostKeyPolicy.Decide(Key.Id, null, Key));
    }

    [Fact]
    public void A_changed_key_is_refused_unless_exactly_this_key_was_confirmed()
    {
        var stored = "ssh-ed25519 SHA256:bbb";
        Assert.Equal(HostKeyDecision.Changed, HostKeyPolicy.Decide(stored, null, Key));
        Assert.Equal(HostKeyDecision.Changed, HostKeyPolicy.Decide(stored, "ssh-ed25519 SHA256:ccc", Key));    // anderer Schluessel bestaetigt: gilt nicht
        Assert.Equal(HostKeyDecision.Changed, HostKeyPolicy.Decide(stored, stored, Key));                      // der alte Schluessel zaehlt nicht als Bestaetigung
        Assert.Equal(HostKeyDecision.AcceptedChange, HostKeyPolicy.Decide(stored, Key.Id, Key));
    }

    [Fact]
    public void Another_algorithm_with_the_same_digest_text_is_a_different_key()
        => Assert.Equal(HostKeyDecision.Changed, HostKeyPolicy.Decide("ssh-rsa SHA256:aaa", null, Key));
}

public sealed class SshRelayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static Task<bool> Alive(CancellationToken _) => Task.FromResult(true);

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

    private static SshTarget Target(string? stored = null, string? accepted = null, Func<string, CancellationToken, Task>? save = null, Guid? owner = null, string? slot = null)
        => new(owner ?? Guid.NewGuid(), new SshConnectOptions("ziel", 22, "tester", "geheim", 100, 30), stored, accepted, save ?? ((_, _) => Task.CompletedTask), slot);

    private static SshRelay Relay(FakeSshSession session, int max = 32) => new(NullLogger<SshRelay>.Instance, () => session, max);

    private static async Task<byte[]> ReceiveAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(Wait);
        var buf = new byte[70000];
        var r = await ws.ReceiveAsync(buf, cts.Token);
        Assert.Equal(WebSocketMessageType.Binary, r.MessageType);
        return buf[..r.Count];
    }

    private static Task SendAsync(WebSocket ws, params byte[] data) => ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
    private static JsonElement Body(byte[] message) => JsonDocument.Parse(message.AsMemory(1)).RootElement;

    [Fact]
    public async Task Output_goes_to_the_browser_and_input_and_resize_to_the_shell()
    {
        var session = new FakeSshSession();
        var (browser, server) = await SocketPairAsync();
        var run = Relay(session).RunAsync(server, Target(), Alive, CancellationToken.None);

        var ready = await ReceiveAsync(browser);
        Assert.Equal(SshProtocol.FromReady, ready[0]);
        Assert.Equal("new", Body(ready).GetProperty("hostKey").GetString());
        Assert.Equal((100, 30), (session.Options!.Cols, session.Options.Rows));

        await session.Output.Writer.WriteAsync(Encoding.UTF8.GetBytes("hallo ä"));
        var data = await ReceiveAsync(browser);
        Assert.Equal(SshProtocol.FromShellData, data[0]);
        Assert.Equal("hallo ä", Encoding.UTF8.GetString(data, 1, data.Length - 1));

        await SendAsync(browser, [SshProtocol.ToShellData, .. "ls\r"u8]);
        await SendAsync(browser, SshProtocol.ToShellResize, 0, 120, 0, 40);
        await SendAsync(browser, SshProtocol.ToShellPing);
        await SendAsync(browser, [SshProtocol.ToShellData, .. "x"u8]);          // Reihenfolge: nach dem Resize noch Daten
        for (var i = 0; i < 100 && session.Written.Count < 2; i++) await Task.Delay(20);
        Assert.Equal("ls\r", Encoding.UTF8.GetString(session.Written[0]));
        Assert.Equal([(120, 40)], session.Resizes);

        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Pause_stops_reading_the_shell_until_resume()
    {
        var session = new FakeSshSession();
        var (browser, server) = await SocketPairAsync();
        var run = Relay(session).RunAsync(server, Target(), Alive, CancellationToken.None);
        await ReceiveAsync(browser);
        await SendAsync(browser, SshProtocol.ToShellPause);
        await Task.Delay(200);
        await session.Output.Writer.WriteAsync("x"u8.ToArray());      // ein Stueck war schon beim Lesen: es kommt noch an, danach ist Ruhe
        Assert.Equal((byte)'x', (await ReceiveAsync(browser))[1]);
        await session.Output.Writer.WriteAsync("a"u8.ToArray());
        var buf = new byte[100];
        var pending = browser.ReceiveAsync(buf, CancellationToken.None);
        Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(400)));                    // nichts kommt an
        await SendAsync(browser, SshProtocol.ToShellResume);
        var r = await pending.WaitAsync(Wait);
        Assert.Equal("a", Encoding.UTF8.GetString(buf, 1, r.Count - 1));
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task A_new_host_key_is_saved_after_login_and_a_known_one_is_not_saved_again()
    {
        var saved = new List<string>();
        foreach (var stored in new string?[] { null, "ssh-ed25519 SHA256:neu" })
        {
            var (browser, server) = await SocketPairAsync();
            var run = Relay(new FakeSshSession()).RunAsync(server, Target(stored, save: (k, _) => { saved.Add(k); return Task.CompletedTask; }), Alive, CancellationToken.None);
            var ready = Body(await ReceiveAsync(browser));
            Assert.Equal(stored is null ? "new" : "known", ready.GetProperty("hostKey").GetString());
            Assert.Equal("SHA256:neu", ready.GetProperty("fingerprint").GetString());
            await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            await run.WaitAsync(Wait);
        }
        Assert.Equal(["ssh-ed25519 SHA256:neu"], saved);
    }

    [Fact]
    public async Task A_changed_host_key_is_refused_with_both_fingerprints_and_nothing_is_saved()
    {
        var saved = 0;
        var session = new FakeSshSession();
        var (browser, server) = await SocketPairAsync();
        var run = Relay(session).RunAsync(server, Target("ssh-ed25519 SHA256:alt", save: (_, _) => { saved++; return Task.CompletedTask; }), Alive, CancellationToken.None);
        var end = await ReceiveAsync(browser);
        Assert.Equal(SshProtocol.FromEnd, end[0]);
        var d = Body(end);
        Assert.Equal(SshProtocol.ErrorHostKeyChanged, d.GetProperty("code").GetInt32());
        Assert.Equal("ssh-ed25519 SHA256:neu", d.GetProperty("fingerprint").GetString());
        Assert.Equal("ssh-ed25519 SHA256:alt", d.GetProperty("previous").GetString());
        await run.WaitAsync(Wait);
        Assert.Equal(0, saved);
        Assert.DoesNotContain("geheim", Encoding.UTF8.GetString(end));
    }

    [Fact]
    public async Task A_confirmed_changed_host_key_connects_and_replaces_the_stored_one()
    {
        var saved = new List<string>();
        var (browser, server) = await SocketPairAsync();
        var run = Relay(new FakeSshSession()).RunAsync(server, Target("ssh-ed25519 SHA256:alt", "ssh-ed25519 SHA256:neu", (k, _) => { saved.Add(k); return Task.CompletedTask; }), Alive, CancellationToken.None);
        Assert.Equal("changed-accepted", Body(await ReceiveAsync(browser)).GetProperty("hostKey").GetString());
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(Wait);
        Assert.Equal(["ssh-ed25519 SHA256:neu"], saved);
    }

    [Theory]
    [InlineData(SshFailure.LogonFailed, 769)]
    [InlineData(SshFailure.Timeout, 514)]
    [InlineData(SshFailure.Unreachable, 520)]
    [InlineData(SshFailure.Failed, 515)]
    public async Task Connection_failures_become_german_messages_with_codes(SshFailure failure, int code)
    {
        var (browser, server) = await SocketPairAsync();
        var run = Relay(new FakeSshSession { Fail = failure, Key = null }).RunAsync(server, Target(), Alive, CancellationToken.None);
        var d = Body(await ReceiveAsync(browser));
        Assert.Equal(code, d.GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("message").GetString()));
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task A_finished_shell_ends_with_a_message_and_closes()
    {
        var session = new FakeSshSession();
        var (browser, server) = await SocketPairAsync();
        var run = Relay(session).RunAsync(server, Target(), Alive, CancellationToken.None);
        await ReceiveAsync(browser);
        session.Output.Writer.Complete();
        var d = Body(await ReceiveAsync(browser));
        Assert.Equal(SshProtocol.EndShellClosed, d.GetProperty("code").GetInt32());
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task The_connection_ends_when_the_app_session_ends()
    {
        var alive = true;
        var (browser, server) = await SocketPairAsync();
        var run = Relay(new FakeSshSession()).RunAsync(server, Target(), _ => Task.FromResult(alive), CancellationToken.None, sessionCheck: TimeSpan.FromMilliseconds(100));
        await ReceiveAsync(browser);
        alive = false;
        Assert.Equal(SshProtocol.EndSessionExpired, Body(await ReceiveAsync(browser)).GetProperty("code").GetInt32());
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task Invalid_messages_close_the_connection()
    {
        foreach (var bad in new byte[][] { [9, 1, 2], [SshProtocol.ToShellData] })
        {
            var (browser, server) = await SocketPairAsync();
            var run = Relay(new FakeSshSession()).RunAsync(server, Target(), Alive, CancellationToken.None);
            await ReceiveAsync(browser);
            await SendAsync(browser, bad);
            await run.WaitAsync(Wait);
        }
        // zu lang
        var (b2, s2) = await SocketPairAsync();
        var run2 = Relay(new FakeSshSession()).RunAsync(s2, Target(), Alive, CancellationToken.None);
        await ReceiveAsync(b2);
        await SendAsync(b2, [SshProtocol.ToShellData, .. new byte[SshProtocol.MaxClientMessage]]);
        await run2.WaitAsync(Wait);
    }

    [Fact]
    public async Task Limits_apply_per_user_and_in_total_and_slots_are_released()
    {
        var owner = Guid.NewGuid();
        var registry = new SessionRegistry(2);
        var relay = new SshRelay(NullLogger<SshRelay>.Instance, () => new FakeSshSession(), maxSessions: 3, registry);
        var conn = Guid.NewGuid();
        string Slot(string sid) => SessionRegistry.SlotOf(owner, conn, sid);
        var open = new List<(WebSocket B, Task Run)>();
        foreach (var sid in new[] { "sitzung-eins", "sitzung-zwei" })        // zwei Sitzungen zum selben Ziel laufen nebeneinander
        {
            var (b, s) = await SocketPairAsync();
            open.Add((b, relay.RunAsync(s, Target(owner: owner, slot: Slot(sid)), Alive, CancellationToken.None)));
            Assert.Equal(SshProtocol.FromReady, (await ReceiveAsync(b))[0]);
        }

        // dritte Sitzung desselben Benutzers: abgelehnt mit freundlichem Text, ein anderer Benutzer darf noch
        var (b3, s3) = await SocketPairAsync();
        var run3 = relay.RunAsync(s3, Target(owner: owner, slot: Slot("sitzung-drei")), Alive, CancellationToken.None);
        var refused = Body(await ReceiveAsync(b3));
        Assert.Equal(SessionRegistry.TooManyStatus, refused.GetProperty("code").GetInt32());
        Assert.Contains("2 Sitzungen", refused.GetProperty("message").GetString());
        await run3.WaitAsync(Wait);

        var (b4, s4) = await SocketPairAsync();
        open.Add((b4, relay.RunAsync(s4, Target(), Alive, CancellationToken.None)));
        await ReceiveAsync(b4);
        var (b5, s5) = await SocketPairAsync();
        var run5 = relay.RunAsync(s5, Target(), Alive, CancellationToken.None);                  // insgesamt 3: voll
        Assert.Equal(SshProtocol.ErrorBusy, Body(await ReceiveAsync(b5)).GetProperty("code").GetInt32());
        await run5.WaitAsync(Wait);

        // eine schliessen: der Platz ist wieder frei
        await open[0].B.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await open[0].Run.WaitAsync(Wait);
        var (b6, s6) = await SocketPairAsync();
        open.Add((b6, relay.RunAsync(s6, Target(owner: owner, slot: Slot("sitzung-drei")), Alive, CancellationToken.None)));
        Assert.Equal(SshProtocol.FromReady, (await ReceiveAsync(b6))[0]);

        foreach (var (b, run) in open.Skip(1)) { await b.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); await run.WaitAsync(Wait); }
    }

    [Fact]
    public async Task A_new_connection_in_the_same_slot_replaces_the_old_one_which_is_told_why()
    {
        var owner = Guid.NewGuid();
        var slot = SessionRegistry.SlotOf(owner, Guid.NewGuid(), "meine-sitzung");
        var first = new FakeSshSession();
        var (b1, s1) = await SocketPairAsync();
        var registry = new SessionRegistry(2);                                  // beide Relays teilen eine Registry, wie im Server
        var relayA = new SshRelay(NullLogger<SshRelay>.Instance, () => first, 32, registry);
        var runA = relayA.RunAsync(s1, Target(owner: owner, slot: slot), Alive, CancellationToken.None);
        await ReceiveAsync(b1);

        var second = new FakeSshSession();
        var (b2, s2) = await SocketPairAsync();
        var runB = new SshRelay(NullLogger<SshRelay>.Instance, () => second, 32, registry).RunAsync(s2, Target(owner: owner, slot: slot), Alive, CancellationToken.None);
        Assert.Equal(SshProtocol.FromReady, (await ReceiveAsync(b2))[0]);

        Assert.Equal(SshProtocol.EndTaken, Body(await ReceiveAsync(b1)).GetProperty("code").GetInt32());   // der alte Browser erfaehrt es
        await runA.WaitAsync(Wait);
        Assert.True(first.Disposed);                                                                          // und seine Shell auf dem Ziel endet
        Assert.False(second.Disposed);                                                                        // die neue laeuft weiter

        await b2.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await runB.WaitAsync(Wait);
    }
}

public sealed class SshTicketTests
{
    [Fact]
    public void A_terminal_ticket_works_once_for_its_user()
    {
        var tickets = new SshTickets(TimeProvider.System);
        var me = Guid.NewGuid();
        var target = new SshTarget(me, new SshConnectOptions("h", 22, "u", "p", 80, 24), null, null, (_, _) => Task.CompletedTask);
        var t = tickets.Create(me, target)!;
        Assert.Null(tickets.Take(t, Guid.NewGuid()));
        Assert.Null(tickets.Take(t, me));
        var t2 = tickets.Create(me, target)!;
        Assert.Same(target, tickets.Take(t2, me));
        Assert.Null(tickets.Take(t2, me));
    }
}
