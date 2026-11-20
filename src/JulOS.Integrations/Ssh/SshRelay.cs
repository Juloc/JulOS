using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Ssh;

/// <summary>
/// Verbindet einen Browser-WebSocket mit einer <see cref="ISshSession"/> (Protokoll: <see cref="SshProtocol"/>). Pro Slot (Benutzer, Verbindung,
/// Sitzung des Browsers) ist nur ein WebSocket offen: ein neuer loest den alten ab (zum Beispiel nach einem Verbindungsabbruch auf dem Handy,
/// dann endet die alte Shell auf dem Ziel gleich mit). Mehrere Sitzungen zum selben Ziel haben verschiedene Slots. Die Obergrenze pro Benutzer
/// steckt in der <see cref="SessionRegistry"/>, dazu eine globale. Wie bei den anderen Verbindungen endet die Sitzung mit der Anmeldung der App
/// (Pruefung alle 60 s).
/// </summary>
public sealed class SshRelay(ILogger<SshRelay> log, Func<ISshSession>? factory = null, int maxSessions = 32, SessionRegistry? registry = null)
{
    public static readonly TimeSpan SessionCheck = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ISshSession> _factory = factory ?? (() => new SshNetSession());
    private readonly SemaphoreSlim _slots = new(maxSessions);
    private readonly SessionRegistry _sessions = registry ?? new SessionRegistry();

    public async Task RunAsync(WebSocket ws, SshTarget target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan? sessionCheck = null)
    {
        if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await FinishAsync(ws, SshProtocol.End(SshProtocol.ErrorBusy, "Der Server ist gerade ausgelastet."), ct).ConfigureAwait(false);
            await CloseAsync(ws).ConfigureAwait(false);
            return;
        }

        try
        {
            await RunCoreAsync(ws, target, sessionAlive, ct, sessionCheck ?? SessionCheck).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
            await CloseAsync(ws).ConfigureAwait(false);
        }
    }

    private async Task RunCoreAsync(WebSocket ws, SshTarget target, Func<CancellationToken, Task<bool>> sessionAlive, CancellationToken ct, TimeSpan sessionCheck)
    {
        var started = Environment.TickCount64;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var lease = _sessions.TryOpen(target.Owner, target.Slot, () => stop.Cancel());
        if (lease is null)
        {
            await FinishAsync(ws, SshProtocol.End(SessionRegistry.TooManyStatus, _sessions.LimitMessage), ct).ConfigureAwait(false);
            return;
        }

        await using var session = _factory();
        var decision = HostKeyDecision.Known;
        try
        {
            await session.ConnectAsync(target.Options, key =>
            {
                decision = HostKeyPolicy.Decide(target.StoredHostKey, target.AcceptHostKey, key);
                return decision != HostKeyDecision.Changed;
            }, stop.Token).ConfigureAwait(false);
        }
        catch (SshException e)
        {
            log.LogInformation("SSH-Verbindung fehlgeschlagen: {Failure}", e.Failure);
            await FinishAsync(ws, FailureMessage(e, session, target), ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            if (lease.Replaced) await FinishAsync(ws, SshProtocol.End(SshProtocol.EndTaken, "Die Sitzung wurde in einem anderen Fenster oder auf einem anderen Gerät übernommen."), ct).ConfigureAwait(false);
            return;                                                  // sonst: Browser weg, bevor die Verbindung stand
        }

        var key = session.PresentedHostKey!;
        if (decision is HostKeyDecision.FirstUse or HostKeyDecision.AcceptedChange)
        {
            // Erst nach erfolgreicher Anmeldung merken: ein Ziel, bei dem das Passwort nicht passt, wird nicht festgeschrieben
            try { await target.SaveHostKey(key.Id, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Host-Schlüssel konnte nicht gespeichert werden: {Type}", e.GetType().Name); }
            log.LogInformation("SSH-Host-Schlüssel {Decision} ({Algorithm})", decision == HostKeyDecision.FirstUse ? "gemerkt" : "ersetzt (vom Benutzer bestätigt)", key.Algorithm);
        }

        byte[]? last = null;
        try
        {
            var plan = await StartProgramAsync(session, target, stop.Token).ConfigureAwait(false);
            await ws.SendAsync(SshProtocol.Ready(decision switch { HostKeyDecision.FirstUse => "new", HostKeyDecision.AcceptedChange => "changed-accepted", _ => "known" }, key.Fingerprint, plan),
                WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            log.LogInformation("SSH-Verbindung offen");
            var flow = new Flow();
            var send = PumpToBrowserAsync(ws, session, flow, stop.Token);
            var receive = ReceiveFromBrowserAsync(ws, session, flow, stop.Token);
            var watch = WatchAsync(sessionAlive, sessionCheck, stop.Token);
            var first = await Task.WhenAny(send, receive, watch).ConfigureAwait(false);
            if (lease.Replaced) last = SshProtocol.End(SshProtocol.EndTaken, "Die Sitzung wurde in einem anderen Fenster oder auf einem anderen Gerät übernommen.");
            else if (first == send && !ct.IsCancellationRequested) last = SshProtocol.End(SshProtocol.EndShellClosed, "Die Sitzung wurde beendet.");
            else if (first == watch) last = SshProtocol.End(SshProtocol.EndSessionExpired, "Die Sitzung der App ist abgelaufen.");

            // Zuerst sagen, warum es endet (ein abgebrochenes Senden reisst den WebSocket ein), dann die Schleifen beenden
            if (last is not null) await FinishAsync(ws, last, ct).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { send, receive, watch })
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or Renci.SshNet.Common.SshException)
                {
                    log.LogDebug("SSH-Ende: {Type}", e.GetType().Name);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Browser weg
        }
        finally
        {
            log.LogInformation("SSH-Verbindung beendet nach {Seconds} s{Replaced}", (Environment.TickCount64 - started) / 1000, lease.Replaced ? " (von einer neuen Verbindung abgelöst)" : "");
        }
    }

    /// <summary>
    /// Programm-Sitzung (docs/CLI-SESSIONS.md): pruefen, ob Programm, Verzeichnis und tmux auf dem Ziel da sind (kurzer Exec-Kanal, hoechstens <see cref="ProbeTimeout"/>), dann die
    /// Startzeile in die Shell schreiben. Die Ausgabe der Shell wartet im Kanal, bis die Pumpe startet. Ohne Programm (gewoehnliche Shell) geschieht nichts. Die Zeile
    /// enthaelt unter Umstaenden den eigenen Befehl des Besitzers: sie wird nicht geloggt.
    /// </summary>
    private async Task<SshStartPlan?> StartProgramAsync(ISshSession session, SshTarget target, CancellationToken ct)
    {
        var launch = target.Launch;
        if (launch is null || launch.IsPlain) return null;
        SshProbe? probe = null;
        try { probe = SshLaunchRules.ParseProbe(await session.ExecAsync(SshLaunchRules.ProbeCommand(launch), ProbeTimeout, ct).ConfigureAwait(false)); }
        catch (Exception e) when (e is not OperationCanceledException) { /* keine Pruefung moeglich: Plan faellt auf die Shell zurueck */ }
        var plan = SshLaunchRules.Plan(launch, target.ConnectionName, target.ConnectionId, probe);
        if (plan.Line is not null) await session.WriteAsync(Encoding.UTF8.GetBytes(plan.Line), ct).ConfigureAwait(false);
        log.LogInformation("Programm-Sitzung: {Program}{Tmux}{Notice}", plan.Program, plan.Tmux ? " (tmux)" : "", plan.Notices.Count > 0 ? " Hinweis: " + plan.NoticeText : "");
        return plan;
    }

    private static byte[] FailureMessage(SshException e, ISshSession session, SshTarget target) => e.Failure switch
    {
        SshFailure.HostKeyRejected => SshProtocol.End(SshProtocol.ErrorHostKeyChanged,
            "Der Schlüssel des Ziels hat sich geändert. Das kann ein Angriff sein oder ein neu aufgesetzter Rechner.", session.PresentedHostKey?.Id, target.StoredHostKey),
        SshFailure.LogonFailed => SshProtocol.End(SshProtocol.ErrorLogonFailed, "Anmeldung am Ziel abgelehnt. Bitte Benutzername und Passwort prüfen."),
        SshFailure.Timeout => SshProtocol.End(SshProtocol.ErrorTimeout, "Das Ziel antwortet nicht (Zeitüberschreitung)."),
        SshFailure.Unreachable => SshProtocol.End(SshProtocol.ErrorUnreachable, "Das Ziel ist nicht erreichbar."),
        _ => SshProtocol.End(SshProtocol.ErrorConnectFailed, "Die Verbindung zum Ziel ist fehlgeschlagen."),
    };

    /// <summary>Shell -> Browser. Der einzige, der sendet.</summary>
    private static async Task PumpToBrowserAsync(WebSocket ws, ISshSession session, Flow flow, CancellationToken ct)
    {
        var buffer = new byte[1 + 16 * 1024];
        buffer[0] = SshProtocol.FromShellData;
        while (true)
        {
            await flow.WaitAsync(ct).ConfigureAwait(false);          // Pause des Browsers: nicht weiterlesen
            var n = await session.ReadAsync(buffer.AsMemory(1), ct).ConfigureAwait(false);
            if (n == 0) return;
            await ws.SendAsync(buffer.AsMemory(0, 1 + n), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Browser -> Shell. Ungueltige Nachrichten beenden die Verbindung (so etwas schickt unsere Seite nicht).</summary>
    private static async Task ReceiveFromBrowserAsync(WebSocket ws, ISshSession session, Flow flow, CancellationToken ct)
    {
        var buffer = new byte[SshProtocol.MaxClientMessage + 1];
        while (!ct.IsCancellationRequested)
        {
            var used = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (used == buffer.Length)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "zu lang", CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                r = await ws.ReceiveAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return;
                used += r.Count;
            }
            while (!r.EndOfMessage);

            if (r.MessageType != WebSocketMessageType.Binary || used == 0 || used > SshProtocol.MaxClientMessage)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "ungültig", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            switch (buffer[0])
            {
                case SshProtocol.ToShellData when used > 1:
                    await session.WriteAsync(buffer.AsMemory(1, used - 1), ct).ConfigureAwait(false);
                    break;
                case SshProtocol.ToShellResize when SshProtocol.TryReadResize(buffer.AsSpan(0, used), out var cols, out var rows):
                    session.Resize(cols, rows);
                    break;
                case SshProtocol.ToShellPing:
                    break;
                case SshProtocol.ToShellPause:
                    flow.Pause();
                    break;
                case SshProtocol.ToShellResume:
                    flow.Resume();
                    break;
                default:
                    await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "ungültig", CancellationToken.None).ConfigureAwait(false);
                    return;
            }
        }
    }

    /// <summary>Haelt das Lesen der Shell an, solange der Browser "Pause" gemeldet hat.</summary>
    private sealed class Flow
    {
        private TaskCompletionSource? _gate;
        private readonly object _lock = new();

        public void Pause() { lock (_lock) _gate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }

        public void Resume() { TaskCompletionSource? g; lock (_lock) { g = _gate; _gate = null; } g?.TrySetResult(); }

        public Task WaitAsync(CancellationToken ct) { Task? t; lock (_lock) t = _gate?.Task; return t is null ? Task.CompletedTask : t.WaitAsync(ct); }
    }

    private static async Task WatchAsync(Func<CancellationToken, Task<bool>> alive, TimeSpan every, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (!await alive(ct).ConfigureAwait(false)) return;
        }
    }

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
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
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
