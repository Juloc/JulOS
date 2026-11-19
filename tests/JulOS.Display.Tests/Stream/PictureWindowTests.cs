namespace JulOS.Integrations.Display.Tests;

/// <summary>Das Fenster unbestaetigter Bilder: ohne Server, ohne Browser, mit allem, was dort schieflaufen kann.</summary>
public sealed class PictureWindowTests
{
    private static PictureWindow Sent(int count, int max = 3)
    {
        var window = new PictureWindow(max);
        for (var i = 1; i <= count; i++)
        {
            window.Sent(i);
        }

        return window;
    }

    [Fact]
    public void The_window_fills_up_and_every_acknowledgement_frees_one_place()
    {
        var window = Sent(3);
        Assert.True(window.Full);
        window.Acknowledge(1);
        Assert.False(window.Full);
        Assert.Equal(2, window.InFlight);
    }

    [Fact]
    public void An_acknowledgement_covers_the_pictures_before_it_too()
    {
        // Bild 1 und 2 wurden verworfen (kein Dekodierer) oder gingen beim Neuoeffnen verloren: die Bestaetigung von 3 raeumt alles auf
        var window = Sent(3);
        window.Acknowledge(3);
        Assert.Equal(0, window.InFlight);
    }

    [Fact]
    public void An_old_or_repeated_acknowledgement_changes_nothing()
    {
        var window = Sent(3);
        window.Acknowledge(2);
        window.Acknowledge(1);
        window.Acknowledge(2);
        Assert.Equal(1, window.InFlight);
    }

    [Fact]
    public void An_acknowledgement_of_a_picture_never_sent_does_not_open_the_window_in_advance()
    {
        var window = Sent(2);
        window.Acknowledge(50);
        Assert.Equal(0, window.InFlight);
        window.Sent(3);
        window.Sent(4);
        window.Sent(5);
        Assert.True(window.Full);
    }

    [Fact]
    public void A_late_acknowledgement_from_before_a_reset_does_not_eat_into_the_new_pictures()
    {
        var window = Sent(3);
        window.Reset();
        Assert.Equal(0, window.InFlight);
        window.Sent(4);
        window.Sent(5);
        window.Acknowledge(3);                          // kommt verspaetet an
        window.Acknowledge(2);
        Assert.Equal(2, window.InFlight);
    }

    [Fact]
    public void An_acknowledgement_without_a_number_frees_the_oldest_place()
    {
        var window = Sent(3);
        window.AcknowledgeOne();
        Assert.Equal(2, window.InFlight);
        window.AcknowledgeOne();
        window.AcknowledgeOne();
        window.AcknowledgeOne();                        // mehr als gesendet: nichts wird negativ
        Assert.Equal(0, window.InFlight);
    }

    [Fact]
    public async Task Parallel_acknowledgements_end_at_the_highest_number()
    {
        var window = Sent(1000, 1000);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 1 + t; i <= 1000; i += 8)
            {
                window.Acknowledge(i);
            }
        })));
        Assert.Equal(0, window.InFlight);
    }
}
