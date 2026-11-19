using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record AgentRecord(Guid HostId, Guid UserId, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen, string? Version, string? Platform, string? Hostname = null, string? Os = null, bool AutoUpdate = false, string Control = "read");

/// <summary>
/// Agenten, die sich von der Ziel-Maschine aus beim Server melden. Je Host hoechstens einer. Einrichtungscodes und Token stehen
/// nur als SHA-256-Wert in der Datenbank; ein Code gilt einmal und nur kurz.
/// </summary>
public sealed class AgentStore(NpgsqlDataSource db, TimeProvider time)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private const string Columns = "host_id, user_id, created_at, last_seen, version, platform, hostname, os, auto_update, control";

    private static AgentRecord Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8), r.GetString(9));

    /// <summary>Neuer Einrichtungscode fuer einen Host des Benutzers (ein frueherer, noch offener Code wird ungueltig). Null, wenn der Host nicht ihm gehoert.</summary>
    public async Task<(string Code, DateTimeOffset Expires)?> CreateCodeAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var own = conn.Cmd(tx, "select 1 from hosts where id = $1 and user_id = $2", hostId, userId))
            if (await own.ExecuteScalarAsync(ct) is null) return null;
        await using (var old = conn.Cmd(tx, "delete from agent_codes where host_id = $1 or expires_at < $2", hostId, now)) await old.ExecuteNonQueryAsync(ct);
        var code = AgentCredentials.NewCode();
        var expires = now + CodeLifetime;
        await using (var ins = conn.Cmd(tx, "insert into agent_codes (code_hash, host_id, user_id, expires_at) values ($1, $2, $3, $4)", AgentCredentials.HashCode(code), hostId, userId, expires))
            await ins.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (code, expires);
    }

    /// <summary>Tauscht einen gueltigen Code (einmalig) gegen ein neues Token. Ein vorhandener Agent desselben Hosts wird ersetzt. Null bei unbekanntem oder abgelaufenem Code.</summary>
    public async Task<(string Token, Guid HostId, Guid UserId)?> RedeemAsync(string code, string? version, string? platform, string? hostname = null, string? os = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid hostId, userId;
        await using (var del = conn.Cmd(tx, "delete from agent_codes where code_hash = $1 and expires_at > $2 returning host_id, user_id", AgentCredentials.HashCode(code), now))
        await using (var r = await del.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            hostId = r.GetGuid(0); userId = r.GetGuid(1);
        }
        var token = AgentCredentials.NewToken();
        await using (var up = conn.Cmd(tx,
            "insert into agents (host_id, user_id, token_hash, created_at, last_seen, version, platform, hostname, os) values ($1, $2, $3, $4, $4, $5, $6, $7, $8) " +
            "on conflict (host_id) do update set token_hash = excluded.token_hash, created_at = excluded.created_at, last_seen = excluded.last_seen, version = excluded.version, platform = excluded.platform, hostname = excluded.hostname, os = excluded.os",
            hostId, userId, AgentCredentials.HashToken(token), now, version, platform, hostname, os))
            await up.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (token, hostId, userId);
    }

    /// <summary>Der Agent zu einem Token, oder null (unbekannt, widerrufen).</summary>
    public async Task<AgentRecord?> FindByTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > AgentCredentials.MaxTokenLength) return null;
        await using var cmd = db.Cmd($"select {Columns} from agents where token_hash = $1", AgentCredentials.HashToken(token));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<AgentRecord?> GetAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from agents where user_id = $1 and host_id = $2", userId, hostId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<AgentRecord>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from agents where user_id = $1", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AgentRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    /// <summary>Merkt Anzeigewerte (fehlende bleiben, wie sie waren) und den Zeitpunkt der letzten Meldung.</summary>
    public async Task TouchAsync(Guid hostId, string? version, string? platform, string? hostname = null, string? os = null, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update agents set last_seen = $2, version = coalesce($3, version), platform = coalesce($4, platform), hostname = coalesce($5, hostname), os = coalesce($6, os) where host_id = $1",
            hostId, time.GetUtcNow(), version, platform, hostname, os);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Schaltet die automatische Aktualisierung des Agenten ein oder aus. False, wenn es fuer den Host keinen Agenten des Benutzers gibt.</summary>
    public async Task<bool> SetAutoUpdateAsync(Guid userId, Guid hostId, bool enabled, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update agents set auto_update = $3 where user_id = $1 and host_id = $2", userId, hostId, enabled);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Stellt ein, was der Server diesem Agenten auftragen darf (read, updates, full). False bei unbekannter Stufe oder wenn es fuer den Host keinen Agenten des Benutzers gibt.</summary>
    public async Task<bool> SetControlAsync(Guid userId, Guid hostId, string level, CancellationToken ct = default)
    {
        if (!AgentControl.IsLevel(level)) return false;
        await using var cmd = db.Cmd("update agents set control = $3 where user_id = $1 and host_id = $2", userId, hostId, level);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Entfernt den Agenten (sein Token gilt nicht mehr) und offene Codes des Hosts.</summary>
    public async Task<bool> RevokeAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("with c as (delete from agent_codes where host_id = $2 and user_id = $1) delete from agents where user_id = $1 and host_id = $2", userId, hostId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
