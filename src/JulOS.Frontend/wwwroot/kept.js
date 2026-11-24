/* =====================================================================
 * Erhaltene Sitzungen, Seite des Browsers (docs/SESSION-KEEP.md, Schritt 1): nach einem Neuladen holt sich die Seite ihre eigene, gerade unbeobachtete
 * Sitzung zurueck, ohne zu fragen. Reine Logik ohne DOM und ohne fetch (getestet in tests/frontend/kept.test.mjs); app.js reicht list/take/sleep herein.
 *
 * Die Seite merkt sich je Verbindung die oeffentliche Kennung ihrer Sitzung in sessionStorage (je Tab, ueberlebt ein Neuladen, verschwindet mit dem Tab).
 * Die Kennung ist kein Geheimnis: allein oeffnet sie nichts, der Server verlangt die App-Sitzung, die Besitzer-Pruefung und ein Einmal-Ticket.
 * Alles andere (mehrere Sitzungen, eine, die gerade angesehen wird, keine gemerkte Kennung) verhaelt sich wie bisher: eine neue Sitzung.
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
 * Was tun mit der Liste des Servers? list: die erhaltenen Sitzungen dieser Verbindung ([{ id, unwatchedSince }]), remembered: die gemerkte Kennung.
 *  take: die eigene Sitzung wartet ohne Browser: nehmen.
 *  wait: sie ist noch "angesehen" (der alte Socket eines Neuladens ist beim Server noch nicht als weg erkannt): gleich noch einmal fragen.
 *  new:  es gibt sie nicht (mehr) oder nichts gemerkt: eine neue Sitzung wie bisher.
 */
export function decide(list, remembered) {
  if (!remembered || !Array.isArray(list)) return { action: 'new' };
  const mine = list.find((k) => k && k.id === remembered);
  if (!mine) return { action: 'new' };
  return mine.unwatchedSince ? { action: 'take', id: mine.id } : { action: 'wait' };
}

export const WAIT_MS = 500;      // das Neuladen schliesst den alten Socket; der Server merkt es meist sofort, manchmal erst nach einem Atemzug
export const MAX_WAITS = 4;      // dann gilt die Sitzung als anderswo angesehen (zum Beispiel ein kopierter Tab) und es gibt eine neue

/**
 * Die eigene erhaltene Sitzung dieser Verbindung zurueckholen.
 * @param deps.list  (connId) => Promise<[{ id, unwatchedSince }]>
 * @param deps.take  (id) => Promise<{ ticket, layout, drive }>  (der Server antwortet auf unbekannt und fremd gleich: Fehler)
 * @returns {{ ticket, layout, drive } | null}  null: eine neue Sitzung aufbauen (jeder Fehler hier heisst nur "nicht zurueckgeholt")
 */
export async function reclaim(connId, { storage, list, take, sleep }) {
  const handle = recall(storage, connId);
  if (!handle) return null;
  for (let waits = 0; waits <= MAX_WAITS; waits++) {
    let items;
    try { items = await list(connId); } catch { return null; }
    const d = decide(items, handle);
    if (d.action === 'take') {
      try {
        const got = await take(d.id);
        if (got && got.ticket) return got;
      } catch { /* weg oder abgelehnt: neue Sitzung */ }
      forget(storage, connId);
      return null;
    }
    if (d.action === 'new') { forget(storage, connId); return null; }
    if (waits < MAX_WAITS) await sleep(WAIT_MS);
  }
  return null;
}
