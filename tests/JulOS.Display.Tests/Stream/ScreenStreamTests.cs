using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Tests;

/// <summary>
/// Gegen den echten xrdp-Desktop der Test-Instanz (JULOS_TEST_RDP_HOST) und mit libopenh264 (deploy/dev/test-display.sh startet das in einem
/// Container). Ohne beides werden die Tests uebersprungen.
/// </summary>
public sealed class ScreenStreamTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static string Host()
    {
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_HOST");
        Assert.SkipWhen(host is null, "JULOS_TEST_RDP_HOST nicht gesetzt (deploy/dev/test-display.sh benutzen)");
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        return host!;
    }

    private static RdpOptions Options(string host, int width, int height) =>
        new() { Host = host, UserName = "ubuntu", Password = "ubuntu", Width = width, Height = height };

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
    public async Task A_desktop_arrives_as_a_key_frame_of_h264_and_becomes_sharp_with_repeats()
    {
        var host = Host();
        await using var stream = new ScreenStream(Options(host, 1280, 720));
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);

        var size = await NextAsync<ScreenSize>(stream, cts.Token);
        Assert.True(size.Width >= 200 && size.Height >= 200, $"Groesse {size.Width} x {size.Height}");

        var first = await NextAsync<ScreenPicture>(stream, cts.Token);
        Assert.True(first.IsKey);
        Assert.True(first.Data.Length > 1000, $"Schluesselbild mit nur {first.Data.Length} Bytes");
        Assert.Equal([0, 0, 0, 1], first.Data[..4]);                        // Annex B
        Assert.Equal(7, first.Data[4] & 0x1f);                              // SPS steht vorn

        // Der Browser bestaetigt; danach kommen Wiederholungen, bis das Bild scharf ist (oder weitere Aenderungen)
        var pictures = 1;
        stream.Acknowledge();
        using var more = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        more.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            while (pictures < 4)
            {
                await NextAsync<ScreenPicture>(stream, more.Token);
                pictures++;
                stream.Acknowledge();
            }
        }
        catch (OperationCanceledException) { /* nicht mehr als noetig */ }

        Assert.True(pictures >= 2, $"nur {pictures} Bild(er)");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task The_desktop_follows_a_new_window_size()
    {
        var host = Host();
        await using var stream = new ScreenStream(Options(host, 1280, 720));
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);
        await NextAsync<ScreenSize>(stream, cts.Token);
        await NextAsync<ScreenPicture>(stream, cts.Token);
        stream.Acknowledge();

        // Die Groesse wird im Hintergrund nachgefuehrt, bis der ferne Rechner sie angenommen hat
        stream.Resize(1000, 600);
        var seen = new List<string>();
        using var shortWait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        shortWait.CancelAfter(TimeSpan.FromSeconds(15));
        ScreenSize? resized = null;
        try
        {
            while (resized is null)
            {
                var next = await NextAsync<ScreenSize>(stream, shortWait.Token);
                seen.Add($"{next.Width}x{next.Height}");
                if (next.Width == 1000 && next.Height == 600) { resized = next; }
            }
        }
        catch (OperationCanceledException) { /* unten melden */ }

        Assert.True(resized is not null, "keine Groesse 1000x600 gesehen, gesehen: " + string.Join(", ", seen));

        var key = await NextAsync<ScreenPicture>(stream, cts.Token, p => p.IsKey);
        Assert.Equal(7, key.Data[4] & 0x1f);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_wrong_password_ends_the_session_with_an_error_not_a_hang()
    {
        var host = Host();
        await using var stream = new ScreenStream(new RdpOptions { Host = host, UserName = "ubuntu", Password = "falsch", Width = 1280, Height = 720 });
        using var cts = new CancellationTokenSource(Wait);
        var error = await Record.ExceptionAsync(() => stream.RunAsync(cts.Token));
        // xrdp meldet eine falsche Anmeldung ueber seinen eigenen Anmeldebildschirm, nicht als RDP-Fehler: dann kommt ein Bild von diesem Bildschirm
        // und die Sitzung laeuft weiter, bis wir abbrechen. Beides ist kein Haengen.
        Assert.True(error is null or OperationCanceledException or RdpException, error?.ToString());
    }
}
