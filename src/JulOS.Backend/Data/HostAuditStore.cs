using Npgsql;

namespace JulOS.Backend.Data;

public sealed record AuditEntry(long Id, DateTimeOffset At, string UserName, string Action, string? Target, string Event, string? Detail, string? Job);

/// <summary>
/// Der Verlauf der Aktionen auf Hosts (Tabelle host_audit, docs/HOST-AGENT.md): jede Anfrage, Ablehnung und jedes Ergebnis, mit Benutzer, Host, Aktion, Ziel, Zeit.
/// Nur kurze Codes und Namen, nie ein Geheimnis oder der Inhalt einer Ausgabe. Jede Abfrage nennt den Benutzer; je Host bleiben die letzten <see cref="KeepPerHost"/> Eintraege.
/// </summary>
public sealed class HostAuditStore(NpgsqlDataSource db, TimeProvider time)
{
    public const int KeepPerHost = 500;

    public async Task AddAsync(Guid userId, Guid hostId, string hostName, string action, string? target, string evt, string? detail = null, string? job = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var ins = conn.Cmd(null, "insert into host_audit (at, user_id, host_id, host_name, action, target, event, detail, job) values ($1, $2, $3, $4, $5, $6, $7, $8, $9)",
            time.GetUtcNow(), userId, hostId, Cut(hostName, 80)!, Cut(action, 40)!, Cut(target, 200), Cut(evt, 24)!, Cut(detail, 80), Cut(job, 40)))
            await ins.ExecuteNonQueryAsync(ct);
        // Der Verlauf waechst nicht unbegrenzt: je Host bleiben die letzten Eintraege
        await using var prune = conn.Cmd(null, "delete from host_audit where user_id = $1 and host_id = $2 and id < coalesce((select min(id) from (select id from host_audit where user_id = $1 and host_id = $2 order by id desc limit $3) k), 0)", userId, hostId, KeepPerHost);
        await prune.ExecuteNonQueryAsync(ct);
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    /// <summary>Die neuesten Eintraege eines Hosts des Benutzers, neueste zuerst.</summary>
    public async Task<IReadOnlyList<AuditEntry>> ListAsync(Guid userId, Guid hostId, int limit, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select a.id, a.at, u.name, a.action, a.target, a.event, a.detail, a.job from host_audit a join users u on u.id = a.user_id where a.user_id = $1 and a.host_id = $2 order by a.id desc limit $3", userId, hostId, Math.Clamp(limit, 1, 200));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditEntry>();
        while (await r.ReadAsync(ct))
            list.Add(new AuditEntry(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }
}
