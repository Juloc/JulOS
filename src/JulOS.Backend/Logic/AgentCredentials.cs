using System.Security.Cryptography;
using System.Text;

namespace JulOS.Backend.Logic;

/// <summary>
/// Einrichtungscode und Token eines Agenten. Der Code ist kurz (50 Bit, Form XXXXX-XXXXX), einmalig und nur wenige Minuten gueltig; er wird
/// gegen ein Token getauscht (256 Bit Zufall). Gespeichert wird von beiden nur der SHA-256-Wert.
/// </summary>
public static class AgentCredentials
{
    public const string TokenPrefix = "jat_";
    public const int CodeLength = 10;
    public const int MaxTokenLength = 80;

    public static string NewCode()
    {
        var raw = Base32.Encode(RandomNumberGenerator.GetBytes(7))[..CodeLength];
        return raw[..5] + "-" + raw[5..];
    }

    public static string NormalizeCode(string? input) => new((input ?? "").Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());

    public static bool LooksLikeCode(string? input) => NormalizeCode(input) is { Length: CodeLength } n && n.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7');

    public static string HashCode(string code) => Sha256(NormalizeCode(code));

    public static string NewToken() => TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string token) => Sha256(token);

    private static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(s)));
}
