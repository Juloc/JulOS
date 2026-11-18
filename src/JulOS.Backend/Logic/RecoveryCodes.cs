using System.Security.Cryptography;
using System.Text;

namespace JulOS.Backend.Logic;

/// <summary>Zehn einmalige Wiederherstellungscodes (80 Bit, Form XXXX-XXXX-XXXX-XXXX). Gespeichert wird nur der SHA-256-Wert.</summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    public const int NormalizedLength = 16;

    public static (string[] Plain, string[] Hashes) Generate()
    {
        var plain = new string[Count];
        for (var i = 0; i < Count; i++)
        {
            var raw = Base32.Encode(RandomNumberGenerator.GetBytes(10));          // 16 Zeichen
            plain[i] = string.Join('-', Enumerable.Range(0, 4).Select(p => raw.Substring(p * 4, 4)));
        }
        return (plain, [.. plain.Select(Hash)]);
    }

    public static string Normalize(string input) => new(input.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());

    public static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(Normalize(code))));

    public static bool LooksLikeRecoveryCode(string input) => Normalize(input).Length == NormalizedLength;
}
