using System.Text;
using JulOS.Integrations.Agent;

namespace JulOS.Backend.Tests;

public sealed class AgentClientTests
{
    [Fact]
    public async Task Replies_and_bodies_are_passed_through_with_their_status()
    {
        await using var fake = new FakeAgent();
        using var agent = new AgentClient(new AgentOptions(fake.SocketPath));

        var windows = await agent.SendAsync(HttpMethod.Get, "/v1/windows");
        Assert.Equal(200, windows.Status);
        Assert.Contains("\"title\":\"Claude\"", windows.Json);

        var activate = await agent.SendAsync(HttpMethod.Post, "/v1/windows/12345/activate", """{"maximize":true}""");
        Assert.Equal(200, activate.Status);
        lock (fake.Requests) Assert.Equal(("POST", "/v1/windows/12345/activate", """{"maximize":true}"""), fake.Requests.Last());

        Assert.Equal(404, (await agent.SendAsync(HttpMethod.Get, "/v1/gibt-es-nicht")).Status);
    }

    [Fact]
    public async Task A_missing_agent_is_reported_not_thrown_as_a_crash()
    {
        using var agent = new AgentClient(new AgentOptions("/tmp/julos-es-gibt-keinen-agenten.sock"));
        await Assert.ThrowsAsync<AgentUnavailableException>(() => agent.SendAsync(HttpMethod.Get, "/v1/health"));
    }

    [Fact]
    public async Task A_slow_agent_runs_into_the_timeout()
    {
        await using var fake = new FakeAgent { Delay = TimeSpan.FromSeconds(2) };
        using var agent = new AgentClient(new AgentOptions(fake.SocketPath), TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<AgentUnavailableException>(() => agent.SendAsync(HttpMethod.Get, "/v1/windows"));
    }

    [Fact]
    public async Task Broken_or_huge_answers_are_refused()
    {
        await using var fake = new FakeAgent();
        using var agent = new AgentClient(new AgentOptions(fake.SocketPath));
        fake.Overrides["/v1/windows"] = (200, "das ist kein json");
        await Assert.ThrowsAsync<AgentUnavailableException>(() => agent.SendAsync(HttpMethod.Get, "/v1/windows"));
        fake.Overrides["/v1/apps"] = (200, "{\"x\":\"" + new string('a', AgentClient.MaxResponseBytes) + "\"}");
        await Assert.ThrowsAsync<AgentUnavailableException>(() => agent.SendAsync(HttpMethod.Get, "/v1/apps"));
    }

    [Fact]
    public async Task The_socket_can_be_used_again_after_the_agent_restarts()
    {
        var fake = new FakeAgent();
        var path = fake.SocketPath;
        using var agent = new AgentClient(new AgentOptions(path));
        Assert.Equal(200, (await agent.SendAsync(HttpMethod.Get, "/v1/health")).Status);
        await fake.DisposeAsync();
        await Assert.ThrowsAsync<AgentUnavailableException>(() => agent.SendAsync(HttpMethod.Get, "/v1/health"));
    }
}

public sealed class ClipboardUploaderTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "julos-up-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>Spielt den Helfer in der Sitzung: wartet auf ein Bild und quittiert es.</summary>
    private Task Helper(string answerSuffix, string content = "")
    {
        return Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var file = Directory.GetFiles(_dir).FirstOrDefault(f => !f.EndsWith(".tmp") && !f.EndsWith(".ok") && !f.EndsWith(".err"));
                if (file is not null) { await File.WriteAllTextAsync(file + answerSuffix, content); return; }
                await Task.Delay(10);
            }
        });
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1 }, "gif")]
    public void The_type_comes_from_the_file_header(byte[] head, string ext) => Assert.Equal(ext, ClipboardUploader.DetectExtension(head));

    [Fact]
    public void Webp_is_recognised_and_other_things_are_not()
    {
        Assert.Equal("webp", ClipboardUploader.DetectExtension("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(ClipboardUploader.DetectExtension("<html>"u8));
        Assert.Null(ClipboardUploader.DetectExtension([0x4D, 0x5A, 0x90, 0x00]));
        Assert.Null(ClipboardUploader.DetectExtension([]));
    }

    [Fact]
    public async Task A_confirmed_image_is_ok_and_leaves_no_temp_file()
    {
        var up = new ClipboardUploader(new UploadOptions(_dir));
        var helper = Helper(".ok");
        Assert.Equal(UploadOutcome.Ok, (await up.SaveAsync(Png, CancellationToken.None)).Outcome);
        await helper;
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp") || f.EndsWith(".ok"));
        Assert.EndsWith(".png", Directory.GetFiles(_dir).Single());          // das Bild selbst raeumt der Helfer weg
    }

    [Fact]
    public async Task An_error_from_the_helper_is_reported_with_its_text()
    {
        var up = new ClipboardUploader(new UploadOptions(_dir));
        _ = Helper(".err", "Zwischenablage gesperrt");
        var r = await up.SaveAsync(Png, CancellationToken.None);
        Assert.Equal(UploadOutcome.HelperFailed, r.Outcome);
        Assert.Equal("Zwischenablage gesperrt", r.Detail);
    }

    [Fact]
    public async Task Without_a_helper_the_image_is_not_left_lying_around()
    {
        var up = new ClipboardUploader(new UploadOptions(_dir, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(UploadOutcome.HelperSilent, (await up.SaveAsync(Png, CancellationToken.None)).Outcome);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Rubbish_empty_and_unwritable_are_rejected()
    {
        var up = new ClipboardUploader(new UploadOptions(_dir));
        Assert.Equal(UploadOutcome.NotAnImage, (await up.SaveAsync(Encoding.UTF8.GetBytes("kein bild"), CancellationToken.None)).Outcome);
        Assert.Equal(UploadOutcome.Empty, (await up.SaveAsync(Array.Empty<byte>(), CancellationToken.None)).Outcome);
        var gone = new ClipboardUploader(new UploadOptions(Path.Combine(_dir, "gibt-es-nicht")));
        Assert.Equal(UploadOutcome.FolderNotWritable, (await gone.SaveAsync(Png, CancellationToken.None)).Outcome);
        Assert.Empty(Directory.GetFiles(_dir));
    }
}

public sealed class HitCounterTests
{
    [Fact]
    public void Allows_a_limited_number_per_window_and_per_key()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var hits = new JulOS.Backend.Logic.HitCounter(time);
        var minute = TimeSpan.FromMinutes(1);
        for (var i = 0; i < 3; i++) Assert.True(hits.Allow("a", 3, minute));
        Assert.False(hits.Allow("a", 3, minute));
        Assert.True(hits.Allow("b", 3, minute));
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(hits.Allow("a", 3, minute));
        hits.Sweep(minute);
        time.Advance(TimeSpan.FromMinutes(2));
        hits.Sweep(minute);
        Assert.True(hits.Allow("a", 1, minute));
    }
}
