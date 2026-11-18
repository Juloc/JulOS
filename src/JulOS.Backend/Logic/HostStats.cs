using System.Globalization;

namespace JulOS.Backend.Logic;

/// <summary>CPU-Zaehler aus /proc/stat: Summe aller Zeiten und der Teil, in dem die CPU arbeitete (ohne Leerlauf und Warten auf Platten).</summary>
public readonly record struct CpuCounters(long Busy, long Total, int Cores);

/// <summary>Netzwerk-Zaehler aus /proc/net/dev: empfangene und gesendete Bytes aller Schnittstellen ausser "lo".</summary>
public readonly record struct NetCounters(long Rx, long Tx);

/// <summary>Arbeitsspeicher aus /proc/meminfo in Bytes. Belegt = gesamt minus verfuegbar (wie "free" und die meisten Anzeigen).</summary>
public readonly record struct MemoryUse(long Total, long Used);

/// <summary>Belegung des Datentraegers in Bytes (wie "df": gesamt und davon belegt).</summary>
public readonly record struct DiskUse(long Total, long Used);

/// <summary>Auslese der Linux-Dateien unter /proc. Reine Funktionen auf Text, damit sie ohne Linux geprueft werden koennen.</summary>
public static class ProcParsers
{
    /// <summary>Erste Zeile "cpu  user nice system idle iowait irq softirq steal ...". Die Zeilen "cpuN" zaehlen die Kerne.</summary>
    public static CpuCounters? ParseCpu(string? procStat)
    {
        if (procStat is null) return null;
        CpuCounters? total = null;
        var cores = 0;
        foreach (var line in procStat.Split('\n'))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f[0] != "cpu") { cores++; continue; }
            // user nice system idle iowait irq softirq steal (guest ist schon in user enthalten)
            var v = new long[8];
            for (var i = 0; i < 8 && i + 1 < f.Length; i++)
                if (!long.TryParse(f[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out v[i])) return null;
            var all = v.Sum();
            total = new CpuCounters(all - v[3] - v[4], all, 0);
        }
        return total is { } t ? t with { Cores = cores } : null;
    }

    /// <summary>"MemTotal:  16384000 kB" und "MemAvailable: ..." (Kilobyte, in Wirklichkeit Kibibyte).</summary>
    public static MemoryUse? ParseMemory(string? meminfo)
    {
        if (meminfo is null) return null;
        long? total = null, available = null;
        foreach (var line in meminfo.Split('\n'))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kib(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kib(line);
        }
        if (total is null || available is null || total <= 0) return null;
        return new MemoryUse(total.Value, Math.Clamp(total.Value - available.Value, 0, total.Value));

        static long? Kib(string line)
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return f.Length >= 2 && long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ? kib * 1024 : null;
        }
    }

    /// <summary>Zwei Kopfzeilen, dann "eth0: rxBytes rxPackets ... (8 Felder) txBytes ...".</summary>
    public static NetCounters? ParseNet(string? netDev)
    {
        if (netDev is null) return null;
        long rx = 0, tx = 0;
        var seen = false;
        foreach (var line in netDev.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (line[..colon].Trim() == "lo") continue;
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9) continue;
            if (!long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r) || !long.TryParse(f[8], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) continue;
            rx += r; tx += t; seen = true;
        }
        return seen ? new NetCounters(rx, tx) : null;
    }
}

/// <summary>Woher die Messwerte kommen. In der Anwendung die Dateien unter /proc; in Tests feste Texte.</summary>
public sealed record HostSource(Func<string?> Stat, Func<string?> MemInfo, Func<string?> NetDev, Func<DiskUse?> Disk)
{
    /// <summary>Liest /proc des Rechners, auf dem der Server laeuft (im Container: der Docker-Host, siehe docs/ARCHITECTURE.md), und den Datentraeger des Ordners.</summary>
    public static HostSource Linux(string diskPath, string procRoot = "/proc") => new(
        () => TryRead(Path.Combine(procRoot, "stat")),
        () => TryRead(Path.Combine(procRoot, "meminfo")),
        () => TryRead(Path.Combine(procRoot, "net", "dev")),
        () =>
        {
            try
            {
                var d = new DriveInfo(diskPath);
                return d.TotalSize <= 0 ? null : new DiskUse(d.TotalSize, d.TotalSize - d.TotalFreeSpace);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
        });

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Was die Uebersicht zeigt. Fehlende Werte (null): nicht lesbar oder noch keine zwei Messungen fuer eine Rate.</summary>
public sealed record HostOverview(
    double? CpuPercent, int? Cores, long? MemoryTotal, long? MemoryUsed, long? DiskTotal, long? DiskUsed,
    double? RxBytesPerSecond, double? TxBytesPerSecond, DateTimeOffset? SampledAt);

/// <summary>
/// Misst den Rechner, auf dem der Server laeuft, in kurzen Abstaenden und rechnet aus den Zaehlern Auslastung und Datenraten aus.
/// Eine Rate braucht zwei Messungen; sie bezieht sich auf die letzten paar Sekunden, damit ein einzelner Ausschlag nicht springt.
/// </summary>
public sealed class HostMonitor(HostSource source)
{
    /// <summary>Raten beziehen sich auf die aelteste Messung, die hoechstens so alt ist (mindestens zwei Messungen bleiben immer).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(6);

    private readonly record struct Point(DateTimeOffset At, CpuCounters? Cpu, NetCounters? Net);

    private readonly object _lock = new();
    private readonly List<Point> _points = [];
    private HostOverview _current = new(null, null, null, null, null, null, null, null, null);

    public HostOverview Current { get { lock (_lock) return _current; } }

    public void Sample(DateTimeOffset now)
    {
        var point = new Point(now, ProcParsers.ParseCpu(source.Stat()), ProcParsers.ParseNet(source.NetDev()));
        var mem = ProcParsers.ParseMemory(source.MemInfo());
        var disk = source.Disk();
        lock (_lock)
        {
            _points.Add(point);
            while (_points.Count > 2 && now - _points[1].At > Window) _points.RemoveAt(0);
            var first = _points[0];
            var seconds = (now - first.At).TotalSeconds;
            double? cpu = null, rx = null, tx = null;
            if (seconds > 0 && _points.Count > 1)
            {
                if (first.Cpu is { } a && point.Cpu is { } b && b.Total > a.Total)
                    cpu = Math.Clamp(100.0 * (b.Busy - a.Busy) / (b.Total - a.Total), 0, 100);
                // Ein Zaehler, der kleiner geworden ist (Schnittstelle neu gestartet), ergibt keine Rate
                if (first.Net is { } n0 && point.Net is { } n1 && n1.Rx >= n0.Rx && n1.Tx >= n0.Tx)
                {
                    rx = (n1.Rx - n0.Rx) / seconds;
                    tx = (n1.Tx - n0.Tx) / seconds;
                }
            }
            _current = new HostOverview(cpu, point.Cpu?.Cores, mem?.Total, mem?.Used, disk?.Total, disk?.Used, rx, tx, now);
        }
    }
}
