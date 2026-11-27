using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using JulOS.Integrations.Ai;

namespace JulOS.Backend.Tests;

/// <summary>
/// Codex im eigenen Container (docs/CODEX-SIDECAR.md): der Starter ueber den Socket gegen ein Fake-Tor (ein Testserver auf einem Socket), der Selbsttest, die Freigabe und die Pruefsummen
/// des Images. Einmal auch gegen das echte Tor (Python) mit dem Fake-Programm, wenn python3 da ist.
/// </summary>
public sealed class CodexSidecarTests : IDisposable
{
    private readonly List<IDisposable> _open = [];
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-26T10:00:00Z"));
    public void Dispose() { foreach (var d in _open) d.Dispose(); }

    /// <summary>Ein Testserver auf einem Unix-Socket: liest die erste Zeile (merkt sie sich), dann entscheidet der Beantworter.</summary>
    private sealed class FakeGate : IDisposable
    {
        public string Path { get; }
        public List<string> First { get; } = [];
        public List<string> Closed { get; } = [];
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        public FakeGate(Func<string, NetworkStream, Task> answer, string? path = null)
        {
            Path = path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jg-" + Guid.NewGuid().ToString("N")[..10] + ".sock");
            _listener.Bind(new UnixDomainSocketEndPoint(Path)); _listener.Listen(8);
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    Socket c;
                    try { c = await _listener.AcceptAsync(); } catch (Exception e) when (e is ObjectDisposedException or SocketException) { return; }
                    _ = Task.Run(async () =>
                    {
                        using var socket = c; using var stream = new NetworkStream(c, ownsSocket: false);
                        var line = new List<byte>(); var one = new byte[1];
                        while (await stream.ReadAsync(one) == 1 && one[0] != '\n') line.Add(one[0]);
                        var first = Encoding.UTF8.GetString([.. line]);
                        lock (First) First.Add(first);
                        try { await answer(first, stream); } catch (IOException) { }
                        // Wartet, bis der Client die Verbindung schliesst (Kill), und merkt es sich
                        try { var sink = new byte[64]; while (await stream.ReadAsync(sink) > 0) { } } catch (IOException) { }
                        lock (Closed) Closed.Add(first);
                    });
                }
            });
        }

        public void Dispose() { _listener.Dispose(); try { File.Delete(Path); } catch (IOException) { } }
    }

    private FakeGate Gate(Func<string, NetworkStream, Task> answer, string? path = null) { var g = new FakeGate(answer, path); _open.Add(g); return g; }

    private static async Task Write(NetworkStream s, string text) { await s.WriteAsync(Encoding.UTF8.GetBytes(text)); await s.FlushAsync(); }

    private static async Task<(string Output, int Exit)> Run(ICodexRunner runner, CodexCommand command, CodexSelfTestArgs? args = null)
    {
        using var p = runner.Start(command, args);
        using var reader = new StreamReader(p.StandardOutput, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        return (text, await p.WaitForExitAsync(TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------ der Starter

    [Fact]
    public async Task The_runner_sends_only_the_name_of_a_fixed_command_and_for_the_self_test_a_path_and_a_word()
    {
        var gate = Gate((_, s) => Write(s, "\u001e{\"exit\":0}\n"));
        var runner = new CodexSocketRunner(gate.Path);
        foreach (var (command, name) in new[] { (CodexCommand.AppServer, "app-server"), (CodexCommand.Login, "login"), (CodexCommand.LoginStatus, "login-status"), (CodexCommand.Logout, "logout"), (CodexCommand.Version, "version") })
            await Run(runner, command);
        await Run(runner, CodexCommand.SelfTest, new CodexSelfTestArgs("/data/ai/canary", "0123456789abcdef"));
        Assert.Equal(
            ["""{"cmd":"app-server"}""", """{"cmd":"login"}""", """{"cmd":"login-status"}""", """{"cmd":"logout"}""", """{"cmd":"version"}""", """{"cmd":"selftest","canary":"/data/ai/canary","word":"0123456789abcdef"}"""],
            gate.First);
        // Es gibt keinen Weg, einen freien Text mitzuschicken: die Schnittstelle kennt nur den Befehl (Aufzaehlung) und die Angaben des Selbsttests
        var start = typeof(ICodexRunner).GetMethod(nameof(ICodexRunner.Start))!;
        Assert.Equal([typeof(CodexCommand), typeof(CodexSelfTestArgs)], start.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Throws<ArgumentNullException>(() => runner.Start(CodexCommand.SelfTest));
    }

    [Fact]
    public async Task Lines_arrive_and_the_closing_line_is_cut_off_as_the_exit_code()
    {
        var gate = Gate((_, s) => Write(s, "{\"a\":1}\n{\"b\":2}\n\u001e{\"exit\":7}\n"));
        var (output, exit) = await Run(new CodexSocketRunner(gate.Path), CodexCommand.LoginStatus);
        Assert.Equal("{\"a\":1}\n{\"b\":2}\n", output);
        Assert.Equal(7, exit);
    }

    [Fact]
    public async Task A_line_that_only_looks_like_an_exit_is_data_and_no_closing_line_means_aborted()
    {
        // falscher Typ, zusaetzliches Feld, anderes Feld, mitten in der Zeile, ohne Zeilenende am Ende: nichts davon ist ein Ende
        var wire = "\u001e{\"exit\":\"x\"}\nA\n\u001e{\"exit\":1,\"more\":2}\n\u001e{\"other\":1}\nabc\u001e{\"exit\":0}\n\u001e{\"exit\":0}";
        var gate = Gate(async (_, s) => { await Write(s, wire); s.Socket.Shutdown(SocketShutdown.Both); });
        var (output, exit) = await Run(new CodexSocketRunner(gate.Path), CodexCommand.Logout);
        Assert.Equal(wire, output);
        Assert.Equal(-1, exit);
    }

    [Fact]
    public async Task Kill_closes_the_connection_so_the_gate_ends_the_program()
    {
        var gate = Gate((_, s) => Write(s, "hello\n"));          // und dann nichts mehr: ein wartendes Programm
        var runner = new CodexSocketRunner(gate.Path);
        var p = runner.Start(CodexCommand.AppServer);
        _open.Add(p);
        var buffer = new byte[16];
        Assert.Equal(6, await p.StandardOutput.ReadAsync(buffer, TestContext.Current.CancellationToken));
        p.Kill();
        Assert.Equal(-1, await p.WaitForExitAsync(TestContext.Current.CancellationToken));
        for (var i = 0; i < 100 && gate.Closed.Count == 0; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(gate.Closed);                              // das Tor hat das Ende der Verbindung gesehen
    }

    [Fact]
    public async Task An_unreachable_container_is_unavailable_not_a_crash()
    {
        var runner = new CodexSocketRunner(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".sock"));
        Assert.Equal("provider_unavailable", Assert.Throws<AiProviderException>(() => runner.Start(CodexCommand.Version)).Code);
        await runner.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(runner.Available); Assert.Null(runner.Version);
        var dir = new AiProviderTests.TempDir(); _open.Add(dir);
        var bridge = new CodexBridge(runner, new CodexInstaller(dir.Path, null), _time);
        var state = await bridge.GetStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal("error", state.Status); Assert.Equal("sidecar_unreachable", state.Detail);
    }

    [Fact]
    public async Task The_runner_reports_the_version_and_tells_when_the_container_is_back()
    {
        var gate = Gate((first, s) => Write(s, "codex-cli 0.171.0\n\u001e{\"exit\":0}\n"));
        var runner = new CodexSocketRunner(gate.Path);
        var came = 0; runner.BecameAvailable += () => came++;
        await runner.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(runner.Available); Assert.Equal("codex-cli 0.171.0", runner.Version); Assert.Equal(1, came);
        Assert.True(runner.Isolated); Assert.Equal("/home/codex/work", runner.WorkDirectory);
    }

    [Fact]
    public async Task A_restart_of_the_container_between_two_looks_also_asks_for_a_new_self_test()
    {
        static Task Answer(string first, NetworkStream s) => Write(s, "codex-cli 0.171.0\n\u001e{\"exit\":0}\n");
        var gate = Gate(Answer);
        var runner = new CodexSocketRunner(gate.Path, _time);
        var came = 0; runner.BecameAvailable += () => came++;
        var ct = TestContext.Current.CancellationToken;
        await runner.RefreshAsync(ct);
        _time.Advance(TimeSpan.FromSeconds(11)); await runner.RefreshAsync(ct);
        Assert.Equal(1, came);                                     // dieselbe Socket-Datei: nichts Neues
        // Das Tor startet neu (neue Socket-Datei am selben Ort), ohne dass eine Nachfrage dazwischen ins Leere ging
        gate.Dispose(); await Task.Delay(50, ct);
        Gate(Answer, gate.Path);
        _time.Advance(TimeSpan.FromSeconds(11)); await runner.RefreshAsync(ct);
        Assert.True(runner.Available); Assert.Equal(2, came);
    }

    // ------------------------------------------------------------------ der Selbsttest

    private static string Checks(params (string Name, bool Value)[] values) =>
        "{\"ok\":true,\"checks\":{" + string.Join(",", values.Select(v => $"\"{v.Name}\":{(v.Value ? "true" : "false")}")) + "}}\n\u001e{\"exit\":0}\n";

    private static (string, bool)[] AllGreen(params string[] red) => [.. CodexSelfTest.Checks.Select(c => (c, !red.Contains(c)))];

    private async Task<CodexSelfTestResult> SelfTest(string selftestAnswer, string version = "codex-cli " + CodexRelease.PinnedVersion, AiProviderTests.TempDir? dir = null)
    {
        var gate = Gate((first, s) => Write(s, first.Contains("selftest") ? selftestAnswer : version + "\n\u001e{\"exit\":0}\n"));
        dir ??= new AiProviderTests.TempDir(); _open.Add(dir);
        return await new CodexSelfTest(new CodexSocketRunner(gate.Path), dir.Path, _time).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_self_test_is_green_only_when_every_expected_check_says_true()
    {
        var green = await SelfTest(Checks(AllGreen()));
        Assert.True(green.Ok); Assert.Empty(green.Failed); Assert.Equal("codex-cli " + CodexRelease.PinnedVersion, green.Version);

        var red = await SelfTest(Checks(AllGreen("no_julos_data")));
        Assert.False(red.Ok); Assert.Equal(["no_julos_data"], red.Failed);

        // das "ok" des Tores wird nicht geglaubt: ein fehlende Pruefung ist rot, auch bei ok:true
        var missing = await SelfTest("{\"ok\":true,\"checks\":{\"uid_not_root\":true}}\n\u001e{\"exit\":0}\n");
        Assert.False(missing.Ok); Assert.Contains("codex_pinned", missing.Failed); Assert.DoesNotContain("uid_not_root", missing.Failed);

        // Muell, ein Fehler des Tores, ein Abbruch, "true" als Text statt als Wert: alles rot
        Assert.False((await SelfTest("not json\n\u001e{\"exit\":0}\n")).Ok);
        Assert.False((await SelfTest("{\"error\":\"unknown_command\"}\n")).Ok);
        Assert.False((await SelfTest(Checks(AllGreen()).Replace("\"exit\":0", "\"exit\":2"))).Ok);
        Assert.False((await SelfTest(Checks(AllGreen()).Replace("true", "\"true\"").Replace("\"ok\":\"true\"", "\"ok\":true"))).Ok);
    }

    [Fact]
    public async Task The_self_test_is_red_when_the_container_has_another_version_or_does_not_answer()
    {
        var other = await SelfTest(Checks(AllGreen()), version: "codex-cli 9.9.9");
        Assert.False(other.Ok); Assert.Equal([CodexSelfTest.VersionCheck], other.Failed);

        var runner = new CodexSocketRunner(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".sock"));
        var dir = new AiProviderTests.TempDir(); _open.Add(dir);
        var gone = await new CodexSelfTest(runner, dir.Path, _time).RunAsync(TestContext.Current.CancellationToken);
        Assert.False(gone.Ok); Assert.Equal(["unreachable"], gone.Failed);
    }

    [Fact]
    public async Task The_canary_file_is_private_keeps_its_word_and_the_gate_gets_its_path_and_word()
    {
        var dir = new AiProviderTests.TempDir(); _open.Add(dir);
        var gate = Gate((first, s) => Write(s, first.Contains("selftest") ? Checks(AllGreen()) : "codex-cli " + CodexRelease.PinnedVersion + "\n\u001e{\"exit\":0}\n"));
        var test = new CodexSelfTest(new CodexSocketRunner(gate.Path), dir.Path, _time);
        await test.RunAsync(TestContext.Current.CancellationToken);
        var word = File.ReadAllText(test.CanaryPath);
        Assert.Matches("^[0-9a-f]{16}$", word);
        Assert.Equal(Path.Combine(dir.Path, "ai", "canary"), test.CanaryPath);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(test.CanaryPath));
        await test.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(word, File.ReadAllText(test.CanaryPath));       // dasselbe Wort: eine von Hand abgelegte Kopie wird auch beim naechsten Lauf gefunden
        var asked = gate.First.Where(f => f.Contains("selftest")).ToList();
        Assert.All(asked, f => { Assert.Contains(test.CanaryPath.Replace("/", "\\/"), f.Replace("/", "\\/")); Assert.Contains(word, f); });
    }

    [Fact]
    public void A_result_frees_the_bridge_only_when_green_fresh_and_for_the_pinned_version()
    {
        var now = _time.GetUtcNow();
        CodexSelfTestResult R(bool ok = true, TimeSpan? age = null, string? pinned = null) => new(now - (age ?? TimeSpan.FromMinutes(5)), ok, [], "codex-cli", pinned ?? CodexRelease.PinnedVersion);
        Assert.True(R().Counts(now));
        Assert.False(R(ok: false).Counts(now));                                   // rot: die Bruecke bleibt fuer Apps aus (provider_unverified)
        Assert.False(R(age: TimeSpan.FromHours(37)).Counts(now));                 // zu alt: der Test laeuft taeglich, fehlt er, gilt sie als ungeprueft
        Assert.True(R(age: TimeSpan.FromHours(30)).Counts(now));
        Assert.False(R(age: TimeSpan.FromMinutes(-30)).Counts(now));              // aus der Zukunft
        Assert.False(R(pinned: "0.0.1").Counts(now));                             // fuer eine andere Version getestet
    }

    // ------------------------------------------------------------------ die Pruefsummen des Images

    [Fact]
    public void The_Dockerfile_of_the_Codex_image_pins_the_same_version_and_hashes_as_CodexRelease()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "JulOS.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir, "deploy", "codex", "Dockerfile"));
        string Arg(string name) => System.Text.RegularExpressions.Regex.Match(text, "^ARG " + name + "=(\\S+)$", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : throw new Xunit.Sdk.XunitException(name + " fehlt im Dockerfile");
        Assert.Equal(CodexRelease.PinnedVersion, Arg("CODEX_VERSION"));
        Assert.Equal(CodexRelease.For(Architecture.X64)!.Sha256, Arg("CODEX_SHA256_AMD64"));
        Assert.Equal(CodexRelease.For(Architecture.Arm64)!.Sha256, Arg("CODEX_SHA256_ARM64"));
        // und die Adresse des Archivs folgt demselben Muster wie die in JulOS
        Assert.Contains("rust-v${CODEX_VERSION}/codex-${triple}.tar.gz", text);
        Assert.EndsWith("x86_64-unknown-linux-musl.tar.gz", CodexRelease.For(Architecture.X64)!.Url); Assert.EndsWith("aarch64-unknown-linux-musl.tar.gz", CodexRelease.For(Architecture.Arm64)!.Url);
    }

    // ------------------------------------------------------------------ mit dem echten Tor und dem Fake-Programm

    [Fact]
    public async Task The_bridge_works_through_the_real_gate_with_the_fake_program()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "JulOS.slnx"))) root = Path.GetDirectoryName(root);
        var python = new[] { "/usr/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(python is null || root is null, "python3 fehlt");

        using var fake = new FakeCodexProgram();
        Directory.CreateDirectory(fake.Home);
        var socket = Path.Combine(Path.GetTempPath(), "jg-" + Guid.NewGuid().ToString("N")[..10] + ".sock");
        var start = new System.Diagnostics.ProcessStartInfo(python!) { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys; sys.path.insert(0, sys.argv[1]); import codex_gate; codex_gate.Gate(sock_path=sys.argv[2], codex=sys.argv[3], home=sys.argv[4]).serve()");
        foreach (var a in new[] { Path.Combine(root!, "tools", "codex-gate"), socket, fake.Executable, fake.Home }) start.ArgumentList.Add(a);
        using var gate = System.Diagnostics.Process.Start(start)!;
        try
        {
            for (var i = 0; i < 100 && !File.Exists(socket); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            var runner = new CodexSocketRunner(socket);
            var bridge = new CodexBridge(runner, new CodexInstaller(fake.DataDir, null), _time, new CodexBridgeOptions { BackoffStart = TimeSpan.FromSeconds(2), ControlTimeout = TimeSpan.FromSeconds(10) });
            _open.Add(bridge);

            Assert.Equal("ready", (await bridge.GetStateAsync(TestContext.Current.CancellationToken)).Status);
            Assert.Equal("codex-cli 0.0.0-fake", runner.Version);

            var chunks = new List<AiChunk>();
            await foreach (var c in bridge.ChatAsync(new AiChatRequest("fake-1", [new AiMessage("user", "Hallo")], null, 100), TestContext.Current.CancellationToken)) chunks.Add(c);
            Assert.Equal(["Hello", " world"], chunks.Where(c => c.Text is not null).Select(c => c.Text).ToArray());
            Assert.Equal(new AiUsage(11, 7), chunks[^1].Usage);
            Assert.Contains("\"cwd\":\"/home/codex/work\"", fake.Read("thread-start-1.json").Replace(" ", ""));          // der Arbeitsordner ist der im Container

            // Abmelden und die Geraete-Anmeldung laufen durch denselben Weg (Fehlerausgabe in die Ausgabe gemischt, Ende als Exit-Zeile)
            await bridge.LogoutAsync(TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal("not_signed_in", (await bridge.GetStateAsync(TestContext.Current.CancellationToken)).Status);
            var login = bridge.StartLogin();
            for (var i = 0; i < 100 && login.State != "waiting"; i++) { await Task.Delay(50, TestContext.Current.CancellationToken); login = bridge.LoginState(); }
            Assert.Equal("waiting", login.State); Assert.Equal("ABCD-12345", login.Code);
            fake.Approve();
            for (var i = 0; i < 100 && login.State != "succeeded"; i++) { await Task.Delay(50, TestContext.Current.CancellationToken); login = bridge.LoginState(); }
            Assert.Equal("succeeded", login.State);
        }
        finally { try { gate.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } try { File.Delete(socket); } catch (IOException) { } }
    }
}
