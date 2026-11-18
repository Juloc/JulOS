using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

public sealed class HostStatsTests
{
    private const string Stat1 = "cpu  1000 0 500 8000 500 0 0 0 0 0\ncpu0 500 0 250 4000 250 0 0 0 0 0\ncpu1 500 0 250 4000 250 0 0 0 0 0\nintr 123 4 5\nctxt 99\n";
    // 10 s spaeter: +400 user, +100 system, +1500 idle (insgesamt +2000, davon 500 Arbeit = 25 %)
    private const string Stat2 = "cpu  1400 0 600 9500 500 0 0 0 0 0\ncpu0 700 0 300 4750 250 0 0 0 0 0\ncpu1 700 0 300 4750 250 0 0 0 0 0\nintr 200 4 5\n";

    private const string Mem = "MemTotal:       16000000 kB\nMemFree:          800000 kB\nMemAvailable:    4000000 kB\nBuffers: 1 kB\n";

    private static string Net(long rx, long tx) =>
        "Inter-|   Receive                                                |  Transmit\n face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n"
        + "    lo: 999999 10 0 0 0 0 0 0 999999 10 0 0 0 0 0 0\n"
        + $"  eth0: {rx} 10 0 0 0 0 0 0 {tx} 10 0 0 0 0 0 0\n";

    [Fact]
    public void Cpu_counters_are_read_and_idle_and_iowait_are_not_work()
    {
        var c = ProcParsers.ParseCpu(Stat1)!.Value;
        Assert.Equal(10000, c.Total);
        Assert.Equal(1500, c.Busy);
        Assert.Equal(2, c.Cores);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("intr 1 2 3\n")]
    [InlineData("cpu  1 2 x 4 5 6 7 8\n")]
    public void Unreadable_cpu_text_gives_nothing(string? text) => Assert.Null(ProcParsers.ParseCpu(text));

    [Fact]
    public void Memory_used_is_total_minus_available_in_bytes()
    {
        var m = ProcParsers.ParseMemory(Mem)!.Value;
        Assert.Equal(16_000_000L * 1024, m.Total);
        Assert.Equal(12_000_000L * 1024, m.Used);
        Assert.Null(ProcParsers.ParseMemory("MemTotal: 5 kB\n"));       // ohne MemAvailable keine Behauptung
        Assert.Null(ProcParsers.ParseMemory(null));
    }

    [Fact]
    public void Network_counters_skip_loopback_and_add_up_the_rest()
    {
        var text = Net(1000, 2000) + "  eth1: 500 1 0 0 0 0 0 0 700 1 0 0 0 0 0 0\n";
        var n = ProcParsers.ParseNet(text)!.Value;
        Assert.Equal(1500, n.Rx);
        Assert.Equal(2700, n.Tx);
        Assert.Null(ProcParsers.ParseNet("    lo: 1 1 0 0 0 0 0 0 1 1 0 0 0 0 0 0\n"));
        Assert.Null(ProcParsers.ParseNet("Kopfzeile ohne Daten\n"));
    }

    private static HostMonitor Monitor(Func<string?> stat, Func<string?> net, Func<DiskUse?>? disk = null) =>
        new(new HostSource(stat, () => Mem, net, disk ?? (() => new DiskUse(1000, 250))));

    [Fact]
    public void The_first_measurement_has_levels_but_no_rates()
    {
        var m = Monitor(() => Stat1, () => Net(0, 0));
        m.Sample(DateTimeOffset.Parse("2026-11-18T10:00:00Z"));
        var o = m.Current;
        Assert.Null(o.CpuPercent);
        Assert.Null(o.RxBytesPerSecond);
        Assert.Equal(2, o.Cores);
        Assert.Equal(16_000_000L * 1024, o.MemoryTotal);
        Assert.Equal(1000, o.DiskTotal);
        Assert.Equal(250, o.DiskUsed);
    }

    [Fact]
    public void Two_measurements_give_the_load_and_the_rates()
    {
        var stat = Stat1; var net = Net(1_000_000, 100_000);
        var m = Monitor(() => stat, () => net);
        var t0 = DateTimeOffset.Parse("2026-11-18T10:00:00Z");
        m.Sample(t0);
        stat = Stat2; net = Net(1_000_000 + 4_000_000, 100_000 + 500_000);
        m.Sample(t0.AddSeconds(5));
        var o = m.Current;
        Assert.Equal(25.0, o.CpuPercent!.Value, 3);
        Assert.Equal(800_000, o.RxBytesPerSecond!.Value, 3);
        Assert.Equal(100_000, o.TxBytesPerSecond!.Value, 3);
        Assert.Equal(t0.AddSeconds(5), o.SampledAt);
    }

    [Fact]
    public void A_counter_that_went_backwards_gives_no_rate_and_a_stalled_clock_gives_nothing()
    {
        var net = Net(5000, 5000);
        var m = Monitor(() => Stat1, () => net);
        var t0 = DateTimeOffset.Parse("2026-11-18T10:00:00Z");
        m.Sample(t0);
        net = Net(100, 100);                       // Schnittstelle neu gestartet
        m.Sample(t0.AddSeconds(2));
        Assert.Null(m.Current.RxBytesPerSecond);
        Assert.Null(m.Current.CpuPercent);        // Stat1 zweimal: die Gesamtzeit ist nicht gewachsen, also keine Aussage (nicht 0 / 0)
        m.Sample(t0.AddSeconds(2));               // gleiche Uhrzeit: keine Division durch null
        Assert.Null(m.Current.RxBytesPerSecond);
    }

    [Fact]
    public void Rates_refer_to_the_last_few_seconds_only()
    {
        long rx = 0;
        var m = Monitor(() => Stat1, () => Net(rx, 0));
        var t0 = DateTimeOffset.Parse("2026-11-18T10:00:00Z");
        m.Sample(t0);                              // 0 B
        rx = 1_000_000_000; m.Sample(t0.AddSeconds(2));   // ein grosser Sprung, ...
        for (var s = 4; s <= 30; s += 2) m.Sample(t0.AddSeconds(s));   // ... danach nichts mehr
        Assert.Equal(0, m.Current.RxBytesPerSecond);
    }

    [Fact]
    public void Missing_sources_leave_the_values_empty_instead_of_failing()
    {
        var m = new HostMonitor(new HostSource(() => null, () => null, () => null, () => null));
        m.Sample(DateTimeOffset.UtcNow);
        m.Sample(DateTimeOffset.UtcNow.AddSeconds(2));
        var o = m.Current;
        Assert.Null(o.CpuPercent); Assert.Null(o.MemoryTotal); Assert.Null(o.DiskTotal); Assert.Null(o.RxBytesPerSecond);
    }

    [Fact]
    public void The_linux_source_reads_fake_proc_files_and_a_real_folder()
    {
        var dir = Directory.CreateTempSubdirectory("julos-proc-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "net"));
            File.WriteAllText(Path.Combine(dir.FullName, "stat"), Stat1);
            File.WriteAllText(Path.Combine(dir.FullName, "meminfo"), Mem);
            File.WriteAllText(Path.Combine(dir.FullName, "net", "dev"), Net(7, 9));
            var src = HostSource.Linux(dir.FullName, dir.FullName);
            Assert.Equal(1500, ProcParsers.ParseCpu(src.Stat())!.Value.Busy);
            Assert.Equal(7, ProcParsers.ParseNet(src.NetDev())!.Value.Rx);
            var disk = src.Disk()!.Value;
            Assert.True(disk.Total > 0 && disk.Used >= 0 && disk.Used <= disk.Total);
            Assert.Null(HostSource.Linux(Path.Combine(dir.FullName, "gibt-es-nicht"), "/gibt/es/nicht").Stat());
        }
        finally { dir.Delete(true); }
    }
}
