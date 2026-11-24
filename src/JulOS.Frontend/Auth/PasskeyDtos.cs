namespace JulOS.Frontend.Auth;

internal sealed record PasskeyNameRequest(string? Name);
/// <summary>Ein Passkey in der Liste. Nie die Credential-Id und nie der Schluessel. Aaguid nennt die Art des Authenticators (zum Wiedererkennen), Synced: kann ueber ein Konto auf andere Geraete gelangen.</summary>
internal sealed record PasskeyDto(Guid Id, string Name, Guid? Aaguid, bool Synced, bool BackedUp, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
/// <summary>Available: false, wenn die oeffentliche Adresse fehlt oder eine IP-Adresse ist (dann kann WebAuthn nicht). Max: die Grenze je Person.</summary>
internal sealed record PasskeysResponse(PasskeyDto[] Passkeys, bool Available, int Max, string? RpId);
