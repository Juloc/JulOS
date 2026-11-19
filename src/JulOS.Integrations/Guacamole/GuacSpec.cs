namespace JulOS.Integrations.Guacamole;

/// <summary>Was guacd verbinden soll: Protokoll, dessen Einstellungen und die Bildgroesse des Browsers. Heute nur noch Desktops (RDP); Terminals laufen ueber den eigenen SSH-Client (Ssh/).</summary>
public sealed record GuacSpec(string Protocol, IReadOnlyDictionary<string, string> Args, int Width, int Height, int Dpi)
{
    /// <summary>Kennung "Benutzer + Verbindung": pro Kennung ist nur eine Browser-Verbindung offen, eine neue loest die alte ab.</summary>
    public string? Slot { get; init; }

    /// <summary>Desktop ueber RDP. Die Werte stammen aus dem bewaehrten Node-Gateway.</summary>
    public static GuacSpec Desktop(string host, int port, string user, string password, int width, int height, int dpi) => new("rdp",
        new Dictionary<string, string>
        {
            ["hostname"] = host, ["port"] = port.ToString(), ["username"] = user, ["password"] = password,
            ["security"] = "any", ["ignore-cert"] = "true", ["resize-method"] = "display-update",
            ["color-depth"] = "24", ["server-layout"] = "de-de-qwertz", ["timezone"] = "Europe/Berlin",
            ["enable-font-smoothing"] = "true", ["enable-wallpaper"] = "false", ["enable-theming"] = "true",
            ["enable-full-window-drag"] = "false", ["enable-desktop-composition"] = "false", ["enable-menu-animations"] = "false",
            ["disable-audio"] = "true", ["clipboard-encoding"] = "UTF-8", ["client-name"] = "julos", ["disable-gfx"] = Environment.GetEnvironmentVariable("JULOS_RDP_GFX") == "on" ? "false" : "true",   // xrdp 0.10 zeigt mit der Grafik-Pipeline (GFX) lange nur Schwarz; ohne sofort Bild
        }, width, height, dpi);
}
