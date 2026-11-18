using Npgsql;

namespace JulOS.Backend.Data;

public sealed record ConnectionRecord(Guid Id, string Name, string Kind, string Host, int Port, string Username);

/// <summary>Gespeicherte Verbindungen eines Benutzers. Es wird kein Passwort gespeichert; es wird bei jeder Verbindung getippt.</summary>
public sealed class ConnectionStore(NpgsqlDataSource db)
{
    private const string Columns = "id, name, kind, host, port, username";

    private static ConnectionRecord Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5));

    public async Task<ConnectionRecord> CreateAsync(Guid userId, string name, string kind, string host, int port, string username, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd(
            $"insert into connections (user_id, name, kind, host, port, username, sort) values ($1, $2, $3, $4, $5, $6, coalesce((select max(sort) + 1 from connections where user_id = $1), 0)) returning {Columns}",
            userId, name, kind, host, port, username);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Read(r);
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
}
