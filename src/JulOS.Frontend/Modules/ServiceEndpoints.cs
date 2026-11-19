using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Modules;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Modules;

internal sealed record ServicesResponse(ServiceRecord[] Services, bool CanEdit, bool KeyStore, int Max);
internal sealed record ServiceStatusesResponse(ServiceStatus[] Statuses);
/// <summary>ApiKey: null = unveraendert lassen, "" = Schluessel entfernen, sonst der neue Schluessel (er wird nur verschluesselt gespeichert und nie ausgeliefert).</summary>
internal sealed record ServiceRequest(string? Name, string? Icon, string? OpenUrl, string? HealthUrl, string? MetricsUrl, ServiceMetric[]? Metrics, int? IntervalSeconds, string? HeaderName, string? ApiKey, string? Template);

/// <summary>
/// Modul "Dienste": eigene Dienste mit Status und Kennzahlen (docs/MODULES.md). JULOS_SERVICES=off|read|control: read zeigt und prueft, control legt an, aendert und loescht.
/// Alles braucht eine Sitzung (ApiGuard) und gilt nur fuer die Dienste des angemeldeten Benutzers. Abgefragt werden nur gespeicherte Adressen.
/// </summary>
internal static class ServiceEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static bool Busy(HttpContext ctx, HitCounter hits, string what, int max) =>
        !hits.Allow($"services-{what}:" + ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId, max, Minute);

    public static void AddServices(this IServiceCollection services)
    {
        services.AddSingleton<ServiceStore>();
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<ModuleSettings>().Services;
            return new ServiceHosts(o.HostList, null);
        });
        services.AddSingleton(sp => new ServiceChecker(sp.GetRequiredService<ServiceHosts>(), time: sp.GetRequiredService<TimeProvider>()));
    }

    public static void MapServices(this IEndpointRouteBuilder app)
    {
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Services");
        var mode = app.ServiceProvider.GetRequiredService<ModuleSettings>().Services.Mode;
        const string Name = "Dienste";

        IResult? Gate(bool write) =>
            mode == ModuleMode.Off ? Error(404, $"{Name} ist auf diesem Server nicht eingeschaltet.")
            : write && mode != ModuleMode.Control ? Error(403, "Dienste sind auf diesem Server nur zum Ansehen eingeschaltet (JULOS_SERVICES=control erlaubt Ändern).")
            : null;

        app.MapGet("/api/modules/services", async (HttpContext ctx, HitCounter hits, ServiceStore store, SecretBox box) =>
        {
            if (Gate(false) is { } no) return no;
            if (Busy(ctx, hits, "list", 60)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var list = await store.ListAsync(id.UserId, ctx.RequestAborted);
            return Results.Json(new ServicesResponse([.. list], mode == ModuleMode.Control, box.Enabled, ServiceRules.MaxPerUser), AppJson.Default.ServicesResponse);
        });

        // Zustand und Kennzahlen aller Dienste des Benutzers (je Dienst so oft neu, wie sein Intervall sagt)
        app.MapGet("/api/modules/services/status", async (HttpContext ctx, HitCounter hits, ServiceStore store, ServiceChecker checker, SecretBox box) =>
        {
            if (Gate(false) is { } no) return no;
            if (Busy(ctx, hits, "status", 60)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var list = await store.ListAsync(id.UserId, ctx.RequestAborted);
            var statuses = await Task.WhenAll(list.Select(s => checker.CheckAsync(s, async ct => KeyOf(box, await store.GetSecretAsync(id.UserId, s.Id, ct), s.Id), false, ctx.RequestAborted)));
            return Results.Json(new ServiceStatusesResponse(statuses), AppJson.Default.ServiceStatusesResponse);
        });

        app.MapPost("/api/modules/services/{service:guid}/check", async (Guid service, HttpContext ctx, HitCounter hits, ServiceStore store, ServiceChecker checker, SecretBox box) =>
        {
            if (Gate(false) is { } no) return no;
            if (Busy(ctx, hits, "check", 20)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (await store.GetAsync(id.UserId, service, ctx.RequestAborted) is not { } s) return Error(404, "Dienst nicht gefunden.");
            var status = await checker.CheckAsync(s, async ct => KeyOf(box, await store.GetSecretAsync(id.UserId, s.Id, ct), s.Id), true, ctx.RequestAborted);
            return Results.Json(status, AppJson.Default.ServiceStatus);
        });

        app.MapPost("/api/modules/services", async (ServiceRequest body, HttpContext ctx, HitCounter hits, ServiceStore store, ServiceHosts policy, SecretBox box) =>
        {
            if (Gate(true) is { } no) return no;
            if (Busy(ctx, hits, "write", 30)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (input, error) = Check(body, policy);
            if (input is null) return Error(400, error!);
            if (body.ApiKey is { Length: > 0 } && !box.Enabled) return Error(409, "Passwörter speichern ist nicht eingerichtet (JULOS_SECRET_KEY fehlt).");
            if (body.ApiKey is { Length: > 0 } && input.HeaderName.Length == 0) return Error(400, "Für den Schlüssel fehlt der Name der Kopfzeile.");
            if (await store.CountAsync(id.UserId, ctx.RequestAborted) >= ServiceRules.MaxPerUser) return Error(400, "Zu viele Dienste.");
            var created = await store.CreateAsync(id.UserId, input, ctx.RequestAborted);
            if (body.ApiKey is { Length: > 0 } key) await store.SetSecretAsync(id.UserId, created.Id, box.Encrypt(key, created.Id), ctx.RequestAborted);
            log.LogInformation("Dienst angelegt: {Service}, Benutzer {User}", created.Id, id.UserName);
            return Results.Json((await store.GetAsync(id.UserId, created.Id, ctx.RequestAborted))!, AppJson.Default.ServiceRecord);
        });

        app.MapPut("/api/modules/services/{service:guid}", async (Guid service, ServiceRequest body, HttpContext ctx, HitCounter hits, ServiceStore store, ServiceHosts policy, ServiceChecker checker, SecretBox box) =>
        {
            if (Gate(true) is { } no) return no;
            if (Busy(ctx, hits, "write", 30)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (input, error) = Check(body, policy);
            if (input is null) return Error(400, error!);
            if (body.ApiKey is { Length: > 0 } && !box.Enabled) return Error(409, "Passwörter speichern ist nicht eingerichtet (JULOS_SECRET_KEY fehlt).");
            if (await store.GetAsync(id.UserId, service, ctx.RequestAborted) is not { } old) return Error(404, "Dienst nicht gefunden.");
            // Ohne Kopfzeile gibt es keinen Platz fuer einen Schluessel: ein gespeicherter wird dann mit entfernt
            var willHaveKey = body.ApiKey is null ? old.HasKey : body.ApiKey.Length > 0;
            if (willHaveKey && input.HeaderName.Length == 0) return Error(400, "Für den Schlüssel fehlt der Name der Kopfzeile.");
            var updated = await store.UpdateAsync(id.UserId, service, input, ctx.RequestAborted);
            if (updated is null) return Error(404, "Dienst nicht gefunden.");
            if (body.ApiKey is { } k) await store.SetSecretAsync(id.UserId, service, k.Length == 0 ? null : box.Encrypt(k, service), ctx.RequestAborted);
            checker.Forget(service);
            return Results.Json((await store.GetAsync(id.UserId, service, ctx.RequestAborted))!, AppJson.Default.ServiceRecord);
        });

        app.MapDelete("/api/modules/services/{service:guid}", async (Guid service, HttpContext ctx, HitCounter hits, ServiceStore store, ServiceChecker checker) =>
        {
            if (Gate(true) is { } no) return no;
            if (Busy(ctx, hits, "write", 30)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!await store.DeleteAsync(id.UserId, service, ctx.RequestAborted)) return Error(404, "Dienst nicht gefunden.");
            checker.Forget(service);
            log.LogInformation("Dienst gelöscht: {Service}, Benutzer {User}", service, id.UserName);
            return Results.NoContent();
        });
    }

    private static (ServiceInput?, string?) Check(ServiceRequest b, ServiceHosts policy) =>
        ServiceRules.Validate(b.Name, b.Icon, b.OpenUrl, b.HealthUrl, b.MetricsUrl, b.Metrics, b.IntervalSeconds, b.HeaderName, b.Template, policy.Allows);

    /// <summary>Der Schluessel im Klartext, nur fuer die eine Abfrage; null, wenn es keinen gibt oder er sich nicht lesen laesst (anderer Server-Schluessel).</summary>
    private static string? KeyOf(SecretBox box, byte[]? sealedKey, Guid id) => sealedKey is null ? null : box.Decrypt(sealedKey, id);
}
