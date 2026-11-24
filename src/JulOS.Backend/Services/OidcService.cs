using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace JulOS.Backend.Services;

/// <summary>Die Felder einer Anfrage an /oidc/authorize, so wie sie ankamen (nichts davon ist schon geprueft).</summary>
public sealed record OidcAuthorizeInput(string? ClientId, string? RedirectUri, string? ResponseType, string? Scope, string? State, string? Nonce, string? CodeChallenge, string? CodeChallengeMethod, string? Prompt, string? MaxAge, string? ResponseMode = null);

/// <summary>Was auf eine Anfrage an /oidc/authorize folgt.</summary>
public abstract record OidcAuthorizeOutcome
{
    /// <summary>Die Anmelde-Zentrale ist nicht eingerichtet (keine oeffentliche Adresse, kein Server-Schluessel).</summary>
    public sealed record Unavailable : OidcAuthorizeOutcome;
    /// <summary>App unbekannt, gesperrt oder Adresse nicht exakt eingetragen: eine Fehlerseite, KEINE Weiterleitung (sonst ein offener Redirect). Gleiche Antwort fuer alle drei Faelle.</summary>
    public sealed record BadClient : OidcAuthorizeOutcome;
    /// <summary>Fehler an die (jetzt geprueften) Rueckkehr-Adresse der App.</summary>
    public sealed record Error(string RedirectUri, string Code, string Description, string? State) : OidcAuthorizeOutcome;
    /// <summary>Keine Sitzung im Cookie angekommen: einmal ueber eine Seite von JulOS selbst neu laden (das Cookie ist SameSite=Strict und kommt bei einem Aufruf von einer fremden Seite nicht mit).</summary>
    public sealed record Bounce : OidcAuthorizeOutcome;
    /// <summary>Anmelden noetig (keine Sitzung, oder max_age ist ueberschritten).</summary>
    public sealed record NeedLogin(string AppName) : OidcAuthorizeOutcome;
    /// <summary>Die Person ist angemeldet, hat aber in dieser App keine Rolle: kein Token.</summary>
    public sealed record NoAccess(string AppName) : OidcAuthorizeOutcome;
    /// <summary>Der Code ist ausgestellt; Weiterleitung an die App.</summary>
    public sealed record Issued(string Location) : OidcAuthorizeOutcome;
}

public sealed record OidcTokenBody(string AccessToken, string IdToken, int ExpiresIn, string Scope);

/// <summary>Ergebnis des Token-Aufrufs: bei Erfolg der Koerper, sonst ein Fehler nach RFC 6749 (Status, Code). Beschreibungen verraten nie, ob es eine App gibt.</summary>
public sealed record OidcTokenResult(int Status, string? Error = null, string? Description = null, OidcTokenBody? Body = null);

public sealed record OidcUserInfo(string Sub, string? Name, string Role);

/// <summary>Was ein id_token_hint beim Abmelden ergibt: die App, die Person (sub) und die Sitzung der App (sid).</summary>
public sealed record OidcHint(OidcApp App, string Sub, string? Sid);

/// <summary>
/// Der Ablauf der Anmelde-Zentrale (docs/SIGN-IN.md): authorize -> Code -> token. Nur Authorization Code mit PKCE (S256, Pflicht); jede Pruefung steht hier, nicht in den Endpunkten.
/// Die Endpunkte lesen nur die Anfrage und schreiben die Antwort.
/// </summary>
public sealed class OidcService(OidcAppStore apps, OidcFlowStore flow, OidcKeys keys, OidcIssuer issuer, SecurityAuditStore audit, TimeProvider clock, ILogger<OidcService> log)
{
    private static readonly string DummyHash = OidcRules.Hash("dummy");

    public bool Ready => issuer.Value is not null && keys.Available;

    // ---- authorize ----

    public async Task<OidcAuthorizeOutcome> AuthorizeAsync(OidcAuthorizeInput q, SessionIdentity? who, bool bounced, CancellationToken ct = default)
    {
        if (!Ready) return new OidcAuthorizeOutcome.Unavailable();

        // 1. App und Adresse. Ab hier ist die Rueckkehr-Adresse vertrauenswuerdig; davor darf nichts dorthin gehen.
        var app = await apps.FindByClientIdAsync(q.ClientId, ct);
        if (app is not { Enabled: true } || q.RedirectUri is null || !app.RedirectUris.Contains(q.RedirectUri, StringComparer.Ordinal)) return new OidcAuthorizeOutcome.BadClient();
        var back = q.RedirectUri;
        OidcAuthorizeOutcome Fail(string code, string text) => new OidcAuthorizeOutcome.Error(back, code, text, OidcRules.OpaqueOk(q.State) ? q.State : null);

        // 2. Form der Anfrage
        if (q.ResponseType != "code") return Fail("unsupported_response_type", "Nur response_type=code.");
        // Die Antwort kommt immer als Suchteil der Adresse. form_post wird von Microsofts Handler standardmaessig verlangt, er nimmt die Antwort aber auch so an; ein Formular an die App
        // liesse die CSP des Servers (form-action 'self') ohnehin nicht zu. Im Fragment (fragment) oder als JWT (jwt) antworten wir nicht.
        if (q.ResponseMode is not (null or "query" or "form_post")) return Fail("invalid_request", "response_mode: query.");
        if (!OidcRules.OpaqueOk(q.State)) return Fail("invalid_request", "state ist Pflicht (höchstens 512 Zeichen).");
        if (q.Nonce is not null && !OidcRules.OpaqueOk(q.Nonce)) return Fail("invalid_request", "nonce ungültig.");
        if (OidcRules.GrantedScopes(q.Scope) is not { } scopes) return Fail("invalid_scope", "scope muss openid enthalten.");
        if (q.CodeChallengeMethod != "S256" || !OidcRules.ChallengeOk(q.CodeChallenge)) return Fail("invalid_request", "PKCE ist Pflicht: code_challenge mit code_challenge_method=S256.");
        if (q.Prompt is not null && q.Prompt is not ("none" or "login")) return Fail("invalid_request", "prompt: none oder login.");
        TimeSpan? maxAge = null;
        if (q.MaxAge is not null)
        {
            if (!int.TryParse(q.MaxAge, out var seconds) || seconds < 0) return Fail("invalid_request", "max_age ungültig.");
            maxAge = TimeSpan.FromSeconds(seconds);
        }
        var silent = q.Prompt == "none";

        // 3. Sitzung
        var session = who is null ? null : await flow.SessionAsync(who.Id, ct);
        if (session is null)
        {
            if (silent) return Fail("login_required", "Keine Sitzung bei JulOS.");
            return bounced ? new OidcAuthorizeOutcome.NeedLogin(app.Name) : new OidcAuthorizeOutcome.Bounce();
        }
        // max_age / prompt=login: war die Anmeldung zu lange her, wird neu angemeldet. Eine Anmeldung von vor weniger als 30 s gilt immer als frisch (sonst ginge es nach der
        // Anmeldung im Kreis: die Seite laedt die Anfrage neu).
        var limit = q.Prompt == "login" ? TimeSpan.Zero : maxAge;
        if (limit is { } l && clock.GetUtcNow() - session.AuthTime > (l < FreshWindow ? FreshWindow : l))
            return silent ? Fail("login_required", "Die Anmeldung ist zu lange her.") : new OidcAuthorizeOutcome.NeedLogin(app.Name);

        // 4. Rolle: ohne Rolle kein Token
        if (await apps.RoleOfAsync(app, session.UserId, ct) is null)
            return silent ? Fail("access_denied", "Kein Zugang zu dieser App.") : new OidcAuthorizeOutcome.NoAccess(app.Name);

        // 5. Code
        var code = OidcRules.NewCode();
        await flow.IssueCodeAsync(OidcRules.Hash(code), app.Id, session.UserId, who!.Id, back, q.CodeChallenge!, q.Nonce, string.Join(' ', scopes), session.AuthTime, clock.GetUtcNow() + OidcRules.CodeLifetime, ct);
        return new OidcAuthorizeOutcome.Issued(OidcRules.WithQuery(back, ("code", code), ("state", q.State), ("iss", issuer.Value)));
    }

    private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(30);

    // ---- token ----

    private static OidcTokenResult Err(int status, string code, string text) => new(status, code, text);

    /// <summary>Tauscht einen Code gegen das ID-Token. clientId und secret sind die schon gelesenen Angaben der App (Basic oder Formular).</summary>
    public async Task<OidcTokenResult> ExchangeAsync(string? clientId, string? secret, string? grantType, string? code, string? redirectUri, string? verifier, CancellationToken ct = default)
    {
        if (!Ready) return Err(503, "temporarily_unavailable", "Die Anmeldung ist nicht eingerichtet.");

        // App anmelden: bei einer unbekannten App wird trotzdem ein Hash verglichen (gleiche Zeit), und der Fehler ist derselbe wie bei einem falschen Geheimnis
        var app = await apps.FindByClientIdAsync(clientId, ct);
        var secretOk = OidcRules.SecretMatches(secret, app?.SecretHash ?? DummyHash);
        if (app is not { Enabled: true } || !secretOk) return Err(401, "invalid_client", "Anmeldung der App fehlgeschlagen.");

        if (grantType != "authorization_code") return Err(400, "unsupported_grant_type", "Nur authorization_code.");
        if (code is not { Length: > 0 and <= 200 }) return Err(400, "invalid_request", "code fehlt.");
        var now = clock.GetUtcNow();
        var codeHash = OidcRules.Hash(code);

        await using var redeem = await flow.BeginRedeemAsync(codeHash, app.Id, now, ct);
        if (redeem is null)
        {
            // schon benutzt? Dann hat ihn jemand zweimal vorgelegt: was daraus entstand, wird widerrufen. (Eine gleichzeitige zweite Vorlage hat hier gewartet, bis die erste fertig war.)
            if (await flow.RevokeReusedCodeAsync(codeHash, app.Id, now, ct))
            {
                log.LogWarning("OIDC: Code von {App} ein zweites Mal vorgelegt, die daraus entstandene Sitzung wird beendet.", app.Name);
                await AuditAsync("oidc", "oidc.code_reused", app.Name);
            }
            return Err(400, "invalid_grant", "Der Code gilt nicht (mehr).");
        }
        var row = redeem.Code;
        // Ab hier ist der Code verbraucht: wird er abgelehnt, bleibt er es (Commit), bei einem Fehler des Servers (keine Freigabe) gilt er weiter.
        async Task<OidcTokenResult> Burn(string text) { await redeem.CommitAsync(ct); return Err(400, "invalid_grant", text); }
        if (redirectUri is null || !string.Equals(redirectUri, row.RedirectUri, StringComparison.Ordinal) || !OidcRules.VerifierMatches(verifier, row.CodeChallenge))
            return await Burn("redirect_uri oder code_verifier stimmen nicht.");

        // die Sitzung muss noch gelten und die Person noch eine Rolle haben (der Code ist nur Sekunden alt, aber beides kann sich geaendert haben)
        var session = await flow.SessionAsync(row.SessionId, ct);
        if (session is null || session.UserId != row.UserId) return await Burn("Die Sitzung bei JulOS gilt nicht mehr.");
        if (await apps.RoleOfAsync(app, session.UserId, ct) is not { } role) return await Burn("Kein Zugang zu dieser App.");

        SecurityKey signing;
        try { signing = await keys.ActiveAsync(ct); }
        catch (OidcKeysUnusableException) { return Err(503, "temporarily_unavailable", "Die Anmeldung ist nicht eingerichtet."); }
        var sid = await redeem.UpsertGrantAsync(row.SessionId, app.Id, session.UserId, session.Sub, session.UserName, now, ct);
        var withProfile = row.Scope.Split(' ').Contains(OidcRules.Profile);
        var idToken = OidcTokens.CreateIdToken(new IdTokenClaims(issuer.Value!, app.ClientId, session.Sub.ToString(), sid, now, row.AuthTime, row.Nonce, withProfile ? session.UserName : null, role),
            signing, OidcRules.TokenLifetime);

        var access = OidcRules.NewCode();
        await redeem.AddAccessTokenAsync(OidcRules.Hash(access), app.Id, session.UserId, row.SessionId, codeHash, row.Scope, now + OidcRules.TokenLifetime, ct);
        await redeem.CommitAsync(ct);
        return new OidcTokenResult(200, Body: new OidcTokenBody(access, idToken, (int)OidcRules.TokenLifetime.TotalSeconds, row.Scope));
    }

    // ---- userinfo ----

    /// <summary>Die Angaben zur Person fuer ein Zugriffs-Token; null, wenn es nicht (mehr) gilt: abgelaufen, Sitzung beendet, App gesperrt, Rolle genommen.</summary>
    public async Task<OidcUserInfo?> UserInfoAsync(string? token, CancellationToken ct = default)
    {
        if (!Ready || token is not { Length: > 0 and <= 200 }) return null;
        if (await flow.FindAccessTokenAsync(OidcRules.Hash(token), clock.GetUtcNow(), ct) is not { } t) return null;
        if (await apps.FindAsync(t.AppId, ct) is not { Enabled: true } app) return null;
        if (await flow.SessionAsync(t.SessionId, ct) is not { } s || s.UserId != t.UserId) return null;
        if (await apps.RoleOfAsync(app, s.UserId, ct) is not { } role) return null;
        return new OidcUserInfo(s.Sub.ToString(), t.Scope.Split(' ').Contains(OidcRules.Profile) ? s.UserName : null, role);
    }

    // ---- end-session ----

    /// <summary>Liest einen id_token_hint: nur ein von uns signiertes Token, fuer eine bekannte App. Sonst null.</summary>
    public async Task<OidcHint?> ReadHintAsync(string? idTokenHint, CancellationToken ct = default)
    {
        if (!Ready) return null;
        try
        {
            var jwt = await OidcTokens.ReadHintAsync(idTokenHint, issuer.Value!, await keys.ValidationKeysAsync(ct));
            if (jwt is null || OidcTokens.Audience(jwt) is not { } clientId || OidcTokens.Claim(jwt, "sub") is not { } sub) return null;
            return await apps.FindByClientIdAsync(clientId, ct) is { } app ? new OidcHint(app, sub, OidcTokens.Claim(jwt, "sid")) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }          // ein kaputtes Token ist einfach kein Hinweis
    }

    public async Task AuditAsync(string user, string evt, string? detail = null)
    {
        try { await audit.AddAsync(user, evt, detail, CancellationToken.None); }
        catch (Exception e) { log.LogError(e, "Verlauf der Sicherheit konnte nicht geschrieben werden"); }
    }
}
