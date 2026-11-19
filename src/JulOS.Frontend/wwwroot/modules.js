/* =====================================================================
 * Module Caddy und DDNS (nur lesen): gemeinsame Daten und Texte fuer Desktop (desktop-modules.js) und Handy (mobile.js).
 * Keine Oberflaeche hier, nur Abrufe und deutsche Beschriftungen (getestet in tests/frontend/modules.test.mjs).
 * Standardmaessig sind beide Module am Server aus; dann gibt es weder Fenster noch Karte.
 * ===================================================================== */

import { t, tk, tp } from './i18n.js';

/** Welche Module der Server eingeschaltet hat. Bei jedem Fehler (auch 401) gilt: keins. */
export async function loadModules(api) {
  try {
    const m = await api('/api/modules');
    return { caddy: m.caddy === 'read', ddns: m.ddns === 'read' };
  } catch { return { caddy: false, ddns: false }; }
}

const KINDS = { proxy: tk('modules.kindProxy'), files: tk('modules.kindFiles'), redirect: tk('modules.kindRedirect'), respond: tk('modules.kindRespond'), other: tk('modules.kindOther') };
export const kindLabel = (k) => t(KINDS[k] || KINDS.other);

/** Domain, Pfad und Ziel einer Zeile als Text. */
export function routeParts(r) {
  const domain = r.domain === '*' ? t('modules.allHosts') : r.domain;
  const path = r.path || '';
  let target;
  if (r.kind === 'proxy') target = (r.targets || []).join(', ') || t('modules.noTarget');
  else if (r.kind === 'files') target = (r.targets || [])[0] || t('modules.fileServer');
  else if (r.kind === 'redirect') target = (r.targets || [])[0] || t('modules.kindRedirect');
  else if (r.kind === 'respond') target = r.detail ? t('modules.fixedReplyWith', { detail: r.detail }) : t('modules.fixedReply');
  else target = t('modules.notShown');
  return { domain, path, target, kind: kindLabel(r.kind) };
}

const SOURCES = { admin: tk('modules.sourceAdmin'), caddyfile: tk('modules.sourceCaddyfile'), none: tk('modules.sourceNone') };
export const sourceLabel = (s) => (SOURCES[s] ? t(SOURCES[s]) : s);

/** Erreichbarkeit je Ziel: { 'host:port': {state, ms} } */
export function reachMap(reach) {
  const m = new Map();
  for (const u of (reach && reach.upstreams) || []) m.set(u.target, u);
  return m;
}
export const reachText = (u) => (!u ? '' : u.state === 'up' ? t('modules.reachUp') + (u.ms !== undefined ? ` (${u.ms} ms)` : '') : u.state === 'down' ? t('modules.reachDown') : t('modules.reachUnknown'));
export const reachClass = (u) => (!u ? '' : u.state === 'up' ? 'ok' : u.state === 'down' ? 'bad' : 'idle');

const STATES = { synced: tk('modules.ddnsSynced'), notSynced: tk('modules.ddnsNotSynced'), unresolved: tk('modules.ddnsUnresolved'), unknown: tk('modules.ddnsUnknown') };
export const ddnsStateLabel = (s) => t(STATES[s] || STATES.unknown);
export const ddnsStateClass = (s) => (s === 'synced' ? 'ok' : s === 'notSynced' ? 'bad' : 'idle');

/** "vor 5 s", "vor 3 min" ... aus einem ISO-Zeitpunkt. */
export function ago(iso, now = Date.now()) {
  const s = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (!Number.isFinite(s)) return '';
  if (s < 5) return t('modules.agoNow');
  if (s < 60) return t('modules.agoSec', { n: s });
  if (s < 3600) return t('modules.agoMin', { n: Math.round(s / 60) });
  return t('modules.agoHour', { n: Math.round(s / 3600) });
}

/** Eine Zeile fuer die Kopfzeile der DDNS-Karte. */
export function ddnsHeadline(s) {
  if (!s) return '';
  if (s.state === 'synced') return t('modules.ddnsAllSynced');
  if (s.state === 'notSynced') {
    const n = (s.hosts || []).filter((h) => h.state !== 'synced').length;
    return tp('modules.ddnsSomeOff', n);
  }
  return s.message || t('modules.ddnsStateUnknown');
}
