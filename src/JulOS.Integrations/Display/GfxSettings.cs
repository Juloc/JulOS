using System.Collections.Concurrent;

namespace JulOS.Integrations.Display;

/// <summary>
/// Der Schalter fuer RDPGFX (siehe docs/RDPGFX.md) und ein Gedaechtnis fuer Server, bei denen es nichts brachte: ein Server, der kein H.264 durch die
/// Grafik-Pipeline liefert (zum Beispiel xrdp ohne x264), kostet bei jedem Versuch eine zweite Verbindung. Nach einem Fehlschlag wird er deshalb eine
/// Weile gar nicht gefragt (bis der JulOS-Server neu startet oder die Zeit um ist).
/// </summary>
internal static class GfxSettings
{
    /// <summary>So lange wird ein Server nach einem Fehlschlag nicht noch einmal gefragt.</summary>
    public static readonly TimeSpan AvoidFor = TimeSpan.FromMinutes(30);

    private static readonly ConcurrentDictionary<string, long> Avoided = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>JULOS_RDP_GFX: 1, on, true oder yes (Gross-/Kleinschreibung egal) schalten es an; alles andere, auch nichts, laesst es aus.</summary>
    public static bool IsOn(string? value) =>
        value is not null && value.Trim().ToLowerInvariant() is "1" or "on" or "true" or "yes";

    public static bool IsAvoided(string host, int port) =>
        Avoided.TryGetValue(Key(host, port), out var until) && Environment.TickCount64 < until;

    public static void Avoid(string host, int port) => Avoided[Key(host, port)] = Environment.TickCount64 + (long)AvoidFor.TotalMilliseconds;

    /// <summary>Nur fuer Tests.</summary>
    public static void Forget() => Avoided.Clear();

    private static string Key(string host, int port) => host + ":" + port;
}
