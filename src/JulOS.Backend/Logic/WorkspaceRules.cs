using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JulOS.Backend.Logic;

public sealed record WorkspaceRect(int X, int Y, int W, int H);
public sealed record WorkspaceScreen(string Id, WorkspaceRect Rect);
public sealed record WorkspaceWindow(string Id, string Kind, string? App, Guid? ConnectionId, WorkspaceRect Rect, int Z, string State, string? Snap, WorkspaceRect? Restore);
public sealed record WorkspaceDock(bool? Collapsed, string? Side, IReadOnlyList<string> Order);
public sealed record Workspace(IReadOnlyList<WorkspaceScreen> Screens, IReadOnlyList<WorkspaceWindow> Windows, string? Focus, WorkspaceDock? Dock);

/// <summary>
/// Der Arbeitsplatz eines Geraets (docs/MULTI-SCREEN.md): Bildschirme (Browserfenster) und Fenster in Desktop-Koordinaten. Wie bei SettingsRules prueft und schreibt der
/// Server jedes Dokument neu: unbekannte Felder fallen weg, Zahl und Laenge sind begrenzt, Zahlen liegen im Bereich. Es steht nie etwas Geheimes darin: eine
/// Verbindung ist nur ihre Kennung, und die muss dem Benutzer gehoeren (<see cref="Keep"/>); Passwoerter und Ziele gehoeren nicht ins Dokument.
/// </summary>
public static partial class WorkspaceRules
{
    public const int MaxWindows = 32, MaxScreens = 8, MaxDockOrder = 64, MaxDevices = 16, MaxNameLength = 40, MaxBytes = 64 * 1024;
    public const int CoordLimit = 100_000, SizeLimit = 100_000, MaxZ = 100_000;

    private static readonly string[] Kinds = ["app", "connection"], States = ["normal", "min", "max", "snapped"], Sides = ["bottom", "top", "left", "right"];
    private static readonly JsonWriterOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"^[0-9A-Za-z_\-]{8,64}$")] private static partial Regex DeviceIdPattern();
    [GeneratedRegex(@"^[0-9A-Za-z_\-:.]{1,64}$")] private static partial Regex IdPattern();      // Kennung eines Fensters oder Bildschirms (vom Browser vergeben)
    [GeneratedRegex(@"^[a-z0-9_\-]{1,30}$")] private static partial Regex Token();                // Name einer App, Art des Einrastens

    public static bool IsDeviceId(string? id) => id is not null && DeviceIdPattern().IsMatch(id);

    /// <summary>Der Anzeigename eines Geraets: gekuerzt, ohne Steuerzeichen; null wenn nichts uebrig bleibt oder er fehlt.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        var s = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        return s.Length > MaxNameLength ? s[..MaxNameLength].TrimEnd() : s;
    }

    /// <returns>Das gepruefte Dokument oder eine Fehlermeldung. Falsche Typen und Werte ausserhalb der Liste sind Fehler, Zahlen ausserhalb des Bereichs werden begrenzt.</returns>
    public static (Workspace? Doc, string? Error) Parse(JsonElement v)
    {
        try { return (ParseDoc(v), null); }
        catch (FormatException e) { return (null, e.Message); }
    }

    private static Workspace ParseDoc(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) throw new FormatException("Arbeitsplatz erwartet ein Objekt.");

        var screens = new List<WorkspaceScreen>();
        if (v.TryGetProperty("screens", out var sc) && sc.ValueKind == JsonValueKind.Array)
        {
            if (sc.GetArrayLength() > MaxScreens) throw new FormatException("Zu viele Bildschirme.");
            var ids = new HashSet<string>();
            foreach (var s in sc.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) throw new FormatException("Bildschirm ungültig.");
                var id = Id(s, "id");
                if (!ids.Add(id)) throw new FormatException("Bildschirmkennung doppelt.");
                screens.Add(new WorkspaceScreen(id, Rect(s)));
            }
        }

        var windows = new List<WorkspaceWindow>();
        if (v.TryGetProperty("windows", out var ws) && ws.ValueKind == JsonValueKind.Array)
        {
            if (ws.GetArrayLength() > MaxWindows) throw new FormatException("Zu viele Fenster.");
            var ids = new HashSet<string>();
            foreach (var w in ws.EnumerateArray())
            {
                if (w.ValueKind != JsonValueKind.Object) throw new FormatException("Fenster ungültig.");
                var id = Id(w, "id");
                if (!ids.Add(id)) throw new FormatException("Fensterkennung doppelt.");
                var kind = OneOf(w, "kind", Kinds);
                string? app = null; Guid? connection = null;
                if (kind == "app") app = TokenOf(w, "app");
                else connection = Guid.TryParse(Str(w, "connectionId", 36), out var g) ? g : throw new FormatException("Feld connectionId ist keine Kennung.");
                var snap = w.TryGetProperty("snap", out _) ? TokenOf(w, "snap") : null;
                WorkspaceRect? restore = w.TryGetProperty("restore", out var rs) && rs.ValueKind == JsonValueKind.Object ? Rect(rs) : null;
                windows.Add(new WorkspaceWindow(id, kind, app, connection, Rect(w), Num(w, "z", 0, MaxZ), OneOf(w, "state", States), snap, restore));
            }
        }

        string? focus = null;
        if (v.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.String)
        {
            focus = f.GetString();
            if (focus is null || !IdPattern().IsMatch(focus)) throw new FormatException("Feld focus ist ungültig.");
        }

        WorkspaceDock? dock = null;
        if (v.TryGetProperty("dock", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            bool? collapsed = d.TryGetProperty("collapsed", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? c.GetBoolean() : null;
            var side = d.TryGetProperty("side", out _) ? OneOf(d, "side", Sides) : null;
            var order = new List<string>();
            if (d.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Array)
            {
                if (o.GetArrayLength() > MaxDockOrder) throw new FormatException("Dock zu lang.");
                foreach (var e in o.EnumerateArray())
                    order.Add(e.ValueKind == JsonValueKind.String && e.GetString() is { } s && IdPattern().IsMatch(s) ? s : throw new FormatException("Eintrag im Dock ungültig."));
            }
            dock = new WorkspaceDock(collapsed, side, order);
        }
        return new Workspace(screens, windows, focus, dock);
    }

    /// <summary>Entfernt Fenster mit einer Verbindung, die dem Benutzer nicht (mehr) gehoert, und den Fokus auf ein fehlendes Fenster. So bleibt nichts Fremdes oder Totes stehen.</summary>
    public static Workspace Keep(Workspace w, IReadOnlySet<Guid> ownedConnections)
    {
        var windows = w.Windows.Where(x => x.ConnectionId is not { } c || ownedConnections.Contains(c)).ToList();
        return w with { Windows = windows, Focus = windows.Any(x => x.Id == w.Focus) ? w.Focus : null };
    }

    public static IEnumerable<Guid> ConnectionIds(Workspace w) => w.Windows.Where(x => x.ConnectionId is not null).Select(x => x.ConnectionId!.Value).Distinct();

    public static string Write(Workspace w)
    {
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms, Plain))
        {
            j.WriteStartObject();
            j.WritePropertyName("screens"); j.WriteStartArray();
            foreach (var s in w.Screens) { j.WriteStartObject(); j.WriteString("id", s.Id); WriteRect(j, s.Rect); j.WriteEndObject(); }
            j.WriteEndArray();
            j.WritePropertyName("windows"); j.WriteStartArray();
            foreach (var x in w.Windows)
            {
                j.WriteStartObject(); j.WriteString("id", x.Id); j.WriteString("kind", x.Kind);
                if (x.App is not null) j.WriteString("app", x.App);
                if (x.ConnectionId is { } c) j.WriteString("connectionId", c);
                WriteRect(j, x.Rect); j.WriteNumber("z", x.Z); j.WriteString("state", x.State);
                if (x.Snap is not null) j.WriteString("snap", x.Snap);
                if (x.Restore is { } r) { j.WritePropertyName("restore"); j.WriteStartObject(); WriteRect(j, r); j.WriteEndObject(); }
                j.WriteEndObject();
            }
            j.WriteEndArray();
            if (w.Focus is not null) j.WriteString("focus", w.Focus);
            if (w.Dock is { } d)
            {
                j.WritePropertyName("dock"); j.WriteStartObject();
                if (d.Collapsed is { } cl) j.WriteBoolean("collapsed", cl);
                if (d.Side is not null) j.WriteString("side", d.Side);
                j.WritePropertyName("order"); j.WriteStartArray();
                foreach (var o in d.Order) j.WriteStringValue(o);
                j.WriteEndArray(); j.WriteEndObject();
            }
            j.WriteEndObject();
        }
        var text = Encoding.UTF8.GetString(ms.ToArray());
        // Die Grenzen oben halten das Dokument weit darunter; die Pruefung bleibt als Sicherung, falls jemand eine Grenze anhebt
        return text.Length > MaxBytes ? throw new FormatException("Arbeitsplatz zu groß.") : text;
    }

    private static void WriteRect(Utf8JsonWriter j, WorkspaceRect r) { j.WriteNumber("x", r.X); j.WriteNumber("y", r.Y); j.WriteNumber("w", r.W); j.WriteNumber("h", r.H); }

    private static WorkspaceRect Rect(JsonElement o) => new(Num(o, "x", -CoordLimit, CoordLimit), Num(o, "y", -CoordLimit, CoordLimit), Num(o, "w", 1, SizeLimit), Num(o, "h", 1, SizeLimit));

    private static int Num(JsonElement o, string name, int min, int max)
    {
        if (!o.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var d) || double.IsNaN(d)) throw new FormatException($"Feld {name} fehlt oder ist keine Zahl.");
        return (int)Math.Clamp(Math.Round(d), min, max);   // Bruchteile (Pixeldichte) werden gerundet, zu grosse Werte begrenzt
    }

    private static string Str(JsonElement o, string name, int max)
    {
        if (!o.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) throw new FormatException($"Feld {name} fehlt.");
        var s = p.GetString() ?? "";
        if (s.Length > max) throw new FormatException($"Feld {name} ist zu lang.");
        return s;
    }

    private static string Id(JsonElement o, string name) => Str(o, name, 64) is var s && IdPattern().IsMatch(s) ? s : throw new FormatException($"Feld {name} ist ungültig.");
    private static string TokenOf(JsonElement o, string name) => Str(o, name, 30) is var s && Token().IsMatch(s) ? s : throw new FormatException($"Feld {name} ist ungültig.");
    private static string OneOf(JsonElement o, string name, string[] allowed) => Str(o, name, 12) is var s && allowed.Contains(s) ? s : throw new FormatException($"Feld {name} ist ungültig.");
}
