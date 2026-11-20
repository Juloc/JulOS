using System.IO.Compression;
using System.Text;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Backend.Tests;

/// <summary>Taegliche Sicherung und Wiederherstellung (docs/BACKUP.md). Die Datenbank-Tests laufen gegen die Test-Instanz, nie gegen Live-Daten.</summary>
public sealed class BackupTests : IDisposable
{
    private const string Password = "korrektes pferd batterie", StoredPassword = "ziel-passwort 🔑 mit\ttab";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "julos-backup-test-" + Guid.NewGuid().ToString("N"));
    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static AuthService AuthFor(TestDb db, ManualTime time) =>
        new(new UserStore(db.Data), new SessionStore(db.Data), new PasswordHasher(), new LoginLimiter(), new SetupCode("AAAA-BBBB-CCCC"), time, NullLogger<AuthService>.Instance);

    private static string CodeFor(string secret, ManualTime time) => Totp.Hotp(Base32.Decode(secret), time.GetUtcNow().ToUnixTimeSeconds() / 30);

    private static BackupService ServiceFor(TestDb db, string dir, string? dataDir, TimeProvider time, int keepDays = 14, bool copyKey = true) =>
        new(db.Data, new BackupOptions(false, true, dir, new TimeOnly(3, 0), keepDays, copyKey, dataDir), time, NullLogger<BackupService>.Instance);

    [Fact]
    public async Task A_backup_restores_into_a_fresh_database_with_login_and_the_stored_password()
    {
        var data = Dir("data");
        var backupDir = Dir("backup");
        var key = SecretBox.ResolveKey(null, data)!;                                     // wie im Server: secret.key im Datenordner
        var time = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 * 30 + 1));

        // Quelle: ein Konto (Passwort + Authenticator) und eine Verbindung mit gespeichertem, verschluesseltem Passwort; der Name enthaelt Tab, Zeilenumbruch, Backslash
        string totpSecret, connName = "Büro\tPC\nzwei \\ eins";
        Guid connId;
        await using (var src = await TestDb.CreateAsync())
        {
            var auth = AuthFor(src, time);
            var begin = await auth.BeginSetupAsync("AAAA-BBBB-CCCC", "jul", Password, "1.2.3.4");
            totpSecret = begin.Data!.Secret;
            Assert.True((await auth.FinishSetupAsync("AAAA-BBBB-CCCC", "jul", CodeFor(totpSecret, time), "1.2.3.4", "t")).Ok);
            var user = (await new UserStore(src.Data).FindByNameAsync("jul"))!;
            var conns = new ConnectionStore(src.Data);
            var c = await conns.CreateAsync(user.Id, connName, "desktop", "rechner.example", 3389, "ubuntu");
            connId = c.Id;
            await conns.SetSecretAsync(user.Id, c.Id, new SecretBox(key).Encrypt(StoredPassword, c.Id));

            var file = await ServiceFor(src, backupDir, data, time).RunAsync(TestContext.Current.CancellationToken);
            Assert.EndsWith(".sql.gz", file);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(backupDir, "secret.key")));
            }
        }

        // Schluessel und Auszug sind getrennte Dateien; der Auszug enthaelt kein gespeichertes Passwort im Klartext und nicht den Schluessel
        var dumpFile = Directory.GetFiles(backupDir, "julos-db-*.sql.gz").Single();
        var keyFile = Path.Combine(backupDir, "secret.key");
        Assert.Equal(key, File.ReadAllText(keyFile).Trim());
        string dumpText;
        using (var r = DbDump.OpenForReading(dumpFile)) dumpText = r.ReadToEnd();
        Assert.DoesNotContain(key, dumpText);
        Assert.DoesNotContain("ziel-passwort", dumpText);
        Assert.StartsWith(DbDump.Magic, dumpText);

        // Ziel: frische, migrierte, leere Datenbank (anderes Schema derselben Test-Instanz)
        await using var dst = await TestDb.CreateAsync();
        using (var r = DbDump.OpenForReading(dumpFile))
        {
            var summary = await DbDump.RestoreAsync(dst.Data, r, replace: false, TestContext.Current.CancellationToken);
            Assert.Equal(1, summary.Rows["users"]);
            Assert.Equal(1, summary.Rows["connections"]);
            Assert.Equal(1, summary.Rows["sessions"]);
        }

        // Login mit Passwort und Authenticator-Code (naechster 30-s-Schritt: der alte ist als verbraucht mitgesichert)
        time.Advance(TimeSpan.FromSeconds(60));
        var login = await AuthFor(dst, time).LoginAsync("jul", Password, CodeFor(totpSecret, time), "9.9.9.9", "t");
        Assert.True(login.Ok, login.Error);
        Assert.Equal(401, (await AuthFor(dst, time).LoginAsync("jul", Password, "000000", "9.9.9.9", "t")).Status);

        // Gespeichertes Passwort mit dem gesicherten Schluessel entschluesseln
        var restoredKey = File.ReadAllText(keyFile).Trim();
        var box = new SecretBox(restoredKey);
        var user2 = (await new UserStore(dst.Data).FindByNameAsync("jul"))!;
        var conns2 = new ConnectionStore(dst.Data);
        Assert.Equal(connName, (await conns2.GetAsync(user2.Id, connId))!.Name);
        Assert.Equal(StoredPassword, box.Decrypt((await conns2.GetSecretAsync(user2.Id, connId))!, connId));
        Assert.Equal((1, 0), await DbDump.CheckSecretsAsync(dst.Data, box, TestContext.Current.CancellationToken));
        Assert.Equal((1, 1), await DbDump.CheckSecretsAsync(dst.Data, new SecretBox(Convert.ToBase64String(new byte[32])), TestContext.Current.CancellationToken));   // anderer Schluessel
    }

    [Fact]
    public async Task Restore_refuses_a_database_with_data_unless_forced_and_never_leaves_half_a_restore()
    {
        await using var src = await TestDb.CreateAsync();
        await src.ExecAsync("insert into users (name, password_hash, totp_secret) values ('a', 'h', 's')");
        var text = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(src.Data, text, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var dump = text.ToString();

        await using var dst = await TestDb.CreateAsync();
        await dst.ExecAsync("insert into users (name, password_hash, totp_secret) values ('b', 'h', 's')");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DbDump.RestoreAsync(dst.Data, new StringReader(dump), false, TestContext.Current.CancellationToken));
        Assert.Contains("nicht leer", ex.Message);
        Assert.Equal("b", await dst.ScalarAsync<string>("select name from users"));

        // abgeschnitten: mitten im Auszug aufhoeren; die vorhandenen Daten bleiben wie sie waren
        var cut = dump[..dump.IndexOf("\\.", StringComparison.Ordinal)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => DbDump.RestoreAsync(dst.Data, new StringReader(cut), true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DbDump.RestoreAsync(dst.Data, new StringReader(dump.Replace("-- end\n", "")), true, TestContext.Current.CancellationToken));
        Assert.Equal("b", await dst.ScalarAsync<string>("select name from users"));

        await DbDump.RestoreAsync(dst.Data, new StringReader(dump), true, TestContext.Current.CancellationToken);
        Assert.Equal("a", await dst.ScalarAsync<string>("select name from users"));
    }

    [Fact]
    public async Task Restore_rejects_foreign_files_newer_dumps_and_unknown_tables()
    {
        await using var db = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DbDump.RestoreAsync(db.Data, new StringReader("hallo\n"), false, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DbDump.RestoreAsync(db.Data, new StringReader(DbDump.Magic + "\n-- migrations 001_x,999_zukunft\n-- end\n"), false, ct));
        var evil = DbDump.Magic + "\n-- migrations 001_accounts_and_connections\nCOPY users; drop table users; -- (id) FROM stdin;\n\\.\n-- end\n";
        await Assert.ThrowsAsync<InvalidOperationException>(() => DbDump.RestoreAsync(db.Data, new StringReader(evil), false, ct));
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from users"));
    }

    [Fact]
    public async Task An_older_dump_restores_into_a_newer_schema_with_column_defaults()
    {
        await using var src = await TestDb.CreateAsync();
        await src.ExecAsync("insert into users (name, password_hash, totp_secret) values ('alt', 'h', 's')");
        var u = await src.ScalarAsync<Guid>("select id from users");
        await src.ExecAsync("insert into connections (user_id, name, kind, host, port) values ($1, 'c', 'desktop', 'h', 3389)", u);
        var w = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(src.Data, w, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        // so tun, als stamme der Auszug aus Version mit nur der ersten Migration: Spalten der spaeteren fehlen in der COPY-Zeile
        var old = w.ToString();
        var lines = old.Split('\n').ToList();
        var i = lines.FindIndex(l => l.StartsWith("COPY connections", StringComparison.Ordinal));
        var cols = lines[i][(lines[i].IndexOf('(') + 1)..lines[i].LastIndexOf(')')].Split(", ").ToList();
        var keep = cols.Select((c, idx) => (c, idx)).Where(x => x.c is "id" or "user_id" or "name" or "kind" or "host" or "port" or "username" or "sort" or "created_at").Select(x => x.idx).ToList();
        lines[i] = $"COPY connections ({string.Join(", ", keep.Select(k => cols[k]))}) FROM stdin;";
        lines[i + 1] = string.Join('\t', lines[i + 1].Split('\t').Where((_, idx) => keep.Contains(idx)));
        old = string.Join('\n', lines).Replace("-- migrations ", "-- migrations ").Replace(",002_session_ids", "").Replace("002_session_ids,", "");
        await using var dst = await TestDb.CreateAsync();
        await DbDump.RestoreAsync(dst.Data, new StringReader(old), false, TestContext.Current.CancellationToken);
        Assert.Equal("guac", await dst.ScalarAsync<string>("select engine from connections"));   // die Spalte engine ist ein Rest aus der Zeit mit zwei Engines und behaelt ihren Standardwert
    }

    [Fact]
    public async Task Retention_removes_old_dumps_but_never_the_newest_and_leaves_foreign_files_alone()
    {
        await using var db = await TestDb.CreateAsync();
        var dir = Dir("ret");
        var time = new ManualTime(new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero));
        var svc = ServiceFor(db, dir, null, time, keepDays: 3);
        File.WriteAllText(Path.Combine(dir, "notizen.txt"), "meins");
        for (var d = 0; d < 6; d++) { await svc.RunAsync(TestContext.Current.CancellationToken); time.Advance(TimeSpan.FromDays(1)); }
        // Letzter Lauf am 6.3. um 3 Uhr, Aufbewahrung 3 Tage: "aelter als 3.3. um 3 Uhr" geht, der 3.3. selbst bleibt
        var names = Directory.GetFiles(dir, "julos-db-*").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["julos-db-20260303T030000Z.sql.gz", "julos-db-20260304T030000Z.sql.gz", "julos-db-20260305T030000Z.sql.gz", "julos-db-20260306T030000Z.sql.gz"], names);
        Assert.True(File.Exists(Path.Combine(dir, "notizen.txt")));

        // lange nichts gelaufen: der neueste bleibt trotzdem
        time.Advance(TimeSpan.FromDays(100));
        Assert.Equal(3, svc.Prune(time.GetUtcNow()));                                        // alle alten weg, der neueste bleibt
        Assert.Single(Directory.GetFiles(dir, "julos-db-*"));
    }

    [Fact]
    public async Task The_key_is_copied_only_when_new_or_changed_and_can_be_left_out()
    {
        await using var db = await TestDb.CreateAsync();
        var data = Dir("d"); var dir = Dir("b"); var other = Dir("b2");
        var time = new ManualTime(DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(data, "secret.key"), "S1\n");
        await ServiceFor(db, dir, data, time).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("S1\n", File.ReadAllText(Path.Combine(dir, "secret.key")));
        File.WriteAllText(Path.Combine(data, "secret.key"), "S2\n");
        time.Advance(TimeSpan.FromMinutes(1));
        await ServiceFor(db, dir, data, time).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("S2\n", File.ReadAllText(Path.Combine(dir, "secret.key")));
        await ServiceFor(db, other, data, time, copyKey: false).RunAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(other, "secret.key")));
        Assert.Single(Directory.GetFiles(other, "julos-db-*"));
        Assert.Empty(Directory.GetFiles(other, "*.tmp"));
    }

    [Fact]
    public async Task A_missing_target_is_reported_loudly_and_nothing_is_created()
    {
        await using var db = await TestDb.CreateAsync();
        var missing = Path.Combine(_root, "gibt-es-nicht");
        var svc = ServiceFor(db, missing, null, TimeProvider.System);
        svc.Initialize();
        Assert.Equal("nodir", svc.Status.State);
        Assert.Contains("fehlt", svc.Status.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal("failed", svc.Status.State);
        Assert.False(Directory.Exists(missing));
        Assert.False(svc.CanRun);
    }

    [Fact]
    public void Options_have_safe_defaults_and_reject_nonsense()
    {
        var o = BackupOptions.Parse(null, null, null, null, null, "/data");
        Assert.False(o.Off); Assert.False(o.Required); Assert.Equal("/backup", o.Dir); Assert.Equal(new TimeOnly(3, 0), o.Time); Assert.Equal(14, o.KeepDays); Assert.Null(o.CopyKey);          // nicht gesetzt: BackupKeys entscheidet (kein Schluessel neben dem Auszug, ausser eine alte Kopie liegt schon da)
        var p = BackupOptions.Parse("on", "/x", "4:30", "30", "off", null);
        Assert.True(p.Required); Assert.Equal(new TimeOnly(4, 30), p.Time); Assert.Equal(30, p.KeepDays); Assert.False(p.CopyKey);
        Assert.True(BackupOptions.Parse("OFF", null, null, null, null, null).Off);
        Assert.Throws<InvalidOperationException>(() => BackupOptions.Parse("ja", null, null, null, null, null));
        Assert.Throws<InvalidOperationException>(() => BackupOptions.Parse(null, null, "25:00", null, null, null));
        Assert.Throws<InvalidOperationException>(() => BackupOptions.Parse(null, null, null, "0", null, null));
        Assert.Throws<InvalidOperationException>(() => BackupOptions.Parse(null, null, null, "x", null, null));
        Assert.True(BackupOptions.Parse(null, null, null, null, "copy", null).CopyKey);
        Assert.Throws<InvalidOperationException>(() => BackupOptions.Parse(null, null, null, null, "alle", null));
    }

    [Fact]
    public void The_next_run_is_the_next_time_of_day_in_the_servers_zone()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var at = new TimeOnly(3, 0);
        // Winter: 02:00 UTC = 03:00 Berlin
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero), BackupService.NextRun(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), berlin, at));
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 2, 0, 0, TimeSpan.Zero), BackupService.NextRun(new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero), berlin, at));
        // Sommer: 01:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 1, 0, 0, TimeSpan.Zero), BackupService.NextRun(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), berlin, at));
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 3, 0, 0, TimeSpan.Zero), BackupService.NextRun(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, at));
    }
}
