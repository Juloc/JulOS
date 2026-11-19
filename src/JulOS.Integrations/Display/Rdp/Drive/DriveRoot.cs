using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display.Rdp.Drive;

/// <summary>
/// Der eine Ordner des Servers, der als Laufwerk in Sitzungen gereicht werden darf (Einstellung <c>JULOS_DRIVE_ROOT</c>). Er wird beim Start geprueft und
/// aufgeloest (<see cref="DriveJail.TryCreate"/>); ist er leer, "off" oder ungueltig, gibt es keine Laufwerksfreigabe, auch wenn eine Verbindung sie
/// verlangt. Der Browser kann den Pfad weder sehen noch aendern: je Verbindung gibt es nur die Schalter "freigeben" und "schreiben".
/// </summary>
public sealed class DriveRoot
{
    private DriveRoot(string? root) => Root = root;

    /// <summary>Der echte Pfad der Wurzel, oder null (keine Freigabe).</summary>
    public string? Root { get; }

    public bool Available => Root is not null;

    public static DriveRoot Off { get; } = new(null);

    /// <summary>Liest die Einstellung; ein Fehler (Pfad fehlt, nicht absolut, "/") wird gemeldet und schaltet die Freigabe ab.</summary>
    public static DriveRoot FromSetting(string? setting, ILogger? log = null)
    {
        var jail = DriveJail.TryCreate(setting, out var error);
        if (jail is null)
        {
            if (error.Length > 0)
            {
                log?.LogWarning("Laufwerksfreigabe aus: {Reason}", error);
            }

            return Off;
        }

        log?.LogInformation("Laufwerksfreigabe vorbereitet: ein Ordner des Servers kann je Verbindung als Laufwerk gereicht werden (nur Lesen, Schreiben mit eigenem Schalter)");
        return new DriveRoot(jail.Root);
    }

    /// <summary>Das Laufwerk fuer eine Verbindung, oder null, wenn es keine Wurzel gibt.</summary>
    public SharedDrive? For(bool writable) => Root is null ? null : new SharedDrive(Root, writable);
}
