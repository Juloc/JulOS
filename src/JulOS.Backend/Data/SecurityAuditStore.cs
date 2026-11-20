using Npgsql;

namespace JulOS.Backend.Data;

public sealed record SecurityAuditEntry(long Id, DateTimeOffset At, string UserName, string Event, string? Detail);

/// <summary>
/// Verlauf der Sicherheits-Einstellungen und der Freigaben fuer Aktionen (Tabelle security_audit): wer, was, wann. Nur kurze Codes und Listen von Adressen,
/// nie ein Code aus der Authenticator-App und nie ein Schluessel. Es bleiben die letzten <see cref="Keep"/> Eintraege.
/// </summary>
public sealed class SecurityAuditStore(NpgsqlDataSource db, TimeProvider time)
{
    public const int Keep = 500;

    public async Task AddAsync(string userName, string evt, string? detail = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var ins = conn.Cmd(null, "insert into security_audit (at, user_name, event, detail) values ($1, $2, $3, $4)", time.GetUtcNow(), Cut(userName, 40)!, Cut(evt, 40)!, Cut(detail, 400)))
            await ins.ExecuteNonQueryAsync(ct);
        await using var prune = conn.Cmd(null, "delete from security_audit where id < coalesce((select min(id) from (select id from security_audit order by id desc limit $1) k), 0)", Keep);
        await prune.ExecuteNonQueryAsync(ct);
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    public async Task<IReadOnlyList<SecurityAuditEntry>> ListAsync(int limit, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id, at, user_name, event, detail from security_audit order by id desc limit $1", Math.Clamp(limit, 1, 200));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SecurityAuditEntry>();
        while (await r.ReadAsync(ct)) list.Add(new SecurityAuditEntry(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }
}
