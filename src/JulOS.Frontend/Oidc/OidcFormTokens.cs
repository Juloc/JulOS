using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace JulOS.Frontend.Oidc;

/// <summary>
/// Einmal-Zeichen fuer das Formular "Bei JulOS abmelden?" (docs/SIGN-IN.md). Es gehoert zu EINER Sitzung, gilt 10 Minuten und genau einmal. Ein Formular, das nur der Server
/// ausgeben kann, ist der sichere Beweis, dass der POST von dieser Seite kommt; die Origin-Kopfzeile allein ist es nicht (Chromium schickt bei "Referrer-Policy: no-referrer"
/// "Origin: null"). Nur im Speicher: nach einem Neustart fragt die Seite eben noch einmal.
/// </summary>
internal sealed class OidcFormTokens(TimeProvider clock)
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(10);
    private const int MaxOpen = 2000;
    private readonly ConcurrentDictionary<string, (Guid Session, DateTimeOffset Until)> _open = new();

    public string Issue(Guid sessionId)
    {
        var now = clock.GetUtcNow();
        if (_open.Count >= MaxOpen) foreach (var (k, v) in _open) if (v.Until <= now) _open.TryRemove(k, out _);
        if (_open.Count >= MaxOpen) _open.Clear();           // Notbremse: lieber eine Rueckfrage mehr als ein wachsender Speicher
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _open[token] = (sessionId, now + Life);
        return token;
    }

    /// <summary>Verbraucht das Zeichen (auch wenn es zu einer anderen Sitzung gehoerte: wer es raet, hat es verbrannt).</summary>
    public bool Take(string? token, Guid sessionId)
    {
        if (string.IsNullOrEmpty(token) || !_open.TryRemove(token, out var e)) return false;
        return e.Session == sessionId && e.Until > clock.GetUtcNow();
    }
}
