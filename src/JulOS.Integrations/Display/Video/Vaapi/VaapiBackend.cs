using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display.Video.Vaapi;

/// <summary>What a look at the machine's GPU found. Made once; <see cref="VaapiBackend.Probe"/>.</summary>
internal sealed record VaapiProbe(string? Device, string? Vendor, string? Version, IReadOnlyList<VaEncodeCaps> Encoders, IReadOnlyList<string> Problems, IReadOnlyList<string> Nodes)
{
    public bool Usable => Device is not null && Encoders.Count > 0;

    public VideoCodecs Codecs => Encoders.Where(e => e.CanConstantRate).Aggregate(VideoCodecs.None, (set, e) => set | e.Codec.ToSet());
}

/// <summary>
/// VAAPI encoding through libva: finds a render node whose driver can encode, remembers what it can do, and makes
/// <see cref="VaapiEncoder"/>s for it. Never throws out of <see cref="CheckAvailable"/>; every problem is a reason.
/// </summary>
public sealed class VaapiBackend : IEncoderBackend
{
    private readonly Lock _gate = new();
    private VaapiProbe? _probe;

    public string Name => "VAAPI";

    public bool IsHardware => true;

    public VideoCodecs Codecs => Probe().Codecs;

    internal VaapiProbe Probe()
    {
        lock (_gate)
        {
            return _probe ??= DoProbe();
        }
    }

    public bool CheckAvailable(out string? reason)
    {
        var probe = Probe();
        if (probe.Usable && probe.Codecs != VideoCodecs.None)
        {
            reason = null;
            return true;
        }

        reason = probe.Problems.Count > 0 ? string.Join("; ", probe.Problems)
            : probe.Usable ? "der Treiber hat keinen Kodierer mit konstanter Bitrate (CBR) für H.264 oder HEVC" : "keine nutzbare Grafikkarte";
        return false;
    }

    private static VaapiProbe DoProbe()
    {
        var problems = new List<string>();
        var nodes = VaDevice.Candidates();
        if (!OperatingSystem.IsLinux())
        {
            return new VaapiProbe(null, null, null, [], ["VAAPI gibt es nur unter Linux"], nodes);
        }

        if (nodes.Count == 0)
        {
            problems.Add("kein Render-Knoten in /dev/dri (der Container braucht 'devices: /dev/dri:/dev/dri')");
            return new VaapiProbe(null, null, null, [], problems, nodes);
        }

        foreach (var node in nodes)
        {
            using var device = VaDevice.Open(node, out var problem);
            if (device is null)
            {
                problems.Add(problem ?? node);
                continue;
            }

            try
            {
                var encoders = device.QueryEncoders();
                if (encoders.Count == 0)
                {
                    problems.Add($"{node}: der Treiber ({device.Vendor}) kann weder H.264 noch HEVC kodieren");
                    continue;
                }

                return new VaapiProbe(node, device.Vendor, device.Version, encoders, problems, nodes);
            }
            catch (VaapiException e)
            {
                problems.Add($"{node}: {e.Message}");
            }
        }

        return new VaapiProbe(null, null, null, [], problems, nodes);
    }

    /// <summary>The (profile, entry point) pairs to try for a codec, best first: the richer profile first, and for a profile the full pipeline before the low-power one.</summary>
    internal static IEnumerable<VaEncodeCaps> Order(IEnumerable<VaEncodeCaps> encoders, VideoCodec codec) =>
        encoders.Where(e => e.Codec == codec && e.CanConstantRate)
            .OrderBy(e => e.Profile switch { LibVa.ProfileH264High => 0, LibVa.ProfileH264Main => 1, LibVa.ProfileH264ConstrainedBaseline => 2, _ => 0 })
            .ThenBy(e => e.Entrypoint == LibVa.EntrypointEncSlice ? 0 : 1);

    public IVideoEncoder Create(EncoderRequest request, VideoCodec codec)
    {
        var probe = Probe();
        if (!probe.Usable)
        {
            throw new InvalidOperationException("keine nutzbare Grafikkarte");
        }

        var errors = new List<string>();
        foreach (var caps in Order(probe.Encoders, codec))
        {
            try
            {
                return VaapiEncoder.Create(probe.Device!, caps, request, codec);
            }
            catch (VaapiException e)
            {
                errors.Add($"{caps.ProfileName}/{caps.EntrypointName}: {e.Message}");
            }
        }

        throw new InvalidOperationException(errors.Count == 0 ? $"the driver has no {codec.Label()} encoder with constant bit rate" : string.Join("; ", errors));
    }
}
