using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display.Video;

/// <summary>JULOS_ENCODER: which encoders may be used.</summary>
public enum EncoderMode
{
    /// <summary>The GPU if it works, else the software encoder.</summary>
    Auto,

    /// <summary>Only the software encoder (H.264).</summary>
    Software,

    /// <summary>The GPU is wanted: it is tried for every session (a failed try is not remembered) and a failure is logged as an error. A session still never fails because of the GPU; the software encoder takes over.</summary>
    Vaapi,
}

/// <summary>JULOS_CODEC: which codec is preferred when the browser can decode more than one.</summary>
public enum CodecPreference
{
    /// <summary>HEVC if the browser and a hardware encoder both can, else H.264.</summary>
    Auto,

    H264,

    /// <summary>The same as Auto for the browser's side: HEVC whenever it works. H.264 stays the way out.</summary>
    Hevc,
}

public sealed record EncoderSettings(EncoderMode Mode = EncoderMode.Auto, CodecPreference Codec = CodecPreference.Auto)
{
    public static EncoderSettings Parse(string? encoder, string? codec, Action<string>? warn = null)
    {
        var mode = (encoder ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "auto" => EncoderMode.Auto,
            "software" or "sw" or "openh264" => EncoderMode.Software,
            "vaapi" or "hardware" or "gpu" => EncoderMode.Vaapi,
            var other => Unknown(other, "JULOS_ENCODER", EncoderMode.Auto, warn),
        };
        var preference = (codec ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "auto" => CodecPreference.Auto,
            "h264" or "avc" => CodecPreference.H264,
            "h265" or "hevc" => CodecPreference.Hevc,
            var other => Unknown(other, "JULOS_CODEC", CodecPreference.Auto, warn),
        };
        return new EncoderSettings(mode, preference);
    }

    private static T Unknown<T>(string value, string name, T fallback, Action<string>? warn)
    {
        warn?.Invoke($"{name}={value} is not known (auto, software, vaapi / auto, h264, h265); using {fallback}.");
        return fallback;
    }
}

/// <summary>What one picture encoder is asked for.</summary>
public sealed record EncoderRequest(int Width, int Height, int FramesPerSecond, int KilobitsPerSecond, VideoCodecs ClientCodecs);

/// <summary>A way to make encoders: the software one, or one GPU interface. Several can be registered; <see cref="EncoderSelector"/> picks.</summary>
public interface IEncoderBackend
{
    string Name { get; }

    bool IsHardware { get; }

    /// <summary>What it can encode (only meaningful when <see cref="CheckAvailable"/> is true).</summary>
    VideoCodecs Codecs { get; }

    /// <summary>Whether it can be used on this machine at all; the reason when not (for the log and diagnostics). Cheap after the first call.</summary>
    bool CheckAvailable(out string? reason);

    /// <summary>Makes an encoder, or throws. Called for a codec in <see cref="Codecs"/> only.</summary>
    IVideoEncoder Create(EncoderRequest request, VideoCodec codec);
}

/// <summary>The result of a choice: the encoder, and what was tried before it (for the log).</summary>
public sealed record EncoderChoice(IVideoEncoder Encoder, IEncoderBackend Backend, IReadOnlyList<string> Skipped);

/// <summary>
/// Picks the encoder for a desktop. Order: for every codec the browser may get (HEVC first when both sides can, then H.264), the hardware
/// backends, then the software one. Whatever throws or is not there is skipped and noted; the software H.264 encoder is the way out, so a
/// session does not fail because of a GPU. In <see cref="EncoderMode.Auto"/> a backend that failed is not tried again for the next sessions
/// (a GPU that does not work is not asked again for every session); a restart or <see cref="EncoderMode.Vaapi"/> tries again.
/// </summary>
public sealed class EncoderSelector
{
    private readonly IReadOnlyList<IEncoderBackend> _backends;
    private readonly HashSet<IEncoderBackend> _failed = [];
    private readonly Lock _gate = new();

    public EncoderSelector(IReadOnlyList<IEncoderBackend> backends, EncoderSettings? settings = null, ILogger? log = null)
    {
        _backends = backends;
        Settings = settings ?? new EncoderSettings();
        Log = log;
    }

    /// <summary>The software backend and the VAAPI backend, as the server uses them.</summary>
    public static EncoderSelector CreateDefault(EncoderSettings settings, ILogger? log = null) =>
        new([new Vaapi.VaapiBackend(), new SoftwareBackend()], settings, log);

    public EncoderSettings Settings { get; }

    public ILogger? Log { get; set; }

    public IReadOnlyList<IEncoderBackend> Backends => _backends;

    /// <summary>The codecs that could be offered to a browser which can decode <paramref name="client"/>, best first.</summary>
    public IReadOnlyList<VideoCodec> CodecOrder(VideoCodecs client)
    {
        var list = new List<VideoCodec>(2);
        if (Settings.Mode != EncoderMode.Software && Settings.Codec != CodecPreference.H264 && client.Has(VideoCodec.Hevc))
        {
            list.Add(VideoCodec.Hevc);
        }

        list.Add(VideoCodec.H264);
        return list;
    }

    /// <param name="client">What the browser can decode. H.264 is assumed when it says nothing (every browser that gets this far has it).</param>
    /// <param name="noHardware">True after a hardware encoder has failed in the middle of a session: only software from here on.</param>
    /// <exception cref="InvalidOperationException">Not even the software encoder can be made (libopenh264 missing).</exception>
    public EncoderChoice Choose(EncoderRequest request, bool noHardware = false)
    {
        var client = request.ClientCodecs == VideoCodecs.None ? VideoCodecs.H264 : request.ClientCodecs;
        var skipped = new List<string>();
        foreach (var codec in CodecOrder(client))
        {
            if (!client.Has(codec))
            {
                continue;
            }

            foreach (var backend in Ordered())
            {
                if (backend.IsHardware && (noHardware || Settings.Mode == EncoderMode.Software))
                {
                    continue;
                }

                if (backend.IsHardware && Settings.Mode == EncoderMode.Auto && IsFailed(backend))
                {
                    Note(skipped, $"{backend.Name}: ist schon einmal ausgefallen");
                    continue;
                }

                if (!backend.CheckAvailable(out var reason))
                {
                    Note(skipped, $"{backend.Name}: {reason}");
                    continue;
                }

                if (!backend.Codecs.Has(codec))
                {
                    continue;
                }

                try
                {
                    var encoder = backend.Create(request, codec);
                    return new EncoderChoice(encoder, backend, skipped);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Note(skipped, $"{backend.Name} ({codec.Label()}): {e.Message}");
                    if (backend.IsHardware)
                    {
                        MarkFailed(backend);
                        Report(e, backend);
                    }
                }
            }
        }

        throw new InvalidOperationException("No video encoder can be made: " + string.Join("; ", skipped));
    }

    /// <summary>An encoder failed while the session ran: in Auto mode its backend is left alone from now on.</summary>
    public void ReportFailure(IEncoderBackend backend, Exception error)
    {
        if (backend.IsHardware)
        {
            MarkFailed(backend);
            Report(error, backend);
        }
    }

    private static void Note(List<string> list, string text)
    {
        if (!list.Contains(text))
        {
            list.Add(text);
        }
    }

    private IEnumerable<IEncoderBackend> Ordered() => _backends.Where(b => b.IsHardware).Concat(_backends.Where(b => !b.IsHardware));

    private bool IsFailed(IEncoderBackend backend)
    {
        lock (_gate)
        {
            return _failed.Contains(backend);
        }
    }

    private void MarkFailed(IEncoderBackend backend)
    {
        lock (_gate)
        {
            _failed.Add(backend);
        }
    }

    private void Report(Exception e, IEncoderBackend backend)
    {
        if (Settings.Mode == EncoderMode.Vaapi)
        {
            Log?.LogError(e, "JULOS_ENCODER=vaapi, but {Backend} does not work; the software encoder is used (run 'diag encoders' for details).", backend.Name);
        }
        else
        {
            Log?.LogWarning("{Backend} does not work ({Message}); the software encoder is used. 'diag encoders' says why.", backend.Name, e.Message);
        }
    }
}

/// <summary>libopenh264: H.264 on the CPU. The way out when nothing else works.</summary>
public sealed class SoftwareBackend : IEncoderBackend
{
    public string Name => "OpenH264";

    public bool IsHardware => false;

    public VideoCodecs Codecs => VideoCodecs.H264;

    public bool CheckAvailable(out string? reason)
    {
        reason = H264Encoder.IsAvailable ? null : H264Encoder.UnavailableReason ?? "libopenh264 is not available";
        return H264Encoder.IsAvailable;
    }

    public IVideoEncoder Create(EncoderRequest request, VideoCodec codec) =>
        new H264Encoder(request.Width, request.Height, request.FramesPerSecond, request.KilobitsPerSecond);
}
