using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Video;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display;

/// <summary>
/// Verbindet einen Browser-WebSocket mit einer <see cref="ScreenStream"/>-Sitzung: Bilder, Mauszeiger und Groesse gehen binaer zum Browser,
/// seine Eingaben (siehe <see cref="ScreenProtocol"/>) an den fernen Rechner. Pro Slot (Benutzer, Verbindung, Sitzung des Browsers) ist nur ein
/// WebSocket offen: ein neuer loest den alten ab, der alten Seite wird das gesagt (sonst bliebe dort ein Standbild stehen). Andere Sitzungen
/// bleiben unberuehrt; die Obergrenze pro Benutzer steckt in der <see cref="SessionRegistry"/>.
///
/// Sitzungserhalt: Die RDP-Sitzung gehoert nicht dem WebSocket. Reisst dieser ab (Handy wechselt das Netz, Tab im Hintergrund), lebt die Sitzung
/// <paramref name="keep"/> lang weiter (Bildkodierung pausiert, Ereignisse werden verworfen); der Browser haengt sich mit dem Sitzungs-Token
/// (Nachricht 11) wieder an: ohne neue Anmeldung am Ziel, mit sofortigem Schluesselbild. Ein ausdruecklich beendete Sitzung (Nachricht 17), ein
/// Ende des fernen Rechners, ein Ablosen im selben Slot, eine abgemeldete App-Sitzung und der Ablauf beenden sie wirklich. Ohne <paramref name="keep"/>
/// (null oder 0) endet sie mit dem WebSocket wie frueher.
/// </summary>
public sealed class ScreenRelay(ILogger<ScreenRelay> log, int maxSessions = 16, SessionRegistry? registry = null, EncoderSelector? encoders = null, TimeSpan? keep = null) : IDisposable
{
    public static readonly TimeSpan SessionCheck = TimeSpan.FromSeconds(60);

    /// <summary>So oft wird bei einer wartenden Sitzung nachgesehen, ob die App-Sitzung noch lebt (Abmeldung beendet sie).</summary>
    public static readonly TimeSpan WaitCheck = TimeSpan.FromSeconds(10);

    /// <summary>So lange ohne Browser lebt eine Sitzung, wenn nichts anderes eingestellt ist (JULOS_STREAM_KEEP_SECONDS).</summary>
    public const int DefaultKeepSeconds = 300;

    /// <summary>Hoechstens so lange darf der Erhalt eingestellt werden; ein Tippfehler soll keine Sitzung tagelang offen halten.</summary>
    public const int MaxKeepSeconds = 3600;

    // Statuscodes der Fehlermeldungen (die Seite uebersetzt sie in Texte, siehe statusText in app.js)
    public const int ErrorBusy = 513, ErrorTimeout = 514, ErrorConnectFailed = 515, ErrorUnreachable = 520, ErrorLogonFailed = 769, ErrorCredentialsMissing = 770;

    private const int ReceiveBuffer = 256;
    private readonly SemaphoreSlim _slots = new(maxSessions);
    private readonly SessionRegistry _sessions = registry ?? new SessionRegistry(int.MaxValue);   // ohne Angabe (Tests): keine Obergrenze, aber Ablosen je Slot
    private readonly Dictionary<string, Live> _kept = [];
    private readonly Lock _keptGate = new();

    /// <summary>Die vorbereiteten Downloads aus der Zwischenablage der fernen Rechner, die der Browser noch abholen kann (30 s, einmal, nur der Benutzer der Sitzung).</summary>
    public ClipDownloads Downloads { get; } = new(TimeProvider.System);

    /// <summary>Wie lange eine Sitzung ohne Browser weiterlebt; null: gar nicht.</summary>
    public TimeSpan? Keep { get; } = keep is { } k && k >= TimeSpan.FromSeconds(1) ? (k > TimeSpan.FromSeconds(MaxKeepSeconds) ? TimeSpan.FromSeconds(MaxKeepSeconds) : k) : null;

    /// <summary>Wie viele Sitzungen gerade ohne Browser warten (Tests, Anzeige).</summary>
    public int DetachedCount
    {
        get
        {
            lock (_keptGate)
            {
                return _kept.Values.Count(l => l.Detached);
            }
        }
    }

    /// <summary>Die Sekunden aus JULOS_STREAM_KEEP_SECONDS: leer = Standard, 0 oder Unsinn = aus.</summary>
    public static TimeSpan? ParseKeep(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromSeconds(DefaultKeepSeconds);
        }

        return int.TryParse(value.Trim(), out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, MaxKeepSeconds)) : null;
    }

    /// <param name="sessionAlive">Wird regelmaessig gefragt; bei false endet die Sitzung (Abmeldung, abgelaufene Sitzung der App).</param>
    /// <param name="owner">Der Benutzer (fuer die Obergrenze offener Sitzungen und das Ablosen im selben Slot).</param>
    /// <param name="clientCodecs">Was der Browser dekodieren kann (aus der Adresse, siehe <see cref="ScreenProtocol.ParseCodecs"/>); ohne Angabe nur H.264.</param>
    /// <param name="resume">Das Sitzungs-Token einer wartenden Sitzung (aus der Adresse, <c>resume=</c>): statt eine neue zu bauen, haengt sich der WebSocket an diese.</param>
    public async Task RunAsync(WebSocket ws, ScreenTarget? target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan? sessionCheck = null, Guid? owner = null, VideoCodecs clientCodecs = VideoCodecs.H264, string? resume = null)
    {
        if (!string.IsNullOrEmpty(resume))
        {
            await ResumeAsync(ws, resume, sessionAlive, ct, sessionCheck ?? SessionCheck, owner ?? Guid.Empty, clientCodecs).ConfigureAwait(false);
            return;
        }

        if (target is null)
        {
            await FinishAsync(ws, ScreenProtocol.Error(ErrorConnectFailed, "Kein Ziel."), ct).ConfigureAwait(false);
            return;
        }

        if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await FinishAsync(ws, ScreenProtocol.Error(ErrorBusy, "Zu viele gleichzeitige Verbindungen."), ct).ConfigureAwait(false);
            return;
        }

        Live? live = null;
        try
        {
            live = await StartAsync(ws, target, sessionAlive, ct, owner ?? Guid.Empty, clientCodecs).ConfigureAwait(false);
            if (live is not null)
            {
                await ServeAsync(ws, live, sessionAlive, ct, sessionCheck ?? SessionCheck).ConfigureAwait(false);
            }
        }
        finally
        {
            if (live is null)
            {
                _slots.Release();     // sonst gibt die Sitzung ihren Platz beim Ende selbst zurueck (EndAsync)
            }
        }
    }

    // ---- eine Sitzung, die vom WebSocket unabhaengig lebt ----

    private sealed class Live
    {
        public required string Token;
        public required Guid Owner;
        public required ScreenStream Stream;
        public required Task Run;
        public required CancellationTokenSource Stop;
        public required SessionLease Lease;
        public required ScreenTarget Target;
        public required long Started;
        public Func<CancellationToken, Task<bool>> Alive = _ => Task.FromResult(true);

        // Ein Browser zur Zeit: wer sich anhaengt, nimmt der Vorgaengerin den Platz (Cancel), wartet bis sie fertig ist und haelt dann die Sperre.
        public readonly SemaphoreSlim AttachLock = new(1, 1);
        public CancellationTokenSource? Attached;          // unter Gate: bricht den laufenden Anschluss ab
        public volatile bool Detached;                      // kein Browser dran (Ereignisse werden verworfen)
        public volatile bool Ended;
        public CancellationTokenSource? Waiting;            // Verwerfen und Ablaufzeit
        public Task? Drain;
        public readonly Lock Gate = new();
    }

    private string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private async Task<Live?> StartAsync(WebSocket ws, ScreenTarget target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, Guid owner, VideoCodecs clientCodecs)
    {
        if (!ScreenStream.IsAvailable)
        {
            log.LogWarning("Bildschirm-Engine nicht verfuegbar: {Reason}", ScreenStream.UnavailableReason);
            await FinishAsync(ws, ScreenProtocol.Error(ErrorUnreachable, "Der Bildkodierer ist auf diesem Server nicht verfügbar."), ct).ConfigureAwait(false);
            return null;
        }

        var stop = new CancellationTokenSource();
        var lease = _sessions.TryOpen(owner, target.Slot, () => stop.Cancel());
        if (lease is null)
        {
            stop.Dispose();
            await FinishAsync(ws, ScreenProtocol.Error(SessionRegistry.TooManyStatus, _sessions.LimitMessage), ct).ConfigureAwait(false);
            return null;
        }

        var stream = new ScreenStream(target.Options, target.MaxKilobitsPerSecond, log, encoders, clientCodecs, target.Trust, drive: target.Drive);
        log.LogInformation("Bildschirm-Verbindung offen (RDP; der Browser kann {Codecs})", clientCodecs);
        var live = new Live
        {
            Token = NewToken(), Owner = owner, Stream = stream, Stop = stop, Lease = lease, Target = target, Started = Environment.TickCount64,
            Run = stream.RunAsync(stop.Token), Alive = sessionAlive,
        };
        if (Keep is not null)
        {
            lock (_keptGate)
            {
                _kept[live.Token] = live;
            }
        }

        // Endet der ferne Rechner, waehrend kein Browser dran ist, raeumt das hier auf (sonst haette niemand mehr einen Grund dazu)
        _ = AfterRunAsync(live);
        return live;
    }

    private async Task AfterRunAsync(Live live)
    {
        try { await live.Run.ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* ServeAsync liest das Ergebnis */ }
        if (live.Detached)
        {
            log.LogInformation("Wartende Sitzung: der ferne Rechner hat sie beendet.");
            await EndAsync(live).ConfigureAwait(false);
        }
    }

    private async Task ResumeAsync(WebSocket ws, string token, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan sessionCheck, Guid owner, VideoCodecs clientCodecs)
    {
        Live? live;
        lock (_keptGate)
        {
            _kept.TryGetValue(token, out live);
        }

        // Fremde und unbekannte Token gleich: dieselbe Antwort, kein Hinweis darauf, ob es das Token gibt
        if (live is null || live.Ended || live.Owner != owner)
        {
            log.LogInformation("Wiederaufnahme abgelehnt (unbekannt, abgelaufen oder fremd).");
            await FinishAsync(ws, ScreenProtocol.ResumeFailed(), ct).ConfigureAwait(false);
            await CloseAsync(ws).ConfigureAwait(false);
            return;
        }

        live.Alive = sessionAlive;
        CancellationTokenSource? previous;
        lock (live.Gate)
        {
            previous = live.Attached;
        }

        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { /* war schon fertig */ }
        try
        {
            await live.AttachLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Ab hier gehoert die Sperre uns; ServeAsync gibt sie frei
        if (live.Ended)
        {
            live.AttachLock.Release();
            await FinishAsync(ws, ScreenProtocol.ResumeFailed(), ct).ConfigureAwait(false);
            await CloseAsync(ws).ConfigureAwait(false);
            return;
        }

        await StopWaitingAsync(live).ConfigureAwait(false);
        log.LogInformation("Sitzung wieder aufgenommen nach {Seconds} s", (Environment.TickCount64 - live.Started) / 1000);
        live.Stream.SetClientCodecs(clientCodecs);
        live.Stream.Resume();
        await ServeAsync(ws, live, sessionAlive, ct, sessionCheck, alreadyLocked: true).ConfigureAwait(false);
    }

    /// <summary>Haelt das Verwerfen der Ereignisse und die Ablaufzeit an (ein Browser kommt).</summary>
    private static async Task StopWaitingAsync(Live live)
    {
        CancellationTokenSource? waiting;
        Task? drain;
        lock (live.Gate)
        {
            waiting = live.Waiting;
            drain = live.Drain;
            live.Waiting = null;
            live.Drain = null;
            live.Detached = false;
        }

        if (waiting is not null)
        {
            try { await waiting.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* egal */ }
        }

        if (drain is not null)
        {
            try { await drain.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* gewollt */ }
        }

        waiting?.Dispose();
    }

    private enum Outcome { BrowserGone, UserEnded, SessionDead, RemoteEnded, Replaced }

    private async Task ServeAsync(WebSocket ws, Live live, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan sessionCheck, bool alreadyLocked = false)
    {
        if (!alreadyLocked)
        {
            await live.AttachLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        using var attach = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (live.Gate)
        {
            live.Attached = attach;
        }

        byte[]? last = null;
        var outcome = Outcome.BrowserGone;
        try
        {
            try
            {
                if (Keep is { } keepFor)
                {
                    await SendOnceAsync(ws, ScreenProtocol.Session(live.Token, (int)keepFor.TotalSeconds), ct).ConfigureAwait(false);
                }

                var limits = live.Stream.ClipboardLimits;
                await SendOnceAsync(ws, ScreenProtocol.Limits(limits.MaxFileBytes, ClipboardOptions.MaxFiles, ClipboardOptions.MaxEntries), ct).ConfigureAwait(false);

                var send = PumpToBrowserAsync(ws, live.Stream, live.Owner, Downloads, attach.Token);
                var receive = ReceiveFromBrowserAsync(ws, live.Stream, attach.Token);
                var watch = WatchAsync(sessionAlive, sessionCheck, attach.Token);
                var first = await Task.WhenAny(live.Run, send, receive, watch).ConfigureAwait(false);

                if (live.Lease.Replaced)
                {
                    outcome = Outcome.Replaced;
                    last = ScreenProtocol.Taken();
                }
                else if (first == live.Run)
                {
                    outcome = Outcome.RemoteEnded;
                    last = await RunResultAsync(live).ConfigureAwait(false);
                }
                else if (first == watch && watch.IsCompletedSuccessfully)
                {
                    outcome = Outcome.SessionDead;
                    log.LogInformation("Sitzung beendet, Bildschirm-Verbindung wird geschlossen.");
                }
                else if (first == receive && receive.IsCompletedSuccessfully && receive.Result)
                {
                    outcome = Outcome.UserEnded;
                }

                // Zuerst sagen, warum es endet (ein abgebrochenes Senden oder Empfangen reisst den WebSocket ein), dann die Schleifen beenden
                if (last is not null)
                {
                    await FinishAsync(ws, last, ct).ConfigureAwait(false);
                }

                await attach.CancelAsync().ConfigureAwait(false);
                foreach (var task in new[] { send, receive, watch })
                {
                    try { await task.ConfigureAwait(false); }
                    catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or RdpException)
                    {
                        log.LogDebug("Bildschirm-Ende: {Type} {Message}", e.GetType().Name, e.Message);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // der Browser ist weg, bevor oder waehrend wir ihm etwas gesendet haben: wie ein Abbruch der Leitung
                log.LogDebug("Bildschirm: {Type} {Message}", e.GetType().Name, e.Message);
                await attach.CancelAsync().ConfigureAwait(false);
            }

            // Die Leitung riss ab, und es gibt etwas zu erhalten: warten. Alles andere beendet die Sitzung wirklich.
            if (outcome == Outcome.BrowserGone && Keep is { } keepIt && !live.Run.IsCompleted && !live.Ended)
            {
                Detach(live, keepIt);
            }
            else
            {
                await EndAsync(live).ConfigureAwait(false);
                log.LogInformation("Bildschirm-Verbindung beendet nach {Seconds} s{Replaced}", (Environment.TickCount64 - live.Started) / 1000, live.Lease.Replaced ? " (von einer neuen Verbindung abgelöst)" : "");
            }
        }
        finally
        {
            lock (live.Gate)
            {
                if (ReferenceEquals(live.Attached, attach))
                {
                    live.Attached = null;
                }
            }

            live.AttachLock.Release();
            await CloseAsync(ws).ConfigureAwait(false);
        }
    }

    // Der Grund, warum der ferne Rechner (oder der Start) endete, als letzte Nachricht an den Browser; null bei Abbruch durch uns.
    private async Task<byte[]?> RunResultAsync(Live live)
    {
        byte[]? last = null;
        try { await live.Run.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* abgebrochen */ }
        catch (RdpException e) when (e.CertificateFingerprint is not null)
        {
            log.LogWarning("RDP-Zertifikat des Ziels hat sich geändert: Verbindung nicht aufgebaut, nichts gesendet");
            last = ScreenProtocol.CertificateChanged(e.CertificateFingerprint, live.Target.Trust?.Stored);
        }
        catch (RdpException e) { last = ScreenProtocol.Error(e.IsCredentialsMissing ? ErrorCredentialsMissing : e.IsLogonFailure ? ErrorLogonFailed : ErrorConnectFailed, e.Message); }
        catch (Exception e) when (e is SocketException or IOException or TimeoutException)
        {
            last = ScreenProtocol.Error(e is TimeoutException ? ErrorTimeout : ErrorUnreachable,
                e is TimeoutException ? "Zeitüberschreitung beim Verbinden." : "Das Ziel ist nicht erreichbar.");
        }

        return last ?? (live.Stream.EndReason is { } reason ? ScreenProtocol.Error(ErrorConnectFailed, reason) : null);
    }

    // Kein Browser mehr: die Sitzung pausiert, Ereignisse werden verworfen, nach "keepFor" (oder wenn die App-Sitzung weg ist) endet sie.
    private void Detach(Live live, TimeSpan keepFor)
    {
        live.Stream.Park();
        var waiting = new CancellationTokenSource();
        lock (live.Gate)
        {
            live.Detached = true;
            live.Waiting = waiting;
            live.Drain = DrainAsync(live.Stream, waiting.Token);
        }

        log.LogInformation("Browser weg: die Sitzung bleibt {Seconds} s erhalten.", (int)keepFor.TotalSeconds);
        _ = ExpireAsync(live, keepFor, waiting.Token);
    }

    private static async Task DrainAsync(ScreenStream stream, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in stream.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // niemand hoert zu
            }
        }
        catch (OperationCanceledException) { /* ein Browser kommt zurueck */ }
    }

    private async Task ExpireAsync(Live live, TimeSpan keepFor, CancellationToken ct)
    {
        try
        {
            var until = Environment.TickCount64 + (long)keepFor.TotalMilliseconds;
            while (true)
            {
                var left = until - Environment.TickCount64;
                if (left <= 0)
                {
                    log.LogInformation("Wartende Sitzung abgelaufen, wird beendet.");
                    break;
                }

                await Task.Delay((int)Math.Min(left, WaitCheck.TotalMilliseconds), ct).ConfigureAwait(false);
                if (Environment.TickCount64 < until && !await live.Alive(ct).ConfigureAwait(false))
                {
                    log.LogInformation("Wartende Sitzung: die App-Sitzung ist beendet, die Sitzung wird beendet.");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;                // ein Browser ist zurueck oder die Sitzung endet anderswo
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.LogDebug("Wartende Sitzung: {Type} {Message}", e.GetType().Name, e.Message);
        }

        await EndAsync(live).ConfigureAwait(false);
    }

    private async Task EndAsync(Live live)
    {
        lock (live.Gate)
        {
            if (live.Ended)
            {
                return;
            }

            live.Ended = true;
        }

        lock (_keptGate)
        {
            if (_kept.TryGetValue(live.Token, out var current) && ReferenceEquals(current, live))
            {
                _kept.Remove(live.Token);
            }
        }

        try { await live.Stop.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* egal */ }
        CancellationTokenSource? waiting;
        lock (live.Gate)
        {
            waiting = live.Waiting;
            live.Waiting = null;
        }

        try { waiting?.Cancel(); }
        catch (ObjectDisposedException) { /* egal */ }
        try { await live.Run.ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or RdpException or SocketException or TimeoutException)
        {
            log.LogDebug("Bildschirm-Ende: {Type} {Message}", e.GetType().Name, e.Message);
        }

        if (live.Drain is { } drain)
        {
            try { await drain.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* gewollt */ }
        }

        await live.Stream.DisposeAsync().ConfigureAwait(false);
        live.Lease.Dispose();
        live.Stop.Dispose();
        _slots.Release();
    }

    /// <summary>Beendet alle Sitzungen (Server faehrt herunter): die RDP-Verbindungen werden getrennt.</summary>
    public void Dispose()
    {
        Live[] all;
        lock (_keptGate)
        {
            all = [.. _kept.Values];
        }

        foreach (var live in all)
        {
            try { live.Stop.Cancel(); }
            catch (ObjectDisposedException) { /* egal */ }
        }
    }

    /// <summary>Stream -> Browser. Der einzige, der sendet; Puffer kommen aus dem Pool.</summary>
    private static async Task PumpToBrowserAsync(WebSocket ws, ScreenStream stream, Guid owner, ClipDownloads downloads, CancellationToken ct)
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
                    case ScreenRefine refine:
                        buffer = pool.Rent(ScreenProtocol.RefineHeader + refine.Png.Length);
                        length = ScreenProtocol.WriteRefine(buffer, refine);
                        break;
                    case ScreenRefineClear clear:
                        buffer = pool.Rent(3 + Math.Min(clear.Rects.Length / 4, ScreenProtocol.MaxClearRects) * 8);
                        length = ScreenProtocol.WriteRefineClear(buffer, clear.Rects);
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
                    case ScreenClipboardNote note:
                        await SendOnceAsync(ws, ScreenProtocol.ClipboardNote(note.Flags, note.Note, note.Count), ct).ConfigureAwait(false);
                        continue;
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
                    case ScreenDownloadReady ready:
                    {
                        // Der Browser holt den Download mit diesem Ticket als eigene HTTP-Anfrage ab
                        var ticket = downloads.Create(owner, ready.Download);
                        if (ticket is null)
                        {
                            stream.CancelFile(ready.Batch);
                            await SendOnceAsync(ws, ScreenProtocol.FileState(ready.Batch, ClipboardBridge.StateFailed, 0, 0, "Zu viele offene Downloads."), ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await SendOnceAsync(ws, ScreenProtocol.FileState(ready.Batch, ClipboardBridge.StateReady, 0, ready.Download.Total, ticket), ct).ConfigureAwait(false);
                        }

                        continue;
                    }
                    case ScreenFileState state:
                        await SendOnceAsync(ws, ScreenProtocol.FileState(state.Batch, state.State, state.Done, state.Total, state.Text), ct).ConfigureAwait(false);
                        continue;
                    case ScreenTransferResult result:
                        await SendOnceAsync(ws, ScreenProtocol.Result(result.Id, result.Code, result.Message), ct).ConfigureAwait(false);
                        continue;
                    default:
                        continue;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await ws.SendAsync(buffer.AsMemory(0, length), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                if (evt is ScreenPicture)
                {
                    stream.SocketSent((long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
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
    private static async Task<bool> ReceiveFromBrowserAsync(WebSocket ws, ScreenStream stream, CancellationToken ct)
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
                        return true;
                    }

                    Array.Resize(ref buffer, Math.Min(maxMessage, buffer.Length * 4));
                }

                result = await ws.ReceiveAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return false;
                }

                used += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary || used == 0)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "ungültig", CancellationToken.None).ConfigureAwait(false);
                return true;
            }

            // Zu lange Nachrichten, die nicht zur Zwischenablage gehoeren, sind nicht von uns
            if (used > ReceiveBuffer && !ScreenProtocol.MayBeLong(buffer[0]))
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "ungültig", CancellationToken.None).ConfigureAwait(false);
                return true;
            }

            // Der Browser beendet die Sitzung ausdruecklich (Schliessen): nicht erhalten
            if (buffer[0] == ScreenProtocol.FromBrowserEnd)
            {
                return true;
            }

            Handle(stream, buffer.AsSpan(0, used));
            if (buffer.Length > ReceiveBuffer * 16)
            {
                buffer = new byte[ReceiveBuffer];                 // der grosse Puffer war nur fuer einen Text
            }
        }

        return false;
    }

    /// <summary>Maustasten wie im Browser: 0 links, 1 Mitte, 2 rechts, 3 zurueck, 4 vor.</summary>
    internal static bool IsMouseButton(byte button) => button <= 4;

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
            case ScreenProtocol.FromBrowserButton when m.Length >= 3 && IsMouseButton(m[1]):
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
                // Ein Pfad ist hoechstens 259 Zeichen lang (ClipboardNames.MaxPathLength); was darueber hinausgeht, wird abgelehnt, nicht abgeschnitten
                var name = m.Length > 16 ? System.Text.Encoding.UTF8.GetString(m[16..Math.Min(m.Length, 16 + 1200)]) : "";
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
            case ScreenProtocol.FromBrowserFileRequest when m.Length >= 5:
                stream.RequestFile(BinaryPrimitives.ReadUInt32BigEndian(m[1..]), System.Text.Encoding.UTF8.GetString(m[5..Math.Min(m.Length, 5 + 1200)]));
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
            case ScreenProtocol.FromBrowserQuality when m.Length >= 2 && StreamQualityPolicy.Parse(m[1]) is { } quality:
                stream.SetQuality(quality);
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
