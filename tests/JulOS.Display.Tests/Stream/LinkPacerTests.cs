namespace JulOS.Integrations.Display.Tests;

/// <summary>Das Tempo auf langsamer Leitung, an einer simulierten Leitung (Warteschlange mit fester Geschwindigkeit) ohne Netz und ohne Browser.</summary>
public sealed class LinkPacerTests
{
    // Eine Leitung mit Warteschlange: Bilder gehen nacheinander durch (Geschwindigkeit kbit/s zur Zeit des Sendens), dazu je Richtung eine feste
    // Laufzeit; der Sender schickt alle 33 ms ein Bild in der Groesse der Bitrate, solange das Fenster Platz hat.
    private sealed class Link(int maxKilobits, Func<long, int> capacityKilobits, int oneWayMs = 10, int demandKilobits = int.MaxValue)
    {
        public readonly LinkPacer Pacer = new(maxKilobits);
        private readonly List<(long Due, int Sequence)> _acks = [];
        private int _sequence, _acked;
        private long _nextPicture;
        private double _busyUntil;
        public int PeakQueueMsInLast5s;

        public void Run(long fromMs, long toMs, Func<long, bool>? slowSocket = null)
        {
            for (var now = fromMs; now < toMs; now++)
            {
                if (now >= _nextPicture && _sequence - _acked >= Pacer.Window)
                {
                    Pacer.Blocked(now);
                }

                if (now >= _nextPicture && _sequence - _acked < Pacer.Window)
                {
                    _nextPicture = now + 33;
                    var bytes = (int)(Math.Min(Pacer.Kilobits, demandKilobits) * 125 / 30.0);
                    _sequence++;
                    Pacer.Sent(_sequence, bytes, now);
                    if (slowSocket?.Invoke(now) == true)
                    {
                        Pacer.SocketSent(120, now);
                    }

                    if (now >= toMs - 5000)
                    {
                        PeakQueueMsInLast5s = Math.Max(PeakQueueMsInLast5s, (int)Math.Max(0, _busyUntil - now));
                    }

                    var start = Math.Max(now, _busyUntil);
                    _busyUntil = start + bytes / (capacityKilobits(now) / 8.0);
                    _acks.Add(((long)Math.Ceiling(_busyUntil) + oneWayMs * 2 + 3, _sequence));     // durch die Leitung, dekodieren, zurueck
                }

                for (var i = _acks.Count - 1; i >= 0; i--)
                {
                    if (_acks[i].Due <= now)
                    {
                        _acked = Math.Max(_acked, _acks[i].Sequence);
                        Pacer.Acknowledged(_acks[i].Sequence, now);
                        _acks.RemoveAt(i);
                    }
                }

                Pacer.Update(now);
            }
        }
    }

    [Fact]
    public void A_fast_link_keeps_the_full_bit_rate_and_window()
    {
        var link = new Link(16_000, _ => 200_000);
        link.Run(0, 20_000);
        Assert.Equal(16_000, link.Pacer.Kilobits);
        Assert.Equal(LinkPacer.MaxWindow, link.Pacer.Window);
    }

    [Fact]
    public void Small_pictures_on_a_long_but_fast_link_do_not_count_as_congestion()
    {
        // Wenig Bewegung (300 kbit/s Bedarf), Rundlauf 300 ms: das Fenster ist oft voll, aber die Leitung ist nicht der Engpass
        var link = new Link(16_000, _ => 100_000, oneWayMs: 150, demandKilobits: 300);
        link.Run(0, 30_000);
        Assert.Equal(16_000, link.Pacer.Kilobits);
        Assert.Equal(LinkPacer.MaxWindow, link.Pacer.Window);
    }

    [Fact]
    public void A_slow_link_gets_a_bit_rate_near_what_it_delivers_and_the_queue_stays_short()
    {
        var link = new Link(16_000, _ => 3_000);
        link.Run(0, 25_000);
        Assert.True(link.Pacer.Kilobits is >= 1_200 and <= 3_600, $"kbit {link.Pacer.Kilobits} delivered {link.Pacer.DeliveredKilobits} window {link.Pacer.Window} rtt {link.Pacer.RoundTrip}");
        Assert.True(link.PeakQueueMsInLast5s < 250, $"Warteschlange bis {link.PeakQueueMsInLast5s} ms");
    }

    [Fact]
    public void Without_the_pacer_the_same_slow_link_would_queue_for_much_longer()
    {
        // Gegenprobe zur Simulation: bei fester Bitrate 16000 und Fenster 3 auf 3000 kbit/s stehen drei Bilder von 67 kB in der Leitung (etwa 0,5 s)
        const int pictureBytes = 16_000 * 125 / 30;
        var queueMs = 3.0 * pictureBytes / (3_000 / 8.0);
        Assert.True(queueMs > 400, $"{queueMs} ms");
    }

    [Fact]
    public void A_link_that_gets_faster_is_used_again()
    {
        var link = new Link(16_000, now => now < 15_000 ? 3_000 : 40_000);
        link.Run(0, 15_000);
        var slow = link.Pacer.Kilobits;
        Assert.True(slow < 5_000, $"{slow}");
        link.Run(15_000, 60_000);
        Assert.True(link.Pacer.Kilobits >= 12_000, $"{link.Pacer.Kilobits}");
        Assert.Equal(LinkPacer.MaxWindow, link.Pacer.Window);
    }

    [Fact]
    public void A_socket_that_takes_long_to_send_is_a_sign_of_congestion_even_when_the_round_trip_is_short()
    {
        var link = new Link(16_000, _ => 200_000);
        link.Run(0, 5_000);
        Assert.Equal(16_000, link.Pacer.Kilobits);
        link.Run(5_000, 8_000, now => true);
        Assert.True(link.Pacer.Kilobits < 16_000);
        Assert.True(link.Pacer.Window < LinkPacer.MaxWindow);
    }

    [Fact]
    public void The_bit_rate_never_goes_below_the_minimum_or_above_the_maximum()
    {
        var link = new Link(8_000, _ => 100);
        link.Run(0, 30_000);
        Assert.Equal(LinkPacer.MinKilobits, link.Pacer.Kilobits);
        var fast = new Link(8_000, _ => 500_000);
        fast.Run(0, 10_000);
        Assert.True(fast.Pacer.Kilobits <= 8_000);
    }

    [Fact]
    public void An_acknowledgement_of_an_unknown_picture_gives_no_measurement()
    {
        var pacer = new LinkPacer(10_000);
        pacer.Acknowledged(7, 1000);
        Assert.Equal((0, 0), pacer.RoundTrip);
        Assert.False(pacer.Update(5000));
    }
}
