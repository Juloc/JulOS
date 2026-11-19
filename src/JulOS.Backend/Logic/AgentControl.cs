namespace JulOS.Backend.Logic;

/// <summary>
/// Die Stufe 'control' eines Agenten (Spalte agents.control, docs/HOST-AGENT.md): was der SERVER ihm auftragen darf. Lesen braucht keine Stufe.
/// Der Agent prueft lokal noch einmal gegen seine Datei /etc/julos-agent/permissions; ein Schreibauftrag geht nur durch, wenn beide erlauben.
/// In dieser Version gibt es keine Schreibaktion, die Pruefung steht fuer Stufe 3 (Updates installieren, Neustart) bereit.
/// </summary>
public static class AgentControl
{
    public const string Read = "read", Updates = "updates", Full = "full";
    public static readonly string[] Levels = [Read, Updates, Full];

    /// <summary>Rechte, die ein Auftrag verlangen kann (dieselben Woerter wie in der Datei des Agenten).</summary>
    public static readonly string[] Rights = ["updates", "reboot"];

    public static bool IsLevel(string? level) => level is Read or Updates or Full;

    /// <summary>Erlaubt die Stufe dieses Recht? 'updates' ab Stufe updates, 'reboot' nur bei full; Unbekanntes nie.</summary>
    public static bool Allows(string? level, string right) => (level, right) switch
    {
        (Updates or Full, "updates") => true,
        (Full, "reboot") => true,
        _ => false,
    };

    /// <summary>Server UND Agent muessen es erlauben: die Stufe am Server und die lokal gemeldeten Rechte (<paramref name="agentAllows"/>, aus dem hello).</summary>
    public static bool Permitted(string? level, IReadOnlyCollection<string> agentAllows, string right) => Allows(level, right) && agentAllows.Contains(right);
}
