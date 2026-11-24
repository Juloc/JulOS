using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>Ein Passkey fuer die Liste in der App: nie der Schluessel, nie die Credential-Id.</summary>
public sealed record PasskeyRow(Guid Id, string Name, Guid? Aaguid, bool BackupEligible, bool BackedUp, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>Was die Pruefung eines Passkeys vom Speicher braucht.</summary>
public sealed record PasskeyKey(Guid Id, Guid UserId, byte[] CredentialId, byte[] PublicKey, long SignCount);

/// <summary>Eine verbrauchte Herausforderung: wofuer sie ausgegeben wurde und fuer wen.</summary>
public sealed record PasskeyChallengeRow(string Purpose, Guid? UserId, Guid? SessionId);

/// <summary>
/// Passkeys und ihre offenen Herausforderungen (Tabellen passkeys und passkey_challenges, database/027_passkeys.sql). Von der Herausforderung steht nur der SHA-256 in der Datenbank;
/// sie wird beim Pruefen geloescht und gilt so hoechstens einmal, auch bei zwei gleichzeitigen Anfragen.
/// </summary>
public sealed class PasskeyStore(NpgsqlDataSource db)
{
    public async Task<IReadOnlyList<PasskeyRow>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id, name, aaguid, backup_eligible, backed_up, created_at, last_used_at from passkeys where user_id = $1 order by created_at, id", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PasskeyRow>();
        while (await r.ReadAsync(ct))
            list.Add(new PasskeyRow(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetBoolean(3), r.GetBoolean(4), r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    public async Task<IReadOnlyList<byte[]>> CredentialIdsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select credential_id from passkeys where user_id = $1", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<byte[]>();
        while (await r.ReadAsync(ct)) list.Add((byte[])r[0]);
        return list;
    }

    public async Task<bool> CredentialExistsAsync(byte[] credentialId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select exists (select 1 from passkeys where credential_id = $1)", credentialId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Speichert einen neuen Passkey, sofern die Person noch unter dem Limit ist und die Credential-Id neu ist. false: abgelehnt.</summary>
    public async Task<PasskeyRow?> AddAsync(Guid userId, string name, PasskeyNew key, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Die Konto-Zeile sperren: zwei gleichzeitige Registrierungen zaehlen sonst beide "19" und legen den 21. an
        await using (var lck = conn.Cmd(tx, "select 1 from users where id = $1 for update", userId)) if (await lck.ExecuteScalarAsync(ct) is null) return null;
        await using (var count = conn.Cmd(tx, "select count(*) from passkeys where user_id = $1", userId))
            if ((long)(await count.ExecuteScalarAsync(ct))! >= PasskeyRules.MaxPerUser) return null;
        var id = Guid.NewGuid();
        await using var ins = conn.Cmd(tx,
            "insert into passkeys (id, user_id, credential_id, public_key, sign_count, name, aaguid, backup_eligible, backed_up, created_at) values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) on conflict (credential_id) do nothing",
            id, userId, key.CredentialId, key.PublicKey, (long)key.Counter, name, key.Aaguid == Guid.Empty ? null : key.Aaguid, key.BackupEligible, key.BackedUp, now);
        if (await ins.ExecuteNonQueryAsync(ct) != 1) return null;
        await tx.CommitAsync(ct);
        return new PasskeyRow(id, name, key.Aaguid == Guid.Empty ? null : key.Aaguid, key.BackupEligible, key.BackedUp, now, null);
    }

    public async Task<PasskeyKey?> FindByCredentialAsync(byte[] credentialId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id, user_id, credential_id, public_key, sign_count from passkeys where credential_id = $1", credentialId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PasskeyKey(r.GetGuid(0), r.GetGuid(1), (byte[])r[2], (byte[])r[3], r.GetInt64(4)) : null;
    }

    /// <summary>Merkt den neuen Zaehler und die Zeit. Nur, wenn der Zaehler noch der ist, mit dem geprueft wurde: zwei gleichzeitige Anmeldungen mit demselben Stand gelingen nicht beide (Wiederholung).</summary>
    public async Task<bool> TouchAsync(Guid id, long expectedCount, long newCount, bool backedUp, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update passkeys set sign_count = $3, backed_up = $4, last_used_at = $5 where id = $1 and sign_count = $2", id, expectedCount, newCount, backedUp, now);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RenameAsync(Guid userId, Guid id, string name, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update passkeys set name = $3 where id = $1 and user_id = $2", id, userId, name);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Loescht einen Passkey der Person; gibt seinen Namen (fuer den Verlauf) oder null, wenn es ihn nicht gibt oder er einer anderen Person gehoert.</summary>
    public async Task<string?> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from passkeys where id = $1 and user_id = $2 returning name", id, userId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ---- Herausforderungen ----

    /// <summary>
    /// Legt eine Herausforderung ab (nur ihr Hash). Abgelaufene werden dabei entfernt. Bei "login" verdraengt eine neue die aeltesten, wenn die Grenze je Adresse
    /// (<paramref name="ip"/>) oder insgesamt erreicht ist, sie wird nie abgelehnt.
    /// </summary>
    public async Task IssueChallengeAsync(byte[] hash, string purpose, Guid? userId, Guid? sessionId, string? ip, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var purge = conn.Cmd(null, "delete from passkey_challenges where expires_at < $1", now)) await purge.ExecuteNonQueryAsync(ct);
        if (purpose == "login")
        {
            // die neueste behalten: Platz fuer die neue schaffen (Grenze minus eins bleibt stehen)
            await using (var own = conn.Cmd(null, "delete from passkey_challenges where hash in (select hash from passkey_challenges where purpose = 'login' and ip is not distinct from $1 order by expires_at desc, hash offset $2)", ip, PasskeyRules.MaxOpenLoginChallengesPerAddress - 1))
                await own.ExecuteNonQueryAsync(ct);
            await using (var all = conn.Cmd(null, "delete from passkey_challenges where hash in (select hash from passkey_challenges where purpose = 'login' order by expires_at desc, hash offset $1)", PasskeyRules.MaxOpenLoginChallenges - 1))
                await all.ExecuteNonQueryAsync(ct);
        }
        await using var ins = conn.Cmd(null, "insert into passkey_challenges (hash, purpose, user_id, session_id, ip, expires_at) values ($1, $2, $3, $4, $5, $6)", hash, purpose, userId, sessionId, ip, now + PasskeyRules.ChallengeLifetime);
        await ins.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Verbraucht eine Herausforderung. null, wenn es sie nicht (mehr) gibt, sie schon benutzt oder abgelaufen ist; benutzt oder abgelaufen ist sie danach in jedem Fall weg.</summary>
    public async Task<PasskeyChallengeRow?> TakeChallengeAsync(byte[] hash, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from passkey_challenges where hash = $1 returning purpose, user_id, session_id, expires_at", hash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        if (r.GetFieldValue<DateTimeOffset>(3) < now) return null;
        return new PasskeyChallengeRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2));
    }
}
