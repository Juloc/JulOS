using System.Text.Json;

namespace JulOS.Backend.Logic;

/// <summary>Auswertung fuer die Gesundheitspruefung des Containers (<c>dotnet JulOS.dll health</c>).</summary>
public static class HealthCheck
{
    /// <summary>Erster Port aus ASPNETCORE_HTTP_PORTS ("8080" oder "8080;9090"), sonst 8080.</summary>
    public static int Port(string? setting)
    {
        var first = (setting ?? "").Split(';', ',')[0].Trim();
        return int.TryParse(first, out var p) && p is > 0 and < 65536 ? p : 8080;
    }

    /// <summary>Die Antwort von /api/health: <c>{"ok":true,...}</c>. Ohne Datenbank steht dort ok=false.</summary>
    public static bool IsHealthy(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
