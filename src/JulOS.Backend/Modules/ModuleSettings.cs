namespace JulOS.Backend.Modules;

/// <summary>
/// Alle Einstellungen der Module an einer Stelle (aus der Umgebung des Servers). Standard: alles aus.
/// JULOS_CADDY / JULOS_DDNS = off|read|control; JULOS_CADDYFILE, JULOS_CADDY_ADMIN_URL;
/// JULOS_DDNS_HOSTS, JULOS_DDNS_FRITZBOX_URL, JULOS_DDNS_IP_URL, JULOS_DDNS_CACHE_SECONDS. Ungueltige Werte werden mit einer Meldung verworfen.
/// </summary>
public sealed record ModuleSettings(CaddyOptions Caddy, DdnsOptions Ddns, string[] Warnings)
{
    public static ModuleSettings From(Func<string, string?> get)
    {
        var w = new List<string>();
        var caddyMode = ModuleModes.Parse("JULOS_CADDY", get("JULOS_CADDY"), w);
        var ddnsMode = ModuleModes.Parse("JULOS_DDNS", get("JULOS_DDNS"), w);

        // Warnungen zu Einstellungen eines ausgeschalteten Moduls waeren nur Laerm
        var caddyWarnings = new List<string>();
        var caddyfile = get("JULOS_CADDYFILE")?.Trim();
        var admin = OutboundHttp.ParseHttpUrl("JULOS_CADDY_ADMIN_URL", get("JULOS_CADDY_ADMIN_URL"), caddyWarnings, rootOnly: true);
        if (caddyMode != ModuleMode.Off) w.AddRange(caddyWarnings);

        var ddnsWarnings = new List<string>();
        var hosts = DdnsOptions.ParseHosts(get("JULOS_DDNS_HOSTS"), ddnsWarnings);
        var router = OutboundHttp.ParseHttpUrl("JULOS_DDNS_FRITZBOX_URL", get("JULOS_DDNS_FRITZBOX_URL"), ddnsWarnings, rootOnly: true);
        var lookup = OutboundHttp.ParseHttpUrl("JULOS_DDNS_IP_URL", get("JULOS_DDNS_IP_URL"), ddnsWarnings);
        if (router is not null && lookup is not null) { ddnsWarnings.Add("JULOS_DDNS_FRITZBOX_URL und JULOS_DDNS_IP_URL sind beide gesetzt: der Router hat Vorrang."); lookup = null; }
        var cache = int.TryParse(get("JULOS_DDNS_CACHE_SECONDS"), out var s) && s >= 10 ? TimeSpan.FromSeconds(Math.Min(s, 3600)) : DdnsOptions.DefaultCache;
        if (ddnsMode != ModuleMode.Off) w.AddRange(ddnsWarnings);

        return new ModuleSettings(
            new CaddyOptions(caddyMode, string.IsNullOrEmpty(caddyfile) ? null : caddyfile, admin),
            new DdnsOptions(ddnsMode, hosts, lookup, router, cache),
            [.. w]);
    }
}
