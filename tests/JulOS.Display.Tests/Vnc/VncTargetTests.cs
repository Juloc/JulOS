using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Vnc;

namespace JulOS.Integrations.Display.Tests.Vnc;

/// <summary>
/// Gegen einen echten TigerVNC-Server im Wegwerf-Container (JULOS_TEST_VNC_HOST; deploy/dev/test-vnc.sh startet ihn): Passwort, VeNCrypt mit
/// Zertifikat, jede Kodierung, Eingaben (ein Fenster im Desktop protokolliert Tasten und Mausknoepfe), Zwischenablage, Groessenwechsel und der
/// ganze Weg bis zum H.264-Bild. Ohne die Umgebung werden die Tests uebersprungen.
/// </summary>
public sealed class VncTargetTests
{
    private const int Port = 5900;

    private static string Host()
    {
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_VNC_HOST");
        Assert.SkipWhen(host is null, "JULOS_TEST_VNC_HOST nicht gesetzt (deploy/dev/test-vnc.sh benutzen)");
        return host!;
    }

    private static string Password => Environment.GetEnvironmentVariable("JULOS_TEST_VNC_PASSWORD") ?? "vnctest1";

    private sealed class Handler : IScreenHandler
    {
        public int Updates;
        public readonly List<(int W, int H)> Sizes = [];
        public int Cursors;

        public void Updated(int x, int y, int width, int height) => Interlocked.Increment(ref Updates);

        public void Resized(int width, int height)
        {
            lock (Sizes) Sizes.Add((width, height));
        }

        public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba) => Interlocked.Increment(ref Cursors);

        public void CursorHidden()
        {
        }

        public void CursorDefault()
        {
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Nicht eingetreten: " + what);
            await Task.Delay(25);
        }
    }

    // Wie viele verschiedene Farben das Bild hat (der Desktop hat einen Hintergrund, ein Terminal und ein Fenster: mehr als eine)
    private static int Colours(VncClient client)
    {
        lock (client.FrameLock)
        {
            var seen = new HashSet<int>();
            for (var i = 0; i < client.Framebuffer.Length; i += 4 * 7)
            {
                seen.Add(client.Framebuffer[i] | client.Framebuffer[i + 1] << 8 | client.Framebuffer[i + 2] << 16);
            }

            return seen.Count;
        }
    }

    private static int Hash(VncClient client)
    {
        lock (client.FrameLock)
        {
            var hash = new HashCode();
            hash.AddBytes(client.Framebuffer);
            return hash.ToHashCode();
        }
    }

    [Theory]
    [InlineData("Raw", new[] { 0 })]
    [InlineData("Hextile", new[] { 5, 0 })]
    [InlineData("ZRLE", new[] { 16, 0 })]
    [InlineData("RRE", new[] { 2, 0 })]
    [InlineData("CopyRect with Raw", new[] { 1, 0 })]
    [InlineData("the usual list", null)]
    public async Task Every_encoding_draws_the_desktop(string name, int[]? encodings)
    {
        var host = Host();
        var handler = new Handler();
        await using var client = new VncClient(new VncOptions { Host = host, Port = Port, Password = Password, Encodings = encodings }, handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await client.ConnectAsync(cts.Token);
        var run = client.RunAsync(cts.Token);
        Assert.True(client.Width >= 200 && client.Height >= 200, $"{name}: {client.Width} x {client.Height}");
        await WaitAsync(() => Colours(client) >= 3, name + ": a picture with a background, a window and text");
        Assert.True(handler.Updates > 0);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_wrong_password_is_a_logon_failure_and_no_password_asks_for_one()
    {
        var host = Host();
        await using var wrong = new VncClient(new VncOptions { Host = host, Port = Port, Password = "falsch123" }, new Handler());
        var error = await Assert.ThrowsAsync<RdpException>(() => wrong.ConnectAsync(CancellationToken.None));
        Assert.True(error.IsLogonFailure);

        await using var none = new VncClient(new VncOptions { Host = host, Port = Port }, new Handler());
        var missing = await Assert.ThrowsAsync<RdpException>(() => none.ConnectAsync(CancellationToken.None));
        Assert.True(missing.IsCredentialsMissing);
    }

    [Fact]
    public async Task VeNCrypt_with_a_certificate_connects_and_the_fingerprint_is_checked_before_the_password_goes_out()
    {
        Host();
        var tls = Environment.GetEnvironmentVariable("JULOS_TEST_VNC_TLS_HOST");
        Assert.SkipWhen(tls is null, "JULOS_TEST_VNC_TLS_HOST nicht gesetzt");
        string? seen = null;
        var handler = new Handler();
        await using var client = new VncClient(new VncOptions { Host = tls!, Port = Port, Password = Password, CertificateCheck = f => { seen = f; return true; } }, handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await client.ConnectAsync(cts.Token);
        var run = client.RunAsync(cts.Token);
        Assert.Equal(19, client.SecurityType);
        Assert.Equal(261, client.SecuritySubType);
        Assert.StartsWith("SHA256:", seen);
        await WaitAsync(() => Colours(client) >= 3, "a picture over TLS");
        await cts.CancelAsync();
        await run;

        // A different certificate than the remembered one: stopped, with the fingerprint that was shown
        await using var changed = new VncClient(new VncOptions { Host = tls!, Port = Port, Password = Password, CertificateCheck = _ => false }, new Handler());
        var error = await Assert.ThrowsAsync<RdpException>(() => changed.ConnectAsync(CancellationToken.None));
        Assert.Equal(seen, error.CertificateFingerprint);
    }

    // Das Fenster "probe" im Desktop schreibt die Tasten und Mausknoepfe, die bei ihm ankommen (Fuehler im Container, Port 8080)
    private static async Task<string> ProbeAsync(string host, string file)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return await http.GetStringAsync($"http://{host}:8080/{file}");
    }

    // Wartet, bis das Fuehlerfenster etwas Bestimmtes protokolliert hat; bei Zeitueberschreitung steht sein Protokoll in der Meldung
    private static async Task ProbeContainsAsync(string host, string needle)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        var log = "";
        while (DateTime.UtcNow < until)
        {
            log = await ProbeAsync(host, "input.log");
            if (log.Contains(needle, StringComparison.Ordinal)) return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Das Fuehlerfenster hat \"{needle}\" nicht gesehen. Protokoll:\n{log}");
    }

    [Fact]
    public async Task Clicks_wheel_and_keys_arrive_in_a_window_of_the_desktop()
    {
        var host = Host();
        var handler = new Handler();
        await using var client = new VncClient(new VncOptions { Host = host, Port = Port, Password = Password }, handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await client.ConnectAsync(cts.Token);
        var run = client.RunAsync(cts.Token);
        await WaitAsync(() => Colours(client) >= 3, "a picture");

        // Click into the probe window (600,400 to 900,700), where the pointer must be for keys to reach it
        client.MouseMove(750, 550);
        await Task.Delay(300);
        client.MouseButton(0, true);
        client.MouseButton(0, false);
        client.MouseButton(2, true);
        client.MouseButton(2, false);
        client.MouseWheel(120, false);
        client.MouseWheel(-120, false);
        await WaitAsync(() => ProbeAsync(host, "input.log").GetAwaiter().GetResult().Contains("button 5,"), "mouse events in the probe window");
        var log = await ProbeAsync(host, "input.log");
        Assert.Contains("button 1,", log);
        Assert.Contains("button 3,", log);
        Assert.Contains("button 4,", log);

        // Keys: a typed character by keysym, a German key by scancode with AltGr (the target has a US layout, so what it names is what X makes of the keysym)
        client.Keysym(0x78, true);                      // x
        client.Keysym(0x78, false);
        client.Keysym(0xFF0D, true);                    // Return
        client.Keysym(0xFF0D, false);
        client.Unicode('q');
        await WaitAsync(() => ProbeAsync(host, "input.log").GetAwaiter().GetResult().Contains("keysym 0x71"), "keys in the probe window");
        log = await ProbeAsync(host, "input.log");
        Assert.Contains("keysym 0x78", log);
        Assert.Contains("keysym 0xff0d", log);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Text_goes_both_ways_through_the_clipboard_also_with_characters_beyond_latin1()
    {
        var host = Host();
        var expected = Environment.GetEnvironmentVariable("JULOS_TEST_VNC_CLIP");
        Assert.SkipWhen(expected is null, "JULOS_TEST_VNC_CLIP nicht gesetzt");
        var received = new List<string>();
        var handler = new Handler();
        await using var client = new VncClient(new VncOptions { Host = host, Port = Port, Password = Password }, handler) { ClipboardReceived = text => { lock (received) received.Add(text); } };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await client.ConnectAsync(cts.Token);
        var run = client.RunAsync(cts.Token);

        // Der Desktop setzt alle zwei Sekunden seine Zwischenablage neu
        await WaitAsync(() => { lock (received) return received.Count > 0; }, "text from the desktop", 20);
        lock (received) Assert.Equal(expected, received[0]);
        Assert.True(client.HasExtendedClipboard);

        // Und zurueck: der Desktop liest seine Zwischenablage in eine Datei
        client.SendClipboard("von JulOS \u00e4\u20ac 42");
        var wanted = "von JulOS \u00e4\u20ac 42";
        var until = DateTime.UtcNow.AddSeconds(20);
        var seen = "";
        while (seen != wanted)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Die Zwischenablage des Desktops ist \"{seen}\", nicht \"{wanted}\".");
            await Task.Delay(100);
            seen = await ProbeAsync(host, "clip.txt");
        }
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task The_desktop_follows_a_requested_size()
    {
        var host = Host();
        var handler = new Handler();
        await using var client = new VncClient(new VncOptions { Host = host, Port = Port, Password = Password }, handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await client.ConnectAsync(cts.Token);
        var run = client.RunAsync(cts.Token);
        await WaitAsync(() => client.CanResize, "ExtendedDesktopSize from the server");
        client.RequestSize(1100, 650);
        await WaitAsync(() => client.Width == 1100 && client.Height == 650, "the new size");
        lock (handler.Sizes) Assert.Contains((1100, 650), handler.Sizes);
        Assert.Equal(1100 * 650 * 4, client.Framebuffer.Length);
        await WaitAsync(() => Colours(client) >= 3, "a picture in the new size");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_vnc_desktop_goes_through_the_stream_as_h264_and_follows_input_and_size()
    {
        var host = Host();
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        await using var stream = new ScreenStream(new VncOptions { Host = host, Port = Port, Password = Password });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = stream.RunAsync(cts.Token);

        var size = await NextAsync<ScreenSize>(stream, cts.Token);
        Assert.True(size.Width >= 200 && size.Height >= 200);
        var first = await NextAsync<ScreenPicture>(stream, cts.Token);
        Assert.True(first.IsKey);
        Assert.Equal([0, 0, 0, 1], first.Data[..4]);
        Assert.False(stream.SupportsFiles);

        // Eine Taste und ein Klick im Fuehlerfenster
        stream.Acknowledge();
        stream.MouseMove(750, 550);
        await Task.Delay(300);
        stream.MouseButton(0, true);
        stream.MouseButton(0, false);
        await ProbeContainsAsync(host, "button 1");              // erst wenn der Klick da ist (und das Fenster den Fokus hat), kommen Tasten an
        await Task.Delay(300);
        stream.Keysym(0x6A, true);
        stream.Keysym(0x6A, false);
        await ProbeContainsAsync(host, "keysym 0x6a");

        // Das Browserfenster wird kleiner: der Desktop folgt, und der Browser bekommt die neue Groesse
        stream.Resize(900, 600);
        var resized = await NextAsync<ScreenSize>(stream, cts.Token, s => s.Width == 900 && s.Height == 600);
        Assert.Equal((900, 600), (resized.Width, resized.Height));
        var key = await NextAsync<ScreenPicture>(stream, cts.Token, p => p.IsKey);
        Assert.True(key.Data.Length > 100);
        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static async Task<T> NextAsync<T>(ScreenStream stream, CancellationToken ct, Func<T, bool>? accept = null) where T : ScreenEvent
    {
        while (await stream.Events.WaitToReadAsync(ct))
        {
            while (stream.Events.TryRead(out var evt))
            {
                if (evt is ScreenPicture picture)
                {
                    stream.Acknowledge(picture.Sequence);
                }

                if (evt is T match && (accept is null || accept(match)))
                {
                    return match;
                }
            }
        }

        throw new InvalidOperationException("Die Sitzung endete, ohne dass " + typeof(T).Name + " kam.");
    }
}
