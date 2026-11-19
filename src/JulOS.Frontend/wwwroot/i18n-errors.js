/* =====================================================================
 * Fehlermeldungen des Servers (JSON {"error": "..."}) in der Sprache der Oberflaeche. Der Server schickt feste deutsche Texte; der Browser ordnet bekannte
 * Texte einem Schluessel zu (SERVER_TEXTS genau, SERVER_PATTERNS fuer Texte mit eingesetzten Werten). Unbekannte Texte bleiben unveraendert.
 * Der Server aendert dafuer nichts: Verhalten und Statuscodes bleiben, in Deutsch ist alles wie vorher.
 * ===================================================================== */
import { t, getLang } from './i18n.js';

export const SERVER_TEXTS = {};
/** [RegExp mit Gruppen, Schluessel, [Parameternamen je Gruppe]] */
export const SERVER_PATTERNS = [];

/** Der Text einer Server-Fehlermeldung in der gewaehlten Sprache; unbekannt oder Deutsch: unveraendert. */
export function serverMsg(text) {
  if (typeof text !== 'string' || !text || getLang() === 'de') return text;
  const key = SERVER_TEXTS[text];
  if (key) return t(key) || text;
  for (const [re, k, names] of SERVER_PATTERNS) {
    const m = re.exec(text);
    if (m) { const p = {}; (names || []).forEach((n, i) => { p[n] = m[i + 1]; }); return t(k, p) || text; }
  }
  return text;
}
