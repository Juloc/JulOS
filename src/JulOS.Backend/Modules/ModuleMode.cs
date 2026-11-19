namespace JulOS.Backend.Modules;

/// <summary>
/// Schalter je Funktion (JULOS_SERVICES, JULOS_CADDY, JULOS_DDNS). Standard ist aus. read = nur ansehen, control = auch aendern.
/// Wo es noch kein Schreiben gibt (Caddy), gilt control wie read und der Start meldet das im Log.
/// </summary>
public enum ModuleMode { Off, Read, Control }

public static class ModuleModes
{
    public static ModuleMode Parse(string name, string? value, List<string> warnings, bool controlExists = true)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "off": return ModuleMode.Off;
            case "read": return ModuleMode.Read;
            case "control":
                if (controlExists) return ModuleMode.Control;
                warnings.Add($"{name}=control: Schreiben gibt es hier nicht (nur als Entwurf in docs/MODULES.md), es gilt read.");
                return ModuleMode.Read;
            default:
                warnings.Add($"{name}: unbekannter Wert (erlaubt: off, read, control). Das Modul bleibt aus.");
                return ModuleMode.Off;
        }
    }

    public static string Name(ModuleMode m) => m switch { ModuleMode.Off => "off", ModuleMode.Read => "read", _ => "control" };
}
