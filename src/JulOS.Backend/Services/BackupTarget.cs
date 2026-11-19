using System.Globalization;
using JulOS.Backend.Logic;

namespace JulOS.Backend.Services;

public enum BackupCopyMode { Off, Path, Sftp }

/// <summary>
/// Einstellungen des zweiten Backup-Ziels (siehe docs/BACKUP.md), optional und standardmaessig aus.
/// <c>JULOS_BACKUP_COPY</c>: <c>off</c> | <c>path</c> (zusaetzlicher eingebundener Ordner, zum Beispiel ein NAS) | <c>sftp</c> (Server im Netz, Schluessel oder Passwort aus einer Datei).
/// Passwoerter und Schluessel stehen nie in Umgebungsvariablen, nur ihre Dateipfade.
/// </summary>
public sealed record BackupCopyOptions(BackupCopyMode Mode, string? Path = null, string? Host = null, int Port = 22, string? User = null, string? RemotePath = null,
    string? KeyFile = null, string? KeyPassphraseFile = null, string? PasswordFile = null, string? HostKeyPin = null, string? HostKeyFile = null)
{
    public const string HostKeyFileName = "backup-sftp-hostkey";
    public static readonly BackupCopyOptions Off = new(BackupCopyMode.Off);
    public bool Enabled => Mode != BackupCopyMode.Off;

    /// <summary>Kurzer Name des Ziels fuer Log und Anzeige, ohne Zugangsdaten.</summary>
    public string Describe() => Mode switch
    {
        BackupCopyMode.Path => Path ?? "",
        BackupCopyMode.Sftp => $"sftp://{User}@{Host}{(Port == 22 ? "" : ":" + Port.ToString(CultureInfo.InvariantCulture))}{(RemotePath!.StartsWith('/') ? "" : "/")}{RemotePath}",
        _ => "",
    };

    /// <summary>Liest die Einstellungen ueber <paramref name="get"/> (Umgebung oder Konfiguration). Falsche Angaben sind ein Fehler beim Start, kein stilles Ignorieren.</summary>
    public static BackupCopyOptions Parse(Func<string, string?> get, string localDir, string? dataDir)
    {
        string? V(string name) => string.IsNullOrWhiteSpace(get(name)) ? null : get(name)!.Trim();
        var mode = (V("JULOS_BACKUP_COPY") ?? "off").ToLowerInvariant();
        switch (mode)
        {
            case "off": return Off;
            case "path":
            {
                var path = V("JULOS_BACKUP_COPY_PATH") ?? throw new InvalidOperationException("JULOS_BACKUP_COPY=path braucht JULOS_BACKUP_COPY_PATH (den eingebundenen Ordner).");
                if (System.IO.Path.GetFullPath(path).TrimEnd('/') == System.IO.Path.GetFullPath(localDir).TrimEnd('/'))
                    throw new InvalidOperationException("JULOS_BACKUP_COPY_PATH ist derselbe Ordner wie das lokale Backup: das wäre kein zweites Ziel.");
                return new BackupCopyOptions(BackupCopyMode.Path, Path: path);
            }
            case "sftp":
            {
                var host = V("JULOS_BACKUP_SFTP_HOST") ?? throw new InvalidOperationException("JULOS_BACKUP_COPY=sftp braucht JULOS_BACKUP_SFTP_HOST.");
                var user = V("JULOS_BACKUP_SFTP_USER") ?? throw new InvalidOperationException("JULOS_BACKUP_COPY=sftp braucht JULOS_BACKUP_SFTP_USER.");
                var path = V("JULOS_BACKUP_SFTP_PATH") ?? throw new InvalidOperationException("JULOS_BACKUP_COPY=sftp braucht JULOS_BACKUP_SFTP_PATH (Ordner auf dem Ziel, muss existieren).");
                var port = 22;
                if (V("JULOS_BACKUP_SFTP_PORT") is { } p && (!int.TryParse(p, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
                    throw new InvalidOperationException("JULOS_BACKUP_SFTP_PORT muss eine Portnummer sein.");
                var key = V("JULOS_BACKUP_SFTP_KEY_FILE");
                var pwd = V("JULOS_BACKUP_SFTP_PASSWORD_FILE");
                if (key is null && pwd is null) throw new InvalidOperationException("JULOS_BACKUP_COPY=sftp braucht JULOS_BACKUP_SFTP_KEY_FILE (privater Schlüssel, bevorzugt) oder JULOS_BACKUP_SFTP_PASSWORD_FILE (Datei mit dem Passwort).");
                if (key is not null && pwd is not null) throw new InvalidOperationException("JULOS_BACKUP_SFTP_KEY_FILE und JULOS_BACKUP_SFTP_PASSWORD_FILE schließen sich aus: nur eins von beiden setzen.");
                var pin = V("JULOS_BACKUP_SFTP_HOST_KEY");
                if (pin is not null && !pin.Contains("SHA256:", StringComparison.Ordinal)) throw new InvalidOperationException("JULOS_BACKUP_SFTP_HOST_KEY muss ein Fingerabdruck der Form SHA256:... sein (wie ssh-keygen -l ihn zeigt).");
                if (pin is null && dataDir is null) throw new InvalidOperationException("Für den SFTP-Host-Schlüssel braucht es JULOS_DATA (dort wird er gemerkt) oder JULOS_BACKUP_SFTP_HOST_KEY.");
                return new BackupCopyOptions(BackupCopyMode.Sftp, Host: host, Port: port, User: user, RemotePath: path, KeyFile: key, KeyPassphraseFile: V("JULOS_BACKUP_SFTP_KEY_PASSPHRASE_FILE"),
                    PasswordFile: pwd, HostKeyPin: pin, HostKeyFile: dataDir is null ? null : System.IO.Path.Combine(dataDir, HostKeyFileName));
            }
            default: throw new InvalidOperationException("JULOS_BACKUP_COPY muss off, path oder sftp sein.");
        }
    }
}

/// <summary>Eine Datei am Ziel.</summary>
public sealed record BackupTargetFile(string Name, long Bytes);

/// <summary>
/// Das zweite Ziel, eine Verbindung pro Abgleich (danach <c>DisposeAsync</c>). Der Ordner wird nie angelegt. Alle Fehler sind Ausnahmen mit lesbarer Meldung ohne Zugangsdaten;
/// der Aufrufer macht daraus eine Warnung, das lokale Backup bleibt unberuehrt.
/// </summary>
public interface IBackupTarget : IAsyncDisposable
{
    /// <summary>Kurzname fuer Log und Anzeige, ohne Zugangsdaten.</summary>
    string Description { get; }

    /// <summary>Verbindet und prueft, dass der Zielordner da ist.</summary>
    Task OpenAsync(CancellationToken ct);

    /// <summary>Die Dateien direkt im Zielordner.</summary>
    Task<IReadOnlyList<BackupTargetFile>> ListAsync(CancellationToken ct);

    /// <summary>Kleine Datei lesen; null, wenn es sie nicht gibt.</summary>
    Task<byte[]?> ReadAsync(string name, CancellationToken ct);

    /// <summary>Atomar: unter einem Zwischennamen (<c>name.tmp</c>) schreiben, dann umbenennen. Eine vorhandene Datei wird ersetzt.</summary>
    Task PutAsync(string name, Stream content, CancellationToken ct);

    Task DeleteAsync(string name, CancellationToken ct);
}

/// <summary>Zielordner im Dateisystem des Servers (NAS-Einbindung, zweite Platte): Dateien 0600, atomar ueber <c>.tmp</c>.</summary>
public sealed class PathBackupTarget(string dir) : IBackupTarget
{
    public string Description => dir;

    public Task OpenAsync(CancellationToken ct)
    {
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"Zielordner {dir} fehlt (Einbindung nicht da?). Er wird nie vom Server angelegt.");
        var probe = Path.Combine(dir, ".julos-write-test");
        try { File.WriteAllText(probe, ""); File.Delete(probe); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { throw new InvalidOperationException($"In den Zielordner {dir} darf der Server nicht schreiben (Besitzer und Rechte prüfen).", e); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BackupTargetFile>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BackupTargetFile>>(Directory.EnumerateFiles(dir).Select(p => new FileInfo(p)).Select(f => new BackupTargetFile(f.Name, f.Length)).ToList());

    public Task<byte[]?> ReadAsync(string name, CancellationToken ct) => Task.FromResult(File.Exists(Path.Combine(dir, name)) ? (byte[]?)File.ReadAllBytes(Path.Combine(dir, name)) : null);

    public async Task PutAsync(string name, Stream content, CancellationToken ct)
    {
        var final = Path.Combine(dir, name);
        var tmp = final + ".tmp";
        try
        {
            await using (var fs = PrivateFile.Create(tmp))
            {
                await content.CopyToAsync(fs, ct);
                await fs.FlushAsync(ct);
            }
            File.Move(tmp, final, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public Task DeleteAsync(string name, CancellationToken ct) { File.Delete(Path.Combine(dir, name)); return Task.CompletedTask; }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
