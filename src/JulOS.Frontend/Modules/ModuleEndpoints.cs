using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Modules;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Modules;

/// <summary>
/// Module Caddy, DDNS und Dienste: standardmaessig aus (siehe docs/MODULES.md). Jeder Weg braucht eine Sitzung (ApiGuard);
/// ist der Schalter des Moduls aus, antwortet sein Weg 404. Die Wege nehmen keine Eingaben an: was geprueft wird, steht in den Einstellungen des Servers.
/// </summary>
internal static class ModuleEndpoints
{
    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static IResult Off(string name) => Error(404, $"{name} ist auf diesem Server nicht eingeschaltet.");
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static bool Busy(HttpContext ctx, HitCounter hits, string what, int max) =>
        !hits.Allow($"modules-{what}:" + ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId, max, Minute);

    private static IResult TooMany() => Error(429, "Zu viele Anfragen, bitte kurz warten.");

    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration config)
    {
        var settings = ModuleSettings.From(k => config[k]);
        services.AddSingleton(settings);
        services.AddServices();
        services.AddSingleton(new CaddyModule(settings.Caddy));
        services.AddSingleton(sp =>
        {
            var d = settings.Ddns;
            IPublicIpSource? source = d.RouterUrl is not null ? new RouterIpSource(OutboundHttp.Create(), d.RouterUrl)
                : d.LookupUrl is not null ? new UrlIpSource(OutboundHttp.Create(), d.LookupUrl) : null;
            return new DdnsMonitor(d, source, new SystemResolver(), sp.GetRequiredService<TimeProvider>());
        });
        return services;
    }

    public static void MapModules(this WebApplication app)
    {
        foreach (var w in app.Services.GetRequiredService<ModuleSettings>().Warnings) app.Logger.LogWarning("Module: {Message}", w);

        // Welche Module eingeschaltet sind (die Oberflaeche zeigt nur diese); verraet keine Einstellung ausser dem Schalter
        app.MapGet("/api/modules", (ModuleSettings s) => Results.Json(
            new ModulesResponse(Mode(s.Caddy.Mode), Mode(s.Ddns.Mode), Mode(s.Services.Mode)), AppJson.Default.ModulesResponse));

        app.MapServices();

        app.MapGet("/api/modules/caddy/routes", async (HttpContext ctx, HitCounter hits, CaddyModule caddy, CancellationToken ct) =>
        {
            if (!caddy.Enabled) return Off("Caddy");
            if (Busy(ctx, hits, "caddy", 60)) return TooMany();
            return Results.Json(await caddy.ReadAsync(ct), AppJson.Default.CaddyListing);
        });

        app.MapGet("/api/modules/caddy/reachability", async (HttpContext ctx, HitCounter hits, CaddyModule caddy, CancellationToken ct) =>
        {
            if (!caddy.Enabled) return Off("Caddy");
            if (Busy(ctx, hits, "caddy-check", 12)) return TooMany();
            return Results.Json(await caddy.CheckAsync(ct), AppJson.Default.CaddyReachability);
        });

        // Zustand der Admin-API (und was Caddy ueber seine Ziele zaehlt), Zertifikate aus TLS-Handshakes, Konfiguration ohne Geheimnisse, Zugriffsprotokoll: alle ohne Eingaben
        app.MapGet("/api/modules/caddy/health", async (HttpContext ctx, HitCounter hits, CaddyModule caddy, CancellationToken ct) =>
        {
            if (!caddy.Enabled) return Off("Caddy");
            if (Busy(ctx, hits, "caddy-health", 30)) return TooMany();
            return Results.Json(await caddy.HealthAsync(ct), AppJson.Default.CaddyHealth);
        });

        app.MapGet("/api/modules/caddy/certificates", async (HttpContext ctx, HitCounter hits, CaddyModule caddy, CancellationToken ct) =>
        {
            if (!caddy.Enabled) return Off("Caddy");
            if (Busy(ctx, hits, "caddy-certs", 12)) return TooMany();
            return Results.Json(await caddy.CertificatesAsync(ct), AppJson.Default.CaddyCertificates);
        });

        app.MapGet("/api/modules/caddy/config", async (HttpContext ctx, HitCounter hits, CaddyModule caddy, CancellationToken ct) =>
        {
            if (!caddy.Enabled) return Off("Caddy");
            if (Busy(ctx, hits, "caddy-config", 12)) return TooMany();
            return Results.Json(await caddy.ConfigAsync(ct), AppJson.Default.CaddyConfigText);
        });

        app.MapGet("/api/modules/caddy/log", async (HttpContext ctx, HitCounter hits, CaddyModule caddy, CancellationToken ct) =>
        {
            if (!caddy.Enabled) return Off("Caddy");
            if (Busy(ctx, hits, "caddy-log", 30)) return TooMany();
            return Results.Json(await caddy.LogAsync(ct), AppJson.Default.CaddyLog);
        });

        app.MapGet("/api/modules/ddns/status", async (HttpContext ctx, HitCounter hits, DdnsMonitor ddns, CancellationToken ct) =>
        {
            if (!ddns.Enabled) return Off("DDNS");
            if (Busy(ctx, hits, "ddns", 30)) return TooMany();
            return Results.Json(await ddns.GetAsync(ct), AppJson.Default.DdnsStatus);
        });
    }

    private static string Mode(ModuleMode m) => ModuleModes.Name(m);
}

internal sealed record ModulesResponse(string Caddy, string Ddns, string Services);
