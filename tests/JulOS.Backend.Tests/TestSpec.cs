using JulOS.Integrations.Guacamole;

namespace JulOS.Backend.Tests;

/// <summary>Eine Verbindung mit dem Protokoll "ssh" fuer die Relay-Tests gegen den Fake-guacd (der Relay kennt keine Protokolle; Terminals gehen heute nicht mehr ueber guacd).</summary>
internal static class TestSpec
{
    public static GuacSpec Ssh(string host, int port, string user, string password, int width, int height, int dpi) => new("ssh",
        new Dictionary<string, string> { ["hostname"] = host, ["port"] = port.ToString(), ["username"] = user, ["password"] = password }, width, height, dpi);
}
