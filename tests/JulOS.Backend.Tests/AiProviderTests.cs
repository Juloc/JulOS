using System.Net;
using System.Security.Cryptography;
using JulOS.Integrations.Ai;

namespace JulOS.Backend.Tests;

/// <summary>Die Anbieter mit API-Schluessel gegen einen Stand-in fuer das Netz (nie ein echter Anbieter), und der Download des Codex-Programms mit Pruefsumme (lokale Dateien, kein Netz).</summary>
public sealed class AiProviderTests
{
    private static readonly AiChatRequest Hello = new("m", [new AiMessage("system", "Sei kurz."), new AiMessage("user", "Hallo")], "low", 100);

    private static async Task<List<AiChunk>> Drain(IAiProvider p, AiChatRequest request)
    {
        var all = new List<AiChunk>();
        await foreach (var c in p.ChatAsync(request, TestContext.Current.CancellationToken)) all.Add(c);
        return all;
    }

    [Fact]
    public async Task OpenAi_streams_text_and_usage_and_sends_the_key_only_to_the_fixed_address()
    {
        var stub = new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("chat/completions")
            ? StubHandler.Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hal\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":2}}\n\ndata: [DONE]\n\n")
            : StubHandler.Json("""{"data":[{"id":"gpt-4.1"},{"id":"text-embedding-3-small"},{"id":"o3"},{"id":"gpt-4o-audio-preview"},{"id":"whisper-1"},{"id":"gpt-5-mini"}]}"""));
        var p = new OpenAiProvider(_ => Task.FromResult<string?>("sk-test-key-0000000000"), stub);

        var chunks = await Drain(p, Hello with { Model = "gpt-5-mini" });
        Assert.Equal("Hallo", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(new AiUsage(9, 2), chunks[^1].Usage);

        var models = await p.ListModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["gpt-4.1", "gpt-5-mini", "o3"], models.Select(m => m.Id).ToArray());          // nur Chat-Modelle
        Assert.Empty(models[0].Efforts); Assert.Equal(["low", "medium", "high"], models[2].Efforts);

        Assert.All(stub.Seen, s => Assert.Equal("api.openai.com", s.Uri!.Host));                      // ein festes Ziel
        Assert.All(stub.Seen, s => Assert.StartsWith("https://api.openai.com/v1/", s.Uri!.AbsoluteUri));
        Assert.Equal("Bearer sk-test-key-0000000000", stub.Seen[0].Headers["Authorization"]);
        Assert.Contains("\"reasoning_effort\":\"low\"", stub.Seen[0].Body); Assert.Contains("\"max_completion_tokens\":100", stub.Seen[0].Body);
        Assert.DoesNotContain("sk-test", stub.Seen[0].Body);                                          // der Schluessel steht nie im Koerper
    }

    [Fact]
    public async Task OpenAi_errors_become_fixed_codes_with_short_texts_without_the_key()
    {
        async Task<AiProviderException> Fail(HttpStatusCode status, string body)
        {
            var p = new OpenAiProvider(_ => Task.FromResult<string?>("sk-test-key-0000000000"), new StubHandler(_ => StubHandler.Json(body, status)));
            return await Assert.ThrowsAsync<AiProviderException>(() => Drain(p, Hello));
        }
        Assert.Equal("not_signed_in", (await Fail(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided: sk-test-key-0000000000"}}""")).Code);
        var limit = await Fail(HttpStatusCode.TooManyRequests, """{"error":{"message":"You exceeded your current quota"}}""");
        Assert.Equal("plan_limit", limit.Code); Assert.Contains("quota", limit.Message);
        Assert.Equal("provider_unavailable", (await Fail(HttpStatusCode.BadGateway, "<html>")).Code);
        var bad = await Fail(HttpStatusCode.BadRequest, """{"error":{"message":"bad model sk-test-key-0000000000 and a very long text """ + new string('y', 500) + "\"}}");
        Assert.Equal("provider_error", bad.Code); Assert.DoesNotContain("sk-test", bad.Message); Assert.True(bad.Message.Length <= AiText.MaxErrorLength + 1);

        var nokey = new OpenAiProvider(_ => Task.FromResult<string?>(null), new StubHandler(_ => throw new InvalidOperationException("kein Aufruf ohne Schluessel")));
        Assert.Equal("no_key", (await nokey.GetStateAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal("not_signed_in", (await Assert.ThrowsAsync<AiProviderException>(() => Drain(nokey, Hello))).Code);
    }

    [Fact]
    public async Task Anthropic_streams_text_and_usage_with_system_apart_and_images_as_blocks()
    {
        var stub = new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("messages")
            ? StubHandler.Sse("event: message_start\ndata: {\"message\":{\"usage\":{\"input_tokens\":12,\"cache_read_input_tokens\":3,\"output_tokens\":1}}}\n\n"
                + "event: content_block_delta\ndata: {\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\nevent: content_block_delta\ndata: {\"delta\":{\"type\":\"text_delta\",\"text\":\" du\"}}\n\n"
                + "event: message_delta\ndata: {\"usage\":{\"output_tokens\":5}}\n\nevent: message_stop\ndata: {}\n\n")
            : StubHandler.Json("""{"data":[{"id":"claude-x","display_name":"Claude X"}]}"""));
        var p = new AnthropicProvider(_ => Task.FromResult<string?>("sk-ant-test-0000000000"), stub);

        var request = Hello with { Messages = [new AiMessage("system", "Sei kurz."), new AiMessage("user", "Was ist das?", [new AiImage("image/png", "AAAA")])] };
        var chunks = await Drain(p, request);
        Assert.Equal("Hi du", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(new AiUsage(15, 5), chunks[^1].Usage);
        Assert.Equal("Claude X", (await p.ListModelsAsync(TestContext.Current.CancellationToken))[0].Name);

        Assert.All(stub.Seen, s => Assert.Equal("api.anthropic.com", s.Uri!.Host));
        Assert.Equal("sk-ant-test-0000000000", stub.Seen[0].Headers["x-api-key"]); Assert.Equal(AnthropicProvider.ApiVersion, stub.Seen[0].Headers["anthropic-version"]);
        Assert.Contains("\"system\":\"Sei kurz.\"", stub.Seen[0].Body); Assert.Contains("\"media_type\":\"image/png\"", stub.Seen[0].Body);
        Assert.DoesNotContain("\"role\":\"system\"", stub.Seen[0].Body);

        var broken = new AnthropicProvider(_ => Task.FromResult<string?>("sk-ant-test-0000000000"), new StubHandler(_ => StubHandler.Sse("event: error\ndata: {\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}\n\n")));
        Assert.Equal("provider_unavailable", (await Assert.ThrowsAsync<AiProviderException>(() => Drain(broken, Hello))).Code);
    }

    [Fact]
    public async Task A_line_without_an_end_is_refused_while_reading_and_lines_end_at_any_line_break()
    {
        var huge = new OpenAiProvider(_ => Task.FromResult<string?>("sk-test-key-0000000000"), new StubHandler(_ => StubHandler.Sse("data: " + new string('x', AiHttp_MaxLine() + 10))));
        Assert.Equal("provider_error", (await Assert.ThrowsAsync<AiProviderException>(() => Drain(huge, Hello))).Code);

        var reader = new JulOS.Integrations.Ai.LineReader(new StringReader("a\r\nb\rc\n\nd"), 100);
        var lines = new List<string?>();
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } l) lines.Add(l);
        Assert.Equal(["a", "b", "c", "", "d"], lines);
    }

    private static int AiHttp_MaxLine() => 1 << 20;

    // ---------------------------------------------------------------- Download des Codex-Programms

    private static CodexRelease ReleaseFor(byte[] archive, string? sha = null) => new("9.9.9", "https://example.invalid/codex.tar.gz", sha ?? Convert.ToHexStringLower(SHA256.HashData(archive)), "codex-test-linux");

    [Fact]
    public async Task A_download_with_the_right_checksum_is_installed_as_one_file_at_a_fixed_place()
    {
        using var dir = new TempDir();
        var archive = FakeCodexProgram.Archive(("README", "nicht das Programm"), ("sub/codex-test-linux", "#!/bin/sh\necho hi\n"));
        var installer = new CodexInstaller(dir.Path, ReleaseFor(archive), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        Assert.False(installer.IsInstalled);
        await installer.InstallAsync(TestContext.Current.CancellationToken);
        Assert.True(installer.IsInstalled);
        Assert.Equal(Path.Combine(dir.Path, "ai", "codex", "bin", "9.9.9", "codex"), installer.InstalledPath);
        Assert.Equal("#!/bin/sh\necho hi\n", File.ReadAllText(installer.InstalledPath!));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(installer.InstalledPath!).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(installer.InstalledPath!).HasFlag(UnixFileMode.OtherWrite));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "ai", "codex", "tmp")));              // der Download selbst wird entfernt
    }

    [Fact]
    public async Task A_wrong_checksum_discards_the_download_and_installs_nothing()
    {
        using var dir = new TempDir();
        var archive = FakeCodexProgram.Archive(("codex-test-linux", "boese"));
        var installer = new CodexInstaller(dir.Path, ReleaseFor(archive, new string('0', 64)), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        var e = await Assert.ThrowsAsync<AiProviderException>(() => installer.InstallAsync(TestContext.Current.CancellationToken));
        Assert.Equal("integrity", e.Code);
        Assert.False(installer.IsInstalled);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "ai", "codex", "tmp")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "ai", "codex", "bin")));

        // Der Hintergrund-Weg meldet den Fehler als Zustand, nicht als Absturz
        await installer.StartAsync();
        Assert.False(installer.IsInstalled); Assert.NotNull(installer.Failure);
    }

    [Fact]
    public async Task The_archive_decides_nothing_about_paths_and_a_missing_or_huge_download_fails()
    {
        using var dir = new TempDir();
        // Ein Eintrag mit Pfad nach oben heisst anders als erwartet und wird nie geschrieben
        var evil = FakeCodexProgram.Archive(("../../escape.txt", "x"));
        var i1 = new CodexInstaller(dir.Path, ReleaseFor(evil), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(evil) }));
        await Assert.ThrowsAsync<AiProviderException>(() => i1.InstallAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(dir.Path, "escape.txt"))); Assert.False(File.Exists(Path.Combine(dir.Path, "..", "escape.txt")));

        var big = new byte[5000];
        var i2 = new CodexInstaller(dir.Path, ReleaseFor(big), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(big) }), maxBytes: 1000);
        Assert.Equal("provider_error", (await Assert.ThrowsAsync<AiProviderException>(() => i2.InstallAsync(TestContext.Current.CancellationToken))).Code);

        var i3 = new CodexInstaller(dir.Path, ReleaseFor(big), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<AiProviderException>(() => i3.InstallAsync(TestContext.Current.CancellationToken));
        Assert.False(new CodexInstaller(dir.Path, null).Supported);
    }

    [Fact]
    public void The_pinned_release_has_a_version_a_https_address_on_github_and_a_sha256_for_each_machine()
    {
        var r = CodexRelease.ForThisMachine();
        if (r is null) return;                                                                        // eine andere Plattform: es gibt keine
        Assert.Equal(CodexRelease.PinnedVersion, r.Version);
        Assert.StartsWith("https://github.com/openai/codex/releases/download/rust-v" + CodexRelease.PinnedVersion + "/", r.Url);
        Assert.Equal(64, r.Sha256.Length); Assert.Matches("^[0-9a-f]{64}$", r.Sha256);
    }

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "julos-ai-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
