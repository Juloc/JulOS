using JulOS.Backend.Logic;

namespace JulOS.Frontend.Cli;

/// <summary>
/// Gesundheitspruefung fuer den Container (HEALTHCHECK in Compose):
///   dotnet JulOS.dll health
/// fragt /api/health auf dem eigenen Port ab und endet mit 0, wenn der Server und die Datenbank antworten. Das Runtime-Image hat weder curl noch wget,
/// deshalb steckt die Abfrage im Programm selbst.
/// </summary>
internal static class HealthCli
{
    public static async Task<int> RunAsync()
    {
        var port = HealthCheck.Port(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/api/health");
            if (HealthCheck.IsHealthy(body)) return 0;
            Console.Error.WriteLine("Datenbank nicht erreichbar");
        }
        catch (Exception e) { Console.Error.WriteLine("Server antwortet nicht: " + e.Message); }
        return 1;
    }
}
