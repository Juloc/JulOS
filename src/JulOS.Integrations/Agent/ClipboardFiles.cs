using System.Security.Cryptography;

namespace JulOS.Integrations.Agent;

public enum FileOutcome { Ok, Empty, TooBig, BadName, Blocked, FolderNotWritable, HelperFailed, HelperSilent }

public sealed record FileResult(FileOutcome Outcome, string? Detail = null);

/// <summary>
/// Datei fuer die Zwischenablage der Ziel-Maschine (fuer die Engine ohne eigenen Dateikanal): die Datei kommt in einen eigenen Ordner
/// <c>files/&lt;id&gt;/&lt;name&gt;</c> des gemeinsamen Upload-Ordners; der Helfer in der Sitzung (tools/julos-clip.py) legt sie als Dateiliste in
/// die Zwischenablage und quittiert mit <c>.ok</c> oder <c>.err</c> in diesem Ordner. Der Name wird bereinigt (nur ein Dateiname) und
/// Endungen, die beim Oeffnen etwas ausfuehren wuerden, werden abgelehnt (<see cref="ClipboardNames"/>).
/// </summary>
public sealed class ClipboardFileUploader(UploadOptions options)
{
    public const int MaxBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    public async Task<FileResult> SaveAsync(string? name, ReadOnlyMemory<byte> data, CancellationToken ct, TimeSpan? wait = null)
    {
        if (data.IsEmpty) return new FileResult(FileOutcome.Empty);
        if (data.Length > MaxBytes) return new FileResult(FileOutcome.TooBig);
        if (ClipboardNames.Sanitize(name) is not { } safe) return new FileResult(FileOutcome.BadName);
        if (ClipboardNames.BlockedExtension(safe) is { } blocked) return new FileResult(FileOutcome.Blocked, blocked);

        var root = Path.GetFullPath(Path.Combine(options.Directory, "files"));
        var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var finalDir = Path.Combine(root, id);
        var tempDir = finalDir + ".tmp";
        var target = Path.GetFullPath(Path.Combine(finalDir, safe));
        // der bereinigte Name hat keinen Pfad; trotzdem: nichts ausserhalb des eigenen Ordners schreiben
        if (!target.StartsWith(finalDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return new FileResult(FileOutcome.BadName);
        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllBytesAsync(Path.Combine(tempDir, safe), data, ct).ConfigureAwait(false);
            Directory.Move(tempDir, finalDir);                          // erst fertig geschrieben, dann sichtbar
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDeleteDir(tempDir);
            return new FileResult(FileOutcome.FolderNotWritable);
        }

        var until = DateTime.UtcNow + (wait ?? options.HelperWait ?? ClipboardUploader.HelperWait);
        try
        {
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(Poll, ct).ConfigureAwait(false);
                if (File.Exists(Path.Combine(finalDir, ".ok"))) return new FileResult(FileOutcome.Ok);
                var err = Path.Combine(finalDir, ".err");
                if (File.Exists(err))
                {
                    string detail;
                    try { detail = (await File.ReadAllTextAsync(err, ct).ConfigureAwait(false)).Trim(); } catch (IOException) { detail = ""; }
                    TryDeleteDir(finalDir);
                    return new FileResult(FileOutcome.HelperFailed, detail.Length > 80 ? detail[..80] : detail);
                }
            }
        }
        catch (OperationCanceledException) { TryDeleteDir(finalDir); throw; }
        TryDeleteDir(finalDir);
        return new FileResult(FileOutcome.HelperSilent);
    }

    private static void TryDeleteDir(string path) { try { Directory.Delete(path, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
}

public enum PullOutcome { Image, Nothing, FolderNotWritable, HelperFailed, HelperSilent }

public sealed record PullResult(PullOutcome Outcome, byte[]? Png = null, string? Detail = null);

/// <summary>
/// Ein Bild aus der Zwischenablage der Ziel-Maschine holen (fuer die Engine, die Bilder nicht selbst meldet): eine Anfrage
/// <c>pull/&lt;id&gt;.req</c> im gemeinsamen Ordner, der Helfer in der Sitzung antwortet mit <c>&lt;id&gt;.png</c>, <c>&lt;id&gt;.none</c> (kein Bild) oder
/// <c>&lt;id&gt;.err</c>. Nur Bilder, nie Dateien oder Pfade; hoechstens <see cref="MaxBytes"/>.
/// </summary>
public sealed class ClipboardPuller(UploadOptions options)
{
    public const int MaxBytes = 14 * 1024 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    public async Task<PullResult> PullAsync(CancellationToken ct, TimeSpan? wait = null)
    {
        var dir = Path.Combine(options.Directory, "pull");
        var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var request = Path.Combine(dir, id + ".req");
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(request + ".tmp", [], ct).ConfigureAwait(false);
            File.Move(request + ".tmp", request);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PullResult(PullOutcome.FolderNotWritable);
        }

        var answers = new[] { ".png", ".none", ".err" }.Select(e => Path.Combine(dir, id + e)).ToArray();
        var until = DateTime.UtcNow + (wait ?? options.HelperWait ?? ClipboardUploader.HelperWait);
        try
        {
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(Poll, ct).ConfigureAwait(false);
                if (File.Exists(answers[0]))
                {
                    var png = await File.ReadAllBytesAsync(answers[0], ct).ConfigureAwait(false);
                    if (png.Length > MaxBytes) return new PullResult(PullOutcome.HelperFailed, Detail: "Bild zu groß");
                    return png.AsSpan().StartsWith(PngSignature) ? new PullResult(PullOutcome.Image, png) : new PullResult(PullOutcome.HelperFailed, Detail: "kein Bild");
                }

                if (File.Exists(answers[1])) return new PullResult(PullOutcome.Nothing);
                if (File.Exists(answers[2]))
                {
                    string detail;
                    try { detail = (await File.ReadAllTextAsync(answers[2], ct).ConfigureAwait(false)).Trim(); } catch (IOException) { detail = ""; }
                    return new PullResult(PullOutcome.HelperFailed, Detail: detail.Length > 80 ? detail[..80] : detail);
                }
            }
        }
        finally
        {
            foreach (var path in answers.Append(request))
            {
                try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        return new PullResult(PullOutcome.HelperSilent);
    }
}
