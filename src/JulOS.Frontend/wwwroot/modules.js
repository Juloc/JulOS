/* =====================================================================
 * Module Dienste, Caddy und DDNS: gemeinsame Daten und Texte fuer Desktop (desktop-modules.js, desktop-widgets.js) und Handy (mobile.js).
 * Keine Oberflaeche hier, nur Abrufe, Vorlagen und Beschriftungen (getestet in tests/frontend/modules.test.mjs).
 * Standardmaessig sind alle Module am Server aus; dann gibt es weder Fenster noch Karte.
 * ===================================================================== */

import { t, tk, tp, fmtNumber } from './i18n.js';

const MODES = ['off', 'read', 'control'];
const modeOf = (v) => (MODES.includes(v) ? v : 'off');

/** Welche Module der Server eingeschaltet hat (caddy, ddns, services: true/false) und mit welchem Schalter (modes: off, read, control). Bei jedem Fehler (auch 401) gilt: keins. */
export async function loadModules(api) {
  try {
    const m = await api('/api/modules');
    const modes = { caddy: modeOf(m.caddy), ddns: modeOf(m.ddns), services: modeOf(m.services) };
    return { caddy: modes.caddy !== 'off', ddns: modes.ddns !== 'off', services: modes.services !== 'off', modes };
  } catch { return { caddy: false, ddns: false, services: false, modes: { caddy: 'off', ddns: 'off', services: 'off' } }; }
}
export const NO_MODULES = { caddy: false, ddns: false, services: false, modes: { caddy: 'off', ddns: 'off', services: 'off' } };
export const anyModule = (m) => !!(m && (m.caddy || m.ddns || m.services));

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


/* ---------- Dienste: Symbole, Vorlagen, Darstellung ---------- */

/** Strich-Symbole (24 px) fuer die Kacheln; dieselben Namen wie ServiceRules.Icons am Server. */
const SERVICE_ICONS = {
  globe: '<circle cx="12" cy="12" r="10"/><path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>',
  server: '<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/>',
  box: '<path d="m21 8-9-5-9 5v8l9 5 9-5z"/><path d="m3.3 7.5 8.7 5 8.7-5M12 22V12.5"/>',
  cloud: '<path d="M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9z"/>',
  activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  monitor: '<rect x="2.5" y="3.5" width="19" height="13" rx="2"/><path d="M8 21h8M12 16.5V21"/>',
  folder: '<path d="M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.7-.9l-.8-1.2A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z"/>',
  shield: '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>',
  grid: '<path d="M3 3h7v7H3zM14 3h7v7h-7zM14 14h7v7h-7zM3 14h7v7H3z"/>',
  terminal: '<path d="m4 17 6-6-6-6M12 19h8"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="m10.8 12.2 8-8M16 7l3 3"/>',
  lock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
};
export const ICON_KEYS = Object.keys(SERVICE_ICONS);
export const ICON_LABEL = { globe: tk('widgets.iconGlobe'), server: tk('widgets.iconServer'), box: tk('widgets.iconBox'), cloud: tk('widgets.iconCloud'), activity: tk('widgets.iconActivity'), monitor: tk('widgets.iconMonitor'),
  folder: tk('widgets.iconFolder'), lock: tk('widgets.iconLock'), terminal: tk('widgets.iconTerminal'), shield: tk('widgets.iconShield'), grid: tk('widgets.iconGrid'), key: tk('widgets.iconKey') };
export const svcIcon = (name) => `<svg class="wd-i" viewBox="0 0 24 24" aria-hidden="true">${SERVICE_ICONS[name] || SERVICE_ICONS.globe}</svg>`;

/**
 * Fertige Vorlagen. Pfade gelten ab der Adresse des Dienstes. Jularr: /health/ready ist anonym; die Kennzahlen kommen aus den Verwaltungs-Endpunkten
 * (GET /api/v1/admin/activity/summary und /api/v1/admin/system/dashboard) und brauchen ein Geraete-Token als Kopfzeile "Authorization: Bearer ..." (nur lesen aus Jularrs Quelltext, nicht gegen eine laufende Jularr-Instanz geprueft).
 */
export const TEMPLATES = [
  { id: 'jularr', name: tk('widgets.tplJularr'), icon: 'box', health: '/health/ready', metrics: '/api/v1/admin/activity/summary', header: 'Authorization', interval: 30, fields: [
    { path: 'activeCount', label: tk('widgets.mJularrActive'), unit: '', format: 'plain' }, { path: 'bytesPerSecond', label: tk('widgets.mJularrSpeed'), unit: '', format: 'rate' },
    { path: 'importCount', label: tk('widgets.mJularrImport'), unit: '', format: 'plain' }, { path: 'errorCount', label: tk('widgets.mJularrErrors'), unit: '', format: 'plain' }] },
  { id: 'jularr-library', name: tk('widgets.tplJularrLibrary'), icon: 'box', health: '/health/ready', metrics: '/api/v1/admin/system/dashboard', header: 'Authorization', interval: 300, fields: [
    { path: 'works', label: tk('widgets.mJularrWorks'), unit: '', format: 'plain' }, { path: 'files', label: tk('widgets.mJularrFiles'), unit: '', format: 'plain' },
    { path: 'openRequests', label: tk('widgets.mJularrRequests'), unit: '', format: 'plain' }] },
  { id: 'caddyui', name: tk('widgets.tplCaddyUi'), icon: 'shield', health: '/health/ready', metrics: '', header: '', interval: 60, fields: [] },
  { id: 'http', name: tk('widgets.tplHttp'), icon: 'globe', health: '/', metrics: '', header: '', interval: 60, fields: [] },
];
export const templateById = (id) => TEMPLATES.find((x) => x.id === id) || null;

/** Der Ursprung einer Adresse ("http://host:8080"), sonst ''. */
export function originOf(url) { try { const u = new URL(String(url).trim()); return /^https?:$/.test(u.protocol) ? u.origin : ''; } catch { return ''; } }

/** Eintraege einer Vorlage fuer eine Adresse des Dienstes. */
export function fromTemplate(tpl, base) {
  const o = originOf(base);
  return {
    icon: tpl.icon, openUrl: o ? o + '/' : '', healthUrl: o && tpl.health ? o + tpl.health : '', metricsUrl: o && tpl.metrics ? o + tpl.metrics : '',
    headerName: tpl.header, intervalSeconds: tpl.interval, metrics: tpl.fields.map((f) => ({ path: f.path, label: t(f.label), unit: f.unit, format: f.format })), template: tpl.id,
  };
}

/** Zustand einer Kachel: ok (gruen), bad (rot), idle (grau). */
export const stateClass = (st) => (!st ? 'idle' : st.state === 'up' ? 'ok' : st.state === 'down' ? 'bad' : 'idle');
const STATE_TEXT = { up: tk('widgets.stateUp'), down: tk('widgets.stateDown'), unknown: tk('widgets.stateUnknown') };
export const stateText = (st) => t(STATE_TEXT[st && st.state] || STATE_TEXT.unknown);
const ERRORS = { timeout: tk('widgets.errTimeout'), refused: tk('widgets.errRefused'), status: tk('widgets.errStatus'), notAllowed: tk('widgets.errNotAllowed'), tooLarge: tk('widgets.errTooLarge'), invalid: tk('widgets.errInvalid') };
export const errorText = (st) => (st && st.error && ERRORS[st.error] ? t(ERRORS[st.error]) + (st.error === 'status' && st.httpStatus ? ' (' + st.httpStatus + ')' : '') : '');
/** Antwortzeit als Text ("12 ms"), leer ohne Wert. */
export const msText = (st) => (st && st.ms !== undefined && st.ms !== null ? t('widgets.ms', { n: st.ms }) : '');

export function fmtBytes(n) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = Math.abs(n), i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return (n < 0 ? '-' : '') + fmtNumber(v, i === 0 || v >= 100 ? 0 : 1) + ' ' + units[i];
}
/** Wert einer Kennzahl als Text: Zahlen je nach Darstellung (plain, bytes, rate, percent), Text unveraendert, fehlender Wert als Strich. */
export function fmtMetric(m) {
  if (!m) return '–';
  if (typeof m.number === 'number') {
    const n = m.number;
    if (m.format === 'bytes') return fmtBytes(n);
    if (m.format === 'rate') return fmtBytes(n) + '/s';
    if (m.format === 'percent') return fmtNumber(n, Number.isInteger(n) ? 0 : 1) + ' %';
    return fmtNumber(n, Number.isInteger(n) ? 0 : 1) + (m.unit ? ' ' + m.unit : '');
  }
  if (m.text !== undefined && m.text !== null) return m.text + (m.unit ? ' ' + m.unit : '');
  return '–';
}
