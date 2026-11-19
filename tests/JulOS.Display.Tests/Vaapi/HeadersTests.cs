using JulOS.Integrations.Display.Video.Vaapi;
using Xunit;

namespace JulOS.Integrations.Display.Video.Tests;

// The headers the GPU encoder writes itself: the bit writer, and the parameter sets and slice headers read back by a small reader that
// follows the syntax of the standards (so a mistake in the order of fields shows as a wrong value further down). ffmpeg's view of the same
// bytes is in the notes of docs/ENCODERS.md.
public sealed class HeadersTests
{
    private sealed class BitReader(byte[] data)
    {
        private int _at;

        public int Position => _at;

        public bool Bit()
        {
            var value = (data[_at >> 3] >> (7 - (_at & 7))) & 1;
            _at++;
            return value != 0;
        }

        public uint Bits(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | (Bit() ? 1u : 0u);
            }

            return value;
        }

        public uint Ue()
        {
            var zeros = 0;
            while (!Bit())
            {
                zeros++;
            }

            return (1u << zeros) - 1 + (zeros == 0 ? 0 : Bits(zeros));
        }

        public int Se()
        {
            var k = Ue();
            return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
        }
    }

    // Takes the emulation prevention bytes out again
    private static byte[] Rbsp(byte[] nal, int headerBytes)
    {
        var result = new List<byte>();
        var zeros = 0;
        foreach (var b in nal.Skip(4 + headerBytes))
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            result.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. result];
    }

    [Fact]
    public void Exp_golomb_codes_are_the_ones_of_the_standard()
    {
        // ue: 0 -> 1, 1 -> 010, 2 -> 011, 3 -> 00100, 7 -> 0001000; se: 1 -> 010, -1 -> 011, 2 -> 00100
        var w = new BitWriter();
        foreach (var v in new uint[] { 0, 1, 2, 3, 7 })
        {
            w.Ue(v);
        }

        w.Se(1);
        w.Se(-1);
        w.Se(2);
        w.Trailing();
        var bits = string.Concat(w.ToArray().Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        Assert.StartsWith("1" + "010" + "011" + "00100" + "0001000" + "010" + "011" + "00100" + "1", bits);
        var r = new BitReader(w.ToArray());
        Assert.Equal<uint>([0, 1, 2, 3, 7], [r.Ue(), r.Ue(), r.Ue(), r.Ue(), r.Ue()]);
        Assert.Equal([1, -1, 2], [r.Se(), r.Se(), r.Se()]);
    }

    [Fact]
    public void Large_exp_golomb_values_round_trip()
    {
        var w = new BitWriter();
        uint[] values = [0, 255, 256, 65535, 1_000_000, 0xFFFFFFFE];
        foreach (var v in values)
        {
            w.Ue(v);
        }

        w.Trailing();
        var r = new BitReader(w.ToArray());
        foreach (var v in values)
        {
            Assert.Equal(v, r.Ue());
        }
    }

    [Fact]
    public void A_nal_unit_gets_a_start_code_and_emulation_prevention_only_when_asked()
    {
        var w = new BitWriter();
        w.Bits(0, 16);
        w.Bits(1, 8);
        w.Bits(0x00, 8);
        w.Bits(0x02, 8);
        var (raw, rawBits) = w.ToNal([0x67], false);
        Assert.Equal([0, 0, 0, 1, 0x67, 0, 0, 1, 0, 2], raw);
        Assert.Equal(raw.Length * 8, rawBits);
        var (escaped, escapedBits) = w.ToNal([0x67], true);
        Assert.Equal([0, 0, 0, 1, 0x67, 0, 0, 3, 1, 0, 2], escaped);          // no escape before the 0x02: only one zero precedes it
        Assert.Equal(escaped.Length * 8, escapedBits);
    }

    [Theory]
    [InlineData(1920, 1080, LibVa.ProfileH264High, 100)]
    [InlineData(1280, 720, LibVa.ProfileH264Main, 77)]
    [InlineData(1024, 768, LibVa.ProfileH264ConstrainedBaseline, 66)]
    [InlineData(1366, 768, LibVa.ProfileH264High, 100)]
    public void An_h264_sps_says_the_size_the_crop_and_the_profile(int width, int height, int profile, int profileIdc)
    {
        var layout = new H264Layout(width, height, profile, H264Layout.PickLevel(width, height, 30));
        var (nal, bits) = layout.Sps(true);
        Assert.Equal(nal.Length * 8, bits);
        Assert.Equal([0, 0, 0, 1, 0x67], nal[..5]);
        var r = new BitReader(Rbsp(nal, 1));
        Assert.Equal((uint)profileIdc, r.Bits(8));
        Assert.Equal(profile == LibVa.ProfileH264ConstrainedBaseline ? 0x40u : 0u, r.Bits(8));
        Assert.Equal((uint)layout.Level, r.Bits(8));
        Assert.Equal(0u, r.Ue());                                        // sps id
        if (layout.High)
        {
            Assert.Equal(1u, r.Ue());                                    // 4:2:0
            Assert.Equal(0u, r.Ue());
            Assert.Equal(0u, r.Ue());
            Assert.False(r.Bit());
            Assert.False(r.Bit());
        }

        Assert.Equal(4u, r.Ue());                                        // log2_max_frame_num_minus4
        Assert.Equal(0u, r.Ue());                                        // poc type 0
        Assert.Equal(4u, r.Ue());                                        // log2_max_poc_lsb_minus4
        Assert.Equal(1u, r.Ue());                                        // one reference
        Assert.False(r.Bit());
        Assert.Equal((uint)(layout.MbWidth - 1), r.Ue());
        Assert.Equal((uint)(layout.MbHeight - 1), r.Ue());
        Assert.True(r.Bit());                                            // frame_mbs_only
        Assert.True(r.Bit());                                            // direct_8x8
        var cropped = r.Bit();
        Assert.Equal(width != layout.AlignedWidth || height != layout.AlignedHeight, cropped);
        if (cropped)
        {
            Assert.Equal(0u, r.Ue());
            Assert.Equal((uint)((layout.AlignedWidth - width) / 2), r.Ue());
            Assert.Equal(0u, r.Ue());
            Assert.Equal((uint)((layout.AlignedHeight - height) / 2), r.Ue());
        }

        Assert.True(r.Bit());                                            // VUI
        Assert.False(r.Bit());
        Assert.False(r.Bit());
        Assert.True(r.Bit());                                            // video signal type
        Assert.Equal(5u, r.Bits(3));
        Assert.False(r.Bit());
        Assert.True(r.Bit());
        Assert.Equal<uint>([1, 13, 6], [r.Bits(8), r.Bits(8), r.Bits(8)]);   // the colours of the software path
        Assert.False(r.Bit());                                           // chroma loc
        Assert.False(r.Bit());                                           // timing
        Assert.False(r.Bit());
        Assert.False(r.Bit());
        Assert.False(r.Bit());                                           // pic_struct
        Assert.True(r.Bit());                                            // bitstream restriction
        Assert.True(r.Bit());
        Assert.Equal<uint>([0, 0, 15, 15, 0, 1], [r.Ue(), r.Ue(), r.Ue(), r.Ue(), r.Ue(), r.Ue()]);   // nothing is reordered
        Assert.True(r.Bit());                                            // the stop bit follows
    }

    [Fact]
    public void An_h264_pps_matches_the_profile()
    {
        foreach (var (profile, cabac, t8) in new[] { (LibVa.ProfileH264High, true, true), (LibVa.ProfileH264Main, true, false), (LibVa.ProfileH264ConstrainedBaseline, false, false) })
        {
            var layout = new H264Layout(1920, 1080, profile, 40);
            var r = new BitReader(Rbsp(layout.Pps(true).Data, 1));
            Assert.Equal<uint>([0, 0], [r.Ue(), r.Ue()]);
            Assert.Equal(cabac, r.Bit());
            Assert.False(r.Bit());
            Assert.Equal<uint>([0, 0, 0], [r.Ue(), r.Ue(), r.Ue()]);
            Assert.False(r.Bit());
            Assert.Equal(0u, r.Bits(2));
            Assert.Equal<int>([0, 0, 0], [r.Se(), r.Se(), r.Se()]);          // init qp 26, qs, chroma offset
            Assert.True(r.Bit());                                            // deblocking control present: the slice header has the idc
            Assert.False(r.Bit());
            Assert.False(r.Bit());
            if (profile == LibVa.ProfileH264High)
            {
                Assert.Equal(t8, r.Bit());
                Assert.False(r.Bit());
                Assert.Equal(0, r.Se());
            }

            Assert.True(r.Bit());                                            // the stop bit
        }
    }

    [Theory]
    [InlineData(true, LibVa.ProfileH264High)]
    [InlineData(false, LibVa.ProfileH264High)]
    [InlineData(true, LibVa.ProfileH264ConstrainedBaseline)]
    [InlineData(false, LibVa.ProfileH264ConstrainedBaseline)]
    public void An_h264_slice_header_has_the_fields_in_order_and_cabac_pads_to_a_byte(bool idr, int profile)
    {
        var layout = new H264Layout(1920, 1080, profile, 40);
        var (nal, bits) = layout.SliceHeader(idr, 5, 3, 10);
        Assert.Equal(idr ? (byte)0x65 : (byte)0x61, nal[4]);
        var headerBits = bits - 40;                                          // minus start code and NAL header
        if (layout.Cabac)
        {
            Assert.Equal(0, headerBits % 8);                                 // slice data starts on a byte boundary
        }

        var r = new BitReader(nal[5..]);
        Assert.Equal(0u, r.Ue());                                            // first_mb_in_slice
        Assert.Equal(idr ? 2u : 0u, r.Ue());
        Assert.Equal(0u, r.Ue());
        Assert.Equal(5u, r.Bits(8));                                         // frame_num
        if (idr)
        {
            Assert.Equal(3u, r.Ue());                                        // idr_pic_id
        }

        Assert.Equal(10u, r.Bits(8));                                        // pic_order_cnt_lsb
        if (!idr)
        {
            Assert.False(r.Bit());
            Assert.False(r.Bit());
        }

        if (idr)
        {
            Assert.False(r.Bit());
            Assert.False(r.Bit());
        }
        else
        {
            Assert.False(r.Bit());
        }

        if (layout.Cabac && !idr)
        {
            Assert.Equal(0u, r.Ue());                                        // cabac_init_idc
        }

        Assert.Equal(0, r.Se());                                             // slice_qp_delta
        Assert.Equal(0u, r.Ue());                                            // deblocking on
        Assert.Equal(0, r.Se());
        Assert.Equal(0, r.Se());
        Assert.True(r.Position <= headerBits);
        Assert.True(headerBits - r.Position < 8, "only the cabac alignment follows");
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    public void An_hevc_sps_says_the_size_the_crop_and_the_block_sizes(int width, int height)
    {
        var layout = new HevcLayout(width, height, HevcLayout.PickLevel(width, height, 30));
        var (nal, bits) = layout.Sps(true);
        Assert.Equal(nal.Length * 8, bits);
        Assert.Equal([0, 0, 0, 1, 0x42, 0x01], nal[..6]);
        var r = new BitReader(Rbsp(nal, 2));
        Assert.Equal(0u, r.Bits(4));
        Assert.Equal(0u, r.Bits(3));
        Assert.True(r.Bit());
        Assert.Equal<uint>([0, 0, 1], [r.Bits(2), r.Bits(1), r.Bits(5)]);    // space, tier, Main
        Assert.Equal(0x60000000u, r.Bits(32));
        Assert.Equal<bool>([true, false, false, true], [r.Bit(), r.Bit(), r.Bit(), r.Bit()]);
        Assert.Equal(0u, r.Bits(32));
        Assert.Equal(0u, r.Bits(11));
        Assert.False(r.Bit());
        Assert.Equal((uint)layout.Level, r.Bits(8));
        Assert.Equal(0u, r.Ue());
        Assert.Equal(1u, r.Ue());
        Assert.Equal((uint)layout.AlignedWidth, r.Ue());
        Assert.Equal((uint)layout.AlignedHeight, r.Ue());
        var cropped = r.Bit();
        Assert.Equal(width != layout.AlignedWidth || height != layout.AlignedHeight, cropped);
        if (cropped)
        {
            Assert.Equal(0u, r.Ue());
            Assert.Equal((uint)((layout.AlignedWidth - width) / 2), r.Ue());
            Assert.Equal(0u, r.Ue());
            Assert.Equal((uint)((layout.AlignedHeight - height) / 2), r.Ue());
        }

        Assert.Equal<uint>([0, 0, 4], [r.Ue(), r.Ue(), r.Ue()]);             // bit depths, poc bits
        Assert.True(r.Bit());
        Assert.Equal<uint>([1, 0, 0], [r.Ue(), r.Ue(), r.Ue()]);             // dpb size, reorder, latency
        Assert.Equal<uint>([0, 2, 0, 3, 2, 2], [r.Ue(), r.Ue(), r.Ue(), r.Ue(), r.Ue(), r.Ue()]);   // 8 .. 32 blocks, 4 .. 32 transforms, depth 2
        Assert.Equal<bool>([false, false, false, false], [r.Bit(), r.Bit(), r.Bit(), r.Bit()]);       // scaling list, amp, sao, pcm
        Assert.Equal<uint>([1, 1, 0, 0], [r.Ue(), r.Ue(), r.Ue(), r.Ue()]);   // one set: one picture before
        Assert.True(r.Bit());
        Assert.False(r.Bit());                                               // no long term
        Assert.False(r.Bit());                                               // no temporal mv prediction
        Assert.True(r.Bit());                                                // strong intra smoothing
        Assert.True(r.Bit());                                                // VUI
    }

    [Fact]
    public void The_hevc_headers_are_the_expected_nal_units_and_the_slice_header_ends_on_a_byte()
    {
        var layout = new HevcLayout(1920, 1080, 120);
        Assert.Equal([0, 0, 0, 1, 0x40, 0x01], layout.Vps(true).Data[..6]);
        Assert.Equal([0, 0, 0, 1, 0x44, 0x01], layout.Pps(true).Data[..6]);
        var (idr, idrBits) = layout.SliceHeader(true, 0);
        var (trail, trailBits) = layout.SliceHeader(false, 7);
        Assert.Equal([0, 0, 0, 1, 0x26, 0x01], idr[..6]);
        Assert.Equal([0, 0, 0, 1, 0x02, 0x01], trail[..6]);
        Assert.Equal(0, idrBits % 8);
        Assert.Equal(0, trailBits % 8);
        Assert.Equal(idr.Length * 8, idrBits);
        var r = new BitReader(trail[6..]);
        Assert.True(r.Bit());                                                // first slice segment
        Assert.Equal(0u, r.Ue());                                            // pps
        Assert.Equal(1u, r.Ue());                                            // P
        Assert.Equal(7u, r.Bits(8));                                         // poc lsb
        Assert.True(r.Bit());                                                // rps from the sps
        Assert.False(r.Bit());                                               // no override
        Assert.Equal(0u, r.Ue());                                            // five minus max merge candidates
        Assert.Equal(0, r.Se());
        Assert.True(r.Bit());                                                // loop filter across slices
        Assert.True(r.Bit());                                                // byte_alignment: the one
    }

    [Theory]
    [InlineData(1920, 1080, 40)]
    [InlineData(1280, 720, 40)]
    [InlineData(3840, 2160, 51)]
    [InlineData(7680, 4320, 52)]
    public void The_h264_level_fits_the_size_and_the_rate(int width, int height, int level)
    {
        Assert.Equal(level, H264Layout.PickLevel(width, height, 30));
    }

    [Fact]
    public void Parameter_sets_are_found_in_the_output_of_a_driver()
    {
        var h264 = new H264Layout(1920, 1080, LibVa.ProfileH264High, 40);
        byte[] slice = [0, 0, 0, 1, 0x65, 0x88, 0x84];
        Assert.True(VaapiEncoder.HasParameterSets([.. h264.Sps(true).Data, .. h264.Pps(true).Data, .. slice], VideoCodec.H264));
        Assert.False(VaapiEncoder.HasParameterSets(slice, VideoCodec.H264));
        var hevc = new HevcLayout(1920, 1080, 120);
        byte[] hevcSlice = [0, 0, 0, 1, 0x26, 0x01, 0xAF];
        Assert.True(VaapiEncoder.HasParameterSets([.. hevc.Vps(true).Data, .. hevc.Sps(true).Data, .. hevc.Pps(true).Data, .. hevcSlice], VideoCodec.Hevc));
        Assert.False(VaapiEncoder.HasParameterSets([.. hevc.Sps(true).Data, .. hevcSlice], VideoCodec.Hevc));      // a VPS is missing
    }

    // ffmpeg's parser as the independent reader: the headers go in with some made-up slice data behind them; its trace_headers filter names every
    // field it read, and complains ("out of range", "Invalid ue-golomb code") at the first one that is wrong. (It found a missing field in the HEVC
    // PPS that the reader above did not, because that one was written from the same idea.) Skipped where there is no ffmpeg.
    private static string Trace(string file, bool decoderComplaints)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-v", "trace", "-i", file, "-c", "copy", "-bsf:v", "trace_headers", "-f", "null", "-" })
        {
            start.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        // What the filter says about the headers; the decoder's own messages only for HEVC (the H.264 decoder complains about the made-up slice data).
        var lines = (errors + output.Result).Split('\n').Where(l => l.Contains("trace_headers", StringComparison.Ordinal) || (decoderComplaints && l.Contains("[hevc", StringComparison.Ordinal)));
        return string.Join('\n', lines);
    }

    [Fact]
    public void Ffmpeg_reads_the_headers_of_all_profiles_without_a_complaint()
    {
        Assert.SkipUnless(Tool.Works("ffmpeg", "-version"), "ffmpeg fehlt");
        var dir = Directory.CreateTempSubdirectory("julos-headers-");
        try
        {
            foreach (var (name, profile) in new[] { ("high", LibVa.ProfileH264High), ("main", LibVa.ProfileH264Main), ("baseline", LibVa.ProfileH264ConstrainedBaseline) })
            {
                var l = new H264Layout(1920, 1080, profile, 40);
                byte[] stream =
                [
                    .. l.Sps(true).Data, .. l.Pps(true).Data,
                    .. l.SliceHeader(true, 0, 0, 0).Data, 0x80, 0x11, 0x22, 0x33,
                    .. l.SliceHeader(false, 1, 0, 2).Data, 0x9A, 0x11, 0x22, 0x33,
                ];
                var file = Path.Combine(dir.FullName, name + ".h264");
                File.WriteAllBytes(file, stream);
                var trace = Trace(file, false);
                Assert.Matches(@"profile_idc\s+\d+ = " + l.ProfileIdc, trace);
                Assert.Matches(@"frame_crop_bottom_offset\s+\d+ = 4", trace);
                Assert.Matches(@"matrix_coefficients\s+\d+ = 6", trace);
                Assert.Matches(@"max_dec_frame_buffering\s+\d+ = 1", trace);
                Assert.Matches(@"slice_type\s+\d+ = 2", trace);
                Assert.DoesNotContain("out of range", trace);
                Assert.DoesNotContain("Invalid", trace);
            }

            var h = new HevcLayout(1920, 1080, 120);
            byte[] hevc =
            [
                .. h.Vps(true).Data, .. h.Sps(true).Data, .. h.Pps(true).Data,
                .. h.SliceHeader(true, 0).Data, 0x80, 0x11, 0x22, 0x33,
                .. h.SliceHeader(false, 1).Data, 0x9A, 0x11, 0x22, 0x33,
            ];
            var hevcFile = Path.Combine(dir.FullName, "main.h265");
            File.WriteAllBytes(hevcFile, hevc);
            var hevcTrace = Trace(hevcFile, true);
            Assert.Matches(@"general_level_idc\s+\d+ = 120", hevcTrace);
            Assert.Matches(@"conf_win_bottom_offset\s+\d+ = 4", hevcTrace);
            Assert.Matches(@"log2_parallel_merge_level_minus2\s+\d+ = 0", hevcTrace);
            Assert.Matches(@"slice_type\s+\d+ = 1", hevcTrace);
            Assert.DoesNotContain("out of range", hevcTrace);
            Assert.DoesNotContain("Invalid", hevcTrace);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
