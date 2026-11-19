using System.Runtime.InteropServices;

namespace JulOS.Integrations.Display.Video.Vaapi;

/// <summary>What the driver says about one (profile, entry point) pair that can encode.</summary>
internal sealed record VaEncodeCaps(int Profile, int Entrypoint, uint RateControls, uint PackedHeaders, uint MaxRefFrames, uint MaxWidth, uint MaxHeight, uint HevcBlockSizes)
{
    public string ProfileName => Profile switch
    {
        LibVa.ProfileH264ConstrainedBaseline => "H264 ConstrainedBaseline",
        LibVa.ProfileH264Main => "H264 Main",
        LibVa.ProfileH264High => "H264 High",
        LibVa.ProfileHevcMain => "HEVC Main",
        _ => "profile " + Profile,
    };

    public string EntrypointName => Entrypoint switch { LibVa.EntrypointEncSlice => "EncSlice", LibVa.EntrypointEncSliceLP => "EncSliceLP", _ => "entry " + Entrypoint };

    public VideoCodec Codec => Profile == LibVa.ProfileHevcMain ? VideoCodec.Hevc : VideoCodec.H264;

    public bool CanConstantRate => (RateControls & LibVa.RcCbr) != 0;

    public string RateControlText => string.Join("+", new[] { (LibVa.RcCbr, "CBR"), (LibVa.RcVbr, "VBR"), (LibVa.RcCqp, "CQP") }.Where(x => (RateControls & x.Item1) != 0).Select(x => x.Item2));

    public string PackedText => PackedHeaders == 0 ? "none" : string.Join("+", new[] { (LibVa.PackedHeaderSequence, "SPS"), (LibVa.PackedHeaderPicture, "PPS"), (LibVa.PackedHeaderSlice, "slice") }.Where(x => (PackedHeaders & x.Item1) != 0).Select(x => x.Item2));
}

/// <summary>Why a device could not be used: the first thing that went wrong, as text for the log and the diagnostics.</summary>
internal sealed record VaDeviceProblem(string Message);

// One opened DRM render node with an initialised libva display on it. Owns both and closes them on Dispose.
internal sealed class VaDevice : IDisposable
{
    private SafeHandle? _file;
    private bool _initialized;

    private VaDevice(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public nint Display { get; private set; }

    public string Vendor { get; private set; } = "";

    public string Version { get; private set; } = "";

    /// <summary>The render nodes of this machine, /dev/dri/renderD*, or the one in JULOS_VAAPI_DEVICE.</summary>
    public static IReadOnlyList<string> Candidates()
    {
        var forced = Environment.GetEnvironmentVariable("JULOS_VAAPI_DEVICE");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            return [forced];
        }

        try
        {
            return Directory.Exists("/dev/dri") ? Directory.GetFiles("/dev/dri", "renderD*").Order(StringComparer.Ordinal).ToArray() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Opens the node and starts libva on it. Null with the reason when that does not work (no libva, no permission, no driver).</summary>
    public static VaDevice? Open(string path, out string? problem)
    {
        var device = new VaDevice(path);
        try
        {
            device._file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
            device.Display = LibVa.vaGetDisplayDRM((int)device._file.DangerousGetHandle());
            if (device.Display == 0)
            {
                throw new VaapiException("vaGetDisplayDRM gave no display");
            }

            LibVa.Check(LibVa.vaInitialize(device.Display, out var major, out var minor), "vaInitialize");
            device._initialized = true;
            device.Version = $"{major}.{minor}";
            device.Vendor = Marshal.PtrToStringUTF8(LibVa.vaQueryVendorString(device.Display)) ?? "";
            problem = null;
            return device;
        }
        catch (Exception e) when (e is VaapiException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            problem = e switch
            {
                UnauthorizedAccessException => $"{path}: keine Berechtigung (der Server-Benutzer braucht die Gruppe des Render-Knotens, siehe docs/ENCODERS.md)",
                DllNotFoundException => "libva ist nicht installiert (Pakete libva2 und libva-drm2): " + e.Message,
                _ => $"{path}: {e.Message}",
            };
            device.Dispose();
            return null;
        }
    }

    /// <summary>Every profile of H.264 and HEVC that can encode, with the entry points the driver has for it.</summary>
    public IReadOnlyList<VaEncodeCaps> QueryEncoders()
    {
        var result = new List<VaEncodeCaps>();
        unsafe
        {
            var attributes = stackalloc VAConfigAttrib[6];
            var profiles = new int[Math.Max(1, LibVa.vaMaxNumProfiles(Display))];
            int count;
            fixed (int* p = profiles)
            {
                LibVa.Check(LibVa.vaQueryConfigProfiles(Display, p, out count), "vaQueryConfigProfiles");
            }

            foreach (var profile in profiles.Take(count))
            {
                if (profile is not (LibVa.ProfileH264ConstrainedBaseline or LibVa.ProfileH264Main or LibVa.ProfileH264High or LibVa.ProfileHevcMain))
                {
                    continue;
                }

                var entries = new int[Math.Max(1, LibVa.vaMaxNumEntrypoints(Display))];
                int entryCount;
                fixed (int* e = entries)
                {
                    LibVa.Check(LibVa.vaQueryConfigEntrypoints(Display, profile, e, out entryCount), "vaQueryConfigEntrypoints");
                }

                foreach (var entry in entries.Take(entryCount))
                {
                    if (entry is not (LibVa.EntrypointEncSlice or LibVa.EntrypointEncSliceLP))
                    {
                        continue;
                    }

                    attributes[0] = new VAConfigAttrib { type = LibVa.AttribRateControl, value = LibVa.AttribNotSupported };
                    attributes[1] = new VAConfigAttrib { type = LibVa.AttribEncPackedHeaders, value = LibVa.AttribNotSupported };
                    attributes[2] = new VAConfigAttrib { type = LibVa.AttribEncMaxRefFrames, value = LibVa.AttribNotSupported };
                    attributes[3] = new VAConfigAttrib { type = LibVa.AttribMaxPictureWidth, value = LibVa.AttribNotSupported };
                    attributes[4] = new VAConfigAttrib { type = LibVa.AttribMaxPictureHeight, value = LibVa.AttribNotSupported };
                    attributes[5] = new VAConfigAttrib { type = LibVa.AttribEncHevcBlockSizes, value = LibVa.AttribNotSupported };
                    LibVa.Check(LibVa.vaGetConfigAttributes(Display, profile, entry, attributes, 6), "vaGetConfigAttributes");
                    uint Value(int i, uint fallback) => attributes[i].value == LibVa.AttribNotSupported ? fallback : attributes[i].value;
                    result.Add(new VaEncodeCaps(profile, entry, Value(0, 0), Value(1, 0), Value(2, 1), Value(3, 4096), Value(4, 4096), Value(5, 0)));
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_initialized && Display != 0)
        {
            try { LibVa.vaTerminate(Display); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { /* nothing to terminate */ }
        }

        _initialized = false;
        Display = 0;
        _file?.Dispose();
        _file = null;
    }
}
