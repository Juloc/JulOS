using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Rdp.Drive;
using JulOS.Integrations.Display.Vnc;

namespace JulOS.Integrations.Display;

/// <summary>
/// Was eine Bildschirm-Sitzung verbinden soll. Slot: pro Benutzer und Verbindung ist nur ein Browser offen, ein neuer loest den alten ab.
/// Trust: das Zertifikat des Ziels (Fingerabdruck merken, Aenderung melden); ohne Angabe wird jedes Zertifikat angenommen (Tests).
/// Vnc: statt RDP ein VNC-Ziel (dann tragen Options nur Host und Port, und Drive hat keine Wirkung).
/// Drive: ein Ordner des Servers, der als Laufwerk in die Sitzung gereicht wird (Wurzel aus der Server-Einstellung, Schreiben nur mit eigenem Schalter); ohne Angabe keins.
/// Kept: nur bei einem Ticket der zweiten Art (eine erhaltene Sitzung nehmen, siehe <see cref="ScreenTickets.CreateKept"/>); dann sind die anderen Angaben leer.
/// Info: was die Liste der erhaltenen Sitzungen ueber diese Sitzung wissen darf (Verbindung, Name, Geraet).
/// </summary>
public sealed record ScreenTarget(RdpOptions Options, string? Slot = null, int MaxKilobitsPerSecond = 16_000, CertificateTrust? Trust = null, SharedDrive? Drive = null, VncOptions? Vnc = null, KeptTake? Kept = null, KeptInfo? Info = null)
{
    /// <summary>Ein VNC-Ziel.</summary>
    public static ScreenTarget ForVnc(VncOptions vnc, string? slot = null, int maxKilobitsPerSecond = 16_000, CertificateTrust? trust = null) => new(new RdpOptions { Host = vnc.Host, Port = vnc.Port }, slot, maxKilobitsPerSecond, trust, null, vnc);
}

/// <summary>Was die Liste der erhaltenen Sitzungen ueber eine Sitzung weiss: die Verbindung, ihr Name (wie die Person ihn vergeben hat) und das Geraet (kurzer Text aus dem Browser); Layout und Drive (ro/rw) nur, damit die Seite beim Nehmen dasselbe weiss wie nach einem Ticket. Nie Host, Adresse oder Ziel-Benutzer.</summary>
public sealed record KeptInfo(Guid ConnectionId, string ConnectionName, string Device, string? Layout = null, string? Drive = null);

/// <summary>Eine erhaltene Sitzung nehmen: ihre oeffentliche Kennung und die App-Sitzung, die danach gefragt hat (nur diese darf das Ticket einloesen).</summary>
public sealed record KeptTake(string Handle, Guid AppSession);

/// <summary>
/// Einmal-Tickets fuer den Bildschirm-WebSocket (siehe <see cref="Tickets{T}"/>). Zwei Arten: eine neue Sitzung (die Verbindungsdaten) und das Nehmen einer
/// erhaltenen Sitzung (<see cref="ScreenTarget.Kept"/>; kurz gueltig, an Person, Sitzung und App-Sitzung gebunden).
/// </summary>
public sealed class ScreenTickets(TimeProvider time) : Tickets<ScreenTarget>(time)
{
    /// <summary>So lange gilt ein Ticket zum Nehmen einer erhaltenen Sitzung.</summary>
    public static readonly TimeSpan KeptLifetime = TimeSpan.FromSeconds(20);

    public string? CreateKept(Guid userId, KeptTake take) => Create(userId, new ScreenTarget(new RdpOptions { Host = "" }, Kept: take), KeptLifetime);

    /// <summary>
    /// Loest ein Ticket am WebSocket ein (einmal, nur die Person, nicht abgelaufen). Ein Ticket zum Nehmen gilt zusaetzlich nur fuer die App-Sitzung, die es geholt hat:
    /// ein anderes Geraet derselben Person mit demselben Ticket bekommt null (das Ticket ist dann trotzdem verbraucht).
    /// </summary>
    public ScreenTarget? Redeem(string ticket, Guid userId, Guid appSession)
    {
        var target = Take(ticket, userId);
        return target?.Kept is { } k && k.AppSession != appSession ? null : target;
    }
}

/// <summary>Einmal-Tickets fuer Downloads aus der Zwischenablage (der Browser holt sie mit GET /api/remote/clip/Ticket ab; siehe <see cref="ClipDownload"/>).</summary>
public sealed class ClipDownloads(TimeProvider time) : Tickets<ClipDownload>(time);
