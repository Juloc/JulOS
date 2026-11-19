namespace JulOS.Integrations.Display.Rdp;

public sealed class RdpOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 3389;

    // "user", "DOMAIN\user" or "user@domain"; a separate Domain wins over one written into the name.
    public string UserName { get; init; } = "";

    public string Domain { get; init; } = "";

    public string Password { get; init; } = "";

    // The size asked for. The server decides; RdpClient.Width and Height say what it became.
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 800;

    // Windows' keyboard layout id, e.g. 0x407 for German.
    public uint KeyboardLayout { get; init; } = 0x409;

    // RDP servers nearly always present a certificate they made themselves. When false, the certificate has to
    // be one this machine trusts and has to carry the host's name.
    public bool IgnoreCertificate { get; init; } = true;

    // Trust on first use: called in the TLS handshake with the certificate's fingerprint ("SHA256:AB:CD:..."), before any credentials are sent.
    // Returning false ends the handshake (the exception carries the fingerprint, see RdpException.CertificateFingerprint). When set, this
    // replaces IgnoreCertificate: whether the certificate is signed by anybody does not matter, only whether it is the one that was seen before.
    public Func<string, bool>? CertificateCheck { get; init; }

    public string ClientName { get; init; } = "JulOS";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // Whether the remote desktop should send its sound at all (the sound channel has to be added as well).
    public bool Sound { get; init; }
}

// What the session tells whoever shows it: the callbacks of IScreenHandler (the same ones the VNC client uses).
public interface IRdpHandler : IScreenHandler
{
}
