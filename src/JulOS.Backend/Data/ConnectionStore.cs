using Npgsql;

namespace JulOS.Backend.Data;

public sealed record ConnectionRecord(Guid Id, string Name, string Kind, string Host, int Port, string Username, bool HasPassword, string Engine);

/// <summary>Gespeicherte Verbindungen eines Benutzers. Ein Passwort gibt es nur auf Wunsch und nur verschluesselt (siehe SecretBox); die Listen liefern nie das Passwort, nur ob eins da ist.</summary>
public sealed class ConnectionStore(NpgsqlDataSource db)
{
    private const string Columns = "id, name, kind, host, port, username, secret is not null, engine";

    private static ConnectionRecord Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetBoolean(6), r.GetString(7));

    public async Task<ConnectionRecord> CreateAsync(Guid userId, string name, string kind, string host, int port, string username, string engine = "guac", CancellationToken ct = default)
    {
        await using var cmd = db.Cmd(
            $"insert into connections (user_id, name, kind, host, port, username, engine, sort) values ($1, $2, $3, $4, $5, $6, $7, coalesce((select max(sort) + 1 from connections where user_id = $1), 0)) returning {Columns}",
            userId, name, kind, host, port, username, engine);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Read(r);
    }

    public async Task<ConnectionRecord?> UpdateAsync(Guid userId, Guid id, string name, string kind, string host, int port, string username, string engine = "guac", CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"update connections set name = $3, kind = $4, host = $5, port = $6, username = $7, engine = $8 where user_id = $1 and id = $2 returning {Columns}",
            userId, id, name, kind, host, port, username, engine);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from connections where user_id = $1 and id = $2", userId, id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> CountAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select count(*) from connections where user_id = $1", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ConnectionRecord?> GetAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from connections where user_id = $1 and id = $2", userId, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<ConnectionRecord>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from connections where user_id = $1 order by sort, created_at", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ConnectionRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    /// <summary>Setzt das verschluesselte Passwort, oder entfernt es (null).</summary>
    public async Task SetSecretAsync(Guid userId, Guid id, byte[]? secret, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update connections set secret = $3 where user_id = $1 and id = $2", userId, id, secret is null ? DBNull.Value : secret);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]?> GetSecretAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select secret from connections where user_id = $1 and id = $2", userId, id);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }
}
