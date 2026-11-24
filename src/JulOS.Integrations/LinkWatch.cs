namespace JulOS.Integrations;

/// <summary>
/// Lebenszeichen des Browsers auf einer WebSocket-Leitung (Stream und Terminal): jede Nachricht des Browsers zaehlt. Reisst die Leitung still ab
/// (Router, Mobilfunk-NAT), meldet das Betriebssystem es erst beim naechsten Schreiben; ein neuer Browser schickt darum alle paar Sekunden ein Ping,
/// wenn sonst nichts fliesst. Wer so ein Ping schon einmal geschickt hat (<see cref="MarkCapable"/>), gilt als tot, wenn laenger als die Frist nichts kam;
/// ein alter Browser, der nie pingt, wird nie fuer tot erklaert (abwaertskompatibel). Haelt der Browser an (Pause: Seite im Hintergrund, seine Zeitgeber
/// werden gedrosselt), gilt die lange Frist.
/// </summary>
public sealed class LinkWatch(Func<long>? clock = null, TimeSpan? deadAfter = null, TimeSpan? relaxedAfter = null)
{
    /// <summary>Der Browser pingt nach 5 s ohne Verkehr; 12 s sind zwei verpasste Pings plus Spielraum.</summary>
    public static readonly TimeSpan DefaultDeadAfter = TimeSpan.FromSeconds(12);

    /// <summary>Im Hintergrund drosselt der Browser Zeitgeber bis auf einmal je Minute.</summary>
    public static readonly TimeSpan DefaultRelaxedAfter = TimeSpan.FromSeconds(90);

    private readonly Func<long> _now = clock ?? (() => Environment.TickCount64);
    private readonly long _deadMs = (long)(deadAfter ?? DefaultDeadAfter).TotalMilliseconds;
    private readonly long _relaxedMs = (long)(relaxedAfter ?? DefaultRelaxedAfter).TotalMilliseconds;
    private long _last = (clock ?? (() => Environment.TickCount64))();
    private volatile bool _capable;
    private volatile bool _relaxed;

    /// <summary>Ob der Browser das Lebenszeichen schon einmal geschickt hat.</summary>
    public bool Capable => _capable;

    /// <summary>Etwas vom Browser ist angekommen.</summary>
    public void Heard() => Volatile.Write(ref _last, _now());

    /// <summary>Der Browser hat gepingt: er kann das Lebenszeichen, ab jetzt zaehlt Stille.</summary>
    public void MarkCapable() => _capable = true;

    /// <summary>Der Browser ist im Hintergrund (Pause): lange Frist.</summary>
    public void SetRelaxed(bool on) => _relaxed = on;

    public long SilentMs => _now() - Volatile.Read(ref _last);

    public bool IsDead => _capable && SilentMs > (_relaxed ? _relaxedMs : _deadMs);

    /// <summary>Endet, sobald die Leitung als tot gilt (Abfrage jede halbe Sekunde); bei Abbruch des Tokens mit der Ausnahme.</summary>
    public async Task WaitDeadAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (IsDead)
            {
                return;
            }
        }
    }
}
