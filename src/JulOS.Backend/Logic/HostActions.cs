using System.Text.RegularExpressions;

namespace JulOS.Backend.Logic;

/// <summary>Was fuer eine Aktion als Ziel gilt: nichts, "all" oder ein Paket der Update-Liste, ein Dienst der gemeldeten Liste, ein Container der Containerliste.</summary>
public enum HostTarget { None, Package, Unit, Container }

/// <summary>Eine Aktion auf einem Host (Stufe 3). Name und Pfad sind fest; der Pfad ist der einzige, den der Server dafuer an den Agenten schickt.</summary>
public sealed record HostActionDef(string Name, string Right, string Cap, string Path, HostTarget Target, TimeSpan Timeout, bool ReadOnly = false)
{
    /// <summary>Muss der Benutzer zur Bestaetigung den Rechnernamen tippen? Neustart, Ausschalten und "alle Updates".</summary>
    public bool RequiresTyping(string? target) => Name is "power.reboot" or "power.poweroff" || (Name == "updates.install" && target == HostActions.AllPackages);
}

/// <summary>Die Liste aller Aktionen (docs/HOST-AGENT.md, Stufe 3) und die Pruefung der Zielnamen. Was nicht hier steht, schickt der Server nie.</summary>
public static partial class HostActions
{
    public const string AllPackages = "all";
    public static readonly Version MinAgentVersion = new(0, 6, 0);

    public static readonly IReadOnlyList<HostActionDef> All =
    [
        new("updates.refresh", "updates", "updates", "/v1/host/updates/refresh", HostTarget.None, TimeSpan.FromMinutes(11)),
        new("updates.install", "updates", "updates", "/v1/host/updates/install", HostTarget.Package, TimeSpan.FromMinutes(65)),
        new("updates.repair", "updates", "updates", "/v1/host/updates/repair", HostTarget.None, TimeSpan.FromMinutes(65)),
        new("power.reboot", "reboot", "sysinfo", "/v1/host/reboot", HostTarget.None, TimeSpan.FromSeconds(40)),
        new("power.poweroff", "reboot", "sysinfo", "/v1/host/poweroff", HostTarget.None, TimeSpan.FromSeconds(40)),
        new("service.start", "services", "services", "/v1/host/service/start", HostTarget.Unit, TimeSpan.FromMinutes(3)),
        new("service.stop", "services", "services", "/v1/host/service/stop", HostTarget.Unit, TimeSpan.FromMinutes(3)),
        new("service.restart", "services", "services", "/v1/host/service/restart", HostTarget.Unit, TimeSpan.FromMinutes(3)),
        new("docker.start", "docker", "docker", "/v1/host/docker/start", HostTarget.Container, TimeSpan.FromMinutes(3)),
        new("docker.stop", "docker", "docker", "/v1/host/docker/stop", HostTarget.Container, TimeSpan.FromMinutes(3)),
        new("docker.restart", "docker", "docker", "/v1/host/docker/restart", HostTarget.Container, TimeSpan.FromMinutes(3)),
        new("docker.pull", "docker", "docker", "/v1/host/docker/pull", HostTarget.Container, TimeSpan.FromMinutes(17)),
        new("logs.unit", "logs", "services", "/v1/host/logs/unit", HostTarget.Unit, TimeSpan.FromSeconds(90), ReadOnly: true),
        new("logs.container", "logs", "docker", "/v1/host/logs/container", HostTarget.Container, TimeSpan.FromSeconds(90), ReadOnly: true),
    ];

    public static HostActionDef? Find(string? name) => name is null ? null : All.FirstOrDefault(a => a.Name == name);

    // Gleiche Muster wie im Agenten (tools/julos-agent.py); der Server prueft vor dem Senden, der Agent und sein Helfer noch einmal.
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9+.\-]*[a-z0-9+.])?(?::[a-z0-9]+)?\z")] private static partial Regex PackagePattern();
    [GeneratedRegex(@"^(?!-)[A-Za-z0-9:_.@\-]{1,100}\.service\z")] private static partial Regex UnitPattern();
    [GeneratedRegex(@"^[0-9a-f]{12}\z")] private static partial Regex ContainerPattern();

    /// <summary>Ist das ein gueltiger Debian-Paketname? (Der Agent meldet Namen aus Protokollen: nur solche werden uebernommen.)</summary>
    public static bool IsPackageName(string? name) => name is not null && PackagePattern().IsMatch(name);

    /// <summary>Sieht der Zielname richtig aus? (Ob er wirklich in der Liste des Agenten steht, prueft der Dienst danach.)</summary>
    public static bool ValidTarget(HostActionDef action, string? target) => action.Target switch
    {
        HostTarget.None => string.IsNullOrEmpty(target),
        HostTarget.Package => target == AllPackages || (target is not null && PackagePattern().IsMatch(target)),
        HostTarget.Unit => target is not null && UnitPattern().IsMatch(target),
        HostTarget.Container => target is not null && ContainerPattern().IsMatch(target),
        _ => false,
    };

    /// <summary>JulOS' eigene Container-Gruppe (Compose-Projekt "julos" oder ein Image mit "julos" im Namen): wer sie stoppt, saegt am eigenen Ast. Nur eine Warnung, keine Sperre.</summary>
    public static bool LooksLikeJulOs(string? project, string? image) =>
        string.Equals(project, "julos", StringComparison.OrdinalIgnoreCase) || (image?.Contains("julos", StringComparison.OrdinalIgnoreCase) ?? false);
}
