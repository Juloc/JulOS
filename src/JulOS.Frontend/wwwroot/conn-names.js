/* =====================================================================
 * Anzeigenamen der Verbindungen (Handy und Desktop): die Host-Zeile unter dem Namen und, wenn zwei Verbindungen gleich heissen,
 * ein Zusatz, an dem man sie unterscheidet (erst der Host, dann die Art, zuletzt die Adresse). Der gespeicherte Name bleibt, wie er ist.
 * Reine Logik ohne DOM, getestet in tests/frontend/conn-names.test.mjs.
 * ===================================================================== */
import { t } from './i18n.js';
import { tileKind, KIND_KEY } from './terminal-programs.js';

const norm = (s) => String(s || '').trim().toLowerCase();
/** Der Name des Hosts, zu dem die Verbindung gehoert (sonst die Adresse, mit der sie sich verbindet). */
export const hostLabel = (hosts, c) => { const h = (hosts || []).find((x) => x.id === c.hostId); return h ? h.name : c.host || ''; };

/** Map Kennung -> { name, host }: name mit Zusatz, wenn der Name mehrfach vorkommt; host ist die Zeile unter dem Namen. */
export function connLabels(conns, hosts) {
  const out = new Map();
  const groups = new Map();
  for (const c of conns) { const k = norm(c.name); (groups.get(k) || groups.set(k, []).get(k)).push(c); }
  for (const c of conns) out.set(c.id, { name: c.name, host: hostLabel(hosts, c) });
  for (const same of groups.values()) {
    if (same.length < 2) continue;
    const distinct = (f) => new Set(same.map((c) => norm(f(c)))).size === same.length;
    const add = distinct((c) => hostLabel(hosts, c)) ? (c) => hostLabel(hosts, c)
      : distinct((c) => tileKind(c)) ? (c) => t(KIND_KEY[tileKind(c)])
      : (c) => `${c.host}:${c.port}`;
    for (const c of same) out.get(c.id).name = `${c.name} (${add(c)})`;
  }
  return out;
}
