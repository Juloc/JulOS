using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Files;

/// <summary>
/// Dateien, nur lesen: Ordner anzeigen, nach Namen suchen, Datei herunterladen. Alles bezieht sich auf die eine eingestellte Wurzel
/// (JULOS_FILES_ROOT, Standard der Upload-Ordner); die Regeln dazu stehen in <see cref="FileBrowser"/>. Braucht eine Sitzung (ApiGuard).
/// </summary>
internal static class FileEndpoints
{
    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static IResult Off() => Error(404, "Dateien sind auf diesem Server abgeschaltet.");
    private static IResult NotFound() => Error(404, "Nicht gefunden.");

    private static FileItemDto ToDto(FileItem i) => new(i.Name, i.Path, i.IsDirectory, i.Size, i.Modified, i.Items);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Auflisten und Suchen gehen durch viele Verzeichniseintraege (bis 50 000 Namen je Suche): je Benutzer und Minute begrenzt.</summary>
    private static bool Busy(HttpContext ctx, HitCounter hits, string what, int max) =>
        !hits.Allow($"files-{what}:" + ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId, max, Minute);

    private static IResult TooMany() => Error(429, "Zu viele Anfragen, bitte kurz warten.");

    public static void MapFiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/files/list", (string? path, HttpContext ctx, HitCounter hits, FileBrowser files) =>
        {
            if (!files.Enabled) return Off();
            if (Busy(ctx, hits, "list", 240)) return TooMany();
            return files.List(path) is { } l ? Results.Json(new FileListingDto(l.Path, [.. l.Entries.Select(ToDto)], l.Truncated), AppJson.Default.FileListingDto) : NotFound();
        });

        app.MapGet("/api/files/search", (string? path, string? q, HttpContext ctx, HitCounter hits, FileBrowser files, CancellationToken ct) =>
        {
            if (!files.Enabled) return Off();
            if (Busy(ctx, hits, "search", 30)) return TooMany();
            return files.Search(path, q, ct) is { } r ? Results.Json(new FileSearchDto([.. r.Results.Select(ToDto)], r.Truncated), AppJson.Default.FileSearchDto) : Error(400, "Suchtext fehlt oder der Ordner gibt es nicht.");
        });

        app.MapGet("/api/files/download", (string? path, FileBrowser files) =>
        {
            if (!files.Enabled) return Off();
            var stream = files.OpenRead(path);
            if (stream is null) return NotFound();
            // Immer als Anhang und als unbekannter Typ: eine HTML- oder SVG-Datei darf im Browser nie als Seite dieser Adresse laufen
            return Results.File(stream, "application/octet-stream", Path.GetFileName(path!.TrimEnd('/')), enableRangeProcessing: true);
        });
    }
}

internal sealed record FileItemDto(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset Modified, int? Items);
internal sealed record FileListingDto(string Path, FileItemDto[] Entries, bool Truncated);
internal sealed record FileSearchDto(FileItemDto[] Results, bool Truncated);
