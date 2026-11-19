using System.Text.Json;

namespace JulOS.Backend.Modules;

/// <summary>
/// Liest aus der Antwort von GET /config/apps/http/servers (laufende Konfiguration, von Caddy aus dem Caddyfile erzeugt) die Zeilen
/// "Domain -> Ziel". Wie beim Caddyfile gilt: nur Hosts, Pfade und Ziele werden uebernommen, nichts sonst.
/// </summary>
public static class CaddyAdminConfig
{
    public static CaddyRoute[] Parse(JsonElement servers)
    {
        var routes = new List<CaddyRoute>();
        if (servers.ValueKind != JsonValueKind.Object) return [];
        foreach (var server in servers.EnumerateObject())
            if (server.Value.ValueKind == JsonValueKind.Object && server.Value.TryGetProperty("routes", out var r))
                Walk(r, null, null, routes, 0);
        return [.. routes.Take(CaddyfileParser.MaxRoutes)];
    }

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Walk(JsonElement routes, string[]? hosts, string? path, List<CaddyRoute> result, int depth)
    {
        if (routes.ValueKind != JsonValueKind.Array || depth > CaddyfileParser.MaxDepth) return;
        foreach (var route in routes.EnumerateArray())
        {
            if (result.Count >= CaddyfileParser.MaxRoutes) return;
            if (route.ValueKind != JsonValueKind.Object) continue;
            var h = hosts; var p = path;
            if (route.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Array)
            {
                var hs = match.EnumerateArray().SelectMany(m => Strings(m, "host")).Distinct().ToArray();
                if (hs.Length > 0) h = hs;
                p = match.EnumerateArray().SelectMany(m => Strings(m, "path")).FirstOrDefault() ?? p;
            }
            if (!route.TryGetProperty("handle", out var handle) || handle.ValueKind != JsonValueKind.Array) continue;
            var domains = (h is { Length: > 0 } ? h : ["*"]).Select(CaddyRedact.Clip).ToArray();
            foreach (var handler in handle.EnumerateArray())
            {
                if (handler.ValueKind != JsonValueKind.Object || !handler.TryGetProperty("handler", out var kind) || kind.ValueKind != JsonValueKind.String) continue;
                switch (kind.GetString())
                {
                    case "subroute":
                        if (handler.TryGetProperty("routes", out var inner)) Walk(inner, domains, p, result, depth + 1);
                        break;
                    case "reverse_proxy":
                    {
                        var targets = handler.TryGetProperty("upstreams", out var ups) && ups.ValueKind == JsonValueKind.Array
                            ? ups.EnumerateArray().Select(u => Str(u, "dial") ?? Str(u, "lookup_srv")).OfType<string>().Select(CaddyRedact.Target).Distinct().ToArray()
                            : [];
                        Emit(result, domains, p, "proxy", targets);
                        break;
                    }
                    case "file_server":
                        Emit(result, domains, p, "files", []);
                        break;
                    case "static_response":
                    {
                        var location = handler.TryGetProperty("headers", out var hd) && hd.ValueKind == JsonValueKind.Object ? Strings(hd, "Location").FirstOrDefault() : null;
                        var code = handler.TryGetProperty("status_code", out var sc) ? sc.ToString() : null;
                        if (location is not null) Emit(result, domains, p, "redirect", [CaddyRedact.Target(location)]);
                        else Emit(result, domains, p, "respond", [], code is { Length: 3 } ? code : null);
                        break;
                    }
                }
            }
        }
    }

    private static void Emit(List<CaddyRoute> result, string[] domains, string? path, string kind, string[] targets, string? detail = null)
    {
        foreach (var d in domains)
            if (result.Count < CaddyfileParser.MaxRoutes) result.Add(new CaddyRoute(d, path is null ? null : CaddyRedact.Clip(path), kind, targets, detail));
    }
}
