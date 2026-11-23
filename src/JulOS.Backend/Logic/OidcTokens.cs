using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JulOS.Backend.Logic;

/// <summary>Was ein ID-Token sagt (docs/SIGN-IN.md, Ansprueche).</summary>
public sealed record IdTokenClaims(string Issuer, string ClientId, string Subject, string Sid, DateTimeOffset IssuedAt, DateTimeOffset AuthTime, string? Nonce, string? Name, string Role);

/// <summary>Was ein Logout-Token sagt (OIDC Back-Channel Logout 1.0).</summary>
public sealed record LogoutTokenClaims(string Issuer, string ClientId, string Subject, string Sid, DateTimeOffset IssuedAt, string Jti);

/// <summary>
/// Signierte Token mit Microsofts Token-Bibliothek (JsonWebTokenHandler). Die Nutzlast wird hier von Hand als JSON geschrieben (kein Reflection-JSON: der Server laeuft ohne),
/// signiert und geprueft wird von der Bibliothek. Algorithmus ist immer ES256; ein Token mit einem anderen Algorithmus wird nie angenommen.
/// </summary>
public static class OidcTokens
{
    public const string Alg = SecurityAlgorithms.EcdsaSha256;
    public const string LogoutType = "logout+jwt";

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); write(w); w.WriteEndObject(); }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string CreateIdToken(IdTokenClaims c, SecurityKey signingKey, TimeSpan lifetime)
    {
        var payload = Json(w =>
        {
            w.WriteString("iss", c.Issuer);
            w.WriteString("sub", c.Subject);
            w.WriteString("aud", c.ClientId);
            w.WriteNumber("exp", c.IssuedAt.Add(lifetime).ToUnixTimeSeconds());
            w.WriteNumber("iat", c.IssuedAt.ToUnixTimeSeconds());
            w.WriteNumber("auth_time", c.AuthTime.ToUnixTimeSeconds());
            if (c.Nonce is not null) w.WriteString("nonce", c.Nonce);
            w.WriteString("sid", c.Sid);
            w.WriteStartArray("amr"); w.WriteStringValue("pwd"); w.WriteStringValue("otp"); w.WriteEndArray();
            if (c.Name is not null) { w.WriteString("name", c.Name); w.WriteString("preferred_username", c.Name); }
            w.WriteString("role", c.Role);
        });
        return Handler.CreateToken(payload, new SigningCredentials(signingKey, Alg));
    }

    /// <summary>Das Logout-Token: Typ logout+jwt, iss, aud, iat, exp, jti, events, sid und sub, kein nonce (OIDC Back-Channel Logout 1.0, Abschnitt 2.4).</summary>
    public static string CreateLogoutToken(LogoutTokenClaims c, SecurityKey signingKey)
    {
        var payload = Json(w =>
        {
            w.WriteString("iss", c.Issuer);
            w.WriteString("aud", c.ClientId);
            w.WriteNumber("iat", c.IssuedAt.ToUnixTimeSeconds());
            w.WriteNumber("exp", c.IssuedAt.Add(OidcRules.LogoutTokenLifetime).ToUnixTimeSeconds());
            w.WriteString("jti", c.Jti);
            w.WriteStartObject("events"); w.WriteStartObject(OidcRules.BackchannelEvent); w.WriteEndObject(); w.WriteEndObject();
            w.WriteString("sid", c.Sid);
            w.WriteString("sub", c.Subject);
        });
        return Handler.CreateToken(payload, new SigningCredentials(signingKey, Alg), new Dictionary<string, object> { ["typ"] = LogoutType });
    }

    /// <summary>
    /// Prueft ein von JulOS ausgestelltes ID-Token als Hinweis fuer das Abmelden (id_token_hint): Signatur mit einem unserer Schluessel, Aussteller, nur ES256.
    /// Die Laufzeit wird nicht geprueft (ein Hinweis darf abgelaufen sein, OIDC RP-Initiated Logout 1.0). Gibt das Token oder null.
    /// </summary>
    public static async Task<JsonWebToken?> ReadHintAsync(string? token, string issuer, IEnumerable<SecurityKey> keys)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return null;
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer, ValidateIssuer = true, ValidateAudience = false, ValidateLifetime = false,
            IssuerSigningKeys = keys, ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidAlgorithms = [Alg],
        };
        var result = await Handler.ValidateTokenAsync(token, parameters);
        return result.IsValid ? result.SecurityToken as JsonWebToken : null;
    }

    /// <summary>Ein Anspruch als Text (oder null).</summary>
    public static string? Claim(JsonWebToken t, string name) => t.TryGetPayloadValue<string>(name, out var v) ? v : null;

    /// <summary>Der Zielgruppen-Eintrag eines Tokens (bei uns immer genau die client_id).</summary>
    public static string? Audience(JsonWebToken t) => t.Audiences.FirstOrDefault();
}
