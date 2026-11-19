using System.Runtime.InteropServices;
using JulOS.Backend.Logic;
using JulOS.Integrations.Display;

namespace JulOS.Frontend.About;

internal sealed record EngineInfo(bool Guacamole, bool Stream, string? StreamUnavailable);

internal sealed record AboutResponse(string Name, string Version, string? Commit, string? Built, string Runtime, string Os, EngineInfo Engines, string Repository, string License);

/// <summary>"Ueber JulOS": Version, Herkunft des Builds und was der Server kann. Braucht eine Sitzung (ApiGuard), nur /api/health ist offen.</summary>
internal static class AboutEndpoints
{
    public static void MapAbout(this IEndpointRouteBuilder app, AppInfo info) =>
        app.MapGet("/api/about", () => Results.Json(
            new AboutResponse("JulOS", info.Version, info.Commit, info.Built, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                new EngineInfo(true, ScreenStream.IsAvailable, ScreenStream.IsAvailable ? null : ScreenStream.UnavailableReason),
                "https://github.com/Juloc/JulOS", "MIT"),
            AppJson.Default.AboutResponse));
}
