using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record AgentRecord(Guid ConnectionId, Guid UserId, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen, string? Version, string? Platform);

/// <summary>
/// Agenten, die sich von der Ziel-Maschine aus beim Server melden. Je Verbindung hoechstens einer. Einrichtungscodes und Token stehen
/// nur als SHA-256-Wert in der Datenbank; ein Code gilt einmal und nur kurz.
/// </summary>
public sealed class AgentStore(NpgsqlDataSource db, TimeProvider time)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private const string Columns = "connection_id, user_id, created_at, last_seen, version, platform";

    private static AgentRecord Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));

    /// <summary>Neuer Einrichtungscode fuer eine Verbindung des Benutzers (ein frueherer, noch offener Code wird ungueltig). Null, wenn die Verbindung nicht ihm gehoert.</summary>
    public async Task<(string Code, DateTimeOffset Expires)?> CreateCodeAsync(Guid userId, Guid connectionId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var own = conn.Cmd(tx, "select 1 from connections where id = $1 and user_id = $2 and kind = 'desktop'", connectionId, userId))
            if (await own.ExecuteScalarAsync(ct) is null) return null;
        await using (var old = conn.Cmd(tx, "delete from agent_codes where connection_id = $1 or expires_at < $2", connectionId, now)) await old.ExecuteNonQueryAsync(ct);
        var code = AgentCredentials.NewCode();
        var expires = now + CodeLifetime;
        await using (var ins = conn.Cmd(tx, "insert into agent_codes (code_hash, connection_id, user_id, expires_at) values ($1, $2, $3, $4)", AgentCredentials.HashCode(code), connectionId, userId, expires))
            await ins.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (code, expires);
    }

    /// <summary>Tauscht einen gueltigen Code (einmalig) gegen ein neues Token. Ein vorhandener Agent derselben Verbindung wird ersetzt. Null bei unbekanntem oder abgelaufenem Code.</summary>
    public async Task<(string Token, Guid ConnectionId, Guid UserId)?> RedeemAsync(string code, string? version, string? platform, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid connectionId, userId;
        await using (var del = conn.Cmd(tx, "delete from agent_codes where code_hash = $1 and expires_at > $2 returning connection_id, user_id", AgentCredentials.HashCode(code), now))
        await using (var r = await del.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            connectionId = r.GetGuid(0); userId = r.GetGuid(1);
        }
        var token = AgentCredentials.NewToken();
        await using (var up = conn.Cmd(tx,
            "insert into agents (connection_id, user_id, token_hash, created_at, last_seen, version, platform) values ($1, $2, $3, $4, $4, $5, $6) " +
            "on conflict (connection_id) do update set token_hash = excluded.token_hash, created_at = excluded.created_at, last_seen = excluded.last_seen, version = excluded.version, platform = excluded.platform",
            connectionId, userId, AgentCredentials.HashToken(token), now, version, platform))
            await up.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (token, connectionId, userId);
    }

    /// <summary>Der Agent zu einem Token, oder null (unbekannt, widerrufen).</summary>
    public async Task<AgentRecord?> FindByTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > AgentCredentials.MaxTokenLength) return null;
        await using var cmd = db.Cmd($"select {Columns} from agents where token_hash = $1", AgentCredentials.HashToken(token));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<AgentRecord?> GetAsync(Guid userId, Guid connectionId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from agents where user_id = $1 and connection_id = $2", userId, connectionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Gehoert die Verbindung dem Benutzer (null: nein), und hat sie einen eingerichteten Agenten?</summary>
    public async Task<bool?> IsEnrolledAsync(Guid userId, Guid connectionId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select a.connection_id is not null from connections c left join agents a on a.connection_id = c.id where c.id = $2 and c.user_id = $1", userId, connectionId);
        return await cmd.ExecuteScalarAsync(ct) is bool enrolled ? enrolled : null;
    }

    public async Task<IReadOnlyList<AgentRecord>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from agents where user_id = $1", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AgentRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    /// <summary>Merkt Anzeigewerte und den Zeitpunkt der letzten Meldung.</summary>
    public async Task TouchAsync(Guid connectionId, string? version, string? platform, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update agents set last_seen = $2, version = coalesce($3, version), platform = coalesce($4, platform) where connection_id = $1", connectionId, time.GetUtcNow(), version, platform);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Entfernt den Agenten (sein Token gilt nicht mehr) und offene Codes der Verbindung.</summary>
    public async Task<bool> RevokeAsync(Guid userId, Guid connectionId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("with c as (delete from agent_codes where connection_id = $2 and user_id = $1) delete from agents where user_id = $1 and connection_id = $2", userId, connectionId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
