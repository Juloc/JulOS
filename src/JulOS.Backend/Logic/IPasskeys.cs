namespace JulOS.Backend.Logic;

/// <summary>Ein frisch registrierter Passkey, wie die Pruefung ihn liefert.</summary>
public sealed record PasskeyNew(byte[] CredentialId, byte[] PublicKey, uint Counter, Guid Aaguid, bool BackupEligible, bool BackedUp);

/// <summary>Eine bestandene Anmeldung: Credential-Id, neuer Zaehler und der Backup-Merker.</summary>
public sealed record PasskeyProof(byte[] CredentialId, uint Counter, bool BackedUp);

/// <summary>Die Pruefung wurde abgelehnt. <see cref="Clone"/>: der Zaehler lief rueckwaerts (Klonverdacht). Der Text ist fuer das Log, nie fuer die Person.</summary>
public sealed class PasskeyRejectedException(string reason, bool clone = false) : Exception(reason)
{
    public bool Clone { get; } = clone;
}

/// <summary>
/// Die Schnittstelle zu einer WebAuthn-Bibliothek. Alles Kryptografische und Protokollhafte (Ursprung, RP-Id-Hash, Herausforderung, Flags, Signatur, Zaehler, CBOR) steht dahinter;
/// JulOS schreibt nichts davon selbst. Ein Austausch der Bibliothek betrifft nur <c>Fido2Passkeys</c>. Optionen und Antworten gehen als JSON-Text hin und her.
/// </summary>
public interface IPasskeys
{
    /// <summary>Neue Herausforderung (32 Byte) und die Optionen fuers Registrieren: Resident Key und Nutzerpruefung verlangt, Attestation none, eigene Passkeys ausgeschlossen.</summary>
    (byte[] Challenge, string OptionsJson) RegistrationOptions(PasskeySite site, byte[] userHandle, string userName, IReadOnlyList<byte[]> exclude);

    /// <summary>Neue Herausforderung und Optionen fuers Anmelden: Nutzerpruefung verlangt; ohne <paramref name="allow"/> (leere Liste) darf jeder Passkey dieser Adresse antworten.</summary>
    (byte[] Challenge, string OptionsJson) AssertionOptions(PasskeySite site, IReadOnlyList<byte[]> allow);

    Task<PasskeyNew> VerifyRegistrationAsync(PasskeySite site, string credentialJson, byte[] challenge, byte[] userHandle, string userName, Func<byte[], Task<bool>> isUnique, CancellationToken ct);

    /// <param name="ownerHandle">Der User-Handle des Kontos, dem die Credential-Id gehoert. Die Antwort muss ihn (falls sie einen nennt) tragen.</param>
    Task<PasskeyProof> VerifyAssertionAsync(PasskeySite site, string credentialJson, byte[] challenge, byte[] publicKey, uint storedCounter, byte[] ownerHandle, CancellationToken ct);
}
