using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

/// <summary>
/// Ein Authenticator in Software (ES256), damit der ganze Ablauf ohne Geraet laeuft: baut clientDataJSON und authenticatorData selbst und signiert. Jeder Schalter erzeugt einen
/// bestimmten Fehler (falscher Ursprung, falscher RP-Hash, fehlendes UV-Flag, Zaehler ...), den der Server ablehnen muss. Er benutzt die Bibliothek des Servers nicht.
/// </summary>
internal sealed class SoftAuthenticator
{
    private const byte UP = 0x01, UV = 0x04, BE = 0x08, BS = 0x10, AT = 0x40;

    public sealed class Cred(byte[] id, ECDsa key, byte[] userHandle) { public byte[] Id = id; public ECDsa Key = key; public byte[] UserHandle = userHandle; public uint Counter; }

    public List<Cred> Creds { get; } = [];

    /// <summary>Wie der Authenticator sich in diesem Aufruf benimmt. Standard: alles richtig.</summary>
    public sealed record Behaviour(string? Origin = null, string? RpId = null, bool UserVerified = true, bool UserPresent = true, uint? Counter = null, bool? BackupEligible = null,
        byte[]? UserHandle = null, bool OmitUserHandle = false, byte[]? ReuseCredentialId = null, string? Challenge = null, bool BadSignature = false, byte[]? CredentialIdOverride = null);

    private static readonly Behaviour Plain = new();

    public static string B64(ReadOnlySpan<byte> b) => PasskeyRules.ToBase64Url(b);
    public static byte[] Unb64(string s) => PasskeyRules.FromBase64Url(s)!;

    private static byte[] AuthData(string rpId, byte flags, uint counter, byte[]? attested = null)
    {
        var ms = new MemoryStream();
        ms.Write(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        ms.WriteByte(flags);
        ms.Write([(byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter]);
        if (attested is not null) ms.Write(attested);
        return ms.ToArray();
    }

    private static byte[] CoseKey(ECParameters p)
    {
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(5);
        w.WriteInt32(1); w.WriteInt32(2);          // kty EC2
        w.WriteInt32(3); w.WriteInt32(-7);         // alg ES256
        w.WriteInt32(-1); w.WriteInt32(1);         // crv P-256
        w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
        w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
        w.WriteEndMap();
        return w.Encode();
    }

    private static string ClientData(string type, string challenge, string origin) =>
        JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false });

    /// <summary>Antwort auf die Optionen zum Registrieren (Text des Servers). Gibt den Text von "credential", wie ihn die Seite sendet.</summary>
    public string Register(string optionsJson, string defaultOrigin, Behaviour? how = null)
    {
        how ??= Plain;
        var o = JsonDocument.Parse(optionsJson).RootElement;
        var rpId = how.RpId ?? o.GetProperty("rp").GetProperty("id").GetString()!;
        var handle = how.UserHandle ?? Unb64(o.GetProperty("user").GetProperty("id").GetString()!);
        var challenge = how.Challenge ?? o.GetProperty("challenge").GetString()!;
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = how.ReuseCredentialId ?? RandomNumberGenerator.GetBytes(32);
        var cred = new Cred(id, key, handle) { Counter = how.Counter ?? 0 };
        Creds.Add(cred);

        var attested = new MemoryStream();
        attested.Write(new byte[16]);                                                  // AAGUID: lauter Nullen, wie bei Software ohne Hersteller
        attested.Write([(byte)(id.Length >> 8), (byte)id.Length]);
        attested.Write(id);
        attested.Write(CoseKey(key.ExportParameters(false)));
        var flags = (byte)(AT | (how.UserPresent ? UP : 0) | (how.UserVerified ? UV : 0) | (how.BackupEligible == true ? BE : 0));
        var auth = AuthData(rpId, flags, how.Counter ?? 0, attested.ToArray());

        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(3);
        w.WriteTextString("fmt"); w.WriteTextString("none");
        w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
        w.WriteTextString("authData"); w.WriteByteString(auth);
        w.WriteEndMap();

        var client = ClientData("webauthn.create", challenge, how.Origin ?? defaultOrigin);
        return JsonSerializer.Serialize(new
        {
            id = B64(id), rawId = B64(id), type = "public-key",
            response = new { clientDataJSON = B64(Encoding.UTF8.GetBytes(client)), attestationObject = B64(w.Encode()) },
            clientExtensionResults = new { },
        });
    }

    /// <summary>Antwort auf die Optionen zum Anmelden. <paramref name="pick"/>: welcher Passkey (Standard: der erste, der zur RP-Id passt oder in der Erlaubnisliste steht).</summary>
    public string Assert(string optionsJson, string defaultOrigin, Behaviour? how = null, Cred? pick = null)
    {
        how ??= Plain;
        var o = JsonDocument.Parse(optionsJson).RootElement;
        var rpId = how.RpId ?? o.GetProperty("rpId").GetString()!;
        var challenge = how.Challenge ?? o.GetProperty("challenge").GetString()!;
        var allowed = o.TryGetProperty("allowCredentials", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(e => Unb64(e.GetProperty("id").GetString()!)).ToList() : [];
        var cred = pick ?? Creds.First(c => allowed.Count == 0 || allowed.Any(x => x.AsSpan().SequenceEqual(c.Id)));
        cred.Counter = how.Counter ?? cred.Counter + 1;
        var flags = (byte)((how.UserPresent ? UP : 0) | (how.UserVerified ? UV : 0) | (how.BackupEligible == true ? BE : 0));
        var auth = AuthData(rpId, flags, cred.Counter);
        var client = Encoding.UTF8.GetBytes(ClientData("webauthn.get", challenge, how.Origin ?? defaultOrigin));
        var signed = auth.Concat(SHA256.HashData(client)).ToArray();
        var sig = cred.Key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (how.BadSignature) sig[^1] ^= 0x01;
        var id = how.CredentialIdOverride ?? cred.Id;
        object response = how.OmitUserHandle
            ? new { authenticatorData = B64(auth), clientDataJSON = B64(client), signature = B64(sig) }
            : new { authenticatorData = B64(auth), clientDataJSON = B64(client), signature = B64(sig), userHandle = B64(how.UserHandle ?? cred.UserHandle) };
        return JsonSerializer.Serialize(new { id = B64(id), rawId = B64(id), type = "public-key", response, clientExtensionResults = new { } });
    }
}
