using JulOS.Integrations.Ai;

namespace JulOS.Backend.Tests;

/// <summary>Die Codex-Bruecke gegen ein Fake-Programm (bash, kein Netz, nie das echte Codex): Start, Anfrage im Strom, Absturz und Neustart mit Wartezeit, Abbruch, Geraete-Anmeldung, Abmeldung, Umgebung.</summary>
public sealed class AiBridgeTests : IDisposable
{
    private readonly FakeCodexProgram _fake = new();
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-23T10:00:00Z"));
    private readonly List<IDisposable> _open = [];

    public void Dispose() { foreach (var d in _open) d.Dispose(); _fake.Dispose(); }

    private CodexBridge Bridge(FakeCodexProgram? fake = null)
    {
        var f = fake ?? _fake;
        var bridge = new CodexBridge(new CodexInstaller(f.DataDir, null), _time, new CodexBridgeOptions { Executable = f.Executable, BackoffStart = TimeSpan.FromSeconds(2), ControlTimeout = TimeSpan.FromSeconds(10) });
        _open.Add(bridge);
        return bridge;
    }

    private static AiChatRequest Ask(string text, string? effort = null) => new("fake-1", [new AiMessage("user", text)], effort, 100);

    private static async Task<List<AiChunk>> Drain(IAiProvider p, AiChatRequest r, CancellationToken? ct = null)
    {
        var all = new List<AiChunk>();
        await foreach (var c in p.ChatAsync(r, ct ?? TestContext.Current.CancellationToken)) all.Add(c);
        return all;
    }

    [Fact]
    public async Task A_request_streams_text_pieces_then_the_usage_through_one_started_program()
    {
        var bridge = Bridge();
        Assert.False(bridge.Running);                                  // nichts laeuft, bis jemand es braucht
        Assert.Empty(_fake.Calls);

        var chunks = await Drain(bridge, Ask("Hallo", "low"));
        Assert.Equal(["Hello", " world"], chunks.Where(c => c.Text is not null).Select(c => c.Text).ToArray());
        Assert.Equal(new AiUsage(11, 7), chunks[^1].Usage);
        Assert.True(bridge.Running);

        // dieselbe Bruecke, derselbe Prozess, ein zweiter Faden
        await Drain(bridge, Ask("Nochmal"));
        Assert.Single(_fake.Calls, c => c == "app-server");
        Assert.Contains("\"threadId\":\"thr_2\"", _fake.Read("turn-start.json"));

        // der Faden ist fluechtig und nur lesend, ohne Werkzeuge; das Gespraech steht im Text, das Modell und die Denkstufe sind gesetzt
        var start = _fake.Read("thread-start-1.json");
        Assert.Contains("\"ephemeral\":true", start); Assert.Contains("\"sandbox\":\"read-only\"", start); Assert.Contains("\"approvalPolicy\":\"never\"", start); Assert.Contains("\"shell_tool\":false", start);
        Assert.Contains("\"model\":\"fake-1\"", start);
        Assert.Equal(_fake.Work, _fake.Read("cwd.txt").Trim());                // eigener Arbeitsordner im Datenordner

        var models = await bridge.ListModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("fake-1", models[0].Id); Assert.Equal(["low", "high"], models[0].Efforts);
        var plan = await bridge.GetPlanUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal("plus", plan!.Plan); Assert.Equal(12.5, plan.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task The_program_gets_no_environment_of_JulOS_and_asks_of_it_are_refused()
    {
        Environment.SetEnvironmentVariable("JULOS_TEST_AI_SECRET", "geheim-0123");
        try
        {
            var bridge = Bridge();
            await Drain(bridge, Ask("ASKME bitte"));
            var env = _fake.Read("env.txt");
            Assert.DoesNotContain("JULOS_TEST_AI_SECRET", env); Assert.DoesNotContain("geheim-0123", env); Assert.DoesNotContain("JULOS_DB", env);
            Assert.Contains("CODEX_HOME=" + _fake.Home, env);
            Assert.Contains("HOME=" + _fake.Home, env);
            // Die Anfrage des Programms (eine Freigabe fuer einen Befehl) wurde abgelehnt, nicht erfuellt
            var reply = _fake.Read("reply.txt");
            Assert.Contains("\"id\":900", reply); Assert.Contains("\"error\"", reply); Assert.DoesNotContain("\"result\"", reply);
        }
        finally { Environment.SetEnvironmentVariable("JULOS_TEST_AI_SECRET", null); }
    }

    [Fact]
    public async Task A_crash_ends_the_request_and_the_program_is_started_again_only_after_a_growing_wait()
    {
        var bridge = Bridge();
        var e = await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("CRASH")));
        Assert.Equal("provider_unavailable", e.Code);

        // gleich danach: noch in der Wartezeit, das Programm wird nicht wieder gestartet
        var early = await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("Hallo")));
        Assert.Equal("provider_unavailable", early.Code);
        Assert.Single(_fake.Calls, c => c == "app-server");

        _time.Advance(TimeSpan.FromSeconds(3));
        var again = await Drain(bridge, Ask("Hallo"));
        Assert.Equal("Hello", again[0].Text);
        Assert.Equal(2, _fake.Calls.Count(c => c == "app-server"));

        // zwei Abstuerze hintereinander (ohne Erfolg dazwischen): die Wartezeit verdoppelt sich, von 2 auf 4 Sekunden
        await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("CRASH")));
        _time.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("CRASH")));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("provider_unavailable", (await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("Hallo")))).Code);   // 3 s reichen jetzt nicht mehr
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("Hello", (await Drain(bridge, Ask("Hallo")))[0].Text);
    }

    [Fact]
    public async Task Cancelling_the_caller_interrupts_the_turn_in_the_program()
    {
        var bridge = Bridge();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var got = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var c in bridge.ChatAsync(Ask("SLOW bitte"), cts.Token)) { got.Add(c.Text ?? ""); cts.Cancel(); }
            await Task.Delay(Timeout.Infinite, cts.Token);
        });
        Assert.Equal(["Hi"], got);
        for (var i = 0; i < 100 && !_fake.Has("interrupted"); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(_fake.Has("interrupted"));                         // turn/interrupt kam beim Programm an
    }

    [Fact]
    public async Task Failures_of_a_turn_become_codes_and_texts_without_secrets()
    {
        var bridge = Bridge();
        var fail = await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("FAIL")));
        Assert.Equal("provider_error", fail.Code); Assert.DoesNotContain("secret-token", fail.Message);
        var limit = await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("LIMIT")));
        Assert.Equal("plan_limit", limit.Code);
        var images = await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, new AiChatRequest("fake-1", [new AiMessage("user", "x", [new AiImage("image/png", "AAAA")])], null, 10)));
        Assert.Equal("bad_request", images.Code);

        // nicht angemeldet: keine Anfrage an das Programm
        using var other = new FakeCodexProgram(signedIn: false);
        var signedOut = await Assert.ThrowsAsync<AiProviderException>(() => Drain(Bridge(other), Ask("Hallo")));
        Assert.Equal("not_signed_in", signedOut.Code);
        Assert.DoesNotContain("app-server", other.Calls);
    }

    [Fact]
    public async Task The_device_login_shows_address_and_code_then_succeeds_and_signing_out_forgets_it()
    {
        using var fake = new FakeCodexProgram(signedIn: false);
        var bridge = Bridge(fake);
        Assert.Equal("not_signed_in", (await bridge.GetStateAsync(TestContext.Current.CancellationToken)).Status);

        var first = bridge.StartLogin();
        var state = first;
        for (var i = 0; i < 100 && state.State != "waiting"; i++) { await Task.Delay(50, TestContext.Current.CancellationToken); state = bridge.LoginState(); }
        Assert.Equal("waiting", state.State);
        Assert.Equal("https://auth.openai.com/codex/device", state.Url); Assert.Equal("ABCD-12345", state.Code); Assert.NotNull(state.ExpiresAt);
        Assert.Equal("login_waiting", (await bridge.GetStateAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal("waiting", bridge.StartLogin().State);             // eine laufende Anmeldung wird weiterbenutzt, nicht doppelt gestartet
        Assert.Single(fake.Calls, c => c == "login --device-auth");

        fake.Approve();                                                  // die Person hat sich im Browser angemeldet
        for (var i = 0; i < 100 && bridge.LoginState().State != "succeeded"; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", bridge.LoginState().State);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("ready", (await bridge.GetStateAsync(TestContext.Current.CancellationToken)).Status);

        await bridge.LogoutAsync(TestContext.Current.CancellationToken);
        Assert.Equal("not_signed_in", (await bridge.GetStateAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Contains("logout", fake.Calls);
    }

    [Fact]
    public async Task A_login_can_be_cancelled_and_an_address_of_another_host_is_never_shown()
    {
        using var fake = new FakeCodexProgram(url: "https://evil.example/codex/device", signedIn: false);
        var bridge = Bridge(fake);
        bridge.StartLogin();
        await Task.Delay(600, TestContext.Current.CancellationToken);
        var shown = bridge.LoginState();
        Assert.NotEqual("waiting", shown.State);                         // ohne Adresse von OpenAI gibt es keinen Code zu zeigen
        Assert.Null(shown.Url);
        bridge.CancelLogin();
        for (var i = 0; i < 100 && bridge.LoginState().State is "starting" or "waiting"; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", bridge.LoginState().State);
    }

    [Fact]
    public async Task Without_the_program_the_state_says_so_and_nothing_is_started()
    {
        using var dir = new AiProviderTests.TempDir();
        var installer = new CodexInstaller(dir.Path, new CodexRelease("1.0.0", "https://example.invalid/x", new string('0', 64), "codex"));
        var bridge = new CodexBridge(installer, _time);
        Assert.Equal("not_installed", (await bridge.GetStateAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Throws<AiProviderException>(() => bridge.StartLogin());
        await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("Hallo")));
        Assert.False(bridge.Running);
        Assert.Equal("error", (await new CodexBridge(new CodexInstaller(dir.Path, null), _time).GetStateAsync(TestContext.Current.CancellationToken)).Status);
    }

    // ---------------------------------------------------------------- Pruefbefunde (F1, F2, F4, F7)

    private static bool Alive(int pid) => Directory.Exists("/proc/" + pid) && !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ");

    [Fact]
    public async Task Cancelling_a_cold_start_ends_the_started_program_and_does_not_count_as_a_failure()
    {
        File.WriteAllText(Path.Combine(_fake.Record, "slow-init"), "");
        var bridge = Bridge();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(bridge, Ask("Hallo"), cts.Token));
        Assert.False(bridge.Running);
        var pid = int.Parse(File.ReadAllLines(Path.Combine(_fake.Record, "pids-app-server"))[0]);
        for (var i = 0; i < 100 && Alive(pid); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(Alive(pid));                                      // kein verwaister app-server

        // der Abbruch war kein Fehler des Programms: kein Backoff, die naechste Anfrage startet sofort
        File.Delete(Path.Combine(_fake.Record, "slow-init"));
        Assert.Equal("Hello", (await Drain(bridge, Ask("Hallo")))[0].Text);
    }

    [Fact]
    public async Task Cancelling_before_the_answer_to_turn_start_still_interrupts_the_turn()
    {
        var bridge = Bridge();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(bridge, Ask("SLOWSTART"), cts.Token));
        for (var i = 0; i < 100 && !_fake.Has("interrupted"); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(_fake.Has("interrupted"));
    }

    [Fact]
    public async Task A_thread_that_is_not_confirmed_read_only_gets_no_text_and_a_tool_item_ends_the_turn()
    {
        var bridge = Bridge();
        File.WriteAllText(Path.Combine(_fake.Record, "unsafe"), "");
        var e = await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("GEHEIM-TEXT")));
        Assert.Equal("provider_error", e.Code); Assert.Contains("nicht bestätigt", e.Message);
        Assert.False(_fake.Has("turn-start.json"));                    // fail-closed: der Text ging nie an das Programm
        File.Delete(Path.Combine(_fake.Record, "unsafe"));

        var tool = await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("TOOL bitte")));
        Assert.Contains("Werkzeug", tool.Message);
        for (var i = 0; i < 100 && !_fake.Has("interrupted"); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(_fake.Has("interrupted"));                         // die Runde wurde unterbrochen

        Assert.True(CodexProtocol.IsLockedDown(ApiKit.J("""{"approvalPolicy":"never","sandbox":"read-only"}""")));
        Assert.True(CodexProtocol.IsLockedDown(ApiKit.J("""{"approvalPolicy":"never","sandbox":{"type":"readOnly"}}""")));
        foreach (var bad in new[] { """{"approvalPolicy":"never"}""", """{"sandbox":"read-only"}""", """{"approvalPolicy":"on-request","sandbox":"read-only"}""", """{"approvalPolicy":"never","sandbox":{"type":"workspaceWrite"}}""", "{}" })
            Assert.False(CodexProtocol.IsLockedDown(ApiKit.J(bad)), bad);
    }

    [Fact]
    public async Task Many_simultaneous_status_questions_start_one_program()
    {
        File.WriteAllText(Path.Combine(_fake.Record, "slow-status"), "");
        var bridge = Bridge();
        var states = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => bridge.GetStateAsync(TestContext.Current.CancellationToken)));
        Assert.All(states, st => Assert.Equal("ready", st.Status));
        Assert.Single(_fake.Calls, c => c == "login status");
    }

    [Fact]
    public async Task The_folders_of_the_bridge_are_private_and_a_changed_program_is_not_started()
    {
        using var dir = new AiProviderTests.TempDir();
        var archive = FakeCodexProgram.Archive(("codex-test-linux", "#!/bin/sh\nexit 0\n"));
        var release = new CodexRelease("9.9.9", "https://example.invalid/c.tar.gz", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(archive)), "codex-test-linux");
        var installer = new CodexInstaller(dir.Path, release, new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        await installer.InstallAsync(TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
            foreach (var folder in new[] { "ai", Path.Combine("ai", "codex"), Path.Combine("ai", "codex", "bin"), Path.Combine("ai", "codex", "bin", "9.9.9"), Path.Combine("ai", "codex", "tmp") })
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(dir.Path, folder)));
        Assert.Equal(installer.InstalledPath, installer.VerifiedPath());

        var bridge = new CodexBridge(installer, _time);
        _open.Add(bridge);
        await Assert.ThrowsAnyAsync<AiProviderException>(() => Drain(bridge, Ask("x")));      // das Skript ist kein app-server, aber es wird gestartet (kein Pfad-Fehler)
        if (!OperatingSystem.IsWindows())
            foreach (var folder in new[] { installer.Home, installer.Work }) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));

        File.AppendAllText(installer.InstalledPath!, "# nachtraeglich veraendert\n");
        Assert.Null(installer.VerifiedPath());
        Assert.Equal("install_failed", (await bridge.GetStateAsync(TestContext.Current.CancellationToken)).Status);
        Assert.NotNull(installer.Failure);
        await Assert.ThrowsAsync<AiProviderException>(() => Drain(bridge, Ask("x")));
        bridge.Stop();

        // eine Pruefsumme, die keine ist, laedt nichts (fail-closed)
        using var dir2 = new AiProviderTests.TempDir();
        var bad = new CodexInstaller(dir2.Path, release with { Sha256 = "nope" }, new StubHandler(_ => throw new InvalidOperationException("kein Download")));
        Assert.Equal("integrity", (await Assert.ThrowsAsync<AiProviderException>(() => bad.InstallAsync(TestContext.Current.CancellationToken))).Code);
    }
}
