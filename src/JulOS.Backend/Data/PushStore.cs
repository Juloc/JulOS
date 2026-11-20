using System.Text.Json;
using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record PushSubscription(Guid Id, Guid UserId, string Endpoint, byte[] P256dh, byte[] Auth, string Label, string Origin, DateTimeOffset CreatedAt, DateTimeOffset? LastOk);
public sealed record AlertLogRow(long Id, DateTimeOffset At, Guid? HostId, string HostName, string Rule, string Kind, string? Detail, string Push);

public enum SubscribeResult { Created, Updated, Taken, TooMany }

/// <summary>
/// Der Speicher fuer Push-Meldungen (docs/PUSH.md): Schluessel des Servers (VAPID), Abos der Geraete, Einstellungen der Regeln, Zustand je Host und die letzten Meldungen.
/// Jede Abfrage nennt den Benutzer; ein fremdes Abo ist nicht zu sehen und nicht zu loeschen.
/// </summary>
public sealed class PushStore(NpgsqlDataSource db, SecretBox box)
{
    public const int MaxSubscriptionsPerUser = 10, KeepLog = 200, ShowLog = 50;
    private static readonly Guid VapidAad = new("6a756c6f-7300-4000-8000-707573680001");      // zugehoerige Daten des privaten Schluessels: er laesst sich nicht an anderer Stelle einsetzen

    public bool Available => box.Enabled;

    // ---------- VAPID ----------

    /// <summary>Das Schluesselpaar des Servers; beim ersten Aufruf wird es erzeugt. Der private Teil kommt nie in eine Antwort oder ein Log. Null, wenn der Server keinen Schluessel zum Verschluesseln hat.</summary>
    public async Task<(byte[] Private, byte[] Public)?> VapidAsync(CancellationToken ct = default)
    {
        if (!box.Enabled) return null;
        if (await ReadVapidAsync(ct) is { } have) return have;
        var (priv, pub) = WebPush.NewVapidKey();
        await using (var ins = db.Cmd("insert into push_vapid (public_key, private_sealed) values ($1, $2) on conflict (id) do nothing", pub, box.Encrypt(Convert.ToBase64String(priv), VapidAad)))
            await ins.ExecuteNonQueryAsync(ct);
        return await ReadVapidAsync(ct);       // bei zwei gleichzeitigen Aufrufen gilt das Paar, das zuerst gespeichert wurde
    }

    private async Task<(byte[], byte[])?> ReadVapidAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("select public_key, private_sealed from push_vapid");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var plain = box.Decrypt(r.GetFieldValue<byte[]>(1), VapidAad);
        return plain is null ? null : (Convert.FromBase64String(plain), r.GetFieldValue<byte[]>(0));
    }

    // ---------- Abos ----------

    private const string SubColumns = "id, user_id, endpoint, p256dh, auth, label, origin, created_at, last_ok";

    private static PushSubscription ReadSub(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<byte[]>(3), r.GetFieldValue<byte[]>(4), r.GetString(5), r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8));

    public async Task<IReadOnlyList<PushSubscription>> SubscriptionsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {SubColumns} from push_subscriptions where user_id = $1 order by created_at, id", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PushSubscription>();
        while (await r.ReadAsync(ct)) list.Add(ReadSub(r));
        return list;
    }

    /// <summary>Legt das Abo eines Geraets an. Derselbe Endpunkt (dasselbe Geraet) aktualisiert sich; ein Endpunkt, der einem anderen Benutzer gehoert, wird abgelehnt.</summary>
    public async Task<(SubscribeResult Result, Guid Id)> SubscribeAsync(Guid userId, string endpoint, byte[] p256dh, byte[] auth, string label, string origin, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lk = conn.Cmd(tx, "select pg_advisory_xact_lock(hashtext($1))", "push:" + userId)) await lk.ExecuteNonQueryAsync(ct);
        await using (var one = conn.Cmd(tx, "select id, user_id from push_subscriptions where endpoint = $1", endpoint))
        await using (var r = await one.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
            {
                var (id, owner) = (r.GetGuid(0), r.GetGuid(1));
                await r.CloseAsync();
                if (owner != userId) return (SubscribeResult.Taken, Guid.Empty);
                await using var upd = conn.Cmd(tx, "update push_subscriptions set p256dh = $2, auth = $3, label = $4, origin = $5 where id = $1", id, p256dh, auth, label, origin);
                await upd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                return (SubscribeResult.Updated, id);
            }
        await using (var count = conn.Cmd(tx, "select count(*) from push_subscriptions where user_id = $1", userId))
            if ((long)(await count.ExecuteScalarAsync(ct))! >= MaxSubscriptionsPerUser) return (SubscribeResult.TooMany, Guid.Empty);
        await using var ins = conn.Cmd(tx, "insert into push_subscriptions (user_id, endpoint, p256dh, auth, label, origin) values ($1, $2, $3, $4, $5, $6) returning id", userId, endpoint, p256dh, auth, label, origin);
        var created = (Guid)(await ins.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return (SubscribeResult.Created, created);
    }

    public async Task<bool> UnsubscribeAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from push_subscriptions where id = $1 and user_id = $2", id, userId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Ein toter Endpunkt (der Push-Dienst sagt 404 oder 410) wird entfernt.</summary>
    public async Task RemoveDeadAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from push_subscriptions where id = $1", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkOkAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update push_subscriptions set last_ok = now() where id = $1", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- Einstellungen und Zustand ----------

    public async Task<AlertSettings> SettingsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select settings::text from alert_settings where user_id = $1", userId);
        return await cmd.ExecuteScalarAsync(ct) is string json ? (JsonSerializer.Deserialize(json, AlertJson.Default.AlertSettings) ?? new AlertSettings()).Clamped() : new AlertSettings();
    }

    public async Task SaveSettingsAsync(Guid userId, AlertSettings settings, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into alert_settings (user_id, settings) values ($1, $2::text::jsonb) on conflict (user_id) do update set settings = excluded.settings, updated_at = now()",
            userId, JsonSerializer.Serialize(settings.Clamped(), AlertJson.Default.AlertSettings));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Die Benutzer, fuer die ausgewertet wird: wer ein Geraet eingeschaltet oder Einstellungen gespeichert hat.</summary>
    public async Task<IReadOnlyList<Guid>> AlertUsersAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select user_id from push_subscriptions union select user_id from alert_settings");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Guid>();
        while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }

    public async Task<HostAlertState> StateAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select state::text from alert_state where user_id = $1 and host_id = $2", userId, hostId);
        return await cmd.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize(json, AlertJson.Default.HostAlertState) ?? new HostAlertState() : new HostAlertState();
    }

    public async Task SaveStateAsync(Guid userId, Guid hostId, HostAlertState state, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into alert_state (user_id, host_id, state) values ($1, $2, $3::text::jsonb) on conflict (user_id, host_id) do update set state = excluded.state",
            userId, hostId, JsonSerializer.Serialize(state, AlertJson.Default.HostAlertState));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- Meldungen ----------

    public async Task<long> AddLogAsync(Guid userId, DateTimeOffset at, Guid? hostId, string hostName, string rule, string kind, string? detail, string push, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("insert into alert_log (user_id, at, host_id, host_name, rule, kind, detail, push) values ($1, $2, $3, $4, $5, $6, $7, $8) returning id",
            userId, at, hostId, hostName, rule, kind, detail, push);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<AlertLogRow>> LogAsync(Guid userId, string? push = null, int limit = ShowLog, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id, at, host_id, host_name, rule, kind, detail, push from alert_log where user_id = $1 and ($2::text is null or push = $2) order by id desc limit $3", userId, push, limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AlertLogRow>();
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7)));
        return list;
    }

    public async Task SetPushAsync(Guid userId, IEnumerable<long> ids, string push, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update alert_log set push = $3 where user_id = $1 and id = any($2)", userId, ids.ToArray(), push);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Was zu lange wartet (nicht zugestellt, aelter als <paramref name="maxAge"/>) wird nicht mehr nachgeschickt; und die Liste bleibt kurz.</summary>
    public async Task TidyAsync(Guid userId, DateTimeOffset now, TimeSpan maxAge, CancellationToken ct = default)
    {
        await using (var old = db.Cmd("update alert_log set push = 'dropped' where user_id = $1 and push = 'pending' and at < $2", userId, now - maxAge)) await old.ExecuteNonQueryAsync(ct);
        await using var trim = db.Cmd("delete from alert_log where user_id = $1 and id < coalesce((select id from alert_log where user_id = $1 order by id desc offset $2 limit 1), 0)", userId, KeepLog);
        await trim.ExecuteNonQueryAsync(ct);
    }
}
