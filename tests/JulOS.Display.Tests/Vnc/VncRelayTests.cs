using System.Buffers;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Integrations.Display.Tests.Vnc;

/// <summary>Der Weg bis zum Browser: ein VNC-Ziel, das die Anmeldung ablehnt oder ein Passwort verlangt, wird als 769 und 770 gemeldet (der Browser fragt dann neu).</summary>
public sealed class VncRelayTests
{
    private static async Task<(int Code, string Text)> FirstErrorAsync(FakeVncServer server, string password)
    {
        using var relay = new ScreenRelay(NullLogger<ScreenRelay>.Instance);
        var up = new System.IO.Pipelines.Pipe();
        var down = new System.IO.Pipelines.Pipe();
        using var serverSocket = WebSocket.CreateFromStream(new PipeStream(up.Reader, down.Writer), new WebSocketCreationOptions { IsServer = true });
        using var browser = WebSocket.CreateFromStream(new PipeStream(down.Reader, up.Writer), new WebSocketCreationOptions { IsServer = false });

        var vnc = new JulOS.Integrations.Display.Vnc.VncOptions { Host = "127.0.0.1", Port = server.Port, Password = password, ConnectTimeout = TimeSpan.FromSeconds(5) };
        var run = relay.RunAsync(serverSocket, ScreenTarget.ForVnc(vnc, "u:c:s1"), _ => Task.FromResult(true), CancellationToken.None, owner: Guid.NewGuid());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var buffer = new byte[4096];
        while (true)
        {
            var result = await browser.ReceiveAsync(buffer, cts.Token);
            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
            if (buffer[0] == ScreenProtocol.ToBrowserError)
            {
                var code = (buffer[1] << 8) | buffer[2];
                var text = System.Text.Encoding.UTF8.GetString(buffer, 3, result.Count - 3);
                // Wie ein Browser: den Abschluss-Rahmen des Servers entgegennehmen und mit dem eigenen antworten
                while ((await browser.ReceiveAsync(buffer, cts.Token)).MessageType != WebSocketMessageType.Close)
                {
                }

                await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                await run.WaitAsync(TimeSpan.FromSeconds(20));
                return (code, text);
            }
        }
    }

    [Fact]
    public async Task A_wrong_vnc_password_reaches_the_browser_as_769_every_time_and_a_missing_one_as_770()
    {
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        // Mehrmals: das Ende der Sitzung und das Ende ihrer Ereignisse liegen dicht beieinander, der Grund darf nie verloren gehen
        for (var i = 0; i < 4; i++)
        {
            using var wrong = new FakeVncServer { Types = [2], Password = "geheim" };
            var (code, text) = await FirstErrorAsync(wrong, "falsch");
            Assert.Equal((ScreenRelay.ErrorLogonFailed, true), (code, text.Contains("VNC", StringComparison.Ordinal)));

            using var none = new FakeVncServer { Types = [2], Password = "geheim" };
            Assert.Equal(ScreenRelay.ErrorCredentialsMissing, (await FirstErrorAsync(none, "")).Code);
        }
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
