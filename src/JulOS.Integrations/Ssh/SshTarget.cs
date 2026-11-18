namespace JulOS.Integrations.Ssh;

/// <summary>
/// Was ein Terminal-Ticket enthaelt. StoredHostKey ist der bekannte Schluessel der Verbindung (null = noch keiner, erste Verbindung);
/// AcceptHostKey ist der Schluessel, den der Benutzer nach einer Warnung ausdruecklich bestaetigt hat (nur genau dieser gilt).
/// SaveHostKey schreibt einen neuen oder bestaetigten Schluessel (die Bibliothek kennt die Datenbank nicht).
/// Slot: Benutzer + Verbindung + Sitzungskennung des Browsers (siehe <see cref="SessionRegistry"/>); ein neuer WebSocket im selben Slot loest den alten ab.
/// </summary>
public sealed record SshTarget(Guid Owner, SshConnectOptions Options, string? StoredHostKey, string? AcceptHostKey, Func<string, CancellationToken, Task> SaveHostKey, string? Slot = null);

/// <summary>Einmal-Tickets fuer den Terminal-WebSocket (siehe <see cref="Tickets{T}"/>).</summary>
public sealed class SshTickets(TimeProvider time) : Tickets<SshTarget>(time);

public enum HostKeyDecision
{
    /// <summary>Gleicher Schluessel wie gespeichert.</summary>
    Known,
    /// <summary>Noch keiner gespeichert: vertrauen und merken (erste Verbindung).</summary>
    FirstUse,
    /// <summary>Anderer Schluessel, aber genau dieser wurde ausdruecklich bestaetigt.</summary>
    AcceptedChange,
    /// <summary>Anderer Schluessel: ablehnen.</summary>
    Changed,
}

public static class HostKeyPolicy
{
    /// <summary>Vertrauen auf erste Nutzung: ein geaenderter Schluessel wird nie stillschweigend akzeptiert.</summary>
    public static HostKeyDecision Decide(string? stored, string? accepted, SshHostKey presented)
    {
        if (string.IsNullOrEmpty(stored)) return HostKeyDecision.FirstUse;
        if (string.Equals(stored, presented.Id, StringComparison.Ordinal)) return HostKeyDecision.Known;
        return string.Equals(accepted, presented.Id, StringComparison.Ordinal) ? HostKeyDecision.AcceptedChange : HostKeyDecision.Changed;
    }
}
