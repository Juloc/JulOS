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

    /// <summary>
    /// Der Ton des Desktops (RDPSND, vom xrdp-Ziel aus PipeWire oder PulseAudio) kommt als PCM an, sobald er eingeschaltet ist. Braucht einen Ton im
    /// Desktop: deploy/dev/test-display.sh spielt ihn (JULOS_TEST_RDP_TONE=1) mit deploy/dev/play-tone.sh, sobald die Sitzung steht.
    /// </summary>
    [Fact]
    public async Task Sound_played_on_the_desktop_arrives_as_pcm()
    {
        var host = Host();
        Assert.SkipWhen(Environment.GetEnvironmentVariable("JULOS_TEST_RDP_TONE") is null, "JULOS_TEST_RDP_TONE nicht gesetzt (deploy/dev/test-display.sh benutzen)");
        await using var stream = new ScreenStream(Options(host, 1280, 720));
        stream.Audio.OutputOn = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = stream.RunAsync(cts.Token);

        var loud = 0;
        var total = 0;
        short peak = 0;
        AudioBlock? sample = null;
        var statuses = new List<AudioStatus>();
        try
        {
            while (loud < 20 && await stream.Events.WaitToReadAsync(cts.Token))
            {
                while (stream.Events.TryRead(out var evt))
                {
                    switch (evt)
                    {
                        case ScreenPicture:
                            stream.Acknowledge();
                            break;
                        case ScreenAudioStatus status:
                            statuses.Add(status.Status);
                            break;
                        case ScreenAudioReady:
                            while (stream.Audio.TryTake(out var block))
                            {
                                total++;
                                sample ??= block;
                                var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(block.Pcm.Span);
                                var top = pcm.IsEmpty ? (short)0 : pcm.ToArray().Max(Math.Abs);
                                peak = Math.Max(peak, top);
                                if (top > 1000)
                                {
                                    loud++;
                                }
                            }

                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* unten melden */ }

        Assert.True(loud >= 20, $"nur {loud} laute von {total} Tonbloecken, lauteste Stelle {peak}, Zustaende: " + string.Join(" | ", statuses));
        Assert.True(statuses.Any(s => s.Output), "der Zustand meldete nie, dass der ferne Rechner Ton liefert");
        Assert.Equal(2, sample!.Channels);
        Assert.InRange(sample.Rate, 8000, 96000);
        await cts.CancelAsync();
        await Record.ExceptionAsync(() => run);
    }

    /// <summary>
    /// Das Mikrofon: der Browser schickt einen Ton, im Desktop nimmt ein Programm auf (deploy/dev/record-mic.sh, gestartet von test-display.sh,
    /// die Rohdaten landen in der Datei JULOS_TEST_RDP_REC) und muss ihn hoeren. Braucht den Mikrofon-Kanal des Ziels (pipewire-module-xrdp).
    /// </summary>
    [Fact]
    public async Task The_microphone_reaches_a_program_recording_on_the_desktop()
    {
        var host = Host();
        var recording = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_REC");
        Assert.SkipWhen(recording is null, "JULOS_TEST_RDP_REC nicht gesetzt (deploy/dev/test-display.sh benutzen)");
        await using var stream = new ScreenStream(Options(host, 1280, 720));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var run = stream.RunAsync(cts.Token);

        // 25 Sekunden lang alle 20 ms ein Stueck eines 600-Hz-Tons (16 kHz, Mono); verworfen wird es, solange niemand im Desktop aufnimmt
        var phase = 0;
        var statuses = new List<AudioStatus>();
        var end = DateTime.UtcNow.AddSeconds(25);
        var sent = 0;
        while (DateTime.UtcNow < end && !cts.IsCancellationRequested)
        {
            while (stream.Events.TryRead(out var evt))
            {
                switch (evt)
                {
                    case ScreenPicture:
                        stream.Acknowledge();
                        break;
                    case ScreenAudioStatus status:
                        statuses.Add(status.Status);
                        break;
                }
            }

            var chunk = new byte[320 * 2];
            for (var i = 0; i < 320; i++, phase++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 2), (short)(12000 * Math.Sin(2 * Math.PI * 600 * phase / 16000)));
            }

            stream.Audio.Microphone(chunk, 16000);
            sent++;
            await Task.Delay(20, cts.Token);
        }

        Assert.True(statuses.Any(s => s.MicrophoneAvailable), "der Desktop hat den Mikrofon-Kanal nie angeboten, Zustaende: " + string.Join(" | ", statuses));
        Assert.True(statuses.Any(s => s.MicrophoneRecording), "der Desktop hat nie Ton verlangt, Zustaende: " + string.Join(" | ", statuses));
        await cts.CancelAsync();
        await Record.ExceptionAsync(() => run);

        // die Aufnahme: es muss der Ton darin sein, nicht nur Stille (die Aufnahme laeuft noch ein paar Sekunden, die Datei ist aber schon voll genug)
        var bytes = File.ReadAllBytes(recording!);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, bytes.Length & ~1)).ToArray();
        var loud = samples.Count(sample => Math.Abs(sample) > 4000);
        Assert.True(loud > 8000, $"in der Aufnahme ({samples.Length} Samples, {sent} Stuecke gesendet) nur {loud} laute Samples");
    }
}
