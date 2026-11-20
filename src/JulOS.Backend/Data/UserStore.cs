using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record UserRow(Guid Id, string Name, string PasswordHash, string TotpSecret, bool TotpConfirmed, long TotpLastStep);

public sealed record UserSummary(string Name, DateTimeOffset CreatedAt, int RecoveryLeft, int Sessions);

/// <summary>
/// Konten. Das TOTP-Geheimnis liegt verschluesselt in der Datenbank (F-10): <c>enc1:</c> und Base64 von AES-256-GCM mit der Benutzer-Kennung als zugehoerigen Daten (SecretBox, der Schluessel
/// ist nicht in der Datenbank). Gelesen werden beide Formen, bis <see cref="MigrateTotpAsync"/> alle umgestellt hat; ohne Schluessel bleibt es Klartext. Ein Auszug der Datenbank allein
/// zeigt so nur Argon2-Hashe und verschluesselte Werte.
/// </summary>
public sealed class UserStore(NpgsqlDataSource db, SecretBox? box = null)
{
    private const string Columns = "id, name, password_hash, totp_secret, totp_confirmed, totp_last_step";
    public const string SealedPrefix = "enc1:";

    private string Seal(Guid id, string secret) => box is { Enabled: true } ? SealedPrefix + Convert.ToBase64String(box.Encrypt(secret, id)) : secret;

    /// <summary>Das Geheimnis im Klartext. Laesst sich ein verschluesselter Wert nicht lesen (anderer Schluessel), kommt "" zurueck: die Anmeldung scheitert, der Weg zurueck ist tools/julos-user.sh.</summary>
    private string Open(Guid id, string stored)
    {
        if (!stored.StartsWith(SealedPrefix, StringComparison.Ordinal)) return stored;
        try { return box?.Decrypt(Convert.FromBase64String(stored[SealedPrefix.Length..]), id) ?? ""; }
        catch (FormatException) { return ""; }
    }

    private UserRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), Open(r.GetGuid(0), r.GetString(3)), r.GetBoolean(4), r.GetInt64(5));

    /// <summary>Verschluesselt alle noch unverschluesselten TOTP-Geheimnisse (beim Start). Gibt die Zahl der umgestellten zurueck; ohne Schluessel 0.</summary>
    public async Task<int> MigrateTotpAsync(CancellationToken ct = default)
    {
        if (box is not { Enabled: true }) return 0;
        var plain = new List<(Guid Id, string Secret)>();
        await using (var cmd = db.Cmd("select id, totp_secret from users where totp_secret not like $1", SealedPrefix + "%"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) plain.Add((r.GetGuid(0), r.GetString(1)));
        var changed = 0;
        foreach (var (id, secret) in plain)
        {
            // nur, wenn sich der Wert inzwischen nicht geaendert hat (ein neuer Authenticator waehrend des Starts bleibt unangetastet)
            await using var up = db.Cmd("update users set totp_secret = $3 where id = $1 and totp_secret = $2", id, secret, Seal(id, secret));
            changed += await up.ExecuteNonQueryAsync(ct);
        }
        return changed;
    }

    public async Task<bool> AnyConfirmedAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select exists (select 1 from users where totp_confirmed)");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<UserRow?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from users where lower(name) = lower($1)", name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<UserRow?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from users where id = $1", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Legt ein Konto samt Wiederherstellungscodes an (in einer Transaktion). Bei der Ersteinrichtung werden vorher unbestaetigte Konten entfernt.</summary>
    public async Task<UserRow> CreateAsync(string name, string passwordHash, string totpSecret, bool confirmed, IEnumerable<string> recoveryHashes, bool replacePending, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (replacePending)
        {
            await using var del = conn.Cmd(tx, "delete from users where not totp_confirmed");
            await del.ExecuteNonQueryAsync(ct);
        }
        var id = Guid.NewGuid();                                    // die Kennung gehoert zu den verschluesselten Daten, sie muss vor dem Einfuegen feststehen
        await using (var ins = conn.Cmd(tx, "insert into users (id, name, password_hash, totp_secret, totp_confirmed) values ($1, $2, $3, $4, $5)", id, name, passwordHash, Seal(id, totpSecret), confirmed))
            await ins.ExecuteNonQueryAsync(ct);
        await InsertRecoveryAsync(conn, tx, id, recoveryHashes, ct);
        await tx.CommitAsync(ct);
        return new UserRow(id, name, passwordHash, totpSecret, confirmed, 0);
    }

    private static async Task InsertRecoveryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, IEnumerable<string> hashes, CancellationToken ct)
    {
        foreach (var h in hashes)
        {
            await using var cmd = conn.Cmd(tx, "insert into recovery_codes (user_id, code_hash) values ($1, $2)", id, h);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Verbraucht einen Zeitschritt. Gelingt nur, wenn er neuer ist als der zuletzt benutzte (Schutz gegen Wiederverwenden, auch bei gleichzeitigen Anfragen).</summary>
    public async Task<bool> TryAdvanceTotpStepAsync(Guid id, long step, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update users set totp_last_step = $2 where id = $1 and totp_last_step < $2", id, step);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(Guid id, string hash, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from recovery_codes where user_id = $1 and code_hash = $2", id, hash);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> RecoveryLeftAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select count(*) from recovery_codes where user_id = $1", id);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ConfirmAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update users set totp_confirmed = true where id = $1", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <param name="keepSession">Bei <paramref name="endSessions"/>: diese Sitzung (Id) bleibt bestehen, zum Beispiel die, in der das Passwort geaendert wird.</param>
    public async Task SetPasswordAsync(Guid id, string passwordHash, bool endSessions, CancellationToken ct = default, Guid? keepSession = null)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.Cmd(tx, "update users set password_hash = $2 where id = $1", id, passwordHash)) await cmd.ExecuteNonQueryAsync(ct);
        if (endSessions) await using (var cmd = conn.Cmd(tx, "delete from sessions where user_id = $1 and ($2::uuid is null or id <> $2)", id, keepSession)) await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Neuer TOTP-Schluessel und neue Wiederherstellungscodes; beendet alle Sitzungen (ausser <paramref name="keepSession"/>).</summary>
    /// <param name="lastStep">Der Zeitschritt, mit dem der neue Schluessel bestaetigt wurde (0 bei der Konsole): derselbe Code geht danach nicht noch einmal.</param>
    public async Task ReplaceTotpAsync(Guid id, string secret, IEnumerable<string> recoveryHashes, CancellationToken ct = default, Guid? keepSession = null, long lastStep = 0)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.Cmd(tx, "update users set totp_secret = $2, totp_last_step = $3, totp_confirmed = true where id = $1", id, Seal(id, secret), lastStep)) await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = conn.Cmd(tx, "delete from recovery_codes where user_id = $1", id)) await cmd.ExecuteNonQueryAsync(ct);
        await InsertRecoveryAsync(conn, tx, id, recoveryHashes, ct);
        await using (var cmd = conn.Cmd(tx, "delete from sessions where user_id = $1 and ($2::uuid is null or id <> $2)", id, keepSession)) await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from users where lower(name) = lower($1)", name);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("""
            select u.name, u.created_at,
                   (select count(*) from recovery_codes c where c.user_id = u.id),
                   (select count(*) from sessions s where s.user_id = u.id)
            from users u where u.totp_confirmed order by u.name
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<UserSummary>();
        while (await r.ReadAsync(ct)) list.Add(new UserSummary(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), (int)r.GetInt64(2), (int)r.GetInt64(3)));
        return list;
    }
}
