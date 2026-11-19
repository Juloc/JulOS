using System.Text;
using System.Text.Json;

namespace JulOS.Backend.Modules;

/// <summary>
/// Konfiguration als Text ohne Geheimnisse. JSON (Admin-API): Felder, deren Name nach Geheimnis klingt (Passwort, Token, Schluessel, Zugangsdaten, Hash, Zertifikat, E-Mail,
/// DNS-Challenge), werden durch *** ersetzt, ebenso alles unter "headers". Caddyfile: Zeilen mit solchen Woertern verlieren ihren Wert,
/// Bloecke von basic_auth und Verwandten werden ganz ersetzt. Im Zweifel wird zu viel verborgen, nie zu wenig. {env.NAME} steht so da, wie es geschrieben ist, und wird nie aufgeloest.
/// </summary>
public static class CaddyRedactor
{
    public const int MaxText = 256 * 1024;
    private static readonly string[] Words = ["pass", "secret", "token", "key", "auth", "credential", "hash", "salt", "cookie", "bearer", "private", "api", "cert", "email", "account", "challenge", "user"];

    public static bool Sensitive(string name)
    {
        var n = name.ToLowerInvariant();
        return Words.Any(n.Contains);
    }

    public static string Json(JsonElement config)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            Write(w, config, 0);
        return Clip(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static void Write(Utf8JsonWriter w, JsonElement e, int depth)
    {
        if (depth > 40) { w.WriteStringValue("…"); return; }
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    // Ein heikler Name verbirgt alles darunter (auch Objekte und Listen); "headers" gehoert dazu, weil Kopfzeilen oft Zugangsdaten tragen
                    if ((Sensitive(p.Name) || string.Equals(p.Name, "headers", StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind != JsonValueKind.Null) w.WriteStringValue("***");
                    else Write(w, p.Value, depth + 1);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var x in e.EnumerateArray()) Write(w, x, depth + 1);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    private static readonly string[] BlockStarts = ["basic_auth", "basicauth", "forward_auth", "jwt", "authenticate", "authorize", "security"];
    private static readonly string[] ValueWords = ["password", "passwd", "secret", "token", "api_key", "apikey", "key ", "acme_dns", "dns ", "email", "authorization", "bearer", "credentials"];

    public static string Caddyfile(string text)
    {
        var sb = new StringBuilder();
        var skipDepth = 0;   // > 0: wir sind in einem Block, dessen Inhalt ersetzt wird
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            var lower = trimmed.ToLowerInvariant();
            if (skipDepth > 0)
            {
                skipDepth += trimmed.Count(c => c == '{') - trimmed.Count(c => c == '}');
                if (skipDepth <= 0) { skipDepth = 0; sb.AppendLine(Indent(line) + "}"); }
                else if (trimmed.Length > 0) sb.AppendLine(Indent(line) + "***");
                continue;
            }
            if (lower.StartsWith('#')) { sb.AppendLine(line); continue; }
            if (BlockStarts.Any(b => lower.StartsWith(b)) && trimmed.EndsWith('{'))
            {
                sb.AppendLine(line);
                skipDepth = 1;
                continue;
            }
            var hit = ValueWords.Select(w => (w, i: lower.IndexOf(w, StringComparison.Ordinal))).Where(x => x.i >= 0).OrderBy(x => x.i).FirstOrDefault();
            if (hit.w is not null && (hit.i == 0 || !char.IsLetterOrDigit(lower[hit.i - 1])))
            {
                var keepTo = Math.Max(0, line.IndexOf(trimmed, StringComparison.Ordinal)) + hit.i + hit.w.TrimEnd().Length;
                sb.AppendLine(line[..Math.Min(keepTo, line.Length)] + " ***");
                continue;
            }
            sb.AppendLine(line);
        }
        return Clip(sb.ToString());
    }

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];
    private static string Clip(string s) => s.Length <= MaxText ? s : s[..MaxText] + "\n…";
}
