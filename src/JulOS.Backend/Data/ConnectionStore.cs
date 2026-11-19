using Npgsql;

namespace JulOS.Backend.Data;

public sealed record ConnectionRecord(Guid Id, string Name, string Kind, string Host, int Port, string Username, bool HasPassword, Guid HostId, string? HostKey = null, string? CertFingerprint = null, bool DriveShare = false, bool DriveWrite = false, string Domain = "", string Notes = "", string KeyboardLayout = "de");

/// <summary>Gespeicherte Verbindungen eines Benutzers. Ein Passwort gibt es nur auf Wunsch und nur verschluesselt (siehe SecretBox); die Listen liefern nie das Passwort, nur ob eins da ist.</summary>
public sealed class ConnectionStore(NpgsqlDataSource db)
{
    private readonly HostStore _hosts = new(db);   // ohne Zustand: nur fuer den Standard-Host einer ersten Verbindung

    private const string Columns = "id, name, kind, host, port, username, secret is not null, host_id, host_key, cert_fingerprint, drive_share, drive_write, domain, notes, keyboard_layout";

    private static ConnectionRecord Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetBoolean(6), r.GetGuid(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetBoolean(10), r.GetBoolean(11), r.GetString(12), r.GetString(13), r.GetString(14));

    public async Task<ConnectionRecord> CreateAsync(Guid userId, string name, string kind, string host, int port, string username, Guid? hostId = null, CancellationToken ct = default, bool driveShare = false, bool driveWrite = false, string domain = "", string notes = "", string layout = "de")
    {
        // Ohne Angabe kommt die Verbindung auf den ersten Host des Benutzers (gibt es keinen, wird er angelegt)
        hostId ??= await _hosts.EnsureDefaultAsync(userId, ct);
        // Die Spalte engine ist ein Rest aus der Zeit mit zwei Engines (alte Zeilen behalten ihren Wert, er wird nicht mehr gelesen); neue Zeilen bekommen den Wert der einzigen Engine
        // Der Host muss dem Benutzer gehoeren (die Endpunkte pruefen das vorher und melden 404; hier gilt es noch einmal)
        await using var cmd = db.Cmd(
            $"insert into connections (user_id, name, kind, host, port, username, engine, host_id, drive_share, drive_write, domain, notes, keyboard_layout, sort) select $1, $2, $3, $4, $5, $6, 'stream', $7, $8, $9, $10, $11, $12, coalesce((select max(sort) + 1 from connections where user_id = $1), 0) " +
            $"where exists (select 1 from hosts where id = $7 and user_id = $1) returning {Columns}",
            userId, name, kind, host, port, username, hostId, driveShare, driveWrite && driveShare, domain, notes, layout);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : throw new InvalidOperationException("Der Host gehört nicht diesem Benutzer.");
    }

    public async Task<ConnectionRecord?> UpdateAsync(Guid userId, Guid id, string name, string kind, string host, int port, string username, Guid? hostId = null, CancellationToken ct = default, bool driveShare = false, bool driveWrite = false, string domain = "", string notes = "", string layout = "de")
    {
        // Ohne Angabe bleibt der bisherige Host; der Host muss dem Benutzer gehoeren (sonst wird nichts geaendert)
        await using var cmd = db.Cmd($"update connections set name = $3, kind = $4, host = $5, port = $6, username = $7, host_id = coalesce($8, host_id), drive_share = $9, drive_write = $10, domain = $11, notes = $12, keyboard_layout = $13 where user_id = $1 and id = $2 " +
            $"and ($8::uuid is null or exists (select 1 from hosts where id = $8 and user_id = $1)) returning {Columns}",
            userId, id, name, kind, host, port, username, hostId, driveShare, driveWrite && driveShare, domain, notes, layout);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from connections where user_id = $1 and id = $2", userId, id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> CountAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select count(*) from connections where user_id = $1", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ConnectionRecord?> GetAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from connections where user_id = $1 and id = $2", userId, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<ConnectionRecord>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from connections where user_id = $1 order by sort, created_at", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ConnectionRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    /// <summary>Setzt das verschluesselte Passwort, oder entfernt es (null).</summary>
    public async Task SetSecretAsync(Guid userId, Guid id, byte[]? secret, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update connections set secret = $3 where user_id = $1 and id = $2", userId, id, secret is null ? DBNull.Value : secret);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]?> GetSecretAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select secret from connections where user_id = $1 and id = $2", userId, id);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    /// <summary>Merkt den Host-Schluessel des Ziels (Fingerabdruck), oder vergisst ihn (null).</summary>
    /// <summary>Merkt den Fingerabdruck des RDP-Zertifikats dieser Verbindung (null: vergessen).</summary>
    public async Task SetCertFingerprintAsync(Guid userId, Guid id, string? fingerprint, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update connections set cert_fingerprint = $3 where user_id = $1 and id = $2", userId, id, fingerprint is null ? DBNull.Value : fingerprint);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetHostKeyAsync(Guid userId, Guid id, string? hostKey, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update connections set host_key = $3 where user_id = $1 and id = $2", userId, id, hostKey is null ? DBNull.Value : hostKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
