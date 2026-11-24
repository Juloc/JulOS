using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Sitzungserhalt: wird eine Sitzung geparkt (kein Browser mehr dran), darf am Ziel keine Taste und keine Maustaste gedrueckt bleiben (docs/SESSION-KEEP.md). Ohne echten Desktop.</summary>
public sealed class KeptInputTests
{
    private sealed class Recorder : IScreenSource
    {
        public readonly List<string> Calls = [];
        public int Width => 1;
        public int Height => 1;
        public byte[] Framebuffer => [];
        public object FrameLock { get; } = new();
        public string? EndReason => null;
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Disconnect() { }
        public void MouseMove(int x, int y) => Calls.Add($"move {x},{y}");
        public void MouseButton(int button, bool down) => Calls.Add($"button {button} {(down ? "down" : "up")}");
        public void MouseWheel(int delta, bool horizontal) => Calls.Add($"wheel {delta}");
        public void Key(int scancode, bool extended, bool down) => Calls.Add($"key {scancode:X}{(extended ? "e" : "")} {(down ? "down" : "up")}");
        public void Unicode(char unit) => Calls.Add($"unicode {unit}");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (ScreenStream Stream, Recorder Source) Make()
    {
        var stream = new ScreenStream(new RdpOptions { Host = "test" });
        var source = new Recorder();
        stream.UseSourceForTests(source);
        return (stream, source);
    }

    [Fact]
    public void Parking_releases_every_key_and_mouse_button_that_is_still_down_and_nothing_else()
    {
        var (stream, source) = Make();
        stream.Key(0x1D, false, true);          // Strg gedrueckt ...
        stream.Key(0x4B, true, true);           // Pfeil links (erweitert) gedrueckt
        stream.Key(0x1D, false, false);         // ... Strg wieder los: das braucht kein zweites Loslassen
        stream.MouseButton(0, true);
        stream.MouseButton(2, true);
        stream.MouseButton(2, false);
        source.Calls.Clear();

        stream.Park();

        Assert.Contains("key 4Be up", source.Calls);
        Assert.Contains("button 0 up", source.Calls);
        Assert.DoesNotContain("key 1D up", source.Calls);
        Assert.DoesNotContain("button 2 up", source.Calls);
        Assert.Equal(2, source.Calls.Count);

        source.Calls.Clear();
        stream.Park();                           // zweimal parken loest nichts doppelt
        Assert.Empty(source.Calls);
    }

    [Fact]
    public void A_key_pressed_again_after_the_release_is_tracked_again()
    {
        var (stream, source) = Make();
        stream.Key(0x2A, false, true);
        stream.ReleaseInput();
        stream.Key(0x2A, false, true);
        source.Calls.Clear();
        stream.ReleaseInput();
        Assert.Equal(["key 2A up"], source.Calls);
    }

    [Fact]
    public void The_session_message_carries_the_public_handle_after_the_token_and_stays_readable_without_it()
    {
        var with = ScreenProtocol.Session("tok", 300, "hand");
        Assert.Equal("tok hand", System.Text.Encoding.ASCII.GetString(with, 3, with.Length - 3));
        var without = ScreenProtocol.Session("tok", 300);
        Assert.Equal("tok", System.Text.Encoding.ASCII.GetString(without, 3, without.Length - 3));
    }
}
