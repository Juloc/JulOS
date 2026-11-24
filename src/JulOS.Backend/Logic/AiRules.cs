using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace JulOS.Backend.Logic;

/// <summary>Wo das Budget einer App steht: Verbrauch und Grenze je Tag und Monat, in Tokens (Grenze null: keine).</summary>
public sealed record AiBudget(long DayUsed, long? DayLimit, long MonthUsed, long? MonthLimit)
{
    /// <summary>"day" oder "month", wenn eine Grenze erreicht ist, sonst null.</summary>
    public string? Exceeded => DayLimit is { } d && DayUsed >= d ? "day" : MonthLimit is { } m && MonthUsed >= m ? "month" : null;

    /// <summary>"day" oder "month", wenn der Verbrauch 80 % einer Grenze erreicht hat (und sie noch nicht ueberschritten ist), sonst null.</summary>
    public string? Warning => Exceeded is not null ? null : DayLimit is { } d && DayUsed * 5 >= d * 4 ? "day" : MonthLimit is { } m && MonthUsed * 5 >= m * 4 ? "month" : null;
}

/// <summary>Reine Regeln des KI-Zugangs: App-Schluessel, erlaubte Modelle, Grenzen, Eingaben. Ohne Datenbank, einzeln pruefbar.</summary>
public static class AiRules
{
    public const string KeyPrefix = "jos_ai_";
    public static readonly string[] ProviderIds = ["codex", "openai", "anthropic"];
    /// <summary>Der Fake-Anbieter ist nur mit JULOS_AI_FAKE=on da; er ist kein Teil der festen Liste der Datenbank.</summary>
    public const string FakeId = "fake";
    public static readonly string[] Efforts = ["minimal", "low", "medium", "high", "xhigh"];

    public const int MaxNameLength = 60;
    public const int MaxRatePerMinute = 600;
    public const int DefaultMaxTokens = 1024;
    public const int MaxMaxTokens = 16384;
    public const int MaxMessages = 100;
    public const int MaxTextBytes = 256 * 1024;
    public const int MaxBodyTextOnly = 320 * 1024;
    public const int MaxBodyWithFiles = 12 * 1024 * 1024;
    public const int MaxResponseChars = 2_000_000;
    public const int MaxConcurrentPerApp = 2;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

    /// <summary>Ein neuer Schluessel: Vorsatz und 256 Zufallsbit (URL-sicher kodiert). Er wird nur einmal angezeigt.</summary>
    public static string NewKey() => KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 des Schluessels: nur das wird gespeichert. Der Schluessel hat 256 Bit Zufall, ein schneller Hash genuegt (nichts zu erraten).</summary>
    public static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static string KeyHint(string key) => key.Length <= 4 ? "" : key[^4..];

    /// <summary>Der Schluessel aus "Authorization: Bearer ...", sonst null.</summary>
    public static string? BearerKey(string? header)
    {
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var key = header[7..].Trim();
        return key.Length is > 0 and <= 200 ? key : null;
    }

    /// <summary>Der Schluessel einer App unter allen gespeicherten Hashes: jeder wird verglichen, ohne frueher aufzuhoeren, damit die Dauer nichts verraet.</summary>
    public static T? Match<T>(byte[] hash, IEnumerable<(byte[] Hash, T Item)> candidates) where T : class
    {
        T? found = null;
        foreach (var (h, item) in candidates)
            if (CryptographicOperations.FixedTimeEquals(hash, h)) found = item;
        return found;
    }

    /// <summary>Ob eine Modell-Kennung "anbieter/modell" zu einem erlaubten Eintrag passt ("anbieter" erlaubt alle seine Modelle).</summary>
    public static bool Allows(IEnumerable<string> allowed, string modelId)
    {
        var slash = modelId.IndexOf('/');
        if (slash <= 0 || slash == modelId.Length - 1) return false;
        var provider = modelId[..slash];
        return allowed.Any(a => string.Equals(a, provider, StringComparison.Ordinal) || string.Equals(a, modelId, StringComparison.Ordinal));
    }

    public static bool TrySplitModel(string? id, out string provider, out string model)
    {
        provider = model = "";
        if (id is null || id.Length > 200) return false;
        var slash = id.IndexOf('/');
        if (slash <= 0 || slash == id.Length - 1) return false;
        provider = id[..slash]; model = id[(slash + 1)..];
        return model.All(c => !char.IsControl(c) && c != ' ');
    }

    /// <summary>Prueft die Liste der erlaubten Anbieter oder Modelle; gibt die bereinigte Liste oder einen Fehlertext zurueck.</summary>
    public static (string[]? Allowed, string? Error) ValidateAllowed(string[]? input, bool fakeEnabled)
    {
        if (input is null || input.Length == 0) return (null, "Mindestens einen Anbieter erlauben.");
        var list = new List<string>();
        foreach (var raw in input)
        {
            var a = raw.Trim();
            var provider = a.Contains('/') ? a[..a.IndexOf('/')] : a;
            var known = ProviderIds.Contains(provider) || fakeEnabled && provider == FakeId;
            if (!known || a.Length > 200 || a.Contains('/') && !TrySplitModel(a, out _, out _)) return (null, "Unbekannter Anbieter oder Modell: " + AiTextShort(a));
            if (!list.Contains(a)) list.Add(a);
        }
        return list.Count > 40 ? (null, "Zu viele Einträge.") : ([.. list], null);
    }

    private static string AiTextShort(string s) => s.Length <= 40 ? s : s[..40] + "…";

    public static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Ein Name fehlt." : name.Trim().Length > MaxNameLength ? $"Der Name darf höchstens {MaxNameLength} Zeichen haben." : name.Any(char.IsControl) ? "Der Name enthält Steuerzeichen." : null;

    public static string? ValidateLimits(long? daily, long? monthly, int rate)
    {
        if (daily is <= 0 || monthly is <= 0) return "Ein Budget muss größer als 0 sein (leer lassen für keine Grenze).";
        if (daily is { } d && monthly is { } m && d > m) return "Das Tagesbudget darf nicht größer sein als das Monatsbudget.";
        return rate is < 1 or > MaxRatePerMinute ? $"Das Ratenlimit muss zwischen 1 und {MaxRatePerMinute} Anfragen pro Minute liegen." : null;
    }

    public static bool ValidEffort(string? effort) => effort is null || Efforts.Contains(effort);

    /// <summary>Schaetzung fuer ein Bild in Tokens (pauschal); der Anbieter nennt sie erst, wenn er antwortet.</summary>
    public const int TokensPerImage = 1000;

    /// <summary>
    /// Was gebucht wird: das Groessere aus der Angabe des Anbieters und der eigenen Schaetzung, je fuer Eingabe und Ausgabe. Fehlt die Angabe, ist sie 0 oder heisst bei einer anderen
    /// Version anders, greift die Schaetzung; sie ist die Untergrenze, nicht nur der Notbehelf. Bricht der Aufrufer ab und der Anbieter nannte keine Ausgabe (Denkmodelle rechnen auch
    /// ohne sichtbaren Text), gilt mindestens maxTokens als Ausgabe: ein Abbruch kommt nicht billiger als ein Ende.
    /// </summary>
    public static (long In, long Out) Book(long reportedIn, long reportedOut, long estimatedIn, long estimatedOut, bool aborted, int maxTokens)
    {
        var tin = Math.Max(Math.Max(0, reportedIn), estimatedIn);
        var tout = Math.Max(Math.Max(0, reportedOut), estimatedOut);
        if (aborted && reportedOut <= 0) tout = Math.Max(tout, maxTokens);
        return (tin, tout);
    }
}

/// <summary>
/// Bremse fuer falsche Schluessel: zaehlt Fehlversuche je Adresse in einem Zeitfenster; ab <see cref="Max"/> wird die Adresse gesperrt, bis das Fenster um ist, und zwar BEVOR ein Schluessel
/// geprueft wird (sonst koennte ein richtiger Treffer trotz Sperre durchgehen und die Grenze waere keine). Auch eine Anfrage ganz ohne Schluessel zaehlt.
/// </summary>
public sealed class AiAuthLimiter(TimeProvider time, int max = 20, TimeSpan? window = null)
{
    public int Max { get; } = max;
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _fails = new();

    public bool Blocked(string client)
    {
        if (!_fails.TryGetValue(client, out var f)) return false;
        if (time.GetUtcNow() - f.Start >= _window) { _fails.TryRemove(client, out _); return false; }
        return f.Count >= Max;
    }

    /// <summary>Wie viele Adressen die Tabelle hoechstens merkt; mehr gleichzeitig falsche Adressen werden nicht mehr einzeln gezaehlt (der Speicher waechst nicht ohne Grenze).</summary>
    public const int MaxTracked = 10_000;
    private DateTimeOffset _lastSweep;

    public int Tracked => _fails.Count;

    public void Fail(string client)
    {
        var now = time.GetUtcNow();
        if (!_fails.ContainsKey(client) && _fails.Count >= MaxTracked)
        {
            // Voll: hoechstens einmal je Fenster aufraeumen (nicht bei jedem Fehlversuch die ganze Tabelle ablaufen), sonst die neue Adresse nicht merken
            if (now - _lastSweep < _window) return;
            _lastSweep = now;
            foreach (var (k, v) in _fails) if (now - v.Start >= _window) _fails.TryRemove(k, out _);
            if (_fails.Count >= MaxTracked) return;
        }
        _fails.AddOrUpdate(client, _ => (now, 1), (_, old) => now - old.Start >= _window ? (now, 1) : (old.Start, old.Count + 1));
    }
}

/// <summary>Gleichzeitige Anfragen je App begrenzen.</summary>
public sealed class AiConcurrency(int perApp)
{
    private readonly ConcurrentDictionary<Guid, int> _active = new();

    public bool TryEnter(Guid app)
    {
        var ok = false;
        _active.AddOrUpdate(app, _ => { ok = true; return 1; }, (_, n) => { ok = n < perApp; return ok ? n + 1 : n; });
        return ok;
    }

    public void Leave(Guid app) => _active.AddOrUpdate(app, 0, (_, n) => Math.Max(0, n - 1));
}
