using Npgsql;

namespace JulOS.Backend.Data;

public sealed record FolderRecord(Guid Id, string Name, string Icon, int Sort);

/// <summary>Wo eine Verbindung einsortiert ist: Ordner, Favorit, letzte Benutzung. Getrennt von ConnectionRecord, damit die Verbindungsliste selbst unveraendert bleibt.</summary>
public sealed record ConnectionPlacement(Guid Id, Guid? FolderId, bool Favorite, DateTimeOffset? LastUsedAt, int UseCount);

/// <summary>Ordner eines Benutzers und die Einsortierung seiner Verbindungen (Ordner, Favorit, zuletzt benutzt, Reihenfolge). Siehe docs/FOLDERS.md.</summary>
public sealed class FolderStore(NpgsqlDataSource db)
{
    private static FolderRecord Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3));

    public async Task<IReadOnlyList<FolderRecord>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id, name, icon, sort from folders where user_id = $1 order by sort, created_at", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FolderRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<int> CountAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select count(*) from folders where user_id = $1", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<FolderRecord> CreateAsync(Guid userId, string name, string icon, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into folders (user_id, name, icon, sort) values ($1, $2, $3, coalesce((select max(sort) + 1 from folders where user_id = $1), 0)) returning id, name, icon, sort", userId, name, icon);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Read(r);
    }

    public async Task<FolderRecord?> UpdateAsync(Guid userId, Guid id, string name, string icon, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update folders set name = $3, icon = $4 where user_id = $1 and id = $2 returning id, name, icon, sort", userId, id, name, icon);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Loescht den Ordner; seine Verbindungen bleiben (die Datenbank loest nur die Zuordnung).</summary>
    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from folders where user_id = $1 and id = $2", userId, id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Neue Reihenfolge der Ordner: die genannten in dieser Reihenfolge zuerst, alle anderen dahinter wie bisher. Fremde und unbekannte Kennungen werden uebersprungen.</summary>
    public async Task ReorderAsync(Guid userId, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rest = new List<Guid>();
        await using (var cmd = conn.Cmd(tx, "select id from folders where user_id = $1 order by sort, created_at", userId))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) rest.Add(r.GetGuid(0));
        var first = ids.Distinct().Where(rest.Contains).ToList();
        var order = first.Concat(rest.Except(first)).ToArray();
        await using (var set = conn.Cmd(tx, "update folders f set sort = o.ord - 1 from unnest($2::uuid[]) with ordinality as o(id, ord) where f.id = o.id and f.user_id = $1", userId, order))
            await set.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ConnectionPlacement>> PlacementsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id, folder_id, favorite, last_used_at, use_count from connections where user_id = $1 order by sort, created_at", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ConnectionPlacement>();
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.GetInt32(4)));
        return list;
    }

    /// <summary>Legt die Verbindung in einen Ordner (null: ohne Ordner). False, wenn die Verbindung oder der Ordner dem Benutzer nicht gehoert.</summary>
    public async Task<bool> SetFolderAsync(Guid userId, Guid connection, Guid? folder, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update connections set folder_id = $3 where user_id = $1 and id = $2 and ($3::uuid is null or exists (select 1 from folders where id = $3 and user_id = $1))", userId, connection, folder);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> SetFavoriteAsync(Guid userId, Guid connection, bool favorite, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update connections set favorite = $3 where user_id = $1 and id = $2", userId, connection, favorite);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Merkt, dass die Verbindung gerade benutzt wurde ("Zuletzt benutzt", Haeufigkeit).</summary>
    public async Task<bool> TouchAsync(Guid userId, Guid connection, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update connections set last_used_at = now(), use_count = least(use_count + 1, 1000000) where user_id = $1 and id = $2", userId, connection);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Verschiebt die Verbindung um eine Stelle (delta -1 hoch, +1 runter) innerhalb ihres Ordners (oder der Verbindungen ohne Ordner).</summary>
    public async Task<bool> MoveAsync(Guid userId, Guid connection, int delta, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var group = new List<Guid>();
        await using (var cmd = conn.Cmd(tx, "select id from connections where user_id = $1 and folder_id is not distinct from (select folder_id from connections where user_id = $1 and id = $2) order by sort, created_at, id for update", userId, connection))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) group.Add(r.GetGuid(0));
        var at = group.IndexOf(connection);
        if (at < 0) return false;
        var to = Math.Clamp(at + Math.Sign(delta), 0, group.Count - 1);
        if (to != at) { group.RemoveAt(at); group.Insert(to, connection); }
        await using (var set = conn.Cmd(tx, "update connections c set sort = o.ord - 1 from unnest($2::uuid[]) with ordinality as o(id, ord) where c.id = o.id and c.user_id = $1", userId, group.ToArray()))
            await set.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Uebernimmt, was ein Geraet bisher nur lokal gemerkt hat (Favoriten, Zaehler, letzte Benutzung). Nur eigene Verbindungen, nie weniger als schon auf dem Server:
    /// mehrfaches Aufrufen oder mehrere Geraete richten keinen Schaden an.
    /// </summary>
    public async Task ImportLocalAsync(Guid userId, IReadOnlyCollection<Guid> favorites, IReadOnlyDictionary<Guid, int> uses, IReadOnlyDictionary<Guid, DateTimeOffset> last, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (favorites.Count > 0)
            await using (var fav = conn.Cmd(tx, "update connections set favorite = true where user_id = $1 and id = any($2::uuid[])", userId, favorites.ToArray())) await fav.ExecuteNonQueryAsync(ct);
        if (uses.Count > 0)
            await using (var use = conn.Cmd(tx, "update connections c set use_count = greatest(c.use_count, u.n) from unnest($2::uuid[], $3::int[]) as u(id, n) where c.user_id = $1 and c.id = u.id", userId, uses.Keys.ToArray(), uses.Values.ToArray())) await use.ExecuteNonQueryAsync(ct);
        if (last.Count > 0)
            await using (var seen = conn.Cmd(tx, "update connections c set last_used_at = greatest(coalesce(c.last_used_at, 'epoch'::timestamptz), u.t) from unnest($2::uuid[], $3::timestamptz[]) as u(id, t) where c.user_id = $1 and c.id = u.id", userId, last.Keys.ToArray(), last.Values.ToArray())) await seen.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
