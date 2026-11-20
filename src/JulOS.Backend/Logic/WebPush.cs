using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace JulOS.Backend.Logic;

/// <summary>
/// Web Push (docs/PUSH.md) mit den Bausteinen von .NET, ohne Fremdbibliothek: die VAPID-Kennung des Servers (RFC 8292, ES256) und die Verschluesselung der Nachricht fuer den Browser
/// (RFC 8291, Inhaltskodierung aes128gcm nach RFC 8188, eine einzige Aufzeichnung). Der Push-Dienst des Browsers sieht nur den verschluesselten Inhalt. Reine Funktionen, mit den
/// Testvektoren aus RFC 8291 Anhang A geprueft (tests/JulOS.Backend.Tests/WebPushTests.cs).
/// </summary>
public static class WebPush
{
    public const int MaxPayload = 3000;     // der Push-Dienst nimmt mindestens 4096 Byte; 3000 laesst Platz fuer Kopf und Schluessel

    public static string B64(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Base64url (mit oder ohne Auffuellung); null, wenn es keins ist.</summary>
    public static byte[]? FromB64(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2048) return null;
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
    }

    /// <summary>Ein neues Schluesselpaar fuer VAPID: (privater Teil, 32 Byte; oeffentlicher Teil, 65 Byte unkomprimiert).</summary>
    public static (byte[] Private, byte[] Public) NewVapidKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        return (p.D!, Point(p.Q));
    }

    private static byte[] Point(ECPoint q)
    {
        var r = new byte[65];
        r[0] = 4;
        q.X!.CopyTo(r, 1 + 32 - q.X.Length);
        q.Y!.CopyTo(r, 33 + 32 - q.Y.Length);
        return r;
    }

    private static ECParameters Params(byte[] publicKey, byte[]? d = null)
    {
        var x = publicKey.AsSpan(1, 32).ToArray();
        var y = publicKey.AsSpan(33, 32).ToArray();
        return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y }, D = d };
    }

    /// <summary>Ob das ein gueltiger Punkt auf der Kurve P-256 ist (65 Byte, 0x04 vorn): die Schluessel des Browsers werden vor dem Speichern so geprueft.</summary>
    public static bool IsPublicKey(byte[]? key)
    {
        if (key is not { Length: 65 } || key[0] != 4) return false;
        try { using var k = ECDiffieHellman.Create(Params(key)); return true; }
        catch (CryptographicException) { return false; }
    }

    /// <summary>
    /// Die Kennung des Servers gegenueber dem Push-Dienst: ein JWT (ES256), gueltig fuer den Dienst (aud = Ursprung des Endpunkts), hoechstens 24 Stunden.
    /// Rueckgabe: der Wert fuer die Kopfzeile Authorization ("vapid t=..., k=...").
    /// </summary>
    public static string VapidHeader(byte[] privateKey, byte[] publicKey, Uri endpoint, string subject, DateTimeOffset now)
    {
        var header = B64(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var aud = endpoint.GetLeftPart(UriPartial.Authority);
        var claims = B64(Encoding.UTF8.GetBytes($"{{\"aud\":\"{Esc(aud)}\",\"exp\":{now.AddHours(12).ToUnixTimeSeconds()},\"sub\":\"{Esc(subject)}\"}}"));
        using var key = ECDsa.Create(Params(publicKey, privateKey));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{B64(signature)}, k={B64(publicKey)}";
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Verschluesselt <paramref name="payload"/> fuer ein Abo (RFC 8291): ein frisches Schluesselpaar je Nachricht und ein frischer Salt (beide nur fuer Tests vorgegeben).
    /// Ergebnis ist der fertige Koerper der Anfrage (Kopf mit Salt, Aufzeichnungsgroesse und Schluessel des Servers, dann die Chiffre samt Tag).
    /// </summary>
    public static byte[] Encrypt(byte[] payload, byte[] browserPublic, byte[] authSecret, byte[]? serverPrivate = null, byte[]? serverPublic = null, byte[]? salt = null)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException("Nachricht zu gross.", nameof(payload));
        if (!IsPublicKey(browserPublic) || authSecret.Length != 16) throw new ArgumentException("Schluessel des Abos ungueltig.");
        using var server = serverPrivate is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : ECDiffieHellman.Create(Params(serverPublic!, serverPrivate));
        var serverPub = serverPublic ?? Point(server.ExportParameters(false).Q);
        using var peer = ECDiffieHellman.Create(Params(browserPublic));
        var shared = server.DeriveRawSecretAgreement(peer.PublicKey);

        // RFC 8291, Abschnitt 3.3 und 3.4
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, shared, authSecret);
        var keyInfo = Concat("WebPush: info\0"u8.ToArray(), browserPublic, serverPub);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        var plain = new byte[payload.Length + 1];
        payload.CopyTo(plain, 0);
        plain[^1] = 2;                                  // Ende der einzigen Aufzeichnung, ohne weitere Auffuellung
        var body = new byte[16 + 4 + 1 + 65 + plain.Length + 16];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), 4096);
        body[20] = 65;
        serverPub.CopyTo(body, 21);
        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, plain, body.AsSpan(86, plain.Length), body.AsSpan(86 + plain.Length, 16));
        return body;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        var i = 0;
        foreach (var p in parts) { p.CopyTo(r.AsSpan(i)); i += p.Length; }
        return r;
    }
}

/// <summary>
/// Welche Adressen als Endpunkt eines Abos taugen. Der Server ruft nur den Endpunkt auf, den der Browser nennt: er muss https sein, auf Port 443 liegen, einen Namen tragen
/// (keine Zahlenadresse, kein localhost) und darf nicht in ein privates Netz fuehren (SSRF). Die Aufloesung wird beim Verbinden noch einmal geprueft (<see cref="IsPublic"/>).
/// </summary>
public static class PushEndpoint
{
    public const int MaxLength = 1000;

    public static Uri? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
        if (host.Length == 0 || !host.Contains('.') || IPAddress.TryParse(host.Trim('[', ']'), out _)) return null;
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan") || host.EndsWith(".home.arpa")) return null;
        return uri;
    }

    /// <summary>Eine oeffentliche Adresse: nicht Loopback, nicht privat, nicht Link-Local, nicht CGNAT, nicht Multicast, nicht unbestimmt (auch als IPv4 in IPv6).</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168 || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2 || b[0] == 198 && b[1] is 18 or 19 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113);
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return false;
        return !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);       // Dokumentationsbereich
    }
}
