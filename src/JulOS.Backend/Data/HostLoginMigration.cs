using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>
/// Uebernimmt beim Start die bisherigen Zugangsdaten der Verbindungen in einen Login je Host (docs/HOSTS.md, "Migration"), ohne etwas zu loeschen:
/// die Passwoerter der Verbindungen bleiben, wie sie sind (auch ihre Verschluesselung); der Host-Login bekommt nur eine Kopie, neu verschluesselt mit der Host-Kennung.
/// Pro Host, der noch nicht geprueft ist (hosts.login_checked):
///   - haben alle Verbindungen mit gespeichertem Passwort (und Benutzernamen) denselben Benutzer, wird er der Host-Login, mit dem Passwort der ersten Verbindung;
///     Verbindungen mit demselben Benutzer und Passwort zeigen danach "nutzt den Host-Login", jede andere behaelt ihren eigenen Login (own_login), damit ihr Zugang derselbe bleibt;
///   - sonst (kein gespeichertes Passwort, oder mehrere Benutzer) bleibt alles, wie es ist; die Host-Seite schlaegt einen Login vor, gesetzt wird er nicht von allein.
/// Laesst sich ein gespeichertes Passwort nicht entschluesseln (falscher oder fehlender Schluessel), bleibt der Host ungeprueft und wird beim naechsten Start wieder versucht.
/// Idempotent: ein gepruefter Host wird nie wieder angefasst, und ein Host mit schon gesetztem Login auch dann nicht.
/// </summary>
public sealed class HostLoginMigration(NpgsqlDataSource db, SecretBox box)
{
    private sealed record Conn(Guid Id, string Username, string Host, byte[]? Secret);

    /// <returns>Wie viele Hosts einen Login bekommen haben.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var hosts = new List<Guid>();
        await using (var cmd = db.Cmd("select id from hosts where not login_checked order by created_at, id"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) hosts.Add(r.GetGuid(0));
        var created = 0;
        foreach (var id in hosts) if (await MigrateHostAsync(id, ct)) created++;
        return created;
    }

    private async Task<bool> MigrateHostAsync(Guid hostId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string? existing;
        await using (var lk = conn.Cmd(tx, "select login_user from hosts where id = $1 and not login_checked for update", hostId))
        await using (var r = await lk.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return false;                          // ein anderer Start war schneller (oder der Host ist weg)
            existing = r.IsDBNull(0) ? null : r.GetString(0);
        }
        var conns = new List<Conn>();
        await using (var list = conn.Cmd(tx, "select id, username, host, secret from connections where host_id = $1 order by sort, created_at, id", hostId))
        await using (var r = await list.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) conns.Add(new Conn(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : (byte[])r["secret"]));

        // Zuerst alle gespeicherten Passwoerter lesen: ein unlesbares heisst, der Schluessel passt (noch) nicht, dann wird nichts angenommen
        var plain = new Dictionary<Guid, string>();
        var unreadable = false;
        foreach (var c in conns.Where(c => c.Secret is not null))
        {
            var p = box.Decrypt(c.Secret!, c.Id);
            if (p is null) unreadable = true; else plain[c.Id] = p;
        }
        if (unreadable) return false;

        var stored = conns.Where(c => plain.ContainsKey(c.Id) && c.Username != "").ToList();
        var users = stored.Select(c => c.Username).Distinct(StringComparer.Ordinal).ToList();
        var made = false;
        if (existing is null && users.Count == 1)
        {
            var user = users[0];
            var password = plain[stored[0].Id];
            var same = stored.Where(c => plain[c.Id] == password).ToList();
            var targets = same.Select(c => LoginResolver.Address(c.Host)).Distinct().Order(StringComparer.Ordinal).ToArray();
            await using (var set = conn.Cmd(tx, "update hosts set login_user = $2, login_secret = $3, login_targets = $4 where id = $1", hostId, user, box.Encrypt(password, hostId), targets))
                await set.ExecuteNonQueryAsync(ct);
            // Wer denselben Benutzer und dasselbe Passwort hat, folgt dem Host-Login; alle anderen behalten ihren Zugang genau wie vorher
            foreach (var c in conns)
            {
                var follows = c.Username == "" ? !plain.ContainsKey(c.Id) : plain.ContainsKey(c.Id) ? same.Contains(c) : c.Username == user;
                await using var pin = conn.Cmd(tx, "update connections set own_login = $2 where id = $1", c.Id, !follows);
                await pin.ExecuteNonQueryAsync(ct);
            }
            made = true;
        }
        await using (var done = conn.Cmd(tx, "update hosts set login_checked = true where id = $1", hostId)) await done.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return made;
    }
}
