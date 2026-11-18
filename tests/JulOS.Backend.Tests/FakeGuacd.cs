using System.Net;
using System.Net.Sockets;
using System.Text;
using JulOS.Integrations.Guacamole;

namespace JulOS.Backend.Tests;

/// <summary>Ein winziger guacd im Speicher: spricht den Handshake und notiert, was danach vom Relay ankommt.</summary>
internal sealed class FakeGuacd : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    /// <summary>Handshake-Anweisungen in der Reihenfolge, wie sie ankamen (select, size, audio, video, image, connect).</summary>
    public List<string[]> Handshake { get; } = [];
    public List<string[]> AfterReady { get; } = [];
    /// <summary>Statt "ready" einen Fehler melden.</summary>
    public (string Message, int Status)? Reject { get; init; }
    /// <summary>Wird gleich nach "ready" gesendet.</summary>
    public byte[] Greeting { get; init; } = [];
    /// <summary>Mehrere Verbindungen nacheinander oder gleichzeitig annehmen (Standard: genau eine).</summary>
    public bool AcceptMany { get; init; }
    /// <summary>Nach "ready" so viele Megabyte Bilddaten hinterherschicken (ein Browser, der nicht schnell genug liest, haelt das Senden des Relays auf).</summary>
    public int FloodMegabytes { get; init; }
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeGuacd()
    {
        _listener.Start();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            do
            {
                var accepted = await _listener.AcceptTcpClientAsync(ct);
                if (AcceptMany) _ = Task.Run(() => ServeAsync(accepted, ct), ct);
                else await ServeAsync(accepted, ct);
            }
            while (AcceptMany);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
    }

    private async Task ServeAsync(TcpClient accepted, CancellationToken ct)
    {
        try
        {
            using var tcp = accepted;
            var stream = tcp.GetStream();
            using var reader = new GuacReader(stream);

            Handshake.Add((await reader.ReadInstructionAsync(ct))!);                         // select
            await stream.WriteAsync(GuacProtocol.Encode("args", "VERSION_1_5_0", "hostname", "port", "username", "password", "unbekannt"), ct);
            for (var i = 0; i < 5; i++) Handshake.Add((await reader.ReadInstructionAsync(ct))!); // size audio video image connect

            if (Reject is { } r) { await stream.WriteAsync(GuacProtocol.Encode("error", r.Message, r.Status.ToString()), ct); return; }
            await stream.WriteAsync(GuacProtocol.Encode("ready", "fake-connection-id"), ct);
            if (Greeting.Length > 0) await stream.WriteAsync(Greeting, ct);
            if (FloodMegabytes > 0)
            {
                var chunk = GuacProtocol.Encode("blob", "0", new string('A', 60_000));
                for (var sent = 0L; sent < FloodMegabytes * 1_048_576L; sent += chunk.Length) await stream.WriteAsync(chunk, ct);
            }

            while (true)
            {
                var block = await reader.ReadBlockAsync(ct);
                if (block.IsEmpty) break;
                var s = new InstructionScanner();
                var span = block.Span;
                var start = 0;
                for (var i = 0; i < span.Length; i++)
                    if (s.Scan(span.Slice(i, 1)) >= 0) { lock (AfterReady) AfterReady.Add(GuacProtocol.Parse(span[start..(i + 1)])); start = i + 1; }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
        finally { Closed.TrySetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch { /* egal */ }
    }

    public static string Text(byte[] b) => Encoding.UTF8.GetString(b);
}
