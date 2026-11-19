using System.Text.Json;
using System.Text.Json.Nodes;

namespace JulOS.Frontend.Agent;

/// <summary>Bereinigt die Fensterliste, die ein Agent meldet, bevor sie zum Browser geht (der Agent ist nicht der Server).</summary>
internal static class WindowList
{
    /// <summary>Behaelt nur Fenster, deren "id" eine Zahl mit hoechstens 12 Ziffern ist (wie die Route /api/agent/windows/{id}/{action} sie verlangt). Kein JSON-Objekt: unveraendert zurueck.</summary>
    public static string Sanitize(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root is not JsonObject o || o["windows"] is not JsonArray windows) return json;
        for (var i = windows.Count - 1; i >= 0; i--)
            if (!ValidId(windows[i])) windows.RemoveAt(i);
        return o.ToJsonString();
    }

    private static bool ValidId(JsonNode? window)
    {
        if (window is not JsonObject w || w["id"] is not JsonValue v) return false;
        if (v.TryGetValue<long>(out var n)) return n is >= 0 and <= 999_999_999_999;
        return false;
    }
}
