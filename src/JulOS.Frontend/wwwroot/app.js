import Guacamole from '/lib/guacamole-common.min.js';
import { StreamClient } from './stream.js';
import { SizeGate, LayoutLog } from './layoutlog.js';
import { Session, Sessions } from './sessions.js';

/* =====================================================================
 * JulOS – Fernsteuerung
 *  - RemoteSession: eine Sitzung (eigene Verbindung, Anzeige, Eingabe, Tastenleiste, Fensterleiste), siehe docs/SESSIONS.md
 *  - Verbindung (guacamole-common-js, RDP und SSH ueber guacd; oder der eigene Stream, stream.js)
 *  - dynamische Aufloesung (Fenstergroesse, Tastatur, Drehen, Zoom)
 *  - Touch: Tippen, Langdruecken, Ziehen, Zwei-Finger-Scrollen, Pinch-Zoom
 *  - Android-Tastatur ohne Autokorrektur + eigene, anpassbare Sondertasten
 * Gemeinsam fuer alle Sitzungen (nur hier): Farben, Einstellungen, Tasten-Editor, Dialoge, das unsichtbare Eingabefeld. Was "die" Sitzung
 * ist, sagt Sessions.active.
 * ===================================================================== */
const $ = (s, r = document) => r.querySelector(s);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const store = {
  get(k, d) { try { const v = localStorage.getItem('pd.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('pd.' + k, JSON.stringify(v)); } catch { /* ignorieren */ } },
};

/* ---------- Material-Icons (inline SVG, 24 px) ---------- */
const ICONS = {
  keyboard: 'M20 5H4c-1.1 0-1.99.9-1.99 2L2 17c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm-9 3h2v2h-2V8zm0 3h2v2h-2v-2zM8 8h2v2H8V8zm0 3h2v2H8v-2zm-1 2H5v-2h2v2zm0-3H5V8h2v2zm9 7H8v-2h8v2zm0-4h-2v-2h2v2zm0-3h-2V8h2v2zm3 3h-2v-2h2v2zm0-3h-2V8h2v2z',
  zoom_in: 'M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14zm2.5-4h-2v2H9v-2H7V9h2V7h1v2h2v1z',
  zoom_out: 'M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14zM7 9h5v1H7z',
  fullscreen: 'M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z',
  image: 'M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z',
  paste: 'M19 2h-4.18C14.4.84 13.3 0 12 0c-1.3 0-2.4.84-2.82 2H5c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm7 18H5V4h2v3h10V4h2v16z',
  more: 'M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z',
  expand_more: 'M16.59 8.59L12 13.17 7.41 8.59 6 10l6 6 6-6z',
  expand_less: 'M12 8l-6 6 1.41 1.41L12 10.83l4.59 4.58L18 14z',
  key: 'M12.65 10C11.83 7.67 9.61 6 7 6c-3.31 0-6 2.69-6 6s2.69 6 6 6c2.61 0 4.83-1.67 5.65-4H17v4h4v-4h2v-4H12.65zM7 14c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z',
  refresh: 'M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z',
  edit: 'M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04c.39-.39.39-1.02 0-1.41l-2.34-2.34c-.39-.39-1.02-.39-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z',
  restart: 'M12 5V2L8 6l4 4V7c3.31 0 6 2.69 6 6 0 2.97-2.17 5.43-5 5.91v2.02c3.95-.49 7-3.85 7-7.93 0-4.42-3.58-8-8-8zm-6 8c0-1.65.67-3.15 1.76-4.24L6.34 7.34C4.9 8.79 4 10.79 4 13c0 4.08 3.05 7.44 7 7.93v-2.02c-2.83-.48-5-2.94-5-5.91z',
  touch: 'M9 11.24V7.5C9 6.12 10.12 5 11.5 5S14 6.12 14 7.5v3.74c1.21-.81 2-2.18 2-3.74C16 5.01 13.99 3 11.5 3S7 5.01 7 7.5c0 1.56.79 2.93 2 3.74zm9.84 4.63l-4.54-2.26c-.17-.07-.35-.11-.54-.11H13v-6c0-.83-.67-1.5-1.5-1.5S10 6.67 10 7.5v10.74l-3.43-.72c-.08-.01-.15-.03-.24-.03-.31 0-.59.13-.79.33l-.79.8 4.94 4.94c.27.27.65.44 1.06.44h6.79c.75 0 1.33-.55 1.44-1.28l.75-5.27c.01-.07.02-.14.02-.2 0-.62-.38-1.16-.91-1.38z',
  auto_kb: 'M2.5 4v3h5v12h3V7h5V4h-13zm19 5h-9v3h3v7h3v-7h3V9z',
  palette: 'M12 3c-4.97 0-9 4.03-9 9s4.03 9 9 9c.83 0 1.5-.67 1.5-1.5 0-.39-.15-.74-.39-1.01-.23-.26-.38-.61-.38-.99 0-.83.67-1.5 1.5-1.5H16c2.76 0 5-2.24 5-5 0-4.42-4.03-8-9-8zm-5.5 9c-.83 0-1.5-.67-1.5-1.5S5.67 9 6.5 9 8 9.67 8 10.5 7.33 12 6.5 12zm3-4C8.67 8 8 7.33 8 6.5S8.67 5 9.5 5s1.5.67 1.5 1.5S10.33 8 9.5 8zm5 0c-.83 0-1.5-.67-1.5-1.5S13.67 5 14.5 5s1.5.67 1.5 1.5S15.33 8 14.5 8zm3 4c-.83 0-1.5-.67-1.5-1.5S16.67 9 17.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z',
  logout: 'M17 7l-1.41 1.41L18.17 11H8v2h10.17l-2.58 2.58L17 17l5-5zM4 5h8V3H4c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h8v-2H4V5z',
  ret: 'M19 7v4H5.83l3.58-3.59L8 6l-6 6 6 6 1.41-1.41L5.83 13H21V7z',
  download: 'M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z',
  chevron_right: 'M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z',
  apps: 'M4 8h4V4H4v4zm6 12h4v-4h-4v4zm-6 0h4v-4H4v4zm0-6h4v-4H4v4zm6 0h4v-4h-4v4zm6-10v4h4V4h-4zm-6 4h4V4h-4v4zm6 6h4v-4h-4v4zm0 6h4v-4h-4v4z',
  desktop: 'M21 2H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h7v2H8v2h8v-2h-2v-2h7c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H3V4h18v12z',
  add: 'M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z',
  terminal: 'M20 4H4c-1.11 0-2 .9-2 2v12c0 1.1.89 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 14H4V8h16v10zm-2-1h-6v-2h6v2zM7.5 17l-1.41-1.41L8.67 13l-2.59-2.59L7.5 9l4 4-4 4z',
  add_photo: 'M19 7v2.99s-1.99.01-2 0V7h-3s.01-1.99 0-2h3V2h2v3h3v2h-3zm-3 4V8h-3V5H5c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2v-8h-3zM5 19l3-4 2 3 3-4 4 5H5z',
  close: 'M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
  minimize: 'M6 19h12v2H6z',
  window: 'M20 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 14H4V8h16v10z',
  bolt: 'M11 21h-1l1-7H7.5c-.58 0-.57-.32-.38-.66.19-.34.05-.08.07-.12C8.48 10.94 10.42 7.54 13 3h1l-1 7h3.5c.49 0 .56.33.47.51l-.07.15C12.96 17.55 11 21 11 21z',
  layers: 'M11.99 18.54l-7.37-5.73L3 14.07l9 7 9-7-1.63-1.27-7.38 5.74zM12 16l7.36-5.73L21 9l-9-7-9 7 1.63 1.27L12 16z',
  home: 'M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z',
  backspace: 'M22 3H7c-.69 0-1.23.35-1.59.88L0 12l5.41 8.11c.36.53.9.89 1.59.89h15c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-3 12.59L17.59 17 14 13.41 10.41 17 9 15.59 12.59 12 9 8.41 10.41 7 14 10.59 17.59 7 19 8.41 15.41 12 19 15.59z',
};
export const ico = (name) => `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${ICONS[name] || ''}"/></svg>`;
function renderIcons(root = document) { root.querySelectorAll('[data-ic]').forEach((n) => { n.innerHTML = ico(n.dataset.ic); }); }

/* ---------- Gemeinsamer Zustand: Einstellungen, die fuer alle Sitzungen gelten (je Sitzung steht alles in RemoteSession) ---------- */
const el = {
  kb: $('#kb'), kbInput: $('#kb'), kbRich: $('#kbRich'),
  imgSeedBtn: $('#imgSeedBtn'), imgSeedInput: $('#imgSeedInput'),
  clipDlg: $('#clipDlg'), sendText: $('#sendText'), remoteClip: $('#remoteClip'), typeBtn: $('#typeBtn'), pasteBtn: $('#pasteBtn'), copyBtn: $('#copyBtn'),
  keyDlg: $('#keyDlg'), keyDlgTitle: $('#keyDlgTitle'), keyLabel: $('#keyLabel'), keyType: $('#keyType'), keyValue: $('#keyValue'),
  keyValueLabel: $('#keyValueLabel'), keyHint: $('#keyHint'), keyLeft: $('#keyLeft'), keyRight: $('#keyRight'),
  keySave: $('#keySave'), keyDelete: $('#keyDelete'),
  photoInput: $('#photoInput'),
  themeDlg: $('#themeDlg'), swatches: $('#swatches'), seedInput: $('#seedInput'), schemeSel: $('#schemeSel'),
  pwDlg: $('#pwDlg'), pwUser: $('#pwUser'), pwPass: $('#pwPass'), pwTypePass: $('#pwTypePass'), pwTypeBoth: $('#pwTypeBoth'),
};

const S = {
  kbMode: store.get('kbMode2', 'rich'),       // 'rich' (Bilder aus der Tastatur) | 'normal' (mit Vorschlaegen) | 'direct' (ohne Vorschlaege)
  touchMode: store.get('touchMode', 'direct'),// 'direct' (Tippen = Klick an der Stelle) | 'pad' (Touchpad)
  sizeHoldUntil: 0,                           // solange die Bildschirmtastatur faehrt, geht keine neue Groesse an die VMs (gilt fuer alle Sitzungen)
  expanded: store.get('expanded', false), group: store.get('group', 'keys'), editing: false,
  autoKb: store.get('autoKb', true),   // Tastatur oeffnet sich beim Tippen auf ein Textfeld (Erkennung am Mauszeiger)
  debug: store.get('debug', true),     // sendet nur Messwerte (Groessen, Ereignisarten), nie getippten Text
  autoPaste: store.get('autoPaste', true),   // Foto nach dem Hochladen sofort mit Strg+V einfuegen
  seed: store.get('seed', '#9a67ea'),         // Grundfarbe fuer das Material-You-Design ('system' oder #rrggbb), Standard Violett wie die Handy-Oberflaeche
  scheme: store.get('scheme', 'auto'),       // 'auto' | 'light' | 'dark'
};
let lastStageSize = { w: window.innerWidth, h: window.innerHeight };   // zuletzt gemessene sichtbare Bildflaeche (fuer Sitzungen im Hintergrund)

/** Alle Fernsteuer-Sitzungen und die aktive (andere Sitzungsarten, etwa das native Terminal, gehoeren nicht hierher). */
const remotes = () => Sessions.list().filter((s) => s instanceof RemoteSession);
const cur = () => (Sessions.active instanceof RemoteSession ? Sessions.active : null);

/* ---------- Toast / kleine Helfer ---------- */
let gToastEl = null, gToastTimer = 0;
function toast(msg, ms = 1400) {
  const s = cur();
  if (s) return s.toast(msg, ms);
  if (!gToastEl) { gToastEl = document.createElement('div'); gToastEl.className = 'sv-toast'; gToastEl.style.cssText = 'position:fixed;z-index:200;top:calc(12px + env(safe-area-inset-top))'; document.body.appendChild(gToastEl); }
  gToastEl.textContent = msg; gToastEl.classList.add('show');
  clearTimeout(gToastTimer); gToastTimer = setTimeout(() => gToastEl.classList.remove('show'), ms);
}

/* ---------- Diagnose: sendet nur Messwerte an das Server-Log (keine Eingaben) ---------- */
const dbgQueue = [];
let dbgTimer = null;
function dbg(evt, data = {}) {
  if (!S.debug || !Sessions.count) return;     // Messwerte nur waehrend einer Verbindung (davor gibt es noch keine Sitzung)
  if (dbgQueue.length >= 400) dbgQueue.shift();
  dbgQueue.push({ t: Math.round(performance.now()), evt, ...data });
  if (!dbgTimer) dbgTimer = setTimeout(function flush() {
    dbgTimer = null;
    const events = dbgQueue.splice(0, 40);
    fetch('/api/log', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-JulOS': '1' }, body: JSON.stringify({ events }), keepalive: true }).catch(() => {});
    if (dbgQueue.length) dbgTimer = setTimeout(flush, 2500);      // mehr als 40 aufgelaufen: der Rest geht im naechsten Paket mit
  }, 2500);
}

/* ---------- Sichtbarer Bereich (Tastatur, Vollbild) und Tastatur-Erkennung (gilt fuer das ganze Geraet) ---------- */
const vv = window.visualViewport;
let baseH = vv ? vv.height : window.innerHeight;
let lastW = vv ? vv.width : window.innerWidth;
let kbOpen = false;
// Messwerte und Schleifen-Erkennung (layoutlog.js, eine je Sitzung). Die Anzeige auf dem Bild und die dichten "lay"-Eintraege gibt es nur mit ?debug in der Adresse
// (der Wert bleibt fuer diesen Tab gemerkt, falls die Seite neu geladen wird); eine erkannte Schleife kommt immer ins Log.
const LAYDEBUG = (() => {
  try { if (/[?&]debug\b/.test(location.search)) sessionStorage.setItem('pd.laydebug', '1'); return sessionStorage.getItem('pd.laydebug') === '1'; } catch { return /[?&]debug\b/.test(location.search); }
})();
const kbFocused = () => document.activeElement === el.kbInput || document.activeElement === el.kbRich;
// Waehrend die Tastatur ein- oder ausfaehrt, wird keine neue Remote-Groesse gesendet (die Ansicht ruckelt sonst durch mehrere Zwischengroessen)
const KB_HOLD_MS = 600;
function holdSize(ms = KB_HOLD_MS) { S.sizeHoldUntil = Math.max(S.sizeHoldUntil || 0, Date.now() + ms); }
let vpFrame = 0;
function layoutViewport(why = 'vv') {
  // Android meldet eine Aenderung oft dreifach (visualViewport resize und scroll, Fenster resize): in einem Bild zusammenfassen
  if (vpFrame) return;
  vpFrame = requestAnimationFrame(() => { vpFrame = 0; applyViewport(why); });
}
function applyViewport(why) {
  const h = vv ? vv.height : window.innerHeight;
  const w = vv ? vv.width : window.innerWidth;
  const root = document.documentElement;
  if (Math.abs(w - lastW) > 40) { baseH = h; lastW = w; }       // Drehen: neue Basis
  if (h > baseH) baseH = h;
  root.style.setProperty('--vvh', h + 'px');
  root.style.setProperty('--vvt', (vv ? vv.offsetTop : 0) + 'px');
  const open = h < baseH - 140;
  if (open !== kbOpen) {
    kbOpen = open; root.classList.toggle('kb-open', open);
    holdSize();
    // Tastatur per Zurueck-Geste geschlossen, Feld hat aber noch den Fokus: Fokus loesen, damit der Zustand stimmt
    if (!open && kbFocused()) el.kb.blur();
    setActOnAll('keyboard', open);
    dbg('kbvisible', { open, vvh: Math.round(h), base: Math.round(baseH), inner: window.innerHeight });
  }
  for (const s of remotes()) s.lay.layout(why);
}
if (vv) { vv.addEventListener('resize', () => layoutViewport('vv')); vv.addEventListener('scroll', () => layoutViewport('vvs')); }
window.addEventListener('resize', () => layoutViewport('win'));
window.addEventListener('orientationchange', () => setTimeout(() => layoutViewport('rot'), 300));
applyViewport('init');

/* =====================================================================
 *  Tasten: Namen -> X11-Keysyms, Umschalter (Strg/Alt/Shift/Win)
 * ===================================================================== */
const KS = {
  esc: 0xff1b, escape: 0xff1b, tab: 0xff09, enter: 0xff0d, return: 0xff0d, backspace: 0xff08, bs: 0xff08,
  delete: 0xffff, del: 0xffff, entf: 0xffff, home: 0xff50, pos1: 0xff50, end: 0xff57, ende: 0xff57,
  left: 0xff51, up: 0xff52, right: 0xff53, down: 0xff54, pageup: 0xff55, pgup: 0xff55, bildauf: 0xff55,
  pagedown: 0xff56, pgdn: 0xff56, bildab: 0xff56, insert: 0xff63, einfg: 0xff63, space: 0x20, menu: 0xff67,
  ctrl: 0xffe3, strg: 0xffe3, control: 0xffe3, shift: 0xffe1, alt: 0xffe9, altgr: 0xfe03,
  super: 0xffeb, win: 0xffeb, meta: 0xffeb, print: 0xff61, printscreen: 0xff61, capslock: 0xffe5,
  plus: 0x2b, minus: 0x2d,
};
for (let i = 1; i <= 12; i++) KS['f' + i] = 0xffbd + i;
const MODSYM = { ctrl: 0xffe3, alt: 0xffe9, shift: 0xffe1, super: 0xffeb };
const MODNAME = { ctrl: 'ctrl', strg: 'ctrl', control: 'ctrl', alt: 'alt', shift: 'shift', super: 'super', win: 'super', meta: 'super' };

const charKeysym = (ch) => {
  const cp = ch.codePointAt(0);
  if (cp === 10 || cp === 13) return KS.enter;
  if (cp === 9) return KS.tab;
  return cp < 0x100 ? cp : 0x01000000 + cp;
};

function parseChord(str) {
  const parts = String(str).split('+').map((s) => s.trim().toLowerCase()).filter(Boolean);
  if (!parts.length) throw new Error('Keine Tasten angegeben');
  return parts.map((p) => {
    if (KS[p] !== undefined) return KS[p];
    if ([...p].length === 1) return charKeysym(p);
    throw new Error('Unbekannte Taste: ' + p);
  });
}

const ITEM = (label, type, value) => ({ label, type, value });
const ACT = (icon, action) => ITEM(':' + icon, 'action', action);
const DEFAULT_ROWS = [
  // drei Gruppen (Akkordeon): nur Symbol und Pfeil als Kopf, darunter die Tasten der geoeffneten Gruppe
  { id: 'keys', title: 'Tasten', icon: 'keyboard', items: [
    ITEM('Alt', 'mod', 'alt'), ITEM('Shift', 'mod', 'shift'),
    ITEM('←', 'chord', 'left'), ITEM('↑', 'chord', 'up'), ITEM('↓', 'chord', 'down'), ITEM('→', 'chord', 'right'),
    ITEM(':ret', 'chord', 'enter'), ITEM(':backspace', 'chord', 'backspace'), ITEM('Entf', 'chord', 'delete'),
    ITEM('Pos1', 'chord', 'home'), ITEM('Ende', 'chord', 'end'), ITEM('Bild↑', 'chord', 'pageup'), ITEM('Bild↓', 'chord', 'pagedown'), ITEM('⇧Tab', 'chord', 'shift+tab') ] },
  { id: 'combos', title: 'Combos', icon: 'bolt', items: [
    ITEM('Strg+C', 'chord', 'ctrl+c'), ITEM('Strg+V', 'chord', 'ctrl+v'), ITEM('Strg+X', 'chord', 'ctrl+x'), ITEM('Strg+A', 'chord', 'ctrl+a'),
    ITEM('Strg+Z', 'chord', 'ctrl+z'), ITEM('Strg+F', 'chord', 'ctrl+f'), ITEM('Alt+Tab', 'chord', 'alt+tab'), ITEM('Strg+⇧+T', 'chord', 'ctrl+shift+t') ] },
  { id: 'funcs', title: 'Funktionen', icon: 'apps', items: [
    ITEM('Bild einfügen', 'action', 'pasteimg'), ITEM('Foto wählen', 'action', 'photo'), ITEM('Text senden', 'action', 'clip'), ITEM('Passwort', 'action', 'password') ] },
  // die Leiste: immer sichtbar, eine Zeile, ganz unten
  { id: 'bar', title: 'Leiste', fixed: true, items: [
    ITEM('Esc', 'chord', 'esc'), ITEM('Strg', 'mod', 'ctrl'), ITEM('Tab', 'chord', 'tab'),
    ACT('zoom_out', 'zoomout'), ACT('zoom_in', 'zoomin'), ACT('fullscreen', 'fullscreen'), ACT('more', 'menu'),
    ACT('expand_less', 'expand'), ACT('keyboard', 'keyboard') ] },
];
const clone = (o) => JSON.parse(JSON.stringify(o));
function loadRows() {
  const saved = store.get('rows4', null);
  if (Array.isArray(saved) && saved.some((r) => r.fixed) && ['keys', 'combos', 'funcs'].every((id) => saved.some((r) => r.id === id))) {
    for (const r of saved) { const d = DEFAULT_ROWS.find((x) => x.id === r.id); if (d && !r.icon) r.icon = d.icon; }
    return saved;
  }
  // Aus der alten Aufteilung (Mehr/Tasten) nur die eigene untere Leiste mitnehmen
  const fresh = clone(DEFAULT_ROWS);
  const old = store.get('rows3', null);
  const oldBar = Array.isArray(old) && old.find((r) => r.fixed);
  if (oldBar) fresh[fresh.findIndex((r) => r.fixed)] = oldBar;
  return fresh;
}
let rows = loadRows();
let suppressClick = false;
let editRow = 0, editIndex = -1;      // editIndex -1 = neue Taste
const saveRows = () => store.set('rows4', rows);

/* Tasten per langem Druecken ziehen: innerhalb der Zeile und in andere Zeilen */
function enableDrag(btn) {
  let timer = null, dragging = false, sx = 0, sy = 0;
  btn.addEventListener('pointerdown', (e) => {
    sx = e.clientX; sy = e.clientY;
    timer = setTimeout(() => {
      dragging = true; btn.classList.add('dragging');
      try { btn.setPointerCapture(e.pointerId); } catch { /* egal */ }
      btn.style.pointerEvents = 'none';
    }, 280);
  });
  btn.addEventListener('pointermove', (e) => {
    if (!dragging) { if (timer && Math.hypot(e.clientX - sx, e.clientY - sy) > 10) { clearTimeout(timer); timer = null; } return; }
    btn.style.transform = `translate(${e.clientX - sx}px, ${e.clientY - sy}px) scale(1.06)`;
  });
  const end = (e) => {
    clearTimeout(timer); timer = null;
    if (!dragging) return;
    dragging = false;
    const under = document.elementFromPoint(e.clientX, e.clientY);
    const target = under && under.closest('.k[data-i]');
    const rowEl = under && under.closest('.rk[data-r]');
    btn.style.pointerEvents = ''; btn.style.transform = ''; btn.classList.remove('dragging');
    suppressClick = true; setTimeout(() => { suppressClick = false; }, 350);
    const fr = Number(btn.dataset.r), fi = Number(btn.dataset.i);
    let tr = null, ti = null;
    if (target) { tr = Number(target.dataset.r); ti = Number(target.dataset.i); }
    else if (rowEl) { tr = Number(rowEl.dataset.r); ti = rows[tr].items.length; }
    if (tr !== null && !Number.isNaN(tr) && !(tr === fr && ti === fi)) {
      const [m] = rows[fr].items.splice(fi, 1);
      if (tr === fr && ti > fi) ti -= 0;           // gleiche Zeile: Ziel bleibt der angetippte Platz
      rows[tr].items.splice(Math.min(ti, rows[tr].items.length), 0, m);
      saveRows();
    }
    renderAllRows();
  };
  btn.addEventListener('pointerup', end);
  btn.addEventListener('pointercancel', end);
}
/* ----- Editor fuer eine Taste ----- */
function updateKeyEditorHints() {
  const t = el.keyType.value;
  if (t === 'chord') { el.keyValueLabel.textContent = 'Tasten'; el.keyHint.textContent = 'Mit + verbinden, z. B. ctrl+shift+t, alt+f4, win+d, f5, pageup. Namen: ctrl/strg, alt, shift, win, esc, tab, enter, backspace, delete, home, end, pageup, pagedown, left/up/right/down, space, f1–f12 oder ein Zeichen.'; }
  else if (t === 'mod') { el.keyValueLabel.textContent = 'Umschalter'; el.keyHint.textContent = 'ctrl, alt, shift oder win. Erster Tipp: gilt für die nächste Taste (Rahmen). Zweiter Tipp: bleibt gedrückt (gefüllt). Dritter Tipp: aus.'; }
  else if (t === 'action') { el.keyValueLabel.textContent = 'Aktion'; el.keyHint.textContent = 'Mögliche Aktionen: ' + Object.keys(ACTIONS).join(', ') + '. Ein Symbol als Beschriftung geht mit Doppelpunkt, zum Beispiel :image, :keyboard, :zoom_in.'; }
  else { el.keyValueLabel.textContent = 'Text'; el.keyHint.textContent = 'Dieser Text wird bei Tipp wie von Hand eingetippt (zum Beispiel ein Befehl oder /clear).'; }
}
function openKeyEditor(r, i) {
  editRow = r; editIndex = i;
  const def = i >= 0 ? rows[r].items[i] : { label: '', type: 'chord', value: '' };
  el.keyDlgTitle.textContent = i >= 0 ? 'Taste bearbeiten' : `Neue Taste in „${rows[r].title}“`;
  el.keyLabel.value = def.label; el.keyType.value = def.type; el.keyValue.value = def.value;
  el.keyDelete.hidden = i < 0; el.keyLeft.hidden = i < 0; el.keyRight.hidden = i < 0;
  updateKeyEditorHints();
  el.keyDlg.showModal();
}
el.keyType.addEventListener('change', updateKeyEditorHints);
el.keySave.addEventListener('click', () => {
  const def = { label: el.keyLabel.value.trim() || el.keyValue.value.trim().slice(0, 10) || '?', type: el.keyType.value, value: el.keyValue.value.trim() };
  if (!def.value) { toast('Bitte Tasten oder Text angeben'); return; }
  try {
    if (def.type === 'chord') parseChord(def.value);
    if (def.type === 'mod' && !MODNAME[def.value.toLowerCase()]) throw new Error('Umschalter: ctrl, alt, shift oder win');
    if (def.type === 'action' && !ACTIONS[def.value.toLowerCase()]) throw new Error('Aktion: ' + Object.keys(ACTIONS).join(', '));
  } catch (e) { toast(e.message, 2600); return; }
  if (editIndex >= 0) rows[editRow].items[editIndex] = def; else rows[editRow].items.push(def);
  saveRows(); el.keyDlg.close(); renderAllRows();
});
el.keyDelete.addEventListener('click', () => { if (editIndex >= 0) { rows[editRow].items.splice(editIndex, 1); saveRows(); el.keyDlg.close(); renderAllRows(); } });
const moveKey = (d) => {
  const items = rows[editRow].items, j = editIndex + d;
  if (editIndex < 0 || j < 0 || j >= items.length) return;
  const [m] = items.splice(editIndex, 1); items.splice(j, 0, m); editIndex = j; saveRows(); renderAllRows(); toast('Verschoben');
};
el.keyLeft.addEventListener('click', () => moveKey(-1));
el.keyRight.addEventListener('click', () => moveKey(1));

/* ----- Gemeinsames fuer alle Tastenleisten ----- */
/** Tasten neu zeichnen, wenn sich etwas an der gemeinsamen Belegung aendert (jede Sitzung hat ihre eigene Leiste). */
function renderAllRows() { for (const s of remotes()) s.renderRows(); }
function setActOnAll(name, on) { for (const s of remotes()) s.setActOn(name, on); }
function toggleExpanded() {
  if (S.editing) return;
  S.expanded = !S.expanded; store.set('expanded', S.expanded); renderAllRows();
}
function toggleFullscreen() {
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  else document.documentElement.requestFullscreen({ navigationUI: 'hide' }).catch(() => toast('Vollbild nicht möglich'));
}
function openClipDlg() { el.remoteClip.value = (cur() && cur().clipText) || ''; el.clipDlg.showModal(); }
/* Aktionen, die eine Taste in der Leiste ausloesen kann (frei zuordenbar im Tasten-Editor); sie bekommen die Sitzung, in deren Leiste getippt wurde */
const ACTIONS = {
  keyboard: () => { if (kbOpen) el.kb.blur(); else { el.kb.blur(); el.kb.focus(); } },
  zoomin: (s) => { s.setK(s.k / 1.12); s.rememberProfile(); },
  zoomout: (s) => { s.setK(s.k * 1.12); s.rememberProfile(); },
  fullscreen: toggleFullscreen,
  menu: (s) => s.toggleMenu(),
  expand: toggleExpanded,
  photo: () => openPhotoDlg(),
  pasteimg: () => pasteImageFromClipboard(),
  clip: () => openClipDlg(),
  password: () => openPwDialog(),
  sessions: () => Sessions.requestSwitcher(),
  home: () => Sessions.requestHome(),
};

/* =====================================================================
 *  Programm starten (Fensterleiste): ein Dialog fuer alle Sitzungen, er gehoert der Sitzung, die ihn geoeffnet hat
 * ===================================================================== */
let appsTarget = null;
async function openApps(session) {
  appsTarget = session;
  $('#appsDlg').showModal(); $('#appsSearch').value = '';
  if (!session.W.apps) { try { session.W.apps = (await agent('/apps')).apps || []; } catch (e) { session.toast('Programme: ' + e.message); session.W.apps = []; } }
  renderApps('');
}
function renderApps(q) {
  const s = appsTarget; if (!s) return;
  const list = $('#appsList'); list.replaceChildren();
  const f = (s.W.apps || []).filter((a) => a.name.toLowerCase().includes(q.toLowerCase())).slice(0, 80);
  for (const a of f) {
    const d = document.createElement('div'); d.className = 'appitem'; d.setAttribute('role', 'button');
    d.innerHTML = (a.icon ? `<img alt="" src="data:image/png;base64,${a.icon}">` : '<span style="width:32px"></span>') + `<span></span>`; d.lastChild.textContent = a.name;
    d.addEventListener('click', async () => {
      $('#appsDlg').close();
      try { await agent('/apps/launch', 'POST', { id: a.id }); s.toast('Starte ' + a.name + ' …', 2200); setTimeout(() => s.pollWindows(), 1500); } catch (e) { s.toast('Start: ' + e.message); }
    });
    list.appendChild(d);
  }
  if (!f.length) list.textContent = 'Nichts gefunden.';
}
$('#appsSearch').addEventListener('input', (e) => renderApps(e.target.value));

/* Mauszeiger der VM auswerten: ist es der Text-Zeiger (I-Balken)? Zeigerbilder sind oft 32x32 mit viel Rand,
   deshalb zaehlt nur das Rechteck um die sichtbaren Pixel. I-Balken: schmal und hoch, Hotspot in der Mitte. Pfeil/Hand: Hotspot an der Ecke. */
let cursorLogN = 0;
function cursorInfo(canvas, hx, hy) {
  const w = canvas.width, h = canvas.height;
  let x0 = 0, y0 = 0, bw = w, bh = h;
  try {
    const d = canvas.getContext('2d', { willReadFrequently: true }).getImageData(0, 0, w, h).data;
    let minX = w, minY = h, maxX = -1, maxY = -1;
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (d[(y * w + x) * 4 + 3] > 40) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
    if (maxX >= 0) { x0 = minX; y0 = minY; bw = maxX - minX + 1; bh = maxY - minY + 1; }
  } catch { /* Pixel nicht lesbar: ganzes Bild nehmen */ }
  const rx = (hx - x0) / Math.max(1, bw), ry = (hy - y0) / Math.max(1, bh);
  const ibeam = bw >= 2 && bh >= 10 && bw / bh <= 0.65 && rx >= 0.2 && rx <= 0.8 && ry >= 0.2 && ry <= 0.8;
  return { w, h, bw, bh, hx, hy, ibeam, sig: `${w}x${h}:${bw}x${bh}@${hx},${hy}` };
}
let tapLogN = 0;
const dist2 = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
const mid2 = (a, b) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });

/* =====================================================================
 *  RemoteSession: eine Fernsteuer-Sitzung (Vertrag: docs/SESSIONS.md, Basisklasse in sessions.js)
 * ===================================================================== */
const profiles = store.get('profiles', {});                   // Anzeigegroesse je Programm (Klasse), nur im Programm-Modus
const defaultProfile = (cls) => (/claude/i.test(cls || '') ? 0.56 : null);   // Claude schmal: dann zeigt die App Burger-Menue statt Seitenleiste
const PROFILE = { stablePolls: 2, minGap: 8000 };
/** Verstaendliche Texte zu den Statuscodes, die guacd (oder der Stream-Server) mit einem Fehler schickt (Guacamole-Protokoll). */
const ERROR_TEXT = {
  513: 'Der Server ist gerade ausgelastet.', 514: 'Das Ziel antwortet nicht (Zeitüberschreitung).', 515: 'Die Verbindung zum Ziel ist fehlgeschlagen.',
  519: 'Das Ziel wurde nicht gefunden.', 520: 'Das Ziel ist nicht erreichbar.', 769: 'Anmeldung am Ziel abgelehnt. Bitte Benutzername und Passwort prüfen.',
  777: 'Die Sitzung wurde in einem anderen Fenster oder auf einem anderen Gerät übernommen.',
  797: null,        // zu viele Sitzungen: der Server schickt den Text selbst ("Es sind schon 8 Sitzungen offen ...")
};

async function agent(path, method = 'GET', body = null) {
  const r = await fetch('/api/agent' + path, { method, headers: { 'Content-Type': 'application/json', 'X-JulOS': '1' }, body: body ? JSON.stringify(body) : undefined });
  if (!r.ok) throw new Error('Agent ' + r.status);
  return r.json();
}

export class RemoteSession extends Session {
  constructor(conn, opts = {}) {
    super({ id: opts.sid, conn, title: opts.title });
    this.password = opts.password || null;         // nur im Arbeitsspeicher dieser Seite
    this.client = null; this.tunnel = null; this.display = null; this.mouse = null;
    this.connected = false; this.everConnected = false; this.intentional = false; this.fatal = false; this.disposed = false;
    this.firstLogin = true; this.forceGuac = false; this.streamWorked = false; this.engine = 'guac';
    this.gen = 0;                                  // zaehlt jeden Verbindungsversuch: spaete Antworten eines alten Versuchs werden verworfen
    this.k = clamp(store.get('k.' + conn.id, store.get('k', 0.8)), 0.35, 1.8);   // Remote-Pixel pro Handy-Pixel (kleiner = Inhalt groesser), je Verbindung gemerkt
    this.lastSent = { w: 0, h: 0 }; this.gate = new SizeGate();
    this.retry = 0; this.retryTimer = null; this.resizeTimer = null; this.connectTimer = null; this.toastTimer = null; this.zoomTimer = null;
    this.connectedAt = 0; this.connectStarted = 0; this.frameSeen = false;
    this.cursor = { ibeam: false };
    this.mods = { ctrl: 'off', alt: 'off', shift: 'off', super: 'off' };
    this.keyChain = Promise.resolve();
    this.actOn = {};
    this.clipText = '';
    this.counts = { instr: 0 };                    // Zaehler fuer Tests (Anweisungen von guacd)
    this.stageW = 0; this.stageH = 0; this.lastStage = null;
    this.W = { available: false, list: [], mode: 'desktop', timer: null, lastCls: null, candCls: null, candN: 0, profileAt: 0, apps: null };
    this.winSig = '';
    this.G = { pts: new Map(), mode: 'idle', timer: null, sx: 0, sy: 0, st: 0, lx: 0, ly: 0, d0: 0, k0: 0, mid0: null, midLast: null,
      decided: null, scrollAcc: 0, pressed: false, px: 0, py: 0, newK: 0, moved: false };
    this.buildDom();
    this.wireTouch();
    this.wireUi();
    this.setState('disconnected', 'Noch nicht verbunden');
  }

  /* ---------- Aufbau der Ansicht aus der Vorlage ---------- */
  buildDom() {
    const root = $('#svTemplate').content.firstElementChild.cloneNode(true);
    const q = (c) => root.querySelector('.' + c);
    this.element = root;
    this.el = {
      topbar: q('sv-topbar'), rowWin: q('sv-rowWin'), winSheet: q('sv-winSheet'), stage: q('sv-stage'), display: q('sv-display'), touch: q('sv-touch'),
      toast: q('sv-toast'), zoomPop: q('sv-zoom'), overlay: q('sv-overlay'), overlayMsg: q('sv-overlayMsg'), spinner: q('sv-spinner'),
      reconnectBtn: q('sv-reconnect'), closeBtn: q('sv-close'), scrim: q('sv-scrim'), dock: q('sv-dock'), menu: q('sv-menu'),
      rowsTop: q('sv-rowsTop'), rowsBottom: q('sv-rowsBottom'),
    };
    renderIcons(root);
    this.lay = new LayoutLog({ emit: (evt, d) => this.dbg(evt, d), parts: { app: $('#app'), stage: this.el.stage, topbar: this.el.topbar, dock: this.el.dock }, state: () => ({ kbOpen }), visible: LAYDEBUG });
    this.ro = new ResizeObserver(() => this.onStageResize());
    this.ro.observe(this.el.stage);
    this.updateMenuLabels();
    this.renderRows();
  }

  wireUi() {
    const e = this.el;
    // Tippen auf Dock-Knoepfe darf den Fokus (und damit die Tastatur) nicht wegnehmen
    e.dock.addEventListener('mousedown', (ev) => { if (ev.target.closest('.hb, .k, .acch, .chip, button')) ev.preventDefault(); });
    e.winSheet.addEventListener('mousedown', (ev) => ev.preventDefault());
    // weder Fenster noch Hintergrund duerfen den Fokus nehmen: die Tastatur bleibt offen
    e.menu.addEventListener('mousedown', (ev) => ev.preventDefault());
    e.scrim.addEventListener('mousedown', (ev) => ev.preventDefault());
    e.scrim.addEventListener('click', () => { this.closeMenu(); this.closeWinSheet(); });
    e.menu.addEventListener('click', (ev) => this.onMenuClick(ev));
    e.reconnectBtn.addEventListener('click', () => { this.retry = 0; if (this.hasCredentials()) this.start(); else this.requestClose(); });
    e.closeBtn.addEventListener('click', () => this.requestClose());
    // Desktop-Browser (Maus, echte Tastatur): ein Klick ins Bild holt die Sitzung nach vorn und gibt ihr die Tasten
    e.touch.addEventListener('mousedown', () => { if (cur() !== this) Sessions.activate(this.id); focusKbForHardware(); });
  }

  dbg(evt, data = {}) { dbg(evt, data); }
  q(sel) { return this.element.querySelector(sel); }
  toast(msg, ms = 1400) {
    this.el.toast.textContent = msg; this.el.toast.classList.add('show');
    clearTimeout(this.toastTimer); this.toastTimer = setTimeout(() => this.el.toast.classList.remove('show'), ms);
  }
  hasCredentials() { return !!(this.password || this.conn.stored); }   // Passwort im Speicher des Browsers oder auf dem Server gespeichert

  /* ---------- Vertrag: einhaengen, sichtbar, Fokus, aufraeumen ---------- */
  mount(container) { container.appendChild(this.element); }
  unmount() { this.element.remove(); }
  resize() { this.scheduleSize(60); this.fitDisplay(); }
  focus() { focusKbForHardware(); }
  blur() { if (cur() === this && kbFocused()) el.kb.blur(); }
  /** Im Hintergrund (nicht sichtbar): Ansicht aus, keine Groessenmeldungen, Fensterleiste ruht, der Stream pausiert (Server kodiert nicht, Browser dekodiert nicht). */
  setVisible(on) {
    on = !!on;
    this.visible = on; this.element.hidden = !on;
    this.applyPause();
    if (on) { this.fitDisplay(); this.scheduleSize(80); if (this.W.available) this.pollWindows(); this.refreshAll(); }
    else { this.closeMenu(); this.closeWinSheet(); }
  }
  applyPause() { if (this.client instanceof StreamClient) this.client.setPaused(!this.visible || document.hidden); }
  refreshAll() { this.updateMenuLabels(); this.renderRows(); }
  dispose() {
    this.disposed = true; this.intentional = true;
    this.teardown();
    this.ro.disconnect(); this.lay.dispose();
    clearTimeout(this.toastTimer); clearTimeout(this.zoomTimer);
    this.password = null;
    this.unmount();
  }
  disconnect() { this.intentional = true; this.teardown(); this.setState('disconnected', 'Getrennt'); }
  /** Seite war im Hintergrund oder gesperrt (Handy): die Verbindung ist danach meist tot, das Bild aber noch da. Beim Zurueckkommen neu verbinden. */
  onPageVisible(away) {
    this.applyPause();
    if (!this.hasCredentials() || this.intentional || !this.everConnected || this.fatal) return;
    if (this.client && !this.connected && Date.now() - this.connectStarted < 10000) return;   // es wird gerade verbunden: nicht noch einmal von vorn
    if (!this.connected || away > 4000) { this.retry = 0; this.start(); }
  }

  /* ---------- Ueberlagerung (Verbinden, Fehler) ---------- */
  showOverlay(msg, canRetry = true) {
    const e = this.el; e.overlayMsg.textContent = msg; e.spinner.hidden = true; e.overlay.hidden = false; e.reconnectBtn.hidden = !canRetry; e.closeBtn.hidden = false;
  }
  showBusy(msg) { // Fortschritt mit Animation, ohne Knoepfe
    const e = this.el; e.overlayMsg.textContent = msg; e.spinner.hidden = false; e.overlay.hidden = false; e.reconnectBtn.hidden = true; e.closeBtn.hidden = true;
  }

  /* =====================================================================
   *  Verbindung
   * ===================================================================== */
  /** Vertrag: startet die Verbindung (ein neues Passwort ersetzt das alte). Fehler zeigt die Sitzung selbst, hier gibt es keine Ausnahme. */
  async connect(password) {
    if (password) this.password = password;
    this.retry = 0; this.everConnected = false; this.firstLogin = true; this.forceGuac = false; this.streamWorked = false;
    return this.start();
  }

  teardown() {
    // Erst den alten Client abkoppeln, dann trennen: sein "getrennt"-Ereignis kommt sofort und wuerde sonst (this.client waere noch er selbst)
    // einen neuen Wiederholungs-Timer setzen, der die frische Verbindung nach 1,5/3/4,5 s wieder abschiesst.
    this.gen++;
    const old = this.client;
    this.client = null; this.tunnel = null; this.display = null; this.mouse = null; this.connected = false;
    try { old && old.disconnect(); } catch { /* egal */ }
    clearTimeout(this.retryTimer); clearTimeout(this.connectTimer); clearTimeout(this.resizeTimer);
    this.stopWindowBar(); this.el.topbar.hidden = true;
    this.el.display.replaceChildren();
    this.resetMods();
  }

  /** Der Stream kam nicht zustande (es wurde nie ein Bild gezeichnet): einmal mit Guacamole neu verbinden, statt einen Fehler zu zeigen.
      Gilt bis die Verbindung verlassen wird. Keine Ausweichung bei Fehlern, die Guacamole genauso haette (Anmeldung abgelehnt, uebernommen, zu viele). */
  fallBackFromStream(client, err) {
    if (!(client instanceof StreamClient)) return false;
    if (client.drawn) this.streamWorked = true;
    if (this.streamWorked || this.forceGuac || this.intentional || this.client !== client) return false;
    if (err && [513, 769, 777, 797].includes(err.code)) return false;
    this.forceGuac = true;
    this.dbg('stream-fallback', { code: err ? err.code : 0, message: err && err.message ? String(err.message).slice(0, 80) : '' });
    this.toast('Der schnelle Stream geht hier nicht, verbinde mit Guacamole …', 3200);
    this.teardown();                       // den Stream sofort abkoppeln: seine spaeteren Ereignisse zaehlen nicht mehr
    setTimeout(() => { if (!this.disposed) this.start(); }, 0);
    return true;
  }

  async start() {
    this.intentional = false; this.fatal = false; this.connectStarted = Date.now();
    this.teardown();
    const gen = this.gen;
    const stale = () => gen !== this.gen || this.disposed;
    this.setState(this.everConnected ? 'reconnecting' : 'connecting', this.everConnected ? 'Verbinde neu …' : 'Verbinde …');
    this.showBusy(this.everConnected ? 'Verbinde neu …' : 'Verbinde …');
    // Hatte der letzte Desktop dieser Verbindung eine Fensterleiste, den Platz dafuer schon jetzt freihalten: sonst schrumpft die Stage kurz nach dem Verbinden
    // und die VM bekommt gleich nach dem Start eine zweite Groesse.
    this.el.topbar.hidden = !(this.conn.kind === 'desktop' && store.get('winbar.' + this.conn.id, store.get('winbar', false)));
    await sleep(30); // Layout setzen, damit die Stage-Groesse stimmt
    if (stale()) return;
    // Eine noch offene Tastatur (Fokus aus der vorigen Sitzung) darf die Groesse nicht bestimmen: sonst bekommt die VM ein flaches Bild
    const mine = cur() === this;
    if (mine && kbFocused()) el.kb.blur();
    for (let i = 0; i < 12 && kbOpen && mine; i++) await sleep(50);
    if (stale()) return;
    const { w, h } = this.computeRemote();
    // Welche Engine: der Test-Schalter pd.engine (localStorage, JSON) hat Vorrang vor der Wahl der Verbindung; der eigene Stream (H.264 im Browser)
    // nur fuer Desktops und nur, wenn der Browser ihn dekodieren kann; sonst Guacamole (auch nach einem misslungenen Stream, siehe fallBackFromStream)
    const choice = this.forceGuac ? 'guac' : store.get('engine', null) || this.conn.engine || 'guac';
    const wantStream = this.conn.kind === 'desktop' && choice === 'stream' && await StreamClient.supported();
    if (stale()) return;
    let ticket, engine = 'guac';
    try {
      const resp = await fetch('/api/remote/ticket', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-JulOS': '1' }, signal: AbortSignal.timeout(10000),
        body: JSON.stringify({ connection: this.conn.id, sid: this.id, password: this.password || undefined, width: w, height: h, dpi: 96, engine: wantStream ? 'stream' : 'guac' }) });
      const data = await resp.json().catch(() => ({}));
      if (stale()) return;
      if (resp.status === 401) return this.requestClose('Die Sitzung ist abgelaufen. Bitte neu anmelden.', 'auth');
      if (!resp.ok) throw new Error(data.error || 'Fehler ' + resp.status);
      ticket = data.ticket; engine = data.engine || 'guac';
    } catch (e) {
      if (stale()) return;
      const msg = e.name === 'TimeoutError' || e.name === 'TypeError' ? 'Keine Verbindung zum Server. Netz prüfen.' : e.message;
      if (!this.everConnected) return this.requestClose(msg);
      this.setState('error', 'Fehler');
      return this.showOverlay(msg);
    }

    const wsBase = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host;
    const isStream = engine === 'stream';
    const tunnel = isStream ? null : new Guacamole.WebSocketTunnel(wsBase + '/api/remote/ws');
    const client = isStream ? new StreamClient(wsBase + '/api/remote/screen') : new Guacamole.Client(tunnel);
    this.engine = engine;
    const display = client.getDisplay();
    this.tunnel = tunnel; this.client = client; this.display = display; this.lastSent = { w, h }; this.gate = new SizeGate();
    this.lay.note('conn', { engine });
    this.el.display.appendChild(display.getElement());
    display.onresize = () => { this.fitDisplay(); const dw = display.getWidth(), dh = display.getHeight(); this.lay.note('remote', { w: dw, h: dh }); this.dbg('remote-size', { w: dw, h: dh }); };
    // Textfeld-Erkennung: Der Mauszeiger der VM wird zum "I-Balken" (schmal, hoch, Hotspot mittig)
    display.oncursor = (canvas, hx, hy) => {
      const c = cursorInfo(canvas, hx, hy);
      if (c.sig !== this.cursor.sig && cursorLogN++ < 40) this.dbg('cursor', { w: c.w, h: c.h, bw: c.bw, bh: c.bh, hx: c.hx, hy: c.hy, ibeam: c.ibeam });
      this.cursor = c;
    };
    if (tunnel) { const inner = tunnel.oninstruction; tunnel.oninstruction = (o, p) => { this.counts.instr++; inner(o, p); }; }   // nur zaehlen (Tests)

    client.onstatechange = (st) => {
      if (this.client !== client) return;
      if (st === 3) { // verbunden
        clearTimeout(this.connectTimer);
        this.connected = true; this.everConnected = true; this.connectedAt = Date.now(); this.frameSeen = false;
        this.setState('connected', 'Verbunden');
        this.startWindowBar(); this.fitDisplay(); this.pushSize(); this.waitForFrame(client); this.applyPause();
        if (cur() === this) focusKbForHardware();
        if (isStream) setTimeout(() => { if (this.client === client && !client.drawn) this.fallBackFromStream(client); }, 10000);   // Groesse gemeldet, aber nie ein Bild: der Dekodierer geht nicht
        // Die Zaehler der Wiederholung gelten erst als zurueckgesetzt, wenn die Verbindung wirklich haelt (sonst endlos: verbunden, sofort getrennt, neu ...)
        setTimeout(() => { if (this.client === client && this.connected) { this.firstLogin = false; this.retry = 0; } }, 12000);
      } else if (st === 5) { // getrennt
        this.connected = false;
        if (!client.closing && this.fallBackFromStream(client)) return;   // ohne Fehlermeldung getrennt, bevor ein Bild kam
        if (this.intentional || this.fatal) return;
        if (!this.everConnected) return; // Fehler wird von onerror gemeldet
        // sofort nach dem Anmelden getrennt = xrdp hat das Passwort abgelehnt: nicht wiederholen
        if (this.firstLogin && Date.now() - this.connectedAt < 12000) return this.requestClose('Anmeldung fehlgeschlagen. Bitte Passwort prüfen.');
        this.scheduleReconnect();
      }
    };
    client.onerror = (err) => {
      if (this.client !== client) return;
      // guacd meldet meist nur "Aborted. See logs."; der Statuscode sagt mehr
      if (this.fallBackFromStream(client, err)) return;
      const code = err && err.code;
      const known = ERROR_TEXT[code];
      const msg = known || ((err && err.message) ? err.message : 'Verbindungsfehler');
      this.connected = false;
      if (code === 777) { this.fatal = true; clearTimeout(this.retryTimer); clearTimeout(this.connectTimer); this.setState('taken', 'Übernommen'); return this.showOverlay(msg); }   // anderswo uebernommen: nicht zurueckholen, sonst streiten sich zwei Fenster
      if (code === 797) { this.fatal = true; clearTimeout(this.retryTimer); clearTimeout(this.connectTimer); return this.requestClose(msg); }   // Obergrenze der Sitzungen: Meldung des Servers, Sitzung zu
      if (!this.everConnected) this.requestClose(known || 'Verbindung nicht möglich: ' + msg);
      else if (this.firstLogin && Date.now() - this.connectedAt < 12000) this.requestClose('Anmeldung fehlgeschlagen. Bitte Passwort prüfen.');
      else { this.setState('error', 'Fehler'); this.showOverlay(msg); }
    };
    if (tunnel) tunnel.onerror = (st) => {
      if (this.client !== client) return;
      if (!this.everConnected) return this.requestClose('Keine Verbindung zum Server: ' + (st && st.message ? st.message : ''));
      // Die Leitung riss ab (Netz weg), ohne dass guacd etwas gemeldet hat: nur diese Sitzung verbindet neu, die anderen laufen weiter
      if (this.connected && !this.intentional && !this.fatal) { this.connected = false; this.scheduleReconnect(); }
    };
    client.onclipboard = (stream, mimetype) => {
      if (!/^text\//.test(mimetype)) return;
      const reader = new Guacamole.StringReader(stream);
      let data = '';
      reader.ontext = (t) => { data += t; };
      reader.onend = () => {
        this.clipText = data;
        if (cur() === this) el.remoteClip.value = data;
        // Strg+C / Strg+X in der VM: Text gleich in die Zwischenablage dieses Browsers legen (braucht HTTPS oder localhost und eine eben erfolgte Eingabe).
        // Nur fuer die aktive Sitzung: eine im Hintergrund darf die Zwischenablage nicht ueberschreiben.
        if (cur() !== this) return;
        if (navigator.clipboard && navigator.clipboard.writeText && document.hasFocus()) navigator.clipboard.writeText(data).then(() => this.toast('Kopiert', 1200), () => this.toast('Text aus der VM kopiert (Symbol Zwischenablage)', 2200));
        else this.toast('Text aus der VM kopiert (Symbol Zwischenablage)', 2200);
      };
    };

    this.setupMouse(client, display);
    client.connect('ticket=' + encodeURIComponent(ticket));
    // kommt innerhalb von 20 s keine Verbindung zustande: nicht ewig "Verbinde ..." zeigen
    clearTimeout(this.connectTimer);
    this.connectTimer = setTimeout(() => {
      if (this.client !== client || this.connected) return;
      if (this.fallBackFromStream(client)) return;
      this.fatal = true; try { client.disconnect(); } catch { /* egal */ }
      this.setState('error', 'Keine Verbindung');
      this.showOverlay('Die Verbindung kommt nicht zustande. Netz prüfen und „Neu verbinden“ tippen.');
    }, 20000);
  }

  /* Nach dem Verbinden bleibt die Ladeanzeige stehen, bis wirklich ein Bild da ist (sonst ist alles schwarz).
     Kommt keins, fordert eine kleine Groessenaenderung vom Server ein neues Vollbild an. */
  nudgeSize(delta = 2) {
    if (!this.client || !this.connected) return;
    const { w, h } = this.computeRemote();
    this.sendRemoteSize(w, h - delta, 'nudge');
    setTimeout(() => { if (this.client && this.connected) this.sendRemoteSize(w, h, 'nudge'); }, 350);
  }
  waitForFrame(client) {
    const started = Date.now();
    let nudges = 0, probes = 0, sizedAt = 0, released = false;
    this.showBusy('Bild wird geladen …');
    const probe = document.createElement('canvas'); probe.width = probe.height = 8;
    const pc = probe.getContext('2d', { willReadFrequently: true });
    let seen = { w: 0, h: 0, max: 0 };
    const hasContent = () => {
      try {
        const cv = this.display.flatten();                       // alle Ebenen zusammengesetzt (die Standardebene allein kann leer sein)
        seen = { w: cv.width, h: cv.height, max: 0 };
        if (!cv.width || !cv.height) return false;
        pc.clearRect(0, 0, 8, 8); pc.drawImage(cv, 0, 0, 8, 8);
        const d = pc.getImageData(0, 0, 8, 8).data;
        for (let i = 0; i < d.length; i += 4) seen.max = Math.max(seen.max, d[i], d[i + 1], d[i + 2]);
        return seen.max > 10;
      } catch { return true; }                                // Pruefung nicht moeglich: nicht blockieren
    };
    // Die Ladeanzeige geht weg, sobald die VM eine Bildgroesse gemeldet hat (oder nach 4 s): Bedienung und Tastenleiste sollen
    // nicht auf ein "nicht schwarzes" Bild warten. Die Anstupser laufen danach unsichtbar weiter, bis Inhalt da ist.
    const release = (why) => { if (released) return; released = true; this.dbg('overlay-weg', { ms: Date.now() - started, why }); this.el.overlay.hidden = true; this.el.spinner.hidden = true; };
    const tick = () => {
      if (this.client !== client || !this.connected) return;
      const age = Date.now() - started;
      const ok = hasContent();
      if (seen.w > 0 && !sizedAt) sizedAt = Date.now();
      if (probes++ % 4 === 0 && probes < 120) this.dbg('frameprobe', { ms: age, nudges, ...seen });
      if (sizedAt || age > 4000) release(sizedAt ? 'groesse' : 'zeit');
      if (ok) this.frameSeen = true;
      if (ok || age > 25000) { this.frameSeen = true; this.dbg('firstframe', { ms: age, nudges, ok, ...seen }); release('bild'); return; }
      // Anstupsen (die VM zeichnet nach einer echten Groessenaenderung alles neu) erst, wenn es eine Bildgroesse gibt:
      // vorher geht die Aenderung ins Leere, weil die VM noch gar nicht verbunden ist
      if (sizedAt && this.engine !== 'stream') {
        const since = Date.now() - sizedAt;
        // erst warten (das Ziel braucht beim Wiederverbinden mehrere Sekunden), dann hoechstens zwei sanfte Anstupser
        if (nudges === 0 && since > 8000) { nudges = 1; this.nudgeSize(2); }
        else if (nudges === 1 && since > 16000) { nudges = 2; this.nudgeSize(64); }
      }
      setTimeout(tick, 250);
    };
    tick();
  }

  scheduleReconnect() {
    if (this.retry >= 3) { this.setState('disconnected', 'Getrennt'); this.showOverlay('Verbindung getrennt. Läuft die Sitzung noch in einem anderen Fenster oder auf einem anderen Gerät?'); return; }
    this.retry += 1;
    const wait = 1500 * this.retry;
    this.setState('reconnecting', 'Verbindung getrennt, neuer Versuch …');
    this.showBusy(`Verbindung getrennt – neuer Versuch in ${Math.round(wait / 1000)} s …`);
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => { if (!this.intentional && !this.disposed && this.hasCredentials()) this.start(); }, wait);
  }

  /* =====================================================================
   *  Groesse: Remote-Aufloesung folgt dem Fenster (Tastatur, Drehen, Zoom)
   * ===================================================================== */
  /** Die Bildflaeche; ist die Sitzung gerade versteckt (Groesse 0), die zuletzt gemessene, damit eine Sitzung im Hintergrund nicht winzig verbindet. */
  stageSize() {
    const r = this.el.stage.getBoundingClientRect();
    if (r.width >= 50 && r.height >= 50) return { w: r.width, h: r.height };
    return this.lastStage || lastStageSize;
  }
  computeRemote() {
    const r = this.stageSize();
    const dpr = window.devicePixelRatio || 1;
    const w = Math.max(400, Math.floor((r.w * dpr * this.k) / 4) * 4);
    const h = Math.max(300, Math.floor((r.h * dpr * this.k) / 2) * 2);
    return { w, h };
  }
  fitDisplay() {
    if (!this.display) return;
    const rw = this.display.getWidth();
    if (!rw) return;
    const r = this.el.stage.getBoundingClientRect();
    if (r.width < 50) return;                       // versteckt: nichts zu skalieren
    this.display.scale(r.width / rw);
  }
  // Wann die Groesse wirklich an die VM geht, entscheidet SizeGate (layoutlog.js): nur bei echtem Unterschied (8 x 16 Geraetepixel Toleranz),
  // nicht waehrend die Tastatur faehrt, hoechstens einmal pro Sekunde und mit Notbremse (4 Aenderungen in 20 s, dann 5 s Pause).
  // Jede Aenderung baut das Bild der VM neu auf; so landet das Handy auch dann nicht in einer Dauerschleife, wenn ein Geraet Groessen
  // in schneller Folge meldet (Adressleiste, Tastatur-Animation, Systemleisten). Eine versteckte Sitzung meldet gar keine Groesse.
  sendRemoteSize(w, h, why) {
    this.lastSent = { w, h };
    this.lay.note('send', { w, h });
    this.dbg('sendSize', { w, h, k: this.k, dpr: window.devicePixelRatio, why });
    this.client.sendSize(w, h);
  }
  pushSize(force = false) {
    if (!this.client || !this.connected || !this.visible) return;
    // Waehrend der VM ihr erstes Bild aufbaut, keine Groessenaenderungen schicken: jede startet beim Ziel das Anzeigen neu und verzoegert das erste Bild
    // (die Groesse steht schon im Verbindungsaufbau). Spaetestens nach 6 s geht es normal weiter.
    if (!this.frameSeen && Date.now() - this.connectedAt < 6000) { this.scheduleSize(); return; }
    const want = this.computeRemote();
    const now = Date.now();
    const d = this.gate.decide(now, want, this.lastSent, { force, holdUntil: S.sizeHoldUntil || 0 });
    if (d.action === 'skip') return;
    if (d.action === 'wait') {
      if (d.why === 'storm') this.dbg('size-storm', { w: want.w, h: want.h, n: d.n });
      this.scheduleSize(d.delay);
      return;
    }
    this.gate.sent(now);
    this.sendRemoteSize(want.w, want.h, force ? 'force' : 'fit');
  }
  scheduleSize(delay = 450) { clearTimeout(this.resizeTimer); this.resizeTimer = setTimeout(() => { this.pushSize(); this.fitDisplay(); }, delay); }
  onStageResize() {
    this.fitDisplay();
    const r = this.el.stage.getBoundingClientRect();
    if (r.width < 50 || r.height < 50) return;                                      // versteckt (display: none) oder noch nicht eingehaengt: keine neue Groesse
    this.lastStage = { w: r.width, h: r.height }; lastStageSize = this.lastStage;
    if (Math.abs(r.width - this.stageW) < 1 && Math.abs(r.height - this.stageH) < 1) return;    // nur ein Rundungs- oder Startsignal: keine neue Groesse
    this.stageW = r.width; this.stageH = r.height;
    this.lay.note('stage', { w: Math.round(r.width), h: Math.round(r.height) });
    this.lay.layout('stage');
    this.scheduleSize();
  }

  zoomPercent() { return Math.round(100 / this.k); }
  showZoomPop() {                                   // Zoomstufe nur kurz einblenden
    this.el.zoomPop.textContent = this.zoomPercent() + ' %'; this.el.zoomPop.classList.add('show');
    clearTimeout(this.zoomTimer); this.zoomTimer = setTimeout(() => this.el.zoomPop.classList.remove('show'), 900);
  }
  setK(k, announce = true) {
    this.k = clamp(k, 0.35, 1.8); store.set('k.' + this.conn.id, this.k);          // je Verbindung gemerkt
    if (announce) this.showZoomPop();
    this.scheduleSize();
  }

  /* =====================================================================
   *  Fensterleiste und Programm-Modus (braucht den Agenten auf dem Zielrechner)
   * ===================================================================== */
  async startWindowBar() {
    this.stopWindowBar();
    const gen = this.gen, W = this.W;
    W.available = false;
    // Der Agent gehoert zur Maschine, neben der der Server laeuft: nur fuer Desktop-Verbindungen anbieten
    if (this.conn.kind === 'desktop') { try { const h = await (await fetch('/api/agent/health')).json(); W.available = !!h.available; } catch { W.available = false; } }
    if (gen !== this.gen || this.disposed) return;
    this.dbg('agent', { available: W.available });
    if (this.conn.kind === 'desktop') store.set('winbar.' + this.conn.id, W.available);
    this.renderWindows();
    if (!W.available) return;
    this.pollWindows(); W.timer = setInterval(() => this.pollWindows(), 2000);
  }
  stopWindowBar() { const W = this.W; clearInterval(W.timer); W.timer = null; W.list = []; W.lastCls = null; W.candCls = null; this.winSig = ''; }
  async pollWindows() {
    const W = this.W;
    if (!this.connected || !W.available || document.hidden || !this.visible) return;     // im Hintergrund wird nicht abgefragt
    const gen = this.gen;
    try {
      const d = await agent('/windows');
      if (gen !== this.gen) return;
      W.list = d.windows || []; W.mode = d.mode || 'desktop';
      this.renderWindows(); this.applyProfile();
    } catch { /* naechster Versuch */ }
  }
  // Ein Programm wird erst nach zwei Abfragen (4 s) in Folge als aktiv gewertet und der Wechsel der Anzeigegroesse haelt mindestens 8 s Abstand.
  // Springt der Fokus der VM sonst zwischen zwei Programmen hin und her (Meldungsfenster, Menues, Tooltips), bekaeme die VM jedes Mal eine
  // neue Aufloesung: der Bildschirm "zappelt".
  applyProfile() {
    const W = this.W;
    if (W.mode !== 'apps') { W.lastCls = null; W.candCls = null; return; }
    const act = W.list.find((w) => w.active); const cls = act ? act.class : null;
    if (!cls || cls === W.lastCls) { W.candCls = null; return; }
    if (W.candCls !== cls) { W.candCls = cls; W.candN = 1; } else W.candN += 1;
    if (W.candN < PROFILE.stablePolls || Date.now() - (W.profileAt || 0) < PROFILE.minGap) return;
    W.candCls = null; W.profileAt = Date.now();
    W.lastCls = cls;
    const k = profiles[cls] ?? defaultProfile(cls);
    if (k && Math.abs(k - this.k) > 0.02) { this.setK(k, false); this.toast('Anzeige für ' + (act.app || cls) + ' angepasst', 1800); }
  }
  rememberProfile() {                               // Zoom von Hand: fuer das aktive Programm merken (nur im Programm-Modus)
    if (this.W.mode !== 'apps' || !this.W.lastCls) return;
    profiles[this.W.lastCls] = this.k; store.set('profiles', profiles);
  }
  renderWindows() {
    const W = this.W, row = this.el.rowWin, topbar = this.el.topbar;
    if (!W.available) { topbar.hidden = true; this.winSig = ''; return; }
    // Die Leiste wird alle 2 s abgefragt. Nur neu aufbauen, wenn sich etwas geaendert hat: sonst springt sie waehrenddessen an den Anfang
    // (Scrollstand weg), Symbole flackern und ein gerade begonnener Fingertipp trifft ein schon ersetztes Element.
    const sig = W.mode + '|' + W.list.map((w) => [w.id, w.active ? 1 : 0, w.minimized ? 1 : 0, w.title, w.app, (w.icon || '').length].join(':')).join('|');
    if (!topbar.hidden && sig === this.winSig) return;
    this.winSig = sig;
    const keepScroll = row.scrollLeft;
    topbar.hidden = false; row.replaceChildren();
    const mode = document.createElement('div'); mode.className = 'chip mode'; mode.setAttribute('role', 'button');
    mode.innerHTML = ico(W.mode === 'apps' ? 'apps' : 'desktop') + `<span>${W.mode === 'apps' ? 'Apps' : 'Desktop'}</span>`;
    mode.addEventListener('click', () => this.toggleMode()); row.appendChild(mode);
    for (const w of W.list) {
      const c = document.createElement('div'); c.className = 'chip' + (w.active ? ' active' : '') + (w.minimized ? ' min' : ''); c.setAttribute('role', 'button');
      const img = w.icon ? `<img alt="" src="data:image/png;base64,${w.icon}">` : '';
      const t = document.createElement('span'); t.textContent = (w.title || w.app || '?').slice(0, 40);
      c.innerHTML = img; c.appendChild(t);
      let timer = null, longDone = false;
      c.addEventListener('pointerdown', () => { longDone = false; timer = setTimeout(() => { longDone = true; this.openWinSheet(w); }, 480); });
      const cancel = () => clearTimeout(timer);
      c.addEventListener('pointerup', cancel); c.addEventListener('pointercancel', cancel); c.addEventListener('pointerleave', cancel);
      c.addEventListener('click', () => { if (longDone) { longDone = false; return; } this.activateWindow(w.id, W.mode === 'apps'); });
      row.appendChild(c);
    }
    const add = document.createElement('div'); add.className = 'chip add'; add.setAttribute('role', 'button'); add.innerHTML = ico('add');
    add.addEventListener('click', () => openApps(this)); row.appendChild(add);
    row.scrollLeft = keepScroll;
  }
  async activateWindow(id, maximize) {
    try { await agent(`/windows/${id}/activate`, 'POST', { maximize }); setTimeout(() => this.pollWindows(), 350); } catch (e) { this.toast('Fenster: ' + e.message); }
  }
  async toggleMode() {
    const W = this.W;
    try { const d = await agent('/mode', 'POST', { mode: W.mode === 'apps' ? 'desktop' : 'apps' }); W.mode = d.mode; W.lastCls = null; this.renderWindows(); this.toast(W.mode === 'apps' ? 'Programm-Modus: Taskleiste der VM ausgeblendet' : 'Desktop-Modus', 2200); setTimeout(() => this.pollWindows(), 500); }
    catch (e) { this.toast('Modus: ' + e.message); }
  }
  openWinSheet(w) {
    const sh = this.el.winSheet; sh.replaceChildren();
    const item = (ic, text, fn) => { const d = document.createElement('div'); d.className = 'mi'; d.setAttribute('role', 'menuitem'); d.innerHTML = `<span class="mic">${ico(ic)}</span><span class="mt">${text}</span>`; d.addEventListener('click', () => { this.closeWinSheet(); fn(); }); sh.appendChild(d); };
    item('window', 'Nach vorn und maximieren', () => this.activateWindow(w.id, true));
    item('window', 'Nach vorn (Größe lassen)', () => this.activateWindow(w.id, false));
    item('minimize', 'Minimieren', async () => { try { await agent(`/windows/${w.id}/minimize`, 'POST', {}); setTimeout(() => this.pollWindows(), 300); } catch (e) { this.toast(e.message); } });
    item('close', 'Schließen', async () => { try { await agent(`/windows/${w.id}/close`, 'POST', {}); setTimeout(() => this.pollWindows(), 500); } catch (e) { this.toast(e.message); } });
    item('palette', 'Anzeigegröße für dieses Programm merken', () => { profiles[w.class] = this.k; store.set('profiles', profiles); this.toast('Gemerkt: ' + this.zoomPercent() + ' % für ' + (w.app || w.class)); });
    sh.hidden = false; this.el.scrim.hidden = false;
  }
  closeWinSheet() { this.el.winSheet.hidden = true; if (this.el.menu.hidden) this.el.scrim.hidden = true; }

  /* =====================================================================
   *  Tasten dieser Sitzung: Umschalter, Warteschlange, Leiste
   * ===================================================================== */
  kdown(s) { return this.client && this.client.sendKeyEvent(1, s); }
  kup(s) { return this.client && this.client.sendKeyEvent(0, s); }
  setMod(name, mode) {
    const prev = this.mods[name];
    if (prev === mode) return;
    if (prev === 'off') this.kdown(MODSYM[name]);
    if (mode === 'off') this.kup(MODSYM[name]);
    this.mods[name] = mode;
    this.refreshModButtons();
  }
  cycleMod(name) { this.setMod(name, this.mods[name] === 'off' ? 'once' : this.mods[name] === 'once' ? 'lock' : 'off'); }
  consumeOnce() { for (const n of Object.keys(this.mods)) if (this.mods[n] === 'once') this.setMod(n, 'off'); }
  resetMods() { for (const n of Object.keys(this.mods)) this.mods[n] = 'off'; this.refreshModButtons(); }
  // Alle Tasten laufen strikt nacheinander durch eine Warteschlange (sonst vertauscht sich die Reihenfolge,
  // zum Beispiel bei "Backspace x3, dann neues Wort" nach einer Autokorrektur). Jede Sitzung hat ihre eigene.
  enqueue(fn) { this.keyChain = this.keyChain.then(fn).catch(() => {}); return this.keyChain; }
  sendChord(syms, gap = 12) {
    return this.enqueue(async () => {
      for (const s of syms) { this.kdown(s); await sleep(gap); }
      for (const s of [...syms].reverse()) { this.kup(s); await sleep(gap); }
      this.consumeOnce();
    });
  }
  tapKey(sym) { return this.enqueue(async () => { this.kdown(sym); await sleep(8); this.kup(sym); this.consumeOnce(); }); }
  typeText(str) {
    return this.enqueue(async () => {
      for (const ch of str) {
        const sym = charKeysym(ch);
        this.kdown(sym); await sleep(6); this.kup(sym); await sleep(4);
        this.consumeOnce();
      }
    });
  }
  // Text in die Zwischenablage der VM legen und danach die Einfuege-Taste druecken (die Pause laesst die Zwischenablage-Meldung ankommen)
  pasteToRemote(txt, keys) {
    const stream = this.client.createClipboardStream('text/plain');
    const w = new Guacamole.StringWriter(stream); w.sendText(txt); w.sendEnd();
    this.enqueue(async () => { await sleep(180); });
    this.sendChord(keys, 8);
  }
  refreshModButtons() {
    this.element.querySelectorAll('.sv-rowsTop .k[data-mod], .sv-rowsBottom .k[data-mod]').forEach((b) => {
      const m = this.mods[b.dataset.mod];
      b.classList.toggle('mod-once', m === 'once');
      b.classList.toggle('mod-lock', m === 'lock');
    });
  }
  async activateKey(def) {
    try {
      if (def.type === 'mod') {
        const name = MODNAME[String(def.value).toLowerCase()];
        if (!name) throw new Error('Unbekannter Umschalter');
        this.cycleMod(name);
      } else if (def.type === 'text') {
        await this.typeText(def.value || '');
      } else if (def.type === 'action') {
        const f = ACTIONS[String(def.value).toLowerCase()];
        if (!f) throw new Error('Unbekannte Aktion');
        f(this);
      } else {
        await this.sendChord(parseChord(def.value));
      }
    } catch (e) { this.toast(e.message); }
  }

  keyEl(def, r, i) {
    const b = document.createElement('div');
    b.setAttribute('role', 'button'); b.className = 'k' + (S.editing ? ' edit' : '');
    const isIcon = String(def.label).startsWith(':');
    let icon = isIcon ? def.label.slice(1) : '';
    if (def.type === 'action') {
      const act = String(def.value).toLowerCase();
      b.dataset.act = act;
      if (act === 'expand') icon = S.expanded ? 'expand_more' : 'expand_less';   // Pfeil zeigt, was der naechste Tipp tut
      if (this.actOn[act]) b.classList.add('on');
      if (act === 'sessions' && Sessions.count > 1) b.dataset.badge = String(Sessions.count);
    }
    if (isIcon && ICONS[icon]) { b.innerHTML = ico(icon); b.classList.add('ic'); } else b.textContent = def.label;
    b.dataset.r = String(r); b.dataset.i = String(i);
    if (def.type === 'mod') b.dataset.mod = MODNAME[String(def.value).toLowerCase()] || '';
    b.addEventListener('click', () => {
      if (suppressClick) { suppressClick = false; return; }
      if (S.editing) openKeyEditor(r, i); else this.activateKey(def);
    });
    if (S.editing) enableDrag(b);
    return b;
  }
  addRow(row, r, label, bar) {
    const wrap = document.createElement('div');
    wrap.className = 'row ' + (bar ? 'bar' : 'grp') + (S.editing ? ' editing' : '');
    const rk = document.createElement('div'); rk.className = 'rk'; rk.dataset.r = String(r);
    if (label && !bar) { const t = document.createElement('div'); t.className = 'gt'; t.textContent = row.title; rk.appendChild(t); }
    row.items.forEach((def, i) => rk.appendChild(this.keyEl(def, r, i)));
    if (S.editing) {
      const add = document.createElement('div'); add.setAttribute('role', 'button'); add.className = 'k add'; add.innerHTML = ico('add') + '<span>Taste</span>';
      add.addEventListener('click', () => openKeyEditor(r, -1)); rk.appendChild(add);
      if (bar || r === rows.length - 1) {
        const done = document.createElement('div'); done.setAttribute('role', 'button'); done.className = 'k'; done.textContent = 'Fertig';
        done.addEventListener('click', () => { S.editing = false; renderAllRows(); toast('Tasten gespeichert'); }); rk.appendChild(done);
      }
    }
    wrap.append(rk);
    return wrap;
  }
  /* Oben: Akkordeon aus drei schmalen Koepfen (nur Symbol und Pfeil) und darunter die Tasten der geoeffneten Gruppe. Beim Bearbeiten sind alle Gruppen offen. */
  renderRows() {
    const top = this.el.rowsTop, bottom = this.el.rowsBottom;
    top.replaceChildren(); bottom.replaceChildren();
    const groups = rows.map((row, r) => ({ row, r })).filter((g) => !g.row.fixed);
    if (S.group && !groups.some((g) => g.row.id === S.group)) S.group = groups[0].row.id;
    if (!S.editing) {
      const heads = document.createElement('div'); heads.className = 'acc'; heads.setAttribute('role', 'tablist');
      for (const { row } of groups) {
        const open = row.id === S.group;
        const h = document.createElement('div'); h.setAttribute('role', 'tab'); h.className = 'acch' + (open ? ' sel' : ''); h.setAttribute('aria-label', row.title); h.setAttribute('aria-expanded', open ? 'true' : 'false');
        h.innerHTML = ico(row.icon || 'apps') + ico(open ? 'expand_less' : 'expand_more');
        h.addEventListener('click', () => { S.group = open ? '' : row.id; store.set('group', S.group); renderAllRows(); });
        heads.appendChild(h);
      }
      top.appendChild(heads);
    }
    for (const { row, r } of groups) if (S.editing || row.id === S.group) top.appendChild(this.addRow(row, r, S.editing, false));
    rows.forEach((row, r) => { if (row.fixed) bottom.appendChild(this.addRow(row, r, false, true)); });
    this.applyKeysOpen(); this.refreshModButtons();
  }
  /* Aufklappen: die Leiste bleibt, darueber erscheinen die weiteren Zeilen */
  applyKeysOpen() { this.el.rowsTop.hidden = !(S.expanded || S.editing); }
  setActOn(name, on) {
    this.actOn[name] = on;
    this.el.dock.querySelectorAll(`.k[data-act="${name}"]`).forEach((b) => b.classList.toggle('on', on));
  }

  /* =====================================================================
   *  Menue dieser Sitzung (kein Dialog, damit die Tastatur offen bleibt)
   * ===================================================================== */
  updateMenuLabels() {
    const set = (c, t) => { const n = this.el.menu.querySelector(c); if (n) n.textContent = t; };
    set('.mv-kb', S.kbMode === 'rich' ? 'mit Bildern' : S.kbMode === 'normal' ? 'mit Vorschlägen' : 'ohne Vorschläge');
    set('.mv-touch', S.touchMode === 'direct' ? 'Tippen = Klick' : 'Touchpad');
    set('.mv-autokb', S.autoKb ? 'an' : 'aus');
    set('.mv-autopaste', S.autoPaste ? 'an' : 'aus');
    set('.mv-count', String(Sessions.count));
  }
  openMenu() { applyKbMode(); this.updateMenuLabels(); this.el.menu.hidden = false; this.el.scrim.hidden = false; this.setActOn('menu', true); }
  closeMenu() { this.el.menu.hidden = true; if (this.el.winSheet.hidden) this.el.scrim.hidden = true; this.setActOn('menu', false); }
  toggleMenu() { if (this.el.menu.hidden) this.openMenu(); else this.closeMenu(); }
  onMenuClick(e) {
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (!act) return;
    if (['kbMode', 'autoKb', 'touchMode', 'autoPaste'].includes(act)) {
      if (act === 'kbMode') S.kbMode = S.kbMode === 'rich' ? 'normal' : S.kbMode === 'normal' ? 'direct' : 'rich';
      if (act === 'autoKb') S.autoKb = !S.autoKb;
      if (act === 'autoPaste') S.autoPaste = !S.autoPaste;
      if (act === 'touchMode') S.touchMode = S.touchMode === 'direct' ? 'pad' : 'direct';
      applyKbMode(); return;
    }
    this.closeMenu();
    if (act === 'reconnect') { this.retry = 0; if (this.hasCredentials()) this.start(); }
    else if (act === 'switcher') Sessions.requestSwitcher();
    else if (act === 'home') Sessions.requestHome();
    else if (act === 'extra') Sessions.open(this.conn, { password: this.password, extra: true });
    else if (act === 'editKeys') { S.editing = true; renderAllRows(); toast('Taste antippen = bearbeiten, gedrückt halten = verschieben (auch in andere Zeilen)', 3600); }
    else if (act === 'resetKeys') { if (confirm('Sondertasten auf Standard zurücksetzen?')) { rows = clone(DEFAULT_ROWS); saveRows(); renderAllRows(); } }
    else if (act === 'password') openPwDialog();
    else if (act === 'theme') openThemeDialog();
    else if (act === 'install') installApp();
    else if (act === 'logout') this.requestClose();
  }

  /* =====================================================================
   *  Maus (Desktop-Browser) und Touch (Handy)
   * ===================================================================== */
  remoteXY(cx, cy) {
    const r = this.el.display.getBoundingClientRect();
    const s = this.display ? this.display.getScale() : 1;
    return { x: Math.round((cx - r.left) / s), y: Math.round((cy - r.top) / s) };
  }
  mouseSend(x, y, l = false, m = false, r = false, up = false, down = false) {
    if (this.client) this.client.sendMouseState(new Guacamole.Mouse.State(x, y, l, m, r, up, down));
  }
  setupMouse(client, display) {
    // echte Maus (nur Desktop-Browser); Touch laeuft ueber die eigene Gestenlogik
    const mouse = new Guacamole.Mouse(this.el.touch);
    this.mouse = mouse;
    const fwd = (state) => {
      if (this.client !== client) return;
      const s = display.getScale() || 1;
      client.sendMouseState(new Guacamole.Mouse.State(state.x / s, state.y / s, state.left, state.middle, state.right, state.up, state.down));
    };
    mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = fwd;
    mouse.onmouseout = () => {};
    display.showCursor(true);
  }
  async clickAt(x, y, button = 'left') {
    this.mouseSend(x, y);
    await sleep(10);
    this.mouseSend(x, y, button === 'left', false, button === 'right');
    await sleep(35);
    this.mouseSend(x, y, false, false, false);
    this.consumeOnce();
  }
  padPoint() {
    const G = this.G;
    if (!G.px && !G.py && this.display) { G.px = this.display.getWidth() / 2; G.py = this.display.getHeight() / 2; }
    return { x: G.px, y: G.py };
  }

  /* ---------- Touch-Gesten (je Sitzung, auf deren Touch-Ebene) ---------- */
  wireTouch() {
    const G = this.G, touch = this.el.touch;
    touch.addEventListener('touchstart', (e) => {
      e.preventDefault();
      for (const t of e.changedTouches) G.pts.set(t.identifier, { x: t.clientX, y: t.clientY });
      const pts = [...G.pts.values()];
      if (pts.length === 1) {
        G.mode = 'pending'; G.sx = pts[0].x; G.sy = pts[0].y; G.lx = pts[0].x; G.ly = pts[0].y; G.st = Date.now(); G.moved = false;
        clearTimeout(G.timer);
        if (S.touchMode !== 'pad') { const q0 = this.remoteXY(pts[0].x, pts[0].y); this.mouseSend(q0.x, q0.y); } // Zeiger vorab hinbewegen: Cursorform (Textfeld?) aktualisiert sich
        G.timer = setTimeout(() => { if (G.mode === 'pending') { G.mode = 'long'; if (navigator.vibrate) navigator.vibrate(15); } }, 520);
      } else if (pts.length === 2) {
        clearTimeout(G.timer);
        if (G.pressed) { const p = this.remoteXY(G.lx, G.ly); this.mouseSend(p.x, p.y); G.pressed = false; }
        G.mode = 'two'; G.d0 = dist2(pts[0], pts[1]); G.k0 = this.k; G.newK = this.k;
        G.mid0 = mid2(pts[0], pts[1]); G.midLast = G.mid0; G.decided = null; G.scrollAcc = 0; G.moved = false; G.st = Date.now();
      }
    }, { passive: false });

    touch.addEventListener('touchmove', (e) => {
      e.preventDefault();
      for (const t of e.changedTouches) if (G.pts.has(t.identifier)) G.pts.set(t.identifier, { x: t.clientX, y: t.clientY });
      const pts = [...G.pts.values()];
      if (pts.length === 1 && (G.mode === 'pending' || G.mode === 'long' || G.mode === 'drag' || G.mode === 'pad')) {
        const p = pts[0];
        const moved = Math.hypot(p.x - G.sx, p.y - G.sy);
        if (G.mode === 'pending' && moved > 10) {
          clearTimeout(G.timer);
          if (S.touchMode === 'pad') { G.mode = 'pad'; } else { G.mode = 'drag'; const s0 = this.remoteXY(G.sx, G.sy); this.mouseSend(s0.x, s0.y); this.mouseSend(s0.x, s0.y, true); G.pressed = true; }
        } else if (G.mode === 'long' && moved > 10) {
          G.mode = 'drag';
          if (S.touchMode === 'pad') { const q = this.padPoint(); this.mouseSend(q.x, q.y, true); } else { const s0 = this.remoteXY(G.sx, G.sy); this.mouseSend(s0.x, s0.y); this.mouseSend(s0.x, s0.y, true); }
          G.pressed = true;
        }
        if (G.mode === 'drag' || G.mode === 'pad') {
          G.moved = true;
          if (S.touchMode === 'pad') {
            const s = this.display ? this.display.getScale() : 1;
            this.padPoint();
            G.px += ((p.x - G.lx) / s) * 1.6; G.py += ((p.y - G.ly) / s) * 1.6;
            G.px = clamp(G.px, 0, this.display ? this.display.getWidth() : 0); G.py = clamp(G.py, 0, this.display ? this.display.getHeight() : 0);
            this.mouseSend(G.px, G.py, G.pressed);
          } else {
            const q = this.remoteXY(p.x, p.y); this.mouseSend(q.x, q.y, true);
          }
        }
        G.lx = p.x; G.ly = p.y;
      } else if (pts.length === 2 && G.mode === 'two') {
        const d = dist2(pts[0], pts[1]); const m = mid2(pts[0], pts[1]);
        if (!G.decided) {
          if (Math.abs(d - G.d0) > 28) G.decided = 'pinch';
          else if (Math.hypot(m.x - G.mid0.x, m.y - G.mid0.y) > 18) G.decided = 'scroll';
        }
        if (G.decided) G.moved = true;
        if (G.decided === 'pinch' && d > 10) {
          G.newK = clamp(G.k0 * (G.d0 / d), 0.35, 1.8);
          this.toast('Anzeige ' + Math.round(100 / G.newK) + ' %', 600);
        } else if (G.decided === 'scroll') {
          const dy = m.y - G.midLast.y; G.midLast = m; G.scrollAcc += dy;
          const q = this.remoteXY(m.x, m.y);
          while (Math.abs(G.scrollAcc) >= 34) {
            const down = G.scrollAcc < 0; // Finger nach oben = nach unten scrollen
            this.mouseSend(q.x, q.y, false, false, false, !down, down);
            this.mouseSend(q.x, q.y, false, false, false, false, false);
            G.scrollAcc += down ? 34 : -34;
          }
        }
      }
    }, { passive: false });

    const endTouch = async (e) => {
      e.preventDefault();
      const before = G.pts.size;
      for (const t of e.changedTouches) G.pts.delete(t.identifier);
      if (before === 1 && G.pts.size === 0) {
        clearTimeout(G.timer);
        const dur = Date.now() - G.st;
        if (G.mode === 'pending' && dur < 380) {
          // Tippen auf ein Textfeld (Zeiger ist ein I-Balken): Tastatur gleich in der Geste oeffnen
          if (tapLogN++ < 40) this.dbg('tap', { ibeam: this.cursor.ibeam, kb: kbOpen, auto: S.autoKb, cur: this.cursor.sig || '' });
          if (S.autoKb && this.cursor.ibeam && !kbOpen) { this.dbg('autokb', { when: 'tap' }); el.kb.blur(); el.kb.focus(); }
          if (S.touchMode === 'pad') { const q = this.padPoint(); await this.clickAt(q.x, q.y, 'left'); }
          else { const q = this.remoteXY(G.sx, G.sy); await this.clickAt(q.x, q.y, 'left'); }
          // Zeigerform kommt erst nach der Mausbewegung an. Electron-Programme wie Claude liefern den I-Balken erst nach rund einer
          // Sekunde, deshalb ein Zeitfenster statt einer einzelnen Pruefung.
          const t0 = Date.now();
          const poll = () => {
            if (!S.autoKb || !this.connected || kbOpen || Date.now() - t0 > 1600) return;
            if (this.cursor.ibeam) { this.dbg('autokb', { when: 'late', ms: Date.now() - t0 }); el.kb.focus(); return; }
            setTimeout(poll, 100);
          };
          setTimeout(poll, 120);
        } else if (G.mode === 'long') {
          if (S.touchMode === 'pad') { const q = this.padPoint(); await this.clickAt(q.x, q.y, 'right'); }
          else { const q = this.remoteXY(G.sx, G.sy); await this.clickAt(q.x, q.y, 'right'); }
        } else if (G.mode === 'drag' || G.mode === 'pad') {
          if (G.pressed) {
            if (S.touchMode === 'pad') this.mouseSend(G.px, G.py, false); else { const q = this.remoteXY(G.lx, G.ly); this.mouseSend(q.x, q.y, false); }
            G.pressed = false; this.consumeOnce();
          }
        }
        G.mode = 'idle';
      } else if (G.mode === 'two' && G.pts.size === 0) {
        const dur = Date.now() - G.st;
        if (!G.decided && !G.moved && dur < 350) { // Zwei-Finger-Tipp = Rechtsklick
          const q = S.touchMode === 'pad' ? this.padPoint() : this.remoteXY(G.midLast.x, G.midLast.y); await this.clickAt(q.x, q.y, 'right');
        } else if (G.decided === 'pinch' && Math.abs(G.newK - G.k0) > 0.01) {
          this.setK(G.newK, false); this.showZoomPop(); this.rememberProfile();
        }
        G.mode = 'idle';
      } else if (G.pts.size === 0) { G.mode = 'idle'; }
    };
    touch.addEventListener('contextmenu', (e) => e.preventDefault());
    touch.addEventListener('touchend', endTouch, { passive: false });
    touch.addEventListener('touchcancel', endTouch, { passive: false });
  }
}

/** Fabriken: Desktop und Terminal laufen heute beide ueber diese Klasse (SSH ueber guacd); das native Terminal ersetzt 'terminal' spaeter. */
Sessions.registerFactory('desktop', (conn, o) => new RemoteSession(conn, o));
Sessions.registerFactory('terminal', (conn, o) => new RemoteSession(conn, o));

/* =====================================================================
 *  Tastatur (Android): ohne Autokorrektur, mit Sondertasten-Unterstuetzung.
 *  Das unsichtbare Eingabefeld gibt es nur einmal; was darin passiert, geht an die aktive Sitzung (Sessions.active).
 * ===================================================================== */
let lastNamedAt = 0;
let prevValue = '';
const kbVal = () => (el.kb.isContentEditable ? el.kb.textContent : el.kb.value);
const setKbVal = (v) => { if (el.kb.isContentEditable) el.kb.textContent = v; else el.kb.value = v; };

function applyKbMode() {
  // rich: Feld nimmt Bilder aus der Tastatur an (Sticker, GIFs, kopierte Bilder), Vorschlaege der Tastatur moeglich
  // normal: Textfeld mit Vorschlaegen. direct: Passwortfeld ohne Vorschlaege (keine Bilder).
  // In allen Modi haelt das Feld den Text, jede Aenderung (auch Autokorrektur) wird als Rueckschritte + neuer Text gesendet.
  if (kbFocused()) { el.kbInput.blur(); el.kbRich.blur(); }
  el.kb = S.kbMode === 'rich' ? el.kbRich : el.kbInput;
  if (S.kbMode !== 'rich') { const t = S.kbMode === 'direct' ? 'password' : 'text'; if (el.kbInput.type !== t) el.kbInput.type = t; }
  setKbVal(''); prevValue = '';
  for (const s of remotes()) s.updateMenuLabels();
  store.set('autoPaste', S.autoPaste); store.set('autoKb', S.autoKb);
  store.set('kbMode2', S.kbMode); store.set('touchMode', S.touchMode);
}
const NAMED = { Enter: KS.enter, Backspace: KS.backspace, Tab: KS.tab, Escape: KS.esc, ArrowLeft: KS.left, ArrowRight: KS.right,
  ArrowUp: KS.up, ArrowDown: KS.down, Delete: KS.delete, Home: KS.home, End: KS.end, PageUp: KS.pageup, PageDown: KS.pagedown, Insert: KS.insert };
for (let i = 1; i <= 12; i++) NAMED['F' + i] = KS['f' + i];   // F-Tasten gehen an die VM (F5/F11/F12 wirken dann nicht im Browser)

// Bilder aus der Tastatur (Sticker, GIFs, kopierte Bilder): hochladen und mit Strg+V einfuegen
async function handleKbFiles(files) {
  const img = [...files].find((f) => /^image\//.test(f.type));
  if (!img) return false;
  dbg('kbimage', { t: img.type, kb: Math.round(img.size / 1024) });
  try { await uploadPhoto(img); } catch (e) { toast('Bild: ' + e.message, 4200); }
  return true;
}
function onKbPaste(e) {
  const dt = e.clipboardData;
  if (dt && dt.files && dt.files.length) { e.preventDefault(); handleKbFiles(dt.files); return; }
  const txt = dt && dt.getData('text/plain');
  const s = cur();
  if (!txt || !s) return;
  e.preventDefault();
  if (Date.now() - pasteKeyAt < 800 && s.client) { s.pasteToRemote(txt, pasteKeys); return; }   // Strg+V: wie am echten Rechner einfuegen
  s.typeText(txt);                                         // Einfuegen ueber das Menue der Bildschirmtastatur: Text direkt eintippen
}
function onKbBeforeInput(e) {
  if (kbEvCount++ < 60) dbg('beforeinput', { it: e.inputType, comp: e.isComposing, len: (e.data || '').length, files: (e.dataTransfer && e.dataTransfer.files ? e.dataTransfer.files.length : 0), mode: S.kbMode });
  const files = e.dataTransfer && e.dataTransfer.files;
  if (files && files.length) { e.preventDefault(); handleKbFiles(files); }
}
function syncDiff() {
  const s = cur();
  if (!s || !s.connected) return;
  const v = kbVal();
  const a = [...prevValue], b = [...v];
  let i = 0; while (i < a.length && i < b.length && a[i] === b[i]) i++;
  for (let n = 0; n < a.length - i; n++) s.tapKey(KS.backspace);
  const add = b.slice(i).join(''); if (add) s.typeText(add);
  prevValue = v;
  if (b.length > 300) { setKbVal(''); prevValue = ''; }
}
let kbEvCount = 0;
// Umschalter, die an der echten Tastatur gerade gehalten werden (ohne die, die schon ueber die Leiste gedrueckt sind)
function heldModifiers(e, mods) {
  const m = [];
  if (e.ctrlKey && mods.ctrl === 'off') m.push(KS.ctrl);
  if (e.altKey && mods.alt === 'off') m.push(KS.alt);
  if (e.shiftKey && mods.shift === 'off') m.push(KS.shift);
  if (e.metaKey && mods.super === 'off') m.push(KS.win);
  return m;
}
let pasteKeyAt = 0, pasteKeys = [];   // Einfuegen per Tastatur: Zeitpunkt und die danach an die VM zu sendende Kombination
function wireKb(node) {
  node.addEventListener('keydown', (e) => {
    if (kbEvCount++ < 60) dbg('keydown', { key: e.key.length === 1 ? 'char' : e.key, code: e.keyCode, comp: e.isComposing });
    const s = cur();
    if (!s || !s.connected) return;
    // AltGr (auf Windows als Strg+Alt gemeldet) erzeugt Zeichen wie @ und {: die kommen ueber das Eingabefeld, nicht als Kombination
    const altGr = e.getModifierState && e.getModifierState('AltGraph');
    const held = altGr ? [] : heldModifiers(e, s.mods);
    // Einfuegen (Strg+V, Strg+Umschalt+V, Umschalt+Einfg): die Tastenkombination nicht abfangen, der Browser meldet dann das paste-Ereignis
    // mit dem Text der lokalen Zwischenablage; onKbPaste legt ihn in die Zwischenablage der VM und sendet danach die Kombination
    if (!altGr && ((e.ctrlKey && !e.altKey && !e.metaKey && e.key.toLowerCase() === 'v') || (e.shiftKey && !e.ctrlKey && !e.altKey && e.key === 'Insert'))) {
      pasteKeyAt = Date.now(); pasteKeys = [...held, e.key === 'Insert' ? KS.insert : charKeysym('v')]; return;
    }
    if (e.key in NAMED) {
      // Backspace mit Text im Feld kommt ueber das input-Ereignis (Abgleich), nur bei leerem Feld oder mit Umschaltern direkt senden
      if (e.key === 'Backspace' && kbVal().length > 0 && !held.length) return;
      e.preventDefault(); lastNamedAt = Date.now();
      if (e.key === 'Enter' || e.key === 'Tab' || (e.key === 'Backspace' && held.length)) { setKbVal(''); prevValue = ''; }
      // mit gehaltenem Shift/Strg/Alt als Kombination, sonst geht Umschalt+Pfeil (Markieren), Strg+Pfeil (Wortsprung) usw. verloren
      if (held.length) s.sendChord([...held, NAMED[e.key]], 3); else s.tapKey(NAMED[e.key]);
      return;
    }
    // Hardware-Tastatur mit Strg/Alt: als Kombination weitergeben (Umschalt bleibt erhalten, Strg+Umschalt+C)
    if ((e.ctrlKey || e.altKey || e.metaKey) && !altGr && e.key.length === 1) {
      e.preventDefault();
      s.sendChord([...held, charKeysym(e.key.toLowerCase())], 3);
    }
  });
  node.addEventListener('beforeinput', onKbBeforeInput);
  node.addEventListener('paste', onKbPaste);
  node.addEventListener('drop', (e) => { if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length) { e.preventDefault(); handleKbFiles(e.dataTransfer.files); } });
  node.addEventListener('compositionend', () => syncDiff());
  node.addEventListener('input', () => syncDiff());
  node.addEventListener('blur', () => { holdSize(); setActOnAll('keyboard', kbOpen); if (node === el.kb) { setKbVal(''); prevValue = ''; } dbg('kbblur', {}); });
  node.addEventListener('focus', () => { holdSize(); setActOnAll('keyboard', true); dbg('kbfocus', { mode: S.kbMode }); });
}
wireKb(el.kbInput); wireKb(el.kbRich);

// Desktop-Browser (Maus, echte Tastatur): Tasten gehen ohne Umweg ueber den Tastatur-Knopf an die Fernsteuerung.
// Das versteckte Eingabefeld bekommt den Fokus beim Verbinden, bei jedem Klick ins Bild und bei der ersten Taste, die sonst ins Leere ginge.
// Es gehoert immer der aktiven Sitzung: am Desktop ist das das Fenster, das den Fokus hat.
const HW_KB = matchMedia('(pointer: fine) and (hover: hover)').matches || location.search.includes('hwkb');   // ?hwkb: nur zum Testen
function focusKbForHardware() {
  const s = cur();
  if (!HW_KB || !s || !s.connected || !s.visible || kbFocused()) return;
  if (document.querySelector('dialog[open]') || !s.el.menu.hidden) return;
  try { el.kb.focus({ preventScroll: true }); } catch { el.kb.focus(); }
}
if (HW_KB) {
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Shift' || e.key === 'Control' || e.key === 'Alt' || e.key === 'Meta') return;
    const a = document.activeElement;
    if (a && a !== document.body && a !== document.documentElement) return;      // ein anderes Feld hat den Fokus: in Ruhe lassen
    focusKbForHardware();
  }, true);
}
// Falls die Tastatur ein Bild als <img> in das Feld legt: Bild holen, hochladen, Element entfernen
new MutationObserver((list) => {
  for (const m of list) for (const n of m.addedNodes) {
    const imgs = n.nodeType === 1 ? (n.tagName === 'IMG' ? [n] : [...n.querySelectorAll('img')]) : [];
    for (const im of imgs) {
      const src = im.currentSrc || im.src; im.remove();
      if (src) fetch(src).then((r) => r.blob()).then((b) => handleKbFiles([new File([b], 'tastatur-bild', { type: b.type || 'image/png' })])).catch(() => {});
    }
    if (n.nodeType === 1 && el.kbRich.children.length) el.kbRich.textContent = prevValue;
  }
}).observe(el.kbRich, { childList: true, subtree: true });

/* =====================================================================
 *  Dialoge: Text senden / Zwischenablage (gelten fuer die aktive Sitzung)
 * ===================================================================== */
document.querySelectorAll('dialog [data-act="close"]').forEach((b) => b.addEventListener('click', () => b.closest('dialog').close()));

el.typeBtn.addEventListener('click', async () => { const t = el.sendText.value; el.clipDlg.close(); const s = cur(); if (t && s) { await sleep(150); await s.typeText(t); toast('Eingetippt'); } });
el.pasteBtn.addEventListener('click', () => {
  const s = cur();
  if (!s || !s.client || !el.sendText.value) return;
  const stream = s.client.createClipboardStream('text/plain');
  const w = new Guacamole.StringWriter(stream); w.sendText(el.sendText.value); w.sendEnd();
  toast('In die Zwischenablage der VM gelegt (dort Strg+V)', 2200);
});
el.copyBtn.addEventListener('click', async () => {
  const t = el.remoteClip.value;
  try { await navigator.clipboard.writeText(t); toast('Kopiert'); }
  catch { el.remoteClip.focus(); el.remoteClip.select(); try { document.execCommand('copy'); toast('Kopiert'); } catch { toast('Bitte Text markieren und kopieren'); } }
});

/* =====================================================================
 *  Material-You-Design: Palette aus einer Grundfarbe
 * ===================================================================== */
const SWATCHES = [['#4f86f7', 'Blau'], ['#00a67e', 'Grün'], ['#e5484d', 'Rot'], ['#9a67ea', 'Violett'], ['#f5a524', 'Orange'], ['#e5599a', 'Pink'], ['#0aa5b8', 'Türkis'], ['#8a8f98', 'Grau']];
function hexToHsl(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex || ''); if (!m) return null;
  const n = parseInt(m[1], 16); const r = ((n >> 16) & 255) / 255, g = ((n >> 8) & 255) / 255, b = (n & 255) / 255;
  const mx = Math.max(r, g, b), mn = Math.min(r, g, b), l = (mx + mn) / 2, d = mx - mn; let h = 0, s = 0;
  if (d) { s = d / (1 - Math.abs(2 * l - 1)); if (mx === r) h = ((g - b) / d) % 6; else if (mx === g) h = (b - r) / d + 2; else h = (r - g) / d + 4; h *= 60; if (h < 0) h += 360; }
  return { h, s, l };
}
function systemAccent() { // Akzentfarbe des Browsers/Systems, falls verfuegbar (CSS AccentColor)
  try {
    const d = document.createElement('div'); d.style.cssText = 'position:fixed;left:-9999px;color:AccentColor'; document.body.appendChild(d);
    const c = getComputedStyle(d).color; d.remove();
    const m = c.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/); if (!m) return null;
    const hex = '#' + [m[1], m[2], m[3]].map((x) => (+x).toString(16).padStart(2, '0')).join('');
    const hsl = hexToHsl(hex); return hsl && hsl.s >= 0.2 ? hex : null;
  } catch { return null; }
}
const hsl = (h, s, l) => `hsl(${Math.round(h)} ${Math.round(s * 100)}% ${Math.round(l * 100)}%)`;
function applyTheme() {
  const dark = (S.scheme === 'auto' ? matchMedia('(prefers-color-scheme: dark)').matches : S.scheme === 'dark');
  const sys = S.seed === 'system' ? systemAccent() : null;
  const c = hexToHsl(S.seed === 'system' ? (sys || '#4f86f7') : S.seed) || hexToHsl('#4f86f7');
  const h = c.h, sp = clamp(c.s, 0.45, 0.85), st = document.documentElement.style, set = (k, v) => st.setProperty(k, v);
  if (dark) {
    set('--md-surface', hsl(h, 0.14, 0.08)); set('--md-surface-container', hsl(h, 0.14, 0.12)); set('--md-surface-high', hsl(h, 0.14, 0.17));
    set('--md-on-surface', hsl(h, 0.15, 0.9)); set('--md-on-variant', hsl(h, 0.1, 0.72)); set('--md-outline', hsl(h, 0.08, 0.32));
    set('--md-primary', hsl(h, sp, 0.78)); set('--md-on-primary', hsl(h, sp, 0.18)); set('--md-primary-container', hsl(h, sp * 0.9, 0.3)); set('--md-on-primary-container', hsl(h, sp, 0.9));
    set('--md-secondary-container', hsl(h, 0.2, 0.25)); set('--md-on-secondary-container', hsl(h, 0.3, 0.9));
    set('--md-error', '#ffb4ab'); set('--md-error-container', '#93000a');
  } else {
    set('--md-surface', hsl(h, 0.25, 0.98)); set('--md-surface-container', hsl(h, 0.22, 0.94)); set('--md-surface-high', hsl(h, 0.22, 0.9));
    set('--md-on-surface', hsl(h, 0.15, 0.11)); set('--md-on-variant', hsl(h, 0.1, 0.35)); set('--md-outline', hsl(h, 0.1, 0.7));
    set('--md-primary', hsl(h, sp, 0.4)); set('--md-on-primary', '#ffffff'); set('--md-primary-container', hsl(h, clamp(sp + 0.2, 0, 1), 0.9)); set('--md-on-primary-container', hsl(h, sp, 0.15));
    set('--md-secondary-container', hsl(h, 0.3, 0.88)); set('--md-on-secondary-container', hsl(h, 0.3, 0.15));
    set('--md-error', '#ba1a1a'); set('--md-error-container', '#ffdad6');
  }
  st.colorScheme = dark ? 'dark' : 'light';
  const meta = document.querySelector('meta[name=theme-color]');
  if (meta) meta.setAttribute('content', getComputedStyle(document.documentElement).getPropertyValue('--md-surface').trim());
  dbg('theme', { seed: S.seed === 'system' ? 'system' : S.seed, sys: sys || 'none', dark });
}
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { if (S.scheme === 'auto') applyTheme(); });

function openThemeDialog() {
  el.swatches.replaceChildren();
  const sys = document.createElement('div'); sys.className = 'sw sys' + (S.seed === 'system' ? ' sel' : ''); sys.textContent = 'System';
  sys.addEventListener('click', () => { S.seed = 'system'; store.set('seed', S.seed); applyTheme(); openThemeDialog(); });
  el.swatches.appendChild(sys);
  for (const [hex, name] of SWATCHES) {
    const s = document.createElement('div'); s.className = 'sw' + (S.seed.toLowerCase() === hex ? ' sel' : ''); s.style.background = hex; s.title = name;
    s.addEventListener('click', () => { S.seed = hex; store.set('seed', S.seed); applyTheme(); openThemeDialog(); });
    el.swatches.appendChild(s);
  }
  el.schemeSel.value = S.scheme;
  if (/^#/.test(S.seed)) el.seedInput.value = S.seed;
  el.themeDlg.showModal();
}
async function seedFromImage(file) {
  // wie Material You: aus dem Bild die haeufigste, kraeftige Farbe waehlen
  const bmp = await createImageBitmap(file);
  const sc = Math.min(1, 96 / Math.max(bmp.width, bmp.height));
  const c = document.createElement('canvas'); c.width = Math.max(1, Math.round(bmp.width * sc)); c.height = Math.max(1, Math.round(bmp.height * sc));
  const ctx = c.getContext('2d', { willReadFrequently: true }); ctx.drawImage(bmp, 0, 0, c.width, c.height);
  const d = ctx.getImageData(0, 0, c.width, c.height).data;
  const B = Array.from({ length: 36 }, () => ({ w: 0, r: 0, g: 0, b: 0 }));
  for (let i = 0; i < d.length; i += 4) {
    const hx = '#' + [d[i], d[i + 1], d[i + 2]].map((x) => x.toString(16).padStart(2, '0')).join('');
    const o = hexToHsl(hx); if (!o || o.s < 0.2 || o.l < 0.15 || o.l > 0.9) continue;
    const w = o.s * (0.4 + (1 - Math.abs(2 * o.l - 1)));
    const k = Math.min(35, Math.floor(o.h / 10)); B[k].w += w; B[k].r += d[i] * w; B[k].g += d[i + 1] * w; B[k].b += d[i + 2] * w;
  }
  let best = -1, bw = 0;
  for (let k = 0; k < 36; k++) { const w = B[k].w + 0.5 * (B[(k + 35) % 36].w + B[(k + 1) % 36].w); if (w > bw) { bw = w; best = k; } }
  if (best < 0 || B[best].w <= 0) return null;
  const q = B[best];
  return '#' + [q.r, q.g, q.b].map((x) => Math.round(x / q.w).toString(16).padStart(2, '0')).join('');
}
el.imgSeedBtn.addEventListener('click', () => { el.imgSeedInput.value = ''; el.imgSeedInput.click(); });
el.imgSeedInput.addEventListener('change', async () => {
  const f = el.imgSeedInput.files && el.imgSeedInput.files[0]; if (!f) return;
  try {
    const hex = await seedFromImage(f);
    if (!hex) { toast('Im Bild keine kräftige Farbe gefunden', 3000); return; }
    S.seed = hex; store.set('seed', hex); applyTheme(); openThemeDialog(); toast('Farbe aus Bild übernommen', 2000); dbg('seedimg', { c: hex });
  } catch (e) { toast('Bild konnte nicht gelesen werden', 3000); }
});
el.seedInput.addEventListener('input', () => { S.seed = el.seedInput.value; store.set('seed', S.seed); applyTheme(); });
el.schemeSel.addEventListener('change', () => { S.scheme = el.schemeSel.value; store.set('scheme', S.scheme); applyTheme(); });
/** Fuer die Handy-Oberflaeche (mobile.js): Farbe und Hell/Dunkel setzen, speichern und anwenden. */
export function setTheme({ seed, scheme }) { if (seed) { S.seed = seed; store.set('seed', seed); } if (scheme) { S.scheme = scheme; store.set('scheme', scheme); } applyTheme(); }
/* =====================================================================
 *  Foto einfuegen: hochladen, Helfer legt es in die Zwischenablage der VM-Sitzung, dann Strg+V
 * ===================================================================== */
function pickPhoto() { el.photoInput.value = ''; el.photoInput.click(); }

/* Bild-Dialog: Galerie/Kamera, echte Zwischenablage (nur mit HTTPS), oder Einfuegen ins Feld (Gboard, langes Tippen) */
const photoDlg = $('#photoDlg'), pasteBox = $('#pasteBox');
function openPhotoDlg() {
  pasteBox.textContent = '';
  $('#photoClip').hidden = !(window.isSecureContext && navigator.clipboard && navigator.clipboard.read);
  photoDlg.showModal();
}
/* Eine Taste: Bild aus der Zwischenablage senden (Bild kopieren, dann tippen). Ohne HTTPS oder ohne Erlaubnis: Dialog. */
async function pasteImageFromClipboard() {
  if (!(window.isSecureContext && navigator.clipboard && navigator.clipboard.read)) { openPhotoDlg(); toast('Ohne HTTPS nur über das Einfügefeld', 3200); return; }
  try {
    const items = await navigator.clipboard.read();
    dbg('clipread', { n: items.length, types: items.map((i) => i.types.join('+')).join(',').slice(0, 80) });
    for (const item of items) {
      const type = item.types.find((t) => t.startsWith('image/'));
      if (type) { const b = await item.getType(type); await handleKbFiles([new File([b], 'zwischenablage', { type })]); return; }
    }
    // Die Google-Tastatur legt kopierte Bilder so ab, dass Chrome nur Text sieht: dann gleich die Galerie anbieten, statt nichts zu tun
    toast('Kein Bild in der Zwischenablage. Wähle eines aus der Galerie.', 3200);
    pickPhoto();
  } catch (e) { dbg('clipread-error', { n: e.name }); openPhotoDlg(); toast('Zwischenablage nicht erlaubt, bitte Einfügefeld nutzen', 3200); }
}
async function sendPhotoFiles(files) {
  const ok = await handleKbFiles(files);
  if (ok && photoDlg.open) photoDlg.close();
  return ok;
}
$('#photoPick').addEventListener('click', () => { photoDlg.close(); pickPhoto(); });
$('#photoClip').addEventListener('click', async () => {
  try {
    for (const item of await navigator.clipboard.read()) {
      const type = item.types.find((t) => t.startsWith('image/'));
      if (type) { const b = await item.getType(type); await sendPhotoFiles([new File([b], 'zwischenablage', { type })]); return; }
    }
    toast('In der Zwischenablage ist kein Bild', 2600);
  } catch (e) { toast('Zwischenablage nicht lesbar: ' + (e.message || e.name), 3600); }
});
pasteBox.addEventListener('paste', (e) => {
  const dt = e.clipboardData; dbg('pastebox', { files: dt && dt.files ? dt.files.length : 0, types: dt ? [...dt.types].join(',').slice(0, 60) : '' });
  if (dt && dt.files && dt.files.length) { e.preventDefault(); sendPhotoFiles(dt.files); }
});
pasteBox.addEventListener('beforeinput', (e) => {
  const f = e.dataTransfer && e.dataTransfer.files;
  dbg('pastebox-input', { it: e.inputType, files: f ? f.length : 0 });
  if (f && f.length) { e.preventDefault(); sendPhotoFiles(f); }
});
pasteBox.addEventListener('drop', (e) => { const f = e.dataTransfer && e.dataTransfer.files; if (f && f.length) { e.preventDefault(); sendPhotoFiles(f); } });
// Tastaturen, die ein Bild als <img> in das Feld legen
new MutationObserver((list) => {
  for (const m of list) for (const n of m.addedNodes) {
    const imgs = n.nodeType === 1 ? (n.tagName === 'IMG' ? [n] : [...n.querySelectorAll('img')]) : [];
    for (const im of imgs) {
      const src = im.currentSrc || im.src;
      if (src) fetch(src).then((r) => r.blob()).then((b) => sendPhotoFiles([new File([b], 'tastatur-bild', { type: b.type || 'image/png' })])).catch(() => toast('Bild konnte nicht gelesen werden', 3000));
    }
  }
}).observe(pasteBox, { childList: true, subtree: true });
el.photoInput.addEventListener('change', async () => {
  const f = el.photoInput.files && el.photoInput.files[0];
  if (!f) return;
  try { await uploadPhoto(f); } catch (e) { toast('Foto: ' + e.message, 4200); dbg('photo-error', { m: String(e.message).slice(0, 20) }); }
});
async function uploadPhoto(file) {
  const s = cur();
  if (!s || !s.connected) throw new Error('nicht verbunden');
  toast('Foto wird vorbereitet …', 6000);
  let blob = file;
  if (!(file.type === 'image/png' && file.size < 5e6) && file.type !== 'image/gif') {
    // grosse Handyfotos auf max. 2000 px verkleinern und als JPEG senden
    const bmp = await createImageBitmap(file);
    const sc = Math.min(1, 2000 / Math.max(bmp.width, bmp.height));
    const c = document.createElement('canvas'); c.width = Math.max(1, Math.round(bmp.width * sc)); c.height = Math.max(1, Math.round(bmp.height * sc));
    c.getContext('2d').drawImage(bmp, 0, 0, c.width, c.height);
    blob = await new Promise((res) => c.toBlob(res, 'image/jpeg', 0.9));
    if (!blob) throw new Error('Bild konnte nicht umgewandelt werden');
  }
  toast('Foto wird hochgeladen …', 6000);
  const r = await fetch('/api/upload', { method: 'POST', headers: { 'Content-Type': blob.type || 'image/jpeg', 'X-JulOS': '1' }, body: blob });
  if (!r.ok) { const j = await r.json().catch(() => ({})); throw new Error(j.error || 'Fehler ' + r.status); }
  dbg('photo', { kb: Math.round(blob.size / 1024), auto: S.autoPaste });
  if (S.autoPaste) { await sleep(250); await s.sendChord([KS.ctrl, charKeysym('v')]); toast('Foto eingefügt (Strg+V)'); }
  else toast('Foto liegt in der Zwischenablage der VM – dort Strg+V', 3500);
}
/* =====================================================================
 *  Passwort per Passwortmanager (Bitwarden) holen und in die VM tippen
 * ===================================================================== */
function openPwDialog() {
  el.pwUser.value = ''; el.pwPass.value = '';
  el.pwDlg.showModal(); setTimeout(() => el.pwPass.focus(), 200);
}
async function typePw(both) {
  const u = el.pwUser.value, p = el.pwPass.value;
  if (!p) { toast('Kein Passwort im Feld'); return; }
  el.pwUser.value = ''; el.pwPass.value = ''; el.pwDlg.close();
  await sleep(250);
  const s = cur(); if (!s) return;
  if (both) { if (u) { await s.typeText(u); await s.tapKey(KS.tab); } await s.typeText(p); await s.tapKey(KS.enter); }
  else await s.typeText(p);
  toast('Eingetippt');
}
el.pwTypePass.addEventListener('click', () => typePw(false));
el.pwTypeBoth.addEventListener('click', () => typePw(true));
el.pwDlg.addEventListener('close', () => { el.pwUser.value = ''; el.pwPass.value = ''; });

/* =====================================================================
 *  Start
 * ===================================================================== */
window.addEventListener('beforeunload', () => { for (const s of Sessions.list()) { try { s.disconnect(); } catch { /* egal */ } } });
// Handy gesperrt oder App im Hintergrund: die Verbindung ist danach meist tot, das Bild aber noch da. Beim Zurueckkommen sofort neu verbinden
// (nach laengerer Pause) oder, wenn schon getrennt, ebenfalls. Kurzes Wegwischen (unter 4 s) laesst die Verbindung in Ruhe. Gilt je Sitzung.
let hiddenAt = 0;
document.addEventListener('visibilitychange', () => {
  if (document.hidden) { hiddenAt = Date.now(); for (const s of remotes()) s.applyPause(); return; }
  const away = hiddenAt ? Date.now() - hiddenAt : 0;
  hiddenAt = 0;
  for (const s of remotes()) s.onPageVisible(away);
});

(async function init() {
  renderIcons(); applyTheme(); applyKbMode();
})();


/* Ripple-Effekt (Material) fuer alle antippbaren Elemente */
document.addEventListener('pointerdown', (e) => {
  const t = e.target.closest ? e.target.closest('.hb, .k, .mi, button') : null;
  if (!t) return;
  const r = t.getBoundingClientRect(); const s = Math.max(r.width, r.height) * 2;
  const d = document.createElement('span'); d.className = 'rip';
  d.style.width = d.style.height = s + 'px'; d.style.left = (e.clientX - r.left - s / 2) + 'px'; d.style.top = (e.clientY - r.top - s / 2) + 'px';
  t.appendChild(d); setTimeout(() => d.remove(), 600);
}, { passive: true });

// nur mit ?debug in der Adresse: Zugriff fuer Tests
if (location.search.includes('debug')) window.__pd = { Sessions, S, rows: () => rows, remotes, cur, cursorInfo, openPhotoDlg, ico };

/* ---------- PWA: Service Worker und Installation ---------- */
let installEvent = null;
window.addEventListener('beforeinstallprompt', (e) => { e.preventDefault(); installEvent = e; });
window.addEventListener('appinstalled', () => { installEvent = null; toast('Installiert'); });
async function installApp() {
  if (installEvent) { installEvent.prompt(); try { await installEvent.userChoice; } catch { /* egal */ } installEvent = null; return; }
  if (window.matchMedia('(display-mode: standalone)').matches || window.matchMedia('(display-mode: fullscreen)').matches) { toast('Läuft schon als App'); return; }
  if (!window.isSecureContext) toast('Zum Installieren braucht die Seite HTTPS (zum Beispiel über Caddy).', 4200);
  else toast('Im Browser-Menü „App installieren“ oder „Zum Startbildschirm“ wählen.', 4200);
}
if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('/sw.js').catch(() => {});
