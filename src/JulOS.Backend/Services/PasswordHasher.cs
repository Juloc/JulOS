using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace JulOS.Backend.Services;

/// <summary>
/// Passwort-Hash mit Argon2id (64 MiB, 3 Durchgaenge). Format: argon2id$m=65536,t=3,p=1$salt$hash (Base64).
/// Die Parameter stehen im Hash, so kann man sie spaeter erhoehen; alte Hashes werden beim naechsten Login neu geschrieben.
/// Hoechstens drei Berechnungen gleichzeitig, damit viele Anmeldeversuche nicht den Speicher fressen.
/// </summary>
public sealed class PasswordHasher
{
    public const int MemoryKiB = 65536;
    public const int Iterations = 3;
    public const int Parallelism = 1;
    private const int KeyLength = 32;

    private static readonly SemaphoreSlim Gate = new(3);
    private readonly Lazy<Task<string>> _dummy;

    public PasswordHasher() => _dummy = new(() => HashAsync(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    /// <summary>Ein Hash zu einem unbekannten Passwort: so dauert die Pruefung bei unbekanntem Benutzer genauso lang.</summary>
    public Task<string> DummyHashAsync() => _dummy.Value;

    public async Task<string> HashAsync(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = await DeriveAsync(password, salt, MemoryKiB, Iterations, Parallelism, KeyLength);
        return $"argon2id$m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public async Task<(bool Ok, bool NeedsRehash)> VerifyAsync(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "argon2id") return (false, false);
        var p = parts[1].Split(',').Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        if (!int.TryParse(p.GetValueOrDefault("m"), out var m) || !int.TryParse(p.GetValueOrDefault("t"), out var t) || !int.TryParse(p.GetValueOrDefault("p"), out var par))
            return (false, false);
        var salt = Convert.FromBase64String(parts[2]);
        var want = Convert.FromBase64String(parts[3]);
        var got = await DeriveAsync(password, salt, m, t, par, want.Length);
        var ok = CryptographicOperations.FixedTimeEquals(got, want);
        return (ok, ok && (m < MemoryKiB || t < Iterations || par < Parallelism));
    }

    private static async Task<byte[]> DeriveAsync(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int length)
    {
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var argon = new Argon2id(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC)))
                {
                    Salt = salt, MemorySize = memoryKiB, Iterations = iterations, DegreeOfParallelism = parallelism,
                };
                return argon.GetBytes(length);
            });
        }
        finally { Gate.Release(); }
    }
}
