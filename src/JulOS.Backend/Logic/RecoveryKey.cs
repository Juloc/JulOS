using System.Security.Cryptography;
using System.Text;

namespace JulOS.Backend.Logic;

/// <summary>
/// Der Wiederherstellungsschluessel (docs/BACKUP.md, F-01): 256 Zufallsbit, die nur der Besitzer kennt (Passwortmanager). Der Server behaelt sie nie. Mit ihnen wird der Schluessel
/// der Passwoerter (<c>secret.key</c>) <b>umhuellt</b> (AES-256-GCM, Schluessel per HKDF): die umhuellte Datei darf neben dem Auszug liegen, ohne den Wiederherstellungsschluessel
/// ist sie wertlos. So geht eine Wiederherstellung aus Auszug, umhuellter Datei und dem Text aus dem Passwortmanager, und ein neuer Schluessel laesst sich jederzeit erzeugen,
/// ohne irgendetwas neu zu verschluesseln. Anzeige als 52 Zeichen Base32 in Vierergruppen.
/// </summary>
public static class RecoveryKey
{
    private const string Header = "JULOS-KEY-WRAP 1";
    private static readonly byte[] Context = Encoding.ASCII.GetBytes("julos-key-wrap-v1");

    public static byte[] New() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Lesbar: Grossbuchstaben und Ziffern in Gruppen zu vier, durch Striche getrennt.</summary>
    public static string Format(byte[] key)
    {
        var text = Base32.Encode(key);
        return string.Join('-', Enumerable.Range(0, (text.Length + 3) / 4).Select(i => text.Substring(i * 4, Math.Min(4, text.Length - i * 4))));
    }

    /// <summary>Liest die Eingabe (Striche, Leerzeichen und Kleinschreibung egal). null, wenn es kein Wiederherstellungsschluessel sein kann.</summary>
    public static byte[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100) return null;
        try { var key = Base32.Decode(text.Trim()); return key.Length == 32 ? key : null; }
        catch (FormatException) { return null; }
    }

    private static byte[] Derive(byte[] recoveryKey) => HKDF.DeriveKey(HashAlgorithmName.SHA256, recoveryKey, 32, info: Context);

    /// <summary>Die umhuellte Form des Schluessels (Nonce 12, Tag 16, Chiffre).</summary>
    public static byte[] Wrap(byte[] secretKey, byte[] recoveryKey)
    {
        var result = new byte[12 + 16 + secretKey.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(Derive(recoveryKey), 16);
        aes.Encrypt(result.AsSpan(0, 12), secretKey, result.AsSpan(28), result.AsSpan(12, 16), Context);
        return result;
    }

    /// <summary>Der Schluessel, oder null bei falschem Wiederherstellungsschluessel (oder beschaedigter Datei).</summary>
    public static byte[]? Unwrap(byte[] wrapped, byte[] recoveryKey)
    {
        if (wrapped.Length < 29) return null;
        var plain = new byte[wrapped.Length - 28];
        try
        {
            using var aes = new AesGcm(Derive(recoveryKey), 16);
            aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), plain, Context);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Der Inhalt der Datei neben dem Auszug: eine Kopfzeile und die umhuellten Bytes als Base64.</summary>
    public static byte[] ToFile(byte[] wrapped) => Encoding.ASCII.GetBytes(Header + "\n" + Convert.ToBase64String(wrapped) + "\n");

    public static byte[]? FromFile(byte[] file)
    {
        var lines = Encoding.ASCII.GetString(file).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2 || lines[0] != Header) return null;
        try { return Convert.FromBase64String(lines[1]); }
        catch (FormatException) { return null; }
    }
}
