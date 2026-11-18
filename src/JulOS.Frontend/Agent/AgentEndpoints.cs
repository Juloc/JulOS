using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Agent;

namespace JulOS.Frontend.Agent;

/// <summary>
/// Agent-API (Fenster, Programme, Modus), Foto-Upload und Diagnose-Log des Clients. Alles braucht eine Sitzung (ApiGuard).
/// Die Wege zum Agenten sind feste Pfade mit geprueften Werten; der Browser kann dem Agenten keine freien Anfragen schicken.
/// </summary>
internal static partial class AgentEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,16}$")] private static partial Regex LogKey();

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static IResult Reply(AgentReply r) => Results.Text(r.Json, "application/json", Encoding.UTF8, r.Status);

    private static async Task<IResult> ViaAgent(AgentClient agent, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        try { return Reply(await agent.SendAsync(method, path, body, ct)); }
        catch (AgentUnavailableException) { return Error(502, "Agent nicht erreichbar."); }
    }

    /// <summary>Uebernimmt nur Schluessel aus Buchstaben/Ziffern/_- (bis 16 Zeichen) mit Zahl, Wahrheitswert oder Text bis 24 Zeichen.</summary>
    private static string SafeEvent(JsonElement ev)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in ev.EnumerateObject())
            {
                if (!LogKey().IsMatch(p.Name)) continue;
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False: p.WriteTo(w); break;
                    case JsonValueKind.String when p.Value.GetString() is { Length: <= 24 }: p.WriteTo(w); break;
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void MapAgent(this IEndpointRouteBuilder app)
    {
        // Ob es einen Agenten gibt; der Client blendet sonst die Fensterleiste aus
        app.MapGet("/api/agent/health", async (AgentClient agent, CancellationToken ct) =>
        {
            try
            {
                var r = await agent.SendAsync(HttpMethod.Get, "/v1/health", null, ct);
                using var doc = JsonDocument.Parse(r.Json);
                using var ms = new MemoryStream();
                await using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteBoolean("available", true);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject()) if (p.Name != "available") p.WriteTo(w);
                    w.WriteEndObject();
                }
                return Results.Text(Encoding.UTF8.GetString(ms.ToArray()), "application/json", Encoding.UTF8);
            }
            catch (AgentUnavailableException) { return Results.Text("""{"available":false}""", "application/json", Encoding.UTF8); }
        });

        app.MapGet("/api/agent/windows", (AgentClient agent, CancellationToken ct) => ViaAgent(agent, HttpMethod.Get, "/v1/windows", null, ct));
        app.MapGet("/api/agent/apps", (AgentClient agent, CancellationToken ct) => ViaAgent(agent, HttpMethod.Get, "/v1/apps", null, ct));

        app.MapPost("/api/agent/windows/{id}/{action}", (string id, string action, ActivateRequest? body, AgentClient agent, CancellationToken ct) =>
        {
            // Fenster-IDs sind Zahlen; nur diese drei Aktionen gibt es
            if (id.Length is < 1 or > 12 || !id.All(char.IsAsciiDigit) || action is not ("activate" or "minimize" or "close")) return Task.FromResult(Error(400, "Ungültige Anfrage."));
            var json = action == "activate" ? JsonSerializer.Serialize(new ActivateRequest(body?.Maximize == true ? true : null), AppJson.Default.ActivateRequest) : "{}";
            return ViaAgent(agent, HttpMethod.Post, $"/v1/windows/{id}/{action}", json, ct);
        });

        app.MapPost("/api/agent/apps/launch", (LaunchRequest body, AgentClient agent, CancellationToken ct) =>
            string.IsNullOrEmpty(body.Id) || body.Id.Length > 200
                ? Task.FromResult(Error(400, "Programm fehlt."))
                : ViaAgent(agent, HttpMethod.Post, "/v1/apps/launch", JsonSerializer.Serialize(body, AppJson.Default.LaunchRequest), ct));

        app.MapPost("/api/agent/mode", (ModeRequest body, AgentClient agent, CancellationToken ct) =>
            body.Mode is "apps" or "desktop"
                ? ViaAgent(agent, HttpMethod.Post, "/v1/mode", JsonSerializer.Serialize(body, AppJson.Default.ModeRequest), ct)
                : Task.FromResult(Error(400, "Ungültiger Modus.")));

        // Foto in die Zwischenablage der Ziel-Maschine: Bild im Body (png, jpg, webp, gif), hoechstens 14 MB, 20 pro Minute und Benutzer
        app.MapPost("/api/upload", async (HttpContext ctx, ClipboardUploader uploader, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("upload:" + id.UserId, 20, Minute)) return Error(429, "Zu viele Uploads, bitte kurz warten.");
            if (ctx.Request.ContentLength is > ClipboardUploader.MaxBytes) return Error(413, "Bild zu groß.");
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = ClipboardUploader.MaxBytes;

            byte[] data;
            try
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                data = ms.ToArray();
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException) { return Error(413, "Bild zu groß."); }

            var r = await uploader.SaveAsync(data, ctx.RequestAborted);
            return r.Outcome switch
            {
                UploadOutcome.Ok => Results.Json(new UploadResponse(true), AppJson.Default.UploadResponse),
                UploadOutcome.Empty or UploadOutcome.NotAnImage => Error(400, "Kein gültiges Bild (png, jpg, webp, gif)."),
                UploadOutcome.FolderNotWritable => Error(500, "Upload-Ordner nicht beschreibbar."),
                UploadOutcome.HelperFailed => Error(500, "Helfer: " + r.Detail),
                _ => Error(504, "Der Helfer in der Sitzung der Ziel-Maschine antwortet nicht (läuft tools/julos-clip.py?)."),
            };
        });

        // Diagnose vom Client: nur Messwerte (Zahlen, Wahrheitswerte, kurze Woerter), kein freier Text; landet im Server-Log
        app.MapPost("/api/log", async (HttpContext ctx, HitCounter hits, ILoggerFactory loggers) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("log:" + id.UserId, 60, Minute)) return Results.StatusCode(429);
            if (ctx.Request.ContentLength is null or > 12 * 1024) return Error(413, "Anfrage zu groß.");

            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Error(400, "Ungültige Anfrage."); }
            using (doc)
            {
                var log = loggers.CreateLogger("Client");
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                    foreach (var ev in events.EnumerateArray().Take(40))
                    {
                        if (ev.ValueKind != JsonValueKind.Object) continue;
                        log.LogInformation("{User} {Event}", id.UserName, SafeEvent(ev));
                    }
            }
            return Results.NoContent();
        });
    }
}
