using System.Diagnostics;
using JulOS.Integrations.Display.Video.Vaapi;

namespace JulOS.Integrations.Display.Video;

/// <summary>What the encoders of this machine can do: a few lines for the log at start, and the longer report of <c>dotnet JulOS.dll diag encoders</c>.</summary>
public static class EncoderReport
{
    /// <summary>A few lines for the log at start.</summary>
    public static IReadOnlyList<string> Summary(EncoderSelector selector)
    {
        var lines = new List<string>
        {
            $"JULOS_ENCODER={selector.Settings.Mode}, JULOS_CODEC={selector.Settings.Codec}",
            H264Encoder.IsAvailable ? $"Software H.264: OpenH264 {H264Encoder.LibraryVersion}" : "Software H.264: nicht verfügbar (" + H264Encoder.UnavailableReason + ")",
        };
        if (selector.Settings.Mode == EncoderMode.Software)
        {
            lines.Add("Grafikkarte: wird nicht benutzt (JULOS_ENCODER=software)");
            return lines;
        }

        foreach (var backend in selector.Backends.Where(b => b.IsHardware))
        {
            lines.Add(backend.CheckAvailable(out var reason)
                ? $"{backend.Name}: {backend.Codecs} verfügbar{(backend is VaapiBackend vaapi && vaapi.Probe() is { Vendor: { } vendor } ? " (" + vendor + ")" : "")}"
                : $"{backend.Name}: nicht verfügbar ({reason})");
        }

        return lines;
    }

    /// <summary>One short phrase for "Über JulOS": what codes the pictures ("H.264 auf der CPU", "GPU (VAAPI: H.264, H.265), sonst H.264 auf der CPU").</summary>
    public static string Short(EncoderSelector selector)
    {
        const string software = "H.264 auf der CPU";
        if (selector.Settings.Mode == EncoderMode.Software)
        {
            return software;
        }

        foreach (var backend in selector.Backends.Where(b => b.IsHardware))
        {
            if (backend.CheckAvailable(out _))
            {
                var codecs = new[] { (VideoCodec.H264, "H.264"), (VideoCodec.Hevc, "H.265") }.Where(c => backend.Codecs.Has(c.Item1)).Select(c => c.Item2);
                return $"GPU ({backend.Name}: {string.Join(", ", codecs)}), sonst {software}";
            }
        }

        return software;
    }

    /// <summary>The full report. Returns 0 when the software encoder works (the GPU is a bonus), 1 when not even that does.</summary>
    public static int Run(TextWriter output, EncoderSettings settings)
    {
        var failed = false;
        output.WriteLine("JulOS Bildkodierer: Diagnose");
        output.WriteLine($"Einstellung: JULOS_ENCODER={settings.Mode}, JULOS_CODEC={settings.Codec}");
        output.WriteLine();

        output.WriteLine("1. Software H.264 (OpenH264, immer der Rückfall)");
        if (!H264Encoder.IsAvailable)
        {
            output.WriteLine($"   FEHLER: libopenh264 nicht verfügbar: {H264Encoder.UnavailableReason}");
            failed = true;
        }
        else
        {
            output.WriteLine($"   Bibliothek: OpenH264 {H264Encoder.LibraryVersion}");
            try
            {
                using var encoder = new H264Encoder(1920, 1080, 30, 16_000);
                output.WriteLine($"   Testbild 1920x1080: OK ({TestEncode(encoder)})");
            }
            catch (Exception e)
            {
                output.WriteLine($"   FEHLER beim Testbild: {e.Message}");
                failed = true;
            }
        }

        output.WriteLine();
        output.WriteLine("2. Grafikkarte (VAAPI)");
        var vaapiBackend = new VaapiBackend();
        ReportVaapi(output, vaapiBackend);

        output.WriteLine();
        output.WriteLine("3. Was ein Browser bekäme");
        var selector = new EncoderSelector([vaapiBackend, new SoftwareBackend()], settings);
        foreach (var client in new[] { VideoCodecs.H264, VideoCodecs.H264 | VideoCodecs.Hevc })
        {
            try
            {
                var choice = selector.Choose(new EncoderRequest(1920, 1080, 30, 16_000, client));
                output.WriteLine($"   Browser kann {client}: {choice.Encoder.Name}, {choice.Encoder.Codec.Label()}, {(choice.Encoder.IsHardware ? "GPU" : "CPU")}");
                foreach (var skipped in choice.Skipped)
                {
                    output.WriteLine($"      übersprungen: {skipped}");
                }

                choice.Encoder.Dispose();
            }
            catch (Exception e)
            {
                output.WriteLine($"   Browser kann {client}: kein Kodierer ({e.Message})");
                failed = true;
            }
        }

        output.WriteLine();
        output.WriteLine(failed ? "Ergebnis: es gibt ein Problem (siehe oben)." : "Ergebnis: in Ordnung. Ohne Grafikkarte läuft alles auf der CPU.");
        return failed ? 1 : 0;
    }

    private static void ReportVaapi(TextWriter output, VaapiBackend backend)
    {
        var probe = backend.Probe();
        output.WriteLine($"   /dev/dri: {(Directory.Exists("/dev/dri") ? "vorhanden" : "FEHLT (Container ohne devices: /dev/dri)")}");
        PrintIdentity(output);
        foreach (var node in probe.Nodes)
        {
            output.WriteLine($"   Knoten {node}: {Describe(node)}");
        }

        if (probe.Nodes.Count == 0)
        {
            output.WriteLine("   Kein Render-Knoten (renderD*) gefunden.");
        }

        foreach (var problem in probe.Problems)
        {
            output.WriteLine($"   Problem: {problem}");
        }

        if (!probe.Usable)
        {
            output.WriteLine("   Ergebnis: keine nutzbare Grafikkarte. Der Server benutzt die CPU (H.264). Hilfe: docs/ENCODERS.md");
            return;
        }

        output.WriteLine($"   Gerät: {probe.Device}");
        output.WriteLine($"   Treiber: {probe.Vendor} (libva {probe.Version})");
        output.WriteLine("   Profile und Einstiegspunkte zum Kodieren:");
        foreach (var caps in probe.Encoders.OrderBy(c => c.Codec).ThenBy(c => c.Profile).ThenBy(c => c.Entrypoint))
        {
            output.WriteLine($"     {caps.ProfileName,-26} {caps.EntrypointName,-11} Ratenregelung {caps.RateControlText,-14} Kopfzeilen {caps.PackedText,-16} bis {caps.MaxWidth}x{caps.MaxHeight}{(caps.CanConstantRate ? "" : "  (ohne CBR: wird nicht benutzt)")}");
        }

        output.WriteLine("   Testbild 1920x1080 (zwei Bilder: Schlüsselbild und Folgebild) je Profil:");
        var any = false;
        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.Hevc })
        {
            var order = VaapiBackend.Order(probe.Encoders, codec).ToList();
            if (order.Count == 0)
            {
                output.WriteLine($"     {codec.Label()}: kein Kodierer mit CBR");
                continue;
            }

            foreach (var caps in order)
            {
                try
                {
                    using var encoder = VaapiEncoder.Create(probe.Device!, caps, new EncoderRequest(1920, 1080, 30, 16_000, codec.ToSet()), codec, testPicture: false);
                    output.WriteLine($"     {caps.ProfileName}/{caps.EntrypointName}: OK ({TestEncode(encoder)})");
                    any = true;
                }
                catch (Exception e) when (e is VaapiException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
                {
                    output.WriteLine($"     {caps.ProfileName}/{caps.EntrypointName}: FEHLER: {e.Message}");
                }
            }
        }

        output.WriteLine(any ? "   Ergebnis: die Grafikkarte kann kodieren." : "   Ergebnis: die Grafikkarte nimmt die Aufträge nicht an. Der Server benutzt die CPU (H.264).");
    }

    // Two pictures: a key frame of a test image and a P frame after a small change. A text for the report: sizes, parameter sets, codec string.
    private static string TestEncode(IVideoEncoder encoder)
    {
        using var frame = new I420Frame(encoder.Width, encoder.Height);
        Pattern(frame, 0);
        frame.MarkChanged();
        var watch = Stopwatch.StartNew();
        var first = encoder.Encode(frame, true, out var key).ToArray();
        var firstMs = watch.Elapsed.TotalMilliseconds;
        if (first.Length == 0 || !key)
        {
            throw new InvalidOperationException("das erste Bild ist leer oder kein Schlüsselbild");
        }

        Pattern(frame, 1);
        frame.MarkChanged(100, 100, 200, 100);
        watch.Restart();
        var second = encoder.Encode(frame, false, out var secondKey).ToArray();
        var secondMs = watch.Elapsed.TotalMilliseconds;
        if (second.Length == 0)
        {
            throw new InvalidOperationException("das zweite Bild ist leer");
        }

        var codec = CodecStrings.FromStream(first, encoder.Codec) ?? "kein SPS im Schlüsselbild";
        return $"{encoder.Codec.Label()}, Schlüsselbild {first.Length} Byte in {firstMs:0.0} ms, Folgebild {second.Length} Byte{(secondKey ? " (Schlüsselbild)" : "")} in {secondMs:0.0} ms, Codec {codec}";
    }

    private static void Pattern(I420Frame frame, int variant)
    {
        var y = frame.Y;
        for (var row = 0; row < frame.Height; row++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                y[row * frame.StrideY + x] = (byte)(16 + ((x / 8 + row / 8) % 2 == 0 ? 40 : 200) + (x * 3 + row * 5 + variant * 7) % 16);
            }
        }

        frame.U.Fill((byte)(110 + variant));
        frame.V.Fill(140);
    }

    private static void PrintIdentity(TextWriter output)
    {
        try
        {
            var status = File.ReadAllLines("/proc/self/status");
            string Field(string name) => status.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal))?[(name.Length + 1)..].Trim() ?? "?";
            output.WriteLine($"   Prozess: Benutzer-ID {Field("Uid").Split('\t', ' ')[0]}, Gruppen-ID {Field("Gid").Split('\t', ' ')[0]}, weitere Gruppen: {Field("Groups")}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // kein /proc: nur Auskunft
        }
    }

    private static string Describe(string node)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "nur unter Linux";
        }

        string rights;
        try
        {
            rights = Convert.ToString((int)File.GetUnixFileMode(node), 8) + " (Modus)";
            var info = new ProcessStartInfo("stat", ["-c", "Besitzer %U, Gruppen-ID %g", node]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(info);
            var text = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit(2000);
            if (!string.IsNullOrEmpty(text))
            {
                rights += ", " + text;
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            rights = "Rechte nicht lesbar";
        }

        try
        {
            using var handle = File.OpenHandle(node, FileMode.Open, FileAccess.ReadWrite);
            return rights + ", Zugriff: ja";
        }
        catch (UnauthorizedAccessException)
        {
            return rights + ", Zugriff: NEIN (keine Berechtigung; group_add mit der Gruppen-ID des Knotens auf dem Host)";
        }
        catch (IOException e)
        {
            return rights + ", Zugriff: NEIN (" + e.Message + ")";
        }
    }
}
