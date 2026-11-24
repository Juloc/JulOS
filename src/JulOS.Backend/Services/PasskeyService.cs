using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Services;

/// <summary>
/// Antwort der Passkey-Wege. Json: Optionen fuer den Browser (fertiger Text). Login: das Ergebnis einer Anmeldung. StepUp: das Ergebnis einer Freigabe. Item: der neue Passkey.
/// </summary>
public sealed record PasskeyResult(bool Ok, int Status, string? Error = null, int RetryAfter = 0, string? Json = null, AuthResult? Login = null, AuthService.StepUpResult? StepUp = null, PasskeyRow? Item = null);

/// <summary>
/// Passkeys registrieren, benutzen und entfernen (docs/PASSKEYS.md). Die WebAuthn-Pruefung macht <see cref="IPasskeys"/>; hier stehen die Regeln drumherum: wer darf was wann,
/// Herausforderungen (einmal, 5 Minuten, an Person und Sitzung gebunden), Sperre und Ratenlimit, einheitliche Fehler und der Verlauf.
/// Fehler der Anmeldung verraten nie, ob es die Person oder den Passkey gibt.
/// </summary>
public sealed class PasskeyService(IPasskeys keys, PasskeyStore store, UserStore users, SessionStore sessions, AuthService auth, LoginLimiter limiter, PasskeyThrottle throttle,
    OidcIssuer issuer, TimeProvider clock, ILogger<PasskeyService> log)
{
    private const string LoginFailed = "Anmeldung mit Passkey fehlgeschlagen.";
    private const string RegisterFailed = "Der Passkey konnte nicht eingerichtet werden.";
    private const string StepUpFailed = "Der Passkey wurde nicht angenommen.";
    private const string TooMany = "Zu viele Versuche. Bitte später erneut versuchen.";

    private static PasskeyResult Refused(int status, string error, int retryAfter = 0) => new(false, status, error, retryAfter);

    /// <summary>Ob Passkeys hier gehen: es braucht die oeffentliche Adresse der Anmelde-Zentrale als Rechnernamen (nicht aus dem Host-Kopf einer Anfrage).</summary>
    public PasskeySite? Site() => PasskeyRules.SiteFrom(issuer.Value);

    private PasskeyResult? Gate(string ip, out PasskeySite? site)
    {
        site = Site();
        if (!throttle.Allow("pk:" + ip, out var retry)) return Refused(429, TooMany, retry);
        if (site is null) return Refused(503, "Passkeys brauchen die öffentliche Adresse von JulOS als Namen (Einstellungen, Sicherheit).");
        return null;
    }

    private PasskeyResult? Locked(params string[] lockKeys)
    {
        var wait = lockKeys.Max(limiter.Wait);
        return wait > TimeSpan.Zero ? Refused(429, TooMany, (int)Math.Ceiling(wait.TotalSeconds)) : null;
    }

    // ---- Liste, Umbenennen, Loeschen ----

    public Task<IReadOnlyList<PasskeyRow>> ListAsync(SessionIdentity who, CancellationToken ct) => store.ListAsync(who.UserId, ct);

    public async Task<PasskeyResult> RenameAsync(SessionIdentity who, Guid id, string? name, CancellationToken ct)
    {
        if (PasskeyRules.CleanName(name) is not { } clean) return Refused(400, $"Name: 1 bis {PasskeyRules.NameMax} Zeichen.");
        return await store.RenameAsync(who.UserId, id, clean, ct) ? new(true, 204) : Refused(404, "Passkey nicht gefunden.");
    }

    /// <summary>
    /// Entfernt einen Passkey (der Weg verlangt vorher die Freigabe, StepUpPolicy). Danach enden alle anderen Sitzungen, die mit einem Passkey begonnen wurden: ein gestohlenes Geraet
    /// soll nicht angemeldet bleiben. Passwort mit Code bleibt immer, der letzte Weg kann also nie wegfallen; die Regel steht trotzdem in <see cref="PasskeyRules.CanRemove"/>.
    /// </summary>
    public async Task<PasskeyResult> DeleteAsync(SessionIdentity who, Guid id, CancellationToken ct)
    {
        var all = await store.ListAsync(who.UserId, ct);
        if (all.All(p => p.Id != id)) return Refused(404, "Passkey nicht gefunden.");
        var user = await users.FindByIdAsync(who.UserId, ct);
        if (user is null) return Refused(401, "Nicht angemeldet.");
        if (!PasskeyRules.CanRemove(all.Count - 1, passwordLogin: user.PasswordHash.Length > 0 && user.TotpConfirmed)) return Refused(409, "Der letzte Weg zur Anmeldung kann nicht entfernt werden.");
        var name = await store.DeleteAsync(who.UserId, id, ct);
        if (name is null) return Refused(404, "Passkey nicht gefunden.");
        var ended = await sessions.DeleteByStrengthAsync(who.UserId, PasskeyRules.Passkey, who.Id, ct);
        await auth.AuditAsync(who.UserName, "passkey.deleted", $"{name}; {ended} Sitzung(en) beendet");
        log.LogWarning("Passkey entfernt: {User}, {Name}", who.UserName, name);
        return new(true, 204);
    }

    // ---- Registrieren ----

    public async Task<PasskeyResult> BeginRegistrationAsync(SessionIdentity who, string ip, CancellationToken ct)
    {
        if (Gate(ip, out var site) is { } refused) return refused;
        var existing = await store.CredentialIdsAsync(who.UserId, ct);
        if (existing.Count >= PasskeyRules.MaxPerUser) return Refused(409, $"Höchstens {PasskeyRules.MaxPerUser} Passkeys.");
        var (challenge, options) = keys.RegistrationOptions(site!, PasskeyRules.UserHandle(who.UserId), who.UserName, existing);
        if (!await store.IssueChallengeAsync(PasskeyRules.ChallengeHash(challenge), "register", who.UserId, who.Id, clock.GetUtcNow(), ct)) return Refused(429, TooMany, 30);
        return new(true, 200, Json: options);
    }

    public async Task<PasskeyResult> FinishRegistrationAsync(SessionIdentity who, string? name, string credentialJson, string ip, CancellationToken ct)
    {
        if (Gate(ip, out var site) is { } refused) return refused;
        if (Locked("acct:" + who.UserId, "ip:" + ip) is { } locked) return locked;
        if (PasskeyRules.CleanName(name) is not { } clean) return Refused(400, $"Name: 1 bis {PasskeyRules.NameMax} Zeichen.");

        async Task<PasskeyResult> Failed(string why)
        {
            limiter.Fail("acct:" + who.UserId); limiter.Fail("ip:" + ip);
            log.LogWarning("Passkey registrieren abgelehnt für {User} von {Ip}: {Why}", who.UserName, ip, why);
            await auth.AuditAsync(who.UserName, "passkey.register_failed", why);
            return Refused(400, RegisterFailed);
        }

        if (ChallengeFrom(credentialJson) is not { } challenge) return await Failed("Antwort ohne Herausforderung");
        var taken = await store.TakeChallengeAsync(PasskeyRules.ChallengeHash(challenge), clock.GetUtcNow(), ct);
        // dieselbe Person, dieselbe Sitzung, die die Optionen angefordert hat: eine andere Sitzung oder Person kann sie nicht einloesen
        if (taken is not { Purpose: "register" } || taken.UserId != who.UserId || taken.SessionId != who.Id) return await Failed("Herausforderung unbekannt, benutzt, abgelaufen oder fremd");

        PasskeyNew made;
        try { made = await keys.VerifyRegistrationAsync(site!, credentialJson, challenge, PasskeyRules.UserHandle(who.UserId), who.UserName, async id => !await store.CredentialExistsAsync(id, ct), ct); }
        catch (PasskeyRejectedException e) { return await Failed(e.Message); }

        var item = await store.AddAsync(who.UserId, clean, made, clock.GetUtcNow(), ct);
        if (item is null) return Refused(409, $"Höchstens {PasskeyRules.MaxPerUser} Passkeys, und jeder nur einmal.");
        limiter.Succeed("acct:" + who.UserId);
        await auth.AuditAsync(who.UserName, "passkey.added", clean);
        log.LogWarning("Passkey eingerichtet: {User}, {Name}", who.UserName, clean);
        return new(true, 200, Item: item);
    }

    // ---- Anmelden ----

    public async Task<PasskeyResult> BeginLoginAsync(string ip, CancellationToken ct)
    {
        if (Gate(ip, out var site) is { } refused) return refused;
        if (Locked("ip:" + ip) is { } locked) return locked;
        var (challenge, options) = keys.AssertionOptions(site!, []);     // leere Liste: jeder Passkey dieser Adresse darf antworten (kein Benutzername)
        if (!await store.IssueChallengeAsync(PasskeyRules.ChallengeHash(challenge), "login", null, null, clock.GetUtcNow(), ct)) return Refused(429, TooMany, 30);
        return new(true, 200, Json: options);
    }

    public async Task<PasskeyResult> FinishLoginAsync(string credentialJson, string ip, string? userAgent, CancellationToken ct)
    {
        if (Gate(ip, out var site) is { } refused) return refused;
        if (Locked("ip:" + ip) is { } locked) return locked;

        // jeder Fehler sieht gleich aus (kein Hinweis, ob es den Passkey oder die Person gibt) und zaehlt fuer die Adresse. Nie fuer das Konto: wer eine Credential-Id kennt,
        // soll die Person nicht aussperren koennen.
        PasskeyResult Failed(string why, string? user = null)
        {
            limiter.Fail("ip:" + ip);
            log.LogWarning("Anmeldung mit Passkey fehlgeschlagen von {Ip}: {Why}{User}", ip, why, user is null ? "" : " (" + user + ")");
            return Refused(401, LoginFailed);
        }

        if (Parse(credentialJson) is not { } p || p.UserHandle is null) return Failed("Antwort unvollständig");
        var taken = await store.TakeChallengeAsync(PasskeyRules.ChallengeHash(p.Challenge), clock.GetUtcNow(), ct);
        if (taken is not { Purpose: "login" }) return Failed("Herausforderung unbekannt, benutzt oder abgelaufen");
        var key = await store.FindByCredentialAsync(p.CredentialId, ct);
        if (key is null) return Failed("Passkey unbekannt");
        // der User-Handle muss zum Besitzer der Credential-Id passen (sonst koennte ein Passkey als Konto eines anderen auftreten)
        if (!PasskeyRules.HandleMatches(p.UserHandle, key.UserId)) return Failed("User-Handle gehört nicht zum Besitzer");
        var user = await users.FindByIdAsync(key.UserId, ct);
        if (user is not { TotpConfirmed: true }) return Failed("Konto nicht fertig eingerichtet");

        PasskeyProof proof;
        try { proof = await keys.VerifyAssertionAsync(site!, credentialJson, p.Challenge, key.PublicKey, (uint)key.SignCount, PasskeyRules.UserHandle(key.UserId), ct); }
        catch (PasskeyRejectedException e)
        {
            if (e.Clone)
            {
                await auth.AuditAsync(user.Name, "passkey.clone_suspected", $"Zähler lief rückwärts (gespeichert {key.SignCount}); Anmeldung abgelehnt");
                log.LogError("Passkey von {User}: Zähler lief rückwärts, mögliche Kopie. Anmeldung von {Ip} abgelehnt.", user.Name, ip);
            }
            return Failed(e.Message, user.Name);
        }
        if (!proof.CredentialId.AsSpan().SequenceEqual(key.CredentialId)) return Failed("andere Credential-Id", user.Name);
        // Zaehler nur weiter, wenn niemand schneller war: dieselbe Antwort zweimal gleichzeitig gelingt nicht zweimal
        if (!await store.TouchAsync(key.Id, key.SignCount, proof.Counter, proof.BackedUp, clock.GetUtcNow(), ct)) return Failed("Zähler gleichzeitig geändert", user.Name);

        var known = await sessions.UserAgentKnownAsync(user.Id, userAgent, ct);
        var token = await sessions.CreateAsync(user.Id, ip, userAgent, ct, auth.LoginGrant(), PasskeyRules.Passkey);
        await auth.AuditAsync(user.Name, "passkey.login", known ? null : "neues Gerät");
        log.LogInformation("Angemeldet mit Passkey: {User} von {Ip}{New}", user.Name, ip, known ? "" : " (neues Gerät)");
        return new(true, 200, Login: new AuthResult(true, 200, User: user.Name, Token: token, RecoveryLeft: await users.RecoveryLeftAsync(user.Id, ct)));
    }

    // ---- Freigabe fuer Aktionen (Step-up) ----

    public async Task<PasskeyResult> BeginStepUpAsync(SessionIdentity who, string ip, CancellationToken ct)
    {
        if (Gate(ip, out var site) is { } refused) return refused;
        if (Locked("acct:" + who.UserId, "ip:" + ip) is { } locked) return locked;
        var own = await store.CredentialIdsAsync(who.UserId, ct);
        if (own.Count == 0) return Refused(400, "Es ist noch kein Passkey eingerichtet.");
        var (challenge, options) = keys.AssertionOptions(site!, own);
        if (!await store.IssueChallengeAsync(PasskeyRules.ChallengeHash(challenge), "step_up", who.UserId, who.Id, clock.GetUtcNow(), ct)) return Refused(429, TooMany, 30);
        return new(true, 200, Json: options);
    }

    public async Task<PasskeyResult> FinishStepUpAsync(SessionIdentity who, string credentialJson, string ip, CancellationToken ct)
    {
        if (Gate(ip, out var site) is { } refused) return refused;
        var lockKeys = new[] { "acct:" + who.UserId, "ip:" + ip };
        if (Locked(lockKeys) is { } locked) return locked;

        async Task<PasskeyResult> Failed(string why)
        {
            foreach (var k in lockKeys) limiter.Fail(k);
            log.LogWarning("Freigabe mit Passkey abgelehnt für {User} von {Ip}: {Why}", who.UserName, ip, why);
            await auth.AuditAsync(who.UserName, "step_up.failed", "passkey");
            return Refused(401, StepUpFailed);
        }

        if (Parse(credentialJson) is not { } p) return await Failed("Antwort unvollständig");
        var taken = await store.TakeChallengeAsync(PasskeyRules.ChallengeHash(p.Challenge), clock.GetUtcNow(), ct);
        if (taken is not { Purpose: "step_up" } || taken.UserId != who.UserId || taken.SessionId != who.Id) return await Failed("Herausforderung unbekannt, benutzt, abgelaufen oder fremd");
        var key = await store.FindByCredentialAsync(p.CredentialId, ct);
        if (key is null || key.UserId != who.UserId) return await Failed("Passkey gehört nicht zu dieser Person");
        if (p.UserHandle is not null && !PasskeyRules.HandleMatches(p.UserHandle, who.UserId)) return await Failed("User-Handle gehört nicht zum Besitzer");

        PasskeyProof proof;
        try { proof = await keys.VerifyAssertionAsync(site!, credentialJson, p.Challenge, key.PublicKey, (uint)key.SignCount, PasskeyRules.UserHandle(who.UserId), ct); }
        catch (PasskeyRejectedException e)
        {
            if (e.Clone) await auth.AuditAsync(who.UserName, "passkey.clone_suspected", $"Zähler lief rückwärts (gespeichert {key.SignCount}); Freigabe abgelehnt");
            return await Failed(e.Message);
        }
        if (!proof.CredentialId.AsSpan().SequenceEqual(key.CredentialId) || !await store.TouchAsync(key.Id, key.SignCount, proof.Counter, proof.BackedUp, clock.GetUtcNow(), ct)) return await Failed("Zähler");
        foreach (var k in lockKeys) limiter.Succeed(k);
        return new(true, 200, StepUp: await auth.GrantStepUpAsync(who, who.UserName, "passkey", ct));
    }

    // ---- Antwort des Browsers lesen (nur Felder zum Nachschlagen; geglaubt wird erst, was die Bibliothek prueft) ----

    private sealed record Parsed(byte[] Challenge, byte[] CredentialId, byte[]? UserHandle);

    private static byte[]? ChallengeFrom(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("clientDataJSON", out var c) && c.ValueKind == JsonValueKind.String
                ? PasskeyRules.ChallengeOf(c.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    private static Parsed? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ChallengeFrom(json) is not { } challenge) return null;
            if (!root.TryGetProperty("rawId", out var raw) || raw.ValueKind != JsonValueKind.String || PasskeyRules.FromBase64Url(raw.GetString()) is not { Length: > 0 } id) return null;
            byte[]? handle = null;
            if (root.GetProperty("response").TryGetProperty("userHandle", out var h) && h.ValueKind == JsonValueKind.String) handle = PasskeyRules.FromBase64Url(h.GetString());
            return new Parsed(challenge, id, handle);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
