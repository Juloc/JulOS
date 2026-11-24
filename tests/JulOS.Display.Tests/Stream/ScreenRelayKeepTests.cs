using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using JulOS.Integrations.Display.Rdp;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display.Tests;

/// <summary>
/// Sitzungserhalt (<see cref="ScreenRelay"/>) gegen den echten xrdp-Desktop der Test-Instanz: reisst der WebSocket ab, lebt die RDP-Sitzung weiter,
/// ein neuer WebSocket mit dem Token nimmt sie wieder auf (Schluesselbild, kein neuer Login), und alles andere beendet sie wirklich.
/// Die WebSockets sind im Speicher; ohne JULOS_TEST_RDP_HOST (deploy/dev/test-display.sh) werden die Tests uebersprungen.
/// </summary>
public sealed class ScreenRelayKeepTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(40);

    private static string Host()
    {
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_HOST");
        Assert.SkipWhen(host is null, "JULOS_TEST_RDP_HOST nicht gesetzt (deploy/dev/test-display.sh benutzen)");
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        return host!;
    }

    private static ScreenTarget Target(string host, string? slot = "u:c:s1") =>
        new(new RdpOptions { Host = host, UserName = "ubuntu", Password = "ubuntu", Width = 1024, Height = 600 }, slot);

    // ---- ein WebSocket-Paar im Speicher ----

    private sealed class PairStream(PipeReader read, PipeWriter write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var result = await read.ReadAsync(ct);
            var data = result.Buffer;
            var count = (int)Math.Min(buffer.Length, data.Length);
            data.Slice(0, count).CopyTo(buffer.Span);
            read.AdvanceTo(data.GetPosition(count));
            return count == 0 && result.IsCompleted ? 0 : count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await write.WriteAsync(buffer, ct);
        }

        // Die Leitung reisst ab (ohne Abschluss des WebSocket): beide Richtungen enden
        public void Cut()
        {
            write.Complete();
            read.Complete();
        }
    }

    private sealed class Browser
    {
        public required WebSocket Server;
        public required WebSocket Client;
        public required PairStream ServerSide;
        public required PairStream ClientSide;

        public static Browser Create()
        {
            var up = new Pipe();          // Browser -> Server
            var down = new Pipe();        // Server -> Browser
            var serverSide = new PairStream(up.Reader, down.Writer);
            var clientSide = new PairStream(down.Reader, up.Writer);
            return new Browser
            {
                ServerSide = serverSide, ClientSide = clientSide,
                Server = WebSocket.CreateFromStream(serverSide, new WebSocketCreationOptions { IsServer = true }),
                Client = WebSocket.CreateFromStream(clientSide, new WebSocketCreationOptions { IsServer = false }),
            };
        }

        // Die naechste Nachricht, die der Server schickt, oder null, wenn die Leitung zu ist
        public async Task<byte[]?> ReadAsync(CancellationToken ct)
        {
            var buffer = new byte[256 * 1024];
            var used = 0;
            while (true)
            {
                ValueWebSocketReceiveResult r;
                try { r = await Client.ReceiveAsync(buffer.AsMemory(used), ct); }
                catch (WebSocketException) { return null; }
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                used += r.Count;
                if (r.EndOfMessage)
                {
                    return buffer[..used];
                }
            }
        }

        public async Task<byte[]> UntilAsync(Func<byte[], bool> match, CancellationToken ct)
        {
            while (await ReadAsync(ct) is { } message)
            {
                if (match(message))
                {
                    return message;
                }
            }

            throw new InvalidOperationException("Die Leitung schloss, ohne dass die erwartete Nachricht kam.");
        }

        public Task SendAsync(params byte[] message) => Client.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    private sealed class TestLog : ILogger<ScreenRelay>
    {
        public readonly List<string> Lines = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception));
            }
        }

        public int Count(string part)
        {
            lock (Lines)
            {
                return Lines.Count(l => l.Contains(part, StringComparison.Ordinal));
            }
        }
    }

    // Nachricht 11: "Token Kennung" (das Token zum Wiederaufnehmen, die oeffentliche Kennung zum Nehmen nach einem Neuladen)
    private static string TokenOf(byte[] session) => System.Text.Encoding.ASCII.GetString(session, 3, session.Length - 3).Split(' ')[0];

    private static string HandleOf(byte[] session) => System.Text.Encoding.ASCII.GetString(session, 3, session.Length - 3).Split(' ')[1];

    private static bool IsKeyPicture(byte[] m) => m[0] == ScreenProtocol.ToBrowserPicture && (m[1] & 1) != 0;

    private static Task<bool> Alive(CancellationToken _) => Task.FromResult(true);

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 15)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Zeit abgelaufen: " + what);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_dropped_connection_keeps_the_session_and_a_resume_gets_a_key_frame_without_a_new_login()
    {
        var host = Host();
        var log = new TestLog();
        using var relay = new ScreenRelay(log, keep: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource(Wait);
        var owner = Guid.NewGuid();

        var first = Browser.Create();
        var run1 = relay.RunAsync(first.Server, Target(host), Alive, CancellationToken.None, owner: owner);
        var session = await first.UntilAsync(m => m[0] == ScreenProtocol.ToBrowserSession, cts.Token);
        var token = TokenOf(session);
        Assert.Equal(60, (session[1] << 8) | session[2]);
        Assert.True(token.Length >= 20);
        await first.UntilAsync(IsKeyPicture, cts.Token);

        first.ClientSide.Cut();                         // Netzwechsel: die Leitung ist weg
        await run1.WaitAsync(Wait);
        await WaitFor(() => relay.DetachedCount == 1, "die Sitzung wartet");
        Assert.Equal(1, log.Count("Bildschirm-Verbindung offen"));

        // Der neue Browser nimmt sie wieder auf: Groesse, dann sofort ein Schluesselbild
        var second = Browser.Create();
        var run2 = relay.RunAsync(second.Server, null, Alive, CancellationToken.None, owner: owner, resume: token);
        await second.UntilAsync(m => m[0] == ScreenProtocol.ToBrowserSize, cts.Token);
        await second.UntilAsync(IsKeyPicture, cts.Token);
        Assert.Equal(0, relay.DetachedCount);
        Assert.Equal(1, log.Count("Bildschirm-Verbindung offen"));      // kein zweiter RDP-Aufbau
        Assert.Equal(1, log.Count("Sitzung wieder aufgenommen"));

        // Ein ausdrueckliches Beenden haelt nichts zurueck
        await second.SendAsync(ScreenProtocol.FromBrowserEnd);
        await run2.WaitAsync(Wait);
        Assert.Equal(0, relay.DetachedCount);

        var third = Browser.Create();
        await relay.RunAsync(third.Server, null, Alive, CancellationToken.None, owner: owner, resume: token);
        var refused = await third.ReadAsync(cts.Token);
        Assert.Equal(ScreenProtocol.ToBrowserResumeFailed, refused![0]);
    }

    [Fact]
    public async Task A_waiting_session_is_taken_by_its_public_handle_for_its_owner_only_with_a_key_frame_and_no_new_login()
    {
        var host = Host();
        var log = new TestLog();
        using var relay = new ScreenRelay(log, keep: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource(Wait);
        var owner = Guid.NewGuid();
        var firstApp = Guid.NewGuid();

        var first = Browser.Create();
        var run1 = relay.RunAsync(first.Server, Target(host) with { Info = new KeptInfo(Guid.NewGuid(), "Test", "Firefox, Linux") }, Alive, CancellationToken.None, owner: owner, appSession: firstApp);
        var session = await first.UntilAsync(m => m[0] == ScreenProtocol.ToBrowserSession, cts.Token);
        var handle = HandleOf(session);
        Assert.NotEqual(TokenOf(session), handle);                       // die Kennung ist nie das Token der Engine
        Assert.Equal(22, handle.Length);                                 // 128 Bit
        await first.UntilAsync(IsKeyPicture, cts.Token);
        first.ClientSide.Cut();
        await run1.WaitAsync(Wait);
        await WaitFor(() => relay.DetachedCount == 1, "die Sitzung wartet");
        Assert.NotNull(relay.FindKept(owner, handle)!.UnwatchedSince);

        // Eine fremde Person bekommt dieselbe Antwort wie bei einer unbekannten Kennung, die Sitzung bleibt
        var stranger = Browser.Create();
        await relay.RunAsync(stranger.Server, null, Alive, CancellationToken.None, owner: Guid.NewGuid(), takeHandle: handle);
        Assert.Equal(ScreenProtocol.ToBrowserResumeFailed, (await stranger.ReadAsync(cts.Token))![0]);
        Assert.Equal(1, relay.DetachedCount);
        Assert.Null(relay.FindKept(Guid.NewGuid(), handle));

        // Die Besitzerin nimmt sie von einer anderen App-Sitzung aus: Schluesselbild, kein zweiter Login, und ab jetzt zaehlt deren Abmeldung
        var secondApp = Guid.NewGuid();
        var second = Browser.Create();
        var run2 = relay.RunAsync(second.Server, null, Alive, CancellationToken.None, owner: owner, appSession: secondApp, takeHandle: handle);
        await second.UntilAsync(IsKeyPicture, cts.Token);
        Assert.Equal(1, log.Count("Bildschirm-Verbindung offen"));
        Assert.Equal(secondApp, relay.FindKept(owner, handle)!.AppSession);
        Assert.Null(relay.FindKept(owner, handle)!.UnwatchedSince);

        var ended = await relay.EndByAppSessionAsync(secondApp);
        Assert.Single(ended);
        await run2.WaitAsync(Wait);
        Assert.Null(relay.FindKept(owner, handle));
    }

    [Fact]
    public async Task A_resume_while_the_old_connection_is_still_there_takes_over()
    {
        var host = Host();
        using var relay = new ScreenRelay(new TestLog(), keep: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource(Wait);
        var owner = Guid.NewGuid();

        var first = Browser.Create();
        var run1 = relay.RunAsync(first.Server, Target(host), Alive, CancellationToken.None, owner: owner);
        var token = TokenOf(await first.UntilAsync(m => m[0] == ScreenProtocol.ToBrowserSession, cts.Token));
        await first.UntilAsync(IsKeyPicture, cts.Token);

        // Das Handy hat die alte Leitung noch nicht als tot erkannt, die neue steht schon
        var second = Browser.Create();
        var run2 = relay.RunAsync(second.Server, null, Alive, CancellationToken.None, owner: owner, resume: token);
        await second.UntilAsync(IsKeyPicture, cts.Token);
        await first.UntilAsync(m => m[0] == ScreenProtocol.ToBrowserTaken, cts.Token);      // der abgeloeste Betrachter erfaehrt es ("uebernommen"), sonst gaebe es Ping-Pong
        await run1.WaitAsync(Wait);
        Assert.Equal(0, relay.DetachedCount);

        await second.SendAsync(ScreenProtocol.FromBrowserEnd);
        await run2.WaitAsync(Wait);
    }

    [Fact]
    public async Task A_waiting_session_ends_after_the_keep_time_and_a_stranger_cannot_take_it()
    {
        var host = Host();
        var log = new TestLog();
        using var relay = new ScreenRelay(log, keep: TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(Wait);
        var owner = Guid.NewGuid();

        var first = Browser.Create();
        var run1 = relay.RunAsync(first.Server, Target(host), Alive, CancellationToken.None, owner: owner);
        var token = TokenOf(await first.UntilAsync(m => m[0] == ScreenProtocol.ToBrowserSession, cts.Token));
        await first.UntilAsync(IsKeyPicture, cts.Token);
        first.ClientSide.Cut();
        await run1.WaitAsync(Wait);
        await WaitFor(() => relay.DetachedCount == 1, "die Sitzung wartet");

        // Ein anderer Benutzer mit demselben Token bekommt dieselbe Antwort wie bei einem unbekannten, und die Sitzung bleibt unberuehrt
        var stranger = Browser.Create();
        await relay.RunAsync(stranger.Server, null, Alive, CancellationToken.None, owner: Guid.NewGuid(), resume: token);
        Assert.Equal(ScreenProtocol.ToBrowserResumeFailed, (await stranger.ReadAsync(cts.Token))![0]);
        Assert.Equal(1, relay.DetachedCount);

        await WaitFor(() => relay.DetachedCount == 0, "die Sitzung laeuft ab");
        Assert.Equal(1, log.Count("Wartende Sitzung abgelaufen"));
        var late = Browser.Create();
        await relay.RunAsync(late.Server, null, Alive, CancellationToken.None, owner: owner, resume: token);
        Assert.Equal(ScreenProtocol.ToBrowserResumeFailed, (await late.ReadAsync(cts.Token))![0]);
    }

    [Fact]
    public async Task Without_keep_the_session_ends_with_the_connection_as_before()
    {
        var host = Host();
        using var relay = new ScreenRelay(new TestLog(), keep: null);
        using var cts = new CancellationTokenSource(Wait);

        var first = Browser.Create();
        var run1 = relay.RunAsync(first.Server, Target(host), Alive, CancellationToken.None);
        var message = await first.UntilAsync(m => m[0] is ScreenProtocol.ToBrowserSession or ScreenProtocol.ToBrowserPicture, cts.Token);
        Assert.Equal(ScreenProtocol.ToBrowserPicture, message[0]);       // kein Token angeboten
        first.ClientSide.Cut();
        await run1.WaitAsync(Wait);
        Assert.Equal(0, relay.DetachedCount);
    }

    [Fact]
    public async Task A_logged_out_app_session_ends_a_waiting_session()
    {
        var host = Host();
        using var relay = new ScreenRelay(new TestLog(), keep: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource(Wait);
        var alive = true;

        var first = Browser.Create();
        var run1 = relay.RunAsync(first.Server, Target(host), _ => Task.FromResult(alive), CancellationToken.None, owner: Guid.NewGuid());
        await first.UntilAsync(IsKeyPicture, cts.Token);
        first.ClientSide.Cut();
        await run1.WaitAsync(Wait);
        await WaitFor(() => relay.DetachedCount == 1, "die Sitzung wartet");

        alive = false;                                   // Abmeldung: die wartende Sitzung merkt es bei der naechsten Pruefung (ScreenRelay.WaitCheck)
        await WaitFor(() => relay.DetachedCount == 0, "die Sitzung endet mit der Abmeldung", 30);
    }
}
