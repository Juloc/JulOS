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
public sealed class ScreenRelay(ILogger<ScreenRelay> log, int maxSessions = 16, SessionRegistry? registry = null, EncoderSelector? encoders = null, TimeSpan? keep = null, TimeSpan? linkDeadAfter = null) : IDisposable
{
    public static readonly TimeSpan SessionCheck = TimeSpan.FromSeconds(15);

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
    private readonly Dictionary<string, Live> _byHandle = [];          // die oeffentliche Kennung (Liste, Nehmen); das Token der Engine kennt nur die Seite, die die Sitzung hat
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

    /// <summary>Eine erhaltene Sitzung, wie die Liste sie zeigt (nur Angaben, die der Besitzer sehen darf).</summary>
    public sealed record KeptSession(string Handle, Guid ConnectionId, string Name, DateTimeOffset Since, DateTimeOffset? UnwatchedSince, string Device, Guid AppSession, string? Layout = null, string? Drive = null);

    private static KeptSession Snapshot(Live l)
    {
        lock (l.Gate)
        {
            return new KeptSession(l.Handle, l.Info?.ConnectionId ?? Guid.Empty, l.Info?.ConnectionName ?? "", l.StartedAt, l.DetachedSince, l.Info?.Device ?? "", l.AppSession, l.Info?.Layout, l.Info?.Drive);
        }
    }

    /// <summary>Die erhaltenen Sitzungen <b>dieser Person</b> zu einer Verbindung (laufende mit Browser dran eingeschlossen, damit die Seite "wird gerade angesehen" sagen kann). Nie die einer anderen Person.</summary>
    public IReadOnlyList<KeptSession> ListKept(Guid owner, Guid connectionId)
    {
        lock (_keptGate)
        {
            return [.. _byHandle.Values.Where(l => !l.Ended && l.Owner == owner && l.Info?.ConnectionId == connectionId).Select(Snapshot).OrderBy(k => k.Since)];
        }
    }

    /// <summary>Die erhaltene Sitzung mit dieser Kennung, wenn sie dieser Person gehoert; unbekannt, beendet und fremd ergeben alle null (kein Hinweis, ob es die Kennung gibt).</summary>
    public KeptSession? FindKept(Guid owner, string handle)
    {
        lock (_keptGate)
        {
            return _byHandle.TryGetValue(handle, out var l) && !l.Ended && l.Owner == owner ? Snapshot(l) : null;
        }
    }

    /// <summary>Beendet eine erhaltene Sitzung dieser Person (die Seite zeigt "beendet", der Desktop am Ziel wird getrennt). Gibt sie zurueck, wenn es sie gab und sie der Person gehoerte.</summary>
    public async Task<KeptSession?> EndKeptAsync(Guid owner, string handle)
    {
        Live? live;
        lock (_keptGate)
        {
            _byHandle.TryGetValue(handle, out live);
        }

        if (live is null || live.Ended || live.Owner != owner)
        {
            return null;
        }

        var snapshot = Snapshot(live);
        log.LogInformation("Erhaltene Sitzung von der Person beendet.");
        await EndAsync(live).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>Beendet alle erhaltenen Sitzungen, die diese App-Sitzung zuletzt hatte (Abmelden an diesem Geraet).</summary>
    public async Task<IReadOnlyList<(Guid Owner, KeptSession Session)>> EndByAppSessionAsync(Guid appSession)
    {
        if (appSession == Guid.Empty)
        {
            return [];
        }

        Live[] mine;
        lock (_keptGate)
        {
            mine = [.. _byHandle.Values.Where(l => !l.Ended && l.AppSession == appSession)];
        }

        var ended = new List<(Guid, KeptSession)>();
        foreach (var live in mine)
        {
            ended.Add((live.Owner, Snapshot(live)));
            await EndAsync(live).ConfigureAwait(false);
        }

        return ended;
    }

    /// <summary>
    /// Nur fuer Tests: eine erhaltene Sitzung ohne echtes Ziel anlegen (die Engine wird nie gestartet), damit Liste, Nehmen und Beenden ohne Desktop pruefbar sind.
    /// Gibt die oeffentliche Kennung zurueck. watched: false = es wartet kein Browser mehr (wie nach einem Neuladen).
    /// </summary>
    internal string AddKeptForTests(Guid owner, KeptInfo info, Guid appSession, bool watched, Func<CancellationToken, Task<bool>>? alive = null)
    {
        _slots.Wait();
        var stop = new CancellationTokenSource();
        var lease = _sessions.TryOpen(owner, $"{owner}:{info.ConnectionId}:{Guid.NewGuid():N}", () => stop.Cancel())!;
        var target = new ScreenTarget(new Rdp.RdpOptions { Host = "test" }, Info: info);
        var live = new Live
        {
            Token = NewToken(), Owner = owner, Stream = new ScreenStream(target.Options, 16_000, log), Stop = stop, Lease = lease, Target = target, Started = Environment.TickCount64,
            Run = Task.Delay(Timeout.Infinite, stop.Token), Handle = NewHandle(), AppSession = appSession, Info = info, Alive = alive ?? (_ => Task.FromResult(true)),
        };
        lock (_keptGate)
        {
            _kept[live.Token] = live;
            _byHandle[live.Handle] = live;
        }

        if (!watched)
        {
            Detach(live, TimeSpan.FromMinutes(5), "test", 0);
        }

        return live.Handle;
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
    /// <param name="appSession">Die App-Sitzung (Cookie), die diesen WebSocket geoeffnet hat: die erhaltene Sitzung endet mit ihrer Abmeldung (siehe docs/SESSION-KEEP.md).</param>
    /// <param name="takeHandle">Die oeffentliche Kennung einer erhaltenen Sitzung, die der Browser nehmen will (das Ticket dazu hat der Endpunkt schon eingeloest).</param>
    /// <param name="resume">Das Sitzungs-Token einer wartenden Sitzung (aus der Adresse, <c>resume=</c>): statt eine neue zu bauen, haengt sich der WebSocket an diese.</param>
    public async Task RunAsync(WebSocket ws, ScreenTarget? target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan? sessionCheck = null, Guid? owner = null, VideoCodecs clientCodecs = VideoCodecs.H264, string? resume = null, Guid? appSession = null, string? takeHandle = null)
    {
        if (!string.IsNullOrEmpty(takeHandle))
        {
            // Kennung -> Token nur fuer den Besitzer; unbekannt, beendet und fremd ergeben dasselbe (leeres Token, dieselbe Antwort "abgelehnt")
            string? token = null;
            lock (_keptGate)
            {
                if (_byHandle.TryGetValue(takeHandle, out var l) && !l.Ended && l.Owner == (owner ?? Guid.Empty))
                {
                    token = l.Token;
                }
            }

            resume = token ?? "-";
        }

        if (!string.IsNullOrEmpty(resume))
        {
            await ResumeAsync(ws, resume, sessionAlive, ct, sessionCheck ?? SessionCheck, owner ?? Guid.Empty, clientCodecs, appSession ?? Guid.Empty).ConfigureAwait(false);
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
            live = await StartAsync(ws, target, sessionAlive, ct, owner ?? Guid.Empty, clientCodecs, appSession ?? Guid.Empty).ConfigureAwait(false);
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
        public required string Handle;                      // oeffentlich: steht in der Liste und im Ticket zum Nehmen, oeffnet allein nichts
        public Guid AppSession;                             // die App-Sitzung, die die Sitzung zuletzt hatte (Guid.Empty: unbekannt)
        public KeptInfo? Info;
        public DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset? DetachedSince;               // unter Gate; null solange ein Browser dran ist
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

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string NewHandle() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).Replace('+', '-').Replace('/', '_').TrimEnd('=');      // 128 Bit, nie das Token

    private async Task<Live?> StartAsync(WebSocket ws, ScreenTarget target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, Guid owner, VideoCodecs clientCodecs, Guid appSession)
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

        var stream = target.Vnc is { } vnc
            ? new ScreenStream(vnc, target.MaxKilobitsPerSecond, log, encoders, clientCodecs, target.Trust)
            : new ScreenStream(target.Options, target.MaxKilobitsPerSecond, log, encoders, clientCodecs, target.Trust, drive: target.Drive);
        log.LogInformation("Bildschirm-Verbindung offen ({Protocol}; der Browser kann {Codecs})", target.Vnc is null ? "RDP" : "VNC", clientCodecs);
        var live = new Live
        {
            Token = NewToken(), Owner = owner, Stream = stream, Stop = stop, Lease = lease, Target = target, Started = Environment.TickCount64,
            Run = stream.RunAsync(stop.Token), Alive = sessionAlive, Handle = NewHandle(), AppSession = appSession, Info = target.Info,
        };
        if (Keep is not null)
        {
            lock (_keptGate)
            {
                _kept[live.Token] = live;
                _byHandle[live.Handle] = live;
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

    private async Task ResumeAsync(WebSocket ws, string token, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan sessionCheck, Guid owner, VideoCodecs clientCodecs, Guid appSession)
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
        if (appSession != Guid.Empty)
        {
            live.AppSession = appSession;          // ab jetzt gilt die Abmeldung dieser App-Sitzung (dieses Geraets)
        }

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
            live.DetachedSince = null;
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
        var attachedAt = Environment.TickCount64;
        var gone = "unbekannt";       // nur fuers Log: warum die Leitung zum Browser endete (Abriss, Schliessen durch den Browser, Ablosung)
        try
        {
            try
            {
                if (Keep is { } keepFor)
                {
                    await SendOnceAsync(ws, ScreenProtocol.Session(live.Token, (int)keepFor.TotalSeconds, live.Handle), ct).ConfigureAwait(false);
                }

                var limits = live.Stream.ClipboardLimits;
                await SendOnceAsync(ws, live.Stream.SupportsFiles ? ScreenProtocol.Limits(limits.MaxFileBytes, ClipboardOptions.MaxFiles, ClipboardOptions.MaxEntries) : ScreenProtocol.Limits(0, 0, 0), ct).ConfigureAwait(false);

                // Lebenszeichen: ein neuer Browser pingt, wenn sonst nichts fliesst; bleibt alles still, ist die Leitung halbtot (Router, Mobilfunk) und wird geschlossen,
                // statt bis zum naechsten Schreiben stehenzubleiben. Ein alter Browser pingt nie und wird nie so beendet.
                await SendOnceAsync(ws, ScreenProtocol.LinkCapable(), ct).ConfigureAwait(false);
                var link = new LinkWatch(null, linkDeadAfter);
                var send = PumpToBrowserAsync(ws, live.Stream, live.Owner, Downloads, attach.Token);
                var receive = ReceiveFromBrowserAsync(ws, live.Stream, link, attach.Token);
                var watch = WatchAsync(sessionAlive, sessionCheck, attach.Token);
                var silent = link.WaitDeadAsync(attach.Token);
                var first = await Task.WhenAny(live.Run, send, receive, watch, silent).ConfigureAwait(false);

                // Die Ereignisse enden, wenn die Sitzung endet (RunAsync schliesst sie in seinem finally), also einen Atemzug bevor die Sitzung selbst
                // als beendet gilt: dann ist es das Ende der Sitzung (mit ihrem Grund, etwa einer abgelehnten Anmeldung), kein Abriss des Browsers.
                if (first == send && send.IsCompletedSuccessfully && await Task.WhenAny(live.Run, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false) == live.Run)
                {
                    first = live.Run;
                }

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
                else if (first == silent && silent.IsCompletedSuccessfully)
                {
                    gone = $"Leitung still: seit {link.SilentMs / 1000} s kein Lebenszeichen";
                    ws.Abort();                        // der Browser hoert nicht mehr: nicht noch auf einen Abschluss warten
                }
                else
                {
                    gone = WhyBrowserGone(first == send ? "Senden" : first == receive ? "Empfangen" : "Sitzung", first, ws);
                }

                // Zuerst sagen, warum es endet (ein abgebrochenes Senden oder Empfangen reisst den WebSocket ein), dann die Schleifen beenden
                if (last is not null)
                {
                    await FinishAsync(ws, last, ct, log).ConfigureAwait(false);

                    // Den Abschluss-Rahmen senden und dem Browser Zeit zum Antworten lassen (sein Rahmen beendet die Empfangsschleife), bevor das Abbrechen
                    // die Verbindung einreisst: sonst geht die Nachricht (der Grund, etwa "Passwort abgelehnt") oft auf dem Weg verloren, weil der Server
                    // die Verbindung schliesst, bevor seine Ausgabe auf dem Draht ist, und der Browser sieht nur einen Abriss
                    await CloseAsync(ws).ConfigureAwait(false);
                    await Task.WhenAny(receive, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
                }

                await attach.CancelAsync().ConfigureAwait(false);
                foreach (var task in new[] { send, receive, watch, silent })
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
                gone = $"Leitung abgerissen beim Start ({e.GetType().Name})";
                log.LogDebug("Bildschirm: {Type} {Message}", e.GetType().Name, e.Message);
                await attach.CancelAsync().ConfigureAwait(false);
            }

            // Die Leitung riss ab, und es gibt etwas zu erhalten: warten. Alles andere beendet die Sitzung wirklich.
            if (outcome == Outcome.BrowserGone && Keep is { } keepIt && !live.Run.IsCompleted && !live.Ended)
            {
                Detach(live, keepIt, gone, (Environment.TickCount64 - attachedAt) / 1000);
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
            log.LogWarning("Zertifikat des Ziels hat sich geändert: Verbindung nicht aufgebaut, nichts gesendet");
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
    private void Detach(Live live, TimeSpan keepFor, string reason, long connectedSeconds)
    {
        live.Stream.Park();
        var waiting = new CancellationTokenSource();
        lock (live.Gate)
        {
            live.Detached = true;
            live.DetachedSince = DateTimeOffset.UtcNow;
            live.Waiting = waiting;
            live.Drain = DrainAsync(live.Stream, waiting.Token);
        }

        // Der Grund ist das, was fehlte, um ein staendiges Neuverbinden zu deuten: "Leitung abgerissen" (ohne Close-Frame) kommt vom Netz zwischen Handy und
        // Server, "Browser schloss selbst" vom Browser (Seite war im Hintergrund, Neuladen, eigenes Neuverbinden), "abgebrochen" von einer neuen Verbindung derselben Sitzung.
        log.LogInformation("Browser weg nach {Connected} s ({Reason}): die Sitzung bleibt {Seconds} s erhalten.", connectedSeconds, reason, (int)keepFor.TotalSeconds);
        _ = ExpireAsync(live, keepFor, waiting.Token);
    }

    /// <summary>Der Grund, warum die erste der Aufgaben der Leitung zum Browser endete, als kurzer Text fuers Log (keine Inhalte).</summary>
    private static string WhyBrowserGone(string leg, Task first, WebSocket ws) =>
        first.IsFaulted ? $"Leitung abgerissen beim {leg} ({first.Exception?.GetBaseException().GetType().Name})"
        : first.IsCanceled ? "abgebrochen, eine neue Verbindung derselben Sitzung kam"
        : leg == "Senden" ? "Ereignisse der Sitzung endeten"
        : $"Browser schloss selbst ({ws.CloseStatus?.ToString() ?? "ohne Status"})";

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

            if (_byHandle.TryGetValue(live.Handle, out var byHandle) && ReferenceEquals(byHandle, live))
            {
                _byHandle.Remove(live.Handle);
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
                    case ScreenLinkPong pong:
                        await SendOnceAsync(ws, ScreenProtocol.Pong(pong.Id), ct).ConfigureAwait(false);
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
    internal static async Task<bool> ReceiveFromBrowserAsync(WebSocket ws, ScreenStream stream, LinkWatch link, CancellationToken ct)
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
                link.Heard();
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

            if (buffer[0] == ScreenProtocol.FromBrowserPing)
            {
                link.MarkCapable();
                if (used >= 5)
                {
                    stream.Echo(BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(1)));
                }

                continue;
            }

            if (buffer[0] == ScreenProtocol.FromBrowserPause && used >= 2)
            {
                link.SetRelaxed(buffer[1] != 0);
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
            case ScreenProtocol.FromBrowserKeysym when m.Length >= 6:
                stream.Keysym(BinaryPrimitives.ReadUInt32BigEndian(m[1..]), m[5] != 0);
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
    private static async Task FinishAsync(WebSocket ws, byte[] message, CancellationToken ct, ILogger? log = null)
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
            log?.LogDebug("Bildschirm: letzte Nachricht nicht gesendet: {Type} {Message}", e.GetType().Name, e.Message);
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
