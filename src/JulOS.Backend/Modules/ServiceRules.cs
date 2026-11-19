using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JulOS.Backend.Modules;

/// <summary>Eine Zeile der Kennzahlen: Feldpfad in der JSON-Antwort, Beschriftung, Einheit und Darstellung (plain, bytes, rate, percent).</summary>
public sealed record ServiceMetric(string Path, string Label, string Unit, string Format);

/// <summary>Ein gespeicherter Dienst. Der Schluessel ist nie dabei, nur ob es einen gibt.</summary>
public sealed record ServiceRecord(Guid Id, string Name, string Icon, string OpenUrl, string HealthUrl, string MetricsUrl, ServiceMetric[] Metrics, int IntervalSeconds, string HeaderName, bool HasKey, string Template);

/// <summary>Das, was der Benutzer eingibt (schon geprueft und bereinigt, siehe <see cref="ServiceRules.Validate"/>).</summary>
public sealed record ServiceInput(string Name, string Icon, string OpenUrl, string HealthUrl, string MetricsUrl, ServiceMetric[] Metrics, int IntervalSeconds, string HeaderName, string Template);

/// <summary>Der Wert einer Kennzahl. Number gibt es nur fuer Zahlen, Text fuer Zeichenketten und Wahrheitswerte; fehlt beides, gab es den Pfad nicht.</summary>
public sealed record MetricValue(string Label, string Unit, string Format, double? Number, string? Text);

/// <summary>State: up (gruen), down (rot), unknown (grau). Error: ein fester Kurzcode (timeout, refused, status, notAllowed, tooLarge, invalid), nie ein Text vom Dienst.</summary>
public sealed record ServiceStatus(Guid Id, string State, int? Ms, int? HttpStatus, DateTimeOffset CheckedAt, string? Error, MetricValue[] Metrics);

/// <summary>Pruefung der Eingaben fuer Dienste: Namen, Adressen, Symbole, Kennzahlen, Intervall und Kopfzeile des Schluessels.</summary>
public static class ServiceRules
{
    public const int MaxPerUser = 30, MaxMetrics = 12, MinInterval = 10, MaxInterval = 3600, DefaultInterval = 60;
    public static readonly string[] Icons = ["globe", "server", "box", "cloud", "activity", "monitor", "folder", "lock", "terminal", "shield", "grid", "key"];
    public static readonly string[] Formats = ["plain", "bytes", "rate", "percent"];

    private static readonly Regex HeaderToken = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "host", "content-length", "transfer-encoding", "connection", "upgrade", "te", "expect", "trailer", "proxy-authorization", "cookie", "x-julos" };
    private static readonly Regex PathText = new(@"^[\p{L}\p{N}_#\-\.\[\]]{1,120}$", RegexOptions.Compiled);

    /// <summary>Gibt die bereinigte Eingabe zurueck, oder einen festen Fehlertext (deutsch, die Oberflaeche uebersetzt ihn).</summary>
    public static (ServiceInput? Input, string? Error) Validate(string? name, string? icon, string? openUrl, string? healthUrl, string? metricsUrl,
        IReadOnlyList<ServiceMetric>? metrics, int? interval, string? headerName, string? template, Func<string, bool> hostAllowed)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) return (null, "Der Name muss 1 bis 60 Zeichen haben.");
        icon = string.IsNullOrWhiteSpace(icon) ? "globe" : icon.Trim();
        if (!Icons.Contains(icon)) return (null, "Unbekanntes Symbol.");

        var open = (openUrl ?? "").Trim();
        if (open.Length > 0 && !IsHttpUrl(open)) return (null, "Die Adresse ist ungültig (http:// oder https://, ohne Benutzer und Passwort).");
        var health = (healthUrl ?? "").Trim();
        var metricsAddress = (metricsUrl ?? "").Trim();
        foreach (var u in new[] { health, metricsAddress })
        {
            if (u.Length == 0) continue;
            if (!IsHttpUrl(u)) return (null, "Die Adresse ist ungültig (http:// oder https://, ohne Benutzer und Passwort).");
            if (!hostAllowed(new Uri(u).Host)) return (null, "Dieses Ziel ist nicht erlaubt.");
        }

        var list = new List<ServiceMetric>();
        foreach (var m in metrics ?? [])
        {
            var path = (m.Path ?? "").Trim();
            var label = (m.Label ?? "").Trim();
            var unit = (m.Unit ?? "").Trim();
            var format = string.IsNullOrWhiteSpace(m.Format) ? "plain" : m.Format.Trim();
            if (path.Length == 0 && label.Length == 0) continue;
            if (!PathText.IsMatch(path) || Segments(path) is null) return (null, "Ein Feldpfad ist ungültig.");
            if (label.Length is < 1 or > 40 || label.Any(char.IsControl)) return (null, "Eine Beschriftung muss 1 bis 40 Zeichen haben.");
            if (unit.Length > 12 || unit.Any(char.IsControl)) return (null, "Eine Einheit darf höchstens 12 Zeichen haben.");
            if (!Formats.Contains(format)) return (null, "Eine Darstellung ist unbekannt.");
            list.Add(new ServiceMetric(path, label, unit, format));
        }
        if (list.Count > MaxMetrics) return (null, "Zu viele Kennzahlen.");
        if (list.Count > 0 && metricsAddress.Length == 0) return (null, "Für Kennzahlen fehlt die Adresse der Kennzahlen.");

        var seconds = interval ?? DefaultInterval;
        if (seconds is < MinInterval or > MaxInterval) return (null, "Das Intervall muss zwischen 10 und 3600 Sekunden liegen.");

        var header = (headerName ?? "").Trim();
        if (header.Length > 0 && (!HeaderToken.IsMatch(header) || ForbiddenHeaders.Contains(header))) return (null, "Der Name der Kopfzeile ist ungültig.");

        var tpl = (template ?? "").Trim();
        if (tpl.Length > 24 || !tpl.All(c => char.IsLetterOrDigit(c) || c == '-')) tpl = "";
        return (new ServiceInput(name, icon, open, health, metricsAddress, [.. list], seconds, header, tpl), null);
    }

    public static bool IsHttpUrl(string text) =>
        text.Length <= 500 && Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Host.Length > 0 && u.UserInfo.Length == 0
        && !text.Any(char.IsControl) && !text.Contains(' ');

    /// <summary>Zerlegt "a.b[0].c" in ["a","b","0","c"]; null, wenn der Pfad leer ist oder zu viele Teile hat.</summary>
    public static string[]? Segments(string path)
    {
        var parts = path.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is >= 1 and <= 12 ? parts : null;
    }

    /// <summary>Liest einen Wert aus der JSON-Antwort. Teil "#" heisst: Anzahl der Eintraege (Feld oder Liste). Null, wenn es den Pfad nicht gibt.</summary>
    public static MetricValue? Extract(JsonElement root, ServiceMetric metric)
    {
        if (Segments(metric.Path) is not { } segments) return null;
        var cur = root;
        foreach (var seg in segments)
        {
            if (seg == "#")
            {
                var n = cur.ValueKind == JsonValueKind.Array ? cur.GetArrayLength() : cur.ValueKind == JsonValueKind.Object ? cur.EnumerateObject().Count() : -1;
                return n < 0 ? null : new MetricValue(metric.Label, metric.Unit, metric.Format, n, null);
            }
            if (cur.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(seg, out var i) || i < 0 || i >= cur.GetArrayLength()) return null;
                cur = cur[i];
            }
            else if (cur.ValueKind == JsonValueKind.Object)
            {
                if (!cur.TryGetProperty(seg, out cur)) return null;
            }
            else return null;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.Number => cur.TryGetDouble(out var d) ? new MetricValue(metric.Label, metric.Unit, metric.Format, d, null) : null,
            JsonValueKind.String => new MetricValue(metric.Label, metric.Unit, metric.Format, null, Clip(cur.GetString() ?? "")),
            JsonValueKind.True => new MetricValue(metric.Label, metric.Unit, metric.Format, null, "true"),
            JsonValueKind.False => new MetricValue(metric.Label, metric.Unit, metric.Format, null, "false"),
            _ => null,   // Objekte, Listen und null sind keine Kennzahl
        };
    }

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
