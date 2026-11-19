using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JulOS.Frontend.Hosts;

// Was ein Host-Agent berichtet (docs/HOST-AGENT.md), in der Form, die der Server an den Browser gibt. Der Agent ist nicht der Server: nichts davon wird
// unveraendert durchgereicht. HostReportParser liest die Antwort des Agenten Feld fuer Feld, kuerzt Texte, entfernt Steuerzeichen, begrenzt Zahlen und
// Listen; falsche Typen und fehlende Felder geben Standardwerte, nie einen Fehler im Browser.

internal sealed record HostMemory(long TotalBytes, long AvailableBytes, long SwapTotalBytes, long SwapFreeBytes);
internal sealed record HostDisk(string Mount, string Fs, long TotalBytes, long UsedBytes, long FreeBytes, double Percent);
internal sealed record HostAddress(string? Iface, int Family, string Address, int Prefix);
internal sealed record HostVirt(string Kind, string Name);
internal sealed record HostProxmox(string Product, string? Version);
internal sealed record HostNet(string Iface, long RxBytes, long TxBytes);
/// <summary>Gesundheit der Platten (nur lesend): SMART je Platte (oder warum nicht lesbar), Software-RAID aus /proc/mdstat, ZFS-Pools. Pending: der Agent hat noch nicht gelesen (erste Abfrage).</summary>
internal sealed record HostSmartDevice(string Name, string Health);
internal sealed record HostSmart(bool Available, string? Reason, HostSmartDevice[] Devices);
internal sealed record HostMdArray(string Name, string? Level, string State, int Active, int Total, double? SyncPercent);
internal sealed record HostZpool(string Name, string State);
internal sealed record HostZfs(bool Healthy, HostZpool[] Pools);
internal sealed record HostHealth(bool Pending, HostSmart? Smart, HostMdArray[] Md, HostZfs? Zfs);
internal sealed record HostSysinfo(string? Hostname, string? Os, string? OsId, string? OsVersion, string? Kernel, string? Arch, long? UptimeSeconds, double[]? Load, int CpuCount,
    HostMemory Memory, HostDisk[] Disks, HostAddress[] Addresses, bool RebootRequired, string[] RebootPackages, HostVirt? Virt, HostProxmox? Proxmox,
    HostNet[]? Network = null, HostHealth? Health = null);

internal sealed record HostPackage(string Name, string? From, string To, bool? Security, bool New);
internal sealed record HostUpdates(string Manager, int Count, int? Security, bool SecurityKnown, bool Truncated, HostPackage[] Packages, string[] KeptBack, string[] Held, int Removals,
    DateTimeOffset? ListsCheckedAt, long? ListsAgeSeconds);

internal sealed record HostFailedUnit(string Unit, string Sub, string? Description);
internal sealed record HostServices(string State, int Count, HostFailedUnit[] Failed);

internal sealed record HostContainer(string Id, string Name, string Image, string State, string Status);
internal sealed record HostDocker(bool Available, string? Reason, int Running, int Total, HostContainer[] Containers, bool Truncated, int Restarting = 0);

/// <summary>Antwort der Endpunkte unter /api/hosts/{id}/...: State (ok, offline, no_agent, unsupported, error), Alter der Daten und die Daten selbst (auch aus dem Zwischenspeicher, dann Stale).</summary>
internal sealed record HostStatusResponse(string State, DateTimeOffset? At, bool Stale, string? Error, HostSysinfo? Info);
internal sealed record HostUpdatesResponse(string State, DateTimeOffset? At, bool Stale, string? Error, HostUpdates? Updates);
internal sealed record HostServicesResponse(string State, DateTimeOffset? At, bool Stale, string? Error, HostServices? Services);
internal sealed record HostDockerResponse(string State, DateTimeOffset? At, bool Stale, string? Error, HostDocker? Docker);

internal static class HostReportParser
{
    public const int MaxPackages = 500, MaxItems = 64, MaxContainers = 100;

    /// <summary>Anzeigetext des Agenten: nur druckbare Zeichen (keine Steuer-, Format- und Privatzeichen, keine Zeilenumbrueche), hoechstens <paramref name="max"/> Zeichen; leer wird null.</summary>
    internal static string? Clean(string? raw, int max)
    {
        if (raw is null) return null;
        var sb = new StringBuilder();
        foreach (var rune in raw.EnumerateRunes())
        {
            if (sb.Length >= max) break;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned) continue;
            sb.Append(rune.ToString());
        }
        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? Text(JsonElement o, string name, int max = 100)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString(), max) : null;

    private static long Long(JsonElement o, string name, long max = long.MaxValue)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? Math.Clamp(n, 0, max) : 0;

    private static long? LongOrNull(JsonElement o, string name, long max = long.MaxValue)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? Math.Clamp(n, 0, max) : null;

    private static bool Bool(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static JsonElement Obj(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static IEnumerable<JsonElement> Items(JsonElement o, string name, int max)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Take(max) : [];

    private static string[] Strings(JsonElement o, string name, int max, int length = 80)
        => [.. Items(o, name, max).Where(i => i.ValueKind == JsonValueKind.String).Select(i => Clean(i.GetString(), length)).OfType<string>()];

    /// <summary>Ein Prozentwert: 0 bis 100 mit einer Nachkommastelle.</summary>
    private static double Percent(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? Math.Round(Math.Clamp(d, 0, 100), 1) : 0;

    public static HostSysinfo? Sysinfo(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        double[]? load = null;
        if (root.TryGetProperty("load", out var l) && l.ValueKind == JsonValueKind.Array)
        {
            var nums = l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var d) && double.IsFinite(d)).Take(3).Select(x => Math.Round(Math.Clamp(x.GetDouble(), 0, 100000), 2)).ToArray();
            if (nums.Length > 0) load = nums;
        }
        var mem = Obj(root, "memory");
        var total = Long(mem, "totalBytes");
        var swapTotal = Long(mem, "swapTotalBytes");
        var memory = new HostMemory(total, Math.Min(Long(mem, "availableBytes"), total), swapTotal, Math.Min(Long(mem, "swapFreeBytes"), swapTotal));
        var disks = Items(root, "disks", MaxItems).Where(d => d.ValueKind == JsonValueKind.Object).Select(d => new HostDisk(Text(d, "mount", 120) ?? "?", Text(d, "fs", 16) ?? "", Long(d, "totalBytes"), Long(d, "usedBytes"), Long(d, "freeBytes"), Percent(d, "percent"))).ToArray();
        var addresses = Items(root, "addresses", MaxItems).Where(a => a.ValueKind == JsonValueKind.Object && ValidAddress(Text(a, "address", 45))).Select(a =>
            new HostAddress(Text(a, "iface", 20), Long(a, "family") == 6 ? 6 : 4, Text(a, "address", 45)!, (int)Long(a, "prefix", 128))).ToArray();
        var virt = Obj(root, "virt");
        var vk = Text(virt, "kind", 12);
        var proxmox = Obj(root, "proxmox");
        var product = Text(proxmox, "product", 8);
        return new HostSysinfo(Text(root, "hostname", 64), Text(root, "os", 64), Text(root, "osId", 24), Text(root, "osVersion", 24), Text(root, "kernel", 64), Text(root, "arch", 16),
            LongOrNull(root, "uptimeSeconds", 100L * 365 * 86400), load, (int)Long(root, "cpuCount", 4096), memory, disks, addresses,
            Bool(root, "rebootRequired"), Strings(root, "rebootPackages", 20),
            vk is "none" or "vm" or "container" ? new HostVirt(vk, Text(virt, "name", 24) ?? "") : null,
            product is "ve" or "pbs" or "pmg" ? new HostProxmox(product, Text(proxmox, "version", 24)) : null,
            [.. Items(root, "network", 16).Where(n => n.ValueKind == JsonValueKind.Object && Text(n, "iface", 20) is not null).Select(n => new HostNet(Text(n, "iface", 20)!, Long(n, "rxBytes"), Long(n, "txBytes")))],
            Health(Obj(root, "health")));
    }

    private static HostHealth? Health(JsonElement h)
    {
        if (h.ValueKind != JsonValueKind.Object) return null;
        if (Bool(h, "pending")) return new HostHealth(true, null, [], null);
        HostSmart? smart = null;
        var s = Obj(h, "smart");
        if (s.ValueKind == JsonValueKind.Object)
        {
            var reason = Text(s, "reason", 16);
            smart = new HostSmart(Bool(s, "available"), reason is "no_smartctl" or "no_access" or "no_devices" ? reason : null,
                [.. Items(s, "devices", 16).Where(d => d.ValueKind == JsonValueKind.Object && Text(d, "name", 40) is not null).Select(d =>
                    new HostSmartDevice(Text(d, "name", 40)!, Text(d, "health", 8) is "ok" or "failing" ? Text(d, "health", 8)! : "unknown"))]);
        }
        var md = Items(h, "md", MaxItems).Where(a => a.ValueKind == JsonValueKind.Object && Text(a, "name", 16) is not null).Select(a =>
        {
            var state = Text(a, "state", 12);
            return new HostMdArray(Text(a, "name", 16)!, Text(a, "level", 16), state is "ok" or "degraded" or "recovering" or "inactive" ? state : "inactive", (int)Long(a, "active", 1000), (int)Long(a, "total", 1000),
                a.TryGetProperty("syncPercent", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetDouble(out var d) && double.IsFinite(d) ? Math.Round(Math.Clamp(d, 0, 100), 1) : null);
        }).ToArray();
        HostZfs? zfs = null;
        var z = Obj(h, "zfs");
        if (z.ValueKind == JsonValueKind.Object)
            zfs = new HostZfs(Bool(z, "healthy"), [.. Items(z, "pools", MaxItems).Where(p => p.ValueKind == JsonValueKind.Object && Text(p, "name", 40) is not null).Select(p => new HostZpool(Text(p, "name", 40)!, Text(p, "state", 16) ?? ""))]);
        return new HostHealth(false, smart, md, zfs);
    }

    private static bool ValidAddress(string? text) => text is not null && System.Net.IPAddress.TryParse(text, out _);

    public static HostUpdates? Updates(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var manager = Text(root, "manager", 12);
        if (manager is not ("apt" or "dnf" or "apk" or "pacman")) return null;
        var packages = Items(root, "packages", MaxPackages).Where(p => p.ValueKind == JsonValueKind.Object && Text(p, "name", 80) is not null && Text(p, "to", 64) is not null).Select(p =>
            new HostPackage(Text(p, "name", 80)!, Text(p, "from", 64), Text(p, "to", 64)!,
                p.TryGetProperty("security", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False ? s.ValueKind == JsonValueKind.True : null, Bool(p, "new"))).ToArray();
        var known = Bool(root, "securityKnown");
        var count = (int)Math.Max(packages.Length, Long(root, "count", 1_000_000));
        var checkedAt = LongOrNull(root, "listsCheckedAt", 4_000_000_000);
        var age = LongOrNull(root, "listsAgeSeconds", 100L * 365 * 86400);
        return new HostUpdates(manager, count, known ? (int)Math.Min(Long(root, "security", 1_000_000), count) : null, known, Bool(root, "truncated") || count > packages.Length, packages,
            Strings(root, "keptBack", MaxItems), Strings(root, "held", MaxItems), (int)Long(root, "removals", 100000),
            checkedAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(checkedAt.Value) : null, age);
    }

    public static HostServices? Services(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var failed = Items(root, "failed", MaxItems).Where(f => f.ValueKind == JsonValueKind.Object && Text(f, "unit", 80) is not null)
            .Select(f => new HostFailedUnit(Text(f, "unit", 80)!, Text(f, "sub", 24) ?? "", Text(f, "description", 100))).ToArray();
        return new HostServices(Text(root, "state", 24) ?? "unknown", (int)Math.Max(failed.Length, Long(root, "count", 100000)), failed);
    }

    public static HostDocker? Docker(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!Bool(root, "available"))
        {
            var reason = Text(root, "reason", 16);
            return new HostDocker(false, reason is "no_socket" or "no_access" or "error" ? reason : "error", 0, 0, [], false);
        }
        var containers = Items(root, "containers", MaxContainers).Where(c => c.ValueKind == JsonValueKind.Object).Select(c =>
            new HostContainer(Text(c, "id", 12) ?? "", Text(c, "name", 64) ?? "", Text(c, "image", 100) ?? "", Text(c, "state", 16) ?? "", Text(c, "status", 64) ?? "")).ToArray();
        return new HostDocker(true, null, (int)Long(root, "running", 100000), (int)Long(root, "total", 100000), containers, Bool(root, "truncated"), (int)Long(root, "restarting", 100000));
    }
}
