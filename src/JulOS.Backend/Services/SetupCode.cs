using System.Security.Cryptography;
using System.Text;
using JulOS.Backend.Logic;

namespace JulOS.Backend.Services;

/// <summary>
/// Einmaliger Code fuer die Ersteinrichtung. Er steht nur im Server-Log, so kann nur jemand mit Zugriff auf den Server das erste Konto anlegen.
/// Kommt aus der Einstellung JULOS_SETUP_CODE (fuer die Test-Instanz), sonst wird er zufaellig erzeugt (12 Zeichen, 60 Bit).
/// </summary>
public sealed class SetupCode(string? fixedValue = null)
{
    private bool _used;

    public string Value { get; } = string.IsNullOrWhiteSpace(fixedValue) ? Generate() : fixedValue.Trim();

    private static string Generate()
    {
        var raw = Base32.Encode(RandomNumberGenerator.GetBytes(8))[..12];
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
    }

    public bool Matches(string? input)
    {
        if (_used || input is null) return false;
        var a = Encoding.UTF8.GetBytes(Normalize(input));
        var b = Encoding.UTF8.GetBytes(Normalize(Value));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public void MarkUsed() => _used = true;

    private static string Normalize(string s) => new(s.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
}
