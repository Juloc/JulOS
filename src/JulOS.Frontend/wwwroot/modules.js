/* =====================================================================
 * Module Caddy und DDNS (nur lesen): gemeinsame Daten und Texte fuer Desktop (desktop-modules.js) und Handy (mobile.js).
 * Keine Oberflaeche hier, nur Abrufe und deutsche Beschriftungen (getestet in tests/frontend/modules.test.mjs).
 * Standardmaessig sind beide Module am Server aus; dann gibt es weder Fenster noch Karte.
 * ===================================================================== */

/** Welche Module der Server eingeschaltet hat. Bei jedem Fehler (auch 401) gilt: keins. */
export async function loadModules(api) {
  try {
    const m = await api('/api/modules');
    return { caddy: m.caddy === 'read', ddns: m.ddns === 'read' };
  } catch { return { caddy: false, ddns: false }; }
}

const KINDS = { proxy: 'Weiterleitung', files: 'Dateien', redirect: 'Umleitung', respond: 'Antwort', other: 'Sonstiges' };
export const kindLabel = (k) => KINDS[k] || 'Sonstiges';

/** Domain, Pfad und Ziel einer Zeile als Text. */
export function routeParts(r) {
  const domain = r.domain === '*' ? '(alle Hosts)' : r.domain;
  const path = r.path || '';
  let target;
  if (r.kind === 'proxy') target = (r.targets || []).join(', ') || 'kein Ziel';
  else if (r.kind === 'files') target = (r.targets || [])[0] || 'Dateiserver';
  else if (r.kind === 'redirect') target = (r.targets || [])[0] || 'Umleitung';
  else if (r.kind === 'respond') target = r.detail ? 'feste Antwort ' + r.detail : 'feste Antwort';
  else target = 'nicht angezeigt';
  return { domain, path, target, kind: kindLabel(r.kind) };
}

const SOURCES = { admin: 'laufende Konfiguration (Admin-API)', caddyfile: 'Caddyfile', none: 'keine Quelle lesbar' };
export const sourceLabel = (s) => SOURCES[s] || s;

/** Erreichbarkeit je Ziel: { 'host:port': {state, ms} } */
export function reachMap(reach) {
  const m = new Map();
  for (const u of (reach && reach.upstreams) || []) m.set(u.target, u);
  return m;
}
export const reachText = (u) => (!u ? '' : u.state === 'up' ? 'erreichbar' + (u.ms !== undefined ? ` (${u.ms} ms)` : '') : u.state === 'down' ? 'nicht erreichbar' : 'nicht prüfbar');
export const reachClass = (u) => (!u ? '' : u.state === 'up' ? 'ok' : u.state === 'down' ? 'bad' : 'idle');

const STATES = { synced: 'Synchron', notSynced: 'Nicht synchron', unresolved: 'Name nicht auflösbar', unknown: 'Unbekannt' };
export const ddnsStateLabel = (s) => STATES[s] || 'Unbekannt';
export const ddnsStateClass = (s) => (s === 'synced' ? 'ok' : s === 'notSynced' ? 'bad' : 'idle');

/** "vor 5 s", "vor 3 min" ... aus einem ISO-Zeitpunkt. */
export function ago(iso, now = Date.now()) {
  const s = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (!Number.isFinite(s)) return '';
  if (s < 5) return 'gerade eben';
  if (s < 60) return `vor ${s} s`;
  if (s < 3600) return `vor ${Math.round(s / 60)} min`;
  return `vor ${Math.round(s / 3600)} h`;
}

/** Eine Zeile fuer die Kopfzeile der DDNS-Karte. */
export function ddnsHeadline(s) {
  if (!s) return '';
  if (s.state === 'synced') return 'Alle Namen zeigen auf die aktuelle Adresse.';
  if (s.state === 'notSynced') {
    const n = (s.hosts || []).filter((h) => h.state !== 'synced').length;
    return n === 1 ? 'Ein Name zeigt nicht auf die aktuelle Adresse.' : `${n} Namen zeigen nicht auf die aktuelle Adresse.`;
  }
  return s.message || 'Der Zustand ist nicht bekannt.';
}
