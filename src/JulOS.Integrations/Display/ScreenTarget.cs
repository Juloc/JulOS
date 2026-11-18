using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display;

/// <summary>Was eine Bildschirm-Sitzung verbinden soll. Slot: pro Benutzer und Verbindung ist nur ein Browser offen, ein neuer loest den alten ab.</summary>
public sealed record ScreenTarget(RdpOptions Options, string? Slot = null, int MaxKilobitsPerSecond = 16_000);

/// <summary>Einmal-Tickets fuer den Bildschirm-WebSocket (siehe <see cref="Tickets{T}"/>).</summary>
public sealed class ScreenTickets(TimeProvider time) : Tickets<ScreenTarget>(time);
