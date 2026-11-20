using System.Text;
using JulOS.Integrations.Files;
using JulOS.Integrations.Ssh;

namespace JulOS.Backend.Tests;

/// <summary>
/// Ein Ziel, das luegt (kompromittierter Host): Namen mit "/" oder "..", falsche Groessen. Darauf darf FileTransfer weder ausserhalb des gemeinten Ordners schreiben oder loeschen
/// (das waere der Weg von einem gekaperten Host auf einen anderen, wie bei scp CVE-2019-6111) noch mehr Daten annehmen, als die Obergrenze erlaubt.
/// </summary>
public sealed class HostileTargetTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Reicht alles an ein Dateisystem im Speicher durch, haengt aber an jede Ordnerliste einen Eintrag mit gefaelschtem Namen und kann Groessen verkleinern.</summary>
    private sealed class Liar(FakeFileSession inner, params string[] fakeNames) : ISftpFileSession
    {
        public readonly List<string> Deleted = [];
        public long? ClaimedSize;
        public SshHostKey? PresentedHostKey => inner.PresentedHostKey;
        public bool IsConnected => inner.IsConnected;
        public string HomeDirectory => inner.HomeDirectory;
        public Task ConnectAsync(SftpConnectOptions o, Func<SshHostKey, bool> t, CancellationToken ct) => inner.ConnectAsync(o, t, ct);
        private RemoteEntry Lie(RemoteEntry e) => ClaimedSize is { } s && e.Kind == RemoteKind.File ? e with { Size = s } : e;
        public async Task<RemoteListing> ListAsync(string directory, int limit, CancellationToken ct)
        {
            var l = await inner.ListAsync(directory, limit, ct);
            var entries = l.Entries.Select(Lie).ToList();
            if (directory == "/src") foreach (var n in fakeNames) entries.Add(new RemoteEntry(n, RemotePath.Join(directory, n), RemoteKind.File, 5, DateTimeOffset.UnixEpoch, "rw-r--r--"));
            return l with { Entries = entries };
        }
        public async Task<RemoteEntry?> StatAsync(string path, bool follow, CancellationToken ct) => await inner.StatAsync(path, follow, ct) is { } e ? Lie(e) : null;
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
            fakeNames.Any(n => path.EndsWith("/" + n)) || path.Contains("..") ? Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("evil"))) : inner.OpenReadAsync(path, ct);
        public Task<Stream> CreateAsync(string path, CancellationToken ct) => inner.CreateAsync(path, ct);
        public Task RenameAsync(string from, string to, bool overwrite, bool fromIsLink, CancellationToken ct) => inner.RenameAsync(from, to, overwrite, fromIsLink, ct);
        public Task DeleteFileAsync(string path, bool isLink, CancellationToken ct) { Deleted.Add(path); return path.Contains("..") ? Task.CompletedTask : inner.DeleteFileAsync(path, isLink, ct); }
        public Task DeleteDirectoryAsync(string path, CancellationToken ct) { Deleted.Add(path); return inner.DeleteDirectoryAsync(path, ct); }
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("sub/inner")]
    public async Task A_listing_name_with_a_slash_never_leaves_the_folder_when_copying_between_hosts(string name)
    {
        var src = new Liar(new FakeFileSession().File("/src/ok.txt", "fine"), name);
        var dst = new FakeFileSession().Dir("/dst").Dir("/etc");
        var before = dst.Nodes.Keys.ToHashSet();

        await FileTransfer.CopyAsync(src, dst, ["/src"], "/dst", false, false, FileLimits.Default, new TransferProgress(), Ct);

        Assert.Equal("fine", dst.Text("/dst/src/ok.txt"));
        var added = dst.Nodes.Keys.Where(k => !before.Contains(k)).ToList();
        Assert.All(added, k => Assert.StartsWith("/dst/src", k));
        Assert.DoesNotContain(added, k => k.Contains("..") || k.Contains("escape") || k.Contains("inner"));
        Assert.DoesNotContain(dst.Log, l => l.Contains(".."));
    }

    [Fact]
    public async Task A_listing_name_with_a_slash_is_never_used_as_a_path_when_deleting_or_counting()
    {
        var src = new Liar(new FakeFileSession().File("/src/ok.txt", "fine"), "../../victim");
        var count = await FileTransfer.CountAsync(src, ["/src"], FileLimits.Default, Ct);
        Assert.Equal(1, count.Files);                                                       // die Datei; der gefaelschte Eintrag zaehlt nicht als Datei

        await FileTransfer.DeleteAsync(src, ["/src"], true, FileLimits.Default, new TransferProgress(), Ct);
        Assert.Contains("/src/ok.txt", src.Deleted);                                         // das Echte wird geloescht
        Assert.DoesNotContain(src.Deleted, p => p.Contains(".."));
    }

    [Fact]
    public async Task A_target_that_lies_about_sizes_cannot_make_a_copy_exceed_the_limit_for_the_whole_operation()
    {
        var a = new FakeFileSession();
        for (var i = 0; i < 3; i++) a.File($"/src/f{i}.bin", new byte[6]);
        var src = new Liar(a) { ClaimedSize = 1 };                                          // sagt 1 Byte je Datei, liefert 6
        var dst = new FakeFileSession().Dir("/dst");
        var limits = FileLimits.Default with { MaxBytes = 10 };

        var e = await Assert.ThrowsAsync<FileRuleException>(() => FileTransfer.CopyAsync(src, dst, ["/src"], "/dst", false, false, limits, new TransferProgress(), Ct));
        Assert.Equal("too_large", e.Code);
        Assert.DoesNotContain(dst.Nodes.Keys, k => k.EndsWith(".part"));                    // aufgeraeumt
    }
}
