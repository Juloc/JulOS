namespace JulOS.Integrations.Display.Video;

/// <summary>
/// The codec string a browser's VideoDecoder wants ("avc1.640028", "hvc1.1.6.L123.90"), read from the parameter sets in the stream - the
/// same reading stream.js does in the browser (the tests give both the same bytes).
/// </summary>
public static class CodecStrings
{
    /// <summary>The string for the SPS found in these Annex B bytes, or null when there is none (or it is too short to read).</summary>
    public static string? FromStream(ReadOnlySpan<byte> annexB, VideoCodec codec)
    {
        for (var i = 0; i + 4 < annexB.Length && i < 256; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0 || annexB[i + 2] != 1)
            {
                continue;
            }

            var at = i + 3;
            if (codec == VideoCodec.H264)
            {
                if ((annexB[at] & 0x1F) == 7 && at + 3 < annexB.Length)
                {
                    return $"avc1.{annexB[at + 1]:x2}{annexB[at + 2]:x2}{annexB[at + 3]:x2}";
                }
            }
            else if (((annexB[at] >> 1) & 0x3F) == 33)
            {
                return HevcFromSps(annexB[(at + 2)..]);
            }
        }

        return null;
    }

    // sps_video_parameter_set_id(4) sps_max_sub_layers_minus1(3) sps_temporal_id_nesting_flag(1), then profile_tier_level: space(2) tier(1)
    // profile(5), 32 compatibility flags, 6 bytes of constraint flags, level: 13 bytes after the NAL header, with emulation prevention removed.
    private static string? HevcFromSps(ReadOnlySpan<byte> escaped)
    {
        Span<byte> rbsp = stackalloc byte[13];
        var n = 0;
        var zeros = 0;
        foreach (var b in escaped)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            rbsp[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
            if (n == rbsp.Length)
            {
                break;
            }
        }

        if (n < 13)
        {
            return null;
        }

        var space = rbsp[1] >> 6;
        var tier = (rbsp[1] >> 5) & 1;
        var profile = rbsp[1] & 0x1F;
        uint compatibility = (uint)(rbsp[2] << 24 | rbsp[3] << 16 | rbsp[4] << 8 | rbsp[5]);
        uint reversed = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            reversed |= ((compatibility >> bit) & 1) << (31 - bit);
        }

        var level = rbsp[12];
        var constraints = rbsp.Slice(6, 6);
        var last = 5;
        while (last >= 0 && constraints[last] == 0)
        {
            last--;
        }

        var text = $"hvc1.{(space == 0 ? "" : ((char)('A' + space - 1)).ToString())}{profile}.{reversed:X}.{(tier == 1 ? 'H' : 'L')}{level}";
        for (var i = 0; i <= last; i++)
        {
            text += $".{constraints[i]:X}";
        }

        return text;
    }
}
