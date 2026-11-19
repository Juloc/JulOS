using System.Diagnostics;
using System.Text.RegularExpressions;
using JulOS.Integrations.Ssh;

namespace JulOS.Backend.Tests;

/// <summary>Programm-Sitzungen (docs/CLI-SESSIONS.md): Pruefung der Felder, Quoting, Kommandozeilen, Pruefbefehl und Plan. Die Zeilen werden auch von einer echten sh ausgefuehrt, mit Attrappen fuer claude und tmux.</summary>
public sealed class SshLaunchTests
{
    private static readonly Guid Id = Guid.Parse("0a1b2c3d-1111-2222-3333-444455556666");

    // ---------------------------------------------------------------- Pruefung

    [Fact]
    public void Defaults_are_a_plain_shell_and_nothing_given_keeps_the_current_values()
    {
        var (v, e) = SshLaunchRules.Validate(null, null, null, null);
        Assert.Null(e);
        Assert.Equal(SshLaunch.Shell, v);
        Assert.True(v!.IsPlain);

        var current = new SshLaunch("claude", "~/x", true, "");
        Assert.Equal(current, SshLaunchRules.Validate(null, null, null, null, current).Value);
        Assert.Equal(current with { WorkDir = "/srv" }, SshLaunchRules.Validate(null, "/srv", null, null, current).Value);
        Assert.False(SshLaunchRules.Validate(null, null, false, null, current).Value!.KeepAlive);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("CLAUDE")]
    [InlineData(" codex ")]
    public void The_four_templates_are_accepted_case_insensitively(string program)
    {
        var (v, e) = SshLaunchRules.Validate(program, null, null, null);
        Assert.Null(e);
        Assert.Equal(program.Trim().ToLowerInvariant(), v!.Program);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("claude; rm -rf /")]
    [InlineData("")]
    [InlineData("claude codex")]
    public void Other_programs_are_refused(string program)
        => Assert.NotNull(SshLaunchRules.Validate(program, null, null, null).Error);

    [Theory]
    [InlineData("~")]
    [InlineData("~/")]
    [InlineData("~/projekte/app")]
    [InlineData("/srv/app-1.2_x")]
    [InlineData("/")]
    [InlineData("projekte/app")]
    [InlineData("a+b@c%d:e,f=g")]
    public void Safe_working_directories_are_accepted(string dir)
    {
        var (v, e) = SshLaunchRules.Validate("claude", dir, null, null);
        Assert.Null(e);
        Assert.Equal(dir, v!.WorkDir);
    }

    [Theory]
    [InlineData("a;b")]
    [InlineData("a`id`")]
    [InlineData("$(id)")]
    [InlineData("a$HOME")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a>b")]
    [InlineData("a<b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\\b")]
    [InlineData("(a)")]
    [InlineData("{a,b}")]
    [InlineData("~root")]
    [InlineData("~/a b")]
    [InlineData("-rf")]
    [InlineData("a~b")]
    [InlineData("a\0b")]
    [InlineData("ä")]
    public void Evil_working_directories_are_refused_and_can_not_be_turned_into_a_command_line(string dir)
    {
        Assert.NotNull(SshLaunchRules.Validate("claude", dir, null, null).Error);
        // auch wer die Pruefung umgeht (ein selbst gebauter Datensatz), bekommt keine Zeile: der Bau prueft noch einmal
        var tampered = new SshLaunch("claude", dir, false, "");
        Assert.Throws<ArgumentException>(() => SshLaunchRules.Line(tampered, false, "n", Id));
        Assert.Throws<ArgumentException>(() => SshLaunchRules.ProbeCommand(tampered));
        Assert.Throws<ArgumentException>(() => SshLaunchRules.DirWord(dir));
    }

    [Fact]
    public void The_working_directory_has_a_length_limit()
    {
        Assert.Null(SshLaunchRules.Validate("shell", "/" + new string('a', SshLaunchRules.MaxWorkDir - 1), null, null).Error);
        Assert.NotNull(SshLaunchRules.Validate("shell", "/" + new string('a', SshLaunchRules.MaxWorkDir), null, null).Error);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u001b[2Jb")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    public void A_custom_command_is_one_line_without_control_characters(string command)
        => Assert.NotNull(SshLaunchRules.Validate("custom", null, null, command).Error);

    [Fact]
    public void A_custom_command_is_free_text_but_needed_for_custom_and_dropped_for_the_others()
    {
        Assert.NotNull(SshLaunchRules.Validate("custom", null, null, "").Error);
        Assert.NotNull(SshLaunchRules.Validate("custom", null, null, "   ").Error);
        var (v, e) = SshLaunchRules.Validate("custom", null, null, "  cd ~/x && claude --resume 'a b'; echo \"$HOME\" `date` $(id)  ");
        Assert.Null(e);
        Assert.Equal("cd ~/x && claude --resume 'a b'; echo \"$HOME\" `date` $(id)", v!.Custom);
        Assert.Equal("", SshLaunchRules.Validate("claude", null, null, "rm -rf /").Value!.Custom);          // gehoert nicht zu Claude: weg
        Assert.NotNull(SshLaunchRules.Validate("custom", null, null, new string('x', SshLaunchRules.MaxCustom + 1)).Error);
        Assert.Null(SshLaunchRules.Validate("custom", null, null, new string('x', SshLaunchRules.MaxCustom)).Error);
    }

    // ---------------------------------------------------------------- Quoting

    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("", "''")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("'", "''\\'''")]
    [InlineData("a;b", "'a;b'")]
    [InlineData("$(id) `id` \"x\"", "'$(id) `id` \"x\"'")]
    public void Quote_wraps_in_single_quotes_and_escapes_single_quotes(string input, string expected)
        => Assert.Equal(expected, SshLaunchRules.Quote(input));

    [Fact]
    public void A_home_directory_is_expanded_by_the_shell_everything_else_is_quoted()
    {
        Assert.Equal("\"$HOME\"", SshLaunchRules.DirWord("~"));
        Assert.Equal("\"$HOME\"'/projekte/app'", SshLaunchRules.DirWord("~/projekte/app"));
        Assert.Equal("'/srv/app'", SshLaunchRules.DirWord("/srv/app"));
        Assert.Equal("'rel/pfad'", SshLaunchRules.DirWord("rel/pfad"));
    }

    // ---------------------------------------------------------------- Sitzungsname

    [Fact]
    public void The_session_name_is_made_of_program_name_and_id_and_only_uses_a_to_z_0_to_9_and_hyphens()
    {
        Assert.Equal("julos-claude-mein-server-0a1b2c", SshLaunchRules.SessionName(Id, "Mein Server", "claude"));
        Assert.Equal("julos-codex-0a1b2c", SshLaunchRules.SessionName(Id, "!!!", "codex"));
        Assert.Equal("julos-shell-mueller-0a1b2c", SshLaunchRules.SessionName(Id, "Müller", "shell"));
        Assert.Equal(SshLaunchRules.SessionName(Id, "Mein Server", "claude"), SshLaunchRules.SessionName(Id, "Mein Server", "claude"));              // stabil: so findet new-session -A sie wieder
        Assert.NotEqual(SshLaunchRules.SessionName(Id, "Mein Server", "claude"), SshLaunchRules.SessionName(Guid.NewGuid(), "Mein Server", "claude"));  // gleicher Name, andere Verbindung
        Assert.NotEqual(SshLaunchRules.SessionName(Id, "Mein Server", "claude"), SshLaunchRules.SessionName(Id, "Mein Server", "codex"));
        foreach (var name in new[] { "a;rm -rf /`x`$(y)\n'q\"", "../../etc", "Ünïcödé ☃ 日本語", new string('x', 500), "  ", "-start", "A  B___C" })
        {
            var s = SshLaunchRules.SessionName(Id, name, "claude");
            Assert.Matches("^julos-[a-z0-9-]+$", s);
            Assert.DoesNotContain("--", s);
            Assert.True(s.Length <= 50, s);
        }
        Assert.Equal("julos-shell-0a1b2c", SshLaunchRules.SessionName(Id, "", "unbekannt"));          // unbekanntes Programm: Shell
    }

    // ---------------------------------------------------------------- Zeilen

    private static string Line(SshLaunch l, bool tmux = false, string name = "Server") => SshLaunchRules.Line(l, tmux, name, Id)!;

    [Fact]
    public void A_plain_shell_needs_no_line()
    {
        Assert.Null(SshLaunchRules.Line(SshLaunch.Shell, false, "n", Id));
        Assert.Null(SshLaunchRules.Plan(SshLaunch.Shell, "n", Id, null).Line);
        Assert.Empty(SshLaunchRules.Plan(SshLaunch.Shell, "n", Id, null).Notices);
    }

    [Fact]
    public void Template_lines_use_fixed_programs_and_start_with_a_space_and_end_with_a_newline()
    {
        Assert.Equal(" exec claude\n", Line(new SshLaunch("claude", "", false, "")));
        Assert.Equal(" exec codex\n", Line(new SshLaunch("codex", "", false, "")));
        Assert.Equal(" cd \"$HOME\"'/p/a' && exec claude\n", Line(new SshLaunch("claude", "~/p/a", false, "")));
        Assert.Equal(" cd '/srv/x'\n", Line(new SshLaunch("shell", "/srv/x", false, "")));
        Assert.Equal(" cd '/srv/x' && exec codex\n", Line(new SshLaunch("codex", "/srv/x", false, "")));
    }

    [Fact]
    public void Tmux_lines_attach_if_the_session_exists_and_set_mouse_and_status()
    {
        Assert.Equal(" exec tmux new-session -A -s julos-claude-server-0a1b2c 'claude' \\; set-option mouse on \\; set-option status off\n",
            Line(new SshLaunch("claude", "", true, ""), true));
        Assert.Equal(" cd \"$HOME\"'/w' && exec tmux new-session -A -s julos-shell-server-0a1b2c \\; set-option mouse on \\; set-option status off\n",
            Line(new SshLaunch("shell", "~/w", true, ""), true));
        Assert.Equal(" exec tmux new-session -A -s julos-custom-server-0a1b2c 'a b; c' \\; set-option mouse on \\; set-option status off\n",
            Line(new SshLaunch("custom", "", true, "a b; c"), true));
    }

    [Fact]
    public void A_custom_command_without_tmux_runs_in_its_own_sh_as_one_quoted_argument()
        => Assert.Equal(" exec sh -c 'cd ~/x && claude --resume '\\''a b'\\''; echo \"$HOME\" `date` $(id)'\n",
            Line(new SshLaunch("custom", "", false, "cd ~/x && claude --resume 'a b'; echo \"$HOME\" `date` $(id)")));

    [Fact]
    public void A_line_with_control_characters_in_the_custom_command_is_never_built()
        => Assert.Throws<ArgumentException>(() => Line(new SshLaunch("custom", "", false, "ls\nrm -rf /")));

    [Theory]
    [InlineData("a;b")]
    [InlineData("a`b`")]
    [InlineData("$(b)")]
    [InlineData("a\"b")]
    [InlineData("a'b")]
    [InlineData("a b")]
    [InlineData("&&")]
    public void A_connection_name_with_shell_characters_never_reaches_the_command_line(string name)
    {
        var line = Line(new SshLaunch("claude", "", true, ""), true, name);
        Assert.Matches(@"^ exec tmux new-session -A -s julos-claude(-[a-z0-9-]+)?-0a1b2c 'claude' \\; set-option mouse on \\; set-option status off\n$", line);
    }

    // Die Zeilen werden von einer echten sh ausgefuehrt; "claude" und "tmux" sind Attrappen, die ihre Argumente zeilenweise ausgeben.
    private static (string Dir, string Run) Sandbox()
    {
        var dir = Directory.CreateTempSubdirectory("julos-launch").FullName;
        File.WriteAllText(Path.Combine(dir, "claude"), "#!/bin/sh\necho \"cwd=$(pwd)\"\nfor a in \"$@\"; do echo \"arg=$a\"; done\necho claude-ran\n");
        File.WriteAllText(Path.Combine(dir, "tmux"), "#!/bin/sh\nfor a in \"$@\"; do echo \"tmux-arg=$a\"; done\n");
        Directory.CreateDirectory(Path.Combine(dir, "work", "sub"));
        foreach (var f in new[] { "claude", "tmux" }) if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(dir, f), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (dir, dir);
    }

    private static async Task<(string Out, int Code)> RunShAsync(string dir, string line, string path = "")
    {
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = "/" };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(line);
        psi.Environment["HOME"] = dir;
        psi.Environment["PATH"] = path.Length > 0 ? path : dir + ":/usr/bin:/bin";
        psi.Environment.Remove("SHELL");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        return (await stdout + await stderr, p.ExitCode);
    }

    [Fact]
    public async Task The_lines_run_in_a_real_sh_in_the_right_directory_with_the_right_arguments()
    {
        Assert.SkipUnless(File.Exists("/bin/sh"), "keine /bin/sh");
        var (dir, _) = Sandbox();
        try
        {
            var (o1, c1) = await RunShAsync(dir, Line(new SshLaunch("claude", "~/work/sub", false, "")));
            Assert.Equal(0, c1);
            Assert.Contains("cwd=" + Path.Combine(dir, "work", "sub"), o1);
            Assert.Contains("claude-ran", o1);

            var (o2, _) = await RunShAsync(dir, Line(new SshLaunch("codex", Path.Combine(dir, "work"), false, "")));          // absolut; codex gibt es hier nicht: die Shell meldet es
            Assert.DoesNotContain("claude-ran", o2);

            // tmux: der Befehl kommt als EIN Argument an, die tmux-Befehle sind getrennt
            var name = "Evil;`id`$(id)\nname";
            var (o3, _) = await RunShAsync(dir, Line(new SshLaunch("claude", "~/work", true, ""), true, name));
            var args = o3.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("tmux-arg=")).Select(l => l["tmux-arg=".Length..]).ToList();
            Assert.Equal("new-session", args[0]);
            Assert.Equal(["-A", "-s"], args.GetRange(1, 2));
            Assert.Matches("^julos-claude-[a-z0-9-]+-0a1b2c$", args[3]);
            Assert.Equal(["claude", ";", "set-option", "mouse", "on", ";", "set-option", "status", "off"], args.GetRange(4, args.Count - 4));
            Assert.DoesNotContain("uid=", o3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_hostile_custom_command_stays_one_argument_for_tmux_and_for_sh()
    {
        var (dir, _) = Sandbox();
        try
        {
            const string evil = "echo 'x'; $(touch PWNED) `touch PWNED2` \"y\" \\ end";
            var (o, _) = await RunShAsync(dir, Line(new SshLaunch("custom", "", true, evil), true));
            var args = o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("tmux-arg=")).Select(l => l["tmux-arg=".Length..]).ToList();
            Assert.Equal(evil, args[4]);                                                      // genau ein Argument, unveraendert
            Assert.False(File.Exists(Path.Combine(dir, "PWNED")) || File.Exists(Path.Combine(dir, "PWNED2")));

            // ohne tmux fuehrt "sh -c" den Befehl des Besitzers aus (so ist es gewollt); die Zeile selbst wird davon nicht gespalten
            var (o2, _) = await RunShAsync(dir, Line(new SshLaunch("custom", "", false, "echo 'a b'; echo $((6*7))")));
            Assert.Equal("a b\n42\n", o2);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- Pruefbefehl und Plan

    [Fact]
    public async Task The_probe_command_reports_program_directory_and_tmux_from_a_real_sh()
    {
        var (dir, _) = Sandbox();
        try
        {
            string Out(string o) => Regex.Match(o, "julos-probe .*").Value;
            // alles da
            var l = new SshLaunch("claude", "~/work", true, "");
            Assert.Equal("julos-probe prog=1 dir=1 tmux=1", Out((await RunShAsync(dir, SshLaunchRules.ProbeCommand(l))).Out));
            // Programm fehlt (PATH ohne die Attrappe), Verzeichnis fehlt; ob tmux fehlt, haengt vom Rechner ab (Login-Shells erweitern den PATH), darum nicht geprueft
            var bare = "/usr/bin:/bin";
            Assert.StartsWith("julos-probe prog=0 dir=1 tmux=", Out((await RunShAsync(dir, SshLaunchRules.ProbeCommand(l), bare)).Out));
            Assert.Equal("julos-probe prog=1 dir=0 tmux=1", Out((await RunShAsync(dir, SshLaunchRules.ProbeCommand(l with { WorkDir = "~/gibt/es/nicht" }))).Out));
            // nur was gebraucht wird, wird geprueft: eine blanke Shell mit Verzeichnis fragt weder nach einem Programm noch nach tmux
            Assert.Equal("julos-probe prog=1 dir=1 tmux=1", Out((await RunShAsync(dir, SshLaunchRules.ProbeCommand(new SshLaunch("shell", "~/work", false, "")), bare)).Out));
            Assert.Equal("julos-probe prog=1 dir=1 tmux=1", Out((await RunShAsync(dir, SshLaunchRules.ProbeCommand(new SshLaunch("custom", "", false, "gibt-es-nicht")), bare)).Out));   // der eigene Befehl wird nicht geprueft
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("julos-probe prog=1 dir=1 tmux=1", true, true, true)]
    [InlineData("julos-probe prog=0 dir=1 tmux=0\n", false, true, false)]
    [InlineData("Willkommen\njulos-probe prog=1 dir=0 tmux=1\nTschuess", true, false, true)]
    public void The_probe_output_is_read_even_with_noise_around_it(string output, bool prog, bool dir, bool tmux)
        => Assert.Equal(new SshProbe(prog, dir, tmux), SshLaunchRules.ParseProbe(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("julos-probe prog=1 dir=1")]
    [InlineData("julos-probe prog=2 dir=1 tmux=1")]
    [InlineData("anderer Text")]
    public void Without_a_proper_probe_line_there_is_no_result(string? output) => Assert.Null(SshLaunchRules.ParseProbe(output));

    [Fact]
    public void The_plan_starts_the_program_when_everything_is_there()
    {
        var l = new SshLaunch("claude", "~/w", true, "");
        var plan = SshLaunchRules.Plan(l, "Server", Id, new SshProbe(true, true, true));
        Assert.Equal("claude", plan.Program);
        Assert.True(plan.Tmux);
        Assert.Empty(plan.Notices);
        Assert.Equal(Line(l, true), plan.Line);
    }

    [Fact]
    public void A_missing_program_falls_back_to_the_shell_with_a_notice_and_keeps_directory_and_tmux_under_a_shell_name()
    {
        var l = new SshLaunch("claude", "~/w", true, "");
        var plan = SshLaunchRules.Plan(l, "Server", Id, new SshProbe(false, true, true));
        Assert.Equal("shell", plan.Program);
        Assert.Equal(["program-missing"], plan.Notices);
        Assert.Contains("julos-shell-server-0a1b2c", plan.Line);                       // nicht in der Sitzung des Programms: die zeigt spaeter auf Claude
        Assert.DoesNotContain("claude", plan.Line!.Replace("julos-shell", ""));
        var plain = SshLaunchRules.Plan(new SshLaunch("codex", "", false, ""), "Server", Id, new SshProbe(false, true, true));
        Assert.Null(plain.Line);                                                       // nichts zu starten: die gewoehnliche Shell
        Assert.Equal(["program-missing"], plain.Notices);
        Assert.Equal("program-missing", plain.NoticeText);
    }

    [Fact]
    public void A_missing_directory_starts_no_program_at_all()
    {
        foreach (var program in new[] { "claude", "codex", "shell", "custom" })
        {
            var plan = SshLaunchRules.Plan(new SshLaunch(program, "~/weg", true, program == "custom" ? "x" : ""), "S", Id, new SshProbe(true, false, true));
            Assert.Null(plan.Line);
            Assert.Equal("shell", plan.Program);
            Assert.False(plan.Tmux);
            Assert.Equal(["dir-missing"], plan.Notices);
        }
    }

    [Fact]
    public void Without_tmux_the_session_is_a_normal_one_with_a_visible_notice()
    {
        var plan = SshLaunchRules.Plan(new SshLaunch("claude", "", true, ""), "S", Id, new SshProbe(true, true, false));
        Assert.Equal(" exec claude\n", plan.Line);
        Assert.Equal("claude", plan.Program);
        Assert.False(plan.Tmux);
        Assert.Equal(["tmux-missing"], plan.Notices);
        var both = SshLaunchRules.Plan(new SshLaunch("codex", "", true, ""), "S", Id, new SshProbe(false, true, false));
        Assert.Equal(["program-missing", "tmux-missing"], both.Notices);
        Assert.Null(both.Line);
    }

    [Fact]
    public void A_failed_probe_means_a_shell_and_a_notice_never_a_blind_start()
    {
        var plan = SshLaunchRules.Plan(new SshLaunch("claude", "~/w", true, ""), "S", Id, null);
        Assert.Null(plan.Line);
        Assert.Equal("shell", plan.Program);
        Assert.Equal(["probe-failed"], plan.Notices);
    }
}
