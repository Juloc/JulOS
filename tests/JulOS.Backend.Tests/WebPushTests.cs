using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

/// <summary>Web Push (docs/PUSH.md): Verschluesselung mit den Testvektoren aus RFC 8291 Anhang A, die VAPID-Kennung, und die Ablehnung gefaehrlicher Endpunkte (SSRF).</summary>
public sealed class WebPushTests
{
    private static byte[] B(string s) => WebPush.FromB64(s)!;

    // RFC 8291, Anhang A
    private const string Plain = "When I grow up, I want to be a watermelon";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string Expected = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    /// <summary>Entschluesselt wie der Browser (RFC 8291, Abschnitt 4): mit dem privaten Schluessel des Geraets.</summary>
    internal static byte[] Decrypt(byte[] body, byte[] uaPrivate, byte[] uaPublic, byte[] auth)
    {
        var salt = body[..16];
        var idLen = body[20];
        var asPublic = body[21..(21 + idLen)];
        var cipher = body[(21 + idLen)..];
        var q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] };
        using var ua = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = q, D = uaPrivate });
        using var peer = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] } });
        var shared = ua.DeriveRawSecretAgreement(peer.PublicKey);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, shared, auth), 32, [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());
        var plain = new byte[cipher.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain);
        var end = Array.FindLastIndex(plain, x => x != 0);
        Assert.Equal(2, plain[end]);                    // Ende der einzigen Aufzeichnung
        return plain[..end];
    }

    [Fact]
    public void Encryption_matches_the_test_vector_of_RFC_8291()
    {
        var body = WebPush.Encrypt(Encoding.UTF8.GetBytes(Plain), B(UaPublic), B(AuthSecret), B(AsPrivate), B(AsPublic), B(Salt));
        Assert.Equal(Expected, WebPush.B64(body));
    }

    [Fact]
    public void A_fresh_encryption_can_be_read_by_the_device_and_differs_each_time()
    {
        var payload = Encoding.UTF8.GetBytes("""{"v":1,"items":[{"h":"Heimserver","r":"disk","k":"alert"}]}""");
        var a = WebPush.Encrypt(payload, B(UaPublic), B(AuthSecret));
        var b = WebPush.Encrypt(payload, B(UaPublic), B(AuthSecret));
        Assert.NotEqual(a, b);
        Assert.Equal(payload, Decrypt(a, B(UaPrivate), B(UaPublic), B(AuthSecret)));
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt(new byte[WebPush.MaxPayload + 1], B(UaPublic), B(AuthSecret)));
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt(payload, new byte[65], B(AuthSecret)));      // kein Punkt auf der Kurve
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt(payload, B(UaPublic), new byte[15]));
    }

    [Fact]
    public void The_vapid_header_carries_a_valid_ES256_token_for_the_service_origin()
    {
        var (priv, pub) = WebPush.NewVapidKey();
        var now = DateTimeOffset.UtcNow;
        var header = WebPush.VapidHeader(priv, pub, new Uri("https://push.example.org/wpush/v2/abc"), "https://app.example.org", now);
        var parts = header["vapid ".Length..].Split(", ");
        Assert.Equal("k=" + WebPush.B64(pub), parts[1]);
        var jwt = parts[0]["t=".Length..].Split('.');
        Assert.Equal("""{"typ":"JWT","alg":"ES256"}""", Encoding.UTF8.GetString(B(jwt[0])));
        var claims = JsonDocument.Parse(B(jwt[1])).RootElement;
        Assert.Equal("https://push.example.org", claims.GetProperty("aud").GetString());
        Assert.Equal("https://app.example.org", claims.GetProperty("sub").GetString());
        var exp = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64());
        Assert.InRange(exp - now, TimeSpan.FromHours(1), TimeSpan.FromHours(24));
        using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = pub[1..33], Y = pub[33..65] } });
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), B(jwt[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]            // kein https
    [InlineData("https://fcm.googleapis.com:8443/x", false)]                 // nicht Port 443
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://[::1]/x", false)]
    [InlineData("https://192.168.1.10/x", false)]
    [InlineData("https://10.0.0.5/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://0x7f000001/x", false)]
    [InlineData("https://2130706433/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://nas.local/x", false)]
    [InlineData("https://intranet/x", false)]                                // Name ohne Punkt
    [InlineData("https://user:pw@fcm.googleapis.com/x", false)]
    [InlineData("https://fcm.googleapis.com/x#frag", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    public void Only_public_https_endpoints_on_port_443_are_accepted(string url, bool ok) => Assert.Equal(ok, PushEndpoint.Parse(url) is not null);

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]                                   // IPv4 in IPv6 versteckt
    [InlineData("::ffff:127.0.0.1", false)]
    public void Resolved_addresses_in_private_ranges_are_refused(string ip, bool pub) => Assert.Equal(pub, PushEndpoint.IsPublic(IPAddress.Parse(ip)));
}
