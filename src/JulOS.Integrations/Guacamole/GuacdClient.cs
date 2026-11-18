using System.Net.Sockets;

namespace JulOS.Integrations.Guacamole;

/// <summary>guacd hat statt "ready" einen Fehler gemeldet (falscher Zugang, Ziel nicht erreichbar, ...).</summary>
public sealed class GuacdException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Eine fertig ausgehandelte Verbindung zu guacd. Ab jetzt laeuft das Protokoll unveraendert in beide Richtungen.</summary>
public sealed class GuacdConnection(TcpClient tcp, GuacReader reader, string id) : IDisposable
{
    public NetworkStream Stream { get; } = tcp.GetStream();
    public GuacReader Reader { get; } = reader;
    public string Id { get; } = id;
    public void Dispose() { Reader.Dispose(); tcp.Dispose(); }
}

/// <summary>Der Verbindungsaufbau mit guacd (Handshake): select, args, size/audio/video/image, connect, ready. Das macht der Server, nicht der Browser.</summary>
public static class GuacdClient
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<GuacdConnection> ConnectAsync(string host, int port, GuacSpec spec, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var tcp = new TcpClient { NoDelay = true };
        GuacReader? reader = null;
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            reader = new GuacReader(stream);

            await stream.WriteAsync(GuacProtocol.Encode("select", spec.Protocol), timeout.Token).ConfigureAwait(false);
            var args = await ReadAsync(reader, "args", timeout.Token).ConfigureAwait(false);

            // Alles, was guacd nach dem "args" erwartet, in einem Schreibvorgang (spart Umlaeufe)
            var values = new string[args.Length - 1];
            for (var i = 1; i < args.Length; i++)
                values[i - 1] = args[i].StartsWith("VERSION_", StringComparison.Ordinal) ? args[i] : spec.Args.GetValueOrDefault(args[i], "");
            using var hello = new MemoryStream();
            foreach (var part in new[]
            {
                GuacProtocol.Encode("size", spec.Width.ToString(), spec.Height.ToString(), spec.Dpi.ToString()),
                GuacProtocol.Encode("audio"),
                GuacProtocol.Encode("video"),
                GuacProtocol.Encode("image", "image/png", "image/jpeg", "image/webp"),
                GuacProtocol.Encode("connect", values),
            }) hello.Write(part);
            await stream.WriteAsync(hello.GetBuffer().AsMemory(0, (int)hello.Length), timeout.Token).ConfigureAwait(false);

            var ready = await ReadAsync(reader, "ready", timeout.Token).ConfigureAwait(false);
            return new GuacdConnection(tcp, reader, ready.Length > 1 ? ready[1] : "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            reader?.Dispose(); tcp.Dispose();
            throw new TimeoutException("guacd hat nicht rechtzeitig geantwortet.");
        }
        catch
        {
            reader?.Dispose(); tcp.Dispose();
            throw;
        }
    }

    private static async Task<string[]> ReadAsync(GuacReader reader, string expected, CancellationToken ct)
    {
        var ins = await reader.ReadInstructionAsync(ct).ConfigureAwait(false) ?? throw new IOException("guacd hat die Verbindung geschlossen.");
        if (ins[0] == "error") throw new GuacdException(ins.Length > 1 ? ins[1] : "Fehler", ins.Length > 2 && int.TryParse(ins[2], out var s) ? s : 512);
        if (ins[0] != expected) throw new InvalidDataException($"Unerwartete Anweisung von guacd: {ins[0]}");
        return ins;
    }
}
