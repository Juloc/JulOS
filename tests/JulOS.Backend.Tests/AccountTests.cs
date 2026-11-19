using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;

namespace JulOS.Backend.Tests;

/// <summary>Konto in der Oberflaeche: Passwort aendern und Authenticator neu einrichten (immer mit dem aktuellen Passwort).</summary>
public sealed class AccountTests
{
    private const string Password = "korrektes pferd batterie";
    private const string NewPassword = "ganz anderes passwort 42";

    private static async Task<(AuthServiceTests.Rig Rig, string Secret, string[] Recovery, string Token, SessionIdentity Me, string Other)> StartAsync(TestDb db)
    {
        var r = AuthServiceTests.Rig.Create(db);
        var (secret, recovery, token) = await r.DoSetupAsync();
        var me = (await r.Sessions.ResolveAsync(token, rotate: false))!;
        r.Time.Advance(TimeSpan.FromSeconds(60));
        var other = (await r.Auth.LoginAsync("jul", Password, r.CodeFor(secret), "7.7.7.7", "Zweites Geraet")).Token!;
        return (r, secret, recovery, token, me, other);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_keeps_only_this_device_signed_in()
    {
        await using var db = await TestDb.CreateAsync();
        var (r, secret, _, token, me, other) = await StartAsync(db);

        Assert.Equal(403, (await r.Auth.ChangePasswordAsync(me, "falsches passwort 123", NewPassword, "1.2.3.4")).Status);
        Assert.Equal(400, (await r.Auth.ChangePasswordAsync(me, null, NewPassword, "1.2.3.4")).Status);
        Assert.Equal(400, (await r.Auth.ChangePasswordAsync(me, Password, "kurz", "1.2.3.4")).Status);
        Assert.Equal(400, (await r.Auth.ChangePasswordAsync(me, Password, Password, "1.2.3.4")).Status);
        Assert.Equal(400, (await r.Auth.ChangePasswordAsync(me, Password, "jul jul jul jul jul", "1.2.3.4")).Status);   // enthaelt den Benutzernamen
        r.Limiter.Succeed("acct:" + me.UserId); r.Limiter.Succeed("ip:1.2.3.4");

        var ok = await r.Auth.ChangePasswordAsync(me, Password, NewPassword, "1.2.3.4");
        Assert.True(ok.Ok, ok.Error);
        Assert.NotNull(await r.Sessions.ResolveAsync(token, rotate: false));
        Assert.Null(await r.Sessions.ResolveAsync(other, rotate: false));

        r.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.False((await r.Auth.LoginAsync("jul", Password, r.CodeFor(secret), "9.9.9.1", "t")).Ok);
        r.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.True((await r.Auth.LoginAsync("jul", NewPassword, r.CodeFor(secret), "9.9.9.2", "t")).Ok);
    }

    [Fact]
    public async Task Wrong_current_passwords_are_counted_and_then_locked_even_for_the_right_one()
    {
        await using var db = await TestDb.CreateAsync();
        var (r, _, _, _, me, _) = await StartAsync(db);
        for (var i = 0; i < 5; i++) Assert.Equal(403, (await r.Auth.ChangePasswordAsync(me, "falsches passwort " + i, NewPassword, "2.2.2.2")).Status);
        var locked = await r.Auth.ChangePasswordAsync(me, Password, NewPassword, "2.2.2.2");
        Assert.Equal(429, locked.Status);
        Assert.True(locked.RetryAfter > 0);
        Assert.Equal(429, (await r.Auth.BeginTotpChangeAsync(me, Password, "3.3.3.3")).Status);   // gilt fuers Konto, nicht nur fuer die Adresse
    }

    [Fact]
    public async Task A_new_authenticator_only_counts_after_its_first_code_and_brings_new_recovery_codes()
    {
        await using var db = await TestDb.CreateAsync();
        var (r, oldSecret, oldRecovery, token, me, other) = await StartAsync(db);

        Assert.Equal(403, (await r.Auth.BeginTotpChangeAsync(me, "falsches passwort 123", "1.2.3.4")).Status);
        Assert.Equal(400, (await r.Auth.FinishTotpChangeAsync(me, "123456", "1.2.3.4")).Status);            // ohne Anfang
        r.Limiter.Succeed("acct:" + me.UserId); r.Limiter.Succeed("ip:1.2.3.4");

        var begin = await r.Auth.BeginTotpChangeAsync(me, Password, "1.2.3.4");
        Assert.True(begin.Ok, begin.Error);
        Assert.NotEqual(oldSecret, begin.Data!.Secret);
        Assert.StartsWith("otpauth://totp/", begin.Data.Uri);
        Assert.Contains(begin.Data.Secret, begin.Data.Uri);

        // bis zur Bestaetigung gilt noch der alte Schluessel
        Assert.Equal(401, (await r.Auth.FinishTotpChangeAsync(me, "000000", "1.2.3.4")).Status);
        Assert.Equal(401, (await r.Auth.FinishTotpChangeAsync(me, r.CodeFor(oldSecret), "1.2.3.4")).Status);
        r.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.True((await r.Auth.LoginAsync("jul", Password, r.CodeFor(oldSecret), "9.8.8.8", "t")).Ok);

        var newCode = r.CodeFor(begin.Data.Secret);
        var done = await r.Auth.FinishTotpChangeAsync(me, newCode, "1.2.3.4");
        Assert.True(done.Ok, done.Error);
        Assert.Equal(RecoveryCodes.Count, done.Recovery!.Length);
        Assert.Empty(done.Recovery.Intersect(oldRecovery));
        Assert.Equal(RecoveryCodes.Count, await r.Users.RecoveryLeftAsync(me.UserId));
        Assert.NotNull(await r.Sessions.ResolveAsync(token, rotate: false));
        Assert.Null(await r.Sessions.ResolveAsync(other, rotate: false));

        // einmal eingeloest: nicht noch einmal, und der alte Schluessel und die alten Codes sind weg
        Assert.Equal(400, (await r.Auth.FinishTotpChangeAsync(me, newCode, "1.2.3.4")).Status);
        r.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.False((await r.Auth.LoginAsync("jul", Password, r.CodeFor(oldSecret), "9.8.8.1", "t")).Ok);
        Assert.False((await r.Auth.LoginAsync("jul", Password, oldRecovery[0], "9.8.8.2", "t")).Ok);
        Assert.True((await r.Auth.LoginAsync("jul", Password, r.CodeFor(begin.Data.Secret), "9.8.8.3", "t")).Ok);
        Assert.True((await r.Auth.LoginAsync("jul", Password, done.Recovery[0], "9.8.8.4", "t")).Ok);
    }

    [Fact]
    public async Task The_code_that_confirmed_the_new_authenticator_cannot_log_in_again()
    {
        await using var db = await TestDb.CreateAsync();
        var (r, _, _, _, me, _) = await StartAsync(db);
        var begin = (await r.Auth.BeginTotpChangeAsync(me, Password, "1.2.3.4")).Data!;
        var code = r.CodeFor(begin.Secret);
        Assert.True((await r.Auth.FinishTotpChangeAsync(me, code, "1.2.3.4")).Ok);
        Assert.False((await r.Auth.LoginAsync("jul", Password, code, "4.4.4.4", "t")).Ok);   // derselbe Zeitschritt ist verbraucht
    }

    [Fact]
    public async Task An_unconfirmed_new_key_expires()
    {
        await using var db = await TestDb.CreateAsync();
        var (r, _, _, _, me, _) = await StartAsync(db);
        var begin = (await r.Auth.BeginTotpChangeAsync(me, Password, "1.2.3.4")).Data!;
        r.Time.Advance(AuthService.TotpChangeLifetime + TimeSpan.FromSeconds(1));
        var late = await r.Auth.FinishTotpChangeAsync(me, r.CodeFor(begin.Secret), "1.2.3.4");
        Assert.Equal(400, late.Status);
        Assert.Contains("abgelaufen", late.Error);
    }
}
