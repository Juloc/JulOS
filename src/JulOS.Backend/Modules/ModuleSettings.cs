namespace JulOS.Backend.Modules;

/// <summary>
/// Alle Einstellungen der Module an einer Stelle (aus der Umgebung des Servers). Standard: alles aus.
/// JULOS_SERVICES / JULOS_CADDY / JULOS_DDNS = off|read|control; JULOS_SERVICES_HOSTS; JULOS_CADDYFILE, JULOS_CADDY_ADMIN_URL;
/// JULOS_DDNS_HOSTS, JULOS_DDNS_FRITZBOX_URL, JULOS_DDNS_IP_URL, JULOS_DDNS_CACHE_SECONDS. Ungueltige Werte werden mit einer Meldung verworfen.
/// </summary>
public sealed record ServiceOptions(ModuleMode Mode, string? HostList);

public sealed record ModuleSettings(CaddyOptions Caddy, DdnsOptions Ddns, ServiceOptions Services, string[] Warnings)
{
    public static ModuleSettings From(Func<string, string?> get)
    {
        var w = new List<string>();
        var servicesMode = ModuleModes.Parse("JULOS_SERVICES", get("JULOS_SERVICES"), w);
        var caddyMode = ModuleModes.Parse("JULOS_CADDY", get("JULOS_CADDY"), w, controlExists: false);
        var ddnsMode = ModuleModes.Parse("JULOS_DDNS", get("JULOS_DDNS"), w);

        // Warnungen zu Einstellungen eines ausgeschalteten Moduls waeren nur Laerm
        var caddyWarnings = new List<string>();
        var caddyfile = get("JULOS_CADDYFILE")?.Trim();
        var admin = OutboundHttp.ParseHttpUrl("JULOS_CADDY_ADMIN_URL", get("JULOS_CADDY_ADMIN_URL"), caddyWarnings, rootOnly: true);

        var authFile = get("JULOS_CADDY_ADMIN_AUTH_FILE")?.Trim();
        var logFile = get("JULOS_CADDY_LOG_FILE")?.Trim();
        UpstreamTarget? tlsConnect = null;
        if (!string.IsNullOrWhiteSpace(get("JULOS_CADDY_TLS_CONNECT")))
        {
            tlsConnect = UpstreamTarget.Parse(get("JULOS_CADDY_TLS_CONNECT"));
            if (tlsConnect is null) caddyWarnings.Add("JULOS_CADDY_TLS_CONNECT: kein gueltiges Host:Port. Die Einstellung wird nicht benutzt.");
        }
        if (caddyMode != ModuleMode.Off) w.AddRange(caddyWarnings);

        var ddnsWarnings = new List<string>();
        var hosts = DdnsOptions.ParseHosts(get("JULOS_DDNS_HOSTS"), ddnsWarnings);
        var router = OutboundHttp.ParseHttpUrl("JULOS_DDNS_FRITZBOX_URL", get("JULOS_DDNS_FRITZBOX_URL"), ddnsWarnings, rootOnly: true);
        var lookup = OutboundHttp.ParseHttpUrl("JULOS_DDNS_IP_URL", get("JULOS_DDNS_IP_URL"), ddnsWarnings);
        if (router is not null && lookup is not null) { ddnsWarnings.Add("JULOS_DDNS_FRITZBOX_URL und JULOS_DDNS_IP_URL sind beide gesetzt: der Router hat Vorrang."); lookup = null; }
        var cache = int.TryParse(get("JULOS_DDNS_CACHE_SECONDS"), out var s) && s >= 10 ? TimeSpan.FromSeconds(Math.Min(s, 3600)) : DdnsOptions.DefaultCache;
        // Anbieter und automatische Aktualisierung gibt es nur mit control
        DdnsProviderOptions? provider = null;
        var providerName = get("JULOS_DDNS_PROVIDER")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(providerName))
        {
            var tokenFile = get("JULOS_DDNS_TOKEN_FILE")?.Trim();
            var zone = get("JULOS_DDNS_CLOUDFLARE_ZONE")?.Trim();
            var updateUrl = get("JULOS_DDNS_UPDATE_URL")?.Trim();
            switch (providerName)
            {
                case "cloudflare" when !string.IsNullOrEmpty(zone) && !string.IsNullOrEmpty(tokenFile) && zone.All(char.IsAsciiLetterOrDigit): provider = new("cloudflare", zone, tokenFile, null, null); break;
                case "cloudflare": ddnsWarnings.Add("JULOS_DDNS_PROVIDER=cloudflare braucht JULOS_DDNS_CLOUDFLARE_ZONE (die Zonen-Kennung) und JULOS_DDNS_TOKEN_FILE. Der Anbieter wird nicht benutzt."); break;
                case "duckdns" when !string.IsNullOrEmpty(tokenFile): provider = new("duckdns", null, tokenFile, null, null); break;
                case "duckdns": ddnsWarnings.Add("JULOS_DDNS_PROVIDER=duckdns braucht JULOS_DDNS_TOKEN_FILE. Der Anbieter wird nicht benutzt."); break;
                case "generic" when OutboundHttp.ParseHttpUrl("JULOS_DDNS_UPDATE_URL", updateUrl?.Replace("{host}", "h.example").Replace("{ip}", "192.0.2.1"), ddnsWarnings) is not null && updateUrl!.Contains("{host}") && updateUrl.Contains("{ip}"):
                    provider = new("generic", null, null, updateUrl, get("JULOS_DDNS_UPDATE_AUTH_FILE")?.Trim()); break;
                case "generic": ddnsWarnings.Add("JULOS_DDNS_PROVIDER=generic braucht JULOS_DDNS_UPDATE_URL mit {host} und {ip}. Der Anbieter wird nicht benutzt."); break;
                default: ddnsWarnings.Add("JULOS_DDNS_PROVIDER: unbekannter Anbieter (erlaubt: cloudflare, duckdns, generic). Es wird nicht geschrieben."); break;
            }
        }
        if (provider is not null && hosts.Length == 0) { ddnsWarnings.Add("JULOS_DDNS_PROVIDER ist gesetzt, aber JULOS_DDNS_HOSTS ist leer: es gibt nichts zu aktualisieren."); }
        var auto = string.Equals(get("JULOS_DDNS_AUTO")?.Trim(), "on", StringComparison.OrdinalIgnoreCase);
        var autoEvery = int.TryParse(get("JULOS_DDNS_AUTO_SECONDS"), out var autoSeconds) && autoSeconds >= 300 ? TimeSpan.FromSeconds(Math.Min(autoSeconds, 86400)) : TimeSpan.FromMinutes(5);
        if (auto && (ddnsMode != ModuleMode.Control || provider is null)) { ddnsWarnings.Add("JULOS_DDNS_AUTO=on gilt nur mit JULOS_DDNS=control und einem Anbieter."); auto = false; }
        if (ddnsMode != ModuleMode.Off) w.AddRange(ddnsWarnings);

        return new ModuleSettings(
            new CaddyOptions(caddyMode, string.IsNullOrEmpty(caddyfile) ? null : caddyfile, admin, string.IsNullOrEmpty(authFile) ? null : authFile, tlsConnect, string.IsNullOrEmpty(logFile) ? null : logFile),
            new DdnsOptions(ddnsMode, hosts, lookup, router, cache, auto, autoEvery, ddnsMode == ModuleMode.Control ? provider : null),
            new ServiceOptions(servicesMode, string.IsNullOrWhiteSpace(get("JULOS_SERVICES_HOSTS")) ? get("JULOS_ALLOWED_HOSTS") : get("JULOS_SERVICES_HOSTS")),
            [.. w]);
    }
}
