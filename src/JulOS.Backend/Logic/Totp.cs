using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace JulOS.Backend.Logic;

/// <summary>Zeitbasierte Einmalcodes nach RFC 6238 (HMAC-SHA1, 6 Stellen, 30 Sekunden).</summary>
public static class Totp
{
    public const int StepSeconds = 30;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public static string Uri(string user, string secret) =>
        $"otpauth://totp/JulOS:{System.Uri.EscapeDataString(user)}?secret={secret}&issuer=JulOS&algorithm=SHA1&digits=6&period=30";

    public static string Hotp(byte[] secret, long counter, int digits = 6)
    {
        Span<byte> msg = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(msg, counter);
        Span<byte> mac = stackalloc byte[20];
        HMACSHA1.HashData(secret, msg, mac);
        var offset = mac[19] & 0x0F;
        var code = ((mac[offset] & 0x7F) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
        var mod = 1;
        for (var i = 0; i < digits; i++) mod *= 10;
        return (code % mod).ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// Prueft einen Code gegen den aktuellen und die benachbarten Zeitschritte (+-1).
    /// Gibt den Zeitschritt zurueck, in dem er gilt, oder null. Schritte bis einschliesslich <paramref name="lastUsedStep"/> sind verbraucht.
    /// Alle drei Schritte werden immer berechnet, damit die Dauer nicht verraet, welcher gepasst hat.
    /// </summary>
    public static long? Check(string secretBase32, string code, long lastUsedStep, DateTimeOffset now)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;
        var secret = Base32.Decode(secretBase32);
        var step = now.ToUnixTimeSeconds() / StepSeconds;
        var given = Encoding.ASCII.GetBytes(code);
        long? hit = null;
        foreach (var delta in new long[] { 0, -1, 1 })
        {
            var s = step + delta;
            var match = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hotp(secret, s)), given);
            if (match && s > lastUsedStep && hit is null) hit = s;
        }
        return hit;
    }
}
