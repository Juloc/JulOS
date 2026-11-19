namespace JulOS.Backend.Logic;

/// <summary>
/// Die Stufe 'control' eines Agenten (Spalte agents.control, docs/HOST-AGENT.md): was der SERVER ihm auftragen darf. Lesen braucht keine Stufe.
/// Standard ist 'read': keine Aktion. Der Benutzer schaltet Aktionen je Host ein: 'updates' (Updates installieren, Logs lesen) oder 'full' (zusaetzlich Neustart/Ausschalten,
/// Dienste, Container). Der Agent prueft lokal noch einmal gegen seine Datei /etc/julos-agent/permissions; eine Aktion geht nur durch, wenn beide erlauben.
/// </summary>
public static class AgentControl
{
    public const string Read = "read", Updates = "updates", Full = "full";
    public static readonly string[] Levels = [Read, Updates, Full];

    /// <summary>Rechte, die ein Auftrag verlangen kann (dieselben Woerter wie in der Datei des Agenten).</summary>
    public static readonly string[] Rights = ["updates", "reboot", "services", "docker", "logs"];

    public static bool IsLevel(string? level) => level is Read or Updates or Full;

    /// <summary>Erlaubt die Stufe dieses Recht? 'updates' und 'logs' ab Stufe updates, der Rest nur bei full; Unbekanntes nie.</summary>
    public static bool Allows(string? level, string right) => (level, right) switch
    {
        (Updates or Full, "updates" or "logs") => true,
        (Full, "reboot" or "services" or "docker") => true,
        _ => false,
    };

    /// <summary>Server UND Agent muessen es erlauben: die Stufe am Server und die lokal gemeldeten Rechte (<paramref name="agentAllows"/>, aus dem hello).</summary>
    public static bool Permitted(string? level, IReadOnlyCollection<string> agentAllows, string right) => Allows(level, right) && agentAllows.Contains(right);
}
