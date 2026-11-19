using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Tests;

/// <summary>
/// RDPGFX gegen echte Server. Gegen den xrdp-Desktop der Test-Instanz (JULOS_TEST_RDP_HOST, wie bei <see cref="ScreenStreamTests"/>): das
/// Debian-Paket xrdp liefert durch die Grafik-Pipeline kein H.264, die Sitzung muss also zurueckfallen und trotzdem ein Bild liefern. Gegen einen
/// Server, der H.264 liefert (JULOS_TEST_RDP_GFX_HOST, siehe docs/RDPGFX.md), muss der Strom das H.264 des Servers durchreichen.
/// </summary>
public sealed class GfxStreamTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static RdpOptions Options(string host) =>
        new() { Host = host, UserName = "ubuntu", Password = "ubuntu", Width = 1280, Height = 720 };

    private static async Task<T> NextAsync<T>(ScreenStream stream, CancellationToken ct, Func<T, bool>? accept = null) where T : ScreenEvent
    {
        while (await stream.Events.WaitToReadAsync(ct))
        {
            while (stream.Events.TryRead(out var evt))
            {
                if (evt is T match && (accept is null || accept(match)))
                {
                    return match;
                }
            }
        }

        throw new InvalidOperationException("Die Sitzung endete, ohne dass " + typeof(T).Name + " kam.");
    }

    [Fact]
    public async Task A_server_without_h264_in_the_pipeline_makes_the_session_fall_back_to_its_own_encoder()
    {
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_HOST");
        Assert.SkipWhen(host is null, "JULOS_TEST_RDP_HOST nicht gesetzt (deploy/dev/test-display.sh benutzen)");
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("JULOS_TEST_RDP_GFX_H264") == "1", "dieser Server liefert H.264 durch RDPGFX");
        GfxSettings.Forget();

        await using var stream = new ScreenStream(Options(host!), graphicsPipeline: true);
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);

        // Ein Bild des eigenen Kodierers kommt, auch wenn der Server RDPGFX angeboten hat
        var key = await NextAsync<ScreenPicture>(stream, cts.Token, picture => picture.IsKey);
        Assert.Equal([0, 0, 0, 1], key.Data[..4]);
        var encoder = await NextAsync<ScreenEncoderInfo>(stream, cts.Token);
        Assert.DoesNotContain("RDPGFX", encoder.Name);

        var (passthrough, left) = stream.GfxState;
        Assert.False(passthrough);
        Assert.NotNull(left);
        Assert.True(GfxSettings.IsAvoided(host!, 3389), "ein Server ohne H.264 wird eine Weile nicht noch einmal gefragt");

        // und es bleibt ein Strom: der Browser bestaetigt, der Desktop liefert weiter
        stream.Acknowledge(key.Sequence);
        await cts.CancelAsync();
        await run;
        GfxSettings.Forget();
    }

    [Fact]
    public async Task A_server_with_h264_in_the_pipeline_has_its_pictures_passed_on_without_coding_them_again()
    {
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_GFX_HOST");
        Assert.SkipWhen(host is null, "JULOS_TEST_RDP_GFX_HOST nicht gesetzt (ein xrdp mit x264, siehe docs/RDPGFX.md)");
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        GfxSettings.Forget();

        await using var stream = new ScreenStream(Options(host!), graphicsPipeline: true);
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);

        var info = await NextAsync<ScreenEncoderInfo>(stream, cts.Token, e => e.Name.Contains("RDPGFX"));
        Assert.Equal(Video.VideoCodec.H264, info.Codec);
        var size = await NextAsync<ScreenSize>(stream, cts.Token);
        Assert.True(size.Width >= 200 && size.Height >= 200);

        // Das erste durchgereichte Bild ist ein Schluesselbild mit SPS vorn (Annex B); danach folgen Bilder, wenn der Browser bestaetigt
        var first = await NextAsync<ScreenPicture>(stream, cts.Token, picture => picture.IsKey);
        Assert.True(first.Data.Length > 500);
        Assert.True(stream.GfxState.Passthrough);
        var more = 0;
        stream.Acknowledge(first.Sequence);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        window.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (more < 5)
            {
                var next = await NextAsync<ScreenPicture>(stream, window.Token);
                Assert.True(next.Sequence > first.Sequence);
                more++;
                stream.Acknowledge(next.Sequence);
            }
        }
        catch (OperationCanceledException) { /* ein ruhender Desktop sendet nicht weiter */ }

        Assert.Null(stream.GfxState.Left);
        await cts.CancelAsync();
        await run;
    }
}
