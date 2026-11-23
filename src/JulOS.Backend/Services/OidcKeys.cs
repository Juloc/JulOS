using System.Security.Cryptography;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace JulOS.Backend.Services;

/// <summary>Ein oeffentlicher Schluessel fuer die JWKS-Ausgabe (Kurve P-256, Koordinaten als Base64Url).</summary>
public sealed record OidcPublicKey(string Kid, string X, string Y, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);

/// <summary>
/// Die Signaturschluessel der Anmelde-Zentrale (ES256, P-256). Beim ersten Bedarf wird einer erzeugt; der private Teil liegt nur verschluesselt in der Datenbank
/// (SecretBox: der Server-Schluessel ist nicht in der Datenbank) und nur beim aktiven Schluessel. Bei der Rotation wird der alte Schluessel "ausgemustert": sein privater Teil
/// wird geloescht, der oeffentliche bleibt im JWKS, bis seine Token abgelaufen sind (<see cref="Grace"/>), und in der Datenbank noch <see cref="Keep"/> (fuer id_token_hint).
/// Ohne Server-Schluessel (SecretBox aus) gibt es keine Schluessel und keine Anmelde-Zentrale.
/// </summary>
public sealed class OidcKeys(NpgsqlDataSource db, SecretBox box, TimeProvider clock)
{
    /// <summary>So lange steht ein ausgemusterter Schluessel noch im JWKS: Lebensdauer der Token plus Uhrenabweichung.</summary>
    public static readonly TimeSpan Grace = OidcRules.TokenLifetime + TimeSpan.FromMinutes(5);
    /// <summary>So lange bleibt er fuer die Pruefung eines id_token_hint in der Datenbank.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    private sealed record Row(Guid Id, string Kid, byte[] Public, byte[]? Sealed, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);
    private sealed record Loaded(DateTimeOffset At, ECDsaSecurityKey? Active, IReadOnlyList<(Row Row, ECDsaSecurityKey Key)> All, bool ActiveUnusable);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Loaded? _cache;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public bool Available => box.Enabled;

    /// <summary>Der Schluessel, mit dem jetzt signiert wird (wird beim ersten Mal erzeugt).</summary>
    public async Task<SecurityKey> ActiveAsync(CancellationToken ct = default)
    {
        var l = await LoadAsync(ct);
        if (l.Active is not null) return l.Active;
        await _gate.WaitAsync(ct);
        try
        {
            _cache = null;
            if (await LoadCoreAsync(ct) is { Active: null } now)
            {
                // Gibt es einen aktiven Schluessel, den dieser Server nicht oeffnen kann (anderer Server-Schluessel, zum Beispiel nach einer Wiederherstellung ohne ihn), wird er ausgemustert
                // und ein neuer erzeugt: die Apps sehen eine neue kid, alte Token verlieren ihre Gueltigkeit ohnehin nach Minuten.
                try { await InsertAsync(retireOld: now.ActiveUnusable, ct); }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { /* ein anderer Start war schneller */ }
            }
            _cache = null;
        }
        finally { _gate.Release(); }
        return (await LoadAsync(ct)).Active ?? throw new InvalidOperationException("Kein Signaturschlüssel.");
    }

    /// <summary>Die Schluessel fuer die JWKS-Ausgabe: der aktive und die ausgemusterten, solange ihre Token noch gelten koennten.</summary>
    public async Task<IReadOnlyList<OidcPublicKey>> PublicKeysAsync(CancellationToken ct = default)
    {
        await ActiveAsync(ct);
        var now = clock.GetUtcNow();
        return [.. (await LoadAsync(ct)).All.Where(k => k.Row.RetiredAt is null || k.Row.RetiredAt + Grace > now).Select(k =>
        {
            var p = k.Key.ECDsa.ExportParameters(false);
            return new OidcPublicKey(k.Row.Kid, OidcRules.Base64Url(p.Q.X!), OidcRules.Base64Url(p.Q.Y!), k.Row.CreatedAt, k.Row.RetiredAt);
        })];
    }

    /// <summary>Alle noch gespeicherten Schluessel (auch ausgemusterte) zum Pruefen eines id_token_hint.</summary>
    public async Task<IReadOnlyList<SecurityKey>> ValidationKeysAsync(CancellationToken ct = default) =>
        [.. (await LoadAsync(ct)).All.Select(k => (SecurityKey)k.Key)];

    /// <summary>Alle Schluessel mit Stand fuer die Anzeige in den Einstellungen (Kennung, erzeugt, ausgemustert), neueste zuerst.</summary>
    public async Task<IReadOnlyList<OidcPublicKey>> ListAsync(CancellationToken ct = default)
    {
        if (!box.Enabled) return [];
        return [.. (await LoadAsync(ct)).All.OrderByDescending(k => k.Row.CreatedAt).Select(k => new OidcPublicKey(k.Row.Kid, "", "", k.Row.CreatedAt, k.Row.RetiredAt))];
    }

    /// <summary>Ein neuer Schluessel wird der aktive; der bisherige bleibt fuer <see cref="Grace"/> im JWKS. Gibt die Kennung des neuen.</summary>
    public async Task<string> RotateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { var kid = await InsertAsync(retireOld: true, ct); _cache = null; return kid; }
        finally { _gate.Release(); }
    }

    /// <summary>Entfernt Schluessel, die laenger als <see cref="Keep"/> ausgemustert sind. Gibt die Zahl.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("delete from oidc_keys where retired_at is not null and retired_at < $1", clock.GetUtcNow() - Keep);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0) _cache = null;
        return n;
    }

    // ---- intern ----

    private async Task<Loaded> LoadAsync(CancellationToken ct)
    {
        if (_cache is { } c && clock.GetUtcNow() - c.At < CacheFor) return c;
        await _gate.WaitAsync(ct);
        try { return _cache is { } c2 && clock.GetUtcNow() - c2.At < CacheFor ? c2 : _cache = await LoadCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<Loaded> LoadCoreAsync(CancellationToken ct)
    {
        if (!box.Enabled) throw new InvalidOperationException("Ohne Server-Schlüssel (JULOS_SECRET_KEY oder secret.key) gibt es keine Anmelde-Zentrale.");
        var rows = new List<Row>();
        await using (var cmd = db.Cmd("select id, kid, public_key, private_sealed, created_at, retired_at from oidc_keys order by created_at desc"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add(new Row(r.GetGuid(0), r.GetString(1), r.GetFieldValue<byte[]>(2), r.IsDBNull(3) ? null : r.GetFieldValue<byte[]>(3), r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5)));

        var all = new List<(Row, ECDsaSecurityKey)>();
        ECDsaSecurityKey? active = null;
        var unusable = false;
        foreach (var row in rows)
        {
            var ec = ECDsa.Create();
            if (row.RetiredAt is null && row.Sealed is not null)
            {
                // der aktive Schluessel: mit dem privaten Teil (laesst er sich nicht entschluesseln, ist der Server-Schluessel ein anderer: dann gibt es hier keinen aktiven)
                if (box.Decrypt(row.Sealed, row.Id) is not { } pkcs8) { ec.Dispose(); unusable = true; continue; }
                ec.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8), out _);
                var key = new ECDsaSecurityKey(ec) { KeyId = row.Kid };
                active = key; all.Add((row, key));
            }
            else
            {
                ec.ImportSubjectPublicKeyInfo(row.Public, out _);
                all.Add((row, new ECDsaSecurityKey(ec) { KeyId = row.Kid }));
            }
        }
        return new Loaded(clock.GetUtcNow(), active, all, unusable);
    }

    private async Task<string> InsertAsync(bool retireOld, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var kid = OidcRules.Base64Url(RandomNumberGenerator.GetBytes(9));
        var pkcs8 = Convert.ToBase64String(ec.ExportPkcs8PrivateKey());
        var sealedKey = box.Encrypt(pkcs8, id);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (retireOld)
        {
            // der alte Schluessel kann nicht mehr signieren: sein privater Teil wird geloescht
            await using var up = conn.Cmd(tx, "update oidc_keys set retired_at = $1, private_sealed = null where retired_at is null", clock.GetUtcNow());
            await up.ExecuteNonQueryAsync(ct);
        }
        await using (var ins = conn.Cmd(tx, "insert into oidc_keys (id, kid, alg, public_key, private_sealed, created_at) values ($1, $2, $3, $4, $5, $6)",
            id, kid, OidcTokens.Alg, ec.ExportSubjectPublicKeyInfo(), sealedKey, clock.GetUtcNow()))
            await ins.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return kid;
    }
}
