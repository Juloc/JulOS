using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record OidcCode(Guid UserId, Guid SessionId, string RedirectUri, string CodeChallenge, string? Nonce, string Scope, DateTimeOffset AuthTime);

public sealed record OidcSessionInfo(Guid UserId, string UserName, Guid Sub, DateTimeOffset AuthTime);

public sealed record OidcAccessToken(Guid AppId, Guid UserId, Guid SessionId, string Scope);

public sealed record OidcLogoutJob(long Id, Guid AppId, string AppName, string ClientId, string? Uri, Guid Sub, string UserName, string Sid, string Reason, int Attempts);

/// <summary>
/// Der Ablauf der Anmelde-Zentrale in der Datenbank: Codes (einmalig, nur als Hash), Zugriffs-Token, die Zuordnung "JulOS-Sitzung hat der App ein Token gegeben" (Grants) und die
/// Warteschlange der Logout-Token. Zeiten kommen vom Aufrufer (TimeProvider), nicht aus der Datenbank, damit sie sich in Tests steuern lassen.
/// </summary>
public sealed class OidcFlowStore(NpgsqlDataSource db)
{
    /// <summary>So lange bleibt ein benutzter Code in der Tabelle, damit seine Wiederverwendung erkannt wird.</summary>
    public static readonly TimeSpan CodeKeep = TimeSpan.FromHours(1);

    // dieselbe Gueltigkeit wie SessionStore (Leerlauf, Hoechstdauer, Konto bestaetigt)
    private static readonly string Valid = $"s.created_at > now() - interval '{SessionStore.MaxDays} days' and s.last_seen > now() - interval '{SessionStore.IdleDays} days' and u.totp_confirmed";

    // ---- Codes ----

    public async Task IssueCodeAsync(string codeHash, Guid appId, Guid userId, Guid sessionId, string redirectUri, string challenge, string? nonce, string scope, DateTimeOffset authTime, DateTimeOffset expires, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into oidc_codes (hash, app_id, user_id, session_id, redirect_uri, code_challenge, nonce, scope, auth_time, expires_at) values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)",
            codeHash, appId, userId, sessionId, redirectUri, challenge, nonce, scope, authTime, expires);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Loest einen Code ein: genau einmal (atomar), nur fuer die App, der er gehoert, und nur vor Ablauf. Danach ist er verbraucht, auch wenn der Aufrufer die Adresse
    /// oder PKCE noch ablehnt (ein geratener Code laesst sich so nicht mehrfach probieren).
    /// </summary>
    public async Task<OidcCode?> RedeemCodeAsync(string codeHash, Guid appId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update oidc_codes set used_at = $3 where hash = $1 and app_id = $2 and used_at is null and expires_at > $3 returning user_id, session_id, redirect_uri, code_challenge, nonce, scope, auth_time",
            codeHash, appId, now);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new OidcCode(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)) : null;
    }

    /// <summary>
    /// Der Code war schon benutzt (Wiederverwendung, RFC 6749 4.1.2): was daraus ausgestellt wurde, wird widerrufen (Zugriffs-Token geloescht, die Sitzung der App ueber ihr
    /// Logout-Token beendet). Gibt true, wenn es ein benutzter Code dieser App war.
    /// </summary>
    public async Task<bool> RevokeReusedCodeAsync(string codeHash, Guid appId, DateTimeOffset now, CancellationToken ct = default)
    {
        Guid session;
        await using (var find = db.Cmd("select session_id from oidc_codes where hash = $1 and app_id = $2 and used_at is not null", codeHash, appId))
        {
            if (await find.ExecuteScalarAsync(ct) is not Guid s) return false;
            session = s;
        }
        await using (var del = db.Cmd("delete from oidc_tokens where code_hash = $1", codeHash)) await del.ExecuteNonQueryAsync(ct);
        await EnqueueLogoutsAsync(appId, session, userId: null, "code_reused", now, ct);
        return true;
    }

    // ---- Sitzung ----

    /// <summary>Die JulOS-Sitzung, wenn sie gilt: wer, und wann sie begonnen hat (auth_time).</summary>
    public async Task<OidcSessionInfo?> SessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select u.id, u.name, u.oidc_sub, s.created_at from sessions s join users u on u.id = s.user_id where s.id = $1 and {Valid}", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new OidcSessionInfo(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3)) : null;
    }

    public async Task<Guid?> SubOfAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select oidc_sub from users where id = $1", userId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    // ---- Grants und Zugriffs-Token ----

    /// <summary>Merkt, dass diese Sitzung der App ein Token gegeben hat. Gibt die "sid" (je Sitzung und App gleich, nicht der Cookie-Wert).</summary>
    public async Task<string> UpsertGrantAsync(Guid sessionId, Guid appId, Guid userId, Guid sub, string userName, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into oidc_grants (session_id, app_id, user_id, sub, user_name, sid, issued_at) values ($1, $2, $3, $4, $5, $6, $7) on conflict (session_id, app_id) do update set issued_at = excluded.issued_at returning sid",
            sessionId, appId, userId, sub, userName, OidcRules.NewSid(), now);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task AddAccessTokenAsync(string tokenHash, Guid appId, Guid userId, Guid sessionId, string codeHash, string scope, DateTimeOffset expires, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into oidc_tokens (hash, app_id, user_id, session_id, code_hash, scope, expires_at) values ($1, $2, $3, $4, $5, $6, $7)", tokenHash, appId, userId, sessionId, codeHash, scope, expires);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<OidcAccessToken?> FindAccessTokenAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select app_id, user_id, session_id, scope from oidc_tokens where hash = $1 and expires_at > $2", tokenHash, now);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new OidcAccessToken(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3)) : null;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GrantCountsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select app_id, count(*)::int from oidc_grants group by app_id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var d = new Dictionary<Guid, int>();
        while (await r.ReadAsync(ct)) d[r.GetGuid(0)] = r.GetInt32(1);
        return d;
    }

    // ---- Warteschlange der Logout-Token ----

    /// <summary>
    /// Alle Grants, deren JulOS-Sitzung nicht mehr gilt (abgemeldet, Geraet entfernt, abgelaufen, Konto geloescht, Passwort geaendert), kommen in die Warteschlange. So ist es egal,
    /// auf welchem Weg eine Sitzung endet: es gibt keinen Weg, der das Logout-Token vergessen koennte. Gibt die Zahl.
    /// </summary>
    public async Task<int> EnqueueForEndedSessionsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($@"with ended as (
              delete from oidc_grants g
              where not exists (select 1 from sessions s join users u on u.id = s.user_id where s.id = g.session_id and {Valid})
              returning g.app_id, g.sub, g.user_name, g.sid)
            insert into oidc_logouts (app_id, sub, user_name, sid, reason, created_at, next_at) select app_id, sub, user_name, sid, 'session_ended', $1, $1 from ended", now);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Beendet die Sitzungen einer App (sie wurde gesperrt oder einer Person wurde die Rolle genommen): die Grants kommen in die Warteschlange. userId null: alle Personen; sessionId null: alle Sitzungen.</summary>
    public async Task<int> EnqueueLogoutsAsync(Guid appId, Guid? sessionId, Guid? userId, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd(@"with moved as (
              delete from oidc_grants where app_id = $1 and ($2::uuid is null or session_id = $2) and ($3::uuid is null or user_id = $3)
              returning app_id, sub, user_name, sid)
            insert into oidc_logouts (app_id, sub, user_name, sid, reason, created_at, next_at) select app_id, sub, user_name, sid, $4, $5, $5 from moved", appId, sessionId, userId, reason, now);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<int> EnqueueLogoutsAsync(Guid appId, Guid? userId, string reason, DateTimeOffset now, CancellationToken ct = default) => EnqueueLogoutsAsync(appId, null, userId, reason, now, ct);

    public async Task<IReadOnlyList<OidcLogoutJob>> DueLogoutsAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd(@"select l.id, l.app_id, a.name, a.client_id, a.backchannel_uri, l.sub, l.user_name, l.sid, l.reason, l.attempts
            from oidc_logouts l join oidc_apps a on a.id = l.app_id where l.done_at is null and l.next_at <= $1 order by l.id limit $2", now, limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<OidcLogoutJob>();
        while (await r.ReadAsync(ct))
            list.Add(new OidcLogoutJob(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetGuid(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt32(9)));
        return list;
    }

    public async Task FinishLogoutAsync(long id, string result, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update oidc_logouts set done_at = $2, result = $3, attempts = attempts + 1 where id = $1", id, now, result);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RetryLogoutAsync(long id, DateTimeOffset next, string result, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update oidc_logouts set next_at = $2, result = $3, attempts = attempts + 1 where id = $1", id, next, result);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Raeumt auf: abgelaufene Codes und Token, erledigte Logouts nach einer Woche, Grants, die aelter sind als jede Sitzung sein kann.</summary>
    public async Task PurgeAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using (var c = db.Cmd("delete from oidc_codes where expires_at < $1", now - CodeKeep)) await c.ExecuteNonQueryAsync(ct);
        await using (var t = db.Cmd("delete from oidc_tokens where expires_at < $1", now)) await t.ExecuteNonQueryAsync(ct);
        await using (var l = db.Cmd("delete from oidc_logouts where done_at is not null and done_at < $1", now - TimeSpan.FromDays(7))) await l.ExecuteNonQueryAsync(ct);
    }
}
