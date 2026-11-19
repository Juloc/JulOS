using JulOS.Backend.Services;
using JulOS.Integrations.Ssh;
using JulOS.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;

namespace JulOS.Backend.Tests;

/// <summary>Zweites Backup-Ziel (docs/BACKUP.md): Ordner und SFTP. Datenbank-Tests laufen gegen die Test-Instanz, SFTP gegen das Test-SSH-Ziel (deploy/dev/test.sh), sonst gegen einen Fake.</summary>
public sealed class BackupCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "julos-backupcopy-test-" + Guid.NewGuid().ToString("N"));
    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static BackupService ServiceFor(TestDb db, string dir, string? data, TimeProvider time, BackupCopyOptions copy, Func<IBackupTarget>? target, int keepDays = 14) =>
        new(db.Data, new BackupOptions(false, true, dir, new TimeOnly(3, 0), keepDays, true, data, copy), time, NullLogger<BackupService>.Instance, target);

    private static Func<IBackupTarget> PathTarget(string dir) => () => new PathBackupTarget(dir);
    private static BackupCopyOptions PathCopy(string dir) => new(BackupCopyMode.Path, Path: dir);
    private static string[] Names(string dir, string pattern = "*") => Directory.GetFiles(dir, pattern).Select(Path.GetFileName).Order().ToArray()!;

    // ---- Einstellungen ----

    private static Func<string, string?> Env(params (string, string)[] values) { var d = values.ToDictionary(v => v.Item1, v => v.Item2); return k => d.GetValueOrDefault(k); }

    [Fact]
    public void The_second_target_is_off_by_default_and_options_reject_nonsense()
    {
        Assert.False(BackupOptions.FromSettings(Env()).Copy!.Enabled);
        Assert.Equal(BackupCopyMode.Off, BackupOptions.FromSettings(Env(("JULOS_BACKUP_COPY", "off"))).Copy!.Mode);
        Assert.Equal(BackupCopyMode.Off, BackupOptions.FromSettings(Env(("JULOS_BACKUP_COPY", " "))).Copy!.Mode);

        var path = BackupOptions.FromSettings(Env(("JULOS_BACKUP_COPY", "PATH"), ("JULOS_BACKUP_COPY_PATH", "/nas")));
        Assert.Equal(BackupCopyMode.Path, path.Copy!.Mode); Assert.Equal("/nas", path.Copy.Path);
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env(("JULOS_BACKUP_COPY", "path"))));
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env(("JULOS_BACKUP_COPY", "path"), ("JULOS_BACKUP_COPY_PATH", "/backup/"))));   // derselbe Ordner wie lokal
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env(("JULOS_BACKUP_COPY", "cloud"))));

        (string, string)[] sftp = [("JULOS_BACKUP_COPY", "sftp"), ("JULOS_BACKUP_SFTP_HOST", "nas"), ("JULOS_BACKUP_SFTP_USER", "u"), ("JULOS_BACKUP_SFTP_PATH", "/b"), ("JULOS_DATA", "/data")];
        var ok = BackupOptions.FromSettings(Env([.. sftp, ("JULOS_BACKUP_SFTP_KEY_FILE", "/run/k")])).Copy!;
        Assert.Equal(22, ok.Port); Assert.Equal("/data/backup-sftp-hostkey", ok.HostKeyFile); Assert.Equal("sftp://u@nas/b", ok.Describe());
        Assert.Equal("sftp://u@nas:2222/b", BackupOptions.FromSettings(Env([.. sftp, ("JULOS_BACKUP_SFTP_PASSWORD_FILE", "/run/p"), ("JULOS_BACKUP_SFTP_PORT", "2222")])).Copy!.Describe());
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env(sftp)));                                                                                            // keine Anmeldeart
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env([.. sftp, ("JULOS_BACKUP_SFTP_KEY_FILE", "/k"), ("JULOS_BACKUP_SFTP_PASSWORD_FILE", "/p")])));      // beide
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env([.. sftp, ("JULOS_BACKUP_SFTP_KEY_FILE", "/k"), ("JULOS_BACKUP_SFTP_PORT", "0")])));
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env([.. sftp, ("JULOS_BACKUP_SFTP_KEY_FILE", "/k"), ("JULOS_BACKUP_SFTP_HOST_KEY", "irgendwas")])));
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env(sftp.Where(s => s.Item1 != "JULOS_BACKUP_SFTP_HOST").Append(("JULOS_BACKUP_SFTP_KEY_FILE", "/k")).ToArray())));
        Assert.Throws<InvalidOperationException>(() => BackupOptions.FromSettings(Env(sftp.Where(s => s.Item1 != "JULOS_DATA").Append(("JULOS_BACKUP_SFTP_KEY_FILE", "/k")).ToArray())));       // nirgends den Host-Schluessel merken
    }

    // ---- Ordner ----

    [Fact]
    public async Task A_backup_is_copied_with_the_key_into_the_second_folder_and_nothing_else_is_touched()
    {
        await using var db = await TestDb.CreateAsync();
        var data = Dir("data"); var local = Dir("local"); var copy = Dir("copy");
        File.WriteAllText(Path.Combine(data, "secret.key"), "S1\n");
        File.WriteAllText(Path.Combine(copy, "notizen.txt"), "meins");
        File.WriteAllText(Path.Combine(copy, "julos-db-20200101T000000Z.sql.gz.tmp"), "liegengeblieben");
        var time = new ManualTime(new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero));
        var svc = ServiceFor(db, local, data, time, PathCopy(copy), PathTarget(copy));
        svc.Initialize();
        Assert.Equal("pending", svc.Status.Copy!.State);

        var file = await svc.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ok", svc.Status.Copy!.State);
        Assert.Equal(time.GetUtcNow(), svc.Status.Copy.LastAt);
        Assert.Equal(copy, svc.Status.Copy.Target);
        var name = Path.GetFileName(file);
        Assert.Equal([name, "notizen.txt", "secret.key"], Names(copy));                     // die alte .tmp ist weg, die fremde Datei bleibt
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(copy, name)));
        Assert.Equal("S1\n", File.ReadAllText(Path.Combine(copy, "secret.key")));
        Assert.Equal(["secret.key"], Names(local, "secret.key"));                             // Schluessel und Auszug bleiben getrennte Dateien
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(mode, File.GetUnixFileMode(Path.Combine(copy, name)));
            Assert.Equal(mode, File.GetUnixFileMode(Path.Combine(copy, "secret.key")));
        }

        // geaenderter Schluessel geht beim naechsten Lauf mit
        File.WriteAllText(Path.Combine(data, "secret.key"), "S2\n");
        time.Advance(TimeSpan.FromDays(1));
        await svc.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("S2\n", File.ReadAllText(Path.Combine(copy, "secret.key")));
        Assert.Equal(2, Names(copy, "julos-db-*").Length);
    }

    [Fact]
    public async Task The_second_folder_keeps_dumps_as_long_as_the_local_one_but_never_deletes_the_newest()
    {
        await using var db = await TestDb.CreateAsync();
        var local = Dir("l"); var copy = Dir("c");
        File.WriteAllText(Path.Combine(copy, "notizen.txt"), "meins");
        var time = new ManualTime(new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero));
        var svc = ServiceFor(db, local, null, time, PathCopy(copy), PathTarget(copy), keepDays: 3);
        for (var d = 0; d < 6; d++) { await svc.RunAsync(TestContext.Current.CancellationToken); time.Advance(TimeSpan.FromDays(1)); }
        Assert.Equal(Names(local, "julos-db-*"), Names(copy, "julos-db-*"));
        Assert.Equal(["julos-db-20260303T030000Z.sql.gz", "julos-db-20260304T030000Z.sql.gz", "julos-db-20260305T030000Z.sql.gz", "julos-db-20260306T030000Z.sql.gz"], Names(copy, "julos-db-*"));
        Assert.True(File.Exists(Path.Combine(copy, "notizen.txt")));

        // lange nichts gelaufen: am Ziel bleibt nach dem naechsten Lauf nur der neueste Auszug (der alte ist ueber der Aufbewahrung)
        time.Advance(TimeSpan.FromDays(100));
        await svc.RunAsync(TestContext.Current.CancellationToken);
        Assert.Single(Names(copy, "julos-db-*"));
        Assert.Single(Names(local, "julos-db-*"));
    }

    [Fact]
    public async Task A_failing_second_target_only_warns_and_the_next_run_catches_up()
    {
        await using var db = await TestDb.CreateAsync();
        var data = Dir("d"); var local = Dir("l"); var missing = Path.Combine(_root, "nas-nicht-eingebunden");
        File.WriteAllText(Path.Combine(data, "secret.key"), "S1\n");
        var time = new ManualTime(new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero));
        var svc = ServiceFor(db, local, data, time, PathCopy(missing), PathTarget(missing));
        svc.Initialize();

        var first = await svc.RunAsync(TestContext.Current.CancellationToken);              // wirft nicht
        Assert.True(File.Exists(first));
        Assert.Equal("ok", svc.Status.State);                                                // das lokale Backup ist in Ordnung
        Assert.Equal("failed", svc.Status.Copy!.State);
        Assert.Contains("fehlt", svc.Status.Copy.Message);
        Assert.Null(svc.Status.Copy.LastAt);
        Assert.False(Directory.Exists(missing));                                             // nie angelegt

        // Ziel taucht auf: der naechste Lauf bringt beide Auszuege hin (auch den vom Vortag)
        Directory.CreateDirectory(missing);
        time.Advance(TimeSpan.FromDays(1));
        await svc.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ok", svc.Status.Copy!.State);
        Assert.Equal(2, Names(missing, "julos-db-*").Length);
        Assert.Equal("S1\n", File.ReadAllText(Path.Combine(missing, "secret.key")));

        // und wieder weg: Status failed behaelt die Zeit der letzten Kopie
        Directory.Delete(missing, true);
        time.Advance(TimeSpan.FromDays(1));
        await svc.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("failed", svc.Status.Copy!.State);
        Assert.Equal(time.GetUtcNow().AddDays(-1), svc.Status.Copy.LastAt);
        Assert.Equal(3, Names(local, "julos-db-*").Length);
    }

    [Fact]
    public async Task Putting_is_atomic_a_cut_off_transfer_leaves_no_file_and_no_temp_file()
    {
        var target = new PathBackupTarget(Dir("t"));
        await target.PutAsync("a.bin", new MemoryStream([1, 2, 3]), TestContext.Current.CancellationToken);
        await target.PutAsync("a.bin", new MemoryStream([4, 5]), TestContext.Current.CancellationToken);              // ersetzt
        Assert.Equal([4, 5], await target.ReadAsync("a.bin", TestContext.Current.CancellationToken));
        Assert.Null(await target.ReadAsync("gibt-es-nicht", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => target.PutAsync("b.bin", new CutOffStream(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => target.PutAsync("a.bin", new CutOffStream(), TestContext.Current.CancellationToken));
        Assert.Equal(["a.bin"], Names(Path.Combine(_root, "t")));                                                    // weder b.bin noch .tmp; a.bin unveraendert
        Assert.Equal([4, 5], await target.ReadAsync("a.bin", TestContext.Current.CancellationToken));
    }

    private sealed class CutOffStream : Stream
    {
        private int _n;
        public override int Read(byte[] buffer, int offset, int count) { if (_n++ > 0) throw new IOException("Verbindung weg"); buffer[offset] = 7; return 1; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Without_a_second_target_nothing_changes_and_the_status_says_off()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = ServiceFor(db, Dir("l"), null, TimeProvider.System, BackupCopyOptions.Off, null);
        svc.Initialize();
        await svc.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("off", svc.Status.Copy!.State);
    }

    // ---- SFTP: Host-Schluessel und Anmeldung (Fake) ----

    private sealed class FakeSftp(SshHostKey key, Dictionary<string, byte[]>? files = null) : ISftpSession
    {
        public Dictionary<string, byte[]> Files { get; } = files ?? new() { ["/b/vorhanden.txt"] = [1] };
        public SftpConnectOptions? Connected;
        public SshHostKey? PresentedHostKey => key;
        public Task ConnectAsync(SftpConnectOptions o, Func<SshHostKey, bool> trust, CancellationToken ct)
        {
            if (!trust(key)) throw new SshException(SshFailure.HostKeyRejected, "Host-Schlüssel nicht vertraut.");
            Connected = o;
            return Task.CompletedTask;
        }
        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct) => Task.FromResult(path == "/b");
        public Task<IReadOnlyList<SftpFile>> ListAsync(string d, CancellationToken ct) => Task.FromResult<IReadOnlyList<SftpFile>>(Files.Select(f => new SftpFile(f.Key[(d.Length + 1)..], f.Value.Length)).ToList());
        public Task<byte[]?> ReadAllAsync(string p, CancellationToken ct) => Task.FromResult(Files.GetValueOrDefault(p));
        public Task UploadAsync(Stream data, string tmp, string final, CancellationToken ct) { var m = new MemoryStream(); data.CopyTo(m); Files[final] = m.ToArray(); return Task.CompletedTask; }
        public Task DeleteAsync(string p, CancellationToken ct) { Files.Remove(p); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private BackupCopyOptions Sftp(string? pin = null, string? passwordFile = null, string? keyFile = null) =>
        new(BackupCopyMode.Sftp, Host: "nas", Port: 22, User: "julos", RemotePath: "/b", PasswordFile: passwordFile, KeyFile: keyFile, HostKeyPin: pin, HostKeyFile: Path.Combine(Dir("data"), BackupCopyOptions.HostKeyFileName));

    private static readonly SshHostKey KeyA = new("ssh-ed25519", "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), KeyB = new("ssh-ed25519", "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");

    [Fact]
    public async Task The_sftp_host_key_is_remembered_on_first_use_and_checked_afterwards()
    {
        var o = Sftp(passwordFile: WriteFile("pw", "geheim-wegwerf\n"));
        await using (var first = new SftpBackupTarget(o, NullLogger.Instance, () => new FakeSftp(KeyA))) await first.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(KeyA.Id, File.ReadAllText(o.HostKeyFile!).Trim());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(o.HostKeyFile!));

        await using (var again = new SftpBackupTarget(o, NullLogger.Instance, () => new FakeSftp(KeyA))) await again.OpenAsync(TestContext.Current.CancellationToken);   // gleicher Schluessel: still durch

        // geaenderter Schluessel: Abbruch mit Warnung, beide Fingerabdruecke genannt, nichts uebertragen, Datei bleibt wie sie war
        var changed = new FakeSftp(KeyB);
        await using var bad = new SftpBackupTarget(o, NullLogger.Instance, () => changed);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bad.OpenAsync(TestContext.Current.CancellationToken));
        Assert.Contains(KeyA.Fingerprint, ex.Message); Assert.Contains(KeyB.Fingerprint, ex.Message); Assert.Contains("ACHTUNG", ex.Message);
        Assert.Null(changed.Connected);
        Assert.Equal(KeyA.Id, File.ReadAllText(o.HostKeyFile!).Trim());
    }

    [Fact]
    public async Task A_pinned_sftp_host_key_wins_and_the_password_stays_out_of_messages()
    {
        var pw = WriteFile("pw2", "streng-geheimes-wegwerf-passwort\r\n");
        var fake = new FakeSftp(KeyA);
        await using (var t = new SftpBackupTarget(Sftp(pin: KeyA.Fingerprint, passwordFile: pw), NullLogger.Instance, () => fake)) await t.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("streng-geheimes-wegwerf-passwort", fake.Connected!.Password);                          // Zeilenende abgeschnitten, sonst unveraendert
        Assert.Null(fake.Connected.PrivateKeyFile);
        Assert.False(File.Exists(Sftp(pin: "x").HostKeyFile!));                                              // mit festem Schluessel wird nichts gemerkt

        var o = Sftp(pin: KeyB.Id, passwordFile: pw);                                                        // "Algorithmus Fingerabdruck" geht auch
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var t = new SftpBackupTarget(o, NullLogger.Instance, () => new FakeSftp(KeyA)); await t.OpenAsync(TestContext.Current.CancellationToken); });
        Assert.DoesNotContain("streng-geheimes", ex.Message);
        Assert.DoesNotContain("streng-geheimes", o.Describe());
        Assert.DoesNotContain("streng-geheimes", o.ToString());                                              // auch nicht in der Textform der Einstellungen

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var t = new SftpBackupTarget(Sftp(passwordFile: Path.Combine(_root, "nope")), NullLogger.Instance, () => new FakeSftp(KeyA)); await t.OpenAsync(TestContext.Current.CancellationToken); });
        Assert.Contains("lässt sich nicht lesen", missing.Message);
    }

    [Fact]
    public async Task The_sftp_target_works_through_the_service_with_the_fake_and_reports_a_changed_host_key_as_status()
    {
        await using var db = await TestDb.CreateAsync();
        var data = Dir("dd"); var local = Dir("ll");
        File.WriteAllText(Path.Combine(data, "secret.key"), "S1\n");
        var o = Sftp(passwordFile: WriteFile("pw3", "x"));
        var files = new Dictionary<string, byte[]>();
        var key = KeyA;
        var time = new ManualTime(new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero));
        var svc = ServiceFor(db, local, data, time, o, () => new SftpBackupTarget(o, NullLogger.Instance, () => new FakeSftp(key, files)));
        await svc.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ok", svc.Status.Copy!.State);
        Assert.Equal(["/b/julos-db-20260301T030000Z.sql.gz", "/b/secret.key"], files.Keys.Order());
        Assert.Equal("sftp://julos@nas/b", svc.Status.Copy.Target);

        key = KeyB; time.Advance(TimeSpan.FromDays(1));
        await svc.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ok", svc.Status.State);
        Assert.Equal("failed", svc.Status.Copy!.State);
        Assert.Contains("Host-Schlüssel", svc.Status.Copy.Message);
        Assert.Equal(2, files.Count);                                                                        // nichts Neues uebertragen
    }

    private string WriteFile(string name, string text) { var p = Path.Combine(Dir("secrets"), name); File.WriteAllText(p, text); return p; }

    // ---- SFTP gegen das Test-SSH-Ziel ----

    [Fact]
    public async Task Real_sftp_target_copies_with_atomic_upload_retention_and_host_key_check()
    {
        var (host, port, password) = RealSshTests.Target();
        await using var db = await TestDb.CreateAsync();
        var remoteDir = "/config/julos-backup-test-" + Guid.NewGuid().ToString("N")[..8];
        using (var admin = new SftpClient(host, port, "tester", password)) { admin.Connect(); admin.CreateDirectory(remoteDir); }
        try
        {
            var data = Dir("rd"); var local = Dir("rl");
            File.WriteAllText(Path.Combine(data, "secret.key"), "S1\n");
            var o = new BackupCopyOptions(BackupCopyMode.Sftp, Host: host, Port: port, User: "tester", RemotePath: remoteDir, PasswordFile: WriteFile("realpw", password + "\n"),
                HostKeyFile: Path.Combine(data, BackupCopyOptions.HostKeyFileName));
            var time = new ManualTime(new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero));
            var svc = ServiceFor(db, local, data, time, o, () => new SftpBackupTarget(o, NullLogger.Instance), keepDays: 2);
            for (var d = 0; d < 4; d++) { await svc.RunAsync(TestContext.Current.CancellationToken); time.Advance(TimeSpan.FromDays(1)); }
            Assert.True(svc.Status.Copy!.State == "ok", svc.Status.Copy.Message);
            Assert.StartsWith("SHA256:", File.ReadAllText(o.HostKeyFile!).Split(' ')[1]);

            using var check = new SftpClient(host, port, "tester", password);
            check.Connect();
            var names = check.ListDirectory(remoteDir).Where(f => f.IsRegularFile).Select(f => f.Name).Order().ToList();
            Assert.Equal(["julos-db-20260302T030000Z.sql.gz", "julos-db-20260303T030000Z.sql.gz", "julos-db-20260304T030000Z.sql.gz", "secret.key"], names);
            Assert.Equal(Names(local, "julos-db-*"), names.Where(n => n.StartsWith("julos-db-")));   // lokal gleiche Aufbewahrung
            Assert.Equal("S1\n", System.Text.Encoding.ASCII.GetString(check.ReadAllBytes(remoteDir + "/secret.key")));
            Assert.All(check.ListDirectory(remoteDir).Where(f => f.IsRegularFile), f => Assert.False(f.OwnerCanExecute || f.GroupCanRead || f.OthersCanRead, f.Name + " ist nicht 0600"));
            Assert.Equal(new FileInfo(Path.Combine(local, "julos-db-20260304T030000Z.sql.gz")).Length, check.GetAttributes(remoteDir + "/julos-db-20260304T030000Z.sql.gz").Size);

            // anderer gemerkter Schluessel: Abbruch, es kommt nichts Neues an
            File.WriteAllText(o.HostKeyFile!, "ssh-ed25519 SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n");
            await svc.RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal("failed", svc.Status.Copy.State);
            Assert.Contains("ACHTUNG", svc.Status.Copy.Message);
            Assert.DoesNotContain(password, svc.Status.Copy.Message);
            Assert.Equal(4, check.ListDirectory(remoteDir).Count(f => f.IsRegularFile));
        }
        finally
        {
            using var admin = new SftpClient(host, port, "tester", password);
            admin.Connect();
            foreach (var f in admin.ListDirectory(remoteDir).Where(f => f.IsRegularFile)) admin.DeleteFile(f.FullName);
            admin.DeleteDirectory(remoteDir);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_sftp_target_logs_in_with_a_private_key_file_with_and_without_passphrase(bool withPassphrase)
    {
        var (host, port, password) = RealSshTests.Target();
        Assert.SkipUnless(File.Exists("/usr/bin/ssh-keygen"), "ssh-keygen fehlt");
        var keyDir = Dir("keys");
        var keyFile = Path.Combine(keyDir, "id_test");
        var passphrase = withPassphrase ? "wegwerf-phrase" : "";
        using (var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/ssh-keygen", ["-q", "-t", "ed25519", "-N", passphrase, "-C", "julos-test", "-f", keyFile]) { RedirectStandardError = true })!)
        {
            await gen.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, gen.ExitCode);
        }
        var pub = File.ReadAllText(keyFile + ".pub");
        const string authorized = "/config/.ssh/authorized_keys";
        using var admin = new SftpClient(host, port, "tester", password);
        admin.Connect();
        var before = admin.ReadAllBytes(authorized);                                           // Wegwerf-Ziel: vorher merken, hinterher zuruecksetzen
        try
        {
            admin.WriteAllBytes(authorized, [.. before, .. System.Text.Encoding.ASCII.GetBytes(pub)]);
            var data = Dir("kd");
            var o = new BackupCopyOptions(BackupCopyMode.Sftp, Host: host, Port: port, User: "tester", RemotePath: "/config", KeyFile: keyFile,
                KeyPassphraseFile: withPassphrase ? WriteFile("phrase", passphrase + "\n") : null, HostKeyFile: Path.Combine(data, BackupCopyOptions.HostKeyFileName));
            await using var t = new SftpBackupTarget(o, NullLogger.Instance);
            await t.OpenAsync(TestContext.Current.CancellationToken);                          // Anmeldung nur mit dem Schluessel (kein Passwort in den Einstellungen)
            await t.PutAsync("julos-key-test.bin", new MemoryStream([9, 8, 7]), TestContext.Current.CancellationToken);
            Assert.Equal([9, 8, 7], await t.ReadAsync("julos-key-test.bin", TestContext.Current.CancellationToken));
            await t.DeleteAsync("julos-key-test.bin", TestContext.Current.CancellationToken);
            Assert.Null(await t.ReadAsync("julos-key-test.bin", TestContext.Current.CancellationToken));

            if (withPassphrase)                                                                // falsche Passphrase: klare Meldung
            {
                var wrong = o with { KeyPassphraseFile = WriteFile("phrase2", "falsch") };
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var w = new SftpBackupTarget(wrong, NullLogger.Instance); await w.OpenAsync(TestContext.Current.CancellationToken); });
                Assert.Contains("Schlüssel", ex.Message);
            }
        }
        finally { admin.WriteAllBytes(authorized, before); }
    }

    [Fact]
    public async Task Real_sftp_target_fails_with_a_clear_message_for_a_wrong_password_and_a_missing_folder()
    {
        var (host, port, password) = RealSshTests.Target();
        var data = Dir("rd2");
        BackupCopyOptions O(string pw, string path) => new(BackupCopyMode.Sftp, Host: host, Port: port, User: "tester", RemotePath: path, PasswordFile: WriteFile("pw" + pw.Length + path.Length, pw), HostKeyFile: Path.Combine(data, BackupCopyOptions.HostKeyFileName));
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var t = new SftpBackupTarget(O("falsch-" + password, "/config"), NullLogger.Instance); await t.OpenAsync(TestContext.Current.CancellationToken); });
        Assert.Contains("Anmeldung abgelehnt", bad.Message);
        Assert.DoesNotContain(password, bad.Message);
        var nodir = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var t = new SftpBackupTarget(O(password, "/config/gibt-es-nicht"), NullLogger.Instance); await t.OpenAsync(TestContext.Current.CancellationToken); });
        Assert.Contains("gibt es am SFTP-Ziel nicht", nodir.Message);
    }
}
