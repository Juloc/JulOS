namespace JulOS.Integrations.Agent;

public enum UploadOutcome { Ok, NotAnImage, Empty, FolderNotWritable, HelperFailed, HelperSilent }

public sealed record UploadResult(UploadOutcome Outcome, string? Detail = null);

/// <param name="FilesQuota">Hoechstens so viele Bytes liegen im Ordner files/ (Standard 2 GiB). Der Helfer raeumt erst nach 7 Tagen auf, bei 20 Uploads je Minute fuellte sich sonst der Datentraeger.</param>
public sealed record UploadOptions(string Directory, TimeSpan? HelperWait = null, long? FilesQuota = null);

/// <summary>
/// Foto-Upload: legt das Bild in einen gemeinsamen Ordner. Ein Helfer in der Sitzung der Ziel-Maschine (tools/julos-clip.py) bemerkt es,
/// legt das Bild in die Zwischenablage und quittiert mit <c>datei.ok</c> oder <c>datei.err</c>. Der Typ wird am Dateikopf erkannt,
/// nicht dem Absender geglaubt.
/// </summary>
public sealed class ClipboardUploader(UploadOptions options)
{
    public const int MaxBytes = 14 * 1024 * 1024;
    public static readonly TimeSpan HelperWait = TimeSpan.FromSeconds(8);
    // als Bytes, nicht als Text: ein UTF-8-Literal wuerde 0x89 in zwei Bytes aufteilen
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    /// <summary>Dateiendung nach Dateikopf, oder null wenn es kein erlaubtes Bild ist (png, jpg, webp, gif).</summary>
    public static string? DetectExtension(ReadOnlySpan<byte> d)
    {
        if (d.StartsWith(PngSignature)) return "png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpg";
        if (d.Length > 12 && d.StartsWith("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8)) return "webp";
        if (d.StartsWith("GIF87a"u8) || d.StartsWith("GIF89a"u8)) return "gif";
        return null;
    }

    public async Task<UploadResult> SaveAsync(ReadOnlyMemory<byte> data, CancellationToken ct, TimeSpan? wait = null)
    {
        if (data.IsEmpty) return new UploadResult(UploadOutcome.Empty);
        var ext = DetectExtension(data.Span);
        if (ext is null) return new UploadResult(UploadOutcome.NotAnImage);

        var name = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))}.{ext}";
        var target = Path.Combine(options.Directory, name);
        var temp = target + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
            File.Move(temp, target);                                   // erst fertig geschrieben, dann sichtbar
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return new UploadResult(UploadOutcome.FolderNotWritable);
        }

        var until = DateTime.UtcNow + (wait ?? options.HelperWait ?? HelperWait);
        try
        {
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(Poll, ct).ConfigureAwait(false);
                if (File.Exists(target + ".ok")) { TryDelete(target + ".ok"); return new UploadResult(UploadOutcome.Ok); }
                if (File.Exists(target + ".err"))
                {
                    string detail;
                    try { detail = (await File.ReadAllTextAsync(target + ".err", ct).ConfigureAwait(false)).Trim(); } catch (IOException) { detail = ""; }
                    TryDelete(target + ".err");
                    return new UploadResult(UploadOutcome.HelperFailed, detail.Length > 80 ? detail[..80] : detail);
                }
            }
        }
        catch (OperationCanceledException) { TryDelete(target); throw; }
        // Niemand hat sich gemeldet: das Bild nicht liegen lassen, sonst taucht es spaeter unerwartet in der Zwischenablage auf
        TryDelete(target);
        return new UploadResult(UploadOutcome.HelperSilent);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
}
