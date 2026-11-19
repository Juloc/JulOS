namespace JulOS.Integrations.Display.Video;

/// <summary>The codec of a stream. The numbers are the ones on the wire (bit of <see cref="VideoCodecs"/>, flag in a picture message).</summary>
public enum VideoCodec : byte
{
    H264 = 1,
    Hevc = 2,
}

/// <summary>A set of codecs: what a browser can decode, what an encoder can make.</summary>
[Flags]
public enum VideoCodecs : byte
{
    None = 0,
    H264 = 1,
    Hevc = 2,
}

public static class VideoCodecExtensions
{
    public static VideoCodecs ToSet(this VideoCodec codec) => codec == VideoCodec.Hevc ? VideoCodecs.Hevc : VideoCodecs.H264;

    public static bool Has(this VideoCodecs set, VideoCodec codec) => (set & codec.ToSet()) != 0;

    public static string Label(this VideoCodec codec) => codec == VideoCodec.Hevc ? "H.265" : "H.264";
}

// One encoder for one desktop size, used by one thread at a time. ScreenStream talks to this and nothing else; what is behind it is
// the software encoder (libopenh264, always there) or a hardware one (VAAPI). The contract is the one H264Encoder has always had:
// one access unit per picture in Annex B, with the parameter sets (SPS and PPS, for HEVC also VPS) in front of every key frame,
// pictures in the order they come, nothing held back. The memory returned is valid until the next call.
public interface IVideoEncoder : IDisposable
{
    VideoCodec Codec { get; }

    /// <summary>Which encoder this is, for logs and diagnostics ("OpenH264 2.4.1", "VAAPI Intel iHD driver ...").</summary>
    string Name { get; }

    /// <summary>True when the picture is coded by a GPU.</summary>
    bool IsHardware { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>The ceiling the stream is held under, in kilobits per second.</summary>
    int KilobitsPerSecond { get; }

    /// <summary>
    /// True when the viewer's picture is as sharp as this encoder makes it: encoding the same frame once more would change nothing worth
    /// sending. After a change or a key frame it is false until the same frame has been encoded again often enough.
    /// </summary>
    bool IsSharp { get; }

    /// <summary>
    /// How many milliseconds the caller should let pass before the next picture in which this many pixels are new (0: its own pace is fine).
    /// An encoder with its own rate control, which cannot take pictures back, answers 0.
    /// </summary>
    int SuggestedInterval(long changedPixels);

    /// <summary>
    /// Encodes the frame. The same frame again without a change in between is the caller asking for a sharper picture. Throws when the
    /// encoder cannot go on (a GPU that went away): the caller then switches to another encoder and asks it for a key frame.
    /// </summary>
    ReadOnlyMemory<byte> Encode(I420Frame frame, bool forceKey, out bool isKey);

    /// <summary>Changes the ceiling for the pictures that follow. False when this encoder cannot change it while it runs.</summary>
    bool TrySetBitrate(int kilobitsPerSecond);
}
