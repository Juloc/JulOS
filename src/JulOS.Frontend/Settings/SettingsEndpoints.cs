using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Settings;

internal sealed record SettingsResponse(Dictionary<string, JsonElement> Settings);
internal sealed record SettingRequest(JsonElement? Value);

/// <summary>Einstellungen je Benutzer auf dem Server (Tastenleiste, Themenfarbe, Sprache), damit sie auf jedem Geraet gelten. Die Seite haelt eine lokale Kopie als Rueckfall.</summary>
internal static class SettingsEndpoints
{
    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", async (HttpContext ctx, SettingsStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var all = await store.GetAllAsync(id.UserId, ctx.RequestAborted);
            var result = new Dictionary<string, JsonElement>();
            foreach (var (key, json) in all)
                if (SettingsRules.Keys.Contains(key)) result[key] = JsonDocument.Parse(json).RootElement.Clone();
            return Results.Json(new SettingsResponse(result), AppJson.Default.SettingsResponse);
        });

        app.MapPut("/api/settings/{key}", async (string key, SettingRequest body, HttpContext ctx, SettingsStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!SettingsRules.Keys.Contains(key)) return Error(404, "Unbekannte Einstellung.");
            if (body.Value is not { } value) return Error(400, "Wert fehlt.");
            var (json, error) = SettingsRules.Validate(key, value);
            if (json is null) return Error(400, error!);
            await store.SetAsync(id.UserId, key, json, ctx.RequestAborted);
            return Results.NoContent();
        });

        // Zuruecksetzen: der Server vergisst den Wert, die Seite faellt auf ihre Standardwerte zurueck
        app.MapDelete("/api/settings/{key}", async (string key, HttpContext ctx, SettingsStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!SettingsRules.Keys.Contains(key)) return Error(404, "Unbekannte Einstellung.");
            await store.DeleteAsync(id.UserId, key, ctx.RequestAborted);
            return Results.NoContent();
        });
    }
}
