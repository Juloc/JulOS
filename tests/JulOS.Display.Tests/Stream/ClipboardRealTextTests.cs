using System.Net.Http;
using System.Text;
using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Tests;

/// <summary>
/// Der Text der Zwischenablage gegen einen echten xrdp (JULOS_TEST_RDP_HOST, deploy/dev/test-display.sh): in beide Richtungen, mit dem, was ein
/// Anwender kopiert (mehrere Zeilen, Umlaute, Zeichen ausserhalb der BMP, grosse Texte). Die Zwischenablage der VM setzen und lesen die Tests ueber
/// den kleinen Dienst in der Sitzung (deploy/dev/rdp-target/clip-probe.py, xclip); ohne ihn werden die Tests uebersprungen.
/// </summary>
public sealed class ClipboardRealTextTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(45);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero }) { Timeout = TimeSpan.FromSeconds(20) };

    private sealed class Probe(string host)
    {
        private string Url(string path) => $"http://{host}:9777{path}";

        public async Task<bool> IsUpAsync()
        {
            try
            {
                using var response = await Http.GetAsync(Url("/targets"));
                return true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        // stall: der Besitzer der Zwischenablage wird angehalten und antwortet nie (wie ein haengender Zwischenablage-Verwalter in der VM)
        public async Task SetAsync(string text, string selection = "clipboard", string type = "UTF8_STRING", bool stall = false)
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            using var response = await Http.PostAsync(Url($"/set?sel={selection}&type={Uri.EscapeDataString(type)}&stall={(stall ? 1 : 0)}"), content);
            response.EnsureSuccessStatusCode();
        }

        public async Task<string?> GetAsync(string selection = "clipboard", string type = "UTF8_STRING")
        {
            using var response = await Http.GetAsync(Url($"/get?sel={selection}&type={Uri.EscapeDataString(type)}"));
            return response.IsSuccessStatusCode ? Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()) : null;
        }

        public async Task ClearAsync()
        {
            using var response = await Http.PostAsync(Url("/clear"), new ByteArrayContent([]));
            response.EnsureSuccessStatusCode();
        }
    }

    private sealed class Session(ScreenStream stream, Probe probe, CancellationTokenSource cts, Task run) : IAsyncDisposable
    {
        public ScreenStream Stream { get; } = stream;
        public Probe Probe { get; } = probe;
        public CancellationToken Token => cts.Token;

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) { /* Ende der Sitzung */ }
            await Stream.DisposeAsync();
            cts.Dispose();
        }

        public async Task<string> NextTextAsync(TimeSpan? within = null)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            limit.CancelAfter(within ?? TimeSpan.FromSeconds(15));
            try
            {
                while (await Stream.Events.WaitToReadAsync(limit.Token))
                {
                    while (Stream.Events.TryRead(out var evt))
                    {
                        if (evt is ScreenClipboardText text)
                        {
                            return text.Text;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                throw new TimeoutException("Es kam kein Text aus der VM.");
            }

            throw new InvalidOperationException("Die Sitzung endete.");
        }

        public async Task<bool> NoTextAsync(TimeSpan within)
        {
            try
            {
                await NextTextAsync(within);
                return false;
            }
            catch (TimeoutException)
            {
                return true;
            }
        }
    }

    private static async Task<Session> OpenAsync()
    {
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_HOST");
        Assert.SkipWhen(host is null, "JULOS_TEST_RDP_HOST nicht gesetzt (deploy/dev/test-display.sh benutzen)");
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        var stream = new ScreenStream(new RdpOptions { Host = host!, UserName = "ubuntu", Password = "ubuntu", Width = 1024, Height = 640 });
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var run = stream.RunAsync(cts.Token);
        var probe = new Probe(host!);
        var session = new Session(stream, probe, cts, run);

        // die Sitzung der VM braucht einen Augenblick, bis der Dienst in ihr antwortet
        for (var i = 0; i < 60 && !await probe.IsUpAsync(); i++)
        {
            await Task.Delay(1000);
        }

        Assert.True(await probe.IsUpAsync(), "Der Dienst clip-probe.py in der Sitzung antwortet nicht (Ziel neu bauen: deploy/dev/up.sh)");
        return session;
    }

    // Text in der VM kopieren, bis er beim Browser ankommt: der Kanal ist erst bereit, wenn chansrv ihn meldet, und davor geht eine Kopie verloren.
    private static async Task<string> CopyInVmUntilArrivalAsync(Session s, string text, string selection = "clipboard")
    {
        for (var attempt = 0; ; attempt++)
        {
            await s.Probe.SetAsync(text, selection);
            try
            {
                return await s.NextTextAsync(TimeSpan.FromSeconds(4));
            }
            catch (TimeoutException) when (attempt < 8)
            {
                // noch einmal
            }
        }
    }

    [Fact]
    public async Task Text_copied_in_the_vm_arrives_at_the_browser()
    {
        await using var s = await OpenAsync();
        Assert.Equal("Hallo Welt", await CopyInVmUntilArrivalAsync(s, "Hallo Welt"));
    }

    [Fact]
    public async Task Lines_umlauts_and_characters_outside_the_bmp_survive_the_way_from_the_vm()
    {
        await using var s = await OpenAsync();
        Assert.Equal("Hallo", await CopyInVmUntilArrivalAsync(s, "Hallo"));

        var text = "Zeile 1\nZeile 2 mit Umlauten: äöüß ÄÖÜ €\n\nZeile 4 \U0001F600 \U0001D11E 漢字\n";
        await s.Probe.SetAsync(text);
        Assert.Equal(text, await s.NextTextAsync());
    }

    [Fact]
    public async Task A_big_text_arrives_from_the_vm()
    {
        await using var s = await OpenAsync();
        Assert.Equal("x", await CopyInVmUntilArrivalAsync(s, "x"));

        var text = string.Join("\n", Enumerable.Range(0, 20000).Select(i => $"Zeile {i} mit etwas Text zum Fuellen äö"));
        await s.Probe.SetAsync(text);
        var got = await s.NextTextAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(text.Length, got.Length);
        Assert.Equal(text, got);
    }

    [Fact]
    public async Task Text_copied_in_the_vm_again_after_a_paste_from_the_browser_still_arrives()
    {
        // Der Browser legt "A" in die VM, in der VM wird "B" kopiert und danach wieder "A": auch das letzte "A" muss beim Browser ankommen
        await using var s = await OpenAsync();
        Assert.Equal("erst", await CopyInVmUntilArrivalAsync(s, "erst"));

        s.Stream.SetClipboard("A");
        await Task.Delay(300);
        Assert.Equal("B", await CopyInVmUntilArrivalAsync(s, "B"));
        await s.Probe.SetAsync("A");
        Assert.Equal("A", await s.NextTextAsync());
    }

    [Fact]
    public async Task Text_from_the_browser_is_pasted_in_the_vm()
    {
        await using var s = await OpenAsync();
        Assert.Equal("warm", await CopyInVmUntilArrivalAsync(s, "warm"));

        var text = "Aus dem Browser\nzweite Zeile äöü \U0001F600";
        s.Stream.SetClipboard(text);
        string? got = null;
        for (var i = 0; i < 20 && got != text; i++)
        {
            await Task.Delay(250);
            got = await s.Probe.GetAsync();
        }

        Assert.Equal(text, got);
    }

    [Fact]
    public async Task Text_from_the_browser_can_be_pasted_in_the_vm_more_than_once_and_changes_with_the_next_copy()
    {
        await using var s = await OpenAsync();
        Assert.Equal("warm", await CopyInVmUntilArrivalAsync(s, "warm"));

        s.Stream.SetClipboard("eins");
        await Task.Delay(400);
        Assert.Equal("eins", await s.Probe.GetAsync());
        Assert.Equal("eins", await s.Probe.GetAsync());
        s.Stream.SetClipboard("zwei");
        await Task.Delay(400);
        Assert.Equal("zwei", await s.Probe.GetAsync());
    }

    [Fact]
    public async Task A_copy_in_the_vm_whose_owner_never_answers_does_not_block_the_next_copies()
    {
        await using var s = await OpenAsync();
        Assert.Equal("warm", await CopyInVmUntilArrivalAsync(s, "warm"));

        await s.Probe.SetAsync("haengt", stall: true);
        await Task.Delay(500);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? got = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(40) && got != "danach")
        {
            await s.Probe.SetAsync("danach");
            try
            {
                got = await s.NextTextAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                // noch warten
            }
        }

        Assert.True(got == "danach", $"nach {watch.Elapsed.TotalSeconds:0} s kam der naechste Text nicht an");
    }

    [Fact]
    public async Task Copies_in_the_vm_in_quick_succession_end_with_the_last_one()
    {
        await using var s = await OpenAsync();
        Assert.Equal("warm", await CopyInVmUntilArrivalAsync(s, "warm"));

        var seen = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            await s.Probe.SetAsync($"eins {round}");
            await s.Probe.SetAsync($"zwei {round}");
            await s.Probe.SetAsync($"drei {round}");
            for (var i = 0; i < 20 && (seen.Count == 0 || seen[^1] != $"drei {round}"); i++)
            {
                try
                {
                    seen.Add(await s.NextTextAsync(TimeSpan.FromSeconds(3)));
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            Assert.True(seen.Count > 0 && seen[^1] == $"drei {round}", "gesehen: " + string.Join(" | ", seen));
        }
    }

    [Fact]
    public async Task What_the_browser_pastes_is_in_the_vm_clipboard_quickly()
    {
        // Das Einfuegen im Browser sendet erst den Text und nach 180 ms die Taste: bis dahin muss die VM den Text haben
        await using var s = await OpenAsync();
        Assert.Equal("warm", await CopyInVmUntilArrivalAsync(s, "warm"));

        for (var round = 0; round < 5; round++)
        {
            var text = "schnell " + round;
            s.Stream.SetClipboard(text);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string? got = null;
            while (watch.ElapsedMilliseconds < 3000 && got != text)
            {
                got = await s.Probe.GetAsync();
            }

            Assert.Equal(text, got);
            Assert.True(watch.ElapsedMilliseconds < 1500, $"Runde {round}: {watch.ElapsedMilliseconds} ms bis der Text in der VM lag");
        }
    }

    [Fact]
    public async Task Both_directions_work_one_after_the_other_for_many_rounds()
    {
        await using var s = await OpenAsync();
        Assert.Equal("warm", await CopyInVmUntilArrivalAsync(s, "warm"));

        for (var round = 0; round < 12; round++)
        {
            var fromVm = $"VM {round} äö";
            await s.Probe.SetAsync(fromVm);
            Assert.Equal(fromVm, await s.NextTextAsync());

            var fromBrowser = $"Browser {round}\nZeile";
            s.Stream.SetClipboard(fromBrowser);
            string? got = null;
            for (var i = 0; i < 30 && got != fromBrowser; i++)
            {
                await Task.Delay(100);
                got = await s.Probe.GetAsync();
            }

            Assert.Equal(fromBrowser, got);
        }
    }
}
