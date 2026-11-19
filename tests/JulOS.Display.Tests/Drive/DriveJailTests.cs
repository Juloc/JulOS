using JulOS.Integrations.Display.Rdp.Drive;

namespace JulOS.Display.Tests.Drive;

// The fence around the shared drive: what the remote machine may name, and what never leaves the root. Most of these are attacks, not features.
// Paths come in the Windows form the remote sends (\folder\file). Real folders and real symlinks in a temp directory.
public sealed class DriveJailTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "julos-jail-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string _outside;

    public DriveJailTests()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the drive is Linux only");
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(Path.Combine(_root, ".ssh"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "sub", "inner.txt"), "inner");
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "TOP SECRET");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link_out"), _outside);                     // out of the root
        Directory.CreateSymbolicLink(Path.Combine(_root, "link_in"), Path.Combine(_root, "sub"));    // into the root: refused as well
        File.CreateSymbolicLink(Path.Combine(_root, "file_link"), Path.Combine(_root, "hello.txt"));
        File.CreateSymbolicLink(Path.Combine(_root, "sub", "up"), "../../outside");                  // relative target that leaves the root
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); }
        catch (IOException) { /* egal */ }
    }

    private DriveJail Jail() => DriveJail.TryCreate(_root, out _)!;

    // ---- accepted ----

    [Theory]
    [InlineData("")]
    [InlineData("\\")]
    [InlineData("\\hello.txt")]
    [InlineData("\\sub\\inner.txt")]
    [InlineData("\\sub\\")]
    [InlineData("\\new name with spaces.txt")]
    [InlineData("\\Ärger-größe.txt")]
    [InlineData("\\日本語.txt")]
    [InlineData("\\😀.txt")]
    [InlineData("\\e\u0301.txt")]                    // e + combining acute (NFD)
    [InlineData("\\a,b;c=d!e#f$g%h&i'j(k)l[m]n{o}p^q`r~s+t@u.v")]
    public void Normal_paths_are_accepted(string path) => Assert.NotNull(DriveJail.Segments(path));

    [Fact]
    public void The_root_is_an_empty_list_of_names_and_a_trailing_backslash_is_ignored()
    {
        Assert.Empty(DriveJail.Segments("")!);
        Assert.Empty(DriveJail.Segments("\\")!);
        Assert.Equal(["sub"], DriveJail.Segments("\\sub\\")!);
        Assert.Equal(["sub", "inner.txt"], DriveJail.Segments("\\sub\\inner.txt")!);
    }

    // ---- refused: ways out and ways in ----

    [Theory]
    [InlineData("\\..")]
    [InlineData("\\..\\")]
    [InlineData("\\..\\outside\\secret.txt")]
    [InlineData("\\sub\\..\\..\\outside")]
    [InlineData("\\sub\\..\\hello.txt")]                  // even a ".." that stays inside is not ours to resolve
    [InlineData("\\.")]
    [InlineData("\\.\\hello.txt")]
    [InlineData("\\sub\\.\\inner.txt")]
    [InlineData("\\...")]
    [InlineData("\\....")]
    [InlineData("\\. .")]
    [InlineData("\\.. ")]
    [InlineData("\\..\u200B")]
    public void Dot_names_are_refused(string path) => Assert.Null(DriveJail.Segments(path));

    [Theory]
    [InlineData("hello.txt")]                              // relative
    [InlineData("sub\\inner.txt")]
    [InlineData("C:\\Windows\\win.ini")]                  // a drive letter
    [InlineData("c:hello.txt")]
    [InlineData("\\\\server\\share\\x")]                  // UNC
    [InlineData("\\\\?\\C:\\x")]
    [InlineData("\\\\.\\PhysicalDrive0")]
    [InlineData("/etc/passwd")]                            // a Unix path
    [InlineData("\\/etc/passwd")]
    [InlineData("\\sub/inner.txt")]
    [InlineData("\\sub/../../outside/secret.txt")]
    [InlineData("\\sub\\\\inner.txt")]                    // an empty part
    [InlineData("\\\\")]
    public void Absolute_relative_and_foreign_forms_are_refused(string path) => Assert.Null(DriveJail.Segments(path));

    [Theory]
    [InlineData("\\.env")]                                  // hidden names: .env, .ssh, .git ...
    [InlineData("\\.ssh\\id_rsa")]
    [InlineData("\\sub\\.hidden")]
    [InlineData("\\.git")]
    [InlineData("\\name.")]                                 // what Windows would cut off
    [InlineData("\\name ")]
    [InlineData("\\name...")]
    public void Hidden_names_and_names_windows_would_change_are_refused(string path) => Assert.Null(DriveJail.Segments(path));

    [Theory]
    [InlineData("\\a:b")]                                   // NTFS alternate data stream
    [InlineData("\\hello.txt:Zone.Identifier")]
    [InlineData("\\hello.txt::$DATA")]
    [InlineData("\\a*")]
    [InlineData("\\a?")]
    [InlineData("\\a<b")]
    [InlineData("\\a>b")]
    [InlineData("\\a|b")]
    [InlineData("\\a\"b")]
    [InlineData("\\a\0b")]                                  // a NUL in the middle (the channel strips the terminator before)
    [InlineData("\\a\u0001b")]                              // control characters
    [InlineData("\\a\nb")]
    [InlineData("\\a\tb")]
    [InlineData("\\a\u007Fb")]
    [InlineData("\\a\u0085b")]                              // NEL, a C1 control
    public void Reserved_characters_and_control_characters_are_refused(string path) => Assert.Null(DriveJail.Segments(path));

    [Theory]
    [InlineData("\\gpj.\u202Etxt")]                         // right-to-left override: "gpj.txt" shown as "txt.jpg"
    [InlineData("\\a\u202Db")]
    [InlineData("\\a\u2066b\u2069")]                        // isolates
    [InlineData("\\a\u200Eb")]                              // left-to-right mark
    [InlineData("\\a\u061Cb")]                              // arabic letter mark
    [InlineData("\\a\u200Bb")]                              // zero width space
    [InlineData("\\a\u200Cb")]
    [InlineData("\\a\u200Db")]
    [InlineData("\\a\u2060b")]                              // word joiner
    [InlineData("\\\uFEFFa")]                               // byte order mark
    [InlineData("\\a\u00ADb")]                              // soft hyphen
    [InlineData("\\a\u2028b")]                              // line separator
    [InlineData("\\a\u2029b")]                              // paragraph separator
    [InlineData("\\a\uE000b")]                              // private use
    [InlineData("\\a\u0378b")]                              // not assigned
    [InlineData("\\a\uD800")]                               // a lone high surrogate
    [InlineData("\\a\uDC00b")]                              // a lone low surrogate
    [InlineData("\\a\uD800\u0041")]                         // a high surrogate without its pair
    public void Format_direction_and_broken_unicode_is_refused(string path) => Assert.Null(DriveJail.Segments(path));

    [Fact]
    public void Broken_utf16_is_refused()
    {
        // built here, not in InlineData: the test framework would turn a lone surrogate into U+FFFD on its way
        foreach (var broken in new[] { "\\a\uD800", "\\a\uDC00b", "\\a\uD800A", "\\\uDBFF", "\\a\uFFFDb", "\\a\uFFFE", "\\a\uFFFF" })
        {
            Assert.Null(DriveJail.Segments(broken));
        }

        Assert.NotNull(DriveJail.Segments("\\a\uD83D\uDE00b"));                // a proper pair is fine
    }

    [Theory]
    [InlineData("\\\uFF0E\uFF0E")]                          // two fullwidth full stops: ".." after NFKC
    [InlineData("\\\uFF0E\uFF0E\\x")]
    [InlineData("\\\u2024\u2024")]                          // two one dot leaders
    [InlineData("\\\uFE52\uFE52")]                          // small full stops
    [InlineData("\\a\uFF0Fb")]                              // fullwidth solidus: "/" after NFKC
    [InlineData("\\a\u2215b")]                              // division slash
    [InlineData("\\a\u2044b")]                              // fraction slash
    [InlineData("\\a\u29F8b")]                              // big solidus
    [InlineData("\\a\uFF3Cb")]                              // fullwidth reverse solidus
    [InlineData("\\a\u2216b")]                              // set minus
    [InlineData("\\a\uFF1Ab")]                              // fullwidth colon
    [InlineData("\\\uFF0Eenv")]                             // ".env" after NFKC: a hidden name in disguise
    [InlineData("\\\uFE68etc")]                             // small reverse solidus
    public void Lookalike_characters_are_refused_even_when_they_only_become_dangerous_after_normalisation(string path) => Assert.Null(DriveJail.Segments(path));

    [Fact]
    public void Too_long_and_too_deep_is_refused()
    {
        Assert.NotNull(DriveJail.Segments("\\" + new string('a', 255)));
        Assert.Null(DriveJail.Segments("\\" + new string('a', 256)));
        Assert.Null(DriveJail.Segments("\\" + new string('ä', 200)));              // 400 bytes of UTF-8 in 200 characters
        Assert.Null(DriveJail.Segments("\\" + string.Concat(Enumerable.Repeat("a\\", DriveJail.MaxSegments + 1))));
        Assert.NotNull(DriveJail.Segments("\\" + string.Join('\\', Enumerable.Repeat("a", DriveJail.MaxSegments))));
        Assert.Null(DriveJail.Segments("\\" + string.Join('\\', Enumerable.Repeat("abcdefgh", 200))));      // over 1024 characters in all
        Assert.Null(DriveJail.Segments("\\" + new string('x', 5000)));
    }

    [Fact]
    public void Names_are_never_corrected_only_accepted_or_refused()
    {
        foreach (var path in new[] { "\\..", "\\a\\..", "\\.hidden", "\\a:b", "\\a\u202Eb" })
        {
            Assert.Null(DriveJail.Segments(path));
        }

        Assert.Equal(["Résumé.txt"], DriveJail.Segments("\\Résumé.txt")!);
    }

    // ---- symlinks ----

    [Theory]
    [InlineData("\\link_out")]
    [InlineData("\\link_out\\secret.txt")]
    [InlineData("\\link_in")]                               // a link into the root is refused as well
    [InlineData("\\link_in\\inner.txt")]
    [InlineData("\\file_link")]
    [InlineData("\\sub\\up")]                               // a relative link that leaves the root
    [InlineData("\\sub\\up\\secret.txt")]
    [InlineData("\\link_out\\new.txt")]                     // creating something through a link
    public void A_symlink_anywhere_in_the_existing_part_of_the_path_is_refused(string path) => Assert.Null(Jail().Resolve(path, out _));

    [Fact]
    public void A_path_without_links_resolves_below_the_root_also_when_it_does_not_exist_yet()
    {
        var jail = Jail();
        Assert.Equal(Path.Combine(_root, "hello.txt"), jail.Resolve("\\hello.txt", out _));
        Assert.Equal(Path.Combine(_root, "sub", "inner.txt"), jail.Resolve("\\sub\\inner.txt", out _));
        Assert.Equal(Path.Combine(_root, "sub", "new.txt"), jail.Resolve("\\sub\\new.txt", out _));
        Assert.Equal(_root, jail.Resolve("\\", out _));
        Assert.True(jail.Inside(jail.Resolve("\\sub\\new.txt", out _)!));
    }

    [Fact]
    public void A_dangling_link_is_refused_too()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "dangling"), Path.Combine(_base, "nothing"));
        Assert.Null(Jail().Resolve("\\dangling", out _));
    }

    [Fact]
    public void Inside_means_the_root_or_below_not_a_neighbour_with_the_same_beginning()
    {
        var jail = Jail();
        Assert.True(jail.Inside(jail.Root));
        Assert.True(jail.Inside(jail.Root + "/x"));
        Assert.False(jail.Inside(jail.Root + "2"));
        Assert.False(jail.Inside(jail.Root + "2/x"));
        Assert.False(jail.Inside(_outside));
        Assert.False(jail.Inside("/"));
        Assert.False(jail.Inside(""));
    }

    // ---- the root itself ----

    [Fact]
    public void A_root_must_be_an_existing_folder_and_not_the_root_of_the_server()
    {
        Assert.Null(DriveJail.TryCreate(null, out var error));
        Assert.Equal("", error);                                                // off is not an error
        Assert.Null(DriveJail.TryCreate("off", out error));
        Assert.Equal("", error);
        Assert.Null(DriveJail.TryCreate("  ", out error));
        Assert.Equal("", error);

        Assert.Null(DriveJail.TryCreate("relative/path", out error));
        Assert.NotEqual("", error);
        Assert.Null(DriveJail.TryCreate("/", out error));
        Assert.NotEqual("", error);
        Assert.Null(DriveJail.TryCreate("/./", out error));                     // the same thing, spelled differently
        Assert.NotEqual("", error);
        Assert.Null(DriveJail.TryCreate(Path.Combine(_base, "missing"), out error));
        Assert.NotEqual("", error);
        Assert.Null(DriveJail.TryCreate(Path.Combine(_root, "hello.txt"), out error));    // a file
        Assert.NotEqual("", error);
        Assert.Null(DriveJail.TryCreate("/tmp/a\0b", out error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void A_root_that_is_a_symlink_is_resolved_once_and_then_fixed()
    {
        var link = Path.Combine(_base, "rootlink");
        Directory.CreateSymbolicLink(link, _root);
        var jail = DriveJail.TryCreate(link, out _)!;
        Assert.Equal(_root, jail.Root);                                        // the real folder, not the link
        Assert.NotNull(jail.Resolve("\\hello.txt", out _));
        Assert.Null(jail.Resolve("\\link_out\\secret.txt", out _));
    }
}
