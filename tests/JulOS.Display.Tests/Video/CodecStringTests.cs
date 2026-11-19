using JulOS.Integrations.Display.Video.Vaapi;
using Xunit;

namespace JulOS.Integrations.Display.Video.Tests;

// The codec string a browser's VideoDecoder is configured with, read from the parameter sets in the stream. stream.js (codecs.js) does the same
// reading in the browser; tests/frontend/codecs.test.mjs gives it these same bytes.
public sealed class CodecStringTests
{
    private static byte[] Nal(params int[] bytes) => [.. new byte[] { 0, 0, 0, 1 }, .. bytes.Select(b => (byte)b)];

    [Fact]
    public void An_h264_string_is_profile_constraints_and_level_of_the_sps()
    {
        var stream = Nal(0x67, 0x64, 0x00, 0x28, 0xAC, 0xD9, 0x40).Concat(Nal(0x68, 0xEE, 0x3C, 0x80)).Concat(Nal(0x65, 0x88)).ToArray();
        Assert.Equal("avc1.640028", CodecStrings.FromStream(stream, VideoCodec.H264));
        Assert.Equal("avc1.42c01f", CodecStrings.FromStream(Nal(0x67, 0x42, 0xC0, 0x1F, 0x00), VideoCodec.H264));
    }

    [Fact]
    public void A_three_byte_start_code_and_leading_units_do_not_matter()
    {
        byte[] stream = [0, 0, 1, 0x09, 0x10, 0, 0, 1, 0x67, 0x4D, 0x40, 0x1E, 0x00];
        Assert.Equal("avc1.4d401e", CodecStrings.FromStream(stream, VideoCodec.H264));
    }

    [Fact]
    public void No_sps_means_no_string()
    {
        Assert.Null(CodecStrings.FromStream(Nal(0x65, 0x88, 0x84), VideoCodec.H264));
        Assert.Null(CodecStrings.FromStream([], VideoCodec.Hevc));
        Assert.Null(CodecStrings.FromStream(Nal(0x42, 0x01, 0x01, 0x01), VideoCodec.Hevc));          // an SPS cut short
    }

    [Theory]
    [InlineData(1920, 1080, 30, "hvc1.1.6.L120.90")]
    [InlineData(1280, 720, 30, "hvc1.1.6.L120.90")]
    [InlineData(3840, 2160, 30, "hvc1.1.6.L150.90")]
    [InlineData(2560, 1440, 60, "hvc1.1.6.L150.90")]
    [InlineData(1920, 1080, 60, "hvc1.1.6.L123.90")]
    public void An_hevc_string_is_profile_compatibility_tier_level_and_constraints_of_the_sps(int width, int height, int fps, string expected)
    {
        var layout = new HevcLayout(width, height, HevcLayout.PickLevel(width, height, fps));
        var stream = layout.Vps(true).Data.Concat(layout.Sps(true).Data).Concat(layout.Pps(true).Data).ToArray();
        Assert.Equal(expected, CodecStrings.FromStream(stream, VideoCodec.Hevc));
    }

    [Fact]
    public void An_hevc_string_from_known_bytes()
    {
        // SPS of a Main profile stream at level 4.1 (general_level_idc 123), tier 0, progressive and frame-only constraint bits
        byte[] sps = [0, 0, 0, 1, 0x42, 0x01, 0x01, 0x01, 0x60, 0x00, 0x00, 0x00, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7B, 0xA0];
        Assert.Equal("hvc1.1.6.L123.90", CodecStrings.FromStream(sps, VideoCodec.Hevc));
        // main tier 'H', profile Main 10 (2), compatibility bit 2 only (reversed: 4), level 5.1, no constraint bits
        byte[] high = [0, 0, 0, 1, 0x42, 0x01, 0x01, 0x22, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x99, 0xA0];
        Assert.Equal("hvc1.2.4.H153", CodecStrings.FromStream(high, VideoCodec.Hevc));
    }

    [Fact]
    public void An_emulation_prevention_byte_inside_the_profile_part_is_skipped()
    {
        // The profile part of an SPS has runs of zero bytes (compatibility and constraint flags), so the real stream has escape bytes (00 00 03) there.
        var sps = new HevcLayout(1920, 1080, 120).Sps(true).Data;
        var escapes = 0;
        for (var i = 0; i + 2 < 24; i++)
        {
            escapes += sps[i] == 0 && sps[i + 1] == 0 && sps[i + 2] == 3 ? 1 : 0;
        }

        Assert.True(escapes >= 1, "the test needs an escape byte in the profile part");
        Assert.Equal("hvc1.1.6.L120.90", CodecStrings.FromStream(sps, VideoCodec.Hevc));
    }
}
