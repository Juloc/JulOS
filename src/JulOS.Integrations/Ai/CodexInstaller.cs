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
    public static CodexRelease? ForThisMachine() => OperatingSystem.IsLinux() ? For(RuntimeInformation.OSArchitecture) : null;

    /// <summary>Die Veroeffentlichung fuer eine Bauart (auch fuer das Image des eigenen Containers: deploy/codex/Dockerfile traegt dieselben Werte, ein Test vergleicht sie).</summary>
    public static CodexRelease? For(Architecture architecture) => architecture switch
    {
        Architecture.X64 => new CodexRelease(PinnedVersion, BaseUrl + "codex-x86_64-unknown-linux-musl.tar.gz", "01baa2e47809f8597be5b2f8abd4e4dfe3fb5bcb6e504c2fbaaad97056c15e8d", "codex-x86_64-unknown-linux-musl"),
        Architecture.Arm64 => new CodexRelease(PinnedVersion, BaseUrl + "codex-aarch64-unknown-linux-musl.tar.gz", "bcf50b836385e51bd2976ab2bbe92b1b08a1cbe37712eb8dbe378aa78d24871c", "codex-aarch64-unknown-linux-musl"),
        _ => null,
    };
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

    private static readonly UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Legt einen Ordner samt fehlender Ebenen an, jeden mit dem Recht 0700 (nur der Server): im Datenordner liegt die Anmeldung beim ChatGPT-Konto.</summary>
    public static void CreatePrivate(string path)
    {
        if (Directory.Exists(path)) return;
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) CreatePrivate(parent);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path); else Directory.CreateDirectory(path, Private);
    }

    public string Root => Path.Combine(dataDir, CodexRelease.DataSubfolder);
    public string Home => Path.Combine(Root, "home");
    public string Work => Path.Combine(Root, "work");
    public string? InstalledPath => release is null ? null : Path.Combine(Root, "bin", release.Version, "codex");
    public bool Supported => release is not null;
    public bool IsInstalled => InstalledPath is { } p && File.Exists(p);

    private (long Length, long Ticks, bool Ok)? _checked;

    /// <summary>
    /// Der Pfad des Programms, aber nur wenn die Datei noch die ist, die der Installer nach geprueftem Archiv abgelegt hat (ihre SHA-256 steht neben ihr in codex.sha256). Vor jedem Start
    /// gefragt: ist sie veraendert oder der Vermerk fort, gibt es keinen Pfad (und Failure sagt warum). Neu gerechnet wird nur, wenn sich Groesse oder Zeitstempel aendern.
    /// Die Grenze: wer die Datei und den Vermerk zugleich aendern kann (derselbe Benutzer), schlaegt diese Pruefung; sie faengt Unfaelle und schlichte Eingriffe, keinen entschlossenen Angreifer.
    /// </summary>
    public string? VerifiedPath()
    {
        var path = InstalledPath;
        if (path is null || !File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (_checked is { } c && c.Length == info.Length && c.Ticks == info.LastWriteTimeUtc.Ticks) return c.Ok ? path : null;
        bool ok;
        try
        {
            var recorded = File.ReadAllText(path + ".sha256").Trim();
            using var stream = File.OpenRead(path);
            ok = recorded.Length == 64 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(recorded));
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { ok = false; }
        _checked = (info.Length, info.LastWriteTimeUtc.Ticks, ok);
        if (!ok) _failure = "Die Datei des Codex-Programms stimmt nicht mehr mit der geprüften überein; sie wird nicht gestartet. Zum Neuladen den Ordner ai/codex/bin im Datenordner löschen.";
        return ok ? path : null;
    }
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
        // Fail-closed: eine Pruefsumme, die keine ist, ist kein Grund, irgendetwas zu laden
        if (release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit)) throw new AiProviderException("integrity", "Die Prüfsumme der festgelegten Version ist ungültig; es wird nichts geladen.");
        if (IsInstalled) return;
        var temp = Path.Combine(Root, "tmp");
        CreatePrivate(temp);
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
        CreatePrivate(dir);
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
        // Die SHA-256 der entpackten Datei merken, damit sie vor jedem Start wieder geprueft werden kann (VerifiedPath)
        using (var stream = File.OpenRead(partial)) File.WriteAllText(final + ".sha256", Convert.ToHexStringLower(SHA256.HashData(stream)));
        File.Move(partial, final, overwrite: true);
        _checked = null;
    }
}
