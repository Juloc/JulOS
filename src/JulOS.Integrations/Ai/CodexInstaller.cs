using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace JulOS.Integrations.Ai;

/// <summary>
/// Welche Version des Codex-Programms JulOS laedt und wo sie liegt. Version, Adresse und Pruefsumme stehen nur hier. Die Pruefsummen sind die SHA-256 der Dateien der Veroeffentlichung
/// rust-v{Version} auf GitHub (wie GitHub sie fuer das Archiv nennt); beim Aendern der Version werden beide zusammen ersetzt.
/// </summary>
public sealed record CodexRelease(string Version, string Url, string Sha256, string EntryName)
{
    public const string PinnedVersion = "0.171.0";

    /// <summary>Unterordner des Datenordners (JULOS_DATA) fuer alles der Bruecke: Programm, Anmeldung (home), Arbeitsordner. Nie im Image.</summary>
    public const string DataSubfolder = "ai/codex";

    private const string BaseUrl = "https://github.com/openai/codex/releases/download/rust-v" + PinnedVersion + "/";

    /// <summary>Die Veroeffentlichung fuer diesen Rechner (Linux, x64 oder arm64, statisch gebunden), sonst null.</summary>
    public static CodexRelease? ForThisMachine() => OperatingSystem.IsLinux() ? RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => new CodexRelease(PinnedVersion, BaseUrl + "codex-x86_64-unknown-linux-musl.tar.gz", "01baa2e47809f8597be5b2f8abd4e4dfe3fb5bcb6e504c2fbaaad97056c15e8d", "codex-x86_64-unknown-linux-musl"),
        Architecture.Arm64 => new CodexRelease(PinnedVersion, BaseUrl + "codex-aarch64-unknown-linux-musl.tar.gz", "bcf50b836385e51bd2976ab2bbe92b1b08a1cbe37712eb8dbe378aa78d24871c", "codex-aarch64-unknown-linux-musl"),
        _ => null,
    } : null;
}

/// <summary>
/// Laedt das Codex-Programm erst, wenn jemand es einschaltet, prueft die SHA-256 des Archivs und entpackt genau eine Datei an einen festen Ort im Datenordner:
/// {Daten}/ai/codex/bin/{Version}/codex. Kein Pfad aus dem Archiv wird benutzt; eine falsche Pruefsumme loescht den Download und bricht ab. Mehrere gleichzeitige Aufrufe teilen einen Download.
/// </summary>
public sealed class CodexInstaller(string dataDir, CodexRelease? release, HttpMessageHandler? handler = null, long maxBytes = 400L << 20)
{
    private readonly object _lock = new();
    private Task? _running;
    private string? _failure;

    public string Root => Path.Combine(dataDir, CodexRelease.DataSubfolder);
    public string Home => Path.Combine(Root, "home");
    public string Work => Path.Combine(Root, "work");
    public string? InstalledPath => release is null ? null : Path.Combine(Root, "bin", release.Version, "codex");
    public bool Supported => release is not null;
    public bool IsInstalled => InstalledPath is { } p && File.Exists(p);
    public bool IsDownloading { get { lock (_lock) return _running is { IsCompleted: false }; } }
    public string? Failure => _failure;

    /// <summary>Startet den Download im Hintergrund, falls noetig; gibt die Aufgabe zurueck (fertig, wenn das Programm liegt oder der Versuch fehlschlug).</summary>
    public Task StartAsync()
    {
        lock (_lock)
        {
            if (IsInstalled) return Task.CompletedTask;
            if (_running is { IsCompleted: false }) return _running;
            _failure = null;
            return _running = Task.Run(async () =>
            {
                try { await InstallAsync(CancellationToken.None); }
                catch (Exception e) when (e is not OperationCanceledException) { _failure = e is AiProviderException ? e.Message : "Der Download ist fehlgeschlagen."; }
            });
        }
    }

    public async Task InstallAsync(CancellationToken ct)
    {
        if (release is null) throw new AiProviderException("provider_unavailable", "Für diesen Rechner gibt es kein Codex-Programm.");
        if (IsInstalled) return;
        var temp = Path.Combine(Root, "tmp");
        Directory.CreateDirectory(temp);
        var archive = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".tar.gz");
        try
        {
            await DownloadAsync(release, archive, ct);
            Extract(release, archive);
            // Aeltere Versionen werden nicht mehr gebraucht
            var bin = Path.Combine(Root, "bin");
            foreach (var old in Directory.GetDirectories(bin).Where(d => Path.GetFileName(d) != release.Version)) Directory.Delete(old, recursive: true);
        }
        finally { try { File.Delete(archive); } catch (IOException) { } }
    }

    private async Task DownloadAsync(CodexRelease r, string target, CancellationToken ct)
    {
        using var http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new AiProviderException("provider_unavailable", $"Der Download antwortete mit {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is { } len && len > maxBytes) throw new AiProviderException("provider_error", "Der Download ist größer als erlaubt.");
        using var sha = SHA256.Create();
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920]; long total = 0; int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > maxBytes) throw new AiProviderException("provider_error", "Der Download ist größer als erlaubt.");
                sha.TransformBlock(buffer, 0, n, null, 0);
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            sha.TransformFinalBlock([], 0, 0);
        }
        if (!CryptographicOperations.FixedTimeEquals(sha.Hash!, Convert.FromHexString(r.Sha256)))
            throw new AiProviderException("integrity", "Die Prüfsumme des Downloads stimmt nicht; er wurde verworfen.");
    }

    /// <summary>Holt die eine Datei des Archivs (nur ihr Name zaehlt, nie ein Pfad daraus) und legt sie mit dem Recht zum Ausfuehren fuer den Server ab.</summary>
    private void Extract(CodexRelease r, string archive)
    {
        var dir = Path.Combine(Root, "bin", r.Version);
        var final = Path.Combine(dir, "codex");
        Directory.CreateDirectory(dir);
        var partial = final + ".part";
        using (var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
        {
            var found = false;
            while (tar.GetNextEntry(copyData: false) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Path.GetFileName(entry.Name) != r.EntryName) continue;
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                using (var output = new FileStream(partial, options)) entry.DataStream!.CopyTo(output);
                found = true;
                break;
            }
            if (!found) throw new AiProviderException("provider_error", "Im Archiv fehlt das Codex-Programm.");
        }
        File.Move(partial, final, overwrite: true);
    }
}
