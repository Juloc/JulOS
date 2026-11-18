namespace JulOS.Integrations.Guacamole;

/// <summary>Einmal-Tickets fuer den Guacamole-WebSocket (siehe <see cref="Tickets{T}"/>).</summary>
public sealed class GuacTickets(TimeProvider time) : Tickets<GuacSpec>(time);
