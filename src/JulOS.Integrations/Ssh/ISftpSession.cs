namespace JulOS.Integrations.Ssh;

/// <summary>
/// Verbindung zu einem SFTP-Ziel fuer das zweite Backup-Ziel. Genau eine Anmeldeart: privater Schluessel aus einer Datei oder Passwort
/// (beides kommt aus Dateien, nie aus Umgebungsvariablen oder Logs; hier steht nur der gelesene Inhalt).
/// </summary>
public sealed record SftpConnectOptions(string Host, int Port, string User, string? Password, string? PrivateKeyFile, string? KeyPassphrase)
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>Eine Datei im Zielordner.</summary>
public sealed record SftpFile(string Name, long Bytes);

/// <summary>
/// SFTP-Sitzung. Eine eigene Schnittstelle wie <see cref="ISshSession"/>: die Bibliothek dahinter bleibt austauschbar, und die Logik darueber laesst sich ohne Netz testen.
/// Fehler sind <see cref="SshException"/> (Meldung nie mit Passwort).
/// </summary>
public interface ISftpSession : IAsyncDisposable
{
    /// <summary>Verbindet, prueft den Host-Schluessel (false von <paramref name="trustHostKey"/> bricht ab) und meldet sich an.</summary>
    Task ConnectAsync(SftpConnectOptions options, Func<SshHostKey, bool> trustHostKey, CancellationToken ct);

    /// <summary>Der Schluessel, den das Ziel gezeigt hat (auch wenn er abgelehnt wurde).</summary>
    SshHostKey? PresentedHostKey { get; }

    Task<bool> DirectoryExistsAsync(string path, CancellationToken ct);

    /// <summary>Die Dateien (keine Ordner) direkt in <paramref name="directory"/>.</summary>
    Task<IReadOnlyList<SftpFile>> ListAsync(string directory, CancellationToken ct);

    /// <summary>Kleine Datei lesen; null, wenn es sie nicht gibt.</summary>
    Task<byte[]?> ReadAllAsync(string path, CancellationToken ct);

    /// <summary>Schreibt erst nach <paramref name="tempPath"/> und benennt dann in <paramref name="finalPath"/> um (eine vorhandene Datei wird ersetzt).</summary>
    Task UploadAsync(Stream data, string tempPath, string finalPath, CancellationToken ct);

    Task DeleteAsync(string path, CancellationToken ct);
}
