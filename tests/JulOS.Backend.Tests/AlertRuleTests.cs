using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

/// <summary>Die festen Regeln der Push-Meldungen (docs/PUSH.md) als reine Funktionen: gemeldet wird beim Wechsel, nicht bei jeder Abfrage.</summary>
public sealed class AlertRuleTests
{
    private static readonly Guid Host = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly AlertSettings Cfg = new();

    private static HostObservation Seen(int? security = null, bool? reboot = null, DiskFill[]? disks = null, string[]? failed = null, string[]? running = null, string[]? stopped = null, bool online = true)
        => new(Host, "Heimserver", online, security, reboot, disks, failed, running, stopped);

    /// <summary>Spielt Abfragen im Abstand von 5 Minuten durch und gibt je Abfrage die Meldungen als "regel:art" zurueck.</summary>
    private static List<string[]> Run(AlertSettings cfg, params HostObservation[] steps) => Run(cfg, TimeSpan.FromMinutes(5), steps);

    private static List<string[]> Run(AlertSettings cfg, TimeSpan step, params HostObservation[] steps)
    {
        var state = new HostAlertState();
        var now = T0;
        var all = new List<string[]>();
        foreach (var o in steps)
        {
            var (events, next) = AlertRules.Evaluate(o, state, cfg, now);
            all.Add([.. events.Select(e => $"{e.Rule}:{e.Kind}")]);
            state = next;
            now += step;
        }
        return all;
    }

    [Fact]
    public void A_full_disk_is_reported_once_per_mount_and_recovery_is_off_by_default()
    {
        DiskFill[] D(double root, double data) => [new("/", root), new("/data", data)];
        var r = Run(Cfg, TimeSpan.FromHours(1), Seen(disks: D(50, 89)), Seen(disks: D(91, 89)), Seen(disks: D(92, 89)), Seen(disks: D(60, 89)), Seen(disks: D(95, 91)));
        Assert.Equal(["", "disk:alert", "", "", "disk:alert"], r.Select(x => string.Join(',', x)));
    }

    [Fact]
    public void A_disk_that_hovers_at_the_threshold_does_not_flap_and_recovery_can_be_switched_on()
    {
        DiskFill[] D(double p) => [new("/", p)];
        var cfg = Cfg with { RecoverDisk = true };
        var r = Run(cfg, TimeSpan.FromHours(1), Seen(disks: D(91)), Seen(disks: D(89)), Seen(disks: D(90.5)), Seen(disks: D(80)), Seen(disks: D(91)));
        Assert.Equal(["disk:alert", "", "", "disk:ok", "disk:alert"], r.Select(x => string.Join(',', x)));      // 89 liegt in der Spanne von 2 Punkten: noch "voll"
    }

    [Fact]
    public void New_security_updates_are_reported_when_the_count_rises_and_the_first_reading_is_only_the_baseline()
    {
        var r = Run(Cfg, TimeSpan.FromHours(1), Seen(security: 3), Seen(security: 3), Seen(security: 5), Seen(security: 5), Seen(security: 0), Seen(security: 1));
        Assert.Equal(["", "", "security:alert", "", "", "security:alert"], r.Select(x => string.Join(',', x)));
    }

    [Fact]
    public void Reboot_is_reported_when_it_appears_not_while_it_stays()
    {
        var r = Run(Cfg, TimeSpan.FromHours(1), Seen(reboot: false), Seen(reboot: true), Seen(reboot: true), Seen(reboot: false), Seen(reboot: true));
        Assert.Equal(["", "reboot:alert", "", "", "reboot:alert"], r.Select(x => string.Join(',', x)));
    }

    [Fact]
    public void An_agent_is_reported_offline_only_after_the_set_time_and_online_again_by_default()
    {
        var off = Seen(online: false);
        // Abfragen alle 5 Minuten: offline ab Minute 0, Meldung ab Minute 10 (Standard), einmal; dann wieder online
        var r = Run(Cfg, off, off, off, off, Seen(), Seen());
        Assert.Equal(["", "", "offline:alert", "", "offline:ok", ""], r.Select(x => string.Join(',', x)));
        Assert.Equal([[], [], [], []], Run(Cfg with { OfflineMinutes = 60 }, off, off, off, off));
        Assert.Equal(["", "", "offline:alert", "", "", ""], Run(Cfg with { RecoverOffline = false }, off, off, off, off, Seen(), Seen()).Select(x => string.Join(',', x)));
    }

    [Fact]
    public void A_failed_unit_or_a_stopped_container_that_was_running_is_reported_once()
    {
        var r = Run(Cfg, TimeSpan.FromHours(1),
            Seen(failed: [], running: ["web", "db"], stopped: ["old"]),        // "old" lief nie: kein Anlass
            Seen(failed: [], running: ["web"], stopped: ["db", "old"]),        // db lief und ist gestoppt
            Seen(failed: [], running: ["web"], stopped: ["db", "old"]),        // bleibt gestoppt: nichts Neues
            Seen(failed: ["backup.service"], running: ["web"], stopped: ["db", "old"]),
            Seen(failed: ["backup.service"], running: ["web"], stopped: ["db", "old"]));
        Assert.Equal(["", "service:alert", "", "service:alert", ""], r.Select(x => string.Join(',', x)));
    }

    [Fact]
    public void Something_new_during_the_pause_is_not_lost_but_comes_after_it()
    {
        var r = Run(Cfg, TimeSpan.FromMinutes(10),
            Seen(disks: [new("/", 95)]),                                        // Minute 0: Meldung
            Seen(disks: [new("/", 95), new("/data", 96)]),                      // Minute 10: neu, aber Pause
            Seen(disks: [new("/", 95), new("/data", 96)]),                      // Minute 20: Pause
            Seen(disks: [new("/", 95), new("/data", 96)]),                      // Minute 30: Pause vorbei
            Seen(disks: [new("/", 95), new("/data", 96)]));
        Assert.Equal(["disk:alert", "", "", "disk:alert", ""], r.Select(x => string.Join(',', x)));
    }

    [Fact]
    public void A_switched_off_rule_stays_silent_but_does_not_flood_when_switched_on()
    {
        var off = Cfg with { Disk = false };
        var state = new HostAlertState();
        var (events, next) = AlertRules.Evaluate(Seen(disks: [new("/", 97)]), state, off, T0);
        Assert.Empty(events);
        Assert.Equal(["/"], next.DiskHigh);
        Assert.Empty(AlertRules.Evaluate(Seen(disks: [new("/", 97)]), next, Cfg, T0.AddHours(1)).Events);
    }

    [Fact]
    public void Unknown_values_keep_the_old_state()
    {
        var state = new HostAlertState { DiskHigh = ["/"], Security = 4, Failed = ["x.service"] };
        var (events, next) = AlertRules.Evaluate(Seen(), state, Cfg, T0);
        Assert.Empty(events);
        Assert.Equal(["/"], next.DiskHigh);
        Assert.Equal(4, next.Security);
        Assert.Equal(["x.service"], next.Failed);
    }

    [Theory]
    [InlineData(23, 0, true)]     // 22 bis 7 Uhr, ueber Mitternacht
    [InlineData(3, 0, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(21, 0, false)]
    [InlineData(22, 0, true)]
    [InlineData(21, 60, true)]    // Zone UTC+1: 21 Uhr UTC ist 22 Uhr dort
    [InlineData(6, 60, false)]
    public void Quiet_hours_wrap_around_midnight_in_the_users_zone(int utcHour, int offset, bool quiet)
    {
        var cfg = new AlertSettings(Quiet: true, QuietFrom: 22, QuietTo: 7, ZoneOffset: offset);
        Assert.Equal(quiet, AlertRules.InQuietHours(cfg, new DateTimeOffset(2026, 11, 20, utcHour, 30, 0, TimeSpan.Zero)));
        Assert.False(AlertRules.InQuietHours(cfg with { Quiet = false }, new DateTimeOffset(2026, 11, 20, utcHour, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Settings_from_the_browser_are_clamped()
    {
        var c = new AlertSettings(DiskPercent: 5, OfflineMinutes: 0, QuietFrom: 99, QuietTo: -3, ZoneOffset: 9999, Zone: "../../etc/passwd").Clamped();
        Assert.Equal((50, 5, 23, 0, 14 * 60, null), (c.DiskPercent, c.OfflineMinutes, c.QuietFrom, c.QuietTo, c.ZoneOffset, c.Zone));
        Assert.Equal("Europe/Berlin", new AlertSettings(Zone: "Europe/Berlin").Clamped().Zone);
    }
}
