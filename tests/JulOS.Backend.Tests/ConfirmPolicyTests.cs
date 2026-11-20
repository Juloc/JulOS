using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

/// <summary>Die Stufen-Zuordnung der Bestaetigung (reine Funktion, docs/HOST-AGENT.md): Routine, gefaehrlich, sehr gefaehrlich und was der Server je Modus verlangt. Die Durchsetzung am Server steht in den Aktions- und Dateitests.</summary>
public sealed class ConfirmPolicyTests
{
    private static (string Level, string Demand) Host(string action, ConfirmMode mode = ConfirmMode.Check, bool runsJulOs = false, bool targetIsJulOs = false)
    {
        var i = ConfirmPolicy.ForHostAction(action, mode, new HostFacts(runsJulOs, targetIsJulOs));
        return (i.LevelName, i.ModeName);
    }

    private static (string Level, string Demand) Files(string action, ConfirmMode mode = ConfirmMode.Check, bool root = false, bool targetRoot = false, string[]? paths = null, bool overwrites = false, bool nonEmpty = false)
    {
        var i = ConfirmPolicy.ForFiles(new FileFacts(action, root, targetRoot, paths ?? ["/home/u/a"], overwrites, nonEmpty), mode);
        return (i.LevelName, i.ModeName);
    }

    [Fact]
    public void Host_actions_are_routine_except_reboot_power_off_and_JulOS_itself()
    {
        foreach (var a in new[] { "updates.refresh", "updates.install", "updates.repair", "service.start", "service.stop", "service.restart", "docker.start", "docker.stop", "docker.restart", "docker.pull", "logs.unit", "logs.container" })
            Assert.Equal(("routine", "click"), Host(a));
        Assert.Equal(("dangerous", "check"), Host("power.reboot"));
        Assert.Equal(("dangerous", "check"), Host("power.poweroff"));
        Assert.Equal(("critical", "check"), Host("power.poweroff", runsJulOs: true));
        Assert.Equal(("dangerous", "check"), Host("power.reboot", runsJulOs: true));                           // ein Neustart bringt JulOS wieder hoch
        Assert.Equal(("critical", "check"), Host("docker.stop", targetIsJulOs: true));
        Assert.Equal(("critical", "check"), Host("service.restart", targetIsJulOs: true));
        Assert.Equal(("routine", "click"), Host("docker.start", targetIsJulOs: true));
    }

    [Fact]
    public void The_mode_decides_what_run_asks_and_very_dangerous_never_drops_below_the_checkbox()
    {
        Assert.Equal(("dangerous", "type"), Host("power.reboot", ConfirmMode.Type));
        Assert.Equal(("dangerous", "click"), Host("power.reboot", ConfirmMode.Click));
        Assert.Equal(("critical", "type"), Host("power.poweroff", ConfirmMode.Type, runsJulOs: true));
        Assert.Equal(("critical", "check"), Host("power.poweroff", ConfirmMode.Click, runsJulOs: true));      // Rueckfall: nie ganz ohne Huerde
        Assert.Equal(("routine", "click"), Host("updates.install", ConfirmMode.Type));                        // Routine bleibt Routine, in jedem Modus
        Assert.Equal(ConfirmMode.Check, ConfirmPolicy.ParseMode(null));
        Assert.Equal(ConfirmMode.Check, ConfirmPolicy.ParseMode("irgendwas"));
        Assert.False(ConfirmPolicy.IsMode("always"));
    }

    [Fact]
    public void File_actions_single_ones_are_routine_many_folders_overwrites_and_root_deletes_are_dangerous()
    {
        foreach (var a in new[] { "upload", "mkdir", "rename", "copy", "move", "download", "text" }) Assert.Equal(("routine", "click"), Files(a));
        Assert.Equal(("routine", "click"), Files("delete"));
        Assert.Equal(("dangerous", "check"), Files("delete", paths: ["/home/u/a", "/home/u/b"]));
        Assert.Equal(("dangerous", "check"), Files("delete", nonEmpty: true));
        Assert.Equal(("dangerous", "check"), Files("delete", root: true, paths: ["/srv/data/x"]));
        Assert.Equal(("routine", "click"), Files("upload", overwrites: true));                                  // ersetzen auf einem gewoehnlichen SSH-Zugang: der Dialog zeigt es, mehr nicht
        Assert.Equal(("dangerous", "check"), Files("upload", root: true, overwrites: true));
        Assert.Equal(("dangerous", "check"), Files("copy", targetRoot: true, overwrites: true));
        Assert.Equal(("routine", "click"), Files("copy", targetRoot: true));
        Assert.Equal(("dangerous", "click"), Files("delete", ConfirmMode.Click, paths: ["/a", "/b"]));
    }

    [Fact]
    public void Paths_near_the_root_on_a_root_place_are_very_dangerous_elsewhere_they_are_not()
    {
        foreach (var p in new[] { "/", "/etc", "/usr", "/var", "/home", "/home/julian", "/boot" })
            Assert.Equal(("critical", "check"), Files("delete", root: true, paths: [p], nonEmpty: true));
        Assert.Equal(("critical", "check"), Files("move", ConfirmMode.Click, root: true, paths: ["/etc"]));      // auch bei "nur Klick" das Haekchen
        Assert.Equal(("critical", "type"), Files("rename", ConfirmMode.Type, root: true, paths: ["/usr"]));
        Assert.Equal(("dangerous", "check"), Files("delete", root: true, paths: ["/etc/nginx"], nonEmpty: true));
        Assert.Equal(("routine", "click"), Files("delete", root: false, paths: ["/etc"]));                      // ein gewoehnlicher SSH-Zugang ist kein Root-Ort
        Assert.Equal(("routine", "click"), Files("copy", root: true, paths: ["/etc"]));                          // Kopieren veraendert die Quelle nicht
        Assert.False(ConfirmPolicy.IsSystemNear("/home/julian/Dokumente"));
        Assert.False(ConfirmPolicy.IsSystemNear("relativ"));
    }
}
