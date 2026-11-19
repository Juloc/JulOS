using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record HostRecord(Guid Id, string Name, DateTimeOffset CreatedAt);

public enum HostDelete { Deleted, NotFound, HasConnections }

/// <summary>Die Hosts eines Benutzers: Rechner oder Server, zu denen Verbindungen und ein Agent gehoeren (docs/HOSTS.md). Jede Abfrage nennt den Benutzer.</summary>
public sealed class HostStore(NpgsqlDataSource db)
{
    private const string Columns = "id, name, created_at";

    private static HostRecord Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2));

    public async Task<IReadOnlyList<HostRecord>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from hosts where user_id = $1 order by sort, created_at, id", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<HostRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<HostRecord?> GetAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from hosts where user_id = $1 and id = $2", userId, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<int> CountAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select count(*) from hosts where user_id = $1", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Ein neuer Host am Ende der Liste.</summary>
    public async Task<HostRecord> CreateAsync(Guid userId, string name, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"insert into hosts (user_id, name, sort) values ($1, $2, coalesce((select max(sort) + 1 from hosts where user_id = $1), 0)) returning {Columns}", userId, name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Read(r);
    }

    public async Task<HostRecord?> RenameAsync(Guid userId, Guid id, string name, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"update hosts set name = $3 where user_id = $1 and id = $2 returning {Columns}", userId, id, name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Loescht den Host samt Agent und offenen Codes (Fremdschluessel), aber nur wenn er keine Verbindung mehr hat: Verbindungen gehen nie nebenbei verloren.</summary>
    public async Task<HostDelete> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var own = conn.Cmd(tx, "select 1 from hosts where user_id = $1 and id = $2 for update", userId, id))
            if (await own.ExecuteScalarAsync(ct) is null) return HostDelete.NotFound;
        await using (var used = conn.Cmd(tx, "select 1 from connections where host_id = $1 limit 1", id))
            if (await used.ExecuteScalarAsync(ct) is not null) return HostDelete.HasConnections;
        await using (var del = conn.Cmd(tx, "delete from hosts where user_id = $1 and id = $2", userId, id)) await del.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return HostDelete.Deleted;
    }

    /// <summary>Der erste Host des Benutzers; gibt es noch keinen, wird einer mit dem Standardnamen angelegt (der Benutzer hat immer einen Ort fuer neue Verbindungen).</summary>
    public async Task<Guid> EnsureDefaultAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Zwei gleichzeitige Aufrufe desselben Benutzers duerfen nicht zwei Standard-Hosts anlegen
        await using (var lk = conn.Cmd(tx, "select pg_advisory_xact_lock(hashtextextended($1, 0))", "host-default:" + userId)) await lk.ExecuteNonQueryAsync(ct);
        await using (var find = conn.Cmd(tx, "select id from hosts where user_id = $1 order by sort, created_at, id limit 1", userId))
            if (await find.ExecuteScalarAsync(ct) is Guid existing) return existing;
        await using var ins = conn.Cmd(tx, "insert into hosts (user_id, name) values ($1, $2) returning id", userId, HostRules.DefaultName);
        var id = (Guid)(await ins.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Der Host einer Verbindung des Benutzers und ob dort ein Agent eingerichtet ist; null, wenn die Verbindung nicht ihm gehoert.</summary>
    public async Task<(Guid HostId, bool AgentEnrolled)?> OfConnectionAsync(Guid userId, Guid connectionId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select c.host_id, a.host_id is not null from connections c left join agents a on a.host_id = c.host_id where c.id = $2 and c.user_id = $1", userId, connectionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetBoolean(1)) : null;
    }

    /// <summary>Ob der Host dem Benutzer gehoert und ob dort ein Agent eingerichtet ist; null, wenn nicht sein Host.</summary>
    public async Task<bool?> AgentEnrolledAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select a.host_id is not null from hosts h left join agents a on a.host_id = h.id where h.id = $2 and h.user_id = $1", userId, hostId);
        return await cmd.ExecuteScalarAsync(ct) is bool enrolled ? enrolled : null;
    }
}
