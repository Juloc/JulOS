namespace JulOS.Frontend.Auth;

/// <summary>
/// Welche Wege eine frische Freigabe mit dem zweiten Faktor verlangen (docs/SECURITY-REVIEW.md, F-04): alles, was Hosts oder Dateien aendert, den Zugang zu Agenten, das Konto
/// und die Sicherheits-Einstellungen selbst. Die Tabelle steht an einer Stelle und der Server prueft sie in <see cref="ApiGuard"/> vor dem Weg, nicht der Browser.
/// Lesen und reine Anzeigen fehlen mit Absicht. Bei Dateien und Aktionen reicht der erste Schritt (<c>prepare</c>): ohne ihn gibt es keine Bestaetigung fuer den zweiten.
/// Die zweistufige Bestaetigung bleibt davon unberuehrt (sie beweist Absicht, die Freigabe beweist, wer fragt).
/// </summary>
internal static class StepUpPolicy
{
    public const string Code = "step_up_required";

    // "*" passt auf genau ein Segment, "**" am Ende auf alles Weitere
    private static readonly (string Method, string Path)[] Rules =
    [
        ("POST", "api/hosts/*/actions/prepare"),
        ("POST", "api/files/remote/prepare"),
        ("PUT", "api/hosts/*/agent/control"),
        ("POST", "api/hosts/*/agent/code"),
        ("POST", "api/hosts/*/agent/update"),
        ("PUT", "api/hosts/*/agent/auto-update"),
        ("DELETE", "api/hosts/*/agent"),
        ("PUT", "api/hosts/*/login"),
        ("DELETE", "api/hosts/*/login"),
        ("POST", "api/auth/password"),
        ("POST", "api/auth/totp/begin"),
        // Passkeys: einen neuen anlegen (begin; finish ist an dessen Herausforderung gebunden) und einen entfernen. Umbenennen aendert nichts an der Sicherheit.
        ("POST", "api/auth/passkeys/register/begin"),
        ("DELETE", "api/auth/passkeys/*"),
        ("PUT", "api/security/**"),
        ("POST", "api/security/**"),
        ("DELETE", "api/security/**"),
        // KI-Zugang: Anbieter, Schluessel, Apps und Grenzen aendern (die API der Apps unter api/ai/v1 geht nie hierher, sie hat keine Sitzung)
        ("PUT", "api/ai/**"),
        ("POST", "api/ai/**"),
        ("DELETE", "api/ai/**"),
    ];

    // Die Vorschau der Zielhosts aendert nichts und braucht keine Freigabe
    private static readonly string[] Exempt = ["api/security/hosts/preview"];

    /// <summary>
    /// Wege, die die Freigabe auch dann verlangen, wenn der Schalter "zweiter Faktor fuer Aktionen" aus ist: die Verwaltung der Anmelde-Zentrale (Apps, Geheimnisse, Rueckkehr-Adressen, Rollen).
    /// Der Eigentuemer ist in jeder App admin; mit einem gestohlenen Cookie ohne diese Sperre liesse sich eine App umbiegen und dort als admin anmelden.
    /// Ebenso das Anlegen und Entfernen von Passkeys: ein neuer Passkey ist ein Zugang, der ein gestohlenes Cookie ueberlebt.
    /// </summary>
    public static bool Always(string method, PathString path) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && Requires(method, path)
        && (path.Value ?? "").Trim('/') is var p && (p.StartsWith("api/security/oidc", StringComparison.OrdinalIgnoreCase) || p.StartsWith("api/auth/passkeys/", StringComparison.OrdinalIgnoreCase));

    public static bool Requires(string method, PathString path)
    {
        var segments = (path.Value ?? "").Trim('/').Split('/');
        var joined = string.Join('/', segments);
        if (Exempt.Contains(joined, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (var (m, pattern) in Rules)
            if (string.Equals(m, method, StringComparison.OrdinalIgnoreCase) && Matches(pattern.Split('/'), segments)) return true;
        return false;
    }

    private static bool Matches(string[] pattern, string[] segments)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == "**") return segments.Length >= i;
            if (i >= segments.Length || (pattern[i] != "*" && !string.Equals(pattern[i], segments[i], StringComparison.OrdinalIgnoreCase))) return false;
        }
        return segments.Length == pattern.Length;
    }
}
