using JulOS.Integrations.Display.Video;

namespace JulOS.Frontend.Cli;

/// <summary>
/// Diagnose auf der Server-Konsole, ohne Datenbank:
///   dotnet JulOS.dll diag encoders
/// prueft den Software-Kodierer, die Grafikkarte (VAAPI) und versucht je ein Testbild (siehe docs/ENCODERS.md).
/// </summary>
internal static class DiagCli
{
    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "encoders")
        {
            var settings = EncoderSettings.Parse(Environment.GetEnvironmentVariable("JULOS_ENCODER"), Environment.GetEnvironmentVariable("JULOS_CODEC"), Console.Error.WriteLine);
            return EncoderReport.Run(Console.Out, settings);
        }

        Console.Error.WriteLine("Aufruf: dotnet JulOS.dll diag encoders");
        return 2;
    }
}
