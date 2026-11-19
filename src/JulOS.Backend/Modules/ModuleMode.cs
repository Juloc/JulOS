namespace JulOS.Backend.Modules;

/// <summary>
/// Schalter je Modul (JULOS_CADDY, JULOS_DDNS). Standard ist aus. "control" ist fuer spaeter vorgesehen (Schreiben); in dieser
/// Version gibt es das nicht, der Wert gilt deshalb wie "read" (und der Start meldet das im Log).
/// </summary>
public enum ModuleMode { Off, Read }

public static class ModuleModes
{
    public static ModuleMode Parse(string name, string? value, List<string> warnings)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "off": return ModuleMode.Off;
            case "read": return ModuleMode.Read;
            case "control":
                warnings.Add($"{name}=control: Schreiben gibt es in dieser Version nicht, es gilt read.");
                return ModuleMode.Read;
            default:
                warnings.Add($"{name}: unbekannter Wert (erlaubt: off, read, control). Das Modul bleibt aus.");
                return ModuleMode.Off;
        }
    }
}
