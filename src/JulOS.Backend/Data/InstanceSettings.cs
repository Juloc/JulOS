using System.Text.Json;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>
/// Einstellungen der ganzen Instanz (Tabelle instance_settings): ein JSON-Wert je Schluessel. Der Stand liegt im Speicher und wird beim Start geladen und bei jeder
/// Aenderung ersetzt, damit Leser (der Proxy-Filter, die Zielpruefung) ohne Datenbankzugriff und ohne Neustart den aktuellen Wert sehen. Ein Wert ist hier nur Text;
/// was er bedeutet und ob er gueltig ist, prueft <see cref="JulOS.Backend.Logic.SecurityConfig"/> vor dem Schreiben.
/// </summary>
public sealed class InstanceSettings(NpgsqlDataSource db)
{
    private volatile IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>();

    /// <summary>Nach jeder Aenderung (auch dem Laden).</summary>
    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select key, value::text from instance_settings");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var all = new Dictionary<string, string>();
        while (await r.ReadAsync(ct)) all[r.GetString(0)] = r.GetString(1);
        _values = all;
        Changed?.Invoke();
    }

    /// <summary>Der Wert als JSON-Text, oder null wenn nichts gesetzt ist.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>Der Wert als JSON-Element, oder null. (Kein Reflection-JSON: der Server laeuft ohne.)</summary>
    public JsonElement? GetElement(string key) => Get(key) is { } json ? JsonDocument.Parse(json).RootElement.Clone() : null;

    /// <summary>Der Wert als Liste von Texten, oder null wenn nichts gesetzt ist (oder es keine Liste ist).</summary>
    public string[]? GetStrings(string key) =>
        GetElement(key) is { ValueKind: JsonValueKind.Array } a ? [.. a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)] : null;

    public async Task SetAsync(string key, string json, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into instance_settings (key, value) values ($1, $2::text::jsonb) on conflict (key) do update set value = excluded.value, updated_at = now()", key, json);
        await cmd.ExecuteNonQueryAsync(ct);
        await LoadAsync(ct);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from instance_settings where key = $1", key);
        var removed = await cmd.ExecuteNonQueryAsync(ct) == 1;
        await LoadAsync(ct);
        return removed;
    }
}
