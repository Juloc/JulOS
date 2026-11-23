using System.Text.Json;
using System.Text.RegularExpressions;

namespace JulOS.Integrations.Ai;

public sealed record CodexPlanWindow(double UsedPercent, int? Minutes, DateTimeOffset? ResetsAt);

/// <summary>Das Kontingent des ChatGPT-Tarifs, wie Codex selbst es nennt (Prozent je Zeitfenster).</summary>
public sealed record CodexPlanUsage(string? Plan, CodexPlanWindow[] Windows);

/// <summary>
/// Was an der Sprache von `codex app-server` (JSON-Zeilen) und der Ausgabe des Programms reine Textarbeit ist; ohne Prozess, daher einzeln pruefbar.
/// Die Form der Nachrichten folgt dem, was die Projekte des Eigentuemers (Jularr, FullWorth, JuNotes) heute gegen das echte Programm benutzen.
/// </summary>
public static partial class CodexProtocol
{
    public const int MethodNotFound = -32601;

    public static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static long Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) && n > 0 ? n : 0;

    public static AiModel[] ParseModels(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<AiModel>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            var id = Text(entry, "model") ?? Text(entry, "id");
            if (id is null) continue;
            var efforts = entry.TryGetProperty("supportedReasoningEfforts", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : Text(e, "reasoningEffort")).OfType<string>().ToArray() : [];
            models.Add(new AiModel(id, Text(entry, "displayName") ?? id, efforts, Text(entry, "defaultReasoningEffort")));
        }
        return [.. models];
    }

    public static CodexPlanUsage? ParseUsage(JsonElement result)
    {
        // Der eigene Eimer des Tarifs ("codex"), wenn die Antwort mehrere hat, sonst der einzige
        JsonElement bucket = default;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            foreach (var property in byId.EnumerateObject())
                if (bucket.ValueKind == JsonValueKind.Undefined || property.Name == "codex") bucket = property.Value;
        if (bucket.ValueKind != JsonValueKind.Object && !(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimits", out bucket) && bucket.ValueKind == JsonValueKind.Object)) return null;
        var windows = new List<CodexPlanWindow>();
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number) continue;
            var minutes = w.TryGetProperty("windowDurationMins", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var v) && v > 0 ? v : (int?)null;
            var reset = w.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : (DateTimeOffset?)null;
            windows.Add(new CodexPlanWindow(Math.Clamp(used.GetDouble(), 0, 100), minutes, reset));
        }
        return new CodexPlanUsage(Text(bucket, "planType"), [.. windows]);
    }

    /// <summary>Das Limit des Tarifs ("You've hit your usage limit ...") als eigener Fehler, sonst null.</summary>
    public static AiProviderException? PlanLimitIn(string? message) =>
        message is not null && message.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            ? new AiProviderException("plan_limit", "Das Limit des ChatGPT-Tarifs ist erreicht.") : null;

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Ansi();

    public static string StripAnsi(string line) => Ansi().Replace(line, "").Trim();

    /// <summary>Die Adresse aus einer Zeile der Geraete-Anmeldung, nur von OpenAIs eigenem Host und nur https, sonst null.</summary>
    public static Uri? LoginAddress(string line)
    {
        var m = Regex.Match(StripAnsi(line), @"https://\S+", RegexOptions.None, TimeSpan.FromMilliseconds(500));
        return m.Success && Uri.TryCreate(m.Value, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0
            && (u.Host == "openai.com" || u.Host.EndsWith(".openai.com", StringComparison.Ordinal)) ? u : null;
    }

    /// <summary>Der Code aus einer Zeile ("ABCD-12345"), sonst null.</summary>
    public static string? LoginCode(string line)
    {
        var text = StripAnsi(line);
        return Regex.IsMatch(text, @"^[A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+$", RegexOptions.None, TimeSpan.FromMilliseconds(500)) ? text : null;
    }
}
