using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Guacamole;

public sealed record GuacdOptions(string Host, int Port, int MaxConnections = 16);

/// <summary>
/// Verbindet einen Browser-WebSocket mit guacd. Der Server macht den Handshake selbst, schickt dem Browser-Tunnel seine interne Kennung
/// und reicht danach nur noch ganze Anweisungen durch. Die interne Ping-Anweisung des Tunnels bleibt beim Server.
/// </summary>
public sealed class GuacRelay(GuacdOptions options, ILogger<GuacRelay> log)
{
    public const int MaxMessage = 64 * 1024;
    public static readonly TimeSpan SessionCheck = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _slots = new(options.MaxConnections);

    /// <param name="sessionAlive">Wird regelmaessig gefragt; bei false wird die Verbindung beendet (Abmeldung, abgelaufene Sitzung).</param>
    public async Task RunAsync(WebSocket ws, GuacSpec spec, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan? sessionCheck = null)
    {
        if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false)) { await FailAsync(ws, 513, "Zu viele gleichzeitige Verbindungen."); return; }
        try { await RunCoreAsync(ws, spec, sessionAlive, ct, sessionCheck ?? SessionCheck).ConfigureAwait(false); }
        finally { _slots.Release(); }
    }

    private async Task RunCoreAsync(WebSocket ws, GuacSpec spec, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan sessionCheck)
    {
        var clock = Stopwatch.StartNew();
        GuacdConnection conn;
        try { conn = await GuacdClient.ConnectAsync(options.Host, options.Port, spec, ct).ConfigureAwait(false); }
        catch (GuacdException e) { log.LogInformation("guacd lehnt ab ({Protocol}): {Message}", spec.Protocol, e.Message); await FailAsync(ws, e.Status, e.Message); return; }
        catch (TimeoutException) { await FailAsync(ws, 514, "Zeitüberschreitung beim Verbinden."); return; }
        catch (Exception e) when (e is SocketException or IOException or InvalidDataException)
        {
            log.LogWarning("guacd nicht erreichbar oder ungueltige Antwort: {Message}", e.Message);
            await FailAsync(ws, 520, "Der Verbindungsdienst ist nicht erreichbar.");
            return;
        }

        using (conn)
        {
            log.LogInformation("Remote-Verbindung offen ({Protocol}, Handshake {Ms} ms)", spec.Protocol, clock.ElapsedMilliseconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                // interne Anweisung: der Browser-Tunnel erwartet als Erstes seine Kennung
                await ws.SendAsync(GuacProtocol.Encode("", conn.Id), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                var down = DownAsync(ws, conn, cts.Token);
                var up = UpAsync(ws, conn, cts.Token);
                var watch = WatchAsync(sessionAlive, sessionCheck, cts.Token);
                var first = await Task.WhenAny(down, up, watch).ConfigureAwait(false);
                if (first == watch && !cts.IsCancellationRequested) log.LogInformation("Sitzung beendet, Remote-Verbindung wird geschlossen.");
                await cts.CancelAsync().ConfigureAwait(false);
                foreach (var t in new[] { down, up, watch })
                    try { await t.ConfigureAwait(false); }
                    catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or InvalidDataException or ObjectDisposedException)
                    { log.LogDebug("Relay-Ende: {Type} {Message}", e.GetType().Name, e.Message); }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException) { /* Browser weg */ }
            finally
            {
                log.LogInformation("Remote-Verbindung beendet nach {Seconds} s", (int)clock.Elapsed.TotalSeconds);
                await CloseAsync(ws).ConfigureAwait(false);
            }
        }
    }

    /// <summary>guacd -> Browser: Bloecke mit ganzen Anweisungen.</summary>
    private static async Task DownAsync(WebSocket ws, GuacdConnection conn, CancellationToken ct)
    {
        while (true)
        {
            var block = await conn.Reader.ReadBlockAsync(ct).ConfigureAwait(false);
            if (block.IsEmpty) return;
            await ws.SendAsync(block, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Browser -> guacd: jede Nachricht muss aus ganzen Anweisungen bestehen.</summary>
    private static async Task UpAsync(WebSocket ws, GuacdConnection conn, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(MaxMessage);
        try
        {
            while (true)
            {
                var len = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (len == MaxMessage) { await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "zu groß", CancellationToken.None).ConfigureAwait(false); return; }
                    r = await ws.ReceiveAsync(buf.AsMemory(len, MaxMessage - len), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    len += r.Count;
                }
                while (!r.EndOfMessage);

                if (len == 0 || GuacProtocol.IsInternal(buf.AsSpan(0, len))) continue;
                var scanner = new InstructionScanner();
                if (r.MessageType != WebSocketMessageType.Text || scanner.Scan(buf.AsSpan(0, len)) != len - 1)
                { await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "ungültig", CancellationToken.None).ConfigureAwait(false); return; }
                await conn.Stream.WriteAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static async Task WatchAsync(Func<CancellationToken, Task<bool>> alive, TimeSpan every, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            if (!await alive(ct).ConfigureAwait(false)) return;
    }

    /// <summary>Meldet einen Fehler im Guacamole-Format (der Browser-Client zeigt ihn an) und schliesst.</summary>
    private static async Task FailAsync(WebSocket ws, int status, string message)
    {
        try
        {
            await ws.SendAsync(GuacProtocol.Encode("", Guid.NewGuid().ToString()), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            await ws.SendAsync(GuacProtocol.Encode("error", message, status.ToString()), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { return; }
        await CloseAsync(ws).ConfigureAwait(false);
    }

    private static async Task CloseAsync(WebSocket ws)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            // nur das Schlusssignal senden und nicht auf die Antwort warten: der Platz wird sofort wieder frei
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException) { /* egal */ }
    }
}
