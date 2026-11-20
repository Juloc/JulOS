using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>Das Ergebnis von <see cref="HostLoginStore.SetAsync"/>: gab es den Host, war es das erste Anlegen, und wie viele Verbindungen ihren eigenen Benutzer behalten haben.</summary>
public sealed record LoginSetResult(bool Found, bool Created, int Pinned);

/// <summary>
/// Der Login eines Hosts (Benutzer und verschluesseltes Passwort, Migration 020, docs/HOSTS.md). Jede Abfrage nennt den Benutzer: ein fremder Host ist "nicht gefunden".
/// Das Passwort kommt nur als verschluesselter Wert hinein und heraus (SecretBox, die Host-Kennung steckt als zugehoerige Daten darin); diese Klasse sieht nie Klartext.
/// </summary>
public sealed class HostLoginStore(NpgsqlDataSource db)
{
    private static HostLoginInfo Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), !r.IsDBNull(2) && r.GetBoolean(2), r.IsDBNull(3) ? [] : [.. r.GetFieldValue<string[]>(3)]);

    private const string Columns = "id, login_user, login_secret is not null, login_targets";

    /// <summary>Der Login des Hosts (User ist null, wenn er keinen hat); null, wenn der Host nicht diesem Benutzer gehoert.</summary>
    public async Task<HostLoginInfo?> GetAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from hosts where user_id = $1 and id = $2", userId, hostId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Die Logins aller Hosts des Benutzers (auch die ohne Login, mit User null), nach Host.</summary>
    public async Task<IReadOnlyDictionary<Guid, HostLoginInfo>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from hosts where user_id = $1", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var map = new Dictionary<Guid, HostLoginInfo>();
        while (await r.ReadAsync(ct)) { var h = Read(r); map[h.HostId] = h; }
        return map;
    }

    /// <summary>Das verschluesselte Passwort des Host-Logins (null: keins).</summary>
    public async Task<byte[]?> GetSecretAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select login_secret from hosts where user_id = $1 and id = $2", userId, hostId);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    /// <summary>Die Adressen der Verbindungen des Hosts, klein geschrieben, ohne Doppelte.</summary>
    public async Task<string[]> ConnectionAddressesAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select distinct lower(btrim(host)) from connections where user_id = $1 and host_id = $2 order by 1", userId, hostId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return [.. list];
    }

    /// <summary>
    /// Setzt oder aendert den Login. secret: ein neues, schon verschluesseltes Passwort; die Passwort-Adressen sind dann die der Verbindungen des Hosts (oder <paramref name="onlyTargets"/>).
    /// Ohne neues Passwort bleibt das alte, ausser clearPassword ist gesetzt oder der Benutzer wird ein anderer (ein Passwort gehoert zu seinem Benutzer, wie bei einer Verbindung).
    /// Beim ersten Anlegen behalten Verbindungen mit einem anderen gespeicherten Benutzer ihren eigenen Login (own_login), damit nichts, was heute geht, nebenbei aufhoert zu gehen.
    /// </summary>
    public async Task<LoginSetResult> SetAsync(Guid userId, Guid hostId, string user, byte[]? secret, bool clearPassword, IReadOnlyList<string>? onlyTargets = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string? before;
        await using (var cur = conn.Cmd(tx, "select login_user from hosts where user_id = $1 and id = $2 for update", userId, hostId))
        await using (var r = await cur.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return new LoginSetResult(false, false, 0);
            before = r.IsDBNull(0) ? null : r.GetString(0);
        }
        var created = before is null;
        if (secret is not null)
        {
            string[] targets;
            if (onlyTargets is not null) targets = [.. onlyTargets];
            else
            {
                await using var addr = conn.Cmd(tx, "select coalesce(array_agg(distinct lower(btrim(host))), '{}') from connections where user_id = $1 and host_id = $2", userId, hostId);
                targets = (string[])(await addr.ExecuteScalarAsync(ct))!;
            }
            await using var set = conn.Cmd(tx, "update hosts set login_user = $3, login_secret = $4, login_targets = $5 where user_id = $1 and id = $2", userId, hostId, user, secret, targets);
            await set.ExecuteNonQueryAsync(ct);
        }
        else if (clearPassword || (before is not null && !string.Equals(before, user, StringComparison.Ordinal)))
        {
            await using var set = conn.Cmd(tx, "update hosts set login_user = $3, login_secret = null, login_targets = '{}' where user_id = $1 and id = $2", userId, hostId, user);
            await set.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var set = conn.Cmd(tx, "update hosts set login_user = $3 where user_id = $1 and id = $2", userId, hostId, user);
            await set.ExecuteNonQueryAsync(ct);
        }
        var pinned = 0;
        if (created)
        {
            await using var pin = conn.Cmd(tx, "update connections set own_login = true where user_id = $1 and host_id = $2 and not own_login and username <> '' and username <> $3", userId, hostId, user);
            pinned = await pin.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new LoginSetResult(true, created, pinned);
    }

    /// <summary>Speichert nur das Passwort zu dem Benutzer, der schon da ist (zum Beispiel aus der Dateien-App, nachdem die Anmeldung geklappt hat); false, wenn es keinen Login gibt oder er nicht diesem Benutzer gehoert.</summary>
    public async Task<bool> SetPasswordAsync(Guid userId, Guid hostId, string expectedUser, byte[] secret, IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update hosts set login_secret = $4, login_targets = $5 where user_id = $1 and id = $2 and login_user = $3", userId, hostId, expectedUser, secret, targets.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Entfernt Benutzer und Passwort des Hosts. Die Verbindungen behalten ihre eigenen Zugangsdaten (nichts davon wurde je geloescht). false, wenn der Host nicht ihm gehoert.</summary>
    public async Task<bool> ClearAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update hosts set login_user = null, login_secret = null, login_targets = '{}' where user_id = $1 and id = $2", userId, hostId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
