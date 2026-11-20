using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend.Cli;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Backend.Tests;

/// <summary>Der Schluessel der Passwoerter liegt nicht mehr neben dem Auszug (F-01): Wiederherstellungsschluessel, umhuellte Datei, Wiederherstellung; TOTP-Geheimnis verschluesselt (F-10).</summary>
public sealed class BackupKeyTests : IDisposable
{
    private readonly List<string> _dirs = [];
    private string Dir(string name) { var d = Path.Combine(Path.GetTempPath(), "julos-bk-" + name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); _dirs.Add(d); return d; }
    public void Dispose() { foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { } }

    private static BackupOptions Options(string dir, string? data, bool? copyKey = null) => new(false, true, dir, new TimeOnly(3, 0), 14, copyKey, data);

    [Fact]
    public void The_plan_follows_app_then_environment_then_an_old_copy_and_else_keeps_the_key_out()
    {
        var dir = Dir("plan");
        Assert.Equal(("default", false), Pair(BackupKeys.PlanFor(Options(dir, null), null, null)));
        Assert.Equal(("env", true), Pair(BackupKeys.PlanFor(Options(dir, null, true), null, null)));
        Assert.Equal(("env", false), Pair(BackupKeys.PlanFor(Options(dir, null, false), null, null)));
        Assert.Equal(("app", true), Pair(BackupKeys.PlanFor(Options(dir, null, false), true, null)));            // die App geht vor der Umgebung
        File.WriteAllText(Path.Combine(dir, "secret.key"), "alt\n");
        Assert.Equal(("legacy", true), Pair(BackupKeys.PlanFor(Options(dir, null), null, null)));                // eine alte Kopie bleibt, bis in der App umgestellt wird
        var off = BackupKeys.PlanFor(Options(dir, null), false, null);
        Assert.False(off.CopyKey); Assert.True(off.RemoveCopies);                                               // nur ausdruecklich in der App ausgeschaltet entfernt es die Kopie
        Assert.False(BackupKeys.PlanFor(Options(dir, null, false), null, null).RemoveCopies);
        static (string, bool) Pair(BackupKeyPlan p) => (p.Source, p.CopyKey);
    }

    [Fact]
    public async Task A_backup_leaves_no_key_in_the_folder_only_the_wrapped_one_and_it_opens_only_with_the_recovery_key()
    {
        await using var db = await TestDb.CreateAsync();
        var data = Dir("data"); var backup = Dir("backup");
        var secret = SecretBox.ResolveKey(null, data)!;
        var box = new SecretBox(secret);
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var settings = new InstanceSettings(db.Data);
        await settings.LoadAsync();
        var keys = new BackupKeys(settings, Options(backup, data), box, time);
        var svc = new BackupService(db.Data, Options(backup, data), time, NullLogger<BackupService>.Instance, null, keys.Plan);
        var ct = TestContext.Current.CancellationToken;

        await svc.RunAsync(ct);
        Assert.Empty(Directory.GetFiles(backup, "secret.key*"));                         // Standard: weder secret.key noch etwas anderes vom Schluessel

        // Wiederherstellungsschluessel: erst erzeugt (wartet), nach "gesichert" gilt er
        var text = await keys.NewRecoveryAsync(ct);
        Assert.Matches("^([A-Z2-7]{4}-){12}[A-Z2-7]{4}$", text);
        Assert.Equal("pending", keys.Recovery().State);
        time.Advance(TimeSpan.FromMinutes(1));
        await svc.RunAsync(ct);
        Assert.Empty(Directory.GetFiles(backup, "secret.key*"));                         // solange er nicht bestaetigt ist, wird nichts abgelegt
        Assert.True(await keys.ConfirmRecoveryAsync(ct));
        Assert.False(await keys.ConfirmRecoveryAsync(ct));
        Assert.Equal("confirmed", keys.Recovery().State);
        time.Advance(TimeSpan.FromMinutes(1));
        await svc.RunAsync(ct);

        Assert.False(File.Exists(Path.Combine(backup, "secret.key")));
        var wrappedFile = File.ReadAllBytes(Path.Combine(backup, BackupService.WrappedName));
        Assert.DoesNotContain(secret, System.Text.Encoding.ASCII.GetString(wrappedFile));
        var wrapped = RecoveryKey.FromFile(wrappedFile)!;
        Assert.Equal(secret, Convert.ToBase64String(RecoveryKey.Unwrap(wrapped, RecoveryKey.Parse(text.ToLowerInvariant().Replace("-", " "))!)!));   // Schreibweise egal
        Assert.Null(RecoveryKey.Unwrap(wrapped, RecoveryKey.New()));                      // falscher Schluessel
        Assert.Null(RecoveryKey.Parse("ABCD-EFGH"));                                      // zu kurz
        foreach (var f in Directory.GetFiles(backup, "julos-db-*")) { using var r = DbDump.OpenForReading(f); Assert.DoesNotContain(secret, r.ReadToEnd()); }
    }

    [Fact]
    public async Task An_old_key_copy_stays_until_the_app_turns_it_off_and_then_goes_away()
    {
        await using var db = await TestDb.CreateAsync();
        var data = Dir("data"); var backup = Dir("backup");
        var box = new SecretBox(SecretBox.ResolveKey(null, data));
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var settings = new InstanceSettings(db.Data);
        await settings.LoadAsync();
        var keys = new BackupKeys(settings, Options(backup, data), box, time);
        var svc = new BackupService(db.Data, Options(backup, data), time, NullLogger<BackupService>.Instance, null, keys.Plan);
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(backup, "secret.key"), "alt\n");                  // wie von einer Version vor 0.18 hinterlassen

        await svc.RunAsync(ct);
        Assert.Equal("legacy", keys.Plan().Source);
        Assert.Equal(File.ReadAllText(Path.Combine(data, "secret.key")), File.ReadAllText(Path.Combine(backup, "secret.key")));   // bisheriges Verhalten: nichts geht verloren

        await keys.SetCopyKeyAsync(false, ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await svc.RunAsync(ct);
        Assert.False(File.Exists(Path.Combine(backup, "secret.key")));
        Assert.Equal("app", keys.Plan().Source);
        await keys.SetCopyKeyAsync(true, ct);                                             // bewusst wieder an: Warnung in der App, aber erlaubt
        time.Advance(TimeSpan.FromMinutes(1));
        await svc.RunAsync(ct);
        Assert.True(File.Exists(Path.Combine(backup, "secret.key")));
    }

    [Fact]
    public async Task Restore_with_the_recovery_key_installs_the_key_and_a_wrong_key_changes_nothing()
    {
        await using var src = await TestDb.CreateAsync();
        var data = Dir("data"); var backup = Dir("backup");
        var secret = SecretBox.ResolveKey(null, data)!;
        var box = new SecretBox(secret);
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var settings = new InstanceSettings(src.Data);
        await settings.LoadAsync();
        var keys = new BackupKeys(settings, Options(backup, data), box, time);
        var ct = TestContext.Current.CancellationToken;
        var recovery = await keys.NewRecoveryAsync(ct);
        await keys.ConfirmRecoveryAsync(ct);

        var users = new UserStore(src.Data, box);
        var user = await users.CreateAsync("jul", "hash", Totp.NewSecret(), true, [], false, ct);
        var conns = new ConnectionStore(src.Data);
        var c = await conns.CreateAsync(user.Id, "PC", "terminal", "pc.example", 22, "u");
        await conns.SetSecretAsync(user.Id, c.Id, box.Encrypt("ziel-passwort", c.Id));
        var dump = await new BackupService(src.Data, Options(backup, data), time, NullLogger<BackupService>.Instance, null, keys.Plan).RunAsync(ct);

        await using var dst = await TestDb.CreateAsync();
        var data2 = Dir("data2");
        var file = Path.Combine(Dir("rk"), "rk.txt");
        File.WriteAllText(file, RecoveryKey.Format(RecoveryKey.New()));                   // falscher Schluessel
        Assert.Equal(1, await BackupCli.RestoreAsync(dst.Data, [dump, "--recovery-key-file", file], k => k == "JULOS_DATA" ? data2 : null));
        Assert.Equal(0L, await dst.ScalarAsync<long>("select count(*) from users"));      // nichts geaendert
        Assert.False(File.Exists(Path.Combine(data2, "secret.key")));

        File.WriteAllText(file, recovery + "\n");
        Assert.Equal(0, await BackupCli.RestoreAsync(dst.Data, [dump, "--recovery-key-file", file], k => k == "JULOS_DATA" ? data2 : null));
        Assert.Equal(secret, File.ReadAllText(Path.Combine(data2, "secret.key")).Trim());
        // Passwoerter und das TOTP-Geheimnis des Kontos sind mit dem wiederhergestellten Schluessel lesbar
        Assert.Equal((2, 0), await DbDump.CheckSecretsAsync(dst.Data, new SecretBox(File.ReadAllText(Path.Combine(data2, "secret.key"))), ct));
    }

    [Fact]
    public async Task The_totp_secret_is_stored_sealed_old_plain_rows_still_work_and_get_sealed_at_start_and_a_dump_shows_no_secret()
    {
        await using var db = await TestDb.CreateAsync();
        var box = new SecretBox(Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()));
        var users = new UserStore(db.Data, box);
        var ct = TestContext.Current.CancellationToken;

        var s1 = Totp.NewSecret();
        var sealedUser = await users.CreateAsync("neu", "hash", s1, true, [], false, ct);
        var stored = await db.ScalarAsync<string>("select totp_secret from users where id = $1", sealedUser.Id);
        Assert.StartsWith("enc1:", stored);
        Assert.DoesNotContain(s1, stored);
        Assert.Equal(s1, (await users.FindByIdAsync(sealedUser.Id, ct))!.TotpSecret);
        Assert.Equal(s1, (await users.FindByNameAsync("NEU", ct))!.TotpSecret);

        // eine alte Zeile im Klartext: lesbar, und der Start (MigrateTotpAsync) verschluesselt sie; ein zweiter Lauf findet nichts mehr
        var s2 = Totp.NewSecret();
        var old = await db.ScalarAsync<Guid>("insert into users (name, password_hash, totp_secret, totp_confirmed) values ('alt', 'h', $1, true) returning id", s2);
        Assert.Equal(s2, (await users.FindByIdAsync(old, ct))!.TotpSecret);
        Assert.Equal(1, await users.MigrateTotpAsync(ct));
        Assert.Equal(0, await users.MigrateTotpAsync(ct));
        Assert.StartsWith("enc1:", await db.ScalarAsync<string>("select totp_secret from users where id = $1", old));
        Assert.Equal(s2, (await users.FindByIdAsync(old, ct))!.TotpSecret);

        // ein neuer Authenticator wird auch verschluesselt abgelegt
        var s3 = Totp.NewSecret();
        await users.ReplaceTotpAsync(old, s3, [], ct);
        Assert.DoesNotContain(s3, await db.ScalarAsync<string>("select totp_secret from users where id = $1", old));
        Assert.Equal(s3, (await users.FindByIdAsync(old, ct))!.TotpSecret);

        // ein Auszug zeigt kein Geheimnis im Klartext; mit einem anderen Schluessel ist es nicht lesbar (Anmeldung scheitert, Weg zurueck ist tools/julos-user.sh)
        var text = new StringWriter { NewLine = "\n" };
        await DbDump.WriteAsync(db.Data, text, DateTimeOffset.UtcNow, ct);
        foreach (var s in new[] { s1, s2, s3 }) Assert.DoesNotContain(s, text.ToString());
        var other = new UserStore(db.Data, new SecretBox(Convert.ToBase64String(new byte[32])));
        Assert.Equal("", (await other.FindByIdAsync(sealedUser.Id, ct))!.TotpSecret);
        Assert.Equal((2, 0), await DbDump.CheckSecretsAsync(db.Data, box, ct));
        Assert.Equal((2, 2), await DbDump.CheckSecretsAsync(db.Data, new SecretBox(Convert.ToBase64String(new byte[32])), ct));
        // ohne Schluessel bleibt es Klartext (nichts wird zerstoert)
        Assert.Equal(0, await new UserStore(db.Data).MigrateTotpAsync(ct));
    }
}
