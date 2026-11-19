using System.Text.Json;

namespace JulOS.Backend.Modules;

public sealed record LogEntry(DateTimeOffset Time, string Host, string Method, string Path, int Status, int Ms, long Size);
public sealed record HostCount(string Host, int Requests, int Errors4xx, int Errors5xx);
/// <summary>Available: false, wenn die Datei nicht eingestellt oder nicht lesbar ist (Warnings sagt nicht warum im Detail, nur dass).</summary>
public sealed record CaddyLog(bool Available, LogEntry[] Entries, HostCount[] Hosts, int Total, string? Warning);

/// <summary>
/// Letzte Zeilen des Zugriffsprotokolls von Caddy (JSON-Zeilen, JULOS_CADDY_LOG_FILE, nur gelesen): die letzten 512 KB der Datei, daraus die neuesten 100 Eintraege und Zaehler
/// je Host. Nur Zeit, Host, Methode, Pfad ohne Query, Status, Dauer und Groesse werden uebernommen: keine Adressen von Besuchern, keine Kopfzeilen, keine Query-Parameter.
/// </summary>
public static class CaddyAccessLog
{
    public const int TailBytes = 512 * 1024, MaxEntries = 100, MaxHosts = 20;

    public static async Task<CaddyLog> ReadAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return new CaddyLog(false, [], [], 0, null);
        try
        {
            await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, f.Length - TailBytes);
            f.Seek(start, SeekOrigin.Begin);
            using var r = new StreamReader(f);
            var text = await r.ReadToEndAsync(ct);
            return Parse(text, dropFirst: start > 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new CaddyLog(false, [], [], 0, "unreadable"); }
    }

    public static CaddyLog Parse(string text, bool dropFirst = false)
    {
        var lines = text.Split('\n');
        var entries = new List<LogEntry>();
        for (var i = dropFirst ? 1 : 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            if (Entry(line) is { } e) entries.Add(e);
        }
        var hosts = entries.GroupBy(e => e.Host).Select(g => new HostCount(g.Key, g.Count(), g.Count(e => e.Status is >= 400 and < 500), g.Count(e => e.Status >= 500)))
            .OrderByDescending(h => h.Requests).Take(MaxHosts).ToArray();
        var newest = entries.OrderByDescending(e => e.Time).Take(MaxEntries).ToArray();
        return new CaddyLog(true, newest, hosts, entries.Count, null);
    }

    private static LogEntry? Entry(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (!o.TryGetProperty("request", out var req) || req.ValueKind != JsonValueKind.Object || !o.TryGetProperty("status", out var st) || !st.TryGetInt32(out var status)) return null;
            var host = req.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()! : "";
            var method = req.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
            var uri = req.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
            var cut = uri.IndexOfAny(['?', '#']);
            if (cut >= 0) uri = uri[..cut];
            var ts = o.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0;
            var dur = o.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            var size = o.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            return new LogEntry(DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)), CaddyRedact.Clip(host), CaddyRedact.Clip(method), CaddyRedact.Clip(uri), status, (int)Math.Round(dur * 1000), size);
        }
        catch (JsonException) { return null; }
    }
}
