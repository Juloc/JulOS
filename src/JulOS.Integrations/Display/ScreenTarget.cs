using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Rdp.Drive;
using JulOS.Integrations.Display.Vnc;

namespace JulOS.Integrations.Display;

/// <summary>
/// Was eine Bildschirm-Sitzung verbinden soll. Slot: pro Benutzer und Verbindung ist nur ein Browser offen, ein neuer loest den alten ab.
/// Trust: das Zertifikat des Ziels (Fingerabdruck merken, Aenderung melden); ohne Angabe wird jedes Zertifikat angenommen (Tests).
/// Vnc: statt RDP ein VNC-Ziel (dann ist Options leer und Drive ohne Wirkung).
/// Drive: ein Ordner des Servers, der als Laufwerk in die Sitzung gereicht wird (Wurzel aus der Server-Einstellung, Schreiben nur mit eigenem Schalter); ohne Angabe keins.
/// </summary>
public sealed record ScreenTarget(RdpOptions? Options, string? Slot = null, int MaxKilobitsPerSecond = 16_000, CertificateTrust? Trust = null, SharedDrive? Drive = null, VncOptions? Vnc = null)
{
    /// <summary>Ein VNC-Ziel.</summary>
    public static ScreenTarget ForVnc(VncOptions vnc, string? slot = null, int maxKilobitsPerSecond = 16_000, CertificateTrust? trust = null) => new(null, slot, maxKilobitsPerSecond, trust, null, vnc);
}

/// <summary>Einmal-Tickets fuer den Bildschirm-WebSocket (siehe <see cref="Tickets{T}"/>).</summary>
public sealed class ScreenTickets(TimeProvider time) : Tickets<ScreenTarget>(time);

/// <summary>Einmal-Tickets fuer Downloads aus der Zwischenablage (der Browser holt sie mit GET /api/remote/clip/Ticket ab; siehe <see cref="ClipDownload"/>).</summary>
public sealed class ClipDownloads(TimeProvider time) : Tickets<ClipDownload>(time);
