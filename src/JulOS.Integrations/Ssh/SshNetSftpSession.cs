using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace JulOS.Integrations.Ssh;

/// <summary><see cref="ISftpSession"/> mit SSH.NET: privater Schluessel (auch mit Passphrase) oder Passwort, Host-Schluessel ueber den Rueckruf.</summary>
public sealed class SshNetSftpSession : ISftpSession
{
    private SftpClient? _client;
    private bool _hostKeyRejected;

    public SshHostKey? PresentedHostKey { get; private set; }

    private SftpClient Client => _client ?? throw new InvalidOperationException("Nicht verbunden.");

    public async Task ConnectAsync(SftpConnectOptions o, Func<SshHostKey, bool> trustHostKey, CancellationToken ct)
    {
        var methods = new List<AuthenticationMethod>();
        if (o.PrivateKeyFile is not null)
        {
            try { methods.Add(new PrivateKeyAuthenticationMethod(o.User, new PrivateKeyFile(o.PrivateKeyFile, o.KeyPassphrase))); }
            catch (Exception e) when (e is SshException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new SshException(SshFailure.Failed, "Der private SFTP-Schlüssel lässt sich nicht lesen (Datei, Format oder Passphrase falsch).", e);
            }
        }
        if (o.Password is not null)
        {
            var password = o.Password;
            methods.Add(new PasswordAuthenticationMethod(o.User, password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(o.User);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = p.Request.Contains("assword", StringComparison.OrdinalIgnoreCase) ? password : "";
            };
            methods.Add(interactive);
        }
        if (methods.Count == 0) throw new SshException(SshFailure.Failed, "Keine Anmeldeart für SFTP angegeben.");
        var client = new SftpClient(new ConnectionInfo(o.Host, o.Port, o.User, methods.ToArray()) { Timeout = o.ConnectTimeout });
        client.OperationTimeout = TimeSpan.FromMinutes(5);
        client.HostKeyReceived += (_, e) =>
        {
            var key = new SshHostKey(e.HostKeyName, SshNetSession.Fingerprint(e.HostKey));
            PresentedHostKey = key;
            e.CanTrust = trustHostKey(key);
            _hostKeyRejected = !e.CanTrust;
        };
        _client = client;
        try { await client.ConnectAsync(ct).ConfigureAwait(false); }
        catch (Exception e) { throw Translate(e, ct); }
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct) => Run(() => Client.Exists(path) && Client.GetAttributes(path).IsDirectory, ct);

    public Task<IReadOnlyList<SftpFile>> ListAsync(string directory, CancellationToken ct) => Run<IReadOnlyList<SftpFile>>(() =>
        Client.ListDirectory(directory).Where(f => f.IsRegularFile).Select(f => new SftpFile(f.Name, f.Length)).ToList(), ct);

    public Task<byte[]?> ReadAllAsync(string path, CancellationToken ct) => Run<byte[]?>(() => Client.Exists(path) ? Client.ReadAllBytes(path) : null, ct);

    public Task UploadAsync(Stream data, string tempPath, string finalPath, CancellationToken ct) => Run(() =>
    {
        try
        {
            Client.UploadFile(data, tempPath, canOverride: true);
            try { Client.RenameFile(tempPath, finalPath, isPosix: true); }                 // ersetzt atomar (OpenSSH); sonst unten
            catch (SshException) when (Client.Exists(finalPath))
            {
                Client.DeleteFile(finalPath);
                Client.RenameFile(tempPath, finalPath);
            }
        }
        catch
        {
            try { if (Client.Exists(tempPath)) Client.DeleteFile(tempPath); } catch (Exception) { /* beste Muehe */ }
            throw;
        }
        return true;
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => Run(() => { if (Client.Exists(path)) Client.DeleteFile(path); return true; }, ct);

    /// <summary>SSH.NET ist blockierend; die Arbeit geht auf einen Pool-Thread, Fehler werden uebersetzt.</summary>
    private Task<T> Run<T>(Func<T> work, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try { return work(); }
        catch (Exception e) when (e is not OperationCanceledException) { throw Translate(e, ct); }
    }, ct);

    private SshException Translate(Exception e, CancellationToken ct)
    {
        if (e is SshException ours) return ours;
        if (e is OperationCanceledException && ct.IsCancellationRequested) throw e;
        if (_hostKeyRejected) return new SshException(SshFailure.HostKeyRejected, "Host-Schlüssel nicht vertraut.", e);
        return e switch
        {
            SshAuthenticationException => new SshException(SshFailure.LogonFailed, "Anmeldung abgelehnt.", e),
            SshOperationTimeoutException or TimeoutException => new SshException(SshFailure.Timeout, "Zeitüberschreitung beim Verbinden.", e),
            SocketException => new SshException(SshFailure.Unreachable, "Das Ziel ist nicht erreichbar.", e),
            SftpPermissionDeniedException => new SshException(SshFailure.Failed, "Zugriff am SFTP-Ziel verweigert (Rechte des Ordners?).", e),
            SftpPathNotFoundException => new SshException(SshFailure.Failed, "Pfad am SFTP-Ziel nicht gefunden.", e),
            _ when e.InnerException is SocketException => new SshException(SshFailure.Unreachable, "Das Ziel ist nicht erreichbar.", e),
            _ => new SshException(SshFailure.Failed, "Die SFTP-Verbindung ist fehlgeschlagen: " + e.Message, e),
        };
    }

    public ValueTask DisposeAsync()
    {
        try { _client?.Dispose(); } catch (Exception) { /* schon weg */ }
        return ValueTask.CompletedTask;
    }
}
