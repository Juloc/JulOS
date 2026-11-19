using JulOS.Backend.Modules;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>Der Verlauf der DNS-Aenderungen (Tabelle ddns_history, siehe Migration 013). Es bleiben die letzten 200 Zeilen.</summary>
public sealed class DdnsHistoryStore(NpgsqlDataSource db) : IDdnsHistory
{
    public const int Keep = 200;

    public async Task AddAsync(DdnsHistoryEntry e, CancellationToken ct)
    {
        await using (var cmd = db.Cmd("insert into ddns_history (at, host, record_type, old_ip, new_ip, trigger, ok, message) values ($1, $2, $3, $4, $5, $6, $7, $8)", e.At, e.Host, e.Type, e.OldIp, e.NewIp, e.Trigger, e.Ok, e.Message))
            await cmd.ExecuteNonQueryAsync(ct);
        await using var prune = db.Cmd("delete from ddns_history where id <= (select max(id) from ddns_history) - $1", (long)Keep);
        await prune.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DdnsHistoryEntry>> RecentAsync(int count, CancellationToken ct)
    {
        await using var cmd = db.Cmd("select at, host, record_type, old_ip, new_ip, trigger, ok, message from ddns_history order by id desc limit $1", count);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DdnsHistoryEntry>();
        while (await r.ReadAsync(ct))
            list.Add(new DdnsHistoryEntry(r.GetFieldValue<DateTimeOffset>(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetBoolean(6), r.GetString(7)));
        return list;
    }

    public async Task<string?> LastWrittenAsync(string host, string type, CancellationToken ct)
    {
        await using var cmd = db.Cmd("select new_ip from ddns_history where host = $1 and record_type = $2 and ok order by id desc limit 1", host, type);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
