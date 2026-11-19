namespace JulOS.Integrations.Display;

/// <summary>
/// Wie viele Bilder unterwegs sind, ohne dass der Browser sie bestaetigt hat. Bestaetigt wird mit der Folgenummer des Bildes und gilt fuer
/// dieses und alle davor (kumulativ): ein Bild, das der Browser verworfen hat oder das beim Neuoeffnen des Dekodierers verloren ging, kann
/// so das Fenster nicht dauerhaft verstopfen, und eine verspaetete Bestaetigung aus der Zeit vor einem Zuruecksetzen zaehlt nicht doppelt.
/// </summary>
internal sealed class PictureWindow(int max)
{
    private int _max = max;
    private long _sent;                 // Folgenummer des zuletzt gesendeten Bildes
    private long _acked;                // Folgenummer, bis zu der alles bestaetigt (oder verworfen) ist

    /// <summary>So viele Bilder duerfen unterwegs sein; auf langsamer Leitung stellt <see cref="LinkPacer"/> es kleiner (mindestens 1).</summary>
    public int Max
    {
        get => Volatile.Read(ref _max);
        set => Volatile.Write(ref _max, Math.Max(1, value));
    }

    public int InFlight => (int)Math.Max(0, Volatile.Read(ref _sent) - Volatile.Read(ref _acked));

    public bool Full => InFlight >= Max;

    /// <summary>Ein Bild geht hinaus; die Nummer kommt vom Aufrufer (ab 1, aufsteigend).</summary>
    public void Sent(long sequence) => Volatile.Write(ref _sent, sequence);

    /// <summary>Alles bis einschliesslich dieser Nummer ist beim Browser angekommen. Alte oder zu grosse Nummern aendern nichts.</summary>
    public void Acknowledge(long sequence)
    {
        var sent = Volatile.Read(ref _sent);
        var target = Math.Min(sequence, sent);
        while (true)
        {
            var current = Volatile.Read(ref _acked);
            if (target <= current || Interlocked.CompareExchange(ref _acked, target, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Eine Bestaetigung ohne Nummer (aelterer Browser-Code): das aelteste offene Bild.</summary>
    public void AcknowledgeOne() => Acknowledge(Volatile.Read(ref _acked) + 1);

    /// <summary>Alles gilt als bestaetigt: nach einem Wechsel der Groesse oder wenn die Bestaetigungen ausblieben.</summary>
    public void Reset() => Acknowledge(Volatile.Read(ref _sent));
}
