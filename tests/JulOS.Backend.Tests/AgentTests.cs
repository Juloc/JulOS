using JulOS.Integrations;
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

public sealed class ClipboardFileUploaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "julos-upf-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>Spielt den Helfer in der Sitzung: wartet auf einen Ordner files/&lt;id&gt; und quittiert ihn.</summary>
    private Task<string?> Helper(string marker, string content = "")
    {
        return Task.Run(async () =>
        {
            var root = Path.Combine(_dir, "files");
            for (var i = 0; i < 300; i++)
            {
                var d = Directory.Exists(root) ? Directory.GetDirectories(root).FirstOrDefault(x => !x.EndsWith(".tmp")) : null;
                if (d is not null) { await File.WriteAllTextAsync(Path.Combine(d, marker), content); return d; }
                await Task.Delay(10);
            }
            return null;
        });
    }

    [Fact]
    public async Task A_confirmed_file_lies_in_its_own_folder_under_a_clean_name()
    {
        var up = new ClipboardFileUploader(new UploadOptions(_dir));
        var helper = Helper(".ok");
        var r = await up.SaveAsync("..\\..\\Bericht: Q3?.txt", "inhalt"u8.ToArray(), CancellationToken.None);
        Assert.Equal(FileOutcome.Ok, r.Outcome);
        var dir = await helper;
        Assert.NotNull(dir);
        Assert.Equal(Path.Combine(_dir, "files"), Path.GetDirectoryName(dir));
        Assert.Equal("Bericht_ Q3_.txt", Directory.GetFiles(dir!).Select(Path.GetFileName).Single(n => !n!.StartsWith('.')));
        Assert.Equal("inhalt", await File.ReadAllTextAsync(Path.Combine(dir!, "Bericht_ Q3_.txt")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "files"), "*.tmp"));
    }

    [Fact]
    public async Task Paths_cannot_leave_the_folder()
    {
        var up = new ClipboardFileUploader(new UploadOptions(_dir, TimeSpan.FromMilliseconds(200)));
        foreach (var name in new[] { "../../evil.txt", "..\\..\\evil.txt", "/etc/cron.d/evil" })
        {
            var r = await up.SaveAsync(name, new byte[] { 1 }, CancellationToken.None);
            Assert.NotEqual(FileOutcome.FolderNotWritable, r.Outcome);
        }
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.txt")));
        Assert.Equal("evil.txt", ClipboardNames.Sanitize("../../evil.txt"));
    }

    [Theory]
    [InlineData("setup.exe", ".exe")]
    [InlineData("Start.DESKTOP", ".desktop")]
    [InlineData("a.ps1", ".ps1")]
    public async Task Names_that_run_when_opened_are_refused_and_nothing_is_written(string name, string ext)
    {
        var up = new ClipboardFileUploader(new UploadOptions(_dir));
        var r = await up.SaveAsync(name, new byte[] { 1, 2 }, CancellationToken.None);
        Assert.Equal((FileOutcome.Blocked, ext), (r.Outcome, r.Detail));
        Assert.False(Directory.Exists(Path.Combine(_dir, "files")));
    }

    [Fact]
    public async Task The_files_folder_has_a_quota_old_folders_make_room_and_new_ones_are_kept()
    {
        var root = Path.Combine(_dir, "files");
        var old = Directory.CreateDirectory(Path.Combine(root, "alt")); File.WriteAllBytes(Path.Combine(old.FullName, "a.bin"), new byte[600]);
        old.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        var fresh = Directory.CreateDirectory(Path.Combine(root, "neu")); File.WriteAllBytes(Path.Combine(fresh.FullName, "b.bin"), new byte[300]);
        var up = new ClipboardFileUploader(new UploadOptions(_dir, TimeSpan.FromMilliseconds(100), FilesQuota: 1000));

        // 900 Bytes liegen schon, 200 neue passen nicht: der Ordner von vorgestern wird geraeumt, danach ist Platz (kein Helfer: Ergebnis "keine Antwort")
        Assert.Equal(FileOutcome.HelperSilent, (await up.SaveAsync("c.bin", new byte[200], CancellationToken.None)).Outcome);
        Assert.False(Directory.Exists(old.FullName));
        Assert.True(Directory.Exists(fresh.FullName));

        // 800 neue passen neben den 300 frischen Bytes nicht: abgelehnt, nichts wird geschrieben, der frische Ordner bleibt
        Assert.Equal(FileOutcome.QuotaExceeded, (await up.SaveAsync("d.bin", new byte[800], CancellationToken.None)).Outcome);
        Assert.Equal([fresh.FullName], Directory.GetDirectories(root));
    }

    [Fact]
    public async Task Empty_too_big_and_nameless_files_are_rejected()
    {
        var up = new ClipboardFileUploader(new UploadOptions(_dir));
        Assert.Equal(FileOutcome.Empty, (await up.SaveAsync("a.txt", Array.Empty<byte>(), CancellationToken.None)).Outcome);
        Assert.Equal(FileOutcome.TooBig, (await up.SaveAsync("a.bin", new byte[ClipboardFileUploader.MaxBytes + 1], CancellationToken.None)).Outcome);
        Assert.Equal(FileOutcome.BadName, (await up.SaveAsync("..", new byte[] { 1 }, CancellationToken.None)).Outcome);
        Assert.Equal(FileOutcome.BadName, (await up.SaveAsync(null, new byte[] { 1 }, CancellationToken.None)).Outcome);
        await File.WriteAllTextAsync(Path.Combine(_dir, "blocker"), "");           // unter einer Datei laesst sich kein Ordner anlegen
        var blocked = new ClipboardFileUploader(new UploadOptions(Path.Combine(_dir, "blocker", "sub")));
        Assert.Equal(FileOutcome.FolderNotWritable, (await blocked.SaveAsync("a.txt", new byte[] { 1 }, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_error_from_the_helper_is_reported_and_the_file_is_removed_and_silence_too()
    {
        var up = new ClipboardFileUploader(new UploadOptions(_dir));
        var helper = Helper(".err", "kein Zugriff");
        var r = await up.SaveAsync("a.txt", new byte[] { 1 }, CancellationToken.None);
        Assert.Equal((FileOutcome.HelperFailed, "kein Zugriff"), (r.Outcome, r.Detail));
        await helper;
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "files")));

        var silent = new ClipboardFileUploader(new UploadOptions(_dir, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(FileOutcome.HelperSilent, (await silent.SaveAsync("b.txt", new byte[] { 1 }, CancellationToken.None)).Outcome);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "files")));
    }
}

public sealed class ClipboardPullerTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "julos-pull-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private Task Helper(string suffix, byte[] content)
    {
        return Task.Run(async () =>
        {
            var dir = Path.Combine(_dir, "pull");
            for (var i = 0; i < 300; i++)
            {
                var req = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.req").FirstOrDefault() : null;
                if (req is not null) { await File.WriteAllBytesAsync(req[..^4] + suffix, content); return; }
                await Task.Delay(10);
            }
        });
    }

    [Fact]
    public async Task The_helper_answers_with_a_png_and_nothing_is_left_behind()
    {
        var helper = Helper(".png", Png);
        var r = await new ClipboardPuller(new UploadOptions(_dir)).PullAsync(CancellationToken.None);
        await helper;
        Assert.Equal(PullOutcome.Image, r.Outcome);
        Assert.Equal(Png, r.Png);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "pull")));
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
