using JulOS.Integrations.Display.Rdp.Drive;

namespace JulOS.Display.Tests.Drive;

// The opening itself, with the path check left out on purpose: whatever got past it (a link swapped in between) must still not open anything outside.
// Both ways: openat2 with RESOLVE_BENEATH, and the way back (O_NOFOLLOW plus the check of where the descriptor really points).
public sealed class JailFsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "julos-fs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string _outside;

    public JailFsTests()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the drive is Linux only");
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "hello");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "TOP SECRET");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link_out"), _outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "link_in"), Path.Combine(_root, "sub"));
        File.CreateSymbolicLink(Path.Combine(_root, "file_link"), Path.Combine(_outside, "secret.txt"));
        File.CreateSymbolicLink(Path.Combine(_root, "file_link_in"), Path.Combine(_root, "hello.txt"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); }
        catch (IOException) { /* egal */ }
    }

    private JailFs Fs(bool fallback)
    {
        var fs = new JailFs(DriveJail.TryCreate(_root, out _)!);
        if (fallback)
        {
            fs.ForceFallback();
        }

        return fs;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_plain_file_and_a_folder_open_and_the_root_opens(bool fallback)
    {
        using var fs = Fs(fallback);
        using (var file = fs.Open(["hello.txt"], false, false, out var errno))
        {
            Assert.NotNull(file);
            Assert.Equal(0, errno);
            Assert.Equal(5, RandomAccess.GetLength(file));
        }

        using (var dir = fs.Open(["sub"], false, false, out _)) Assert.NotNull(dir);
        using (var root = fs.Open([], false, false, out _)) Assert.NotNull(root);
        Assert.False(fallback && fs.UsesOpenat2);
    }

    [Theory]
    [InlineData(false, "link_out", "secret.txt")]                 // a folder that is a link to the outside
    [InlineData(true, "link_out", "secret.txt")]
    [InlineData(false, "link_in", "inner.txt")]                   // a link to a folder inside is not followed either
    [InlineData(true, "link_in", "inner.txt")]
    public void A_link_in_the_middle_of_the_path_is_never_walked(bool fallback, string link, string name)
    {
        File.WriteAllText(Path.Combine(_root, "sub", "inner.txt"), "inner");
        using var fs = Fs(fallback);
        var opened = fs.Open([link, name], false, false, out var errno);
        if (fallback && link == "link_in")
        {
            // The way back only promises "never outside": a link to a folder inside the root may be walked there (the path check before it refuses it)
            Assert.True(opened is null || fs.Jail.Inside(JailFs.ActualPath(opened)!));
            opened?.Dispose();
        }
        else
        {
            Assert.Null(opened);
            Assert.NotEqual(JailFs.Failure.None, JailFs.Classify(errno));
        }

        if (link == "link_out")
        {
            Assert.Null(fs.Open(["link_out", "secret.txt"], true, false, out _));
            Assert.Null(fs.Open(["link_out", "new.txt"], true, true, out _));
            Assert.False(File.Exists(Path.Combine(_outside, "new.txt")), "nothing may be created through the link (or it is removed again)");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_link_at_the_end_is_never_opened(bool fallback)
    {
        using var fs = Fs(fallback);
        Assert.Null(fs.Open(["file_link"], false, false, out _));
        Assert.Null(fs.Open(["file_link"], true, false, out _));
        Assert.Null(fs.Open(["file_link_in"], false, false, out _));
        Assert.Null(fs.Open(["link_out"], false, false, out _));
        Assert.Null(fs.Open(["file_link"], true, true, out _));              // exclusive create does not follow a link either
        Assert.Equal("TOP SECRET", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exclusive_create_makes_a_new_file_and_refuses_an_existing_one(bool fallback)
    {
        using var fs = Fs(fallback);
        using (var created = fs.Open(["made.txt"], true, true, out var errno))
        {
            Assert.NotNull(created);
            RandomAccess.Write(created, "abc"u8, 0);
        }

        Assert.Equal("abc", File.ReadAllText(Path.Combine(_root, "made.txt")));
        Assert.Null(fs.Open(["made.txt"], true, true, out var again));
        Assert.Equal(17, again);                                                // EEXIST
        Assert.Null(fs.Open(["missing.txt"], false, false, out var missing));
        Assert.Equal(JailFs.Failure.NotFound, JailFs.Classify(missing));
    }

    [Fact]
    public void Dot_dot_never_gets_through_openat2()
    {
        using var fs = Fs(false);
        if (!fs.UsesOpenat2)
        {
            return;                         // this kernel or seccomp profile has no openat2: the way back is tested above
        }

        // Names never contain ".." (the jail refuses them first); here they are handed over directly anyway
        Assert.Null(fs.Open(["..", "outside", "secret.txt"], false, false, out var errno));
        Assert.NotEqual(0, errno);
        Assert.Null(fs.Open(["sub", "..", "..", "outside", "secret.txt"], false, false, out _));
    }

    [Fact]
    public void Where_a_descriptor_points_is_what_counts()
    {
        using var fs = Fs(false);
        using var file = fs.Open(["hello.txt"], false, false, out _);
        Assert.Equal(Path.Combine(fs.Jail.Root, "hello.txt"), JailFs.ActualPath(file!));
        File.Delete(Path.Combine(_root, "hello.txt"));
        Assert.Equal(Path.Combine(fs.Jail.Root, "hello.txt"), JailFs.ActualPath(file!));      // a deleted file is still where it was
    }
}
