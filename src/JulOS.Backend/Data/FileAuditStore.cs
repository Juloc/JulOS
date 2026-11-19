using Npgsql;

namespace JulOS.Backend.Data;

public sealed record FileAuditEntry(long Id, DateTimeOffset At, string UserName, string Host, string Action, string? Path, string Result, string? Detail, int? Items);

/// <summary>
/// Der Verlauf der Dateien-App (Tabelle files_audit, docs/FILES.md): jede schreibende Aktion und jeder Fehler mit Benutzer, Host, Aktion, Pfad (gekuerzt), Ergebnis und Zeit.
/// Nie ein Inhalt und nie ein Passwort; nur Pfade, Zaehler und kurze Codes. Je Benutzer bleiben die letzten <see cref="KeepPerUser"/> Eintraege.
/// </summary>
public sealed class FileAuditStore(NpgsqlDataSource db, TimeProvider time)
{
    public const int KeepPerUser = 2000, MaxPath = 300;

    public async Task AddAsync(Guid userId, Guid? connectionId, string host, string action, string? path, string result, string? detail = null, int? items = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var ins = conn.Cmd(null, "insert into files_audit (at, user_id, connection_id, host_name, action, path, result, detail, items) values ($1, $2, $3, $4, $5, $6, $7, $8, $9)",
            time.GetUtcNow(), userId, connectionId, Cut(host, 120)!, Cut(action, 24)!, CutPath(path), Cut(result, 16)!, Cut(detail, 60), items))
            await ins.ExecuteNonQueryAsync(ct);
        await using var prune = conn.Cmd(null, "delete from files_audit where user_id = $1 and id < coalesce((select min(id) from (select id from files_audit where user_id = $1 order by id desc limit $2) k), 0)", userId, KeepPerUser);
        await prune.ExecuteNonQueryAsync(ct);
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    /// <summary>Ein langer Pfad behaelt sein Ende (den Namen), vorn steht "…". Steuerzeichen werden zu Leerzeichen (eine Zeile im Verlauf bleibt eine Zeile).</summary>
    internal static string? CutPath(string? p)
    {
        if (p is null) return null;
        p = new string(p.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return p.Length <= MaxPath ? p : "…" + p[^(MaxPath - 1)..];
    }

    /// <summary>Die neuesten Eintraege des Benutzers, neueste zuerst.</summary>
    public async Task<IReadOnlyList<FileAuditEntry>> ListAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select a.id, a.at, u.name, a.host_name, a.action, a.path, a.result, a.detail, a.items from files_audit a join users u on u.id = a.user_id where a.user_id = $1 order by a.id desc limit $2", userId, Math.Clamp(limit, 1, 200));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FileAuditEntry>();
        while (await r.ReadAsync(ct))
            list.Add(new FileAuditEntry(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt32(8)));
        return list;
    }
}
