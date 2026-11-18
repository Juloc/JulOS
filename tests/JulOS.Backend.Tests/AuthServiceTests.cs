using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Backend.Tests;

public sealed class AuthServiceTests
{
    private const string Password = "korrektes pferd batterie";

    private sealed class Rig(TestDb db)
    {
        public ManualTime Time { get; } = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 * 30 + 1));
        public UserStore Users { get; } = new(db.Data);
        public SessionStore Sessions { get; } = new(db.Data);
        public LoginLimiter Limiter { get; } = new();
        public SetupCode Setup { get; } = new("AAAA-BBBB-CCCC");
        public AuthService Auth { get; private set; } = null!;

        public static Rig Create(TestDb db)
        {
            var r = new Rig(db);
            r.Auth = new AuthService(r.Users, r.Sessions, new PasswordHasher(), r.Limiter, r.Setup, r.Time, NullLogger<AuthService>.Instance);
            return r;
        }

        public string CodeFor(string secret, int stepOffset = 0) => Totp.Hotp(Base32.Decode(secret), Time.GetUtcNow().ToUnixTimeSeconds() / 30 + stepOffset);

        public async Task<(string Secret, string[] Recovery, string Session)> DoSetupAsync(string user = "jul")
        {
            var begin = await Auth.BeginSetupAsync("AAAA-BBBB-CCCC", user, Password, "1.2.3.4");
            Assert.True(begin.Ok, begin.Error);
            var done = await Auth.FinishSetupAsync("AAAA-BBBB-CCCC", user, CodeFor(begin.Data!.Secret), "1.2.3.4", "test");
            Assert.True(done.Ok, done.Error);
            return (begin.Data.Secret, begin.Data.Recovery, done.Token!);
        }
    }

    [Fact]
    public async Task First_run_setup_needs_the_code_and_a_valid_authenticator_code_and_then_closes()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        Assert.True(await r.Auth.SetupNeededAsync());

        Assert.Equal(403, (await r.Auth.BeginSetupAsync("WRONG-CODE-0000", "jul", Password, "1.1.1.1")).Status);
        Assert.Equal(400, (await r.Auth.BeginSetupAsync("AAAA-BBBB-CCCC", "J U L", Password, "1.1.1.1")).Status);
        Assert.Equal(400, (await r.Auth.BeginSetupAsync("AAAA-BBBB-CCCC", "jul", "kurz", "1.1.1.1")).Status);

        var first = await r.Auth.BeginSetupAsync("aaaa bbbb cccc", "jul", Password, "1.1.1.1");
        Assert.True(first.Ok);
        Assert.Equal(10, first.Data!.Recovery.Length);
        Assert.False(await r.Users.AnyConfirmedAsync(), "vor der Bestaetigung gibt es noch kein gueltiges Konto");
        Assert.Equal(401, (await r.Auth.LoginAsync("jul", Password, r.CodeFor(first.Data.Secret), "9.9.9.9", "t")).Status);

        var second = await r.Auth.BeginSetupAsync("AAAA-BBBB-CCCC", "jul", Password, "1.1.1.1");       // neu beginnen ersetzt das unbestaetigte Konto
        Assert.NotEqual(first.Data.Secret, second.Data!.Secret);
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from users"));

        Assert.Equal(401, (await r.Auth.FinishSetupAsync("AAAA-BBBB-CCCC", "jul", "000000", "1.1.1.1", "t")).Status);
        Assert.Equal(401, (await r.Auth.FinishSetupAsync("AAAA-BBBB-CCCC", "jul", r.CodeFor(first.Data.Secret), "1.1.1.1", "t")).Status);   // Code vom ersetzten Schluessel
        var done = await r.Auth.FinishSetupAsync("AAAA-BBBB-CCCC", "jul", r.CodeFor(second.Data.Secret), "1.1.1.1", "t");
        Assert.True(done.Ok);
        Assert.Equal("jul", done.User);
        Assert.Equal("jul", (await r.Auth.ResolveSessionAsync(done.Token!, rotate: true))!.UserName);

        Assert.False(await r.Auth.SetupNeededAsync());
        Assert.Equal(409, (await r.Auth.BeginSetupAsync("AAAA-BBBB-CCCC", "zwei", Password, "1.1.1.1")).Status);
        Assert.Equal(409, (await r.Auth.FinishSetupAsync("AAAA-BBBB-CCCC", "jul", "123456", "1.1.1.1", "t")).Status);
    }

    [Fact]
    public async Task Setup_is_rate_limited_per_address()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        for (var i = 0; i < 5; i++) Assert.Equal(403, (await r.Auth.BeginSetupAsync("WRONG-CODE-000" + i, "jul", Password, "5.5.5.5")).Status);
        var locked = await r.Auth.BeginSetupAsync("AAAA-BBBB-CCCC", "jul", Password, "5.5.5.5");
        Assert.Equal(429, locked.Status);
        Assert.True(locked.RetryAfter > 0);
        Assert.True((await r.Auth.BeginSetupAsync("AAAA-BBBB-CCCC", "jul", Password, "6.6.6.6")).Ok, "andere Adressen sind nicht betroffen");
    }

    [Fact]
    public async Task Login_needs_password_and_code_and_never_says_which_was_wrong()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        var (secret, _, _) = await r.DoSetupAsync();
        r.Time.Advance(TimeSpan.FromSeconds(60));

        var wrongPassword = await r.Auth.LoginAsync("jul", "falsch falsch falsch", r.CodeFor(secret), "2.2.2.2", "t");
        var wrongCode = await r.Auth.LoginAsync("jul", Password, "000000", "2.2.2.3", "t");
        var unknown = await r.Auth.LoginAsync("gibtsnicht", Password, r.CodeFor(secret), "2.2.2.4", "t");
        var noCode = await r.Auth.LoginAsync("jul", Password, null, "2.2.2.5", "t");
        Assert.All(new[] { wrongPassword, wrongCode, unknown, noCode }, x => { Assert.False(x.Ok); Assert.Equal(401, x.Status); });
        Assert.Single(new[] { wrongPassword.Error, wrongCode.Error, unknown.Error, noCode.Error }.Distinct());

        var ok = await r.Auth.LoginAsync("JUL", Password, r.CodeFor(secret), "2.2.2.6", "Pixel");
        Assert.True(ok.Ok);
        Assert.Equal("jul", ok.User);
        var replay = await r.Auth.LoginAsync("jul", Password, r.CodeFor(secret), "2.2.2.7", "t");
        Assert.False(replay.Ok);                                                  // derselbe Code gilt nur einmal
        Assert.True((await r.Auth.LoginAsync("jul", Password, r.CodeFor(secret, 1), "2.2.2.8", "t")).Ok);
    }

    [Fact]
    public async Task The_same_authenticator_code_works_once_even_with_parallel_requests()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        var (secret, _, _) = await r.DoSetupAsync();
        r.Time.Advance(TimeSpan.FromSeconds(60));
        var code = r.CodeFor(secret);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => r.Auth.LoginAsync("jul", Password, code, "3.3.3." + i, "t")));
        Assert.Equal(1, results.Count(x => x.Ok));
    }

    [Fact]
    public async Task Recovery_codes_work_once_and_are_counted()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        var (_, recovery, _) = await r.DoSetupAsync();
        var first = await r.Auth.LoginAsync("jul", Password, recovery[0].ToLowerInvariant(), "4.4.4.1", "t");
        Assert.True(first.Ok);
        Assert.Equal(9, first.RecoveryLeft);
        Assert.False((await r.Auth.LoginAsync("jul", Password, recovery[0], "4.4.4.2", "t")).Ok);
        Assert.True((await r.Auth.LoginAsync("jul", Password, recovery[1], "4.4.4.3", "t")).Ok);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_correct_credentials()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        var (secret, _, _) = await r.DoSetupAsync();
        r.Time.Advance(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 5; i++) Assert.Equal(401, (await r.Auth.LoginAsync("jul", "falsch falsch falsch", "000000", "7.7.7." + i, "t")).Status);
        var locked = await r.Auth.LoginAsync("jul", Password, r.CodeFor(secret), "7.7.7.99", "t");
        Assert.Equal(429, locked.Status);
        Assert.True(locked.RetryAfter >= 1);
    }

    [Fact]
    public async Task Sessions_rotate_keep_a_grace_period_expire_and_can_be_ended()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        var (_, _, token) = await r.DoSetupAsync();

        var plain = await r.Sessions.ResolveAsync(token, rotate: true);
        Assert.NotNull(plain);
        Assert.Null(plain!.NewToken);
        Assert.Equal(SessionTokens.Hash(token), plain.Hash);
        Assert.Null(await r.Sessions.ResolveAsync("falsch", rotate: true));

        await db.ExecAsync("update sessions set rotated_at = now() - interval '13 hours'");
        Assert.Null((await r.Sessions.ResolveAsync(token, rotate: false))!.NewToken);                // ohne Erneuerung (WebSocket)
        var rotated = await r.Sessions.ResolveAsync(token, rotate: true);
        Assert.NotNull(rotated!.NewToken);
        Assert.NotEqual(token, rotated.NewToken);
        Assert.Equal(1L, await db.ScalarAsync<long>("select count(*) from sessions"));
        Assert.Equal("jul", (await r.Sessions.ResolveAsync(rotated.NewToken!, rotate: true))!.UserName);
        var viaOld = await r.Sessions.ResolveAsync(token, rotate: true);                              // altes Cookie: Schonfrist
        Assert.NotNull(viaOld);
        Assert.Null(viaOld!.NewToken);
        Assert.Equal(rotated.Hash, viaOld.Hash);

        await db.ExecAsync("update sessions set prev_until = now() - interval '1 minute'");
        Assert.Null(await r.Sessions.ResolveAsync(token, rotate: true));                              // Schonfrist vorbei
        Assert.NotNull(await r.Sessions.ResolveAsync(rotated.NewToken!, rotate: true));

        await db.ExecAsync("update sessions set last_seen = now() - interval '31 days'");
        Assert.Null(await r.Sessions.ResolveAsync(rotated.NewToken!, rotate: true));                  // Leerlauf
        await db.ExecAsync("update sessions set last_seen = now(), created_at = now() - interval '181 days'");
        Assert.Null(await r.Sessions.ResolveAsync(rotated.NewToken!, rotate: true));                  // Hoechstdauer
        Assert.Equal(1, await r.Sessions.PurgeAsync());
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from sessions"));
    }

    [Fact]
    public async Task Logout_logout_others_listing_and_password_change_end_sessions()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        var (secret, _, first) = await r.DoSetupAsync();
        r.Time.Advance(TimeSpan.FromSeconds(60));
        var second = (await r.Auth.LoginAsync("jul", Password, r.CodeFor(secret), "8.8.8.8", "Zweites Geraet")).Token!;
        var id = (await r.Sessions.ResolveAsync(first, rotate: true))!;

        var list = await r.Auth.ListSessionsAsync(id.UserId, id.Hash);
        Assert.Equal(2, list.Count);
        Assert.Single(list, s => s.Current);
        Assert.Contains(list, s => s.UserAgent == "Zweites Geraet" && s.Ip == "8.8.8.8");

        Assert.Equal(1, await r.Auth.LogoutOthersAsync(id.UserId, id.Hash));
        Assert.Null(await r.Sessions.ResolveAsync(second, rotate: true));
        Assert.NotNull(await r.Sessions.ResolveAsync(first, rotate: true));

        await r.Auth.LogoutAsync(id.Hash);
        Assert.Null(await r.Sessions.ResolveAsync(first, rotate: true));

        r.Time.Advance(TimeSpan.FromSeconds(60));
        var third = (await r.Auth.LoginAsync("jul", Password, r.CodeFor(secret), "8.8.8.9", "t")).Token!;
        var user = (await r.Users.FindByNameAsync("jul"))!;
        await r.Users.SetPasswordAsync(user.Id, await new PasswordHasher().HashAsync("ganz anderes passwort 42"), endSessions: true);
        Assert.Null(await r.Sessions.ResolveAsync(third, rotate: true));
        r.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.False((await r.Auth.LoginAsync("jul", Password, r.CodeFor(secret), "8.8.8.10", "t")).Ok);
        Assert.True((await r.Auth.LoginAsync("jul", "ganz anderes passwort 42", r.CodeFor(secret), "8.8.8.11", "t")).Ok);
    }

    [Fact]
    public async Task A_replaced_authenticator_ends_sessions_and_old_codes_stop_working()
    {
        await using var db = await TestDb.CreateAsync();
        var r = Rig.Create(db);
        var (oldSecret, _, token) = await r.DoSetupAsync();
        var user = (await r.Users.FindByNameAsync("jul"))!;
        var newSecret = Totp.NewSecret();
        var (_, hashes) = RecoveryCodes.Generate();
        await r.Users.ReplaceTotpAsync(user.Id, newSecret, hashes);
        Assert.Null(await r.Sessions.ResolveAsync(token, rotate: true));
        r.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.False((await r.Auth.LoginAsync("jul", Password, r.CodeFor(oldSecret), "9.1.1.1", "t")).Ok);
        Assert.True((await r.Auth.LoginAsync("jul", Password, r.CodeFor(newSecret), "9.1.1.2", "t")).Ok);
    }
}
