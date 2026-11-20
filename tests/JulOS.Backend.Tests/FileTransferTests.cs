using System.Text;
using JulOS.Integrations.Files;

namespace JulOS.Backend.Tests;

/// <summary>Die Logik der Dateioperationen auf SFTP (FileTransfer, RemotePath) gegen ein Dateisystem im Speicher: ohne Netz, jeder Fehlerweg.</summary>
public sealed class FileTransferTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static TransferProgress P() => new();

    // ---------- Pfade ----------

    [Theory]
    [InlineData("/a/b", "/a/b")]
    [InlineData("/a//b/./c/", "/a/b/c")]
    [InlineData("/", "/")]
    [InlineData("//", "/")]
    [InlineData("", "/home/u")]
    [InlineData("~", "/home/u")]
    [InlineData("~/x/y", "/home/u/x/y")]
    public void Paths_are_normalized_and_the_home_is_the_start(string input, string expected) => Assert.Equal(expected, RemotePath.Normalize(input, "/home/u"));

    [Theory]
    [InlineData("a/b")]                       // relativ
    [InlineData("/a/../b")]                   // ..
    [InlineData("/..")]
    [InlineData("../x")]
    [InlineData("/a\nb")]                     // Steuerzeichen
    [InlineData("/a\0b")]
    [InlineData("~x")]
    public void Relative_dotdot_and_control_characters_are_refused(string input) => Assert.Null(RemotePath.Normalize(input, "/home/u"));

    [Fact]
    public void Paths_have_length_and_depth_limits_and_no_home_means_no_tilde()
    {
        Assert.Null(RemotePath.Normalize("/" + new string('a', RemotePath.MaxLength), "/h"));
        Assert.Null(RemotePath.Normalize("/" + string.Join('/', Enumerable.Repeat("a", RemotePath.MaxSegments + 1)), "/h"));
        Assert.NotNull(RemotePath.Normalize("/" + string.Join('/', Enumerable.Repeat("a", RemotePath.MaxSegments)), "/h"));
        Assert.Null(RemotePath.Normalize("~/x", null));
        Assert.Null(RemotePath.Normalize("", null));
    }

    [Theory]
    [InlineData("datei.txt", true)]
    [InlineData("Ünïcode ✓", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\0", false)]
    public void New_names_are_one_plain_segment(string name, bool ok) => Assert.Equal(ok, RemotePath.ValidName(name));

    [Fact]
    public void A_name_is_limited_to_255_bytes_not_characters()
    {
        Assert.True(RemotePath.ValidName(new string('a', 255)));
        Assert.False(RemotePath.ValidName(new string('a', 256)));
        Assert.False(RemotePath.ValidName(new string('ä', 128)));            // 256 Byte
    }

    [Fact]
    public void Natural_order_compares_numbers_by_value_and_ignores_case()
    {
        var names = new[] { "b10.txt", "B2.txt", "a", "A1", "b2.txt", "file01", "file1", "file002", "Zeta", "10", "9" };
        Assert.Equal(["9", "10", "a", "A1", "B2.txt", "b2.txt", "b10.txt", "file01", "file1", "file002", "Zeta"], names.OrderBy(n => n, Comparer<string>.Create(RemotePath.NaturalCompare)).ToArray());
        Assert.True(RemotePath.NaturalCompare("x", "x") == 0);
        Assert.True(RemotePath.NaturalCompare("ä", "b") != 0);
    }

    [Fact]
    public void Join_parent_name_and_inside_work_at_the_root_too()
    {
        Assert.Equal("/a", RemotePath.Join("/", "a"));
        Assert.Equal("/a/b", RemotePath.Join("/a", "b"));
        Assert.Equal("/", RemotePath.Parent("/a"));
        Assert.Equal("/a", RemotePath.Parent("/a/b"));
        Assert.Equal("b", RemotePath.NameOf("/a/b"));
        Assert.True(RemotePath.IsInside("/a", "/a/b"));
        Assert.True(RemotePath.IsInside("/a", "/a"));
        Assert.False(RemotePath.IsInside("/a", "/ab"));
        Assert.True(RemotePath.IsInside("/", "/x"));
        Assert.Equal("…" + new string('x', 9), RemotePath.Shorten(new string('y', 100) + new string('x', 9), 10));
    }

    // ---------- Zaehlen ----------

    [Fact]
    public async Task Counting_never_follows_a_link_even_a_loop()
    {
        var fs = new FakeFileSession().Dir("/t").File("/t/a", "12345").Dir("/t/d").File("/t/d/b", "123").Link("/t/loop", "/t").Link("/t/out", "/home");
        var c = await FileTransfer.CountAsync(fs, ["/t"], FileLimits.Default, Ct);
        Assert.Equal((2, 2, 2, 8L), (c.Files, c.Directories, c.Links, c.Bytes));
    }

    [Fact]
    public async Task Counting_stops_at_too_many_items_and_too_deep_and_too_big()
    {
        var fs = new FakeFileSession().Dir("/t");
        for (var i = 0; i < 12; i++) fs.File($"/t/f{i}", "x");
        var e = await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CountAsync(fs, ["/t"], FileLimits.Default with { MaxItems = 10 }, Ct));
        Assert.Equal("too_many_items", e.Code);

        var deep = new FakeFileSession().Dir("/d/1/2/3/4/5");
        Assert.Equal("too_deep", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CountAsync(deep, ["/d"], FileLimits.Default with { MaxDepth = 3 }, Ct))).Code);

        var big = new FakeFileSession().File("/b", new byte[100]);
        Assert.Equal("too_large", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CountAsync(big, ["/b"], FileLimits.Default with { MaxBytes = 99 }, Ct))).Code);
        await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.CountAsync(big, ["/nope"], FileLimits.Default, Ct));
    }

    // ---------- Schreiben ----------

    [Fact]
    public async Task A_file_is_written_under_a_temporary_name_and_renamed_when_complete()
    {
        var fs = new FakeFileSession().Dir("/w");
        var n = await FileTransfer.WriteAtomicAsync(fs, "/w/out.txt", new MemoryStream(Encoding.UTF8.GetBytes("hallo")), false, 1000, 4, null, Ct);
        Assert.Equal(5, n);
        Assert.Equal("hallo", fs.Text("/w/out.txt"));
        var created = fs.Log.Single(l => l.StartsWith("create "));
        Assert.StartsWith("create /w/.julos-", created);
        Assert.EndsWith(".part", created);
        Assert.Equal(["/w/out.txt"], fs.Under("/w").ToArray());                       // kein temporaerer Rest
        Assert.Equal(0, fs.OpenStreams);
        Assert.True(fs.Log.IndexOf(created) < fs.Log.FindIndex(l => l.StartsWith("rename ")));
    }

    [Fact]
    public async Task An_existing_target_needs_overwrite_and_nothing_is_written_before_that()
    {
        var fs = new FakeFileSession().File("/w/out.txt", "alt");
        var e = await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.WriteAtomicAsync(fs, "/w/out.txt", new MemoryStream([1]), false, 1000, 4, null, Ct));
        Assert.Equal(RemoteFileError.Exists, e.Error);
        Assert.DoesNotContain(fs.Log, l => l.StartsWith("create "));
        Assert.Equal("alt", fs.Text("/w/out.txt"));
        await FileTransfer.WriteAtomicAsync(fs, "/w/out.txt", new MemoryStream(Encoding.UTF8.GetBytes("neu")), true, 1000, 4, null, Ct);
        Assert.Equal("neu", fs.Text("/w/out.txt"));
        Assert.Single(fs.Under("/w"));
    }

    [Fact]
    public async Task A_folder_with_the_same_name_is_never_replaced()
    {
        var fs = new FakeFileSession().Dir("/w/x");
        var e = await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.WriteAtomicAsync(fs, "/w/x", new MemoryStream([1]), true, 1000, 4, null, Ct));
        Assert.Equal("type_mismatch", e.Code);
    }

    [Fact]
    public async Task A_failed_or_too_big_write_removes_the_temporary_file_and_leaves_the_old_one()
    {
        var fs = new FakeFileSession().File("/w/out.txt", "alt");
        fs.FailWriteAfter = 3;
        await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.WriteAtomicAsync(fs, "/w/out.txt", new MemoryStream(new byte[10]), true, 1000, 2, null, Ct));
        Assert.Equal(["/w/out.txt"], fs.Under("/w").ToArray());
        Assert.Equal("alt", fs.Text("/w/out.txt"));
        Assert.Equal(0, fs.OpenStreams);

        fs.FailWriteAfter = null;
        var e = await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.WriteAtomicAsync(fs, "/w/out.txt", new MemoryStream(new byte[10]), true, 5, 2, null, Ct));
        Assert.Equal("too_large", e.Code);
        Assert.Equal(["/w/out.txt"], fs.Under("/w").ToArray());
    }

    [Fact]
    public async Task A_cancelled_write_cleans_up_and_a_lost_target_does_not_replace_the_file()
    {
        var fs = new FakeFileSession().File("/w/out.txt", "alt");
        using var cts = new CancellationTokenSource();
        var source = new ChunkStream(new byte[100], () => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileTransfer.WriteAtomicAsync(fs, "/w/out.txt", source, true, 1000, 10, null, cts.Token));
        Assert.Equal(["/w/out.txt"], fs.Under("/w").ToArray());
        Assert.Equal("alt", fs.Text("/w/out.txt"));
    }

    /// <summary>Ein Strom, der nach dem ersten Lesen eine Aktion ausfuehrt (zum Beispiel den Abbruch).</summary>
    private sealed class ChunkStream(byte[] data, Action afterFirst) : Stream
    {
        private int _pos; private bool _first = true;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => data.Length; public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(buffer.Length, 10), data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer); _pos += n;
            if (_first) { _first = false; afterFirst(); }
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return new(Read(buffer.Span)); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------- Kopieren ----------

    private static FakeFileSession Tree() => new FakeFileSession().Dir("/src/proj/sub").File("/src/proj/a.txt", "AAA").File("/src/proj/sub/b.txt", "BBBB").Link("/src/proj/link", "/src/proj/a.txt").Dir("/dst");

    [Fact]
    public async Task Copying_a_folder_between_two_hosts_copies_files_and_folders_but_skips_links()
    {
        var a = Tree(); var b = new FakeFileSession().Dir("/dst");
        var p = P();
        var r = await FileTransfer.CopyAsync(a, b, ["/src/proj"], "/dst", false, false, FileLimits.Default, p, Ct);
        Assert.Equal("AAA", b.Text("/dst/proj/a.txt"));
        Assert.Equal("BBBB", b.Text("/dst/proj/sub/b.txt"));
        Assert.False(b.Has("/dst/proj/link"));                               // nie verfolgt, nicht kopiert
        Assert.Equal((2, 2, 1), (r.Files, r.Directories, r.Skipped));
        Assert.Equal(7, p.BytesDone);
        Assert.Equal(p.ItemsTotal, p.ItemsDone);
        Assert.DoesNotContain(b.Nodes.Keys, k => k.EndsWith(".part"));
        Assert.True(a.Has("/src/proj/a.txt"));                               // die Quelle bleibt
        Assert.Equal(0, a.OpenStreams + b.OpenStreams);
    }

    [Fact]
    public async Task A_link_to_a_file_on_the_top_level_is_copied_as_a_file_a_link_to_a_folder_is_skipped()
    {
        var a = new FakeFileSession().File("/s/real.txt", "REAL").Link("/s/l.txt", "/s/real.txt").Dir("/s/d").Link("/s/ld", "/s/d");
        var b = new FakeFileSession().Dir("/dst");
        var r = await FileTransfer.CopyAsync(a, b, ["/s/l.txt", "/s/ld"], "/dst", false, false, FileLimits.Default, P(), Ct);
        Assert.Equal("REAL", b.Text("/dst/l.txt"));
        Assert.False(b.Has("/dst/ld"));
        Assert.Equal((1, 1), (r.Files, r.Skipped));
    }

    [Fact]
    public async Task Copying_never_overwrites_without_the_flag_top_level_is_an_error_deeper_is_skipped_and_counted()
    {
        var a = Tree(); var b = new FakeFileSession().File("/dst/proj/a.txt", "ALT").Dir("/dst/proj/sub");
        var r = await FileTransfer.CopyAsync(a, b, ["/src/proj"], "/dst", false, false, FileLimits.Default, P(), Ct);       // Ordner werden zusammengefuehrt
        Assert.Equal("ALT", b.Text("/dst/proj/a.txt"));
        Assert.Equal("BBBB", b.Text("/dst/proj/sub/b.txt"));
        Assert.Equal(1, r.SkippedExisting);

        var top = new FakeFileSession().File("/dst/x.txt", "ALT");
        var one = new FakeFileSession().File("/s/x.txt", "NEU");
        var e = await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.CopyAsync(one, top, ["/s/x.txt"], "/dst", false, false, FileLimits.Default, P(), Ct));
        Assert.Equal(RemoteFileError.Exists, e.Error);
        Assert.Equal("ALT", top.Text("/dst/x.txt"));
        await FileTransfer.CopyAsync(one, top, ["/s/x.txt"], "/dst", false, true, FileLimits.Default, P(), Ct);
        Assert.Equal("NEU", top.Text("/dst/x.txt"));
    }

    [Fact]
    public async Task Copying_inside_one_host_refuses_a_folder_into_itself_and_the_same_folder()
    {
        var a = Tree().Dir("/src/other");
        Assert.Equal("into_itself", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CopyAsync(a, a, ["/src/proj"], "/src/proj/sub", true, false, FileLimits.Default, P(), Ct))).Code);
        Assert.Equal("into_itself", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CopyAsync(a, a, ["/src/proj"], "/src/proj", true, false, FileLimits.Default, P(), Ct))).Code);
        Assert.Equal("same_place", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CopyAsync(a, a, ["/src/proj/a.txt"], "/src/proj", true, false, FileLimits.Default, P(), Ct))).Code);
        await FileTransfer.CopyAsync(a, a, ["/src/proj"], "/src/other", true, false, FileLimits.Default, P(), Ct);
        Assert.Equal("BBBB", a.Text("/src/other/proj/sub/b.txt"));
    }

    [Fact]
    public async Task Copying_checks_the_target_and_the_limits_before_it_writes_anything()
    {
        var a = Tree(); var b = new FakeFileSession().File("/notadir", "x");
        Assert.Equal(RemoteFileError.NotFound, (await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.CopyAsync(a, b, ["/src/proj"], "/none", false, false, FileLimits.Default, P(), Ct))).Error);
        Assert.Equal(RemoteFileError.NotADirectory, (await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.CopyAsync(a, b, ["/src/proj"], "/notadir", false, false, FileLimits.Default, P(), Ct))).Error);
        var c = new FakeFileSession().Dir("/dst");
        await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CopyAsync(a, c, ["/src/proj"], "/dst", false, false, FileLimits.Default with { MaxItems = 2 }, P(), Ct));
        Assert.DoesNotContain(c.Log, l => l.StartsWith("create ") || l.StartsWith("mkdir "));
        Assert.Equal(RemoteFileError.NotFound, (await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.CopyAsync(a, c, ["/src/missing"], "/dst", false, false, FileLimits.Default, P(), Ct))).Error);
    }

    [Fact]
    public async Task A_file_over_a_folder_or_a_folder_over_a_file_is_a_type_mismatch()
    {
        var a = new FakeFileSession().File("/s/x", "F").Dir("/s/y").File("/s/y/z", "z");
        var b = new FakeFileSession().Dir("/dst/x").File("/dst/y", "datei");
        Assert.Equal("type_mismatch", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CopyAsync(a, b, ["/s/x"], "/dst", false, true, FileLimits.Default, P(), Ct))).Code);
        Assert.Equal("type_mismatch", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CopyAsync(a, b, ["/s/y"], "/dst", false, true, FileLimits.Default, P(), Ct))).Code);
    }

    [Fact]
    public async Task Cancelling_a_copy_removes_the_temporary_file_and_keeps_what_was_finished()
    {
        var a = new FakeFileSession().File("/s/one", "1").File("/s/two", new byte[5000]);
        var b = new FakeFileSession().Dir("/dst");
        using var cts = new CancellationTokenSource();
        a.BeforeRead = (path, _) => { if (path == "/s/two") cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileTransfer.CopyAsync(a, b, ["/s/one", "/s/two"], "/dst", false, false, FileLimits.Default, P(), cts.Token));
        Assert.Equal("1", b.Text("/dst/one"));
        Assert.False(b.Has("/dst/two"));
        Assert.DoesNotContain(b.Nodes.Keys, k => k.EndsWith(".part"));
        Assert.Equal(0, a.OpenStreams + b.OpenStreams);
    }

    [Fact]
    public async Task A_target_that_breaks_mid_copy_leaves_no_temporary_file_and_no_target()
    {
        var a = new FakeFileSession().File("/s/big", new byte[10_000]);
        var b = new FakeFileSession().Dir("/dst");
        b.FailWriteAfter = 4096;
        await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.CopyAsync(a, b, ["/s/big"], "/dst", false, false, FileLimits.Default with { BufferSize = 1024 }, P(), Ct));
        Assert.Empty(b.Under("/dst"));
        Assert.Equal(0, a.OpenStreams + b.OpenStreams);
    }

    // ---------- Verschieben ----------

    [Fact]
    public async Task Moving_inside_one_host_renames_and_refuses_an_existing_target_without_overwrite()
    {
        var a = Tree().Dir("/dst2").File("/dst2/a.txt", "OLD");
        await FileTransfer.MoveAsync(a, a, ["/src/proj/sub"], "/dst", true, false, FileLimits.Default, P(), Ct);
        Assert.Equal("BBBB", a.Text("/dst/sub/b.txt"));
        Assert.False(a.Has("/src/proj/sub"));
        Assert.Contains(a.Log, l => l == "rename /src/proj/sub /dst/sub");
        Assert.DoesNotContain(a.Log, l => l.StartsWith("create "));                     // kein Kopieren noetig

        Assert.Equal(RemoteFileError.Exists, (await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.MoveAsync(a, a, ["/src/proj/a.txt"], "/dst2", true, false, FileLimits.Default, P(), Ct))).Error);
        Assert.Equal("OLD", a.Text("/dst2/a.txt"));
        await FileTransfer.MoveAsync(a, a, ["/src/proj/a.txt"], "/dst2", true, true, FileLimits.Default, P(), Ct);
        Assert.Equal("AAA", a.Text("/dst2/a.txt"));
        Assert.False(a.Has("/src/proj/a.txt"));
    }

    [Fact]
    public async Task Moving_a_folder_into_itself_or_to_the_same_place_is_refused()
    {
        var a = Tree();
        Assert.Equal("into_itself", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.MoveAsync(a, a, ["/src/proj"], "/src/proj/sub", true, false, FileLimits.Default, P(), Ct))).Code);
        Assert.Equal("same_place", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.MoveAsync(a, a, ["/src/proj/a.txt"], "/src/proj", true, false, FileLimits.Default, P(), Ct))).Code);
        Assert.True(a.Has("/src/proj/sub/b.txt"));
    }

    [Fact]
    public async Task Moving_between_hosts_copies_then_deletes_only_what_arrived_a_link_stays_and_so_does_its_folder()
    {
        var a = Tree(); var b = new FakeFileSession().Dir("/dst");
        var r = await FileTransfer.MoveAsync(a, b, ["/src/proj"], "/dst", false, false, FileLimits.Default, P(), Ct);
        Assert.Equal("BBBB", b.Text("/dst/proj/sub/b.txt"));
        Assert.False(a.Has("/src/proj/a.txt"));
        Assert.False(a.Has("/src/proj/sub"));
        Assert.True(a.Has("/src/proj/link"));                                           // der Link wurde nicht kopiert und darf nicht verloren gehen
        Assert.True(a.Has("/src/proj"));                                                // und sein Ordner bleibt, weil er nicht leer ist
        Assert.Equal(2, r.Files);
    }

    [Fact]
    public async Task A_failed_move_between_hosts_deletes_nothing_it_did_not_copy()
    {
        var a = new FakeFileSession().File("/s/one", "1").File("/s/two", new byte[5000]);
        var b = new FakeFileSession().Dir("/dst");
        b.FailWriteAfter = 2000;
        await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.MoveAsync(a, b, ["/s/one", "/s/two"], "/dst", false, false, FileLimits.Default, P(), Ct));
        Assert.True(a.Has("/s/two"));
        Assert.Equal("1", b.Text("/dst/one"));
        Assert.False(a.Has("/s/one"));                                                  // diese kam an, die zweite nicht
    }

    [Fact]
    public async Task Moving_falls_back_to_copy_and_delete_when_the_rename_fails_across_file_systems()
    {
        var a = new FakeFileSession().File("/s/x", "X").Dir("/dst");
        a.RenameFails = true;
        await FileTransfer.MoveAsync(a, a, ["/s/x"], "/dst", true, false, FileLimits.Default, P(), Ct);
        Assert.Equal("X", a.Text("/dst/x"));
        Assert.False(a.Has("/s/x"));
    }

    // ---------- Loeschen ----------

    [Fact]
    public async Task Deleting_a_file_or_a_link_removes_exactly_that_never_a_links_target()
    {
        var a = new FakeFileSession().File("/s/f", "x").Link("/s/l", "/s/f").Dir("/s/d").File("/s/d/in", "i").Link("/s/ld", "/s/d");
        await FileTransfer.DeleteAsync(a, ["/s/l", "/s/ld"], false, FileLimits.Default, P(), Ct);
        Assert.False(a.Has("/s/l")); Assert.False(a.Has("/s/ld"));
        Assert.True(a.Has("/s/f")); Assert.True(a.Has("/s/d/in"));                       // die Ziele bleiben
    }

    [Fact]
    public async Task A_link_is_renamed_moved_and_never_replaced_as_itself_not_as_its_target()
    {
        var a = new FakeFileSession().File("/s/real.txt", "R").Link("/s/l", "/s/real.txt").Dir("/dst").File("/dst/vorhanden", "V").Link("/dst/zeigt", "/dst/vorhanden");
        await FileTransfer.MoveAsync(a, a, ["/s/l"], "/dst", true, false, FileLimits.Default, P(), Ct);
        Assert.True(a.Nodes["/dst/l"] is FakeFileSession.LinkNode);                          // der Link ist umgezogen, nicht seine Datei
        Assert.Equal("R", a.Text("/s/real.txt"));
        Assert.False(a.Has("/s/l"));
        // Ein Link als Ziel wird nie ersetzt, auch nicht mit overwrite; ein Link als Quelle ersetzt nie etwas
        var e1 = await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.WriteAtomicAsync(a, "/dst/zeigt", new MemoryStream([1]), true, 100, 4, null, Ct));
        Assert.Equal("target_is_link", e1.Code);
        Assert.Equal("V", a.Text("/dst/vorhanden"));
        a.Link("/s/q", "/s/real.txt").File("/dst2/q", "X");
        Assert.Equal("target_is_link", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.MoveAsync(a, a, ["/s/q"], "/dst2", true, true, FileLimits.Default, P(), Ct))).Code);
        Assert.Equal("R", a.Text("/s/real.txt"));
        // Kopieren in einen Ordner, in dem es schon einen Link gleichen Namens gibt: nie hindurchschreiben
        var b = new FakeFileSession().Dir("/d").Link("/d/f", "/elsewhere").File("/elsewhere", "E");
        var src = new FakeFileSession().File("/s/f", "NEU");
        Assert.Equal("target_is_link", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CopyAsync(src, b, ["/s/f"], "/d", false, true, FileLimits.Default, P(), Ct))).Code);
        Assert.Equal("E", b.Text("/elsewhere"));
    }

    [Fact]
    public async Task A_folder_with_content_is_only_deleted_recursively_and_never_through_links()
    {
        var a = new FakeFileSession().Dir("/s/d/sub").File("/s/d/sub/f", "x").Link("/s/d/out", "/keep").Dir("/keep").File("/keep/precious", "p");
        var e = await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.DeleteAsync(a, ["/s/d"], false, FileLimits.Default, P(), Ct));
        Assert.Equal(RemoteFileError.NotEmpty, e.Error);
        Assert.True(a.Has("/s/d/sub/f"));

        var p = P();
        var r = await FileTransfer.DeleteAsync(a, ["/s/d"], true, FileLimits.Default, p, Ct);
        Assert.False(a.Has("/s/d"));
        Assert.True(a.Has("/keep/precious"));                                           // der Link wurde als Link entfernt, nicht durch ihn hindurch
        Assert.Equal((2, 2), (r.Files, r.Directories));
        Assert.Equal(p.ItemsTotal, p.ItemsDone);
    }

    [Fact]
    public async Task An_empty_folder_goes_without_recursive_and_the_root_never()
    {
        var a = new FakeFileSession().Dir("/s/empty");
        await FileTransfer.DeleteAsync(a, ["/s/empty"], false, FileLimits.Default, P(), Ct);
        Assert.False(a.Has("/s/empty"));
        Assert.Equal("root_forbidden", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.DeleteAsync(a, ["/"], true, FileLimits.Default, P(), Ct))).Code);
        Assert.Equal(RemoteFileError.NotFound, (await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.DeleteAsync(a, ["/s/none"], false, FileLimits.Default, P(), Ct))).Error);
    }

    [Fact]
    public async Task Deleting_checks_the_count_before_it_removes_anything()
    {
        var a = new FakeFileSession().Dir("/s/d");
        for (var i = 0; i < 15; i++) a.File($"/s/d/f{i}", "x");
        await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.DeleteAsync(a, ["/s/d"], true, FileLimits.Default with { MaxItems = 10 }, P(), Ct));
        Assert.Equal(15, a.Under("/s/d").Count());
    }

    [Fact]
    public async Task Denied_paths_report_denied_and_stop_the_delete()
    {
        var a = new FakeFileSession().Dir("/s/d").File("/s/d/a", "1").File("/s/d/locked/b", "2");
        a.Denied.Add("/s/d/locked");
        var e = await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.DeleteAsync(a, ["/s/d"], true, FileLimits.Default, P(), Ct));
        Assert.Equal(RemoteFileError.Denied, e.Error);
        Assert.True(a.Has("/s/d/locked/b"));
    }

    // ---------- Vorschau ----------

    [Fact]
    public async Task A_text_preview_returns_text_and_cuts_at_one_megabyte_on_a_character_boundary()
    {
        var a = new FakeFileSession().File("/t.txt", "Grüße\nzwei");
        var small = await FileTransfer.ReadTextAsync(a, "/t.txt", FileLimits.Default, Ct);
        Assert.Equal(("Grüße\nzwei", false), (small.Text, small.Truncated));

        var big = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ä", 700_000)));             // 1,4 MB
        a.File("/big.txt", big);
        var cut = await FileTransfer.ReadTextAsync(a, "/big.txt", FileLimits.Default, Ct);
        Assert.True(cut.Truncated);
        Assert.Equal(big.Length, cut.Size);
        Assert.True(Encoding.UTF8.GetByteCount(cut.Text) <= FileLimits.Default.MaxPreview);
        Assert.All(cut.Text, c => Assert.Equal('ä', c));                                              // nichts zerschnitten
    }

    [Fact]
    public async Task Binary_and_invalid_text_and_non_files_are_not_text()
    {
        var a = new FakeFileSession().File("/bin", [1, 2, 0, 3]).File("/bad", [0xFF, 0xFE, 0x41]).Dir("/d");
        foreach (var p in new[] { "/bin", "/bad", "/d" })
            Assert.Equal("not_text", (await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.ReadTextAsync(a, p, FileLimits.Default, Ct))).Code);
        Assert.Equal(RemoteFileError.NotFound, (await Assert.ThrowsAsync<RemoteFileException>(() => FileTransfer.ReadTextAsync(a, "/none", FileLimits.Default, Ct))).Error);
        Assert.Equal(0, a.OpenStreams);
    }

    [Fact]
    public async Task Html_stays_text_the_preview_never_interprets_it()
    {
        var a = new FakeFileSession().File("/x.html", "<script>alert(1)</script><svg onload=x>");
        Assert.Equal("<script>alert(1)</script><svg onload=x>", (await FileTransfer.ReadTextAsync(a, "/x.html", FileLimits.Default, Ct)).Text);
    }

    [Fact]
    public void Progress_counts_from_several_threads()
    {
        var p = P();
        Parallel.For(0, 1000, _ => { p.AddBytes(3); p.ItemDone(); });
        Assert.Equal((3000L, 1000), (p.BytesDone, p.ItemsDone));
    }
}
