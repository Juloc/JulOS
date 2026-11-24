using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JulOS.Backend.Logic;

/// <summary>Wo Passkeys gelten: RP-Id (nur der Rechnername) und Ursprung (Schema, Name, Port). Beides kommt aus der oeffentlichen Adresse der Anmelde-Zentrale, nie aus einer Anfrage.</summary>
public sealed record PasskeySite(string RpId, string Origin);

/// <summary>Reine Regeln der Passkeys (docs/PASSKEYS.md): Grenzen, Namen, Herausforderungen, amr. Die WebAuthn-Pruefung selbst macht die Bibliothek hinter <see cref="IPasskeys"/>.</summary>
public static class PasskeyRules
{
    public const int MaxPerUser = 20, NameMax = 60, ChallengeBytes = 32;
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// So viele offene Herausforderungen der Anmeldung gibt es hoechstens, je Adresse und insgesamt (sie sind oeffentlich anforderbar). Ist die Grenze erreicht, verdraengt die neue die
    /// aeltesten (je Adresse die dieser Adresse, insgesamt die aeltesten ueberhaupt) statt abzulehnen: so kann ein Schwall die Anmeldung der anderen nicht sperren.
    /// </summary>
    public const int MaxOpenLoginChallenges = 2000, MaxOpenLoginChallengesPerAddress = 10;

    /// <summary>Die Passkey-Strecke je Adresse: so viele Optionen- und Pruefanfragen je Minute.</summary>
    public const int RequestsPerMinute = 20;

    public const string Password = "password", Passkey = "passkey";

    /// <summary>
    /// Die Adresse, an die Passkeys gebunden sind, aus der oeffentlichen Adresse (Aussteller). RP-Id muss ein Rechnername sein: eine IP-Adresse kann WebAuthn nicht (der Browser lehnt sie ab),
    /// darum gibt es dann keine Passkeys (null). Ein Pfad in der Adresse zaehlt nicht zum Ursprung.
    /// </summary>
    public static PasskeySite? SiteFrom(string? issuer)
    {
        if (issuer is null || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns) return null;
        return new PasskeySite(uri.IdnHost.ToLowerInvariant(), uri.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>Der User-Handle: 16 Byte der Konto-Kennung. Nichts, was den Namen verraet; er gehoert genau einem Konto und wird beim Anmelden gegen den Besitzer der Credential-Id geprueft.</summary>
    public static byte[] UserHandle(Guid userId) => userId.ToByteArray();

    public static bool HandleMatches(byte[]? handle, Guid userId) => handle is not null && CryptographicOperations.FixedTimeEquals(handle, UserHandle(userId));

    public static byte[] ChallengeHash(byte[] challenge) => SHA256.HashData(challenge);

    /// <summary>Der Name eines Passkeys: ohne Steuerzeichen, Leerraum zusammengefasst, 1 bis 60 Zeichen. null: unbrauchbar.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        var clean = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length is >= 1 and <= NameMax ? clean : null;
    }

    /// <summary>
    /// Ob ein Passkey entfernt werden darf: nur, wenn danach noch ein Weg zur Anmeldung bleibt. In JulOS bleibt immer Passwort mit Code (es laesst sich nicht entfernen);
    /// die Regel steht trotzdem hier, damit sie nicht an einer Stelle haengt, die ein spaeterer Umbau vergisst.
    /// </summary>
    public static bool CanRemove(int otherPasskeys, bool passwordLogin) => otherPasskeys > 0 || passwordLogin;

    /// <summary>amr (RFC 8176) im ID-Token je Staerke der Sitzung. Passkey: Besitz eines Schluessels (hwk) und eine Pruefung der Person am Geraet (user). Siehe docs/SIGN-IN.md.</summary>
    public static string[] Amr(string? strength) => strength == Passkey ? ["hwk", "user"] : ["pwd", "otp"];

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 8192) return null;
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(t); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// Die Herausforderung aus dem clientDataJSON einer Antwort des Browsers (nur zum Nachschlagen des gespeicherten Hashs). Gepruefte Wahrheit bleibt die Bibliothek: sie
    /// vergleicht dieselbe Herausforderung mit dem clientDataJSON und prueft Ursprung und Art. Hier wird nichts geglaubt, nur gesucht.
    /// </summary>
    public static byte[]? ChallengeOf(string? clientDataBase64Url)
    {
        var raw = FromBase64Url(clientDataBase64Url);
        if (raw is null || raw.Length > 4096) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("challenge", out var c) && c.ValueKind == JsonValueKind.String
                ? FromBase64Url(c.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }
}
