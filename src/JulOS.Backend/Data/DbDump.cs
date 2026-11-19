using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

/// <summary>Was ein Dump oder eine Wiederherstellung angefasst hat: Zeilen je Tabelle.</summary>
public sealed record DumpSummary(IReadOnlyDictionary<string, long> Rows, IReadOnlyList<string> Migrations)
{
    public long TotalRows => Rows.Values.Sum();
}

/// <summary>
/// Logischer Auszug der Datenbank als Text, ohne pg_dump: das Image hat keinen passenden Client (Debian liefert nur PostgreSQL 17, die Datenbank ist 18,
/// und pg_dump verweigert eine neuere Datenbank). Der Auszug besteht aus <c>COPY ... FROM stdin</c>-Bloecken (jede Tabelle des Schemas, in
/// Reihenfolge der Fremdschluessel) und ist mit psql lesbar. Das Schema selbst steht nicht drin: der Migrator baut es, die Kopfzeile nennt nur, welche
/// Migrationen der Auszug kennt. So geht auch ein Auszug aus einer aelteren Version in ein neueres JulOS (neue Spalten haben Standardwerte),
/// ein Auszug aus einer neueren Version wird abgelehnt. Ein Auszug ist ein konsistenter Schnappschuss (eine Transaktion), das Schreiben stoert den Betrieb nicht.
/// </summary>
public static partial class DbDump
{
    public const string Magic = "-- JulOS backup format 1";
    private const string EndMarker = "-- end";

    public static async Task<DumpSummary> WriteAsync(NpgsqlDataSource db, TextWriter output, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);   // ein Schnappschuss fuer alle Tabellen
        await using (var ro = conn.Cmd(tx, "set transaction read only")) await ro.ExecuteNonQueryAsync(ct);

        var migrations = await ReadColumnAsync(conn, tx, "select id from schema_migrations order by id", ct);
        var tables = await TablesInOrderAsync(conn, tx, ct);
        await output.WriteLineAsync(Magic);
        await output.WriteLineAsync($"-- created {now:yyyy-MM-ddTHH:mm:ssZ}");
        await output.WriteLineAsync("-- migrations " + string.Join(',', migrations));

        var rows = new Dictionary<string, long>();
        foreach (var table in tables)
        {
            var columns = await ColumnsAsync(conn, tx, table, ct);
            await output.WriteLineAsync($"COPY {table} ({string.Join(", ", columns)}) FROM stdin;");
            long n = 0;
            using (var export = await conn.BeginTextExportAsync($"copy {table} ({string.Join(", ", columns)}) to stdout", ct))
                while (await export.ReadLineAsync(ct) is { } line) { await output.WriteLineAsync(line); n++; }
            await output.WriteLineAsync("\\.");
            rows[table] = n;
        }
        await output.WriteLineAsync(EndMarker);
        await output.FlushAsync(ct);
        return new DumpSummary(rows, migrations);
    }

    /// <summary>
    /// Liest einen Auszug in die Datenbank. Migriert zuerst (leere Datenbank bekommt das Schema). Ist schon etwas drin, bricht es ab, ausser <paramref name="replace"/>:
    /// dann wird alles ersetzt. Alles oder nichts: bei jedem Fehler (auch einem abgeschnittenen Auszug) bleibt die Datenbank, wie sie war.
    /// </summary>
    public static async Task<DumpSummary> RestoreAsync(NpgsqlDataSource db, TextReader input, bool replace, CancellationToken ct = default)
    {
        if (await input.ReadLineAsync(ct) != Magic) throw new InvalidOperationException("Das ist kein JulOS-Auszug (erste Zeile stimmt nicht).");
        string[]? dumpMigrations = null;
        string? next;
        while ((next = await input.ReadLineAsync(ct)) is not null && !next.StartsWith("COPY ", StringComparison.Ordinal) && next != EndMarker)
            if (next.StartsWith("-- migrations ", StringComparison.Ordinal)) dumpMigrations = next["-- migrations ".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (dumpMigrations is null) throw new InvalidOperationException("Der Auszug nennt seine Migrationen nicht (beschädigt?).");

        await new Migrator(db).RunAsync(ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        var known = await ReadColumnAsync(conn, null, "select id from schema_migrations", ct);
        var unknown = dumpMigrations.Except(known).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"Der Auszug stammt aus einer neueren JulOS-Version (kennt {unknown[0]}). Erst JulOS aktualisieren.");

        await using var tx = await conn.BeginTransactionAsync(ct);
        var tables = await TablesInOrderAsync(conn, tx, ct);
        if (replace)
        {
            if (tables.Count > 0) await using (var wipe = conn.Cmd(tx, $"truncate table {string.Join(", ", tables)} cascade")) await wipe.ExecuteNonQueryAsync(ct);
        }
        else
            foreach (var t in tables)
                await using (var any = conn.Cmd(tx, $"select exists (select 1 from {t})"))
                    if ((bool)(await any.ExecuteScalarAsync(ct))!) throw new InvalidOperationException($"Die Datenbank ist nicht leer (Tabelle {t}). Mit --force wird alles ersetzt.");

        var columnsOf = new Dictionary<string, HashSet<string>>();
        foreach (var t in tables) columnsOf[t] = (await ColumnsAsync(conn, tx, t, ct)).ToHashSet();
        var rows = new Dictionary<string, long>();
        var ended = false;
        // "next" ist die erste Zeile nach dem Kopf (COPY oder Ende)
        for (; next is not null; next = await input.ReadLineAsync(ct))
        {
            if (next == EndMarker) { ended = true; break; }
            if (next.Length == 0) continue;
            var m = CopyHeader().Match(next);
            // Tabellen- und Spaltennamen kommen aus einer Datei: nur Namen, die es in der Datenbank gibt, kommen in den Befehl
            if (!m.Success || !columnsOf.TryGetValue(m.Groups[1].Value, out var allowed)) throw new InvalidOperationException("Unerwartete Zeile im Auszug: " + Shorten(next));
            var cols = m.Groups[2].Value.Split(", ");
            if (cols.Any(c => !allowed.Contains(c))) throw new InvalidOperationException($"Der Auszug kennt Spalten, die es in {m.Groups[1].Value} nicht gibt (neuere Version?).");
            long n = 0;
            using (var import = await conn.BeginTextImportAsync($"copy {m.Groups[1].Value} ({m.Groups[2].Value}) from stdin", ct))
            {
                string? line;
                while ((line = await input.ReadLineAsync(ct)) is not null && line != "\\.") { await import.WriteLineAsync(line); n++; }
                if (line is null) throw new InvalidOperationException("Der Auszug ist abgeschnitten (Datei unvollständig).");
            }
            rows[m.Groups[1].Value] = n;
        }
        if (!ended) throw new InvalidOperationException("Der Auszug ist abgeschnitten (Ende fehlt).");
        await tx.CommitAsync(ct);
        return new DumpSummary(rows, dumpMigrations);
    }

    /// <summary>Wie viele gespeicherte Passwoerter es gibt und wie viele davon sich mit dem Schluessel nicht oeffnen lassen (falscher oder fehlender Schluessel).</summary>
    public static async Task<(int Total, int Unreadable)> CheckSecretsAsync(NpgsqlDataSource db, SecretBox box, CancellationToken ct = default)
    {
        int total = 0, bad = 0;
        await using var cmd = db.CreateCommand("select id, secret from connections where secret is not null");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            total++;
            if (box.Decrypt((byte[])r["secret"], r.GetGuid(0)) is null) bad++;
        }
        return (total, bad);
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..60] + "…";

    private static async Task<List<string>> ReadColumnAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        var list = new List<string>();
        await using var cmd = conn.Cmd(tx, sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Alle Tabellen des aktuellen Schemas (ohne die Migrationsliste), Eltern vor Kindern, damit Fremdschluessel beim Einlesen halten.</summary>
    private static async Task<List<string>> TablesInOrderAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var all = await ReadColumnAsync(conn, tx,
            "select quote_ident(relname) from pg_class where relnamespace = current_schema()::regnamespace and relkind = 'r' and relname <> 'schema_migrations' order by relname", ct);
        var parents = all.ToDictionary(t => t, _ => new HashSet<string>());
        await using (var cmd = conn.Cmd(tx,
            "select quote_ident(c.relname), quote_ident(p.relname) from pg_constraint k join pg_class c on c.oid = k.conrelid join pg_class p on p.oid = k.confrelid "
            + "where k.contype = 'f' and c.relnamespace = current_schema()::regnamespace and c.oid <> p.oid"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) if (parents.TryGetValue(r.GetString(0), out var set)) set.Add(r.GetString(1));
        var ordered = new List<string>();
        while (ordered.Count < all.Count)
        {
            var ready = all.Where(t => !ordered.Contains(t) && parents[t].All(ordered.Contains)).ToList();
            if (ready.Count == 0) throw new InvalidOperationException("Fremdschlüssel bilden einen Kreis; Reihenfolge nicht bestimmbar.");
            ordered.AddRange(ready);
        }
        return ordered;
    }

    private static async Task<List<string>> ColumnsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = conn.Cmd(tx, "select quote_ident(attname) from pg_attribute where attrelid = $1::regclass and attnum > 0 and not attisdropped and attgenerated = '' order by attnum", table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    [GeneratedRegex(@"^COPY (\S+) \((.+)\) FROM stdin;$")]
    private static partial Regex CopyHeader();

    /// <summary>Oeffnet einen Auszug zum Lesen: gzip oder schon entpackter Text.</summary>
    public static StreamReader OpenForReading(string path)
    {
        var fs = File.OpenRead(path);
        var b1 = fs.ReadByte();
        var b2 = fs.ReadByte();
        fs.Position = 0;
        Stream s = b1 == 0x1f && b2 == 0x8b ? new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress) : fs;
        return new StreamReader(s, new UTF8Encoding(false));
    }
}
