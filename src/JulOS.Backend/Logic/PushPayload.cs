using System.Text;
using System.Text.Json;

namespace JulOS.Backend.Logic;

/// <summary>Ein Eintrag der Push-Nachricht: Name des Hosts, Regel (security, disk, service, offline, reboot, test), Art (alert, ok) und Kennung des Hosts fuer den Sprung in die App.</summary>
public sealed record PushItem(string Host, string Rule, string Kind, Guid? HostId);

/// <summary>
/// Der Inhalt einer Push-Nachricht: nur Namen und Codes, keine Texte, keine Pfade, keine Ausgaben. Den lesbaren Text setzt der Service Worker auf dem Geraet in der Sprache des Geraets zusammen
/// (sw-push.js). So kennt auch der Push-Dienst (verschluesselt) und kein Log des Servers mehr als das.
/// </summary>
public static class PushPayload
{
    public const int MaxItems = 8, MaxName = 40;

    public static byte[] Build(IReadOnlyList<PushItem> items)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteNumber("n", items.Count);                       // wie viele es insgesamt sind (die Liste ist gekuerzt)
            w.WriteStartArray("items");
            foreach (var i in items.Take(MaxItems))
            {
                w.WriteStartObject();
                w.WriteString("h", Clean(i.Host));
                w.WriteString("r", i.Rule);
                w.WriteString("k", i.Kind);
                if (i.HostId is { } id) w.WriteString("id", id);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static string Clean(string name)
    {
        var s = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        return s.Length <= MaxName ? s : s[..MaxName];
    }
}
