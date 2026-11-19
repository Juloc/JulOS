using System.Text;
using JulOS.Integrations.Files;
using JulOS.Integrations.Ssh;
using Renci.SshNet;

namespace JulOS.Backend.Tests;

/// <summary>
/// SshNetFileSession und FileTransfer gegen das Test-SSH-Ziel der Test-Instanz (JULOS_TEST_SSH=host:port, JULOS_TEST_TARGET_PASSWORD; deploy/dev/test.sh setzt sie): Liste, Lesen, Schreiben,
/// Umbenennen, Loeschen, Links, Kopieren, Abbruch und die Fehlerfaelle (kein Zugriff, nicht vorhanden, grosser Ordner, falsches Passwort, Host-Schluessel geaendert). Jeder Test arbeitet in einem
/// eigenen Ordner unter dem Home-Verzeichnis und raeumt ihn weg. Ohne die Umgebungsvariablen werden die Tests uebersprungen.
/// </summary>
public sealed class RealSftpFilesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static SftpConnectOptions Options(string? password = null)
    {
        var (host, port, pw) = RealSshTests.Target();
        return new SftpConnectOptions(host, port, "tester", password ?? pw, null, null);
    }

    private static async Task<SshNetFileSession> ConnectAsync(string? password = null)
    {
        var s = new SshNetFileSession();
        try { await s.ConnectAsync(Options(password), _ => true, Ct); }
        catch { await s.DisposeAsync(); throw; }
        return s;
    }

    /// <summary>Ein Arbeitsordner, der am Ende samt Inhalt verschwindet (ueber ein eigenes "rm -rf" des SSH-Benutzers).</summary>
    private sealed class Work : IAsyncDisposable
    {
        public SshNetFileSession Session { get; }
        public string Dir { get; }
        private Work(SshNetFileSession s, string dir) { Session = s; Dir = dir; }
        public static async Task<Work> CreateAsync()
        {
            var s = await ConnectAsync();
            var dir = RemotePath.Join(s.HomeDirectory, "julos-test-" + Guid.NewGuid().ToString("N")[..10]);
            await s.CreateDirectoryAsync(dir, Ct);
            return new Work(s, dir);
        }
        public string P(string rel) => Dir + "/" + rel;
        public async Task PutAsync(string rel, string text) => await FileTransfer.WriteAtomicAsync(Session, P(rel), new MemoryStream(Encoding.UTF8.GetBytes(text)), true, 1 << 20, 4096, null, Ct);
        public async ValueTask DisposeAsync()
        {
            try { Shell($"chmod -R u+rwx '{Dir}' 2>/dev/null; rm -rf '{Dir}'"); } catch (Exception) { /* beste Muehe */ }
            await Session.DisposeAsync();
        }
    }

    /// <summary>Ein Befehl als SSH-Benutzer (Vorbereitung und Pruefung, nie Teil dessen, was getestet wird).</summary>
    private static string Shell(string command)
    {
        var (host, port, pw) = RealSshTests.Target();
        using var ssh = new SshClient(host, port, "tester", pw);
        ssh.Connect();
        var r = ssh.RunCommand(command);
        return r.Result;
    }

    [Fact]
    public async Task Connect_reports_the_host_key_the_home_directory_and_a_wrong_password_is_refused()
    {
        await using var s = await ConnectAsync();
        Assert.True(s.IsConnected);
        Assert.StartsWith("/", s.HomeDirectory);
        Assert.Matches("^SHA256:[A-Za-z0-9+/]{43}$", s.PresentedHostKey!.Fingerprint);
        Assert.StartsWith("ssh-", s.PresentedHostKey.Algorithm);
        var e = await Assert.ThrowsAsync<SshException>(() => ConnectAsync("falsches-passwort"));
        Assert.Equal(SshFailure.LogonFailed, e.Failure);
        Assert.DoesNotContain("falsches-passwort", e.ToString());
    }

    [Fact]
    public async Task A_host_key_that_changed_stops_the_connection_before_the_logon()
    {
        var stored = new SshHostKey("ssh-ed25519", "SHA256:" + new string('Q', 43));
        SshHostKey? presented = null;
        var s = new SshNetFileSession();
        var e = await Assert.ThrowsAsync<SshException>(() => s.ConnectAsync(Options(), key => { presented = key; return HostKeyPolicy.Decide(stored.Id, null, key) != HostKeyDecision.Changed; }, Ct));
        Assert.Equal(SshFailure.HostKeyRejected, e.Failure);
        Assert.NotNull(presented);
        Assert.NotEqual(stored.Id, presented!.Id);
        Assert.False(s.IsConnected);
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Write_list_stat_read_rename_and_delete_work_on_a_real_target()
    {
        await using var w = await Work.CreateAsync();
        var s = w.Session;
        await s.CreateDirectoryAsync(w.P("ordner"), Ct);
        await w.PutAsync("a.txt", "Grüße vom Test\nzweite Zeile");
        await w.PutAsync("ordner/b.txt", "B");

        var list = await s.ListAsync(w.Dir, 100, Ct);
        Assert.False(list.Truncated);
        var a = Assert.Single(list.Entries, e => e.Name == "a.txt");
        Assert.Equal((RemoteKind.File, (long)Encoding.UTF8.GetByteCount("Grüße vom Test\nzweite Zeile")), (a.Kind, a.Size));
        Assert.Equal(w.P("a.txt"), a.Path);
        Assert.Matches("^[r-][w-][x-][r-][w-][x-][r-][w-][x-]$", a.Mode);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - a.Modified).TotalMinutes) < 30);
        Assert.Equal(RemoteKind.Directory, Assert.Single(list.Entries, e => e.Name == "ordner").Kind);
        Assert.DoesNotContain(list.Entries, e => e.Name.StartsWith(".julos-"));                 // kein temporaerer Rest

        Assert.Null(await s.StatAsync(w.P("gibtsnicht"), false, Ct));
        Assert.Equal(RemoteKind.File, (await s.StatAsync(w.P("a.txt"), true, Ct))!.Kind);
        var text = await FileTransfer.ReadTextAsync(s, w.P("a.txt"), FileLimits.Default, Ct);
        Assert.Equal("Grüße vom Test\nzweite Zeile", text.Text);

        await s.RenameAsync(w.P("a.txt"), w.P("c.txt"), false, false, Ct);
        Assert.Null(await s.StatAsync(w.P("a.txt"), false, Ct));
        await w.PutAsync("d.txt", "D");
        var exists = await Assert.ThrowsAsync<RemoteFileException>(() => s.RenameAsync(w.P("d.txt"), w.P("c.txt"), false, false, Ct));
        Assert.Equal(RemoteFileError.Exists, exists.Error);
        await s.RenameAsync(w.P("d.txt"), w.P("c.txt"), true, false, Ct);                            // ersetzt
        Assert.Equal("D", (await FileTransfer.ReadTextAsync(s, w.P("c.txt"), FileLimits.Default, Ct)).Text);

        var notEmpty = await Assert.ThrowsAsync<RemoteFileException>(() => s.DeleteDirectoryAsync(w.P("ordner"), Ct));
        Assert.Equal(RemoteFileError.NotEmpty, notEmpty.Error);
        await s.DeleteFileAsync(w.P("ordner/b.txt"), false, Ct);
        await s.DeleteDirectoryAsync(w.P("ordner"), Ct);
        Assert.Null(await s.StatAsync(w.P("ordner"), false, Ct));
        var dup = await Assert.ThrowsAsync<RemoteFileException>(() => s.CreateDirectoryAsync(w.Dir, Ct));
        Assert.Equal(RemoteFileError.Exists, dup.Error);
        var dupFile = await Assert.ThrowsAsync<RemoteFileException>(() => s.CreateAsync(w.P("c.txt"), Ct));
        Assert.Equal(RemoteFileError.Exists, dupFile.Error);
    }

    [Fact]
    public async Task Errors_are_translated_not_found_no_access_and_a_binary_file_is_not_text()
    {
        await using var w = await Work.CreateAsync();
        var s = w.Session;
        Assert.Equal(RemoteFileError.NotFound, (await Assert.ThrowsAsync<RemoteFileException>(() => s.ListAsync(w.P("nein"), 10, Ct))).Error);
        Assert.Equal(RemoteFileError.NotFound, (await Assert.ThrowsAsync<RemoteFileException>(() => s.OpenReadAsync(w.P("nein"), Ct))).Error);
        Assert.Equal(RemoteFileError.Denied, (await Assert.ThrowsAsync<RemoteFileException>(() => s.CreateDirectoryAsync("/julos-darf-nicht", Ct))).Error);     // der Benutzer darf im Wurzelverzeichnis nichts anlegen
        Assert.Equal(RemoteFileError.Denied, (await Assert.ThrowsAsync<RemoteFileException>(() => s.ListAsync("/root", 10, Ct))).Error);
        await w.PutAsync("geheim.txt", "x");
        Shell($"chmod 000 '{w.P("geheim.txt")}'");
        Assert.Equal(RemoteFileError.Denied, (await Assert.ThrowsAsync<RemoteFileException>(async () => { await using var _ = await s.OpenReadAsync(w.P("geheim.txt"), Ct); })).Error);
        Shell($"head -c 100 /dev/urandom > '{w.P("zufall.bin")}'; printf 'a\\0b' > '{w.P("nul.bin")}'");
        Assert.Equal("not_text", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.ReadTextAsync(s, w.P("nul.bin"), FileLimits.Default, Ct))).Code);
        Assert.Equal("not_text", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.ReadTextAsync(s, w.Dir, FileLimits.Default, Ct))).Code);
    }

    [Fact]
    public async Task A_large_folder_is_cut_at_the_limit_and_names_with_control_characters_are_hidden()
    {
        await using var w = await Work.CreateAsync();
        Shell($"cd '{w.Dir}' && for i in $(seq 1 350); do : > f$i.txt; done; : > \"$(printf 'bad\\nname')\"");
        var cut = await w.Session.ListAsync(w.Dir, 100, Ct);
        Assert.Equal((100, true), (cut.Entries.Count, cut.Truncated));
        var all = await w.Session.ListAsync(w.Dir, 1000, Ct);
        Assert.Equal((350, false, 1), (all.Entries.Count, all.Truncated, all.Hidden));
        Assert.Equal(1, (await w.Session.ListAsync(w.Dir, 1000, Ct)).Hidden);
    }

    [Fact]
    public async Task Links_are_shown_and_never_followed_when_deleting_or_copying()
    {
        await using var w = await Work.CreateAsync();
        var s = w.Session;
        Shell($"cd '{w.Dir}' && mkdir -p tree/sub outside && echo precious > outside/wichtig.txt && echo F > tree/f.txt && echo S > tree/sub/s.txt && ln -s ../outside tree/zum-ordner && ln -s f.txt tree/zur-datei && ln -s /nonexistent tree/kaputt && ln -s . tree/schleife");
        var list = (await s.ListAsync(w.P("tree"), 100, Ct)).Entries.ToDictionary(e => e.Name);
        Assert.Equal((RemoteKind.Link, RemoteKind.Directory), (list["zum-ordner"].Kind, list["zum-ordner"].Resolved));
        Assert.Equal((RemoteKind.Link, RemoteKind.File), (list["zur-datei"].Kind, list["zur-datei"].Resolved));
        Assert.Null(list["kaputt"].Resolved);
        Assert.True(list["zum-ordner"].IsOpenable);
        Assert.False(list["kaputt"].IsOpenable);
        Assert.Equal(RemoteKind.Link, (await s.StatAsync(w.P("tree/schleife"), false, Ct))!.Kind);

        var count = await FileTransfer.CountAsync(s, [w.P("tree")], FileLimits.Default, Ct);
        Assert.Equal((2, 2, 4), (count.Files, count.Directories, count.Links));

        // Kopieren: Links werden uebersprungen, nichts ausserhalb wird mitkopiert
        var copied = await FileTransfer.CopyAsync(s, s, [w.P("tree")], w.P("outside"), true, false, FileLimits.Default, new TransferProgress(), Ct);
        Assert.Equal((2, 4), (copied.Files, copied.Skipped));
        Assert.Equal("S\n", Shell($"cat '{w.P("outside/tree/sub/s.txt")}'"));
        Assert.Equal("", Shell($"ls '{w.P("outside/tree")}' | grep -c 'zum-ordner\\|kaputt\\|schleife' | grep -v '^0$'"));

        // Loeschen: die Links verschwinden als Links, ihre Ziele bleiben
        await FileTransfer.DeleteAsync(s, [w.P("tree")], true, FileLimits.Default, new TransferProgress(), Ct);
        Assert.Null(await s.StatAsync(w.P("tree"), false, Ct));
        Assert.Equal("precious\n", Shell($"cat '{w.P("outside/wichtig.txt")}'"));
    }

    [Fact]
    public async Task Deleting_or_renaming_a_link_touches_the_link_and_never_its_target_on_a_real_target()
    {
        await using var w = await Work.CreateAsync();
        var s = w.Session;
        Shell($"cd '{w.Dir}' && echo F > f.txt && echo G > g.txt && mkdir dir && echo in > dir/in.txt && ln -s f.txt l1 && ln -s f.txt l2 && ln -s dir ld && ln -s /nonexistent broken && ln -s g.txt zielvorhanden");
        var lstat = await s.StatAsync(w.P("l1"), false, Ct);
        Assert.Equal((RemoteKind.Link, RemoteKind.File), (lstat!.Kind, lstat.Resolved));                // lstat, nicht das Ziel
        Assert.Equal(RemoteKind.Link, (await s.StatAsync(w.P("broken"), false, Ct))!.Kind);             // auch ein kaputter Link ist da
        Assert.Equal(RemoteKind.File, (await s.StatAsync(w.P("l1"), true, Ct))!.Kind);                  // stat folgt
        Assert.Null(await s.StatAsync(w.P("broken"), true, Ct));
        Assert.Null(await s.StatAsync(w.P("gibtsnicht"), false, Ct));

        await s.DeleteFileAsync(w.P("l1"), true, Ct);
        await s.DeleteFileAsync(w.P("ld"), true, Ct);
        await s.DeleteFileAsync(w.P("broken"), true, Ct);
        Assert.Equal("F\n", Shell($"cat '{w.P("f.txt")}'"));                                          // die Ziele sind unberuehrt
        Assert.Equal("in\n", Shell($"cat '{w.P("dir/in.txt")}'"));
        Assert.Equal("", Shell($"cd '{w.Dir}'; ls -A | grep -E '^(l1|ld|broken)$'"));

        await s.RenameAsync(w.P("l2"), w.P("l2neu"), false, true, Ct);
        Assert.Equal("f.txt\n", Shell($"readlink '{w.P("l2neu")}'"));
        Assert.Equal("F\n", Shell($"cat '{w.P("f.txt")}'"));
        Assert.Equal(RemoteFileError.Exists, (await Assert.ThrowsAsync<RemoteFileException>(() => s.RenameAsync(w.P("l2neu"), w.P("g.txt"), false, true, Ct))).Error);
        // Ueber FileTransfer: ein Link als Ziel wird nie durchschrieben
        var e = await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.WriteAtomicAsync(s, w.P("zielvorhanden"), new MemoryStream([65]), true, 100, 4, null, Ct));
        Assert.Equal("target_is_link", e.Code);
        Assert.Equal("G\n", Shell($"cat '{w.P("g.txt")}'"));
        // Ein Verzeichnis mit Link darin: rekursiv loeschen laesst das Ziel des Links stehen
        Shell($"cd '{w.Dir}' && mkdir tree outside && echo keep > outside/k.txt && ln -s ../outside tree/o && echo x > tree/x.txt");
        await FileTransfer.DeleteAsync(s, [w.P("tree")], true, FileLimits.Default, new TransferProgress(), Ct);
        Assert.Equal("keep\n", Shell($"cat '{w.P("outside/k.txt")}'"));
        Assert.Null(await s.StatAsync(w.P("tree"), false, Ct));
    }

    [Fact]
    public async Task Two_real_sessions_copy_and_move_a_tree_with_a_big_file_in_the_stream()
    {
        await using var w = await Work.CreateAsync();
        await using var other = await ConnectAsync();                                         // der "andere Host": eine zweite Sitzung
        Shell($"cd '{w.Dir}' && mkdir -p quelle/inner ziel && head -c 3000000 /dev/urandom > quelle/gross.bin && echo klein > quelle/inner/k.txt && sha256sum quelle/gross.bin > sum.txt");
        var progress = new TransferProgress();
        var r = await FileTransfer.CopyAsync(w.Session, other, [w.P("quelle")], w.P("ziel"), false, false, FileLimits.Default, progress, Ct);
        Assert.Equal((2, 2), (r.Files, r.Directories));
        Assert.Equal(3_000_006, progress.BytesDone);
        Assert.Equal(progress.ItemsTotal, progress.ItemsDone);
        Assert.Equal(Shell($"cd '{w.Dir}' && sha256sum < quelle/gross.bin"), Shell($"cd '{w.Dir}' && sha256sum < ziel/quelle/gross.bin"));
        Assert.Equal("", Shell($"find '{w.Dir}' -name '.julos-*'"));                           // kein temporaerer Rest

        // Ein vorhandenes Ziel ohne overwrite: Fehler vor dem ersten Byte; mit: ersetzt
        var e = await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.CopyAsync(w.Session, other, [w.P("quelle/inner/k.txt")], w.P("ziel/quelle/inner"), false, false, FileLimits.Default, new TransferProgress(), Ct));
        Assert.Equal(RemoteFileError.Exists, e.Error);
        Shell($"echo neu > '{w.P("quelle/inner/k.txt")}'");
        await FileTransfer.CopyAsync(w.Session, other, [w.P("quelle/inner/k.txt")], w.P("ziel/quelle/inner"), false, true, FileLimits.Default, new TransferProgress(), Ct);
        Assert.Equal("neu\n", Shell($"cat '{w.P("ziel/quelle/inner/k.txt")}'"));

        // Verschieben ueber zwei Sitzungen: erst kopiert, dann geloescht
        await FileTransfer.MoveAsync(w.Session, other, [w.P("quelle")], w.P("ziel"), false, true, FileLimits.Default, new TransferProgress(), Ct);
        Assert.Null(await w.Session.StatAsync(w.P("quelle"), false, Ct));
        Assert.Equal("", Shell($"cd '{w.Dir}' && cat sum.txt | sed 's#quelle/#ziel/quelle/#' | sha256sum -c - 2>&1 | grep -v ': OK$'"));

        // Verschieben innerhalb eines Hosts ist ein Umbenennen (ein Aufruf, ohne Kopie)
        Shell($"cd '{w.Dir}' && echo x > eins.txt && mkdir drueber");
        await FileTransfer.MoveAsync(w.Session, w.Session, [w.P("eins.txt")], w.P("drueber"), true, false, FileLimits.Default, new TransferProgress(), Ct);
        Assert.Equal("x\n", Shell($"cat '{w.P("drueber/eins.txt")}'"));
    }

    [Fact]
    public async Task Cancelling_a_big_copy_removes_the_temporary_file_and_the_session_stays_usable()
    {
        await using var w = await Work.CreateAsync();
        await using var other = await ConnectAsync();
        Shell($"cd '{w.Dir}' && mkdir ziel && head -c 40000000 /dev/zero > gross.bin");
        using var cts = new CancellationTokenSource();
        var progress = new TransferProgress();
        var copy = FileTransfer.CopyAsync(w.Session, other, [w.P("gross.bin")], w.P("ziel"), false, false, FileLimits.Default, progress, cts.Token);
        for (var i = 0; i < 400 && progress.BytesDone == 0 && !copy.IsCompleted; i++) await Task.Delay(25);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
        Assert.True(progress.BytesDone < 40_000_000);
        Assert.Equal("", Shell($"ls -A '{w.P("ziel")}'"));                                   // weder Ziel noch temporaerer Rest
        Assert.Equal(RemoteKind.File, (await w.Session.StatAsync(w.P("gross.bin"), false, Ct))!.Kind);
        Assert.True(w.Session.IsConnected && other.IsConnected);
        Assert.NotEmpty((await other.ListAsync(w.Dir, 10, Ct)).Entries);
    }

    [Fact]
    public async Task One_session_serves_a_listing_while_a_download_streams()
    {
        await using var w = await Work.CreateAsync();
        Shell($"cd '{w.Dir}' && head -c 20000000 /dev/urandom > gross.bin");
        await using var stream = await w.Session.OpenReadAsync(w.P("gross.bin"), Ct);
        var buffer = new byte[65536];
        var read = await stream.ReadAsync(buffer, Ct);
        Assert.True(read > 0);
        for (var i = 0; i < 5; i++) Assert.Contains((await w.Session.ListAsync(w.Dir, 10, Ct)).Entries, e => e.Name == "gross.bin");     // zwischen zwei Lesevorgaengen
        long total = read;
        int n;
        while ((n = await stream.ReadAsync(buffer, Ct)) > 0) total += n;
        Assert.Equal(20_000_000, total);
    }

    [Fact]
    public async Task A_dropped_connection_is_reported_as_closed_not_as_a_hang()
    {
        await using var w = await Work.CreateAsync();
        await w.Session.DisposeAsync();
        Assert.False(w.Session.IsConnected);
        var e = await Assert.ThrowsAnyAsync<Exception>(() => w.Session.ListAsync(w.Dir, 10, Ct));
        Assert.True(e is RemoteFileException { Error: RemoteFileError.Closed } || e is InvalidOperationException || e is ObjectDisposedException, e.GetType().Name);
    }
}
