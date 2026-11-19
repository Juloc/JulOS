using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Die waehlbare Bildrate je Sitzung (Nachricht 31): Protokoll, Zahlen der Stufen und ihre Wirkung auf die Bitrate des Streams, ohne Netz.</summary>
public sealed class StreamQualityTests
{
    private static ScreenStream NewStream(int maxKilobits = 16_000) =>
        new(new RdpOptions { Host = "unused", UserName = "u", Password = "p" }, maxKilobits);

    [Fact]
    public async Task The_quality_message_chooses_the_step_and_nonsense_is_ignored()
    {
        await using var stream = NewStream();
        Assert.Equal(StreamQuality.Auto, stream.Quality);                                   // der Standard ist das bisherige Verhalten
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserQuality, 1]);
        Assert.Equal(StreamQuality.Fps60, stream.Quality);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserQuality, 2]);
        Assert.Equal(StreamQuality.Fps30, stream.Quality);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserQuality, 3]);
        Assert.Equal(StreamQuality.Economy, stream.Quality);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserQuality, 9]);                // unbekannte Stufe: bleibt, wie sie ist
        Assert.Equal(StreamQuality.Economy, stream.Quality);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserQuality]);                   // zu kurz
        Assert.Equal(StreamQuality.Economy, stream.Quality);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserQuality, 0]);
        Assert.Equal(StreamQuality.Auto, stream.Quality);
    }

    [Fact]
    public void The_message_number_follows_the_last_one_given_out_and_collides_with_none()
    {
        Assert.Equal(31, ScreenProtocol.FromBrowserQuality);
        var all = new[]
        {
            ScreenProtocol.FromBrowserAck, ScreenProtocol.FromBrowserMove, ScreenProtocol.FromBrowserButton, ScreenProtocol.FromBrowserWheel,
            ScreenProtocol.FromBrowserKey, ScreenProtocol.FromBrowserUnicode, ScreenProtocol.FromBrowserResize, ScreenProtocol.FromBrowserKeyFrame,
            ScreenProtocol.FromBrowserClipboard, ScreenProtocol.FromBrowserSendBegin, ScreenProtocol.FromBrowserSendChunk, ScreenProtocol.FromBrowserSendEnd,
            ScreenProtocol.FromBrowserSendCancel, ScreenProtocol.FromBrowserFileRequest, ScreenProtocol.FromBrowserFileCancel, ScreenProtocol.FromBrowserPause,
            ScreenProtocol.FromBrowserEnd, ScreenProtocol.FromBrowserAudio, ScreenProtocol.FromBrowserMicrophone, ScreenProtocol.FromBrowserCodecs,
            ScreenProtocol.FromBrowserQuality,
        };
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Theory]
    [InlineData((byte)0, true)]
    [InlineData((byte)3, true)]
    [InlineData((byte)4, false)]
    [InlineData((byte)255, false)]
    public void Only_the_four_steps_are_steps(byte value, bool known) => Assert.Equal(known, StreamQualityPolicy.Parse(value) is not null);

    [Fact]
    public void The_steps_give_the_gaps_between_pictures_that_make_60_30_and_15_a_second()
    {
        Assert.Equal(0, StreamQualityPolicy.MinIntervalMs(StreamQuality.Auto));
        Assert.Equal(16, StreamQualityPolicy.MinIntervalMs(StreamQuality.Fps60));
        Assert.Equal(33, StreamQualityPolicy.MinIntervalMs(StreamQuality.Fps30));
        Assert.Equal(66, StreamQualityPolicy.MinIntervalMs(StreamQuality.Economy));
        Assert.InRange(1000 / StreamQualityPolicy.MinIntervalMs(StreamQuality.Economy), 14, 16);
    }

    [Theory]
    [InlineData(StreamQuality.Auto, 16_000, 16_000, 16_000)]
    [InlineData(StreamQuality.Fps60, 16_000, 16_000, 16_000)]
    [InlineData(StreamQuality.Fps30, 16_000, 9_000, 9_000)]
    [InlineData(StreamQuality.Economy, 16_000, 16_000, 5_600)]       // 35 % der Hoechstrate
    [InlineData(StreamQuality.Economy, 16_000, 3_000, 3_000)]        // die Leitungsmessung darf darunter bleiben
    [InlineData(StreamQuality.Economy, 1_000, 1_000, LinkPacer.MinKilobits)]   // nie unter dem Mindestwert
    public void The_bit_rate_is_capped_by_the_step_but_never_raised_above_what_the_link_allows(StreamQuality quality, int max, int paced, int expected) =>
        Assert.Equal(expected, StreamQualityPolicy.Kilobits(quality, max, paced));

    [Fact]
    public async Task The_economy_step_lowers_the_stream_bit_rate_and_auto_brings_it_back()
    {
        await using var stream = NewStream(16_000);
        Assert.Equal(16_000, stream.CurrentKilobits);
        stream.AdaptToLink();
        Assert.Equal(16_000, stream.CurrentKilobits);                          // Auto: nichts geaendert

        stream.SetQuality(StreamQuality.Economy);
        stream.AdaptToLink();
        Assert.Equal(5_600, stream.CurrentKilobits);

        stream.SetQuality(StreamQuality.Fps60);                                // 60: volle Bitrate
        stream.AdaptToLink();
        Assert.Equal(16_000, stream.CurrentKilobits);

        stream.SetQuality(StreamQuality.Economy);
        stream.AdaptToLink();
        stream.SetQuality(StreamQuality.Auto);
        stream.AdaptToLink();
        Assert.Equal(16_000, stream.CurrentKilobits);
    }
}
