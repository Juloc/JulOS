using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>Einstellungen je Benutzer als JSON-Wert je Schluessel (der Server prueft sie vorher, siehe SettingsRules).</summary>
public sealed class SettingsStore(NpgsqlDataSource db)
{
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select key, value::text from user_settings where user_id = $1", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var all = new Dictionary<string, string>();
        while (await r.ReadAsync(ct)) all[r.GetString(0)] = r.GetString(1);
        return all;
    }

    public async Task SetAsync(Guid userId, string key, string json, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into user_settings (user_id, key, value) values ($1, $2, $3::text::jsonb) on conflict (user_id, key) do update set value = excluded.value, updated_at = now()", userId, key, json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, string key, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from user_settings where user_id = $1 and key = $2", userId, key);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
