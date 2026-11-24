using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Exceptions;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using JulOS.Backend.Logic;

namespace JulOS.Backend.Services;

/// <summary>
/// <see cref="IPasskeys"/> mit der Bibliothek Fido2 (MIT, 4.x). Sie prueft Ursprung, RP-Id-Hash, Herausforderung, Flags (Nutzerpruefung), Signatur und Zaehler und liest CBOR und COSE-Schluessel;
/// diese Datei ruft sie nur auf und uebersetzt ihre Fehler. Die Einstellung wird je Aufruf aus der Adresse gebaut: aendert sich die oeffentliche Adresse, gilt sie sofort.
/// </summary>
public sealed class Fido2Passkeys : IPasskeys
{
    private static IFido2 Lib(PasskeySite site) => new Fido2(new Fido2Configuration
    {
        RPID = site.RpId,
        RPName = "JulOS",
        Origins = new HashSet<string>([site.Origin], StringComparer.Ordinal),
        ChallengeSize = PasskeyRules.ChallengeBytes,
        // Zeitfenster und Zaehler regeln wir selbst (Herausforderung 5 Minuten, einmal gueltig; Zaehler in der Datenbank)
        TimestampDriftTolerance = 300_000,
    });

    /// <summary>
    /// Die Verfahren, die wir beim Registrieren anbieten und beim Pruefen zulassen, ausdruecklich und nicht die Vorgabe der Bibliothek (die auch ES384/512, PS* und RS384/512 kennt):
    /// ES256 (die meisten Geraete), EdDSA und RS256 (Windows Hello mit TPM). Kein RS1, keine schwachen Hashes. Die Reihenfolge ist die Vorliebe.
    /// </summary>
    private static readonly PubKeyCredParam[] Algorithms = [PubKeyCredParam.ES256, PubKeyCredParam.Ed25519, PubKeyCredParam.RS256];

    public (byte[] Challenge, string OptionsJson) RegistrationOptions(PasskeySite site, byte[] userHandle, string userName, IReadOnlyList<byte[]> exclude)
    {
        var options = Lib(site).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userHandle, Name = userName, DisplayName = userName },
            ExcludeCredentials = [.. exclude.Select(id => new PublicKeyCredentialDescriptor(id))],
            PubKeyCredParams = [.. Algorithms],
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Required },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        return (options.Challenge, options.ToJson());
    }

    public (byte[] Challenge, string OptionsJson) AssertionOptions(PasskeySite site, IReadOnlyList<byte[]> allow)
    {
        var options = Lib(site).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [.. allow.Select(id => new PublicKeyCredentialDescriptor(id))],
            UserVerification = UserVerificationRequirement.Required,
        });
        return (options.Challenge, options.ToJson());
    }

    public async Task<PasskeyNew> VerifyRegistrationAsync(PasskeySite site, string credentialJson, byte[] challenge, byte[] userHandle, string userName, Func<byte[], Task<bool>> isUnique, CancellationToken ct)
    {
        try
        {
            var response = JsonSerializer.Deserialize(credentialJson, PasskeyJson.Default.AuthenticatorAttestationRawResponse) ?? throw new PasskeyRejectedException("leere Antwort");
            // die Optionen, wie wir sie ausgegeben haben (die Herausforderung ist die gespeicherte, nicht die des Browsers)
            var original = new CredentialCreateOptions
            {
                Challenge = challenge,
                Rp = new PublicKeyCredentialRpEntity(site.RpId, "JulOS", null),
                User = new Fido2User { Id = userHandle, Name = userName, DisplayName = userName },
                PubKeyCredParams = [.. Algorithms],
                AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Required },
                Attestation = AttestationConveyancePreference.None,
                ExcludeCredentials = [],
            };
            var made = await Lib(site).MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = original,
                IsCredentialIdUniqueToUserCallback = async (args, _) => await isUnique(args.CredentialId),
            }, ct);
            return new PasskeyNew(made.Id, made.PublicKey, made.SignCount, made.AaGuid, made.IsBackupEligible, made.IsBackedUp);
        }
        catch (Fido2VerificationException e) { throw new PasskeyRejectedException(e.Message); }
        catch (JsonException e) { throw new PasskeyRejectedException("Antwort nicht lesbar: " + e.Message); }
    }

    public async Task<PasskeyProof> VerifyAssertionAsync(PasskeySite site, string credentialJson, byte[] challenge, byte[] publicKey, uint storedCounter, byte[] ownerHandle, CancellationToken ct)
    {
        try
        {
            var response = JsonSerializer.Deserialize(credentialJson, FidoModelSerializerContext.Default.AuthenticatorAssertionRawResponse) ?? throw new PasskeyRejectedException("leere Antwort");
            var original = new AssertionOptions
            {
                Challenge = challenge,
                RpId = site.RpId,
                UserVerification = UserVerificationRequirement.Required,
                AllowCredentials = [],
            };
            var result = await Lib(site).MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = original,
                StoredPublicKey = publicKey,
                StoredSignatureCounter = storedCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(args.UserHandle, ownerHandle)),
            }, ct);
            return new PasskeyProof(result.CredentialId, result.SignCount, result.IsBackedUp);
        }
        catch (Fido2VerificationException e) { throw new PasskeyRejectedException(e.Message, clone: e.Code == Fido2ErrorCode.InvalidSignCount); }
        catch (JsonException e) { throw new PasskeyRejectedException("Antwort nicht lesbar: " + e.Message); }
    }
}

/// <summary>JSON der Registrierungs-Antwort ohne Reflection (der Server laeuft ohne): die Bibliothek bringt dafuer keinen eigenen Kontext mit.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(AuthenticatorAttestationRawResponse))]
internal sealed partial class PasskeyJson : System.Text.Json.Serialization.JsonSerializerContext;
