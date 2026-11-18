using System.Text;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;

namespace JulOS.Backend.Tests;

public sealed class LogicTests
{
    [Fact]
    public void Base32_matches_the_rfc_example_and_round_trips()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(secret));
        Assert.Equal(secret, Base32.Decode("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));
        for (var n = 0; n < 40; n++)
        {
            var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(n);
            Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        }
        Assert.Throws<FormatException>(() => Base32.Decode("ab1"));
    }

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Hotp_matches_the_rfc_6238_test_vectors(long seconds, string expected) =>
        Assert.Equal(expected, Totp.Hotp(Encoding.ASCII.GetBytes("12345678901234567890"), seconds / 30, digits: 8));

    [Fact]
    public void Totp_check_accepts_neighbours_rejects_far_and_used_steps()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var step = now.ToUnixTimeSeconds() / 30;
        string At(long s) => Totp.Hotp(Base32.Decode(secret), s);
        Assert.Equal(step, Totp.Check(secret, At(step), 0, now));
        Assert.Equal(step - 1, Totp.Check(secret, At(step - 1), 0, now));
        Assert.Equal(step + 1, Totp.Check(secret, At(step + 1), 0, now));
        Assert.Null(Totp.Check(secret, At(step + 2), 0, now));
        Assert.Null(Totp.Check(secret, At(step - 2), 0, now));
        Assert.Null(Totp.Check(secret, At(step), step, now));          // verbraucht
        Assert.Equal(step + 1, Totp.Check(secret, At(step + 1), step, now));
        Assert.Null(Totp.Check(secret, "12345", 0, now));
        Assert.Null(Totp.Check(secret, "12a456", 0, now));
        Assert.StartsWith("otpauth://totp/JulOS:jul?secret=" + secret, Totp.Uri("jul", secret));
    }

    [Fact]
    public void Recovery_codes_are_unique_normalised_and_hashed()
    {
        var (plain, hashes) = RecoveryCodes.Generate();
        Assert.Equal(10, plain.Length);
        Assert.Equal(10, plain.Distinct().Count());
        Assert.All(plain, c => Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$", c));
        Assert.Equal(hashes[0], RecoveryCodes.Hash(plain[0].ToLowerInvariant().Replace("-", " ")));
        Assert.True(RecoveryCodes.LooksLikeRecoveryCode(plain[3]));
        Assert.False(RecoveryCodes.LooksLikeRecoveryCode("123456"));
    }

    [Fact]
    public void Limiter_locks_with_growing_wait_and_forgets()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var limiter = new LoginLimiter(time);
        for (var i = 0; i < 4; i++) { limiter.Fail("user:a"); Assert.Equal(TimeSpan.Zero, limiter.Wait("user:a")); }
        limiter.Fail("user:a");
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.Wait("user:a"));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(TimeSpan.Zero, limiter.Wait("user:a"));
        limiter.Fail("user:a");
        Assert.Equal(TimeSpan.FromSeconds(60), limiter.Wait("user:a"));
        for (var i = 0; i < 30; i++) limiter.Fail("user:a");
        Assert.Equal(TimeSpan.FromMinutes(15), limiter.Wait("user:a"));         // Obergrenze
        Assert.Equal(TimeSpan.Zero, limiter.Wait("user:b"));
        limiter.Succeed("user:a");
        Assert.Equal(TimeSpan.Zero, limiter.Wait("user:a"));
        for (var i = 0; i < 9; i++) limiter.Fail("ip:x");
        Assert.Equal(TimeSpan.Zero, limiter.Wait("ip:x"));                      // Adressen sind grosszuegiger
        limiter.Fail("ip:x");
        Assert.True(limiter.Wait("ip:x") > TimeSpan.Zero);
        time.Advance(TimeSpan.FromHours(2));
        limiter.Fail("user:c");
        limiter.Sweep();
        Assert.Equal(TimeSpan.Zero, limiter.Wait("ip:x"));
    }

    [Fact]
    public void Password_and_user_name_rules()
    {
        Assert.Equal("jul", PasswordPolicy.NormalizeUserName("  Jul "));
        Assert.Null(PasswordPolicy.NormalizeUserName("a b"));
        Assert.Null(PasswordPolicy.NormalizeUserName("../x"));
        Assert.Null(PasswordPolicy.NormalizeUserName(new string('a', 33)));
        Assert.NotEqual("", PasswordPolicy.Problem("kurz", "jul"));
        Assert.NotEqual("", PasswordPolicy.Problem("aaaaaaaaaaaaaaaa", "jul"));
        Assert.NotEqual("", PasswordPolicy.Problem("mein-JUL-passwort", "jul"));
        Assert.Equal("", PasswordPolicy.Problem("korrektes pferd batterie", "jul"));
    }

    [Fact]
    public async Task Password_hash_is_argon2id_salted_and_verifiable()
    {
        var hasher = new PasswordHasher();
        var a = await hasher.HashAsync("korrektes pferd batterie");
        var b = await hasher.HashAsync("korrektes pferd batterie");
        Assert.StartsWith("argon2id$m=65536,t=3,p=1$", a);
        Assert.NotEqual(a, b);
        Assert.Equal((true, false), await hasher.VerifyAsync("korrektes pferd batterie", a));
        Assert.Equal((false, false), await hasher.VerifyAsync("korrektes pferd batteriE", a));
        Assert.Equal((false, false), await hasher.VerifyAsync("x", "kaputt"));
        // alter, schwaecherer Hash (8 MiB, 2 Durchgaenge): stimmt das Passwort, wird er als "neu schreiben" gemeldet
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        using var weakArgon = new Konscious.Security.Cryptography.Argon2id(Encoding.UTF8.GetBytes("korrektes pferd batterie")) { Salt = salt, MemorySize = 8192, Iterations = 2, DegreeOfParallelism = 1 };
        var weak = $"argon2id$m=8192,t=2,p=1${Convert.ToBase64String(salt)}${Convert.ToBase64String(weakArgon.GetBytes(32))}";
        Assert.Equal((true, true), await hasher.VerifyAsync("korrektes pferd batterie", weak));
    }

    [Fact]
    public void Setup_code_is_random_grouped_single_use_and_tolerant_in_typing()
    {
        var code = new SetupCode();
        Assert.Matches("^[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}$", code.Value);
        Assert.NotEqual(code.Value, new SetupCode().Value);
        Assert.True(code.Matches(code.Value.ToLowerInvariant().Replace("-", " ")));
        Assert.False(code.Matches("AAAA-AAAA-AAAA"));
        Assert.False(code.Matches(null));
        code.MarkUsed();
        Assert.False(code.Matches(code.Value));
    }
}
