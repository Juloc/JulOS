using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

/// <summary>Die Dateien: nichts ausserhalb der Wurzel, egal ueber welchen Weg (Pfad, Symlink, Kette, Schleife).</summary>
public sealed class FileBrowserTests : IDisposable
{
    // base/files = Wurzel, base/outside = ausserhalb, base/files-secret = Geschwister mit gleichem Namensanfang
    private readonly DirectoryInfo _base = Directory.CreateTempSubdirectory("julos-files-");
    private readonly string _root, _outside, _sibling;

    public FileBrowserTests()
    {
        _root = Dir("files"); _outside = Dir("outside"); _sibling = Dir("files-secret");
        File.WriteAllText(Path.Combine(_outside, "geheim.txt"), "GEHEIM");
        File.WriteAllText(Path.Combine(_sibling, "geheim.txt"), "GEHEIM");
        Directory.CreateDirectory(Path.Combine(_root, "Media"));
        Directory.CreateDirectory(Path.Combine(_root, "Projects", "alt"));
        Directory.CreateDirectory(Path.Combine(_root, ".agent"));
        File.WriteAllText(Path.Combine(_root, "notiz.txt"), "hallo");
        File.WriteAllText(Path.Combine(_root, ".env"), "SCHLUESSEL=1");
        File.WriteAllText(Path.Combine(_root, ".agent", "agent.sock"), "x");
        File.WriteAllText(Path.Combine(_root, "Media", "Foto.JPG"), "bild");
        File.WriteAllText(Path.Combine(_root, "Projects", "alt", "Foto-alt.png"), "bild2");
        File.WriteAllText(Path.Combine(_root, "Projects", "plan.md"), "plan");
    }

    private string Dir(string name) { var p = Path.Combine(_base.FullName, name); Directory.CreateDirectory(p); return p; }
    public void Dispose() => _base.Delete(true);

    private FileBrowser Browser() => new(_root);
    private static string Text(FileStream? s) { using (s) return s is null ? "<null>" : new StreamReader(s).ReadToEnd(); }

    [Fact]
    public void Listing_shows_folders_first_hides_dot_names_and_counts_items()
    {
        var l = Browser().List("")!;
        Assert.Equal(["Media", "Projects", "notiz.txt"], l.Entries.Select(e => e.Name));
        Assert.True(l.Entries[0].IsDirectory);
        Assert.Equal(1, l.Entries[0].Items);
        Assert.Equal(2, l.Entries[1].Items);
        Assert.Equal(5, l.Entries[2].Size);
        Assert.Equal("Projects", l.Entries[1].Path);
        var sub = Browser().List("Projects/")!;
        Assert.Equal("Projects", sub.Path);
        Assert.Equal("Projects/alt", sub.Entries[0].Path);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("Media/../../outside")]
    [InlineData("Media/..")]
    [InlineData("./../outside")]
    [InlineData(".agent")]
    [InlineData(".env")]
    [InlineData(".agent/agent.sock")]
    [InlineData("Media/.hidden")]
    [InlineData("Media\0/x")]
    public void Dangerous_or_hidden_paths_are_refused(string path)
    {
        var b = Browser();
        Assert.Null(b.List(path));
        Assert.Null(b.Resolve(path));
        Assert.Null(b.OpenRead(path));
        Assert.Null(b.Search(path, "a"));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("//etc//passwd")]
    [InlineData("%2e%2e/outside/geheim.txt")]
    [InlineData("..%2foutside")]
    [InlineData("Media\\..\\..\\outside")]
    public void Odd_spellings_are_just_names_below_the_root_and_never_reach_outside(string path)
    {
        var b = Browser();
        Assert.Null(b.OpenRead(path));
        Assert.Null(b.List(path));
    }

    [Fact]
    public void A_leading_slash_means_the_root_not_the_file_system()
    {
        Assert.Contains(Browser().List("/")!.Entries, e => e.Name == "notiz.txt");
        Assert.Equal("hallo", Text(Browser().OpenRead("/notiz.txt")));
    }

    [Fact]
    public void Too_long_or_too_deep_paths_are_refused()
    {
        var b = Browser();
        Assert.Null(b.List(new string('a', 5000)));
        Assert.Null(b.List(string.Join('/', Enumerable.Repeat("d", 65))));
    }

    [Fact]
    public void A_symlink_to_a_file_outside_is_invisible_and_cannot_be_opened()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "raus.txt"), Path.Combine(_outside, "geheim.txt"));
        File.CreateSymbolicLink(Path.Combine(_root, "relativ.txt"), "../outside/geheim.txt");
        var b = Browser();
        Assert.DoesNotContain(b.List("")!.Entries, e => e.Name is "raus.txt" or "relativ.txt");
        Assert.Null(b.OpenRead("raus.txt"));
        Assert.Null(b.OpenRead("relativ.txt"));
        Assert.Null(b.Resolve("raus.txt"));
    }

    [Fact]
    public void A_symlinked_folder_outside_cannot_be_listed_or_walked_into()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "tuer"), _outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "Media", "hoch"), "../../outside");
        Directory.CreateSymbolicLink(Path.Combine(_root, "geschwister"), _sibling);          // gleicher Namensanfang wie die Wurzel
        var b = Browser();
        Assert.DoesNotContain(b.List("")!.Entries, e => e.Name is "tuer" or "geschwister");
        Assert.DoesNotContain(b.List("Media")!.Entries, e => e.Name == "hoch");
        foreach (var p in new[] { "tuer", "tuer/geheim.txt", "Media/hoch/geheim.txt", "geschwister", "geschwister/geheim.txt" })
        {
            Assert.Null(b.List(p));
            Assert.Null(b.OpenRead(p));
        }
        Assert.Empty(b.Search("", "geheim")!.Results);
    }

    [Fact]
    public void A_chain_of_links_is_followed_to_the_end_and_judged_there()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "c"), Path.Combine(_outside, "geheim.txt"));
        File.CreateSymbolicLink(Path.Combine(_root, "b"), "c");
        File.CreateSymbolicLink(Path.Combine(_root, "a"), "b");
        Assert.Null(Browser().OpenRead("a"));
        // Kette, die in der Wurzel bleibt, ist erlaubt
        File.CreateSymbolicLink(Path.Combine(_root, "z2"), "z1");
        File.CreateSymbolicLink(Path.Combine(_root, "z1"), "notiz.txt");
        Assert.Equal("hallo", Text(Browser().OpenRead("z2")));
    }

    [Fact]
    public void A_link_loop_and_a_dangling_link_end_in_refusal_and_not_in_a_hang()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "l1"), "l2");
        File.CreateSymbolicLink(Path.Combine(_root, "l2"), "l1");
        File.CreateSymbolicLink(Path.Combine(_root, "leer"), "gibt-es-nicht");
        var b = Browser();
        Assert.Null(b.OpenRead("l1"));
        Assert.Null(b.List("l1"));
        Assert.Null(b.OpenRead("leer"));
        var names = b.List("")!.Entries.Select(e => e.Name).ToArray();
        Assert.DoesNotContain("l1", names); Assert.DoesNotContain("leer", names);
    }

    [Fact]
    public void A_link_that_stays_inside_the_root_works()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "Abkuerzung"), "Projects");
        File.CreateSymbolicLink(Path.Combine(_root, "Media", "plan-link.md"), "../Projects/plan.md");
        var b = Browser();
        Assert.Contains(b.List("")!.Entries, e => e.Name == "Abkuerzung" && e.IsDirectory);
        Assert.Contains(b.List("Abkuerzung")!.Entries, e => e.Name == "plan.md");
        Assert.Equal("plan", Text(b.OpenRead("Media/plan-link.md")));
        Assert.Equal("plan", Text(b.OpenRead("Abkuerzung/plan.md")));
    }

    [Fact]
    public void A_link_up_out_of_the_root_through_a_subfolder_is_refused()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "Projects", "oben"), "../../outside");
        Directory.CreateSymbolicLink(Path.Combine(_root, "Wurzelhoch"), "..");
        var b = Browser();
        Assert.Null(b.OpenRead("Projects/oben/geheim.txt"));
        Assert.Null(b.List("Wurzelhoch"));
        Assert.Null(b.OpenRead("Wurzelhoch/outside/geheim.txt"));
    }

    [Fact]
    public void The_root_itself_may_be_a_symlink()
    {
        var link = Path.Combine(_base.FullName, "wurzel-link");
        Directory.CreateSymbolicLink(link, _root);
        var b = new FileBrowser(link);
        Assert.Contains(b.List("")!.Entries, e => e.Name == "notiz.txt");
        Assert.Equal("hallo", Text(b.OpenRead("notiz.txt")));
        Directory.CreateSymbolicLink(Path.Combine(_root, "Media", "zurueck"), "../../files-secret");
        Assert.Null(b.OpenRead("Media/zurueck/geheim.txt"));
    }

    [Fact]
    public void Download_gives_the_bytes_and_refuses_folders_and_missing_files()
    {
        var b = Browser();
        Assert.Equal("bild", Text(b.OpenRead("Media/Foto.JPG")));
        Assert.Null(b.OpenRead("Media"));
        Assert.Null(b.OpenRead("gibt-es-nicht.txt"));
        Assert.Null(b.OpenRead(""));
    }

    [Fact]
    public void Search_finds_names_in_subfolders_ignoring_case_and_skips_hidden_things_and_links()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "Media", "innen"), "../Projects");      // erlaubt, aber nicht durchsucht (keine Schleifen)
        var r = Browser().Search("", "foto")!;
        Assert.Equal(["Media/Foto.JPG", "Projects/alt/Foto-alt.png"], r.Results.Select(x => x.Path));        // nach Pfad sortiert
        Assert.False(r.Truncated);
        Assert.Empty(Browser().Search("", "agent")!.Results);          // .agent ist unsichtbar
        Assert.Empty(Browser().Search("", "env")!.Results);
        Assert.Equal(["Projects/alt/Foto-alt.png"], Browser().Search("Projects", "foto")!.Results.Select(x => x.Path));
        Assert.Null(Browser().Search("", "  "));
        Assert.Null(Browser().Search("", new string('x', 101)));
    }

    [Fact]
    public void Search_stops_at_the_result_limit()
    {
        var many = Dir("files/viele");
        for (var i = 0; i < FileBrowser.MaxResults + 20; i++) File.WriteAllText(Path.Combine(many, $"treffer{i}.txt"), "");
        var r = Browser().Search("", "treffer")!;
        Assert.Equal(FileBrowser.MaxResults, r.Results.Length);
        Assert.True(r.Truncated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("off")]
    [InlineData("OFF")]
    public void Without_a_root_the_files_are_off(string? root)
    {
        var b = new FileBrowser(root);
        Assert.False(b.Enabled);
        Assert.Null(b.List(""));
        Assert.Null(b.OpenRead("notiz.txt"));
    }

    [Fact]
    public void A_missing_root_folder_answers_nothing_instead_of_failing()
    {
        var b = new FileBrowser(Path.Combine(_base.FullName, "nicht-da"));
        Assert.True(b.Enabled);
        Assert.Null(b.List(""));
    }

    [Fact]
    public void Real_path_resolves_links_step_by_step()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "Media", "p"), "../Projects");
        Assert.Equal(Path.Combine(_root, "Projects", "plan.md"), FileBrowser.RealPath(Path.Combine(_root, "Media", "p", "plan.md")));
        Assert.Equal(_root, FileBrowser.RealPath(Path.Combine(_root, "Media", "..")));
    }

    [Fact]
    public async Task A_named_pipe_in_the_folder_is_not_opened_and_does_not_block_a_thread()
    {
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", Path.Combine(_root, "rohr"))) { mkfifo.WaitForExit(); Assert.Equal(0, mkfifo.ExitCode); }
        var open = Task.Run(() => Browser().OpenRead("rohr"));
        // open(2) auf einen FIFO ohne Schreiber wartet sonst fuer immer
        Assert.Same(open, await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Null(await open);
        Assert.Equal("hallo", Text(Browser().OpenRead("notiz.txt")));          // gewoehnliche Dateien gehen weiter
    }
}
