using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace JulOS.Backend.Logic;

/// <summary>
/// Einstellungen, die der Server je Benutzer merkt (Tastenleiste, Themenfarbe, Sprache). Es gibt nur diese Schluessel, und jeder Wert wird geprueft und neu
/// geschrieben: unbekannte Felder fallen weg, Laengen und Zahl der Eintraege sind begrenzt. So kann ein Browser dem Server nichts Beliebiges unterschieben.
/// </summary>
public static partial class SettingsRules
{
    public const string Keybar = "keybar";
    public const string Theme = "theme";
    public const string Lang = "lang";
    public const string Files = "files";
    public static readonly IReadOnlyList<string> Keys = [Keybar, Theme, Lang, Files];

    public const int MaxPins = 100, MaxClosed = 200;

    public const int MaxRows = 12, MaxItemsPerRow = 60;

    // Ohne die Ersetzung von + und Umlauten: das Ergebnis ist JSON fuer die Datenbank und die API, kein HTML
    private static readonly JsonWriterOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] KeyTypes = ["chord", "mod", "text", "action"];
    private static readonly string[] Scopes = ["both", "desktop", "terminal"];
    private static readonly string[] Schemes = ["auto", "light", "dark"];
    private static readonly string[] Languages = ["de", "en", "system"];

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}$")] private static partial Regex HexColor();
    [GeneratedRegex(@"^[a-z0-9_\-]{1,30}$")] private static partial Regex Token();
    [GeneratedRegex(@"^[0-9A-Za-z_\-]{1,64}$")] private static partial Regex PlaceId();                  // eine Verbindungskennung (UUID) oder "local"

    /// <returns>Der Wert in der Form, in der er gespeichert wird, oder eine Fehlermeldung.</returns>
    public static (string? Json, string? Error) Validate(string key, JsonElement value)
    {
        try
        {
            return key switch
            {
                Keybar => (WriteKeybar(value), null),
                Theme => (WriteTheme(value), null),
                Lang => (WriteLang(value), null),
                Files => (WriteFiles(value), null),
                _ => (null, "Unbekannte Einstellung."),
            };
        }
        catch (FormatException e) { return (null, e.Message); }
    }

    private static string Text(JsonElement o, string name, int max, bool required = true)
    {
        if (!o.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return required ? throw new FormatException($"Feld {name} fehlt.") : "";
        var s = p.GetString() ?? "";
        if (s.Length > max || s.Any(char.IsControl)) throw new FormatException($"Feld {name} ist ungültig oder zu lang.");
        return s;
    }

    private static string WriteTheme(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) throw new FormatException("Thema erwartet ein Objekt.");
        var seed = Text(v, "seed", 7);
        if (seed != "system" && !HexColor().IsMatch(seed)) throw new FormatException("Farbe ungültig (system oder #rrggbb).");
        var scheme = Text(v, "scheme", 5);
        if (!Schemes.Contains(scheme)) throw new FormatException("Helligkeit ungültig.");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Plain))
        {
            w.WriteStartObject(); w.WriteString("seed", seed.ToLowerInvariant()); w.WriteString("scheme", scheme); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static readonly string[] FileModes = ["list", "tiles"], FileDensities = ["compact", "comfy"], FileSorts = ["name", "date", "type", "size"], FileDirs = ["asc", "desc"], FileCols = ["name", "date", "type", "size"];

    /// <summary>
    /// Die Ansicht der Dateien-App (Liste oder Kacheln, Dichte, Sortierung, versteckte Dateien, Spaltenbreiten), die angehefteten Ordner und die zugeklappten Knoten der Seitenleiste (docs/FILES-EXPLORER.md).
    /// Ein Pin ist nur ein Ort (Verbindungskennung) und ein Pfad: er gibt keinen Zugriff, jede Anfrage wird wie sonst geprueft.
    /// </summary>
    private static string WriteFiles(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) throw new FormatException("Dateien erwartet ein Objekt.");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Plain))
        {
            w.WriteStartObject();
            if (v.TryGetProperty("view", out var view) && view.ValueKind == JsonValueKind.Object)
            {
                w.WritePropertyName("view"); w.WriteStartObject();
                w.WriteString("mode", OneOf(view, "mode", FileModes)); w.WriteString("density", OneOf(view, "density", FileDensities));
                w.WriteString("sortCol", OneOf(view, "sortCol", FileSorts)); w.WriteString("sortDir", OneOf(view, "sortDir", FileDirs));
                w.WriteBoolean("showHidden", !view.TryGetProperty("showHidden", out var sh) || sh.ValueKind != JsonValueKind.False);
                if (view.TryGetProperty("cols", out var cols) && cols.ValueKind == JsonValueKind.Object)
                {
                    w.WritePropertyName("cols"); w.WriteStartObject();
                    foreach (var c in FileCols)
                        if (cols.TryGetProperty(c, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var px)) w.WriteNumber(c, Math.Clamp(px, 60, 800));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            if (v.TryGetProperty("pins", out var pins) && pins.ValueKind == JsonValueKind.Array)
            {
                if (pins.GetArrayLength() > MaxPins) throw new FormatException("Zu viele angeheftete Ordner.");
                w.WritePropertyName("pins"); w.WriteStartArray();
                var seen = new HashSet<string>();
                foreach (var pin in pins.EnumerateArray())
                {
                    if (pin.ValueKind != JsonValueKind.Object) throw new FormatException("Pin ungültig.");
                    var place = Text(pin, "place", 64); var path = Text(pin, "path", 4096); var name = Text(pin, "name", 255, required: false);
                    if (!PlaceId().IsMatch(place) || path.Length == 0 || !seen.Add(place + "\0" + path)) continue;
                    w.WriteStartObject(); w.WriteString("place", place); w.WriteString("path", path); w.WriteString("name", name); w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (v.TryGetProperty("closed", out var closed) && closed.ValueKind == JsonValueKind.Array)
            {
                if (closed.GetArrayLength() > MaxClosed) throw new FormatException("Zu viele Knoten.");
                w.WritePropertyName("closed"); w.WriteStartArray();
                foreach (var id in closed.EnumerateArray())
                {
                    if (id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 80 } s || s.Any(char.IsControl)) throw new FormatException("Knoten ungültig.");
                    w.WriteStringValue(s);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string OneOf(JsonElement o, string name, string[] allowed)
    {
        var s = Text(o, name, 12, required: false);
        return allowed.Contains(s) ? s : allowed[0];
    }

    private static string WriteLang(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) throw new FormatException("Sprache erwartet ein Objekt.");
        var lang = Text(v, "lang", 6);
        if (!Languages.Contains(lang)) throw new FormatException("Sprache ungültig (de, en oder system).");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Plain)) { w.WriteStartObject(); w.WriteString("lang", lang); w.WriteEndObject(); }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string WriteKeybar(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new FormatException("Tastenleiste erwartet eine Liste von Zeilen.");
        if (rows.GetArrayLength() is < 1 or > MaxRows) throw new FormatException("Zu viele oder keine Zeilen.");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Plain))
        {
            w.WriteStartObject(); w.WritePropertyName("rows"); w.WriteStartArray();
            var ids = new HashSet<string>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw new FormatException("Zeile ungültig.");
                var id = Text(row, "id", 30);
                if (!Token().IsMatch(id) || !ids.Add(id)) throw new FormatException("Zeilenkennung ungültig oder doppelt.");
                var title = Text(row, "title", 40);
                var icon = Text(row, "icon", 30, required: false);
                if (icon.Length > 0 && !Token().IsMatch(icon)) throw new FormatException("Symbol ungültig.");
                if (!row.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxItemsPerRow) throw new FormatException("Tasten ungültig oder zu viele.");
                w.WriteStartObject(); w.WriteString("id", id); w.WriteString("title", title);
                if (icon.Length > 0) w.WriteString("icon", icon);
                if (row.TryGetProperty("fixed", out var fx) && fx.ValueKind == JsonValueKind.True) w.WriteBoolean("fixed", true);
                w.WritePropertyName("items"); w.WriteStartArray();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Taste ungültig.");
                    var type = Text(item, "type", 10);
                    if (!KeyTypes.Contains(type)) throw new FormatException("Tastenart ungültig.");
                    var scope = Text(item, "scope", 10, required: false);
                    if (scope.Length > 0 && !Scopes.Contains(scope)) throw new FormatException("Bereich ungültig.");
                    w.WriteStartObject(); w.WriteString("label", Text(item, "label", 24)); w.WriteString("type", type); w.WriteString("value", Text(item, "value", 200));
                    if (scope.Length > 0 && scope != "both") w.WriteString("scope", scope);
                    w.WriteEndObject();
                }
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
