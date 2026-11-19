using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Video;

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

    // A "GPU" that is only there to be told about: it can encode what it is asked for (fake bytes) or fail, and counts its calls.
    private sealed class FakeGpu(VideoCodecs codecs, bool failOnEncode = false) : IEncoderBackend
    {
        public int Created { get; private set; }

        public int Encoded { get; set; }

        public string Name => "Fake GPU";

        public bool IsHardware => true;

        public VideoCodecs Codecs => codecs;

        public bool CheckAvailable(out string? reason)
        {
            reason = null;
            return true;
        }

        public IVideoEncoder Create(EncoderRequest request, VideoCodec codec)
        {
            Created++;
            return new FakeGpuEncoder(this, request, codec, failOnEncode);
        }
    }

    private sealed class FakeGpuEncoder(FakeGpu owner, EncoderRequest request, VideoCodec codec, bool fail) : IVideoEncoder
    {
        public VideoCodec Codec => codec;

        public string Name => "Fake GPU";

        public bool IsHardware => true;

        public int Width => request.Width;

        public int Height => request.Height;

        public int KilobitsPerSecond => request.KilobitsPerSecond;

        public bool IsSharp => true;

        public int SuggestedInterval(long changedPixels) => 0;

        public ReadOnlyMemory<byte> Encode(I420Frame frame, bool forceKey, out bool isKey)
        {
            owner.Encoded++;
            if (fail)
            {
                throw new InvalidOperationException("the device is gone");
            }

            isKey = true;
            return codec == VideoCodec.Hevc ? new byte[] { 0, 0, 0, 1, 0x40, 0x01, 1, 2, 3 } : new byte[] { 0, 0, 0, 1, 0x67, 1, 2, 3 };
        }

        public bool TrySetBitrate(int kilobitsPerSecond) => true;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_gpu_that_fails_in_the_middle_of_the_session_hands_over_to_the_software_encoder_with_a_key_frame()
    {
        var host = Host();
        var gpu = new FakeGpu(VideoCodecs.H264, failOnEncode: true);
        var selector = new EncoderSelector([gpu, new SoftwareBackend()], new EncoderSettings(EncoderMode.Auto));
        await using var stream = new ScreenStream(Options(host, 1280, 720), 16_000, null, selector);
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);
        await NextAsync<ScreenSize>(stream, cts.Token);

        var first = await NextAsync<ScreenEncoderInfo>(stream, cts.Token);
        Assert.True(first.Hardware);
        var second = await NextAsync<ScreenEncoderInfo>(stream, cts.Token, i => !i.Hardware);       // after the failure
        Assert.Equal(VideoCodec.H264, second.Codec);
        Assert.StartsWith("OpenH264", second.Name);
        var picture = await NextAsync<ScreenPicture>(stream, cts.Token);
        Assert.True(picture.IsKey);
        Assert.Equal(VideoCodec.H264, picture.Codec);
        Assert.Equal(7, picture.Data[4] & 0x1F);
        Assert.True(gpu.Encoded >= 1);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Hevc_goes_to_a_browser_that_can_and_the_stream_goes_back_to_h264_when_the_browser_says_so()
    {
        var host = Host();
        var gpu = new FakeGpu(VideoCodecs.H264 | VideoCodecs.Hevc);
        var selector = new EncoderSelector([gpu, new SoftwareBackend()], new EncoderSettings(EncoderMode.Auto));
        await using var stream = new ScreenStream(Options(host, 1280, 720), 16_000, null, selector, VideoCodecs.H264 | VideoCodecs.Hevc);
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);
        await NextAsync<ScreenSize>(stream, cts.Token);

        var info = await NextAsync<ScreenEncoderInfo>(stream, cts.Token);
        Assert.Equal(VideoCodec.Hevc, info.Codec);
        var hevc = await NextAsync<ScreenPicture>(stream, cts.Token);
        Assert.Equal(VideoCodec.Hevc, hevc.Codec);
        Assert.True(hevc.IsKey);
        stream.Acknowledge(hevc.Sequence);

        // the browser's HEVC decoder failed: H.264 only from now on
        stream.SetClientCodecs(VideoCodecs.H264);
        var back = await NextAsync<ScreenEncoderInfo>(stream, cts.Token, i => i.Codec == VideoCodec.H264);
        Assert.Equal(VideoCodec.H264, back.Codec);
        var key = await NextAsync<ScreenPicture>(stream, cts.Token, p => p.Codec == VideoCodec.H264);
        Assert.True(key.IsKey);
        await cts.CancelAsync();
        await run;
    }

    // Bestaetigt alles, was kommt, bis eine Weile nichts mehr kommt (der Bildschirm steht still und ist scharf); gibt die letzte Folgenummer zurueck.
    private static async Task<int> SettleAsync(ScreenStream stream, CancellationToken ct)
    {
        var last = 0;
        while (true)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(TimeSpan.FromSeconds(2.5));
            try
            {
                var picture = await NextAsync<ScreenPicture>(stream, quiet.Token);
                last = picture.Sequence;
                stream.Acknowledge(picture.Sequence);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return last;
            }
        }
    }

    [Fact]
    public async Task A_key_frame_request_is_answered_even_when_the_screen_is_still_and_sharp()
    {
        // Der Browser verliert den Faden (Dekodierer ausgestiegen): ruht der Bildschirm, kam frueher nie ein neues Schluesselbild
        var host = Host();
        await using var stream = new ScreenStream(Options(host, 1280, 720));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var run = stream.RunAsync(cts.Token);
        await NextAsync<ScreenSize>(stream, cts.Token);
        // Erst warten, bis der Desktop wirklich ruht (der frisch gestartete tut es nicht gleich, und ein laufendes Video nie)
        var settled = 0;
        var quiet = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        for (var round = 0; round < 6 && !quiet; round++)
        {
            var last = await SettleAsync(stream, budget.Token);
            settled = Math.Max(settled, last);
            quiet = last == 0 && settled > 0;                                       // eine ganze Ruhephase ohne ein Bild
        }

        Assert.SkipUnless(quiet, "der Test-Desktop kommt nicht zur Ruhe (laeuft dort ein Video?)");
        stream.RequestKeyFrame();
        using var soon = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        soon.CancelAfter(TimeSpan.FromSeconds(3));                                   // nicht erst, wenn sich zufaellig etwas aendert
        var key = await NextAsync<ScreenPicture>(stream, soon.Token, p => p.IsKey);
        Assert.True(key.Sequence > settled);
        Assert.Equal(7, key.Data[4] & 0x1f);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Lost_acknowledgements_stall_the_stream_only_until_the_watchdog_resets_the_window()
    {
        var host = Host();
        await using var stream = new ScreenStream(Options(host, 1280, 720));
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);
        await NextAsync<ScreenSize>(stream, cts.Token);
        await NextAsync<ScreenPicture>(stream, cts.Token);                        // ohne je zu bestaetigen

        // Hoechstens drei Bilder ohne Bestaetigung ...
        var unacknowledged = 1;
        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
        {
            quiet.CancelAfter(TimeSpan.FromSeconds(1.2));
            try
            {
                while (true)
                {
                    await NextAsync<ScreenPicture>(stream, quiet.Token);
                    unacknowledged++;
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested) { /* still */ }
        }

        Assert.True(unacknowledged <= ScreenStream.MaxPicturesInFlight, $"{unacknowledged} Bilder ohne Bestaetigung");

        // ... und nach einigen Sekunden Stille kommt von selbst ein Schluesselbild
        var key = await NextAsync<ScreenPicture>(stream, cts.Token, p => p.IsKey);
        Assert.True(key.Sequence > unacknowledged || unacknowledged == 1);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task One_acknowledgement_by_number_frees_the_window_for_pictures_that_were_dropped()
    {
        var host = Host();
        await using var stream = new ScreenStream(Options(host, 1280, 720));
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);
        await NextAsync<ScreenSize>(stream, cts.Token);
        var first = await NextAsync<ScreenPicture>(stream, cts.Token);
        // Alles, was der Browser (zum Beispiel wegen eines Dekodiererwechsels) nie zeichnet, wird mit einer einzigen Nummer bestaetigt
        stream.Acknowledge(first.Sequence + 100);                                  // weit ueber das Gesendete hinaus: gilt nur bis dahin
        stream.RequestKeyFrame();
        var key = await NextAsync<ScreenPicture>(stream, cts.Token, p => p.IsKey && p.Sequence > first.Sequence);
        Assert.True(key.Sequence > first.Sequence);
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

    [Fact]
    public async Task An_image_and_a_file_from_the_browser_are_taken_by_the_clipboard_of_a_real_xrdp()
    {
        var host = Host();
        await using var stream = new ScreenStream(Options(host, 1024, 640));
        using var cts = new CancellationTokenSource(Wait);
        var run = stream.RunAsync(cts.Token);
        await NextAsync<ScreenSize>(stream, cts.Token);

        // der Kanal braucht einen Augenblick, bis der Server (chansrv) ihn bereit meldet
        ScreenTransferResult result;
        var bmp = TinyBmp();
        for (var attempt = 1; ; attempt++)
        {
            stream.BeginTransfer(1, ClipboardBridge.KindImage, 0, 1, bmp.Length, "");
            stream.TransferChunk(1, bmp);
            stream.EndTransfer(1);
            result = await NextAsync<ScreenTransferResult>(stream, cts.Token, r => r.Id == 1);
            if (result.Code != ClipboardBridge.ResultNoClipboard || attempt >= 10)
            {
                break;
            }

            await Task.Delay(500, cts.Token);
        }

        Assert.True(result.Code == ClipboardBridge.ResultOk, $"Bild: Code {result.Code} {result.Message} (laeuft xrdp-chansrv? siehe docs/DEVELOPMENT.md)");

        // Dateien nimmt xrdp nur an, wenn es sie per FUSE einhaengen kann (compose.yaml: /dev/fuse beim rdp-target) und wenn der letzte Client sie
        // wollte: chansrv behaelt die Faehigkeiten des zuletzt verbundenen Clients, und guacd schaltet sie ab (siehe docs/DEVELOPMENT.md)
        byte[] content = [1, 2, 3, 4, 5];
        stream.BeginTransfer(2, ClipboardBridge.KindFile, 0, 1, content.Length, "Test Übung.bin");
        stream.TransferChunk(2, content);
        stream.EndTransfer(2);
        result = await NextAsync<ScreenTransferResult>(stream, cts.Token, r => r.Id == 2);
        if (result.Code == ClipboardBridge.ResultNoClipboard)
        {
            await cts.CancelAsync();
            await run;
            Assert.Skip("xrdp bietet keine Dateien an (kein /dev/fuse, oder zuletzt war ein Guacamole-Client verbunden: Ziel neu starten).");
        }

        Assert.True(result.Code == ClipboardBridge.ResultOk, $"Datei: Code {result.Code} {result.Message}");

        await cts.CancelAsync();
        await run;
    }

    // 2 x 2 Punkte, 24 Bit
    private static byte[] TinyBmp()
    {
        var bmp = new byte[54 + 16];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(2).CopyTo(bmp, 18);
        BitConverter.GetBytes(2).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(16).CopyTo(bmp, 34);
        bmp.AsSpan(54).Fill(0x80);
        return bmp;
    }
}
