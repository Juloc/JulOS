namespace JulOS.Integrations.Display;

/// <summary>
/// Haelt die Verzoegerung klein, wenn die Leitung langsamer ist als die eingestellte Bitrate (WLAN, Mobilfunk, WAN). Drei Messungen:
/// (1) die Liefergeschwindigkeit: kommen Bestaetigungen im Abstand, in dem die Leitung die Bilder durchlaesst (Bilder gehen hintereinander
/// hinaus, bevor das vorige bestaetigt ist), sagt Bytes je Abstand, was die Leitung schafft; (2) der Rundlauf kleiner Bilder (bis 8 kB: Tippen,
/// Mauszeiger) gegen den kleinsten der letzten Sekunden: steigt er, steht etwas in der Warteschlange der Leitung, und genau das spuert der
/// Benutzer; (3) wie lange der Socket zum Senden braucht (das Gegenstueck zu <c>bufferedAmount</c> im Browser: ist der Sendepuffer voll, dauert
/// das Senden). Liefert die Leitung weniger als gesendet wird oder staut sich etwas, geht die erlaubte Bitrate auf etwa 85 % der gemessenen
/// Liefergeschwindigkeit herunter und das Fenster unbestaetigter Bilder wird kleiner; ist alles ruhig, geht beides in kleinen Schritten zurueck
/// zum Hoechstwert. Die Bitrate geht in den <c>RateController</c>, der daraus gröbere Bilder und weniger Bilder pro Sekunde macht.
///
/// Reine Rechnung mit der Uhr des Aufrufers (Millisekunden): ohne Browser und ohne Netz zu pruefen (LinkPacerTests simuliert eine Leitung).
/// </summary>
internal sealed class LinkPacer
{
    public const int MinKilobits = 600;
    public const int MaxWindow = 3;

    private const int Ring = 256;
    private const int EvaluateEveryMs = 200;
    private const int BackOffEveryMs = 500;
    private const int ProbeEveryMs = 1000;
    private const int HoldAfterBackOffMs = 2500;
    private const int RateWindowMs = 3000;
    private const long MinRttHoldMs = 10_000;
    private const int SlowSendMs = 60;
    private const int SmallPicture = 8 * 1024;
    private const long Never = long.MinValue / 2;

    private readonly int _maxKilobits;
    private readonly long[] _sentAt = new long[Ring];
    private readonly int[] _sentBytes = new int[Ring];
    private readonly int[] _sentSequence = new int[Ring];
    private readonly Queue<(long At, double Delivered, int Largest)> _samples = new();
    private long _blockedAt = Never;
    private int _congestedRuns;
    private int _acked;
    private long _lastAckAt = Never;
    private double _smallRtt = -1;
    private double _minRtt = double.MaxValue;
    private long _minRttAt;
    private long _slowSendAt = Never;
    private long _lastEvaluate, _lastBackOff = Never, _lastProbe, _holdUntil;

    public LinkPacer(int maxKilobits)
    {
        _maxKilobits = Math.Max(maxKilobits, MinKilobits);
        Kilobits = _maxKilobits;
        Window = MaxWindow;
    }

    /// <summary>Die Bitrate, die gerade gilt (zwischen MinKilobits und dem Hoechstwert).</summary>
    public int Kilobits { get; private set; }

    /// <summary>So viele Bilder duerfen unbestaetigt unterwegs sein (1 bis MaxWindow).</summary>
    public int Window { get; private set; }

    /// <summary>Die groesste Liefergeschwindigkeit der letzten Sekunden in kbit/s (0: keine Messung).</summary>
    public int DeliveredKilobits { get; private set; }

    /// <summary>Rundlauf kleiner Bilder (geglaettet) und der kleinste der letzten Sekunden, in ms; 0 vor der ersten Messung.</summary>
    public (int Rtt, int MinRtt) RoundTrip => _smallRtt < 0 ? (0, 0) : ((int)_smallRtt, (int)_minRtt);

    /// <summary>Ein Bild geht hinaus.</summary>
    public void Sent(int sequence, int bytes, long now)
    {
        var slot = sequence & (Ring - 1);
        _sentAt[slot] = now;
        _sentBytes[slot] = bytes;
        _sentSequence[slot] = sequence;
    }

    /// <summary>Der Browser hat alles bis zu dieser Folgenummer dekodiert (kumulativ wie <see cref="PictureWindow"/>).</summary>
    public void Acknowledged(int sequence, long now)
    {
        if (sequence <= _acked)
        {
            return;
        }

        var bytes = 0;
        var first = Math.Max(_acked + 1, sequence - Ring + 1);
        for (var s = first; s <= sequence; s++)
        {
            var slot = s & (Ring - 1);
            if (_sentSequence[slot] == s)
            {
                bytes += _sentBytes[slot];
            }
        }

        var newest = sequence & (Ring - 1);
        _acked = sequence;
        var previousAck = _lastAckAt;
        _lastAckAt = now;
        if (_sentSequence[newest] != sequence)
        {
            return;     // das Bild selbst ist nicht bekannt (nach einem Zuruecksetzen): keine Messung daraus
        }

        // Liefergeschwindigkeit: nur wenn das Bild schon unterwegs war, als das vorige bestaetigt wurde (die Leitung war nicht leer)
        if (previousAck != Never && _sentAt[newest] <= previousAck && now > previousAck)
        {
            _samples.Enqueue((now, bytes * 8.0 / (now - previousAck), bytes));
        }

        // Rundlauf kleiner Bilder: so kurz, dass ihre eigene Sendezeit keine Rolle spielt, also Warteschlange plus Laufzeit
        if (_sentBytes[newest] <= SmallPicture)
        {
            var sample = Math.Max(0, now - _sentAt[newest]);
            _smallRtt = _smallRtt < 0 ? sample : _smallRtt + 0.25 * (sample - _smallRtt);
            if (sample <= _minRtt || now - _minRttAt > MinRttHoldMs)
            {
                _minRtt = sample;
                _minRttAt = now;
            }
        }
    }

    /// <summary>Das Senden eines Bildes an den Socket hat so lange gedauert (voller Sendepuffer).</summary>
    public void SocketSent(long elapsedMs, long now)
    {
        if (elapsedMs >= SlowSendMs)
        {
            _slowSendAt = now;
        }
    }

    /// <summary>Es gab etwas zu senden, aber das Fenster unbestaetigter Bilder war voll: die Leitung (oder der Browser) ist der Engpass.</summary>
    public void Blocked(long now) => _blockedAt = now;

    /// <summary>Alle Bilder gelten als bestaetigt (Zuruecksetzen des Fensters): die Messung von vorher sagt nichts mehr.</summary>
    public void Reset(int lastSequence)
    {
        _acked = Math.Max(_acked, lastSequence);
        _lastAckAt = Never;
    }

    /// <summary>Rechnet nach; true, wenn sich Bitrate oder Fenster geaendert haben.</summary>
    public bool Update(long now)
    {
        if (now - _lastEvaluate < EvaluateEveryMs)
        {
            return false;
        }

        _lastEvaluate = now;
        while (_samples.Count > 0 && now - _samples.Peek().At > RateWindowMs)
        {
            _samples.Dequeue();
        }

        double best = 0;
        var largest = 0;
        foreach (var (_, delivered, size) in _samples)
        {
            best = Math.Max(best, delivered);
            largest = Math.Max(largest, size);
        }

        DeliveredKilobits = (int)best;
        var queue = _smallRtt < 0 ? 0 : _smallRtt - _minRtt;
        var slowSocket = now - _slowSendAt < 1000;

        // Das Fenster war voll, die Bilder sind so gross, dass sie die erlaubte Bitrate brauchen, und geliefert wird deutlich weniger: die Leitung ist
        // der Engpass. (Kleine Bilder bei vollem Fenster heissen nur: der Rundlauf ist lang; da hilft eine kleinere Bitrate nicht.)
        var wanted = Math.Min(Kilobits, largest * 8.0 * 30 / 1000);
        var limited = best > 0 && now - _blockedAt < 600 && best < 0.75 * wanted;
        var jammed = limited || slowSocket || queue > Math.Max(40, 0.75 * _minRtt);

        // Einmal zu langsam ist Zufall (ein belegter Prozessor, ein verspaetetes Paket): erst wenn es zweimal hintereinander so ist
        _congestedRuns = jammed ? _congestedRuns + 1 : 0;
        var congested = _congestedRuns >= 2;
        var kilobits = Kilobits;
        var window = Window;
        if (congested)
        {
            if (now - _lastBackOff >= BackOffEveryMs)
            {
                _lastBackOff = now;
                _holdUntil = now + HoldAfterBackOffMs;
                var target = best > 0 ? (int)(best * 0.85) : (int)(Kilobits * 0.75);
                kilobits = Math.Clamp(Math.Min(Kilobits, target), MinKilobits, _maxKilobits);
                window = queue > 3 * _minRtt + 150 ? 1 : 2;
            }
        }
        else if (!jammed && now >= _holdUntil && now - _lastProbe >= ProbeEveryMs && queue < Math.Max(15, 0.25 * _minRtt))
        {
            _lastProbe = now;
            kilobits = Math.Min(_maxKilobits, (int)(Kilobits * 1.1) + 100);
            window = Math.Min(MaxWindow, Window + 1);
        }

        if (kilobits == Kilobits && window == Window)
        {
            return false;
        }

        Kilobits = kilobits;
        Window = window;
        return true;
    }
}
