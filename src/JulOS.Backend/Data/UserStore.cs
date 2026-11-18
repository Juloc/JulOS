using Npgsql;

namespace JulOS.Backend.Data;

public sealed record UserRow(Guid Id, string Name, string PasswordHash, string TotpSecret, bool TotpConfirmed, long TotpLastStep);

public sealed record UserSummary(string Name, DateTimeOffset CreatedAt, int RecoveryLeft, int Sessions);

public sealed class UserStore(NpgsqlDataSource db)
{
    private const string Columns = "id, name, password_hash, totp_secret, totp_confirmed, totp_last_step";

    private static UserRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetInt64(5));

    public async Task<bool> AnyConfirmedAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select exists (select 1 from users where totp_confirmed)");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<UserRow?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from users where lower(name) = lower($1)", name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Legt ein Konto samt Wiederherstellungscodes an (in einer Transaktion). Bei der Ersteinrichtung werden vorher unbestaetigte Konten entfernt.</summary>
    public async Task<UserRow> CreateAsync(string name, string passwordHash, string totpSecret, bool confirmed, IEnumerable<string> recoveryHashes, bool replacePending, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (replacePending)
        {
            await using var del = conn.Cmd(tx, "delete from users where not totp_confirmed");
            await del.ExecuteNonQueryAsync(ct);
        }
        Guid id;
        await using (var ins = conn.Cmd(tx, "insert into users (name, password_hash, totp_secret, totp_confirmed) values ($1, $2, $3, $4) returning id", name, passwordHash, totpSecret, confirmed))
            id = (Guid)(await ins.ExecuteScalarAsync(ct))!;
        await InsertRecoveryAsync(conn, tx, id, recoveryHashes, ct);
        await tx.CommitAsync(ct);
        return new UserRow(id, name, passwordHash, totpSecret, confirmed, 0);
    }

    private static async Task InsertRecoveryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, IEnumerable<string> hashes, CancellationToken ct)
    {
        foreach (var h in hashes)
        {
            await using var cmd = conn.Cmd(tx, "insert into recovery_codes (user_id, code_hash) values ($1, $2)", id, h);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Verbraucht einen Zeitschritt. Gelingt nur, wenn er neuer ist als der zuletzt benutzte (Schutz gegen Wiederverwenden, auch bei gleichzeitigen Anfragen).</summary>
    public async Task<bool> TryAdvanceTotpStepAsync(Guid id, long step, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update users set totp_last_step = $2 where id = $1 and totp_last_step < $2", id, step);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(Guid id, string hash, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from recovery_codes where user_id = $1 and code_hash = $2", id, hash);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> RecoveryLeftAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select count(*) from recovery_codes where user_id = $1", id);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ConfirmAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update users set totp_confirmed = true where id = $1", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetPasswordAsync(Guid id, string passwordHash, bool endSessions, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.Cmd(tx, "update users set password_hash = $2 where id = $1", id, passwordHash)) await cmd.ExecuteNonQueryAsync(ct);
        if (endSessions) await using (var cmd = conn.Cmd(tx, "delete from sessions where user_id = $1", id)) await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Neuer TOTP-Schluessel und neue Wiederherstellungscodes; beendet alle Sitzungen.</summary>
    public async Task ReplaceTotpAsync(Guid id, string secret, IEnumerable<string> recoveryHashes, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.Cmd(tx, "update users set totp_secret = $2, totp_last_step = 0, totp_confirmed = true where id = $1", id, secret)) await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = conn.Cmd(tx, "delete from recovery_codes where user_id = $1", id)) await cmd.ExecuteNonQueryAsync(ct);
        await InsertRecoveryAsync(conn, tx, id, recoveryHashes, ct);
        await using (var cmd = conn.Cmd(tx, "delete from sessions where user_id = $1", id)) await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from users where lower(name) = lower($1)", name);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("""
            select u.name, u.created_at,
                   (select count(*) from recovery_codes c where c.user_id = u.id),
                   (select count(*) from sessions s where s.user_id = u.id)
            from users u where u.totp_confirmed order by u.name
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<UserSummary>();
        while (await r.ReadAsync(ct)) list.Add(new UserSummary(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), (int)r.GetInt64(2), (int)r.GetInt64(3)));
        return list;
    }
}
