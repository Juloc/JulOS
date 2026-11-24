using System.Text.Json.Serialization;

namespace JulOS.Frontend.Oidc;

// Die Verwaltung der Anmelde-Zentrale (/api/security/oidc/...). Das Geheimnis einer App steht nur in OidcCreated und OidcSecretResponse, einmal; die Liste kennt es nicht.
internal sealed record OidcAppDto(Guid Id, string Name, string ClientId, string[] RedirectUris, string[] PostLogoutUris, string? BackchannelUri, string[] Roles, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset SecretChangedAt, int Sessions, bool BackchannelPrivate);
internal sealed record OidcPersonDto(Guid Id, string Name, bool Owner);
internal sealed record OidcAssignmentDto(Guid AppId, Guid UserId, string Role);
internal sealed record OidcKeyDto(string Kid, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);
internal sealed record OidcOverview(bool Ready, bool KeyStore, bool KeyProblem, string? Issuer, string IssuerSource, string SuggestedIssuer, string ConfirmMode, OidcAppDto[] Apps, OidcPersonDto[] People,
    OidcAssignmentDto[] Assignments, OidcKeyDto[] Keys);
internal sealed record OidcAppRequest(string? Name, string[]? RedirectUris, string[]? PostLogoutUris, string? BackchannelUri, string[]? Roles, bool? BackchannelPrivate = null);
internal sealed record OidcCreated(OidcAppDto App, string ClientSecret);
internal sealed record OidcSecretResponse(string ClientSecret);
internal sealed record OidcEnabledRequest(bool? Enabled);
internal sealed record OidcRoleRequest(string? Role);
internal sealed record OidcOwnerRequest(Guid? UserId);
internal sealed record OidcIssuerRequest(string? Issuer);
internal sealed record OidcIssuerResponse(string? Issuer, string Source);
internal sealed record OidcKeyRotated(string Kid);
internal sealed record OidcError(string Error, string Code);

/// <summary>JSON ohne Reflection fuer die Verwaltung (die Protokoll-Antworten schreibt OidcEndpoints von Hand, sie haben snake_case-Namen).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OidcOverview))]
[JsonSerializable(typeof(OidcAppDto))]
[JsonSerializable(typeof(OidcAppRequest))]
[JsonSerializable(typeof(OidcCreated))]
[JsonSerializable(typeof(OidcSecretResponse))]
[JsonSerializable(typeof(OidcEnabledRequest))]
[JsonSerializable(typeof(OidcRoleRequest))]
[JsonSerializable(typeof(OidcOwnerRequest))]
[JsonSerializable(typeof(OidcIssuerRequest))]
[JsonSerializable(typeof(OidcIssuerResponse))]
[JsonSerializable(typeof(OidcKeyRotated))]
[JsonSerializable(typeof(OidcError))]
internal sealed partial class OidcJson : JsonSerializerContext;
