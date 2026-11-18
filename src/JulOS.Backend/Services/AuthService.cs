using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Services;

public sealed record AuthResult(bool Ok, int Status, string? Error = null, int RetryAfter = 0, string? User = null, string? Token = null, int RecoveryLeft = 0);

public sealed record SetupBegin(string Secret, string Uri, string[] Recovery);

public sealed record SetupBeginResult(bool Ok, int Status, string? Error = null, int RetryAfter = 0, SetupBegin? Data = null);

/// <summary>
/// Ersteinrichtung, Anmeldung und Sitzungen. Fehlermeldungen verraten nie, ob Benutzer, Passwort oder Code falsch war.
/// </summary>
public sealed class AuthService(UserStore users, SessionStore sessions, PasswordHasher hasher, LoginLimiter limiter, SetupCode setupCode, TimeProvider clock, ILogger<AuthService> log)
{
    private const string LoginFailed = "Anmeldung fehlgeschlagen. Benutzer, Passwort und Code prüfen.";

    public async Task<bool> SetupNeededAsync(CancellationToken ct = default) => !await users.AnyConfirmedAsync(ct);

    public async Task<SetupBeginResult> BeginSetupAsync(string? code, string? user, string? password, string ip, CancellationToken ct = default)
    {
        if (!await SetupNeededAsync(ct)) return new(false, 409, "Die Einrichtung ist schon abgeschlossen.");
        var key = "setup:" + ip;
        if (limiter.Wait(key) is var wait && wait > TimeSpan.Zero) return new(false, 429, "Zu viele Versuche. Bitte später erneut versuchen.", (int)Math.Ceiling(wait.TotalSeconds));
        if (!setupCode.Matches(code)) { limiter.Fail(key); log.LogWarning("Falscher Setup-Code von {Ip}", ip); return new(false, 403, "Der Setup-Code stimmt nicht. Er steht im Server-Log."); }

        var name = PasswordPolicy.NormalizeUserName(user);
        if (name is null) return new(false, 400, "Benutzername: 1 bis 32 Zeichen aus a-z, 0-9, Punkt, Strich und Unterstrich.");
        var problem = PasswordPolicy.Problem(password, name);
        if (problem.Length > 0) return new(false, 400, problem);

        var secret = Totp.NewSecret();
        var (plain, hashes) = RecoveryCodes.Generate();
        await users.CreateAsync(name, await hasher.HashAsync(password!), secret, confirmed: false, hashes, replacePending: true, ct);
        return new(true, 200, Data: new SetupBegin(secret, Totp.Uri(name, secret), plain));
    }

    public async Task<AuthResult> FinishSetupAsync(string? code, string? user, string? totp, string ip, string? userAgent, CancellationToken ct = default)
    {
        if (!await SetupNeededAsync(ct)) return new(false, 409, "Die Einrichtung ist schon abgeschlossen.");
        var key = "setup:" + ip;
        if (limiter.Wait(key) is var wait && wait > TimeSpan.Zero) return new(false, 429, "Zu viele Versuche. Bitte später erneut versuchen.", (int)Math.Ceiling(wait.TotalSeconds));
        if (!setupCode.Matches(code)) { limiter.Fail(key); return new(false, 403, "Der Setup-Code stimmt nicht."); }

        var name = PasswordPolicy.NormalizeUserName(user);
        var pending = name is null ? null : await users.FindByNameAsync(name, ct);
        if (pending is null || pending.TotpConfirmed) return new(false, 400, "Bitte zuerst Benutzer und Passwort eingeben.");
        var step = Totp.Check(pending.TotpSecret, (totp ?? "").Replace(" ", ""), pending.TotpLastStep, clock.GetUtcNow());
        if (step is null || !await users.TryAdvanceTotpStepAsync(pending.Id, step.Value, ct))
        {
            limiter.Fail(key);
            return new(false, 401, "Der Code stimmt nicht. Prüfe den Eintrag in der App und die Uhrzeit des Handys.");
        }

        await users.ConfirmAsync(pending.Id, ct);
        setupCode.MarkUsed();
        var token = await sessions.CreateAsync(pending.Id, ip, userAgent, ct);
        log.LogInformation("Ersteinrichtung abgeschlossen, Konto {User} angelegt.", pending.Name);
        return new(true, 200, User: pending.Name, Token: token, RecoveryLeft: RecoveryCodes.Count);
    }

    public async Task<AuthResult> LoginAsync(string? userName, string? password, string? code, string ip, string? userAgent, CancellationToken ct = default)
    {
        var name = PasswordPolicy.NormalizeUserName(userName);
        var wait = TimeSpan.Zero;
        foreach (var key in name is null ? new[] { "ip:" + ip } : ["ip:" + ip, "user:" + name]) { var w = limiter.Wait(key); if (w > wait) wait = w; }
        if (wait > TimeSpan.Zero) return new(false, 429, "Zu viele Versuche. Bitte später erneut versuchen.", (int)Math.Ceiling(wait.TotalSeconds));
        if (password is null || password.Length is 0 or > PasswordPolicy.MaxLength) return new(false, 400, "Eingabe ungültig.");

        var user = name is null ? null : await users.FindByNameAsync(name, ct);
        if (user is { TotpConfirmed: false }) user = null;
        var (passwordOk, needsRehash) = await hasher.VerifyAsync(password, user?.PasswordHash ?? await hasher.DummyHashAsync());

        var secondOk = false; long? step = null; string? recoveryHash = null;
        if (user is not null && passwordOk && !string.IsNullOrWhiteSpace(code))
        {
            if (RecoveryCodes.LooksLikeRecoveryCode(code)) recoveryHash = RecoveryCodes.Hash(code);
            else step = Totp.Check(user.TotpSecret, code.Replace(" ", ""), user.TotpLastStep, clock.GetUtcNow());
        }
        if (user is not null && passwordOk)
        {
            // der verbrauchende Schritt ist atomar: derselbe Code geht auch bei zwei gleichzeitigen Anfragen nur einmal
            if (step is not null) secondOk = await users.TryAdvanceTotpStepAsync(user.Id, step.Value, ct);
            else if (recoveryHash is not null) secondOk = await users.ConsumeRecoveryCodeAsync(user.Id, recoveryHash, ct);
        }

        if (user is null || !passwordOk || !secondOk)
        {
            limiter.Fail("ip:" + ip);
            if (name is not null) limiter.Fail("user:" + name);
            log.LogWarning("Anmeldung fehlgeschlagen für {User} von {Ip}", name ?? "?", ip);
            return new(false, 401, LoginFailed);
        }

        if (needsRehash) await users.SetPasswordAsync(user.Id, await hasher.HashAsync(password), endSessions: false, ct);
        limiter.Succeed("user:" + user.Name.ToLowerInvariant());
        var token = await sessions.CreateAsync(user.Id, ip, userAgent, ct);
        log.LogInformation("Angemeldet: {User} von {Ip}{Recovery}", user.Name, ip, recoveryHash is not null ? " (Wiederherstellungscode)" : "");
        return new(true, 200, User: user.Name, Token: token, RecoveryLeft: await users.RecoveryLeftAsync(user.Id, ct));
    }

    public Task<bool> IsSessionAliveAsync(Guid sessionId, CancellationToken ct = default) => sessions.IsAliveAsync(sessionId, ct);

    public Task<SessionIdentity?> ResolveSessionAsync(string token, bool rotate, CancellationToken ct = default) => sessions.ResolveAsync(token, rotate, ct);

    public Task LogoutAsync(string hash, CancellationToken ct = default) => sessions.DeleteAsync(hash, ct);

    public Task<int> LogoutOthersAsync(Guid userId, string keepHash, CancellationToken ct = default) => sessions.DeleteOthersAsync(userId, keepHash, ct);

    public Task<IReadOnlyList<SessionListItem>> ListSessionsAsync(Guid userId, string currentHash, CancellationToken ct = default) => sessions.ListAsync(userId, currentHash, ct);
}
