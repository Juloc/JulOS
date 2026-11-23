using JulOS.Backend.Data;
using JulOS.Backend.Services;

namespace JulOS.Frontend.Oidc;

/// <summary>Meldet die Anmelde-Zentrale an (eine Zeile in Program.cs). Alles Weitere steht in diesem Ordner und in docs/SIGN-IN.md.</summary>
internal static class OidcSetup
{
    public static IServiceCollection AddOidc(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<OidcAppStore>();
        services.AddSingleton<OidcFlowStore>();
        services.AddSingleton<OidcKeys>();
        services.AddSingleton(sp => new OidcIssuer(sp.GetRequiredService<InstanceSettings>(), k => config[k]));
        services.AddSingleton<OidcHttp>();
        services.AddSingleton<OidcService>();
        services.AddSingleton<OidcLogoutService>();
        services.AddHostedService<OidcLogoutWorker>();
        // JSON der Verwaltung ohne Reflection: die Typen stehen in OidcJson (AppJson bleibt unberuehrt)
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Add(OidcJson.Default));
        return services;
    }

    /// <summary>Die Wege der Anmelde-Zentrale (oeffentlich, siehe OidcEndpoints) und ihre Verwaltung (hinter ApiGuard, OidcAdminEndpoints).</summary>
    public static void MapOidcAll(this IEndpointRouteBuilder app)
    {
        app.MapOidc();
        app.MapOidcAdmin();
    }
}
