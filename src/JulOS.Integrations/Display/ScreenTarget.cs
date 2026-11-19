using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Rdp.Drive;

namespace JulOS.Integrations.Display;

/// <summary>
/// Was eine Bildschirm-Sitzung verbinden soll. Slot: pro Benutzer und Verbindung ist nur ein Browser offen, ein neuer loest den alten ab.
/// Trust: das Zertifikat des Ziels (Fingerabdruck merken, Aenderung melden); ohne Angabe wird jedes Zertifikat angenommen (Tests).
/// Drive: ein Ordner des Servers, der als Laufwerk in die Sitzung gereicht wird (Wurzel aus der Server-Einstellung, Schreiben nur mit eigenem Schalter); ohne Angabe keins.
/// </summary>
public sealed record ScreenTarget(RdpOptions Options, string? Slot = null, int MaxKilobitsPerSecond = 16_000, CertificateTrust? Trust = null, SharedDrive? Drive = null);

/// <summary>Einmal-Tickets fuer den Bildschirm-WebSocket (siehe <see cref="Tickets{T}"/>).</summary>
public sealed class ScreenTickets(TimeProvider time) : Tickets<ScreenTarget>(time);
