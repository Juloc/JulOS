using System.Collections.Concurrent;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Services;

public sealed record AuthResult(bool Ok, int Status, string? Error = null, int RetryAfter = 0, string? User = null, string? Token = null, int RecoveryLeft = 0);

public sealed record SetupBegin(string Secret, string Uri, string[] Recovery);

public sealed record TotpChange(string Secret, string Uri);

public sealed record TotpChangeResult(bool Ok, int Status, string? Error = null, int RetryAfter = 0, TotpChange? Data = null);

public sealed record RecoveryResult(bool Ok, int Status, string? Error = null, int RetryAfter = 0, string[]? Recovery = null);

public sealed record SetupBeginResult(bool Ok, int Status, string? Error = null, int RetryAfter = 0, SetupBegin? Data = null);

/// <summary>
/// Ersteinrichtung, Anmeldung und Sitzungen. Fehlermeldungen verraten nie, ob Benutzer, Passwort oder Code falsch war.
/// </summary>
public sealed class AuthService(UserStore users, SessionStore sessions, PasswordHasher hasher, LoginLimiter limiter, SetupCode setupCode, TimeProvider clock, ILogger<AuthService> log,
    SecurityConfig? security = null, SecurityAuditStore? audit = null)
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
        var token = await sessions.CreateAsync(pending.Id, ip, userAgent, ct, LoginGrant());
        log.LogInformation("Ersteinrichtung abgeschlossen, Konto {User} angelegt.", pending.Name);
        return new(true, 200, User: pending.Name, Token: token, RecoveryLeft: RecoveryCodes.Count);
    }

    /// <summary>So viele Anmeldungen einer Adresse duerfen gleichzeitig geprueft werden (die Passwortpruefung braucht 64 MiB je Versuch).</summary>
    public const int MaxLoginsInFlightPerAddress = 3;

    public async Task<AuthResult> LoginAsync(string? userName, string? password, string? code, string ip, string? userAgent, CancellationToken ct = default)
    {
        var busy = "ip:" + ip;
        if (!limiter.TryBegin(busy, MaxLoginsInFlightPerAddress)) return new(false, 429, "Zu viele Versuche. Bitte später erneut versuchen.", 5);
        try { return await LoginCoreAsync(userName, password, code, ip, userAgent, ct); }
        finally { limiter.End(busy); }
    }

    private async Task<AuthResult> LoginCoreAsync(string? userName, string? password, string? code, string ip, string? userAgent, CancellationToken ct)
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
        var token = await sessions.CreateAsync(user.Id, ip, userAgent, ct, LoginGrant());
        log.LogInformation("Angemeldet: {User} von {Ip}{Recovery}", user.Name, ip, recoveryHash is not null ? " (Wiederherstellungscode)" : "");
        return new(true, 200, User: user.Name, Token: token, RecoveryLeft: await users.RecoveryLeftAsync(user.Id, ct));
    }

    // ---- Zweiter Faktor fuer Aktionen (Step-up, docs/SECURITY-REVIEW.md F-04) ----

    /// <summary>
    /// Eine Anmeldung mit Code aus der Authenticator-App ist selbst ein frischer zweiter Faktor: sie gibt die Sitzung fuer das Zeitfenster frei (bei "immer fragen" nicht).
    /// Ein gestohlenes Cookie, das erst spaeter benutzt wird, hat diese Freigabe nicht mehr.
    /// </summary>
    internal DateTimeOffset? LoginGrant() => security?.StepUp is { Enabled: true, Minutes: > 0 } s ? clock.GetUtcNow().AddMinutes(s.Minutes) : null;

    /// <summary>Ob die Sitzung Aktionen ausfuehren darf: die Freigabe ist abgeschaltet oder gilt noch. Eine Freigabe "fuer eine Aktion" wird verbraucht.</summary>
    /// <param name="always">Der Weg verlangt die Freigabe auch bei ausgeschaltetem Schalter (Anmelde-Zentrale: wer sie verwaltet, kann sich in jeder App als admin anmelden).</param>
    public async Task<bool> StepUpFreshAsync(SessionIdentity who, CancellationToken ct = default, bool always = false) =>
        (!always && security is not { StepUp.Enabled: true }) || await sessions.TakeStepUpAsync(who.Id, clock.GetUtcNow(), ct);

    public sealed record StepUpResult(bool Ok, int Status, string? Error = null, int RetryAfter = 0, DateTimeOffset? Until = null, int Minutes = 0);

    /// <summary>
    /// Ein frischer Code aus der Authenticator-App (oder ein Wiederherstellungscode) gibt die Sitzung fuer das Zeitfenster frei. Wie beim Anmelden: ein Zeitschritt gilt nur einmal
    /// (auch nicht der, mit dem man sich eben angemeldet hat: man wartet auf den naechsten), Fehlversuche zaehlen je Konto und je Adresse und sperren mit wachsender Wartezeit.
    /// </summary>
    public async Task<StepUpResult> ConfirmStepUpAsync(SessionIdentity who, string? code, string ip, CancellationToken ct = default)
    {
        var keys = new[] { "acct:" + who.UserId, "ip:" + ip };
        var wait = keys.Max(limiter.Wait);
        if (wait > TimeSpan.Zero) return new(false, 429, TooMany, (int)Math.Ceiling(wait.TotalSeconds));
        var user = await users.FindByIdAsync(who.UserId, ct);
        if (user is null) return new(false, 401, "Nicht angemeldet.");
        var plain = (code ?? "").Replace(" ", "");
        var ok = false;
        if (plain.Length is > 0 and <= 20)
        {
            if (RecoveryCodes.LooksLikeRecoveryCode(plain)) ok = await users.ConsumeRecoveryCodeAsync(user.Id, RecoveryCodes.Hash(plain), ct);
            else if (Totp.Check(user.TotpSecret, plain, user.TotpLastStep, clock.GetUtcNow()) is { } step) ok = await users.TryAdvanceTotpStepAsync(user.Id, step, ct);
        }
        if (!ok)
        {
            foreach (var k in keys) limiter.Fail(k);
            log.LogWarning("Freigabe für Aktionen: falscher Code für {User} von {Ip}", user.Name, ip);
            await AuditAsync(user.Name, "step_up.failed");
            return new(false, 401, "Der Code stimmt nicht. Prüfe den Eintrag in der App und die Uhrzeit des Handys, ein Code gilt nur einmal.");
        }
        foreach (var k in keys) limiter.Succeed(k);
        return await GrantStepUpAsync(who, user.Name, null, ct);
    }

    /// <summary>Gibt die Sitzung nach einem bestandenen Nachweis (Code oder Passkey) fuer das Zeitfenster frei. <paramref name="via"/> steht nur im Verlauf.</summary>
    internal async Task<StepUpResult> GrantStepUpAsync(SessionIdentity who, string userName, string? via, CancellationToken ct)
    {
        var cfg = security?.StepUp ?? new StepUpConfig(true, StepUpConfig.DefaultMinutes);
        var once = cfg.Minutes == 0;
        var until = clock.GetUtcNow() + (once ? StepUpConfig.OnceLifetime : TimeSpan.FromMinutes(cfg.Minutes));
        await sessions.GrantStepUpAsync(who.Id, until, once, ct);
        await AuditAsync(userName, "step_up.ok", (via is null ? "" : via + ", ") + (once ? "once" : cfg.Minutes + " min"));
        return new(true, 200, Until: until, Minutes: cfg.Minutes);
    }

    internal async Task AuditAsync(string user, string evt, string? detail = null)
    {
        if (audit is null) return;
        try { await audit.AddAsync(user, evt, detail, CancellationToken.None); }
        catch (Exception e) { log.LogError(e, "Verlauf der Sicherheit konnte nicht geschrieben werden"); }
    }

    // ---- Konto in der Oberflaeche: Passwort aendern, Authenticator neu einrichten. Immer mit gueltiger Sitzung (ApiGuard) UND dem aktuellen Passwort. ----

    private const string TooMany = "Zu viele Versuche. Bitte später erneut versuchen.";
    private const string WrongCurrent = "Das aktuelle Passwort stimmt nicht.";

    /// <summary>So lange gilt ein neuer, noch nicht bestaetigter Authenticator-Schluessel.</summary>
    public static readonly TimeSpan TotpChangeLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, (string Secret, DateTimeOffset Until)> _pendingTotp = new();

    /// <summary>Prueft das aktuelle Passwort einer angemeldeten Person. Fehlversuche zaehlen je Konto ("acct:") und je Adresse; die teure Pruefung laeuft je Konto nur einmal gleichzeitig.</summary>
    private async Task<(UserRow? User, AuthResult? Refused)> VerifyCurrentAsync(SessionIdentity who, string? current, string ip, CancellationToken ct)
    {
        var keys = new[] { "acct:" + who.UserId, "ip:" + ip };
        var wait = keys.Max(limiter.Wait);
        if (wait > TimeSpan.Zero) return (null, new(false, 429, TooMany, (int)Math.Ceiling(wait.TotalSeconds)));
        if (current is null || current.Length is 0 or > PasswordPolicy.MaxLength) return (null, new(false, 400, "Eingabe ungültig."));
        var busy = "acct-busy:" + who.UserId;
        if (!limiter.TryBegin(busy, 1)) return (null, new(false, 429, TooMany, 5));
        try
        {
            var user = await users.FindByIdAsync(who.UserId, ct);
            if (user is null) return (null, new(false, 401, "Nicht angemeldet."));
            var (ok, _) = await hasher.VerifyAsync(current, user.PasswordHash);
            if (!ok)
            {
                foreach (var k in keys) limiter.Fail(k);
                log.LogWarning("Konto: falsches aktuelles Passwort für {User} von {Ip}", user.Name, ip);
                return (null, new(false, 403, WrongCurrent));
            }
            return (user, null);
        }
        finally { limiter.End(busy); }
    }

    /// <summary>Neues Passwort setzen. Alle anderen Geraete werden abgemeldet, dieses bleibt angemeldet.</summary>
    public async Task<AuthResult> ChangePasswordAsync(SessionIdentity who, string? current, string? next, string ip, CancellationToken ct = default)
    {
        var (user, refused) = await VerifyCurrentAsync(who, current, ip, ct);
        if (user is null) return refused!;
        var problem = PasswordPolicy.Problem(next, user.Name);
        if (problem.Length > 0) return new(false, 400, problem);
        if (next == current) return new(false, 400, "Das neue Passwort muss sich vom aktuellen unterscheiden.");
        await users.SetPasswordAsync(user.Id, await hasher.HashAsync(next!), endSessions: true, ct, keepSession: who.Id);
        limiter.Succeed("acct:" + user.Id);
        log.LogWarning("Konto: Passwort geändert für {User} von {Ip}, andere Geräte abgemeldet", user.Name, ip);
        return new(true, 200, User: user.Name);
    }

    /// <summary>Schritt 1 fuer einen neuen Authenticator: mit dem aktuellen Passwort einen neuen Schluessel anfordern. Gilt erst nach <see cref="FinishTotpChangeAsync"/>.</summary>
    public async Task<TotpChangeResult> BeginTotpChangeAsync(SessionIdentity who, string? current, string ip, CancellationToken ct = default)
    {
        var (user, refused) = await VerifyCurrentAsync(who, current, ip, ct);
        if (user is null) return new(false, refused!.Status, refused.Error, refused.RetryAfter);
        var now = clock.GetUtcNow();
        foreach (var (id, p) in _pendingTotp) if (p.Until < now) _pendingTotp.TryRemove(id, out _);
        var secret = Totp.NewSecret();
        _pendingTotp[user.Id] = (secret, now + TotpChangeLifetime);
        log.LogWarning("Konto: neuer Authenticator angefordert für {User} von {Ip}", user.Name, ip);
        return new(true, 200, Data: new TotpChange(secret, Totp.Uri(user.Name, secret)));
    }

    /// <summary>Schritt 2: der erste Code aus dem neuen Authenticator. Danach gelten der neue Schluessel und neue Wiederherstellungscodes (die alten entfallen), andere Geraete werden abgemeldet.</summary>
    public async Task<RecoveryResult> FinishTotpChangeAsync(SessionIdentity who, string? code, string ip, CancellationToken ct = default)
    {
        var keys = new[] { "acct:" + who.UserId, "ip:" + ip };
        var wait = keys.Max(limiter.Wait);
        if (wait > TimeSpan.Zero) return new(false, 429, TooMany, (int)Math.Ceiling(wait.TotalSeconds));
        var now = clock.GetUtcNow();
        if (!_pendingTotp.TryGetValue(who.UserId, out var pending) || pending.Until < now)
        {
            _pendingTotp.TryRemove(who.UserId, out _);
            return new(false, 400, "Der neue Schlüssel ist abgelaufen. Bitte noch einmal beginnen.");
        }
        var step = Totp.Check(pending.Secret, (code ?? "").Replace(" ", ""), 0, now);
        if (step is null)
        {
            foreach (var k in keys) limiter.Fail(k);
            return new(false, 401, "Der Code stimmt nicht. Prüfe den Eintrag in der App und die Uhrzeit des Handys.");
        }
        // nur der Besitzer dieses Eintrags darf ihn einloesen, und nur einmal
        if (!_pendingTotp.TryRemove(new KeyValuePair<Guid, (string, DateTimeOffset)>(who.UserId, pending))) return new(false, 400, "Der neue Schlüssel ist abgelaufen. Bitte noch einmal beginnen.");
        var (plain, hashes) = RecoveryCodes.Generate();
        await users.ReplaceTotpAsync(who.UserId, pending.Secret, hashes, ct, keepSession: who.Id, lastStep: step.Value);
        limiter.Succeed("acct:" + who.UserId);
        log.LogWarning("Konto: Authenticator ersetzt für {User} von {Ip}, neue Wiederherstellungscodes, andere Geräte abgemeldet", who.UserName, ip);
        return new(true, 200, Recovery: plain);
    }

    public Task<bool> IsSessionAliveAsync(Guid sessionId, CancellationToken ct = default) => sessions.IsAliveAsync(sessionId, ct);

    public Task<SessionIdentity?> ResolveSessionAsync(string token, bool rotate, CancellationToken ct = default) => sessions.ResolveAsync(token, rotate, ct);

    public Task LogoutAsync(string hash, CancellationToken ct = default) => sessions.DeleteAsync(hash, ct);

    public Task<int> LogoutOthersAsync(Guid userId, string keepHash, CancellationToken ct = default) => sessions.DeleteOthersAsync(userId, keepHash, ct);

    public Task<IReadOnlyList<SessionListItem>> ListSessionsAsync(Guid userId, string currentHash, CancellationToken ct = default) => sessions.ListAsync(userId, currentHash, ct);
}
