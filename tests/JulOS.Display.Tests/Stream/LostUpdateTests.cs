using System.Reflection;
using JulOS.Integrations.Display;
using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Video;
using Xunit;

namespace JulOS.Integrations.Display.Tests;

// An update reported while the encoder converts must not be lost (bug of 0.31.0 and before: doubled, shifted lines while
// scrolling). A source draws into its frame buffer and reports Updated() exactly like the RDP and VNC readers do (draw under
// FrameLock, release, then Updated) while the real encode loop runs. Afterwards, with nothing pending, the encoder's picture
// must equal the whole frame buffer. A timing test: it never fails on correct code, and caught the bug in 5 of 9 runs.
public sealed class LostUpdateTests
{
    private const int W = 640, H = 480;

    private sealed class Source : IScreenSource
    {
        public int Width => W;
        public int Height => H;
        public byte[] Framebuffer { get; } = new byte[W * H * 4];
        public object FrameLock { get; } = new();
        public string? EndReason => null;
        public Task ConnectAsync(CancellationToken c) => Task.CompletedTask;
        public Task RunAsync(CancellationToken c) => Task.CompletedTask;
        public void Disconnect() { }
        public void MouseMove(int x, int y) { }
        public void MouseButton(int b, bool d) { }
        public void MouseWheel(int d, bool h) { }
        public void Key(int s, bool e, bool d) { }
        public void Unicode(char u) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Backend : IEncoderBackend
    {
        public string Name => "Fake";
        public bool IsHardware => true;
        public VideoCodecs Codecs => VideoCodecs.H264;
        public bool CheckAvailable(out string? reason) { reason = null; return true; }
        public IVideoEncoder Create(EncoderRequest r, VideoCodec c) => new Enc(r);
    }

    private sealed class Enc(EncoderRequest r) : IVideoEncoder
    {
        public VideoCodec Codec => VideoCodec.H264;
        public string Name => "Fake";
        public bool IsHardware => true;
        public int Width => r.Width;
        public int Height => r.Height;
        public int KilobitsPerSecond => r.KilobitsPerSecond;
        public bool IsSharp => true;
        public int SuggestedInterval(long p) => 0;
        public ReadOnlyMemory<byte> Encode(I420Frame f, bool k, out bool isKey) { isKey = k; return new byte[] { 0, 0, 0, 1, 0x65, 1 }; }
        public bool TrySetBitrate(int k) => true;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Encoder_picture_matches_framebuffer_after_concurrent_updates(int run)
    {
        var selector = new EncoderSelector([new Backend()], new EncoderSettings(EncoderMode.Auto));
        await using var stream = new ScreenStream(new RdpOptions { Host = "test" }, 16_000, null, selector);
        var source = new Source();
        stream.UseSourceForTests(source);
        IScreenHandler handler = stream;
        handler.Resized(W, H);

        using var cts = new CancellationTokenSource();
        var loop = (Task)typeof(ScreenStream).GetMethod("EncodeLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(stream, [cts.Token])!;
        var pictures = 0;
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in stream.Events.ReadAllAsync(cts.Token))
                {
                    if (e is ScreenPicture p) { pictures++; stream.Acknowledge(p.Sequence); }
                }
            }
            catch (OperationCanceledException) { }
        });

        // The "RDP thread": random rectangles, each a new colour, drawn under FrameLock, reported after releasing it.
        var random = new Random(run);
        var updates = 0;
        var until = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < until)
        {
            var x = random.Next(0, W - 8); var y = random.Next(0, H - 8);
            var w = random.Next(8, W - x + 1); var h = random.Next(8, Math.Min(H - y, 200) + 1);
            var color = (uint)random.Next() | 0xFF000000u;
            lock (source.FrameLock)
            {
                var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.Framebuffer.AsSpan());
                for (var row = y; row < y + h; row++) px.Slice(row * W + x, w).Fill(color);
            }

            handler.Updated(x, y, w, h);
            updates++;
            if (random.Next(4) == 0) Thread.Sleep(1);
        }

        await Task.Delay(1000);       // let the encoder catch up on everything still marked dirty
        var dirty = (bool)typeof(ScreenStream).GetField("_dirty", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(stream)!;
        var frame = (I420Frame)typeof(ScreenStream).GetField("_frame", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(stream)!;
        using var truth = new I420Frame(W, H);
        int differentY;
        lock (source.FrameLock)
        {
            ColorConverter.BgrxToI420(source.Framebuffer, W * 4, truth, 0, 0, W, H);
            differentY = 0;
            var a = frame.Y; var b = truth.Y;
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) differentY++;
        }

        await cts.CancelAsync();
        try { await loop; } catch (OperationCanceledException) { }
        await reader;
        TestContext.Current.SendDiagnosticMessage($"run {run}: updates {updates}, pictures {pictures}, still dirty {dirty}, stale luma samples {differentY} of {W * H}");
        Assert.False(dirty);
        Assert.Equal(0, differentY);
    }
}
