using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JulOS.Backend.Logic;

/// <summary>
/// Die reinen Regeln der Anmelde-Zentrale (docs/SIGN-IN.md): Adressen und Namen der Apps pruefen, Geheimnisse und Kennungen erzeugen, PKCE, Scopes, Rueckkehr-Adressen bauen.
/// Kein Zugriff auf Datenbank oder Netz; die Sicherheitsregeln stehen hier an einer Stelle und sind direkt testbar.
/// </summary>
public static partial class OidcRules
{
    public const string Openid = "openid", Profile = "profile";
    /// <summary>So lange gilt ein Code (hoechstens 60 Sekunden, RFC 6749 empfiehlt 10 Minuten als Obergrenze).</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    /// <summary>ID-Token und Zugriffs-Token: 5 Minuten. Die App haelt danach ihre eigene Sitzung.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LogoutTokenLifetime = TimeSpan.FromMinutes(2);
    public const string BackchannelEvent = "http://schemas.openid.net/event/backchannel-logout";

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")] private static partial Regex ChallengeShape();
    [GeneratedRegex("^[A-Za-z0-9._~-]{43,128}$")] private static partial Regex VerifierShape();
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")] private static partial Regex RoleShape();
    [GeneratedRegex("^[a-f0-9]{32}$")] private static partial Regex ClientIdShape();

    // ---- Kennungen und Geheimnisse ----

    public static string NewClientId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    public static bool LooksLikeClientId(string? s) => s is not null && ClientIdShape().IsMatch(s);

    /// <summary>Das Geheimnis einer App: 256 Bit Zufall. Es wird einmal gezeigt und nur als Hash gespeichert.</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Hash fuer Geheimnisse und Codes. SHA-256 genuegt: die Werte sind 256 Bit Zufall, ein Woerterbuch gibt es nicht.</summary>
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Vergleich in konstanter Zeit (beide Seiten sind gleich lange Hash-Texte).</summary>
    public static bool SecretMatches(string? secret, string storedHash)
    {
        var given = Encoding.ASCII.GetBytes(Hash(secret ?? ""));
        var stored = Encoding.ASCII.GetBytes(storedHash);
        return given.Length == stored.Length && CryptographicOperations.FixedTimeEquals(given, stored);
    }

    public static string NewCode() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string NewSid() => Base64Url(RandomNumberGenerator.GetBytes(18));
    public static string NewJti() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- PKCE (RFC 7636), nur S256 ----

    public static bool ChallengeOk(string? challenge) => challenge is not null && ChallengeShape().IsMatch(challenge);

    public static string S256(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Gehoert der Verifier zur Herausforderung? Form geprueft, Vergleich in konstanter Zeit.</summary>
    public static bool VerifierMatches(string? verifier, string challenge)
    {
        if (verifier is null || !VerifierShape().IsMatch(verifier)) return false;
        var a = Encoding.ASCII.GetBytes(S256(verifier));
        var b = Encoding.ASCII.GetBytes(challenge);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ---- Eingaben von aussen ----

    /// <summary>state und nonce: Text ohne Steuerzeichen, hoechstens 512 Zeichen. null = in Ordnung.</summary>
    public static bool OpaqueOk(string? s) => s is { Length: > 0 and <= 512 } && s.All(c => c >= ' ' && c != '\u007f');

    /// <summary>
    /// Die Scopes der Anfrage. openid ist Pflicht; profile wird gewaehrt; alles andere (zum Beispiel offline_access: es gibt keine Refresh-Token) wird ohne Fehler uebergangen
    /// (OIDC Core 3.1.2.1 erlaubt das). Gibt die gewaehrten Scopes zurueck, null wenn openid fehlt.
    /// </summary>
    public static string[]? GrantedScopes(string? scope)
    {
        if (scope is null || scope.Length > 512) return null;
        var asked = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!asked.Contains(Openid)) return null;
        return asked.Where(s => s is Openid or Profile).Distinct().ToArray();
    }

    // ---- Apps ----

    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 60 || name.Any(char.IsControl) ? "Name: 1 bis 60 Zeichen." : null;

    /// <summary>Rollen-Schluessel einer App: kurz, klein geschrieben (a-z, 0-9, Strich, Unterstrich), 1 bis 12 verschiedene.</summary>
    public static string? RolesProblem(string[]? roles)
    {
        if (roles is not { Length: > 0 and <= 12 }) return "Rollen: 1 bis 12 Schlüssel, zum Beispiel admin, member, viewer.";
        if (roles.Any(r => r is null || !RoleShape().IsMatch(r))) return "Rollen: nur a-z, 0-9, Strich und Unterstrich, bis 32 Zeichen, mit einem Buchstaben am Anfang.";
        if (roles.Distinct().Count() != roles.Length) return "Rollen: jeder Schlüssel nur einmal.";
        return null;
    }

    public static bool RoleOk(string? role) => role is not null && RoleShape().IsMatch(role);

    /// <summary>
    /// Eine Adresse, an die JulOS etwas schickt (Rueckkehr, Abmelden, Logout-Token). Absolut, https (http nur zu localhost, 127.0.0.1, ::1 und *.localhost fuer Entwicklung),
    /// ohne Benutzer:Passwort und ohne Fragment, hoechstens 300 Zeichen. Eine Rueckkehr-Adresse wird exakt verglichen, es gibt keine Platzhalter.
    /// </summary>
    public static string? UriProblem(string? value, bool allowQuery = true)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "Adresse fehlt oder ist zu lang.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return $"{Short(value)}: keine gültige Adresse.";
        if (uri.Scheme is not ("https" or "http")) return $"{Short(value)}: nur https (oder http zu localhost).";
        if (uri.Scheme == "http" && !IsLoopbackName(uri.Host)) return $"{Short(value)}: http nur zu localhost, sonst https.";
        if (uri.UserInfo.Length > 0) return $"{Short(value)}: ohne Benutzer und Passwort.";
        if (uri.Fragment.Length > 0 || value.Contains('#')) return $"{Short(value)}: ohne Fragment (#).";
        if (!allowQuery && uri.Query.Length > 0) return $"{Short(value)}: ohne Suchteil (?).";
        if (value.Contains('*')) return $"{Short(value)}: keine Platzhalter, die Adresse gilt genau so.";
        return null;
    }

    /// <summary>
    /// Die Adresse fuer das Abmelden (Logout-Token): wie jede Adresse, aber http nur, wenn der Aussteller selbst http-localhost ist (Entwicklung); eine IP-Adresse, die nie erlaubt ist
    /// (Loopback, Link-Local, Metadaten), oder eine im lokalen Netz ohne den Haken der App, wird schon beim Eintragen abgelehnt. Beim Verbinden prueft <see cref="OidcNet"/> noch einmal.
    /// </summary>
    public static string? BackchannelProblem(string? value, string? issuer, bool allowPrivate)
    {
        if (UriProblem(value) is { } p) return p;
        var policy = OidcNetPolicy.For(allowPrivate, issuer);
        if (value!.StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !policy.Dev) return $"{Short(value)}: nur https.";
        return OidcNet.LiteralProblem(value, policy) is { } lp ? $"{Short(value)}: {lp}" : null;
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..60] + "…";

    public static bool IsLoopbackName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));

    /// <summary>Prueft eine Liste von Adressen (je Eintrag <see cref="UriProblem"/>); doppelte entfallen. Gibt die bereinigte Liste oder den Fehler.</summary>
    public static (string[]? Uris, string? Error) CheckUris(string[]? values, bool required, int max, string label)
    {
        var list = (values ?? []).Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (required && list.Length == 0) return (null, $"{label}: mindestens eine Adresse.");
        if (list.Length > max) return (null, $"{label}: höchstens {max} Adressen.");
        foreach (var v in list) if (UriProblem(v) is { } p) return (null, $"{label}: {p}");
        return (list, null);
    }

    /// <summary>Die Adresse der Instanz (Aussteller): https oder, fuer Entwicklung, http zu localhost; ohne Suchteil, Fragment und abschliessenden Schraegstrich.</summary>
    public static (string? Issuer, string? Error) CheckIssuer(string? value)
    {
        var v = (value ?? "").Trim().TrimEnd('/');
        if (v.Length == 0) return (null, "Öffentliche Adresse fehlt.");
        if (UriProblem(v, allowQuery: false) is { } p) return (null, p);
        return (v, null);
    }

    // ---- Rueckkehr ----

    /// <summary>Haengt Felder an die Adresse an (der Suchteil einer registrierten Adresse bleibt erhalten). Werte werden kodiert.</summary>
    public static string WithQuery(string uri, params (string Key, string? Value)[] fields)
    {
        var sb = new StringBuilder(uri);
        sb.Append(uri.Contains('?') ? '&' : '?');
        var first = true;
        foreach (var (k, v) in fields)
        {
            if (v is null) continue;
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }
}
