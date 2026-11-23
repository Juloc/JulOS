using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record OidcApp(Guid Id, string Name, string ClientId, string SecretHash, string[] RedirectUris, string[] PostLogoutUris, string? BackchannelUri, string[] Roles, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset SecretChangedAt);

public sealed record OidcPerson(Guid Id, string Name, Guid Sub, bool Owner);

public sealed record OidcAssignment(Guid AppId, Guid UserId, string Role);

/// <summary>
/// Die registrierten Apps der Anmelde-Zentrale und die Rollen der Personen (Tabellen oidc_apps, oidc_roles). Vom Geheimnis steht nur der Hash in der Datenbank.
/// Eigentuemer ist das aelteste bestaetigte Konto; er hat in jeder App die Rolle admin, ohne dass ein Eintrag noetig ist.
/// </summary>
public sealed class OidcAppStore(NpgsqlDataSource db, TimeProvider clock)
{
    public const string OwnerRole = "admin";
    private const string Columns = "id, name, client_id, secret_hash, redirect_uris, post_logout_uris, backchannel_uri, roles, enabled, created_at, secret_changed_at";

    private static OidcApp Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetFieldValue<string[]>(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<string[]>(7), r.GetBoolean(8), r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10));

    public async Task<IReadOnlyList<OidcApp>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from oidc_apps order by lower(name)");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<OidcApp>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<OidcApp?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd($"select {Columns} from oidc_apps where id = $1", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<OidcApp?> FindByClientIdAsync(string? clientId, CancellationToken ct = default)
    {
        if (!OidcRules.LooksLikeClientId(clientId)) return null;
        await using var cmd = db.Cmd($"select {Columns} from oidc_apps where client_id = $1", clientId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Legt eine App an. Gibt sie und das Geheimnis im Klartext (nur jetzt); null, wenn der Name schon vergeben ist.</summary>
    public async Task<(OidcApp App, string Secret)?> CreateAsync(string name, string[] redirectUris, string[] postLogoutUris, string? backchannelUri, string[] roles, CancellationToken ct = default)
    {
        var secret = OidcRules.NewSecret();
        var clientId = OidcRules.NewClientId();
        try
        {
            await using var cmd = db.Cmd($"insert into oidc_apps (name, client_id, secret_hash, redirect_uris, post_logout_uris, backchannel_uri, roles, created_at, secret_changed_at) values ($1, $2, $3, $4, $5, $6, $7, $8, $8) returning {Columns}",
                name.Trim(), clientId, OidcRules.Hash(secret), redirectUris, postLogoutUris, backchannelUri, roles, clock.GetUtcNow());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (Read(r), secret);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return null; }
    }

    /// <summary>Aendert Name, Adressen und Rollen. Eine Rolle, die es nicht mehr gibt, verliert ihre Zuweisungen (Rueckgabe: wie viele). null: App fehlt oder Name vergeben.</summary>
    public async Task<(OidcApp App, IReadOnlyList<Guid> Dropped)?> UpdateAsync(Guid id, string name, string[] redirectUris, string[] postLogoutUris, string? backchannelUri, string[] roles, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            OidcApp app;
            await using (var cmd = conn.Cmd(tx, $"update oidc_apps set name = $2, redirect_uris = $3, post_logout_uris = $4, backchannel_uri = $5, roles = $6 where id = $1 returning {Columns}",
                id, name.Trim(), redirectUris, postLogoutUris, backchannelUri, roles))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return null;
                app = Read(r);
            }
            var dropped = new List<Guid>();
            await using (var del = conn.Cmd(tx, "delete from oidc_roles where app_id = $1 and not (role = any($2)) returning user_id", id, roles))
            await using (var r = await del.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) dropped.Add(r.GetGuid(0));
            await tx.CommitAsync(ct);
            return (app, dropped);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return null; }
    }

    /// <summary>Ein neues Geheimnis (das alte gilt sofort nicht mehr). Gibt es im Klartext, nur jetzt; null, wenn die App fehlt.</summary>
    public async Task<string?> NewSecretAsync(Guid id, CancellationToken ct = default)
    {
        var secret = OidcRules.NewSecret();
        await using var cmd = db.Cmd("update oidc_apps set secret_hash = $2, secret_changed_at = $3 where id = $1", id, OidcRules.Hash(secret), clock.GetUtcNow());
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? secret : null;
    }

    public async Task<bool> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("update oidc_apps set enabled = $2 where id = $1", id, enabled);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from oidc_apps where id = $1", id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ---- Personen und Rollen ----

    /// <summary>Alle bestaetigten Konten; der Eigentuemer (das aelteste) ist markiert.</summary>
    public async Task<IReadOnlyList<OidcPerson>> PeopleAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id, name, oidc_sub from users where totp_confirmed order by created_at, id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<OidcPerson>();
        while (await r.ReadAsync(ct)) list.Add(new OidcPerson(r.GetGuid(0), r.GetString(1), r.GetGuid(2), list.Count == 0));
        return list;
    }

    public async Task<Guid?> OwnerIdAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select id from users where totp_confirmed order by created_at, id limit 1");
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<IReadOnlyList<OidcAssignment>> AssignmentsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select app_id, user_id, role from oidc_roles");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<OidcAssignment>();
        while (await r.ReadAsync(ct)) list.Add(new OidcAssignment(r.GetGuid(0), r.GetGuid(1), r.GetString(2)));
        return list;
    }

    /// <summary>
    /// Die Rolle einer Person in einer App, oder null (kein Zugang). Der Eigentuemer hat immer admin. Eine Rolle, die die App nicht (mehr) kennt, zaehlt nicht.
    /// </summary>
    public async Task<string?> RoleOfAsync(OidcApp app, Guid userId, CancellationToken ct = default)
    {
        if (await OwnerIdAsync(ct) == userId) return OwnerRole;
        await using var cmd = db.Cmd("select role from oidc_roles where app_id = $1 and user_id = $2", app.Id, userId);
        return await cmd.ExecuteScalarAsync(ct) is string role && app.Roles.Contains(role) ? role : null;
    }

    /// <summary>Setzt die Rolle einer Person (null: entfernt sie, kein Zugang). Gibt zurueck, ob sich etwas geaendert hat.</summary>
    public async Task<bool> SetRoleAsync(Guid appId, Guid userId, string? role, CancellationToken ct = default)
    {
        if (role is null)
        {
            await using var del = db.Cmd("delete from oidc_roles where app_id = $1 and user_id = $2", appId, userId);
            return await del.ExecuteNonQueryAsync(ct) == 1;
        }
        await using var cmd = db.Cmd("insert into oidc_roles (app_id, user_id, role) values ($1, $2, $3) on conflict (app_id, user_id) do update set role = excluded.role where oidc_roles.role <> excluded.role", appId, userId, role);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
