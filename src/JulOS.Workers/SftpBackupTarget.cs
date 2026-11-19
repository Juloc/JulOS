using System.Text;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Integrations.Ssh;
using Microsoft.Extensions.Logging;

namespace JulOS.Workers;

/// <summary>
/// Zweites Backup-Ziel auf einem SFTP-Server. Anmeldung mit privatem Schluessel oder Passwort, beides aus einer Datei (nie aus einer Umgebungsvariable, nie im Log).
/// Der Host-Schluessel wird beim ersten Mal gemerkt (Datei <c>backup-sftp-hostkey</c> im Datenordner, Algorithmus und SHA-256-Fingerabdruck wie bei <c>ssh-keygen -l</c>)
/// und danach geprueft; <c>JULOS_BACKUP_SFTP_HOST_KEY</c> legt ihn stattdessen fest vor. Eine Aenderung bricht ab und warnt: erst pruefen, ob das Ziel neu aufgesetzt wurde, dann die Datei loeschen.
/// </summary>
public sealed class SftpBackupTarget(BackupCopyOptions o, ILogger log, Func<ISftpSession>? sessionFactory = null) : IBackupTarget
{
    private readonly ISftpSession _session = (sessionFactory ?? (() => new SshNetSftpSession()))();

    public string Description => o.Describe();

    private string Remote(string name) => o.RemotePath!.TrimEnd('/') + "/" + name;

    public async Task OpenAsync(CancellationToken ct)
    {
        var (password, passphrase) = (ReadSecret(o.PasswordFile, "Passwort"), ReadSecret(o.KeyPassphraseFile, "Passphrase"));
        if (o.KeyFile is not null && !File.Exists(o.KeyFile)) throw new InvalidOperationException($"Die Schlüsseldatei {o.KeyFile} gibt es nicht (nur lesend einbinden, siehe docs/BACKUP.md).");
        string? hostKeyProblem = null;
        try
        {
            await _session.ConnectAsync(new SftpConnectOptions(o.Host!, o.Port, o.User!, password, o.KeyFile, passphrase), key => { hostKeyProblem = CheckHostKey(key); return hostKeyProblem is null; }, ct);
        }
        catch (SshException e)
        {
            throw new InvalidOperationException(e.Failure == SshFailure.HostKeyRejected && hostKeyProblem is not null ? hostKeyProblem : e.Message, e);
        }
        if (!await Wrap(() => _session.DirectoryExistsAsync(o.RemotePath!, ct))) throw new InvalidOperationException($"Der Ordner {o.RemotePath} gibt es am SFTP-Ziel nicht. Er wird nie vom Server angelegt.");
    }

    public async Task<IReadOnlyList<BackupTargetFile>> ListAsync(CancellationToken ct) =>
        (await Wrap(() => _session.ListAsync(o.RemotePath!, ct))).Select(f => new BackupTargetFile(f.Name, f.Bytes)).ToList();

    public Task<byte[]?> ReadAsync(string name, CancellationToken ct) => Wrap(() => _session.ReadAllAsync(Remote(name), ct));

    public Task PutAsync(string name, Stream content, CancellationToken ct) => Wrap(() => _session.UploadAsync(content, Remote(name) + ".tmp", Remote(name), ct));

    public Task DeleteAsync(string name, CancellationToken ct) => Wrap(() => _session.DeleteAsync(Remote(name), ct));

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private static async Task<T> Wrap<T>(Func<Task<T>> work)
    {
        try { return await work(); }
        catch (SshException e) { throw new InvalidOperationException(e.Message, e); }
    }

    private static async Task Wrap(Func<Task> work)
    {
        try { await work(); }
        catch (SshException e) { throw new InvalidOperationException(e.Message, e); }
    }

    /// <summary>Null = vertraut. Sonst der Grund (mit beiden Fingerabdruecken).</summary>
    private string? CheckHostKey(SshHostKey key)
    {
        if (o.HostKeyPin is { } pin)
        {
            var pinned = pin.Contains(' ') ? pin : null;                               // "ssh-ed25519 SHA256:..." oder nur "SHA256:..."
            if (pinned is not null ? pinned == key.Id : pin == key.Fingerprint) return null;
            return Mismatch($"festgelegt in JULOS_BACKUP_SFTP_HOST_KEY: {pin}", key, "JULOS_BACKUP_SFTP_HOST_KEY prüfen oder anpassen");
        }
        var file = o.HostKeyFile!;
        if (File.Exists(file))
        {
            var known = File.ReadAllText(file).Trim();
            if (known == key.Id) return null;
            return Mismatch($"gemerkt: {known}", key, $"die Datei {file} löschen, um den neuen zu merken");
        }
        try
        {
            using (var fs = PrivateFile.Create(file + ".tmp")) fs.Write(Encoding.UTF8.GetBytes(key.Id + "\n"));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Der SFTP-Host-Schlüssel ({key.Id}) lässt sich nicht merken: {file} ist nicht beschreibbar.";
        }
        log.LogWarning("SFTP-Host-Schlüssel von {Host} zum ersten Mal gesehen und gemerkt: {Key}. Bitte einmal mit dem Fingerabdruck des Ziels vergleichen (ssh-keygen -lf /etc/ssh/ssh_host_*_key.pub).", o.Host, key.Id);
        return null;
    }

    private string Mismatch(string known, SshHostKey now, string remedy)
    {
        var text = $"ACHTUNG: Der Host-Schlüssel von {o.Host} stimmt nicht ({known}; jetzt gezeigt: {now.Id}). Es wird nichts übertragen. Wurde das Ziel neu aufgesetzt, ist das harmlos, sonst könnte jemand das Ziel nachahmen. Nach Prüfung: {remedy}.";
        log.LogError("{Message}", text);
        return text;
    }

    private static string? ReadSecret(string? file, string what)
    {
        if (file is null) return null;
        try
        {
            var text = File.ReadAllText(file).TrimEnd('\r', '\n');
            return text.Length > 0 ? text : throw new InvalidOperationException($"Die Datei mit dem {what} ({file}) ist leer.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"Die Datei mit dem {what} ({file}) lässt sich nicht lesen.", e); }
    }
}

/// <summary>Baut das zweite Ziel aus den Einstellungen (Server und Konsole benutzen dieselbe Stelle).</summary>
public static class BackupTargets
{
    public static Func<IBackupTarget>? Factory(BackupOptions o, ILogger log) => o.Copy switch
    {
        { Mode: BackupCopyMode.Path } c => () => new PathBackupTarget(c.Path!),
        { Mode: BackupCopyMode.Sftp } c => () => new SftpBackupTarget(c, log),
        _ => null,
    };
}
