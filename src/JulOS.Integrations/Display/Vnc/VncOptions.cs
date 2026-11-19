namespace JulOS.Integrations.Display.Vnc;

public sealed class VncOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 5900;

    // The classic VNC password: only its first eight characters count (that is what the protocol does, not JulOS).
    public string Password { get; init; } = "";

    // Only for the VeNCrypt "Plain" sign-in (user name and password over TLS); the classic password needs no user.
    public string UserName { get; init; } = "";

    // Trust on first use for the certificate of a TLS connection (VeNCrypt with X.509): called in the TLS handshake with
    // "SHA256:AB:CD:...", before anything secret is sent. Returning false ends the handshake (the exception carries the fingerprint).
    public Func<string, bool>? CertificateCheck { get; init; }

    // Without CertificateCheck: whether any certificate is taken (self-signed ones are the rule for VNC servers).
    public bool IgnoreCertificate { get; init; } = true;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // The encodings to ask for, most wanted first (null: the usual list). Mainly for tests that want to see one encoding at a time.
    public int[]? Encodings { get; init; }

    // Leave the desktop to the viewer alone (false: other viewers may stay connected, which is what most servers do by default).
    public bool Exclusive { get; init; }
}
