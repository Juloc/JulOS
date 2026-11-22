using Npgsql;

namespace JulOS.Backend.Data;

public sealed record WorkspaceRow(string DeviceId, string Name, string Doc, int Version, DateTimeOffset UpdatedAt);

/// <summary>
/// Der Arbeitsplatz je Benutzer und Geraet (Tabelle workspaces, docs/MULTI-SCREEN.md). Jede Abfrage hat den Benutzer in der Bedingung: ein fremdes Geraet ist nicht
/// zu finden, auch wenn jemand dessen Kennung kennt. Das Dokument kommt gepruefte hier an (WorkspaceRules).
/// </summary>
public sealed class WorkspaceStore(NpgsqlDataSource db)
{
    private static WorkspaceRow Read(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4));

    private const string Columns = "device_id, name, doc::text, version, updated_at";

    public async Task<WorkspaceRow?> GetAsync(Guid userId, string deviceId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from workspaces where user_id = $1 and device_id = $2", userId, deviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Alle Geraete des Benutzers, das zuletzt benutzte zuerst.</summary>
    public async Task<IReadOnlyList<WorkspaceRow>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from workspaces where user_id = $1 order by updated_at desc", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<WorkspaceRow>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// Speichert das ganze Dokument, wenn <paramref name="expectedVersion"/> der gespeicherten Version entspricht (0: es gibt noch keins). Sonst null: ein anderes
    /// Browserfenster desselben Geraets war schneller, und der Aufrufer meldet den aktuellen Stand. Pruefen und Schreiben sind eine Anweisung, es gibt kein Dazwischen.
    /// Ein neues Geraet ueber der Obergrenze verdraengt das am laengsten unbenutzte (Browser, die ihre Daten loeschen, erzeugen sonst nur Leichen).
    /// </summary>
    public async Task<(int Version, DateTimeOffset UpdatedAt)?> SaveAsync(Guid userId, string deviceId, string? name, string doc, int expectedVersion, int maxDevices, CancellationToken ct = default)
    {
        if (expectedVersion == 0)
        {
            await using var ins = db.Cmd("insert into workspaces (user_id, device_id, name, doc) values ($1, $2, $3, $4::text::jsonb) on conflict (user_id, device_id) do nothing returning version, updated_at", userId, deviceId, name ?? "", doc);
            await using var r = await ins.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var saved = (r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1));
            await r.CloseAsync();
            await using var trim = db.Cmd("delete from workspaces where user_id = $1 and device_id in (select device_id from workspaces where user_id = $1 order by updated_at desc offset $2)", userId, maxDevices);
            await trim.ExecuteNonQueryAsync(ct);
            return saved;
        }
        await using var up = db.Cmd("update workspaces set doc = $3::text::jsonb, name = coalesce($4, name), version = version + 1, updated_at = now() where user_id = $1 and device_id = $2 and version = $5 returning version, updated_at", userId, deviceId, doc, name, expectedVersion);
        await using var u = await up.ExecuteReaderAsync(ct);
        return await u.ReadAsync(ct) ? (u.GetInt32(0), u.GetFieldValue<DateTimeOffset>(1)) : null;
    }

    /// <summary>Das Geraet vergessen. false, wenn es nichts zu vergessen gab.</summary>
    public async Task<bool> DeleteAsync(Guid userId, string deviceId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from workspaces where user_id = $1 and device_id = $2", userId, deviceId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Von diesen Kennungen die, die eine Verbindung dieses Benutzers sind.</summary>
    public async Task<HashSet<Guid>> OwnedConnectionsAsync(Guid userId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var owned = new HashSet<Guid>();
        if (ids.Count == 0) return owned;
        await using var cmd = db.Cmd("select id from connections where user_id = $1 and id = any($2)", userId, ids.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) owned.Add(r.GetGuid(0));
        return owned;
    }
}
