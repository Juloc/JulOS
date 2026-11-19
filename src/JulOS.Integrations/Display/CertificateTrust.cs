using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JulOS.Integrations.Display;

public enum CertificateDecision
{
    /// <summary>Noch kein Zertifikat gemerkt: dieses wird gemerkt (Vertrauen bei erster Nutzung).</summary>
    FirstUse,
    /// <summary>Dasselbe wie beim letzten Mal.</summary>
    Known,
    /// <summary>Anderes Zertifikat, aber genau dieses wurde ausdruecklich bestaetigt.</summary>
    AcceptedChange,
    /// <summary>Anderes Zertifikat: ablehnen.</summary>
    Changed,
}

public static class CertificatePolicy
{
    /// <summary>Vertrauen auf erste Nutzung: ein geaendertes Zertifikat wird nie stillschweigend akzeptiert.</summary>
    public static CertificateDecision Decide(string? stored, string? accepted, string presented)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return CertificateDecision.FirstUse;
        }

        if (string.Equals(stored, presented, StringComparison.Ordinal))
        {
            return CertificateDecision.Known;
        }

        return string.Equals(accepted, presented, StringComparison.Ordinal) ? CertificateDecision.AcceptedChange : CertificateDecision.Changed;
    }
}

public static class CertificateFingerprint
{
    /// <summary>"SHA256:" und der SHA-256-Wert des Zertifikats als Hex mit Doppelpunkten, wie <c>openssl x509 -noout -fingerprint -sha256</c> ihn schreibt.</summary>
    public static string Of(X509Certificate certificate) => Of(certificate.GetCertHash(HashAlgorithmName.SHA256));

    public static string Of(byte[] sha256) => "SHA256:" + string.Join(':', sha256.Select(b => b.ToString("X2")));
}

/// <summary>
/// Was fuer eine RDP-Verbindung ueber das Zertifikat des Ziels bekannt ist: der gemerkte Fingerabdruck (null: erste Verbindung) und der, den der
/// Benutzer nach einer Warnung ausdruecklich bestaetigt hat. <see cref="Check"/> laeuft im TLS-Handshake, bevor Zugangsdaten gesendet werden;
/// <see cref="CommitAsync"/> schreibt einen neuen oder bestaetigten Fingerabdruck, wenn die Anmeldung geklappt hat (die Bibliothek kennt die Datenbank nicht).
/// </summary>
public sealed class CertificateTrust(string? stored, string? accepted, Func<string, CancellationToken, Task> save)
{
    /// <summary>Der gemerkte Fingerabdruck dieser Verbindung (null: noch keiner).</summary>
    public string? Stored { get; } = stored;

    /// <summary>Der Fingerabdruck, den das Ziel zuletzt gezeigt hat.</summary>
    public string? Presented { get; private set; }

    public CertificateDecision? Decision { get; private set; }

    /// <summary>Darf die Verbindung mit diesem Zertifikat weitergehen?</summary>
    public bool Check(string fingerprint)
    {
        Presented = fingerprint;
        Decision = CertificatePolicy.Decide(Stored, accepted, fingerprint);
        return Decision != CertificateDecision.Changed;
    }

    /// <summary>Merkt das gezeigte Zertifikat, wenn es neu oder bestaetigt war. True, wenn etwas geschrieben wurde.</summary>
    public async Task<bool> CommitAsync(CancellationToken ct)
    {
        if (Presented is null || Decision is not (CertificateDecision.FirstUse or CertificateDecision.AcceptedChange))
        {
            return false;
        }

        await save(Presented, ct).ConfigureAwait(false);
        return true;
    }
}
