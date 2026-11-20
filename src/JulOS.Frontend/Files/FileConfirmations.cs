using System.Security.Cryptography;
using System.Text;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;

namespace JulOS.Frontend.Files;

/// <summary>Required: das Haekchen oder der Name fehlt (die Bestaetigung bleibt), TypedMismatch: der Name stimmt nicht (sie bleibt auch dann).</summary>
internal enum ConfirmResult { Ok, Invalid, Expired, Mismatch, Required, TypedMismatch }

/// <summary>Was der Server beim Vorbereiten festgestellt und dem Benutzer gezeigt hat: es gibt einen Ordner mit Inhalt (rekursiv loeschen) oder Namen, die ersetzt wuerden. Nur dann darf die Ausfuehrung recursive oder overwrite verlangen.</summary>
internal sealed record ConfirmedWrite(string Action, bool NeedsRecursive, bool NeedsOverwrite, ConfirmInfo Confirm);

/// <summary>Was der Benutzer im zweiten Schritt bestaetigt hat: das Haekchen und/oder den getippten Namen.</summary>
internal sealed record ConfirmAnswer(bool Acknowledged = false, string? Typed = null);

/// <summary>
/// Die Bestaetigungen fuer schreibende Aktionen der Dateien-App (wie bei den Host-Aktionen, docs/FILES.md): <c>prepare</c> prueft alles und stellt eine kurzlebige (60 s), einmalige Bestaetigung aus, die an
/// Sitzung, Aktion und die genaue Anfrage gebunden ist (Verbindung, Quell-Pfade, Ziel, Name; die Anzahl steckt in den Pfaden), <c>run</c> loest sie ein. Im Speicher steht nur ihr SHA-256.
/// Aenderung der Pfade oder der Aktion, eine andere Sitzung, Wiederverwendung und Ablauf werden abgelehnt. Ein Hochladen mehrerer Dateien gilt fuer genau die vorbereiteten Namen, jeder einmal.
/// </summary>
internal sealed class FileConfirmations(TimeProvider time, TimeSpan lifetime)
{
    public const int MaxPerUser = 20;
    private sealed record Pending(Guid UserId, Guid SessionId, string Canon, string Action, bool NeedsRecursive, bool NeedsOverwrite, HashSet<string>? Names, ConfirmInfo Confirm, string? Expected, DateTimeOffset Expires);

    private readonly Dictionary<string, Pending> _pending = [];
    private readonly Lock _gate = new();

    public TimeSpan Lifetime => lifetime;

    /// <summary>Die genaue Anfrage als ein Text: Aktion, Verbindung, Ziel-Verbindung, Pfade (normalisiert), Ordner, Name. Pfade und Namen enthalten nie Steuerzeichen, das Trennzeichen kommt also nicht darin vor.</summary>
    public static string Canon(string action, Guid connection, Guid? targetConnection, IEnumerable<string> paths, string? dir, string? name) =>
        string.Join('\u001f', [action, connection.ToString("N"), targetConnection?.ToString("N") ?? "", string.Join('\u001e', paths), dir ?? "", name ?? ""]);

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Stellt eine Bestaetigung aus; null, wenn der Benutzer schon zu viele offene hat. <paramref name="names"/>: beim Hochladen die Dateinamen, die gelten. <paramref name="confirm"/>: die Stufe (ohne Angabe Routine);
    /// <paramref name="expected"/>: der Name, der bei <see cref="ConfirmDemand.Type"/> getippt werden muss.
    /// </summary>
    public string? Issue(SessionIdentity who, string canon, string action, bool needsRecursive, bool needsOverwrite, IEnumerable<string>? names = null, ConfirmInfo? confirm = null, string? expected = null)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            foreach (var (k, p) in _pending.ToList()) if (p.Expires <= now) _pending.Remove(k);
            if (_pending.Values.Count(p => p.UserId == who.UserId) >= MaxPerUser) return null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            _pending[Hash(token)] = new Pending(who.UserId, who.Id, canon, action, needsRecursive, needsOverwrite, names is null ? null : [.. names], confirm ?? new ConfirmInfo(ConfirmLevel.Routine, ConfirmDemand.None, null, null), expected, now + lifetime);
            return token;
        }
    }

    /// <summary>
    /// Loest eine Bestaetigung ein. Unbekannt oder schon benutzt: Invalid; abgelaufen: Expired; andere Sitzung, andere Aktion oder andere Anfrage: Mismatch (die Bestaetigung ist dann verbraucht).
    /// Beim Hochladen (<paramref name="uploadName"/>) wird nur der Name aus der Menge gestrichen, die Bestaetigung gilt fuer die uebrigen weiter.
    /// Verlangt die Stufe das Haekchen oder den Namen (<paramref name="answer"/>) und fehlt es, gibt es Required (oder TypedMismatch); die Bestaetigung bleibt dann gueltig bis zum Ablauf.
    /// </summary>
    public ConfirmResult Take(SessionIdentity who, string? token, string canon, string action, string? uploadName, out ConfirmedWrite? confirmed, ConfirmAnswer? answer = null)
    {
        confirmed = null;
        if (string.IsNullOrEmpty(token) || token.Length > 100) return ConfirmResult.Invalid;
        var key = Hash(token);
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var p)) return ConfirmResult.Invalid;
            if (p.Expires <= time.GetUtcNow()) { _pending.Remove(key); return ConfirmResult.Expired; }
            if (p.UserId != who.UserId || p.SessionId != who.Id || p.Action != action || p.Canon != canon) { _pending.Remove(key); return ConfirmResult.Mismatch; }
            if (p.Confirm.Demand == ConfirmDemand.Check && answer?.Acknowledged != true) return ConfirmResult.Required;
            if (p.Confirm.Demand == ConfirmDemand.Type)
            {
                if (string.IsNullOrWhiteSpace(answer?.Typed)) return ConfirmResult.Required;
                if (!string.Equals(answer.Typed.Trim(), p.Expected, StringComparison.OrdinalIgnoreCase)) return ConfirmResult.TypedMismatch;
            }
            if (p.Names is { } names)
            {
                if (uploadName is null || !names.Remove(uploadName)) { _pending.Remove(key); return ConfirmResult.Mismatch; }
                if (names.Count == 0) _pending.Remove(key);
            }
            else _pending.Remove(key);
            confirmed = new ConfirmedWrite(p.Action, p.NeedsRecursive, p.NeedsOverwrite, p.Confirm);
            return ConfirmResult.Ok;
        }
    }

    public int Count { get { lock (_gate) return _pending.Count; } }
}
