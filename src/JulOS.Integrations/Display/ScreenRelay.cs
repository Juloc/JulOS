using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Video;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display;

/// <summary>
/// Verbindet einen Browser-WebSocket mit einer <see cref="ScreenStream"/>-Sitzung: Bilder, Mauszeiger und Groesse gehen binaer zum Browser,
/// seine Eingaben (siehe <see cref="ScreenProtocol"/>) an den fernen Rechner. Pro Slot (Benutzer, Verbindung, Sitzung des Browsers) ist nur ein
/// WebSocket offen: ein neuer loest den alten ab, der alten Seite wird das gesagt (sonst bliebe dort ein Standbild stehen). Andere Sitzungen
/// bleiben unberuehrt; die Obergrenze pro Benutzer steckt in der <see cref="SessionRegistry"/>.
/// </summary>
public sealed class ScreenRelay(ILogger<ScreenRelay> log, int maxSessions = 16, SessionRegistry? registry = null, EncoderSelector? encoders = null)
{
    public static readonly TimeSpan SessionCheck = TimeSpan.FromSeconds(60);

    // Fehlercodes wie bei Guacamole, damit die Seite dieselben Texte zeigt
    public const int ErrorBusy = 513, ErrorTimeout = 514, ErrorConnectFailed = 515, ErrorUnreachable = 520, ErrorLogonFailed = 769;

    private const int ReceiveBuffer = 256;
    private readonly SemaphoreSlim _slots = new(maxSessions);
    private readonly SessionRegistry _sessions = registry ?? new SessionRegistry(int.MaxValue);   // ohne Angabe (Tests): keine Obergrenze, aber Ablosen je Slot

    /// <param name="sessionAlive">Wird regelmaessig gefragt; bei false endet die Sitzung (Abmeldung, abgelaufene Sitzung der App).</param>
    /// <param name="owner">Der Benutzer (fuer die Obergrenze offener Sitzungen und das Ablosen im selben Slot).</param>
    /// <param name="clientCodecs">Was der Browser dekodieren kann (aus der Adresse, siehe <see cref="ScreenProtocol.ParseCodecs"/>); ohne Angabe nur H.264.</param>
    public async Task RunAsync(WebSocket ws, ScreenTarget target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan? sessionCheck = null, Guid? owner = null, VideoCodecs clientCodecs = VideoCodecs.H264)
    {
        if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await FinishAsync(ws, ScreenProtocol.Error(ErrorBusy, "Zu viele gleichzeitige Verbindungen."), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await RunCoreAsync(ws, target, sessionAlive, ct, sessionCheck ?? SessionCheck, owner, clientCodecs).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task RunCoreAsync(WebSocket ws, ScreenTarget target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan sessionCheck, Guid? owner, VideoCodecs clientCodecs)
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
        await using var stream = new ScreenStream(target.Options, target.MaxKilobitsPerSecond, log, encoders, clientCodecs);
        byte[]? last = null;
        try
        {
            log.LogInformation("Bildschirm-Verbindung offen (RDP; der Browser kann {Codecs})", clientCodecs);
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
                        length = ScreenProtocol.WritePicture(buffer, picture.Sequence, picture.IsKey, picture.Data, picture.Codec);
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
                    case ScreenAudioStatus audio:
                        buffer = pool.Rent(ScreenProtocol.AudioStatusLength);
                        length = ScreenProtocol.WriteAudioStatus(buffer, audio.Status);
                        break;
                    case ScreenAudioReady:
                        // Der Ton liegt in der begrenzten Warteschlange der Bruecke; hier wird sie ausgesendet
                        while (stream.Audio.TryTake(out var block))
                        {
                            var sound = pool.Rent(ScreenProtocol.AudioHeader + block.Pcm.Length);
                            try
                            {
                                var size = ScreenProtocol.WriteAudio(sound, block);
                                await ws.SendAsync(sound.AsMemory(0, size), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                pool.Return(sound);
                            }
                        }

                        continue;
                    case ScreenSize size:
                        buffer = pool.Rent(5);
                        length = ScreenProtocol.WriteSize(buffer, size.Width, size.Height);
                        break;
                    case ScreenEncoderInfo info:
                        await SendOnceAsync(ws, ScreenProtocol.Encoder(info.Codec, info.Hardware, info.Name), ct).ConfigureAwait(false);
                        continue;
                    case ScreenClipboardImage image:
                        await SendOnceAsync(ws, ScreenProtocol.Image(image.Format, image.Data), ct).ConfigureAwait(false);
                        continue;
                    case ScreenClipboardFiles files:
                        await SendOnceAsync(ws, ScreenProtocol.Files(files.Batch, files.Files), ct).ConfigureAwait(false);
                        continue;
                    case ScreenFileChunk chunk:
                        await SendOnceAsync(ws, ScreenProtocol.FileChunk(chunk.Batch, chunk.Index, chunk.Flags, chunk.Data), ct).ConfigureAwait(false);
                        continue;
                    case ScreenTransferResult result:
                        await SendOnceAsync(ws, ScreenProtocol.Result(result.Id, result.Code, result.Message), ct).ConfigureAwait(false);
                        continue;
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

    private static Task SendOnceAsync(WebSocket ws, byte[] message, CancellationToken ct) =>
        ws.SendAsync(message, WebSocketMessageType.Binary, true, ct);

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
            if (used > ReceiveBuffer && !ScreenProtocol.MayBeLong(buffer[0]))
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
                stream.Acknowledge(m.Length >= 5 ? BinaryPrimitives.ReadInt32BigEndian(m[1..]) : null);
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
            case ScreenProtocol.FromBrowserSendBegin when m.Length >= 16:
            {
                var size = BinaryPrimitives.ReadUInt64BigEndian(m[8..]);
                var name = m.Length > 16 ? System.Text.Encoding.UTF8.GetString(m[16..Math.Min(m.Length, 16 + 255)]) : "";
                stream.BeginTransfer(BinaryPrimitives.ReadUInt32BigEndian(m[1..]), m[5], m[6], m[7], size > long.MaxValue ? long.MaxValue : (long)size, name);
                break;
            }
            case ScreenProtocol.FromBrowserSendChunk when m.Length >= 5:
                stream.TransferChunk(BinaryPrimitives.ReadUInt32BigEndian(m[1..]), m[5..]);
                break;
            case ScreenProtocol.FromBrowserSendEnd when m.Length >= 5:
                stream.EndTransfer(BinaryPrimitives.ReadUInt32BigEndian(m[1..]));
                break;
            case ScreenProtocol.FromBrowserSendCancel when m.Length >= 5:
                stream.CancelTransfer(BinaryPrimitives.ReadUInt32BigEndian(m[1..]));
                break;
            case ScreenProtocol.FromBrowserFileRequest when m.Length >= 6:
                stream.RequestFile(BinaryPrimitives.ReadUInt32BigEndian(m[1..]), m[5]);
                break;
            case ScreenProtocol.FromBrowserFileCancel when m.Length >= 5:
                stream.CancelFile(BinaryPrimitives.ReadUInt32BigEndian(m[1..]));
                break;
            case ScreenProtocol.FromBrowserKeyFrame:
                stream.RequestKeyFrame();
                break;
            case ScreenProtocol.FromBrowserAudio when m.Length >= 2:
                stream.Audio.OutputOn = m[1] != 0;
                break;
            case ScreenProtocol.FromBrowserMicrophone when m.Length >= 7 && m.Length <= ScreenProtocol.MaxMicrophoneMessage:
                stream.Audio.Microphone(m[5..], (int)Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(m[1..]), 8000u, 96000u));
                break;
            case ScreenProtocol.FromBrowserPause when m.Length >= 2:
                stream.SetPaused(m[1] != 0);
                break;
            case ScreenProtocol.FromBrowserCodecs when m.Length >= 2:
                stream.SetClientCodecs((VideoCodecs)(m[1] & 3));
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
