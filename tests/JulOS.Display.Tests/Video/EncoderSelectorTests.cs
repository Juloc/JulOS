using Xunit;

namespace JulOS.Integrations.Display.Video.Tests;

// Which encoder a session gets: with fake backends, so it runs everywhere. What matters is that the GPU is used when it works, that the
// software H.264 encoder takes over whenever it does not, and that HEVC only ever goes to a browser that can decode it.
public sealed class EncoderSelectorTests
{
    private sealed class FakeEncoder(VideoCodec codec, bool hardware, string name, EncoderRequest request) : IVideoEncoder
    {
        public VideoCodec Codec => codec;

        public string Name => name;

        public bool IsHardware => hardware;

        public int Width => request.Width;

        public int Height => request.Height;

        public int KilobitsPerSecond => request.KilobitsPerSecond;

        public bool IsSharp => true;

        public int SuggestedInterval(long changedPixels) => 0;

        public ReadOnlyMemory<byte> Encode(I420Frame frame, bool forceKey, out bool isKey)
        {
            isKey = true;
            return new byte[] { 0, 0, 0, 1 };
        }

        public bool TrySetBitrate(int kilobitsPerSecond) => false;

        public void Dispose()
        {
        }
    }

    private sealed class FakeBackend(string name, bool hardware, VideoCodecs codecs, string? unavailable = null, VideoCodec[]? failFor = null) : IEncoderBackend
    {
        public int Created { get; private set; }

        public int Checked { get; private set; }

        public string Name => name;

        public bool IsHardware => hardware;

        public VideoCodecs Codecs => codecs;

        public bool CheckAvailable(out string? reason)
        {
            Checked++;
            reason = unavailable;
            return unavailable is null;
        }

        public IVideoEncoder Create(EncoderRequest request, VideoCodec codec)
        {
            Created++;
            if (failFor?.Contains(codec) == true)
            {
                throw new InvalidOperationException($"{name} cannot do {codec}");
            }

            return new FakeEncoder(codec, hardware, name, request);
        }
    }

    private static EncoderRequest Request(VideoCodecs client) => new(1920, 1080, 30, 16_000, client);

    private const VideoCodecs Both = VideoCodecs.H264 | VideoCodecs.Hevc;

    [Fact]
    public void The_gpu_is_used_when_it_works()
    {
        var gpu = new FakeBackend("gpu", true, Both);
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var choice = new EncoderSelector([cpu, gpu]).Choose(Request(VideoCodecs.H264));
        Assert.Same(gpu, choice.Backend);
        Assert.Equal(VideoCodec.H264, choice.Encoder.Codec);
        Assert.Equal(0, cpu.Created);
    }

    [Fact]
    public void Software_takes_over_when_the_gpu_is_not_there()
    {
        var gpu = new FakeBackend("gpu", true, Both, unavailable: "no render node");
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var choice = new EncoderSelector([gpu, cpu]).Choose(Request(Both));
        Assert.Same(cpu, choice.Backend);
        Assert.Equal(VideoCodec.H264, choice.Encoder.Codec);
        Assert.Contains(choice.Skipped, s => s.Contains("no render node"));
    }

    [Fact]
    public void Software_takes_over_when_the_gpu_throws_and_it_is_not_asked_again_in_auto_mode()
    {
        var gpu = new FakeBackend("gpu", true, VideoCodecs.H264, failFor: [VideoCodec.H264]);
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var selector = new EncoderSelector([gpu, cpu]);
        Assert.Same(cpu, selector.Choose(Request(VideoCodecs.H264)).Backend);
        Assert.Equal(1, gpu.Created);
        Assert.Same(cpu, selector.Choose(Request(VideoCodecs.H264)).Backend);
        Assert.Equal(1, gpu.Created);                       // not tried a second time
    }

    [Fact]
    public void In_vaapi_mode_the_gpu_is_tried_for_every_session()
    {
        var gpu = new FakeBackend("gpu", true, VideoCodecs.H264, failFor: [VideoCodec.H264]);
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var selector = new EncoderSelector([gpu, cpu], new EncoderSettings(EncoderMode.Vaapi));
        selector.Choose(Request(VideoCodecs.H264));
        var again = selector.Choose(Request(VideoCodecs.H264));
        Assert.Equal(2, gpu.Created);
        Assert.Same(cpu, again.Backend);                    // a session does not fail because of the GPU, even when it was asked for
    }

    [Fact]
    public void Software_mode_never_touches_the_gpu()
    {
        var gpu = new FakeBackend("gpu", true, Both);
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var choice = new EncoderSelector([gpu, cpu], new EncoderSettings(EncoderMode.Software)).Choose(Request(Both));
        Assert.Same(cpu, choice.Backend);
        Assert.Equal(0, gpu.Checked);
        Assert.Equal(VideoCodec.H264, choice.Encoder.Codec);
    }

    [Fact]
    public void Hevc_goes_only_to_a_browser_that_can_decode_it()
    {
        var gpu = new FakeBackend("gpu", true, Both);
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var selector = new EncoderSelector([gpu, cpu]);
        Assert.Equal(VideoCodec.Hevc, selector.Choose(Request(Both)).Encoder.Codec);
        Assert.Equal(VideoCodec.H264, selector.Choose(Request(VideoCodecs.H264)).Encoder.Codec);
        Assert.Equal(VideoCodec.H264, selector.Choose(Request(VideoCodecs.None)).Encoder.Codec);       // says nothing: H.264
    }

    [Fact]
    public void Hevc_is_not_offered_when_only_the_cpu_can_encode()
    {
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        Assert.Equal(VideoCodec.H264, new EncoderSelector([cpu]).Choose(Request(Both)).Encoder.Codec);
    }

    [Fact]
    public void A_failed_hevc_falls_back_to_h264_on_the_gpu_before_the_cpu()
    {
        var gpu = new FakeBackend("gpu", true, Both, failFor: [VideoCodec.Hevc]);
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var choice = new EncoderSelector([gpu, cpu], new EncoderSettings(EncoderMode.Vaapi)).Choose(Request(Both));
        Assert.Same(gpu, choice.Backend);
        Assert.Equal(VideoCodec.H264, choice.Encoder.Codec);
    }

    [Fact]
    public void The_codec_preference_h264_keeps_hevc_away()
    {
        var gpu = new FakeBackend("gpu", true, Both);
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var selector = new EncoderSelector([gpu, cpu], new EncoderSettings(EncoderMode.Auto, CodecPreference.H264));
        Assert.Equal(VideoCodec.H264, selector.Choose(Request(Both)).Encoder.Codec);
        Assert.Equal([VideoCodec.H264], selector.CodecOrder(Both));
    }

    [Fact]
    public void After_a_failure_in_the_session_only_software_is_chosen()
    {
        var gpu = new FakeBackend("gpu", true, Both);
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264);
        var selector = new EncoderSelector([gpu, cpu], new EncoderSettings(EncoderMode.Auto));
        var choice = selector.Choose(Request(Both));
        selector.ReportFailure(choice.Backend, new InvalidOperationException("device lost"));
        var next = selector.Choose(Request(Both), noHardware: true);
        Assert.Same(cpu, next.Backend);
        Assert.Equal(VideoCodec.H264, next.Encoder.Codec);
        // and the next session leaves the GPU alone too (auto mode)
        Assert.Same(cpu, selector.Choose(Request(Both)).Backend);
    }

    [Fact]
    public void Nothing_to_choose_from_is_an_error_that_says_why()
    {
        var cpu = new FakeBackend("cpu", false, VideoCodecs.H264, unavailable: "libopenh264 is missing");
        var error = Assert.Throws<InvalidOperationException>(() => new EncoderSelector([cpu]).Choose(Request(VideoCodecs.H264)));
        Assert.Contains("libopenh264 is missing", error.Message);
    }

    [Theory]
    [InlineData(null, null, EncoderMode.Auto, CodecPreference.Auto)]
    [InlineData("auto", "auto", EncoderMode.Auto, CodecPreference.Auto)]
    [InlineData("software", "h264", EncoderMode.Software, CodecPreference.H264)]
    [InlineData("VAAPI", "H265", EncoderMode.Vaapi, CodecPreference.Hevc)]
    [InlineData(" vaapi ", "hevc", EncoderMode.Vaapi, CodecPreference.Hevc)]
    public void The_settings_are_read_from_the_environment_values(string? encoder, string? codec, EncoderMode mode, CodecPreference preference)
    {
        var settings = EncoderSettings.Parse(encoder, codec);
        Assert.Equal(mode, settings.Mode);
        Assert.Equal(preference, settings.Codec);
    }

    [Fact]
    public void An_unknown_setting_is_a_warning_and_means_auto()
    {
        var warnings = new List<string>();
        var settings = EncoderSettings.Parse("nvenc", "av1", warnings.Add);
        Assert.Equal(new EncoderSettings(), settings);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("JULOS_ENCODER", warnings[0]);
    }

    [Fact]
    public void The_software_encoder_still_works_through_the_interface()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 fehlt (deploy/dev/test-display.sh benutzen)");
        using var encoder = new SoftwareBackend().Create(new EncoderRequest(640, 360, 30, 4000, VideoCodecs.H264), VideoCodec.H264);
        Assert.False(encoder.IsHardware);
        Assert.Equal(VideoCodec.H264, encoder.Codec);
        using var frame = new I420Frame(640, 360);
        frame.MarkChanged();
        var data = encoder.Encode(frame, false, out var key);
        Assert.True(key);
        Assert.Equal(7, data.Span[4] & 0x1F);
        Assert.StartsWith("avc1.", CodecStrings.FromStream(data.Span, VideoCodec.H264));
        Assert.False(encoder.TrySetBitrate(1000));
    }
}
