using JulOS.Backend.Data;
using JulOS.Integrations.Display;

namespace JulOS.Frontend.Remote;

/// <summary>
/// Wer darf eine erhaltene Sitzung nehmen? Die Regel steht in drei Schichten: der Besitzer (<c>ScreenRelay.FindKept</c> und die Wiederaufnahme pruefen "gehoert der Person"),
/// ein Einmal-Ticket, das an die Person, die Sitzung und die fragende App-Sitzung gebunden ist (<c>ScreenTickets.Redeem</c>), und diese Stelle.
///
/// Entscheidung des Besitzers vom 2026-11-24 (docs/SESSION-KEEP.md, "Decisions of the owner"): <b>kein zweiter Faktor beim Nehmen</b>, auf keinem Geraet.
/// Darum gibt es keinen Nachweis zu pruefen. Eine Grenze gilt trotzdem, ebenfalls Entscheidung des Besitzers: eine Sitzung, die <b>anderswo gerade angesehen</b> wird, wird nie still genommen
/// (Schritt 3, die Uebergabe): nehmen ohne Rueckfrage nur, wenn niemand zusieht, oder wenn es dieselbe App-Sitzung ist, die sie zuletzt hatte (ein Neuladen,
/// dessen alter Socket noch nicht als weg erkannt ist). Sieht ein anderes Geraet zu, fragt die Seite die Person ("Hierher holen?") und schickt erst dann <c>confirm</c>;
/// das alte Geraet erfaehrt es (Status 777) und kann sie ebenso zurueckholen. Wer das Verfahren verschaerfen will (die geplante Einstellung "beim Nehmen von einem anderen Geraet den zweiten Faktor
/// verlangen", siehe docs/SECURITY-REVIEW.md), tut es genau hier: <c>kept.AppSession != who.Id</c> ist "anderes Geraet", der Rest steht in <c>AuthService.StepUpFreshAsync</c>.
/// </summary>
internal static class KeptTakeRule
{
    public static bool Allows(SessionIdentity who, ScreenRelay.KeptSession kept, bool confirmed) =>
        kept.UnwatchedSince is not null || kept.AppSession == who.Id || confirmed;
}
