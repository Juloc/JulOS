using System.Security.Cryptography;
using System.Text;

namespace JulOS.Backend.Logic;

/// <summary>
/// Verschluesselt gespeicherte Verbindungs-Passwoerter mit AES-256-GCM. Der Schluessel (JULOS_SECRET_KEY, 32 Byte als Base64) liegt
/// nur in der Server-Einstellung, nicht in der Datenbank: ein Datenbank-Auszug allein verraet kein Passwort.
/// Format: Nonce (12) + Tag (16) + Chiffre. Die Verbindungs-Id ist "zugehoerige Daten", ein Eintrag laesst sich also nicht zu einer anderen Verbindung verschieben.
/// Ohne Schluessel ist das Speichern ausgeschaltet.
/// </summary>
public sealed class SecretBox
{
    private const int NonceSize = 12, TagSize = 16;
    private readonly byte[]? _key;

    public SecretBox(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key)) return;
        try { _key = Convert.FromBase64String(base64Key.Trim()); }
        catch (FormatException) { throw new InvalidOperationException("JULOS_SECRET_KEY ist kein gültiges Base64 (erzeugen mit: openssl rand -base64 32)."); }
        if (_key.Length != 32) throw new InvalidOperationException("JULOS_SECRET_KEY muss 32 Byte lang sein (erzeugen mit: openssl rand -base64 32).");
    }

    public bool Enabled => _key is not null;

    public byte[] Encrypt(string plain, Guid connectionId)
    {
        if (_key is null) throw new InvalidOperationException("Passwort-Speicher ist nicht eingerichtet.");
        var data = Encoding.UTF8.GetBytes(plain);
        var result = new byte[NonceSize + TagSize + data.Length];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, result.AsSpan(NonceSize + TagSize), result.AsSpan(NonceSize, TagSize), connectionId.ToByteArray());
        CryptographicOperations.ZeroMemory(data);
        return result;
    }

    /// <returns>Das Passwort, oder null wenn es sich nicht entschluesseln laesst (anderer Schluessel, beschaedigt).</returns>
    public string? Decrypt(byte[] sealedValue, Guid connectionId)
    {
        if (_key is null || sealedValue.Length < NonceSize + TagSize) return null;
        var plain = new byte[sealedValue.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealedValue.AsSpan(0, NonceSize), sealedValue.AsSpan(NonceSize + TagSize), sealedValue.AsSpan(NonceSize, TagSize), plain, connectionId.ToByteArray());
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
