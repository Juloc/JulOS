using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Folders;

/// <summary>
/// Ordner fuer Verbindungen, Favoriten und "Zuletzt benutzt" je Benutzer auf dem Server (siehe docs/FOLDERS.md). Alles braucht eine Sitzung (ApiGuard);
/// Ordner und Verbindungen anderer Benutzer sind nicht erreichbar (404).
/// </summary>
internal static class FolderEndpoints
{
    private const int MaxImport = 200;

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static FolderDto ToDto(FolderRecord f) => new(f.Id, f.Name, f.Icon, f.Sort);

    public static void MapFolders(this IEndpointRouteBuilder app)
    {
        // Ordner und Einsortierung aller Verbindungen in einer Antwort
        app.MapGet("/api/folders", async (HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var folders = await store.ListAsync(id.UserId, ctx.RequestAborted);
            var placements = await store.PlacementsAsync(id.UserId, ctx.RequestAborted);
            return Results.Json(new OrganisationResponse([.. folders.Select(ToDto)], [.. placements.Select(p => new PlacementDto(p.Id, p.FolderId, p.Favorite, p.LastUsedAt, p.UseCount))]), AppJson.Default.OrganisationResponse);
        });

        app.MapPost("/api/folders", async (FolderRequest body, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (name, icon, error) = FolderRules.Validate(body.Name, body.Icon);
            if (name is null) return Error(400, error!);
            if (await store.CountAsync(id.UserId, ctx.RequestAborted) >= FolderRules.MaxPerUser) return Error(400, "Zu viele Ordner.");
            return Results.Json(ToDto(await store.CreateAsync(id.UserId, name, icon!, ctx.RequestAborted)), AppJson.Default.FolderDto);
        });

        app.MapPut("/api/folders/{folder:guid}", async (Guid folder, FolderRequest body, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (name, icon, error) = FolderRules.Validate(body.Name, body.Icon);
            if (name is null) return Error(400, error!);
            var f = await store.UpdateAsync(id.UserId, folder, name, icon!, ctx.RequestAborted);
            return f is null ? Error(404, "Ordner nicht gefunden.") : Results.Json(ToDto(f), AppJson.Default.FolderDto);
        });

        // Die Verbindungen eines geloeschten Ordners bleiben, ohne Ordner
        app.MapDelete("/api/folders/{folder:guid}", async (Guid folder, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return await store.DeleteAsync(id.UserId, folder, ctx.RequestAborted) ? Results.NoContent() : Error(404, "Ordner nicht gefunden.");
        });

        app.MapPut("/api/folders/order", async (FolderOrderRequest body, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (body.Ids is not { Length: > 0 and <= FolderRules.MaxPerUser }) return Error(400, "Reihenfolge fehlt.");
            await store.ReorderAsync(id.UserId, body.Ids, ctx.RequestAborted);
            return Results.NoContent();
        });

        app.MapPut("/api/connections/{connection:guid}/folder", async (Guid connection, FolderAssignRequest body, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return await store.SetFolderAsync(id.UserId, connection, body.FolderId, ctx.RequestAborted) ? Results.NoContent() : Error(404, "Verbindung oder Ordner nicht gefunden.");
        });

        app.MapPut("/api/connections/{connection:guid}/favorite", async (Guid connection, FavoriteRequest body, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (body.Favorite is not { } on) return Error(400, "Favorit fehlt.");
            return await store.SetFavoriteAsync(id.UserId, connection, on, ctx.RequestAborted) ? Results.NoContent() : Error(404, "Verbindung nicht gefunden.");
        });

        app.MapPost("/api/connections/{connection:guid}/used", async (Guid connection, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return await store.TouchAsync(id.UserId, connection, ctx.RequestAborted) ? Results.NoContent() : Error(404, "Verbindung nicht gefunden.");
        });

        app.MapPost("/api/connections/{connection:guid}/move", async (Guid connection, MoveRequest body, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (body.Delta is not (-1 or 1)) return Error(400, "Richtung fehlt (-1 oder 1).");
            return await store.MoveAsync(id.UserId, connection, body.Delta.Value, ctx.RequestAborted) ? Results.NoContent() : Error(404, "Verbindung nicht gefunden.");
        });

        // Einmalig vom Geraet: seine lokalen Favoriten und Zaehler kommen auf den Server (nur eigene Verbindungen, nie weniger als schon dort)
        app.MapPost("/api/folders/import-local", async (LocalImportRequest body, HttpContext ctx, FolderStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var favorites = (body.Favorites ?? []).Take(MaxImport).Distinct().ToList();
            var uses = new Dictionary<Guid, int>();
            foreach (var (key, n) in (body.Uses ?? []).Take(MaxImport))
                if (Guid.TryParse(key, out var g)) uses[g] = Math.Clamp(n, 0, 100000);
            var last = new Dictionary<Guid, DateTimeOffset>();
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, ms) in (body.Last ?? []).Take(MaxImport))
                if (Guid.TryParse(key, out var g) && ms > 0 && ms < 253402300799000) last[g] = new[] { DateTimeOffset.FromUnixTimeMilliseconds(ms), now }.Min();   // nie in der Zukunft
            await store.ImportLocalAsync(id.UserId, favorites, uses, last, ctx.RequestAborted);
            return Results.NoContent();
        });
    }
}
