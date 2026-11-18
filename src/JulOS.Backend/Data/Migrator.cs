using System.Reflection;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>
/// Wendet die nummerierten SQL-Skripte aus <c>database/</c> der Reihe nach an (jedes in einer Transaktion).
/// Ein Skript wird nie zweimal ausgefuehrt und nie veraendert; Aenderungen kommen als neues Skript.
/// Ein Advisory-Lock verhindert, dass zwei Instanzen gleichzeitig migrieren.
/// </summary>
public sealed class Migrator(NpgsqlDataSource db)
{
    private const long LockKey = 0x4A554C4F53; // "JULOS"
    private const string Prefix = "database/";

    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        var scripts = ReadScripts();
        var applied = new List<string>();

        await using var conn = await db.OpenConnectionAsync(ct);
        await ExecuteAsync(conn, "select pg_advisory_lock($1)", ct, LockKey);
        try
        {
            await ExecuteAsync(conn, "create table if not exists schema_migrations (id text primary key, applied_at timestamptz not null default now())", ct);
            foreach (var (id, sql) in scripts)
            {
                if (await IsAppliedAsync(conn, id, ct)) continue;
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var run = new NpgsqlCommand(sql, conn, tx)) await run.ExecuteNonQueryAsync(ct);
                await using (var mark = new NpgsqlCommand("insert into schema_migrations (id) values ($1)", conn, tx) { Parameters = { new() { Value = id } } })
                    await mark.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                applied.Add(id);
            }
        }
        finally
        {
            await ExecuteAsync(conn, "select pg_advisory_unlock($1)", CancellationToken.None, LockKey);
        }
        return applied;
    }

    private static List<(string Id, string Sql)> ReadScripts()
    {
        var asm = typeof(Migrator).Assembly;
        var list = new List<(string, string)>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Migration fehlt: {name}");
            using var r = new StreamReader(s);
            list.Add((name[Prefix.Length..^4], r.ReadToEnd()));
        }
        return list;
    }

    private static async Task<bool> IsAppliedAsync(NpgsqlConnection conn, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select 1 from schema_migrations where id = $1", conn) { Parameters = { new() { Value = id } } };
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
