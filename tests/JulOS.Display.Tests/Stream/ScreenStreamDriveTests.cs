using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Rdp.Drive;

namespace JulOS.Integrations.Display.Tests;

/// <summary>
/// Das Laufwerk gegen den echten xrdp-Desktop der Test-Instanz. Ob xrdp den Ordner einhaengt (chansrv, FUSE unter ~/thinclient_drives/JULOS), laesst sich
/// nur im Desktop selbst sehen: der Test haelt die Sitzung mit dem Laufwerk offen (JULOS_TEST_RDP_DRIVE_HOLD Sekunden), und von aussen wird mit
/// "docker exec" nachgesehen (siehe docs/DRIVES.md). JULOS_TEST_RDP_DRIVE_ROOT: der Ordner (Standard: ein neuer in /tmp), JULOS_TEST_RDP_DRIVE_WRITE=1: mit Schreiben.
/// Ohne JULOS_TEST_RDP_DRIVE_HOLD wird der Test uebersprungen.
/// </summary>
public sealed class ScreenStreamDriveTests
{
    [Fact]
    public async Task A_server_folder_is_offered_as_a_drive_and_stays_for_the_time_given()
    {
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_HOST");
        var hold = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_DRIVE_HOLD");
        Assert.SkipWhen(host is null || hold is null, "JULOS_TEST_RDP_HOST und JULOS_TEST_RDP_DRIVE_HOLD nicht gesetzt");
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");

        var root = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_DRIVE_ROOT") ?? Path.Combine(Path.GetTempPath(), "julos-share-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello from the server\n");
        File.WriteAllText(Path.Combine(root, "sub", "inner.txt"), "inner\n");
        File.WriteAllText(Path.Combine(root, ".env"), "SECRET=1\n");
        var writable = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_DRIVE_WRITE") == "1";

        await using var stream = new ScreenStream(
            new RdpOptions { Host = host!, UserName = "ubuntu", Password = "ubuntu", Width = 1024, Height = 600 },
            drive: new SharedDrive(root, writable));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(hold!) + 60));
        var run = stream.RunAsync(cts.Token);
        var until = DateTime.UtcNow.AddSeconds(int.Parse(hold!));
        while (DateTime.UtcNow < until && !run.IsCompleted)
        {
            while (stream.Events.TryRead(out var evt))
            {
                if (evt is ScreenPicture)
                {
                    stream.Acknowledge();
                }
            }

            await Task.Delay(100);
        }

        Assert.False(run.IsCompleted && run.IsFaulted, "die Sitzung endete mit einem Fehler");
        await cts.CancelAsync();
        try { await run; }
        catch (OperationCanceledException) { /* gewollt */ }
    }
}
