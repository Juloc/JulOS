using System.Text.Json;

namespace JulOS.Integrations.Agent;

/// <summary>
/// Foto, Datei und Zwischenablage ueber die Verbindung eines Agenten (statt eines gemeinsamen Ordners). Geprueft wird hier auf dem Server, bevor
/// etwas den Server verlaesst (Bildkopf, Groesse, Dateiname und Endung); der Agent prueft noch einmal, schreibt in seinen eigenen Ordner und
/// spricht lokal mit dem Zwischenablage-Helfer. Die Ergebnisse sind dieselben wie bei der Variante mit gemeinsamem Ordner.
/// </summary>
public static class LinkTransfers
{
    public static async Task<UploadResult> SaveImageAsync(AgentLink link, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.IsEmpty) return new UploadResult(UploadOutcome.Empty);
        if (data.Length > ClipboardUploader.MaxBytes || ClipboardUploader.DetectExtension(data.Span) is null) return new UploadResult(UploadOutcome.NotAnImage);
        var r = await link.PutAsync("image", "", data, ct).ConfigureAwait(false);
        return r.Status switch
        {
            200 => new UploadResult(UploadOutcome.Ok),
            504 => new UploadResult(UploadOutcome.HelperSilent),
            _ => new UploadResult(UploadOutcome.HelperFailed, Detail(r.Json)),
        };
    }

    public static async Task<FileResult> SaveFileAsync(AgentLink link, string? name, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.IsEmpty) return new FileResult(FileOutcome.Empty);
        if (data.Length > ClipboardFileUploader.MaxBytes) return new FileResult(FileOutcome.TooBig);
        if (ClipboardNames.Sanitize(name) is not { } safe) return new FileResult(FileOutcome.BadName);
        if (ClipboardNames.BlockedExtension(safe) is { } blocked) return new FileResult(FileOutcome.Blocked, blocked);
        var r = await link.PutAsync("file", safe, data, ct).ConfigureAwait(false);
        return r.Status switch
        {
            200 => new FileResult(FileOutcome.Ok),
            504 => new FileResult(FileOutcome.HelperSilent),
            _ => new FileResult(FileOutcome.HelperFailed, Detail(r.Json)),
        };
    }

    public static async Task<PullResult> PullAsync(AgentLink link, CancellationToken ct)
    {
        var r = await link.PullAsync(ClipboardPuller.MaxBytes, ct).ConfigureAwait(false);
        return r.Reply.Status switch
        {
            200 when r.Blob is { } png && png.AsSpan().StartsWith(PngSignature) => new PullResult(PullOutcome.Image, png),
            200 => new PullResult(PullOutcome.HelperFailed, Detail: "kein Bild"),
            204 => new PullResult(PullOutcome.Nothing),
            504 => new PullResult(PullOutcome.HelperSilent),
            _ => new PullResult(PullOutcome.HelperFailed, Detail: Detail(r.Reply.Json)),
        };
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Der Fehlertext des Agenten, hoechstens 80 Zeichen.</summary>
    private static string Detail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                var s = new string(e.GetString()!.Where(c => !char.IsControl(c)).ToArray());
                return s.Length > 80 ? s[..80] : s;
            }
        }
        catch (JsonException) { }
        return "";
    }
}
