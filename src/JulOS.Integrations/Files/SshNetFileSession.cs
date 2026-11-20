using System.Net.Sockets;
using JulOS.Integrations.Ssh;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Common = Renci.SshNet.Common;

namespace JulOS.Integrations.Files;

/// <summary>
/// <see cref="ISftpFileSession"/> mit SSH.NET. Die Anmeldung und die Pruefung des Host-Schluessels macht <see cref="SshNetSftpSession"/> (dieselben wie beim zweiten Backup-Ziel),
/// hier kommen die Operationen auf Dateien dazu. Alles laeuft ueber die async-Aufrufe der Bibliothek oder einen Pool-Thread; Fehler werden in <see cref="RemoteFileException"/> uebersetzt.
/// </summary>
public sealed class SshNetFileSession : ISftpFileSession
{
    /// <summary>Wie viele Links je Ordnerliste auf ihr Ziel geprueft werden (ein Aufruf pro Link): mehr bleiben "unbekannt".</summary>
    internal const int MaxResolvedLinks = 200;

    private readonly SshNetSftpSession _connection = new();

    public SshHostKey? PresentedHostKey => _connection.PresentedHostKey;

    private SftpClient Client => _connection.RawClient ?? throw new InvalidOperationException("Nicht verbunden.");

    public bool IsConnected
    {
        get { try { return _connection.RawClient is { IsConnected: true }; } catch (ObjectDisposedException) { return false; } }
    }

    /// <summary>
    /// Handles aus Ordnerlisten (Pfad zum Eintrag). Noetig, weil die Bibliothek bei Aufrufen mit einem Pfad einem Link FOLGT (sie macht vorher ein "realpath"): Loeschen oder Umbenennen
    /// ueber den Pfad eines Links trifft dessen Ziel, "Get" liefert die Werte des Ziels. Ein Handle aus einer Ordnerliste traegt den Pfad des Eintrags selbst und die Werte von "lstat".
    /// </summary>
    private readonly Dictionary<string, (Renci.SshNet.Sftp.ISftpFile File, long At)> _handles = [];
    private const int MaxHandles = 20_000, MaxAgeMs = 5000;

    public string HomeDirectory { get; private set; } = "/";

    public async Task ConnectAsync(SftpConnectOptions options, Func<SshHostKey, bool> trustHostKey, CancellationToken ct)
    {
        await _connection.ConnectAsync(options, trustHostKey, ct).ConfigureAwait(false);
        HomeDirectory = RemotePath.Normalize(Client.WorkingDirectory, "/") ?? "/";
    }

    private static RemoteKind KindOf(SftpFileAttributes a) => a.IsSymbolicLink ? RemoteKind.Link : a.IsDirectory ? RemoteKind.Directory : a.IsRegularFile ? RemoteKind.File : RemoteKind.Other;

    private static string ModeOf(SftpFileAttributes a)
    {
        static char C(bool on, char c) => on ? c : '-';
        return string.Concat(C(a.OwnerCanRead, 'r'), C(a.OwnerCanWrite, 'w'), C(a.OwnerCanExecute, 'x'), C(a.GroupCanRead, 'r'), C(a.GroupCanWrite, 'w'), C(a.GroupCanExecute, 'x'),
            C(a.OthersCanRead, 'r'), C(a.OthersCanWrite, 'w'), C(a.OthersCanExecute, 'x'));
    }

    private static RemoteEntry Describe(string name, string path, SftpFileAttributes a, RemoteKind? resolved = null) =>
        new(name, path, KindOf(a), a.Size, new DateTimeOffset(DateTime.SpecifyKind(a.LastWriteTimeUtc, DateTimeKind.Utc)), ModeOf(a), resolved);

    private async Task<RemoteKind?> ResolveAsync(string path, CancellationToken ct)
    {
        try { return KindOf(await Client.GetAttributesAsync(path, ct).ConfigureAwait(false)); }      // stat folgt dem Link
        catch (Exception e) when (e is Common.SftpPathNotFoundException or Common.SftpPermissionDeniedException) { return null; }    // kaputter oder gesperrter Link
    }

    public async Task<RemoteListing> ListAsync(string directory, int limit, CancellationToken ct)
    {
        var entries = new List<RemoteEntry>();
        var hidden = 0;
        var truncated = false;
        var links = 0;
        lock (_handles) { if (_handles.Count > MaxHandles) _handles.Clear(); }
        try
        {
            await foreach (var f in Client.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
            {
                if (f.Name is "." or "..") continue;
                if (!RemotePath.SafeSegment(f.Name)) { hidden++; continue; }                     // Steuerzeichen, "/" oder ".." im Namen: ein Name des Ziels ist nie ein Pfad
                if (entries.Count >= limit) { truncated = true; break; }
                var path = RemotePath.Join(directory, f.Name);
                lock (_handles) _handles[path] = (f, Environment.TickCount64);
                RemoteKind? resolved = null;
                if (f.IsSymbolicLink && links++ < MaxResolvedLinks) resolved = await ResolveAsync(path, ct).ConfigureAwait(false);
                entries.Add(Describe(f.Name, path, f.Attributes, resolved));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { throw Translate(e, directory); }
        return new RemoteListing(entries, truncated, hidden);
    }

    public async Task<RemoteEntry?> StatAsync(string path, bool follow, CancellationToken ct)
    {
        try
        {
            if (follow) return Describe(RemotePath.NameOf(path), path, await Client.GetAttributesAsync(path, ct).ConfigureAwait(false));
            if (path == "/") return Describe("/", path, await Client.GetAttributesAsync(path, ct).ConfigureAwait(false));
            if (await HandleAsync(path, ct).ConfigureAwait(false) is not { } f) return null;
            return Describe(f.Name, path, f.Attributes, f.IsSymbolicLink ? await ResolveAsync(path, ct).ConfigureAwait(false) : null);
        }
        catch (Common.SftpPathNotFoundException) { return null; }
        catch (Exception e) when (e is not OperationCanceledException) { throw Translate(e, path); }
    }

    /// <summary>Das Handle des Eintrags selbst ("lstat"): aus der letzten Liste des Ordners, sonst ueber eine Liste des Ordners. null, wenn es ihn nicht gibt.</summary>
    private async Task<Renci.SshNet.Sftp.ISftpFile?> HandleAsync(string path, CancellationToken ct)
    {
        lock (_handles) { if (_handles.TryGetValue(path, out var known) && Environment.TickCount64 - known.At < MaxAgeMs) return known.File; }
        var parent = RemotePath.Parent(path); var name = RemotePath.NameOf(path);
        await foreach (var f in Client.ListDirectoryAsync(parent, ct).ConfigureAwait(false))
        {
            if (f.Name != name) continue;
            lock (_handles) _handles[path] = (f, Environment.TickCount64);
            return f;
        }
        return null;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        try { return await Client.OpenAsync(path, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { throw Translate(e, path); }
    }

    public async Task<Stream> CreateAsync(string path, CancellationToken ct)
    {
        try { return await Client.OpenAsync(path, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false); }       // CreateNew: das Ziel prueft selbst, dass es den Pfad noch nicht gibt
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (e is not Common.SftpPermissionDeniedException && await StatAsync(path, follow: false, ct).ConfigureAwait(false) is not null) throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.", e);
            throw Translate(e, path);
        }
    }

    public async Task RenameAsync(string from, string to, bool overwrite, bool fromIsLink, CancellationToken ct)
    {
        try
        {
            if (fromIsLink)
            {
                // Den Link selbst umbenennen (nie sein Ziel); ein vorhandenes Ziel ersetzt der Aufrufer nie
                if (await StatAsync(to, follow: false, ct).ConfigureAwait(false) is not null) throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.");
                var handle = await HandleAsync(from, ct).ConfigureAwait(false) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
                await Task.Run(() => handle.MoveTo(to), ct).ConfigureAwait(false);
                lock (_handles) _handles.Remove(from);
                return;
            }
            await Task.Run(() =>
            {
                try { Client.RenameFile(from, to, isPosix: overwrite); }                        // posix-rename ersetzt atomar (OpenSSH); ohne overwrite das gewoehnliche Umbenennen, das ein vorhandenes Ziel ablehnt
                catch (Exception) when (overwrite)                                              // Ziel ohne ersetzendes Umbenennen: erst die alte Datei weg, dann umbenennen
                {
                    Client.DeleteFile(to);
                    Client.RenameFile(from, to);
                }
            }, ct).ConfigureAwait(false);
            lock (_handles) _handles.Remove(from);
        }
        catch (Exception e) when (e is not OperationCanceledException and not RemoteFileException)
        {
            if (!overwrite && e is not Common.SftpPermissionDeniedException && e is not Common.SftpPathNotFoundException && await StatAsync(to, follow: false, ct).ConfigureAwait(false) is not null)
                throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.", e);
            throw Translate(e, from);
        }
    }

    public async Task DeleteFileAsync(string path, bool isLink, CancellationToken ct)
    {
        try
        {
            if (isLink)
            {
                var handle = await HandleAsync(path, ct).ConfigureAwait(false) ?? throw new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.");
                await Task.Run(handle.Delete, ct).ConfigureAwait(false);                         // entfernt den Link selbst (SFTP "remove" folgt keinem Link)
            }
            else await Task.Run(() => Client.DeleteFile(path), ct).ConfigureAwait(false);
            lock (_handles) _handles.Remove(path);
        }
        catch (Exception e) when (e is not OperationCanceledException and not RemoteFileException) { throw Translate(e, path); }
    }

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        try { await Client.DeleteDirectoryAsync(path, ct).ConfigureAwait(false); lock (_handles) _handles.Remove(path); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Ein volles Verzeichnis meldet SFTP nur als allgemeinen Fehler: nachsehen, ob etwas drin ist
            if (e is not Common.SftpPathNotFoundException && e is not Common.SftpPermissionDeniedException && (await ListAsync(path, 1, ct).ConfigureAwait(false)).Entries.Count > 0)
                throw new RemoteFileException(RemoteFileError.NotEmpty, "Der Ordner ist nicht leer.", e);
            throw Translate(e, path);
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        try { await Client.CreateDirectoryAsync(path, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (e is not Common.SftpPermissionDeniedException && await StatAsync(path, follow: false, ct).ConfigureAwait(false) is not null) throw new RemoteFileException(RemoteFileError.Exists, "Es gibt diesen Pfad schon.", e);
            throw Translate(e, path);
        }
    }

    internal static Exception Translate(Exception e, string path) => e switch
    {
        RemoteFileException or SshException => e,
        Common.SftpPathNotFoundException => new RemoteFileException(RemoteFileError.NotFound, "Pfad nicht gefunden.", e),
        Common.SftpPermissionDeniedException => new RemoteFileException(RemoteFileError.Denied, "Zugriff verweigert.", e),
        Common.SshConnectionException or Common.SshOperationTimeoutException or SocketException or ObjectDisposedException or InvalidOperationException { Message: "Nicht verbunden." } =>
            new RemoteFileException(RemoteFileError.Closed, "Die Verbindung zum Ziel ist abgerissen.", e),
        IOException { InnerException: SocketException or Common.SshConnectionException } => new RemoteFileException(RemoteFileError.Closed, "Die Verbindung zum Ziel ist abgerissen.", e),
        _ => new RemoteFileException(RemoteFileError.Failed, "Die Operation am Ziel ist fehlgeschlagen.", e),    // der Text der Bibliothek bleibt in inner, er geht nie zum Browser
    };

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
