namespace JulOS.Integrations.Ssh;

/// <summary>Der Host-Schluessel des Ziels: Algorithmus und SHA-256-Fingerabdruck im Format von ssh-keygen ("SHA256:...").</summary>
public sealed record SshHostKey(string Algorithm, string Fingerprint)
{
    /// <summary>So steht der Schluessel in der Datenbank und in Vergleichen.</summary>
    public string Id => $"{Algorithm} {Fingerprint}";
}

public sealed record SshConnectOptions(string Host, int Port, string User, string Password, int Cols, int Rows)
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public enum SshFailure { Timeout, Unreachable, LogonFailed, HostKeyRejected, Failed }

/// <summary>Verbindungsfehler mit einer Art, die der Relay in einen freundlichen Text und einen Code uebersetzt. Die Meldung enthaelt nie das Passwort.</summary>
public sealed class SshException(SshFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public SshFailure Failure { get; } = failure;
}

/// <summary>
/// Eine SSH-Sitzung mit Shell und Pseudo-Terminal. Eine eigene Schnittstelle, damit die Bibliothek dahinter austauschbar bleibt
/// (siehe docs/ARCHITECTURE.md) und der Relay ohne Netz getestet werden kann.
/// </summary>
public interface ISshSession : IAsyncDisposable
{
    /// <summary>
    /// Verbindet, prueft den Host-Schluessel (<paramref name="trustHostKey"/> wird mit dem gezeigten Schluessel gefragt, false bricht ab),
    /// meldet sich mit dem Passwort an und oeffnet die Shell mit der Groesse in <paramref name="options"/>.
    /// </summary>
    Task ConnectAsync(SshConnectOptions options, Func<SshHostKey, bool> trustHostKey, CancellationToken ct);

    /// <summary>Der Schluessel, den das Ziel gezeigt hat (nach dem Verbindungsaufbau, auch wenn er abgelehnt wurde).</summary>
    SshHostKey? PresentedHostKey { get; }

    /// <summary>Liest Ausgabe der Shell. 0 = die Shell ist beendet oder die Verbindung weg.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Fenstergroesse geaendert (SSH "window-change").</summary>
    void Resize(int cols, int rows);

    /// <summary>
    /// Fuehrt einen Befehl in einem eigenen kurzen Kanal ohne Pseudo-Terminal aus (neben der Shell) und liefert seine Standardausgabe;
    /// null bei Fehler oder wenn die Frist <paramref name="timeout"/> ablaeuft. Dient der Pruefung vor dem Start eines Programms (siehe <see cref="SshLaunchRules"/>).
    /// </summary>
    Task<string?> ExecAsync(string command, TimeSpan timeout, CancellationToken ct) => Task.FromResult<string?>(null);
}
