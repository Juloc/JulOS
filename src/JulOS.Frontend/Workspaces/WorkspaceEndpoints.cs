using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Workspaces;

internal sealed record WorkspaceSaveRequest(int? Version, string? Name, JsonElement? Doc);
internal sealed record WorkspaceSaved(int Version, DateTimeOffset UpdatedAt, int Dropped);
internal sealed record WorkspaceDto(string DeviceId, string Name, int Version, DateTimeOffset UpdatedAt, JsonElement Doc);
internal sealed record WorkspaceDeviceDto(string DeviceId, string Name, int Version, DateTimeOffset UpdatedAt, int Windows, IReadOnlyList<Guid> Connections);
internal sealed record WorkspaceListResponse(IReadOnlyList<WorkspaceDeviceDto> Devices);
/// <summary>Version 0 und kein Dokument: das Geraet ist (inzwischen) vergessen.</summary>
internal sealed record WorkspaceConflict(string Error, string Code, int Version, DateTimeOffset? UpdatedAt, JsonElement? Doc);

/// <summary>
/// Arbeitsplatz je Geraet (docs/MULTI-SCREEN.md): Bildschirme und Fenster, damit beim naechsten Oeffnen alles wieder da ist. Alles braucht eine Sitzung (ApiGuard) und
/// betrifft nur die Geraete des angemeldeten Benutzers; die Geraete-Kennung kommt vom Browser, ist kein Geheimnis und gibt allein nichts frei.
/// </summary>
internal static class WorkspaceEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    public const int WritesPerMinute = 120, ReadsPerMinute = 240;

    private static IResult Fail(int status, string message, string? code = null) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message, Code: code), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static bool Limited(HttpContext ctx, HitCounter hits, string what, int max) =>
        !hits.Allow($"workspace-{what}:" + ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId, max, Minute);

    private static Workspace Stored(string stored)
    {
        using var doc = JsonDocument.Parse(stored);
        return WorkspaceRules.Parse(doc.RootElement).Doc ?? new Workspace([], [], null, null);
    }

    /// <summary>Das gespeicherte Dokument ohne Fenster, deren Verbindung es nicht mehr oder nicht fuer diesen Benutzer gibt (geloescht, oder nie seine).</summary>
    private static Workspace Clean(Workspace stored, IReadOnlySet<Guid> owned) => WorkspaceRules.Keep(stored, owned);

    private static JsonElement ToElement(Workspace w) => JsonDocument.Parse(WorkspaceRules.Write(w)).RootElement.Clone();

    public static void MapWorkspaces(this IEndpointRouteBuilder app)
    {
        // Die Geraete des Benutzers (zum Auswaehlen von "hier weiterarbeiten"): Name, zuletzt benutzt, Zahl der Fenster, offene Verbindungen
        app.MapGet("/api/workspaces", async (HttpContext ctx, WorkspaceStore store, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (Limited(ctx, hits, "read", ReadsPerMinute)) return Fail(429, "Zu viele Anfragen.", "rate_limited");
            var rows = await store.ListAsync(id.UserId, ctx.RequestAborted);
            var docs = rows.Select(r => (Row: r, Doc: Stored(r.Doc))).ToList();
            var owned = await store.OwnedConnectionsAsync(id.UserId, [.. docs.SelectMany(d => WorkspaceRules.ConnectionIds(d.Doc)).Distinct()], ctx.RequestAborted);
            var devices = docs.Select(d =>
            {
                var w = Clean(d.Doc, owned);
                return new WorkspaceDeviceDto(d.Row.DeviceId, d.Row.Name, d.Row.Version, d.Row.UpdatedAt, w.Windows.Count, [.. WorkspaceRules.ConnectionIds(w)]);
            }).ToList();
            return Results.Json(new WorkspaceListResponse(devices), AppJson.Default.WorkspaceListResponse);
        });

        app.MapGet("/api/workspaces/{deviceId}", async (string deviceId, HttpContext ctx, WorkspaceStore store, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!WorkspaceRules.IsDeviceId(deviceId)) return Fail(400, "Geräte-Kennung ungültig.", "invalid_device");
            if (Limited(ctx, hits, "read", ReadsPerMinute)) return Fail(429, "Zu viele Anfragen.", "rate_limited");
            if (await store.GetAsync(id.UserId, deviceId, ctx.RequestAborted) is not { } row) return Fail(404, "Für dieses Gerät ist nichts gespeichert.", "not_found");
            return Results.Json(new WorkspaceDto(row.DeviceId, row.Name, row.Version, row.UpdatedAt, await CleanElementAsync(store, id.UserId, row.Doc, ctx.RequestAborted)), AppJson.Default.WorkspaceDto);
        });

        // Ganzes Dokument speichern. "version" ist die Version, auf der der Browser aufbaut (0: er kennt noch keine). Stimmt sie nicht mit der gespeicherten ueberein,
        // war ein anderes Browserfenster desselben Geraets schneller: 409 mit dem aktuellen Stand; wer trotzdem schreiben will, sendet dieselben Daten mit der neuen Version (der letzte gewinnt).
        app.MapPut("/api/workspaces/{deviceId}", async (string deviceId, WorkspaceSaveRequest body, HttpContext ctx, WorkspaceStore store, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!WorkspaceRules.IsDeviceId(deviceId)) return Fail(400, "Geräte-Kennung ungültig.", "invalid_device");
            if (Limited(ctx, hits, "write", WritesPerMinute)) return Fail(429, "Zu viele Anfragen.", "rate_limited");
            if (body.Version is not >= 0) return Fail(400, "Version fehlt oder ist ungültig.", "invalid_request");
            if (body.Doc is not { } docElement) return Fail(400, "Dokument fehlt.", "invalid_request");
            var (parsed, error) = WorkspaceRules.Parse(docElement);
            if (parsed is null) return Fail(400, error!, "invalid_doc");

            // Fenster mit einer fremden oder unbekannten Verbindung werden verworfen, nicht gespeichert
            var owned = await store.OwnedConnectionsAsync(id.UserId, [.. WorkspaceRules.ConnectionIds(parsed)], ctx.RequestAborted);
            var kept = WorkspaceRules.Keep(parsed, owned);
            string json;
            try { json = WorkspaceRules.Write(kept); } catch (FormatException e) { return Fail(400, e.Message, "invalid_doc"); }

            var saved = await store.SaveAsync(id.UserId, deviceId, WorkspaceRules.CleanName(body.Name), json, body.Version.Value, WorkspaceRules.MaxDevices, ctx.RequestAborted);
            if (saved is { } s) return Results.Json(new WorkspaceSaved(s.Version, s.UpdatedAt, parsed.Windows.Count - kept.Windows.Count), AppJson.Default.WorkspaceSaved);

            var current = await store.GetAsync(id.UserId, deviceId, ctx.RequestAborted);
            var conflict = current is null
                ? new WorkspaceConflict("Dieses Gerät wurde inzwischen vergessen.", "workspace_conflict", 0, null, null)
                : new WorkspaceConflict("Der Arbeitsplatz wurde inzwischen von einem anderen Fenster dieses Geräts gespeichert.", "workspace_conflict", current.Version, current.UpdatedAt,
                    await CleanElementAsync(store, id.UserId, current.Doc, ctx.RequestAborted));
            return Results.Text(JsonSerializer.Serialize(conflict, AppJson.Default.WorkspaceConflict), "application/json", Encoding.UTF8, 409);
        });

        // Das Geraet vergessen (auch wenn es nichts mehr gab: danach ist es weg)
        app.MapDelete("/api/workspaces/{deviceId}", async (string deviceId, HttpContext ctx, WorkspaceStore store, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!WorkspaceRules.IsDeviceId(deviceId)) return Fail(400, "Geräte-Kennung ungültig.", "invalid_device");
            if (Limited(ctx, hits, "write", WritesPerMinute)) return Fail(429, "Zu viele Anfragen.", "rate_limited");
            await store.DeleteAsync(id.UserId, deviceId, ctx.RequestAborted);
            return Results.NoContent();
        });
    }

    private static async Task<JsonElement> CleanElementAsync(WorkspaceStore store, Guid userId, string stored, CancellationToken ct)
    {
        var w = Stored(stored);
        return ToElement(Clean(w, await store.OwnedConnectionsAsync(userId, [.. WorkspaceRules.ConnectionIds(w)], ct)));
    }
}
