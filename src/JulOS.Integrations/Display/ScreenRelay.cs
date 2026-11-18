using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using JulOS.Integrations.Display.Rdp;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display;

/// <summary>
/// Verbindet einen Browser-WebSocket mit einer <see cref="ScreenStream"/>-Sitzung: Bilder, Mauszeiger und Groesse gehen binaer zum Browser,
/// seine Eingaben (siehe <see cref="ScreenProtocol"/>) an den fernen Rechner. Pro Slot (Benutzer, Verbindung, Sitzung des Browsers) ist nur ein
/// WebSocket offen: ein neuer loest den alten ab, der alten Seite wird das gesagt (sonst bliebe dort ein Standbild stehen). Andere Sitzungen
/// bleiben unberuehrt; die Obergrenze pro Benutzer steckt in der <see cref="SessionRegistry"/>.
/// </summary>
public sealed class ScreenRelay(ILogger<ScreenRelay> log, int maxSessions = 16, SessionRegistry? registry = null)
{
    public static readonly TimeSpan SessionCheck = TimeSpan.FromSeconds(60);

    // Fehlercodes wie bei Guacamole, damit die Seite dieselben Texte zeigt
    public const int ErrorBusy = 513, ErrorTimeout = 514, ErrorConnectFailed = 515, ErrorUnreachable = 520, ErrorLogonFailed = 769;

    private const int ReceiveBuffer = 256;
    private readonly SemaphoreSlim _slots = new(maxSessions);
    private readonly SessionRegistry _sessions = registry ?? new SessionRegistry(int.MaxValue);   // ohne Angabe (Tests): keine Obergrenze, aber Ablosen je Slot

    /// <param name="sessionAlive">Wird regelmaessig gefragt; bei false endet die Sitzung (Abmeldung, abgelaufene Sitzung der App).</param>
    /// <param name="owner">Der Benutzer (fuer die Obergrenze offener Sitzungen und das Ablosen im selben Slot).</param>
    public async Task RunAsync(WebSocket ws, ScreenTarget target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan? sessionCheck = null, Guid? owner = null)
    {
        if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await FinishAsync(ws, ScreenProtocol.Error(ErrorBusy, "Zu viele gleichzeitige Verbindungen."), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await RunCoreAsync(ws, target, sessionAlive, ct, sessionCheck ?? SessionCheck, owner).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task RunCoreAsync(WebSocket ws, ScreenTarget target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan sessionCheck, Guid? owner)
    {
        if (!ScreenStream.IsAvailable)
        {
            log.LogWarning("Bildschirm-Engine nicht verfuegbar: {Reason}", ScreenStream.UnavailableReason);
            await FinishAsync(ws, ScreenProtocol.Error(ErrorUnreachable, "Der Bildkodierer ist auf diesem Server nicht verfügbar."), ct).ConfigureAwait(false);
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var mine = _sessions.TryOpen(owner ?? Guid.Empty, target.Slot, () => stop.Cancel());
        if (mine is null)
        {
            await FinishAsync(ws, ScreenProtocol.Error(SessionRegistry.TooManyStatus, _sessions.LimitMessage), ct).ConfigureAwait(false);
            return;
        }

        var started = Environment.TickCount64;
        await using var stream = new ScreenStream(target.Options, target.MaxKilobitsPerSecond, log);
        byte[]? last = null;
        try
        {
            log.LogInformation("Bildschirm-Verbindung offen (RDP, H.264)");
            var run = stream.RunAsync(stop.Token);
            var send = PumpToBrowserAsync(ws, stream, ct);
            var receive = ReceiveFromBrowserAsync(ws, stream, ct);
            var watch = WatchAsync(sessionAlive, sessionCheck, stop.Token);
            var first = await Task.WhenAny(run, send, receive, watch).ConfigureAwait(false);

            if (first == run)
            {
                try { await run.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* abgebrochen */ }
                catch (RdpException e) { last = ScreenProtocol.Error(e.IsLogonFailure ? ErrorLogonFailed : ErrorConnectFailed, e.Message); }
                catch (Exception e) when (e is SocketException or IOException or TimeoutException)
                {
                    last = ScreenProtocol.Error(e is TimeoutException ? ErrorTimeout : ErrorUnreachable,
                        e is TimeoutException ? "Zeitüberschreitung beim Verbinden." : "Das Ziel ist nicht erreichbar.");
                }

                last ??= stream.EndReason is { } reason ? ScreenProtocol.Error(ErrorConnectFailed, reason) : null;
            }
            else if (mine.Replaced)
            {
                last = ScreenProtocol.Taken();
            }
            else if (first == watch)
            {
                log.LogInformation("Sitzung beendet, Bildschirm-Verbindung wird geschlossen.");
            }

            // Zuerst sagen, warum es endet (ein abgebrochenes Senden oder Empfangen reisst den WebSocket ein), dann die Schleifen beenden
            if (last is not null)
            {
                await FinishAsync(ws, last, ct).ConfigureAwait(false);
            }

            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { run, send, receive, watch })
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or RdpException)
                {
                    log.LogDebug("Bildschirm-Ende: {Type} {Message}", e.GetType().Name, e.Message);
                }
            }
        }
        finally
        {
            mine.Dispose();
            log.LogInformation("Bildschirm-Verbindung beendet nach {Seconds} s{Replaced}", (Environment.TickCount64 - started) / 1000, mine.Replaced ? " (von einer neuen Verbindung abgelöst)" : "");
            await CloseAsync(ws).ConfigureAwait(false);
        }
    }

    /// <summary>Stream -> Browser. Der einzige, der sendet; Puffer kommen aus dem Pool.</summary>
    private static async Task PumpToBrowserAsync(WebSocket ws, ScreenStream stream, CancellationToken ct)
    {
        var pool = ArrayPool<byte>.Shared;
        await foreach (var evt in stream.Events.ReadAllAsync(ct).ConfigureAwait(false))
        {
            byte[]? buffer = null;
            try
            {
                int length;
                switch (evt)
                {
                    case ScreenPicture picture:
                        buffer = pool.Rent(ScreenProtocol.PictureHeader + picture.Data.Length);
                        length = ScreenProtocol.WritePicture(buffer, picture.Sequence, picture.IsKey, picture.Data);
                        break;
                    case ScreenCursor cursor:
                        buffer = pool.Rent(ScreenProtocol.CursorHeader + (cursor.Rgba?.Length ?? 0));
                        length = ScreenProtocol.WriteCursor(buffer, cursor);
                        break;
                    case ScreenClipboardText clip:
                        buffer = pool.Rent(1 + System.Text.Encoding.UTF8.GetMaxByteCount(clip.Text.Length));
                        buffer[0] = ScreenProtocol.ToBrowserClipboard;
                        length = 1 + System.Text.Encoding.UTF8.GetBytes(clip.Text, buffer.AsSpan(1));
                        break;
                    case ScreenSize size:
                        buffer = pool.Rent(5);
                        length = ScreenProtocol.WriteSize(buffer, size.Width, size.Height);
                        break;
                    default:
                        continue;
                }

                await ws.SendAsync(buffer.AsMemory(0, length), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
            finally
            {
                if (buffer is not null)
                {
                    pool.Return(buffer);
                }
            }
        }
    }

    /// <summary>Browser -> Stream: kleine binaere Nachrichten (siehe ScreenProtocol); nur die Zwischenablage darf laenger sein und kommt dann in Teilen.</summary>
    private static async Task ReceiveFromBrowserAsync(WebSocket ws, ScreenStream stream, CancellationToken ct)
    {
        const int maxMessage = 1 + ClipboardBridge.MaxTextBytes;
        var buffer = new byte[ReceiveBuffer];
        while (!ct.IsCancellationRequested)
        {
            var used = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (used == buffer.Length)
                {
                    if (used >= maxMessage)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "zu lang", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    Array.Resize(ref buffer, Math.Min(maxMessage, buffer.Length * 4));
                }

                result = await ws.ReceiveAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                used += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary || used == 0)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "ungültig", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // Zu lange Nachrichten, die nicht zur Zwischenablage gehoeren, sind nicht von uns
            if (used > ReceiveBuffer && buffer[0] != ScreenProtocol.FromBrowserClipboard)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "ungültig", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            Handle(stream, buffer.AsSpan(0, used));
            if (buffer.Length > ReceiveBuffer * 16)
            {
                buffer = new byte[ReceiveBuffer];                 // der grosse Puffer war nur fuer einen Text
            }
        }
    }

    internal static void Handle(ScreenStream stream, ReadOnlySpan<byte> m)
    {
        switch (m[0])
        {
            case ScreenProtocol.FromBrowserAck:
                stream.Acknowledge();
                break;
            case ScreenProtocol.FromBrowserMove when m.Length >= 5:
                stream.MouseMove(BinaryPrimitives.ReadUInt16BigEndian(m[1..]), BinaryPrimitives.ReadUInt16BigEndian(m[3..]));
                break;
            case ScreenProtocol.FromBrowserButton when m.Length >= 3 && m[1] <= 2:
                stream.MouseButton(m[1], m[2] != 0);
                break;
            case ScreenProtocol.FromBrowserWheel when m.Length >= 4:
                stream.MouseWheel(BinaryPrimitives.ReadInt16BigEndian(m[1..]), m[3] != 0);
                break;
            case ScreenProtocol.FromBrowserKey when m.Length >= 3:
                stream.Key(m[1], (m[2] & 1) != 0, (m[2] & 2) != 0);
                break;
            case ScreenProtocol.FromBrowserUnicode when m.Length >= 3:
                stream.Unicode((char)BinaryPrimitives.ReadUInt16BigEndian(m[1..]));
                break;
            case ScreenProtocol.FromBrowserResize when m.Length >= 5:
                stream.Resize(Math.Clamp((int)BinaryPrimitives.ReadUInt16BigEndian(m[1..]), 200, 4096), Math.Clamp((int)BinaryPrimitives.ReadUInt16BigEndian(m[3..]), 200, 4096));
                break;
            case ScreenProtocol.FromBrowserClipboard when m.Length >= 2:
                // Ungueltiges UTF-8 wird zu U+FFFD, nie zu einer Ausnahme
                stream.SetClipboard(System.Text.Encoding.UTF8.GetString(m[1..]));
                break;
            case ScreenProtocol.FromBrowserKeyFrame:
                stream.RequestKeyFrame();
                break;
        }
    }

    private static async Task WatchAsync(Func<CancellationToken, Task<bool>> alive, TimeSpan every, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (!await alive(ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Eine letzte Nachricht senden und den WebSocket schliessen (Fehler, Ablosung, Abmeldung).</summary>
    private static async Task FinishAsync(WebSocket ws, byte[] message, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            await ws.SendAsync(message, WebSocketMessageType.Binary, true, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // der Browser ist schon weg
        }
    }

    private static async Task CloseAsync(WebSocket ws)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // egal
        }
    }
}
