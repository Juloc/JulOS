using System.Text.Json;
using JulOS.Backend.Modules;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>Die Dienste eines Benutzers (Modul "Dienste"). Jede Abfrage nennt den Benutzer. Der Schluessel liegt verschluesselt in <c>secret</c> und kommt nur ueber <see cref="GetSecretAsync"/> heraus, nie in die Listen.</summary>
public sealed class ServiceStore(NpgsqlDataSource db)
{
    private const string Columns = "id, name, icon, open_url, health_url, metrics_url, metrics::text, interval_seconds, header_name, secret is not null, template";

    private static ServiceRecord Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        ParseMetrics(r.GetString(6)), r.GetInt32(7), r.GetString(8), r.GetBoolean(9), r.GetString(10));

    private static ServiceMetric[] ParseMetrics(string json)
    {
        try { return JsonSerializer.Deserialize(json, ModuleJson.Default.ServiceMetricArray) ?? []; }
        catch (JsonException) { return []; }   // eine beschaedigte Zeile macht die Liste nicht kaputt
    }

    private static string Metrics(ServiceMetric[] m) => JsonSerializer.Serialize(m, ModuleJson.Default.ServiceMetricArray);

    public async Task<IReadOnlyList<ServiceRecord>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from services where user_id = $1 order by sort, created_at, id", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ServiceRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<ServiceRecord?> GetAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from services where user_id = $1 and id = $2", userId, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<int> CountAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select count(*) from services where user_id = $1", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ServiceRecord> CreateAsync(Guid userId, ServiceInput s, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd(
            $"insert into services (user_id, name, icon, open_url, health_url, metrics_url, metrics, interval_seconds, header_name, template, sort) values ($1, $2, $3, $4, $5, $6, $7::text::jsonb, $8, $9, $10, coalesce((select max(sort) + 1 from services where user_id = $1), 0)) returning {Columns}",
            userId, s.Name, s.Icon, s.OpenUrl, s.HealthUrl, s.MetricsUrl, Metrics(s.Metrics), s.IntervalSeconds, s.HeaderName, s.Template);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Read(r);
    }

    public async Task<ServiceRecord?> UpdateAsync(Guid userId, Guid id, ServiceInput s, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd(
            $"update services set name = $3, icon = $4, open_url = $5, health_url = $6, metrics_url = $7, metrics = $8::text::jsonb, interval_seconds = $9, header_name = $10, template = $11 where user_id = $1 and id = $2 returning {Columns}",
            userId, id, s.Name, s.Icon, s.OpenUrl, s.HealthUrl, s.MetricsUrl, Metrics(s.Metrics), s.IntervalSeconds, s.HeaderName, s.Template);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from services where user_id = $1 and id = $2", userId, id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Setzt den verschluesselten Schluessel, oder entfernt ihn (null).</summary>
    public async Task SetSecretAsync(Guid userId, Guid id, byte[]? secret, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update services set secret = $3 where user_id = $1 and id = $2", userId, id, secret is null ? DBNull.Value : secret);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]?> GetSecretAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select secret from services where user_id = $1 and id = $2", userId, id);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }
}
