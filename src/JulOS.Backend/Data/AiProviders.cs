using System.Security.Cryptography;
using System.Text;
using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>
/// Anbieter der KI (Tabelle ai_providers): ob er eingeschaltet ist und, fuer die beiden Schluessel-Anbieter, der API-Schluessel verschluesselt mit dem Geheimnis des Servers (SecretBox).
/// Der Schluessel verlaesst diese Klasse nur ueber <see cref="GetKeyAsync"/> an den Anbieter-Code; in keiner Antwort und in keinem Log.
/// </summary>
public sealed class AiProviderStore(NpgsqlDataSource db, SecretBox box)
{
    public bool CanStoreKeys => box.Enabled;

    // Die "zugehoerigen Daten" der Verschluesselung: ein Schluessel ist an seinen Anbieter gebunden und passt zu keinem anderen
    private static Guid Bind(string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes("julos-ai-provider:" + id))[..16]);

    public async Task<IReadOnlyDictionary<string, (bool Enabled, bool KeySet)>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id, enabled, sealed_key is not null from ai_providers");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var all = new Dictionary<string, (bool, bool)>();
        while (await r.ReadAsync(ct)) all[r.GetString(0)] = (r.GetBoolean(1), r.GetBoolean(2));
        return all;
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into ai_providers (id, enabled) values ($1, $2) on conflict (id) do update set enabled = excluded.enabled, updated_at = now()", id, enabled);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetKeyAsync(string id, string key, CancellationToken ct = default)
    {
        var sealedKey = box.Encrypt(key, Bind(id));
        await using var cmd = db.Cmd("insert into ai_providers (id, enabled, sealed_key) values ($1, true, $2) on conflict (id) do update set sealed_key = excluded.sealed_key, updated_at = now()", id, sealedKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearKeyAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update ai_providers set sealed_key = null, updated_at = now() where id = $1", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Der Klartext-Schluessel fuer den Aufruf beim Anbieter, oder null (keiner gespeichert, nicht entschluesselbar).</summary>
    public async Task<string?> GetKeyAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select sealed_key from ai_providers where id = $1", id);
        return await cmd.ExecuteScalarAsync(ct) is byte[] sealedKey ? box.Decrypt(sealedKey, Bind(id)) : null;
    }
}

/// <summary>Die Schalter des KI-Zugangs in den Einstellungen der Instanz: an oder aus (aus ist der Anfang) und das Standardmodell ("anbieter/modell").</summary>
public sealed class AiSettings(InstanceSettings settings)
{
    public const string EnabledKey = "ai.enabled", DefaultModelKey = "ai.defaultModel", CodexConfirmedKey = "ai.codexConfirmed", CodexSelfTestKey = "ai.codexSelfTest";

    /// <summary>Das letzte Ergebnis des Selbsttests des Codex-Containers als JSON (docs/CODEX-SIDECAR.md), oder null.</summary>
    public System.Text.Json.JsonElement? CodexSelfTest => settings.GetElement(CodexSelfTestKey);

    public Task SetCodexSelfTestAsync(string json, CancellationToken ct = default) => settings.SetAsync(CodexSelfTestKey, json, ct);

    /// <summary>
    /// Die Version der Codex-Bruecke, deren Pruefung von Hand der Besitzer bestaetigt hat (docs/AI-GATEWAY.md, "Before turning the Codex bridge on, check it by hand"), oder null. Ohne
    /// Bestaetigung der gerade festgelegten Version dient die Bruecke keiner App (aus per Vorgabe); eine neue Version verlangt die Pruefung neu.
    /// </summary>
    public string? CodexConfirmedVersion => settings.GetElement(CodexConfirmedKey) is { ValueKind: System.Text.Json.JsonValueKind.String } e ? e.GetString() : null;

    public Task SetCodexConfirmedAsync(string? version, CancellationToken ct = default) =>
        version is null ? settings.DeleteAsync(CodexConfirmedKey, ct) : settings.SetAsync(CodexConfirmedKey, System.Text.Json.Nodes.JsonValue.Create(version)!.ToJsonString(), ct);

    public bool Enabled => settings.GetElement(EnabledKey) is { ValueKind: System.Text.Json.JsonValueKind.True };

    public string? DefaultModel => settings.GetElement(DefaultModelKey) is { ValueKind: System.Text.Json.JsonValueKind.String } e ? e.GetString() : null;

    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => settings.SetAsync(EnabledKey, enabled ? "true" : "false", ct);

    public Task SetDefaultModelAsync(string? model, CancellationToken ct = default) =>
        model is null ? settings.DeleteAsync(DefaultModelKey, ct) : settings.SetAsync(DefaultModelKey, System.Text.Json.Nodes.JsonValue.Create(model)!.ToJsonString(), ct);
}
