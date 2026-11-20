using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend.Auth;
using JulOS.Frontend.Hosts;

namespace JulOS.Frontend.Push;

/// <summary>
/// Push-Meldungen (docs/PUSH.md): Abos der Geraete, Regeln, Testmeldung, Liste der letzten Meldungen. Alles braucht eine Sitzung (ApiGuard, Aenderungen auch die Sonderkopfzeile und den Origin);
/// ein Abo gehoert dem Benutzer, der es angelegt hat. Der Endpunkt eines Abos kommt vom Browser und wird streng geprueft (https, Port 443, Name, nicht privat: SSRF).
/// </summary>
internal static class PushEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static Guid User(HttpContext ctx) => ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId;

    public static void MapPush(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/push", async (HttpContext ctx, PushStore store) =>
        {
            var user = User(ctx);
            var devices = (await store.SubscriptionsAsync(user, ctx.RequestAborted)).Select(s => new PushDeviceDto(s.Id, s.Label, s.CreatedAt, s.LastOk)).ToArray();
            var log = (await store.LogAsync(user, null, PushStore.ShowLog, ctx.RequestAborted)).Select(r => new AlertLogDto(r.Id, r.At, r.HostId, r.HostName, r.Rule, r.Kind, r.Detail, r.Push)).ToArray();
            return Results.Json(new PushStateResponse(store.Available, PushStore.MaxSubscriptionsPerUser, devices, await store.SettingsAsync(user, ctx.RequestAborted), log), AppJson.Default.PushStateResponse);
        });

        // Der oeffentliche Schluessel des Servers (VAPID) fuer den Browser; beim ersten Aufruf wird das Paar erzeugt. Der private Teil verlaesst den Server nie.
        app.MapGet("/api/push/key", async (HttpContext ctx, PushStore store, HitCounter hits) =>
        {
            if (!hits.Allow("push-key:" + User(ctx), 30, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            if (await store.VapidAsync(ctx.RequestAborted) is not { } key) return Error(503, "Push-Meldungen brauchen den Schluessel des Servers (JULOS_SECRET_KEY oder JULOS_DATA).");
            return Results.Json(new PushKeyResponse(WebPush.B64(key.Public)), AppJson.Default.PushKeyResponse);
        });

        app.MapPost("/api/push/subscriptions", async (PushSubscribeRequest body, HttpContext ctx, PushStore store, HitCounter hits) =>
        {
            var user = User(ctx);
            if (!hits.Allow("push-sub:" + user, 20, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            if (!store.Available) return Error(503, "Push-Meldungen brauchen den Schluessel des Servers.");
            if (PushEndpoint.Parse(body.Endpoint) is null) return Error(400, "Der Endpunkt des Push-Dienstes ist nicht zulaessig.");
            var p256dh = WebPush.FromB64(body.Keys?.P256dh);
            var auth = WebPush.FromB64(body.Keys?.Auth);
            if (!WebPush.IsPublicKey(p256dh) || auth is not { Length: 16 }) return Error(400, "Die Schluessel des Abos sind ungueltig.");
            var label = HostReportParser.Clean(body.Label, 60) ?? "Browser";
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 200) origin = "";
            var (result, id) = await store.SubscribeAsync(user, body.Endpoint, p256dh!, auth, label, origin, ctx.RequestAborted);
            return result switch
            {
                SubscribeResult.TooMany => Error(409, $"Hoechstens {PushStore.MaxSubscriptionsPerUser} Geraete. Bitte erst eins entfernen."),
                SubscribeResult.Taken => Error(409, "Dieses Geraet ist schon einem anderen Benutzer zugeordnet."),
                _ => Results.Json(new PushSubscribeResponse(id, result == SubscribeResult.Created), AppJson.Default.PushSubscribeResponse, statusCode: result == SubscribeResult.Created ? 201 : 200),
            };
        });

        app.MapDelete("/api/push/subscriptions/{id:guid}", async (Guid id, HttpContext ctx, PushStore store) =>
            await store.UnsubscribeAsync(User(ctx), id, ctx.RequestAborted) ? Results.NoContent() : Error(404, "Geraet nicht gefunden."));

        app.MapPut("/api/push/settings", async (AlertSettings body, HttpContext ctx, PushStore store) =>
        {
            var clean = body.Clamped();
            await store.SaveSettingsAsync(User(ctx), clean, ctx.RequestAborted);
            return Results.Json(clean, AppJson.Default.AlertSettings);
        });

        // Eine Testmeldung an alle Geraete des Benutzers; ohne Schluessel des Servers oder ohne Geraet sagt die Antwort das (Devices 0)
        app.MapPost("/api/push/test", async (HttpContext ctx, PushStore store, PushService push, HitCounter hits, TimeProvider time) =>
        {
            var user = User(ctx);
            if (!hits.Allow("push-test:" + user, 5, Minute)) return Error(429, "Zu viele Testmeldungen, bitte kurz warten.");
            var outcome = await push.SendAsync(user, PushPayload.Build([new PushItem("JulOS", "test", "alert", null)]), ctx.RequestAborted);
            await store.AddLogAsync(user, time.GetUtcNow(), null, "JulOS", "test", "alert", null, outcome.Sent > 0 ? "sent" : "none", ctx.RequestAborted);
            return Results.Json(new PushTestResponse(outcome.Sent, outcome.Failed, outcome.Removed, outcome.Devices, outcome.Limited), AppJson.Default.PushTestResponse);
        });
    }
}
