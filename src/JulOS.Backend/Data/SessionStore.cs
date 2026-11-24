using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace JulOS.Backend.Data;

public static class SessionTokens
{
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}

public sealed record SessionIdentity(Guid UserId, string UserName, string Hash, string? NewToken, Guid Id);

public sealed record SessionListItem(bool Current, DateTimeOffset CreatedAt, DateTimeOffset LastSeen, string? UserAgent, string? Ip);

/// <summary>
/// Sitzungen in PostgreSQL. Das Cookie enthaelt 256 Bit Zufall, gespeichert wird nur der SHA-256-Wert.
/// Leerlauf 30 Tage, hoechstens 180 Tage. Alle 12 Stunden bekommt die Sitzung ein neues Cookie; das alte gilt noch 10 Minuten,
/// damit gleichzeitige Anfragen und eine verlorene Antwort nicht abmelden.
/// </summary>
public sealed class SessionStore(NpgsqlDataSource db)
{
    public const int IdleDays = 30;
    public const int MaxDays = 180;
    public const int RotateHours = 12;
    public const int GraceMinutes = 10;

    private static readonly string Valid = $"s.created_at > now() - interval '{MaxDays} days' and s.last_seen > now() - interval '{IdleDays} days' and u.totp_confirmed";

    /// <param name="strength">womit die Sitzung angemeldet wurde: "password" (mit Code) oder "passkey"; daraus entsteht amr im ID-Token der Anmelde-Zentrale.</param>
    /// <param name="stepUpUntil">Eine frische Anmeldung (mit Code aus der Authenticator-App) gilt zugleich als Freigabe fuer Aktionen bis zu diesem Zeitpunkt, null: keine.</param>
    public async Task<string> CreateAsync(Guid userId, string? ip, string? userAgent, CancellationToken ct = default, DateTimeOffset? stepUpUntil = null, string strength = "password")
    {
        var token = SessionTokens.New();
        await using var cmd = db.Cmd(
            "insert into sessions (hash, user_id, created_at, last_seen, rotated_at, user_agent, ip, step_up_until, strength) values ($1, $2, now(), now(), now(), $3, $4, $5, $6)",
            SessionTokens.Hash(token), userId, userAgent is { Length: > 120 } ? userAgent[..120] : userAgent, ip, stepUpUntil, strength);
        await cmd.ExecuteNonQueryAsync(ct);
        return token;
    }

    /// <param name="rotate">false bei WebSocket-Aufbau: dort kann kein neues Cookie gesetzt werden.</param>
    public async Task<SessionIdentity?> ResolveAsync(string token, bool rotate, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = SessionTokens.Hash(token);

        Guid userId, id; string userName; bool due;
        await using (var cmd = db.Cmd($"select s.user_id, u.name, s.rotated_at < now() - interval '{RotateHours} hours', s.id from sessions s join users u on u.id = s.user_id where s.hash = $1 and {Valid}", hash))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return await ResolveByPreviousAsync(hash, ct);
            userId = r.GetGuid(0); userName = r.GetString(1); due = r.GetBoolean(2); id = r.GetGuid(3);
        }

        if (rotate && due)
        {
            var newToken = SessionTokens.New();
            await using var up = db.Cmd(
                $"update sessions set hash = $2, prev_hash = $1, prev_until = now() + interval '{GraceMinutes} minutes', rotated_at = now(), last_seen = now() where hash = $1 and rotated_at < now() - interval '{RotateHours} hours'",
                hash, SessionTokens.Hash(newToken));
            if (await up.ExecuteNonQueryAsync(ct) == 1) return new SessionIdentity(userId, userName, SessionTokens.Hash(newToken), newToken, id);
            // eine gleichzeitige Anfrage war schneller: das alte Cookie gilt noch (Schonfrist), wir melden nur die Identitaet
            return await ResolveByPreviousAsync(hash, ct);
        }

        await using var touch = db.Cmd("update sessions set last_seen = now() where hash = $1 and last_seen < now() - interval '10 minutes'", hash);
        await touch.ExecuteNonQueryAsync(ct);
        return new SessionIdentity(userId, userName, hash, null, id);
    }

    private async Task<SessionIdentity?> ResolveByPreviousAsync(string previousHash, CancellationToken ct)
    {
        await using var cmd = db.Cmd($"select s.hash, s.user_id, u.name, s.id from sessions s join users u on u.id = s.user_id where s.prev_hash = $1 and s.prev_until > now() and {Valid}", previousHash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new SessionIdentity(r.GetGuid(1), r.GetString(2), r.GetString(0), null, r.GetGuid(3)) : null;
    }

    /// <summary>Ob die Sitzung (nach ihrer stabilen Kennung) noch gilt: nicht abgemeldet, nicht abgelaufen.</summary>
    public async Task<bool> IsAliveAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select 1 from sessions s join users u on u.id = s.user_id where s.id = $1 and {Valid}", sessionId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Gibt die Sitzung fuer Aktionen frei (nach einem richtigen Code): bis <paramref name="until"/>, oder mit <paramref name="once"/> fuer genau eine Aktion.</summary>
    public async Task GrantStepUpAsync(Guid sessionId, DateTimeOffset until, bool once, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update sessions set step_up_until = $2, step_up_once = $3 where id = $1", sessionId, until, once);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Eine geaenderte Dauer gilt sofort: bestehende Freigaben aller Sitzungen werden auf die neue Dauer gekuerzt, bei "immer fragen" (0) gestrichen. Laengere werden nie verlaengert.</summary>
    public async Task CapStepUpAsync(DateTimeOffset now, int minutes, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update sessions set step_up_until = case when $2 = 0 then null else least(step_up_until, $1::timestamptz + make_interval(mins => $2)) end where step_up_until is not null", now, minutes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ob die Sitzung jetzt freigegeben ist. Eine Freigabe "fuer eine Aktion" wird dabei verbraucht (atomar: zwei gleichzeitige Aktionen bekommen sie nicht beide).</summary>
    public async Task<bool> TakeStepUpAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default)
    {
        DateTimeOffset? until; bool once;
        await using (var cmd = db.Cmd("select step_up_until, step_up_once from sessions where id = $1", sessionId))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct) || r.IsDBNull(0)) return false;
            until = r.GetFieldValue<DateTimeOffset>(0); once = r.GetBoolean(1);
        }
        if (until <= now) return false;
        if (!once) return true;
        await using var take = db.Cmd("update sessions set step_up_until = null, step_up_once = false where id = $1 and step_up_until = $2", sessionId, until);
        return await take.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task DeleteAsync(string hash, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from sessions where hash = $1", hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DeleteOthersAsync(Guid userId, string keepHash, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from sessions where user_id = $1 and hash <> $2", userId, keepHash);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ob die Person eine Sitzung mit genau diesem Browser-Kennzeichen hat (Hinweis "neues Geraet" im Verlauf; kein Beweis, ein User-Agent ist leicht zu aendern).</summary>
    public async Task<bool> UserAgentKnownAsync(Guid userId, string? userAgent, CancellationToken ct = default)
    {
        var ua = userAgent is { Length: > 120 } ? userAgent[..120] : userAgent;
        await using var cmd = db.Cmd("select exists (select 1 from sessions where user_id = $1 and user_agent is not distinct from $2)", userId, ua);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Beendet die Sitzungen einer Staerke (nach dem Entfernen eines Passkeys: wer ihn gestohlen hatte, ist damit nicht mehr angemeldet). <paramref name="keep"/> bleibt.</summary>
    public async Task<int> DeleteByStrengthAsync(Guid userId, string strength, Guid keep, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from sessions where user_id = $1 and strength = $2 and id <> $3", userId, strength, keep);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DeleteAllAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from sessions where user_id = $1", userId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SessionListItem>> ListAsync(Guid userId, string currentHash, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select hash, created_at, last_seen, user_agent, ip from sessions where user_id = $1 order by last_seen desc", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SessionListItem>();
        while (await r.ReadAsync(ct))
            list.Add(new SessionListItem(r.GetString(0) == currentHash, r.GetFieldValue<DateTimeOffset>(1), r.GetFieldValue<DateTimeOffset>(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    /// <summary>Entfernt abgelaufene Sitzungen und alte Schonfrist-Eintraege.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        await using var del = db.Cmd($"delete from sessions where created_at < now() - interval '{MaxDays} days' or last_seen < now() - interval '{IdleDays} days'");
        var n = await del.ExecuteNonQueryAsync(ct);
        await using var clean = db.Cmd("update sessions set prev_hash = null, prev_until = null where prev_until < now()");
        await clean.ExecuteNonQueryAsync(ct);
        return n;
    }
}
