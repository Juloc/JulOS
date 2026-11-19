using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using JulOS.Integrations.Display.Rdp;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Integrations.Display.Tests;

/// <summary>
/// Anmeldung in der Sitzung, Seite des Servers: verlangt das Ziel Anmeldedaten, bevor es etwas zeigt, und es gibt keine, meldet der Server das als
/// eigenen Fehlercode (770), damit der Browser neu fragen kann; eine abgelehnte Anmeldung bleibt 769. Das Ziel ist ein kleiner Server im Test,
/// der nur die Aushandlung beantwortet (Fehler "Netzwerkanmeldung erforderlich").
/// </summary>
public sealed class LoginFailureTests
{
    private sealed class NoHandler : IRdpHandler
    {
        public void Updated(int x, int y, int width, int height) { }

        public void Resized(int width, int height) { }

        public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba) { }

        public void CursorHidden() { }

        public void CursorDefault() { }
    }

    // Antwortet auf die Verbindungsanfrage mit RDP_NEG_FAILURE (Code 5: Netzwerkanmeldung erforderlich) und haelt die Leitung dann offen
    private sealed class DemandingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public DemandingServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (Exception) { return; }
                    _ = Task.Run(() => Answer(client));
                }
            });
        }

        private async Task Answer(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4];
                await stream.ReadExactlyAsync(buffer, _stop.Token);
                var rest = new byte[BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2)) - 4];
                await stream.ReadExactlyAsync(rest, _stop.Token);
                byte[] reply = [0x03, 0x00, 0x00, 0x13, 0x0E, 0xD0, 0x00, 0x00, 0x12, 0x34, 0x00, 0x03, 0x00, 0x08, 0x00, 0x05, 0x00, 0x00, 0x00];
                await stream.WriteAsync(reply, _stop.Token);
                try { await Task.Delay(Timeout.Infinite, _stop.Token); }
                catch (OperationCanceledException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static async Task<RdpException> ConnectAsync(DemandingServer server, string user, string password)
    {
        var options = new RdpOptions { Host = "127.0.0.1", Port = server.Port, UserName = user, Password = password, ConnectTimeout = TimeSpan.FromSeconds(5) };
        await using var client = new RdpClient(options, new NoHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await Assert.ThrowsAsync<RdpException>(() => client.ConnectAsync(cts.Token));
    }

    [Fact]
    public async Task A_target_that_wants_a_sign_in_first_is_told_apart_from_a_rejected_one()
    {
        using var server = new DemandingServer();

        var missing = await ConnectAsync(server, "", "");
        Assert.True(missing.IsCredentialsMissing);
        Assert.True(missing.IsLogonFailure);

        var halfGiven = await ConnectAsync(server, "anna", "");      // a user without a password is as good as none
        Assert.True(halfGiven.IsCredentialsMissing);

        // with a user and a password the client already asks for the network sign-in; what is left over is a kind it cannot do, no question for the user
        var given = await ConnectAsync(server, "anna", "geheim");
        Assert.False(given.IsCredentialsMissing);
        Assert.False(given.IsLogonFailure);
    }

    [Fact]
    public async Task The_relay_reports_missing_credentials_as_status_770_and_not_as_769()
    {
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        using var server = new DemandingServer();
        using var relay = new ScreenRelay(NullLogger<ScreenRelay>.Instance);
        var up = new System.IO.Pipelines.Pipe();
        var down = new System.IO.Pipelines.Pipe();
        using var serverSocket = WebSocket.CreateFromStream(new PipeStream(up.Reader, down.Writer), new WebSocketCreationOptions { IsServer = true });
        using var browser = WebSocket.CreateFromStream(new PipeStream(down.Reader, up.Writer), new WebSocketCreationOptions { IsServer = false });

        var target = new ScreenTarget(new RdpOptions { Host = "127.0.0.1", Port = server.Port, UserName = "", Password = "", ConnectTimeout = TimeSpan.FromSeconds(5) }, "u:c:s1");
        var run = relay.RunAsync(serverSocket, target, _ => Task.FromResult(true), CancellationToken.None, owner: Guid.NewGuid());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var buffer = new byte[4096];
        while (true)
        {
            var result = await browser.ReceiveAsync(buffer, cts.Token);
            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
            if (buffer[0] == ScreenProtocol.ToBrowserError)
            {
                Assert.Equal(ScreenRelay.ErrorCredentialsMissing, (buffer[1] << 8) | buffer[2]);
                Assert.Equal(770, ScreenRelay.ErrorCredentialsMissing);
                Assert.Equal(769, ScreenRelay.ErrorLogonFailed);
                break;
            }
        }

        await run.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class PipeStream(System.IO.Pipelines.PipeReader read, System.IO.Pipelines.PipeWriter write) : Stream
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

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => await write.WriteAsync(buffer, ct);
    }
}
