/* =====================================================================
 * Erhaltene Sitzungen, Seite des Browsers (docs/SESSION-KEEP.md, Schritte 1 und 3): nach einem Neuladen holt sich die Seite ihre eigene, gerade unbeobachtete
 * Sitzung zurueck, ohne zu fragen; die Sitzung eines anderen Geraets holt sie nur nach einer Rueckfrage (Uebergabe). Reine Logik ohne DOM und ohne fetch (getestet in tests/frontend/kept.test.mjs); app.js reicht list/take/sleep herein.
 *
 * Die Seite merkt sich je Verbindung die oeffentliche Kennung ihrer Sitzung in sessionStorage (je Tab, ueberlebt ein Neuladen, verschwindet mit dem Tab).
 * Die Kennung ist kein Geheimnis: allein oeffnet sie nichts, der Server verlangt die App-Sitzung, die Besitzer-Pruefung und ein Einmal-Ticket.
 * Ohne Rueckfrage eine neue Sitzung aufzubauen, waehrend das Ziel noch eine erhaltene hat, hiesse: das Ziel gibt der neuen Anmeldung den Desktop, die alte
 * verbindet sich neu und nimmt ihn zurueck, und die beiden Geraete streiten sich.
 * ===================================================================== */

const PREFIX = 'julos.kept.';

/** Der Server hat dem Browser die Kennung seiner Sitzung genannt (Nachricht 11): merken, fuer ein Neuladen dieses Tabs. */
export function remember(storage, connId, handle) {
  if (!storage || !connId || !handle) return;
  try { storage.setItem(PREFIX + connId, String(handle)); } catch { /* voll oder gesperrt: dann eben keine stille Rueckkehr */ }
}

export function recall(storage, connId) {
  try { return (storage && connId && storage.getItem(PREFIX + connId)) || null; } catch { return null; }
}

export function forget(storage, connId) {
  try { storage && connId && storage.removeItem(PREFIX + connId); } catch { /* egal */ }
}

/**
 * Was tun mit der Liste des Servers? list: die erhaltenen Sitzungen dieser Verbindung ([{ id, since, unwatchedSince, device, own }]), remembered: die gemerkte Kennung.
 *  take: die eigene Sitzung wartet ohne Browser: still nehmen (ein Neuladen); ebenso eine wartende, die dieses Geraet (diese App-Sitzung, own) zuletzt hatte.
 *  wait: sie ist noch "angesehen" (der alte Socket eines Neuladens ist beim Server noch nicht als weg erkannt): gleich noch einmal fragen.
 *  ask:  eine Sitzung, die nicht die gemerkte ist (ein anderes Geraet, ein kopierter Tab, ein Handy ohne sessionStorage): die Person fragt der Dialog
 *        ("Hierher holen" / "Neue Sitzung"); nie still nehmen. Bei mehreren die zuletzt begonnene.
 *  new:  es gibt keine: eine neue Sitzung wie bisher.
 */
export function decide(list, remembered) {
  if (!Array.isArray(list)) return { action: 'new' };
  const items = list.filter((k) => k && k.id);
  const mine = remembered && items.find((k) => k.id === remembered);
  if (mine) return mine.unwatchedSince ? { action: 'take', id: mine.id } : { action: 'wait' };
  if (!items.length) return { action: 'new' };
  const latest = (xs) => xs.reduce((a, b) => (String(b.since || '') > String(a.since || '') ? b : a));
  // Dieses Geraet hatte sie zuletzt und niemand sieht sie an (Tab geschlossen und wieder geoeffnet): still nehmen, wie ein Neuladen
  const ownWaiting = items.filter((k) => k.own && k.unwatchedSince);
  if (ownWaiting.length) return { action: 'take', id: latest(ownWaiting).id };
  return { action: 'ask', item: latest(items) };
}

export const WAIT_MS = 500;      // das Neuladen schliesst den alten Socket; der Server merkt es meist sofort, manchmal erst nach einem Atemzug
export const MAX_WAITS = 4;      // dann gilt die eigene Sitzung als anderswo angesehen (zum Beispiel ein kopierter Tab) und der Dialog fragt

/**
 * Vor dem ersten Aufbau: die eigene erhaltene Sitzung zurueckholen, oder fragen, was mit der eines anderen Geraets geschehen soll.
 * @param deps.list  (connId) => Promise<[{ id, since, unwatchedSince, device, own }]>
 * @param deps.take  (id, confirm) => Promise<{ ticket, layout, drive }>  (der Server antwortet auf unbekannt und fremd gleich: Fehler)
 * @param deps.end   (id) => Promise  beendet eine erhaltene Sitzung ("Neue Sitzung": sonst gaebe das Ziel der neuen Anmeldung den Desktop, und die alte zeigte "uebernommen")
 * @param deps.ask   (item) => Promise<'take' | 'new' | 'cancel'>  der Dialog
 * @returns {{ ticket, layout, drive } | { cancelled: true } | null}  null: eine neue Sitzung aufbauen (jeder Fehler hier heisst nur "nicht zurueckgeholt")
 */
export async function reclaim(connId, { storage, list, take, end, ask, sleep }) {
  const handle = recall(storage, connId);
  for (let waits = 0; ; waits++) {
    let items;
    try { items = await list(connId); } catch { return null; }
    let d = decide(items, handle);
    if (d.action === 'wait' && waits < MAX_WAITS) { await sleep(WAIT_MS); continue; }
    if (d.action === 'wait') d = { action: 'ask', item: items.find((k) => k && k.id === handle) };
    if (d.action === 'new') { forget(storage, connId); return null; }
    let confirm = false;
    if (d.action === 'ask') {
      const answer = await ask(d.item);
      if (answer === 'cancel') return { cancelled: true };
      if (answer !== 'take') {
        try { await end(d.item.id); } catch { /* schon weg: egal */ }
        forget(storage, connId);
        return null;
      }
      d = { action: 'take', id: d.item.id }; confirm = true;
    }
    try {
      const got = await take(d.id, confirm);
      if (got && got.ticket) return got;
    } catch { /* weg oder abgelehnt: neue Sitzung */ }
    forget(storage, connId);
    return null;
  }
}
