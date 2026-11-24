using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record AiApp(Guid Id, string Name, string KeyHint, bool Active, string[] Allowed, long? DailyTokens, long? MonthlyTokens, int RatePerMinute, bool AllowFiles, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// Die Apps der Familie, die KI ueber JulOS benutzen (Tabelle ai_apps). Der Schluessel einer App wird einmal angezeigt und nur als SHA-256 gespeichert. Ein Schluessel gibt Zugang zur
/// KI-API und zu sonst nichts in JulOS.
/// </summary>
public sealed class AiAppStore(NpgsqlDataSource db, TimeProvider time)
{
    private const string Columns = "id, name, key_hint, active, allowed, daily_tokens, monthly_tokens, rate_per_minute, allow_files, created_at, last_used_at";

    private static AiApp Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetFieldValue<string[]>(4),
        r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.GetInt32(7), r.GetBoolean(8), r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10));

    public async Task<IReadOnlyList<AiApp>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from ai_apps order by lower(name)");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AiApp>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<AiApp?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from ai_apps where id = $1", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Legt eine App an und gibt sie mit ihrem Schluessel zurueck (Key ist null, wenn der Name schon vergeben ist).</summary>
    public async Task<(AiApp? App, string? Key)> CreateAsync(string name, string[] allowed, long? daily, long? monthly, int rate, bool allowFiles, CancellationToken ct = default)
    {
        var key = AiRules.NewKey();
        try
        {
            await using var cmd = db.Cmd($"insert into ai_apps (name, key_hash, key_hint, allowed, daily_tokens, monthly_tokens, rate_per_minute, allow_files) values ($1, $2, $3, $4, $5, $6, $7, $8) returning {Columns}",
                name.Trim(), AiRules.HashKey(key), AiRules.KeyHint(key), allowed, daily, monthly, rate, allowFiles);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (Read(r), key);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return (null, null); }
    }

    /// <summary>Aendert Name, Rechte und Grenzen (nicht den Schluessel). False: unbekannte App oder der Name ist schon vergeben (conflict).</summary>
    public async Task<(bool Found, bool Conflict)> UpdateAsync(Guid id, string name, string[] allowed, long? daily, long? monthly, int rate, bool allowFiles, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = db.Cmd("update ai_apps set name = $2, allowed = $3, daily_tokens = $4, monthly_tokens = $5, rate_per_minute = $6, allow_files = $7 where id = $1",
                id, name.Trim(), allowed, daily, monthly, rate, allowFiles);
            return (await cmd.ExecuteNonQueryAsync(ct) == 1, false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return (false, true); }
    }

    public async Task<bool> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update ai_apps set active = $2 where id = $1", id, active);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Ein neuer Schluessel; der alte gilt sofort nicht mehr.</summary>
    public async Task<string?> RotateKeyAsync(Guid id, CancellationToken ct = default)
    {
        var key = AiRules.NewKey();
        await using var cmd = db.Cmd("update ai_apps set key_hash = $2, key_hint = $3 where id = $1", id, AiRules.HashKey(key), AiRules.KeyHint(key));
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? key : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from ai_apps where id = $1", id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Die App zu einem Schluessel oder null. Es werden alle Hashes verglichen (gleiche Dauer fuer Treffer und Fehlschlag); eine gesperrte App ist ebenfalls eine App.</summary>
    public async Task<AiApp?> FindByKeyAsync(string key, CancellationToken ct = default)
    {
        var hash = AiRules.HashKey(key);
        await using var cmd = db.Cmd($"select key_hash, {Columns} from ai_apps");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var candidates = new List<(byte[], AiApp)>();
        while (await r.ReadAsync(ct)) candidates.Add((r.GetFieldValue<byte[]>(0), new AiApp(r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetFieldValue<string[]>(5),
            r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7), r.GetInt32(8), r.GetBoolean(9), r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11))));
        return AiRules.Match(hash, candidates);
    }

    public async Task TouchAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update ai_apps set last_used_at = $2 where id = $1", id, time.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
