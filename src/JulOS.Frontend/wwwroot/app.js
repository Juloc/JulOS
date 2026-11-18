import Guacamole from '/lib/guacamole-common.min.js';

/* =====================================================================
 * JulOS – Client
 *  - Verbindung (guacamole-common-js, RDP ueber guacd)
 *  - dynamische Aufloesung (Fenstergroesse, Tastatur, Drehen, Zoom)
 *  - Touch: Tippen, Langdruecken, Ziehen, Zwei-Finger-Scrollen, Pinch-Zoom
 *  - Android-Tastatur ohne Autokorrektur + eigene, anpassbare Sondertasten
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
  backspace: 'M22 3H7c-.69 0-1.23.35-1.59.88L0 12l5.41 8.11c.36.53.9.89 1.59.89h15c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-3 12.59L17.59 17 14 13.41 10.41 17 9 15.59 12.59 12 9 8.41 10.41 7 14 10.59 17.59 7 19 8.41 15.41 12 19 15.59z',
};
export const ico = (name) => `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${ICONS[name] || ''}"/></svg>`;
function renderIcons(root = document) { root.querySelectorAll('[data-ic]').forEach((n) => { n.innerHTML = ico(n.dataset.ic); }); }

/* ---------- Elemente ---------- */
const el = {
  remote: $('#remote'), stage: $('#stage'), display: $('#display'), touch: $('#touchLayer'), toast: $('#toast'),
  overlay: $('#overlay'), overlayMsg: $('#overlayMsg'), reconnectBtn: $('#reconnectBtn'), toLoginBtn: $('#toLoginBtn'),
  dock: $('#dock'), topbar: $('#topbar'), zoomPop: $('#zoomPop'),
  kb: $('#kb'), kbInput: $('#kb'), kbRich: $('#kbRich'),
  menuSheet: $('#menuSheet'), menuScrim: $('#menuScrim'), spinner: $('#spinner'), imgSeedBtn: $('#imgSeedBtn'), imgSeedInput: $('#imgSeedInput'), kbModeLabel: $('#kbModeLabel'), touchModeLabel: $('#touchModeLabel'), autoKbLabel: $('#autoKbLabel'),
  clipDlg: $('#clipDlg'), sendText: $('#sendText'), remoteClip: $('#remoteClip'), typeBtn: $('#typeBtn'), pasteBtn: $('#pasteBtn'), copyBtn: $('#copyBtn'),
  keyDlg: $('#keyDlg'), keyDlgTitle: $('#keyDlgTitle'), keyLabel: $('#keyLabel'), keyType: $('#keyType'), keyValue: $('#keyValue'),
  keyValueLabel: $('#keyValueLabel'), keyHint: $('#keyHint'), keyLeft: $('#keyLeft'), keyRight: $('#keyRight'),
  keySave: $('#keySave'), keyDelete: $('#keyDelete'),
  photoInput: $('#photoInput'), autoPasteLabel: $('#autoPasteLabel'),
  themeDlg: $('#themeDlg'), swatches: $('#swatches'), seedInput: $('#seedInput'), schemeSel: $('#schemeSel'),
  pwDlg: $('#pwDlg'), pwUser: $('#pwUser'), pwPass: $('#pwPass'), pwTypePass: $('#pwTypePass'), pwTypeBoth: $('#pwTypeBoth'),
};

/* ---------- Zustand ---------- */
const S = {
  client: null, tunnel: null, display: null, mouse: null,
  password: null,            // nur im Arbeitsspeicher dieser Seite
  connected: false, everConnected: false, intentional: false,
  k: clamp(store.get('k', 0.8), 0.35, 1.8),   // Remote-Pixel pro Handy-Pixel (kleiner = Inhalt groesser)
  kbMode: store.get('kbMode2', 'rich'),       // 'rich' (Bilder aus der Tastatur) | 'normal' (mit Vorschlaegen) | 'direct' (ohne Vorschlaege)
  touchMode: store.get('touchMode', 'direct'),// 'direct' (Tippen = Klick an der Stelle) | 'pad' (Touchpad)
  lastSent: { w: 0, h: 0 },
  retry: 0, retryTimer: null, resizeTimer: null, toastTimer: null,
  expanded: store.get('expanded', false), actOn: {}, zoomTimer: null, editing: false,
  autoKb: store.get('autoKb', true),   // Tastatur oeffnet sich beim Tippen auf ein Textfeld (Erkennung am Mauszeiger)
  debug: store.get('debug', true),     // sendet nur Messwerte (Groessen, Ereignisarten), nie getippten Text
  cursor: { ibeam: false },
  autoPaste: store.get('autoPaste', true),   // Foto nach dem Hochladen sofort mit Strg+V einfuegen
  seed: store.get('seed', 'system'),         // Grundfarbe fuer das Material-You-Design ('system' oder #rrggbb)
  scheme: store.get('scheme', 'auto'),       // 'auto' | 'light' | 'dark'
};

/* ---------- Toast / kleine Helfer ---------- */
function toast(msg, ms = 1400) {
  el.toast.textContent = msg; el.toast.classList.add('show');
  clearTimeout(S.toastTimer); S.toastTimer = setTimeout(() => el.toast.classList.remove('show'), ms);
}

/* ---------- Diagnose: sendet nur Messwerte an das Server-Log (keine Eingaben) ---------- */
const dbgQueue = [];
let dbgTimer = null;
function dbg(evt, data = {}) {
  if (!S.debug || !S.conn) return;     // Messwerte nur waehrend einer Verbindung (davor gibt es noch keine Sitzung)
  dbgQueue.push({ t: Math.round(performance.now()), evt, ...data });
  if (!dbgTimer) dbgTimer = setTimeout(() => {
    dbgTimer = null;
    const events = dbgQueue.splice(0, 40);
    fetch('/api/log', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-JulOS': '1' }, body: JSON.stringify({ events }), keepalive: true }).catch(() => {});
  }, 2500);
}

/* ---------- Sichtbarer Bereich (Tastatur, Vollbild) und Tastatur-Erkennung ---------- */
const vv = window.visualViewport;
let baseH = vv ? vv.height : window.innerHeight;
let lastW = vv ? vv.width : window.innerWidth;
let kbOpen = false;
const kbFocused = () => document.activeElement === el.kbInput || document.activeElement === el.kbRich;
function layoutViewport() {
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
    // Tastatur per Zurueck-Geste geschlossen, Feld hat aber noch den Fokus: Fokus loesen, damit der Zustand stimmt
    if (!open && kbFocused()) el.kb.blur();
    setActOn('keyboard', open);
    dbg('kbvisible', { open, vvh: Math.round(h), base: Math.round(baseH), inner: window.innerHeight });
  }
}
if (vv) { vv.addEventListener('resize', layoutViewport); vv.addEventListener('scroll', layoutViewport); }
window.addEventListener('resize', layoutViewport);
window.addEventListener('orientationchange', () => setTimeout(layoutViewport, 300));
layoutViewport();

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

const mods = { ctrl: 'off', alt: 'off', shift: 'off', super: 'off' };
const kdown = (s) => S.client && S.client.sendKeyEvent(1, s);
const kup = (s) => S.client && S.client.sendKeyEvent(0, s);

function setMod(name, mode) {
  const prev = mods[name];
  if (prev === mode) return;
  if (prev === 'off') kdown(MODSYM[name]);
  if (mode === 'off') kup(MODSYM[name]);
  mods[name] = mode;
  refreshModButtons();
}
const cycleMod = (name) => setMod(name, mods[name] === 'off' ? 'once' : mods[name] === 'once' ? 'lock' : 'off');
function consumeOnce() { for (const n of Object.keys(mods)) if (mods[n] === 'once') setMod(n, 'off'); }
function resetMods() { for (const n of Object.keys(mods)) mods[n] = 'off'; refreshModButtons(); }

// Alle Tasten laufen strikt nacheinander durch eine Warteschlange (sonst vertauscht sich die Reihenfolge,
// zum Beispiel bei "Backspace x3, dann neues Wort" nach einer Autokorrektur).
let keyChain = Promise.resolve();
const enqueue = (fn) => { keyChain = keyChain.then(fn).catch(() => {}); return keyChain; };
const sendChord = (syms) => enqueue(async () => {
  for (const s of syms) { kdown(s); await sleep(12); }
  for (const s of [...syms].reverse()) { kup(s); await sleep(12); }
  consumeOnce();
});
const tapKey = (sym) => enqueue(async () => { kdown(sym); await sleep(8); kup(sym); consumeOnce(); });
const typeText = (str) => enqueue(async () => {
  for (const ch of str) {
    const sym = charKeysym(ch);
    kdown(sym); await sleep(6); kup(sym); await sleep(4);
    consumeOnce();
  }
});

/* =====================================================================
 *  Leisten-Zeilen: Tasten (Schnell, Mehr), je einklappbar und bearbeitbar; dazu die Fensterleiste
 * ===================================================================== */
const ITEM = (label, type, value) => ({ label, type, value });
const ACT = (icon, action) => ITEM(':' + icon, 'action', action);
const DEFAULT_ROWS = [
  { id: 'more', title: 'Mehr', open: false, items: [
    ITEM('⇧Tab', 'chord', 'shift+tab'), ITEM('Bild↑', 'chord', 'pageup'), ITEM('Bild↓', 'chord', 'pagedown'), ITEM('Pos1', 'chord', 'home'),
    ITEM('Ende', 'chord', 'end'), ITEM('Entf', 'chord', 'delete'), ITEM('Strg+C', 'chord', 'ctrl+c'), ITEM('Strg+V', 'chord', 'ctrl+v'),
    ITEM('Strg+X', 'chord', 'ctrl+x'), ITEM('Strg+A', 'chord', 'ctrl+a'), ITEM('Strg+Z', 'chord', 'ctrl+z'), ITEM('Strg+F', 'chord', 'ctrl+f'),
    ITEM('Alt+Tab', 'chord', 'alt+tab'), ITEM('Strg+⇧+T', 'chord', 'ctrl+shift+t'),
    ACT('image', 'photo'), ACT('paste', 'clip'), ACT('key', 'password') ] },
  { id: 'quick', title: 'Tasten', open: true, items: [
    ITEM('Alt', 'mod', 'alt'), ITEM('Shift', 'mod', 'shift'), ACT('add_photo', 'pasteimg'),
    ITEM('←', 'chord', 'left'), ITEM('↑', 'chord', 'up'), ITEM('↓', 'chord', 'down'), ITEM('→', 'chord', 'right'),
    ITEM(':ret', 'chord', 'enter'), ITEM(':backspace', 'chord', 'backspace') ] },
  // die Leiste: immer sichtbar, eine Zeile, ganz unten
  { id: 'bar', title: 'Leiste', fixed: true, items: [
    ITEM('Esc', 'chord', 'esc'), ITEM('Strg', 'mod', 'ctrl'), ITEM('Tab', 'chord', 'tab'),
    ACT('zoom_out', 'zoomout'), ACT('zoom_in', 'zoomin'), ACT('fullscreen', 'fullscreen'), ACT('more', 'menu'),
    ACT('expand_less', 'expand'), ACT('keyboard', 'keyboard') ] },
];
const clone = (o) => JSON.parse(JSON.stringify(o));
function loadRows() {
  const saved = store.get('rows3', null);
  if (Array.isArray(saved) && saved.some((r) => r.fixed)) {
    // neue Standardtaste nachtragen, ohne eigene Anpassungen zu verlieren
    const has = saved.some((r) => r.items.some((i) => i.type === 'action' && i.value === 'pasteimg'));
    if (!has) { const q = saved.find((r) => r.id === 'quick') || saved.find((r) => !r.fixed); if (q) q.items.splice(Math.min(2, q.items.length), 0, ACT('add_photo', 'pasteimg')); }
    return saved;
  }
  return clone(DEFAULT_ROWS);
}
let rows = loadRows();
let suppressClick = false;
let editRow = 0, editIndex = -1;      // editIndex -1 = neue Taste
const saveRows = () => store.set('rows3', rows);

function refreshModButtons() {
  document.querySelectorAll('#rowsTop .k[data-mod], #rowsBottom .k[data-mod]').forEach((b) => {
    const m = mods[b.dataset.mod];
    b.classList.toggle('mod-once', m === 'once');
    b.classList.toggle('mod-lock', m === 'lock');
  });
}

async function activateKey(def) {
  try {
    if (def.type === 'mod') {
      const name = MODNAME[String(def.value).toLowerCase()];
      if (!name) throw new Error('Unbekannter Umschalter');
      cycleMod(name);
    } else if (def.type === 'text') {
      await typeText(def.value || '');
    } else if (def.type === 'action') {
      const v = String(def.value).toLowerCase();
      const f = ACTIONS[v];
      if (!f) throw new Error('Unbekannte Aktion');
      f();
    } else {
      await sendChord(parseChord(def.value));
    }
  } catch (e) { toast(e.message); }
}

function keyEl(def, r, i) {
  const b = document.createElement('div');
  b.setAttribute('role', 'button'); b.className = 'k' + (S.editing ? ' edit' : '');
  const isIcon = String(def.label).startsWith(':');
  let icon = isIcon ? def.label.slice(1) : '';
  if (def.type === 'action') {
    const act = String(def.value).toLowerCase();
    b.dataset.act = act;
    if (act === 'expand') icon = S.expanded ? 'expand_more' : 'expand_less';   // Pfeil zeigt, was der naechste Tipp tut
    if (S.actOn[act]) b.classList.add('on');
  }
  if (isIcon && ICONS[icon]) { b.innerHTML = ico(icon); b.classList.add('ic'); } else b.textContent = def.label;
  b.dataset.r = String(r); b.dataset.i = String(i);
  if (def.type === 'mod') b.dataset.mod = MODNAME[String(def.value).toLowerCase()] || '';
  b.addEventListener('click', () => {
    if (suppressClick) { suppressClick = false; return; }
    if (S.editing) openKeyEditor(r, i); else activateKey(def);
  });
  if (S.editing) enableDrag(b);
  return b;
}

function renderRows() {
  const top = $('#rowsTop'), bottom = $('#rowsBottom');
  top.replaceChildren(); bottom.replaceChildren();
  rows.forEach((row, r) => {
    const fixed = !!row.fixed;
    const open = S.editing || fixed ? true : row.open;
    const wrap = document.createElement('div');
    wrap.className = 'row' + (open ? '' : ' collapsed') + (fixed ? ' bar' : '') + (S.editing ? ' editing' : '');
    const rk = document.createElement('div'); rk.className = 'rk'; rk.dataset.r = String(r);
    row.items.forEach((def, i) => rk.appendChild(keyEl(def, r, i)));
    if (S.editing) {
      const add = document.createElement('div'); add.setAttribute('role', 'button'); add.className = 'k add'; add.textContent = '＋ Taste';
      add.addEventListener('click', () => openKeyEditor(r, -1)); rk.appendChild(add);
      if (r === rows.length - 1) {
        const done = document.createElement('div'); done.setAttribute('role', 'button'); done.className = 'k'; done.textContent = '✓ Fertig';
        done.addEventListener('click', () => { S.editing = false; renderRows(); toast('Tasten gespeichert'); }); rk.appendChild(done);
      }
    }
    if (fixed) wrap.append(rk);
    else {
      const rh = document.createElement('div'); rh.className = 'rh'; rh.setAttribute('role', 'button');
      rh.innerHTML = ico(open ? 'expand_more' : 'chevron_right') + `<span>${row.title}</span>`;
      rh.addEventListener('click', () => { if (S.editing) return; row.open = !row.open; saveRows(); renderRows(); });
      wrap.append(rh, rk);
    }
    (fixed ? bottom : top).appendChild(wrap);
  });
  applyKeysOpen(); refreshModButtons();
}
const renderKeys = renderRows;

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
    renderRows();
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
  saveRows(); el.keyDlg.close(); renderRows();
});
el.keyDelete.addEventListener('click', () => { if (editIndex >= 0) { rows[editRow].items.splice(editIndex, 1); saveRows(); el.keyDlg.close(); renderRows(); } });
const moveKey = (d) => {
  const items = rows[editRow].items, j = editIndex + d;
  if (editIndex < 0 || j < 0 || j >= items.length) return;
  const [m] = items.splice(editIndex, 1); items.splice(j, 0, m); editIndex = j; saveRows(); renderRows(); toast('Verschoben');
};
el.keyLeft.addEventListener('click', () => moveKey(-1));
el.keyRight.addEventListener('click', () => moveKey(1));

/* ----- Aufklappen: die Leiste bleibt, darueber erscheinen die weiteren Zeilen ----- */
function applyKeysOpen() { $('#rowsTop').hidden = !(S.expanded || S.editing); }
function toggleExpanded() {
  if (S.editing) return;
  S.expanded = !S.expanded; store.set('expanded', S.expanded); renderRows();
}
function setActOn(name, on) {
  S.actOn[name] = on;
  document.querySelectorAll(`.k[data-act="${name}"]`).forEach((b) => b.classList.toggle('on', on));
}
function toggleFullscreen() {
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  else document.documentElement.requestFullscreen({ navigationUI: 'hide' }).catch(() => toast('Vollbild nicht möglich'));
}
/* Aktionen, die eine Taste in der Leiste ausloesen kann (frei zuordenbar im Tasten-Editor) */
const ACTIONS = {
  keyboard: () => { if (kbOpen) el.kb.blur(); else { el.kb.blur(); el.kb.focus(); } },
  zoomin: () => { setK(S.k / 1.12); rememberProfile(); },
  zoomout: () => { setK(S.k * 1.12); rememberProfile(); },
  fullscreen: toggleFullscreen,
  menu: () => (el.menuSheet.hidden ? openMenu() : closeMenu()),
  expand: toggleExpanded,
  photo: () => openPhotoDlg(),
  pasteimg: () => pasteImageFromClipboard(),
  clip: () => el.clipDlg.showModal(),
  password: () => openPwDialog(),
};

/* Tippen auf Dock-Knoepfe darf den Fokus (und damit die Tastatur) nicht wegnehmen */
el.dock.addEventListener('mousedown', (e) => { if (e.target.closest('.hb, .k, .chip, .rh, button')) e.preventDefault(); });

/* =====================================================================
 *  Fensterleiste und Programm-Modus (braucht den Agenten auf dem Zielrechner)
 * ===================================================================== */
const W = { available: false, list: [], mode: 'desktop', timer: null, lastCls: null, apps: null };
const profiles = store.get('profiles', {});
const defaultProfile = (cls) => (/claude/i.test(cls || '') ? 0.56 : null);   // Claude schmal: dann zeigt die App Burger-Menue statt Seitenleiste

async function agent(path, method = 'GET', body = null) {
  const r = await fetch('/api/agent' + path, { method, headers: { 'Content-Type': 'application/json', 'X-JulOS': '1' }, body: body ? JSON.stringify(body) : undefined });
  if (!r.ok) throw new Error('Agent ' + r.status);
  return r.json();
}
async function startWindowBar() {
  stopWindowBar();
  W.available = false;
  // Der Agent gehoert zur Maschine, neben der der Server laeuft: nur fuer Desktop-Verbindungen anbieten
  if (S.conn && S.conn.kind === 'desktop') { try { const h = await (await fetch('/api/agent/health')).json(); W.available = !!h.available; } catch { W.available = false; } }
  dbg('agent', { available: W.available });
  renderWindows();
  if (!W.available) return;
  pollWindows(); W.timer = setInterval(pollWindows, 2000);
}
function stopWindowBar() { clearInterval(W.timer); W.timer = null; W.list = []; W.lastCls = null; }
async function pollWindows() {
  if (!S.connected || !W.available || document.hidden) return;
  try {
    const d = await agent('/windows');
    W.list = d.windows || []; W.mode = d.mode || 'desktop';
    renderWindows(); applyProfile();
  } catch { /* naechster Versuch */ }
}
function applyProfile() {
  if (W.mode !== 'apps') { W.lastCls = null; return; }
  const act = W.list.find((w) => w.active); const cls = act ? act.class : null;
  if (!cls || cls === W.lastCls) return;
  W.lastCls = cls;
  const k = profiles[cls] ?? defaultProfile(cls);
  if (k && Math.abs(k - S.k) > 0.02) { setK(k, false); toast('Anzeige für ' + (act.app || cls) + ' angepasst', 1800); }
}
function rememberProfile() {                               // Zoom von Hand: fuer das aktive Programm merken (nur im Programm-Modus)
  if (W.mode !== 'apps' || !W.lastCls) return;
  profiles[W.lastCls] = S.k; store.set('profiles', profiles);
}
function renderWindows() {
  const row = $('#rowWin');
  if (!W.available) { el.topbar.hidden = true; return; }
  el.topbar.hidden = false; row.replaceChildren();
  const mode = document.createElement('div'); mode.className = 'chip mode'; mode.setAttribute('role', 'button');
  mode.innerHTML = ico(W.mode === 'apps' ? 'apps' : 'desktop') + `<span>${W.mode === 'apps' ? 'Apps' : 'Desktop'}</span>`;
  mode.addEventListener('click', toggleMode); row.appendChild(mode);
  for (const w of W.list) {
    const c = document.createElement('div'); c.className = 'chip' + (w.active ? ' active' : '') + (w.minimized ? ' min' : ''); c.setAttribute('role', 'button');
    const img = w.icon ? `<img alt="" src="data:image/png;base64,${w.icon}">` : '';
    const t = document.createElement('span'); t.textContent = (w.title || w.app || '?').slice(0, 40);
    c.innerHTML = img; c.appendChild(t);
    let timer = null, longDone = false;
    c.addEventListener('pointerdown', () => { longDone = false; timer = setTimeout(() => { longDone = true; openWinSheet(w); }, 480); });
    const cancel = () => clearTimeout(timer);
    c.addEventListener('pointerup', cancel); c.addEventListener('pointercancel', cancel); c.addEventListener('pointerleave', cancel);
    c.addEventListener('click', () => { if (longDone) { longDone = false; return; } activateWindow(w.id, W.mode === 'apps'); });
    row.appendChild(c);
  }
  const add = document.createElement('div'); add.className = 'chip add'; add.setAttribute('role', 'button'); add.innerHTML = ico('add');
  add.addEventListener('click', openApps); row.appendChild(add);
}
async function activateWindow(id, maximize) {
  try { await agent(`/windows/${id}/activate`, 'POST', { maximize }); setTimeout(pollWindows, 350); } catch (e) { toast('Fenster: ' + e.message); }
}
async function toggleMode() {
  try { const d = await agent('/mode', 'POST', { mode: W.mode === 'apps' ? 'desktop' : 'apps' }); W.mode = d.mode; W.lastCls = null; renderWindows(); toast(W.mode === 'apps' ? 'Programm-Modus: Taskleiste der VM ausgeblendet' : 'Desktop-Modus', 2200); setTimeout(pollWindows, 500); }
  catch (e) { toast('Modus: ' + e.message); }
}
function openWinSheet(w) {
  const sh = $('#winSheet'); sh.replaceChildren();
  const item = (ic, text, fn) => { const d = document.createElement('div'); d.className = 'mi'; d.setAttribute('role', 'menuitem'); d.innerHTML = `<span class="mic">${ico(ic)}</span><span class="mt">${text}</span>`; d.addEventListener('click', () => { closeWinSheet(); fn(); }); sh.appendChild(d); };
  item('window', 'Nach vorn und maximieren', () => activateWindow(w.id, true));
  item('window', 'Nach vorn (Größe lassen)', () => activateWindow(w.id, false));
  item('minimize', 'Minimieren', async () => { try { await agent(`/windows/${w.id}/minimize`, 'POST', {}); setTimeout(pollWindows, 300); } catch (e) { toast(e.message); } });
  item('close', 'Schließen', async () => { try { await agent(`/windows/${w.id}/close`, 'POST', {}); setTimeout(pollWindows, 500); } catch (e) { toast(e.message); } });
  item('palette', 'Anzeigegröße für dieses Programm merken', () => { profiles[w.class] = S.k; store.set('profiles', profiles); toast('Gemerkt: ' + zoomPercent() + ' % für ' + (w.app || w.class)); });
  sh.hidden = false; el.menuScrim.hidden = false;
  el.menuScrim.onclick = closeWinSheet;
}
function closeWinSheet() { $('#winSheet').hidden = true; el.menuScrim.hidden = true; el.menuScrim.onclick = null; }
$('#winSheet').addEventListener('mousedown', (e) => e.preventDefault());

async function openApps() {
  $('#appsDlg').showModal(); $('#appsSearch').value = '';
  if (!W.apps) { try { W.apps = (await agent('/apps')).apps || []; } catch (e) { toast('Programme: ' + e.message); W.apps = []; } }
  renderApps('');
}
function renderApps(q) {
  const list = $('#appsList'); list.replaceChildren();
  const f = (W.apps || []).filter((a) => a.name.toLowerCase().includes(q.toLowerCase())).slice(0, 80);
  for (const a of f) {
    const d = document.createElement('div'); d.className = 'appitem'; d.setAttribute('role', 'button');
    d.innerHTML = (a.icon ? `<img alt="" src="data:image/png;base64,${a.icon}">` : '<span style="width:32px"></span>') + `<span></span>`; d.lastChild.textContent = a.name;
    d.addEventListener('click', async () => {
      $('#appsDlg').close();
      try { await agent('/apps/launch', 'POST', { id: a.id }); toast('Starte ' + a.name + ' …', 2200); setTimeout(pollWindows, 1500); } catch (e) { toast('Start: ' + e.message); }
    });
    list.appendChild(d);
  }
  if (!f.length) list.textContent = 'Nichts gefunden.';
}
$('#appsSearch').addEventListener('input', (e) => renderApps(e.target.value));

/* =====================================================================
 *  Groesse: Remote-Aufloesung folgt dem Fenster (Tastatur, Drehen, Zoom)
 * ===================================================================== */
function computeRemote() {
  const r = el.stage.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  const w = Math.max(400, Math.floor((r.width * dpr * S.k) / 4) * 4);
  const h = Math.max(300, Math.floor((r.height * dpr * S.k) / 2) * 2);
  return { w, h };
}
function fitDisplay() {
  if (!S.display) return;
  const rw = S.display.getWidth();
  if (!rw) return;
  S.display.scale(el.stage.getBoundingClientRect().width / rw);
}
function pushSize(force = false) {
  if (!S.client || !S.connected) return;
  const { w, h } = computeRemote();
  if (!force && w === S.lastSent.w && h === S.lastSent.h) return;
  S.lastSent = { w, h };
  dbg('sendSize', { w, h, k: S.k, dpr: window.devicePixelRatio });
  S.client.sendSize(w, h);
}
function scheduleSize() { clearTimeout(S.resizeTimer); S.resizeTimer = setTimeout(() => { pushSize(); fitDisplay(); }, 380); }
new ResizeObserver(() => { fitDisplay(); scheduleSize(); }).observe(el.stage);

const zoomPercent = () => Math.round(100 / S.k);
function showZoomPop() {                                   // Zoomstufe nur kurz einblenden
  el.zoomPop.textContent = zoomPercent() + ' %'; el.zoomPop.classList.add('show');
  clearTimeout(S.zoomTimer); S.zoomTimer = setTimeout(() => el.zoomPop.classList.remove('show'), 900);
}
function setK(k, announce = true) {
  S.k = clamp(k, 0.35, 1.8); store.set('k', S.k);
  if (announce) showZoomPop();
  scheduleSize();
}

/* =====================================================================
 *  Verbindung
 * ===================================================================== */
function teardown() {
  clearTimeout(S.retryTimer); stopWindowBar(); el.topbar.hidden = true;
  try { S.client && S.client.disconnect(); } catch { /* egal */ }
  S.client = null; S.tunnel = null; S.display = null; S.mouse = null; S.connected = false;
  el.display.replaceChildren();
  resetMods();
}
function showOverlay(msg, canRetry = true) {
  el.overlayMsg.textContent = msg; el.spinner.hidden = true; el.overlay.hidden = false; el.reconnectBtn.hidden = !canRetry; el.toLoginBtn.hidden = false;
}
function showBusy(msg) { // Fortschritt mit Animation, ohne Knoepfe
  el.overlayMsg.textContent = msg; el.spinner.hidden = false; el.overlay.hidden = false; el.reconnectBtn.hidden = true; el.toLoginBtn.hidden = true;
}
/** Verstaendliche Texte zu den Statuscodes, die guacd mit einem Fehler schickt (Guacamole-Protokoll). */
const ERROR_TEXT = {
  513: 'Der Server ist gerade ausgelastet.', 514: 'Das Ziel antwortet nicht (Zeitüberschreitung).', 515: 'Die Verbindung zum Ziel ist fehlgeschlagen.',
  519: 'Das Ziel wurde nicht gefunden.', 520: 'Das Ziel ist nicht erreichbar.', 769: 'Anmeldung am Ziel abgelehnt. Bitte Benutzername und Passwort prüfen.',
};
let leaveHandler = () => {};
/** Verbindung beenden und zur Startseite (die Seite zeigt die Meldung). reason: 'auth' = Sitzung der App ist abgelaufen. */
function leave(msg, reason = '') {
  S.intentional = true; teardown(); S.password = null; S.everConnected = false;
  el.remote.hidden = true; el.overlay.hidden = true; el.spinner.hidden = true;
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  leaveHandler(msg || '', reason);
}

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

async function connect() {
  S.intentional = false;
  teardown();
  el.remote.hidden = false;
  showBusy(S.everConnected ? 'Verbinde neu …' : 'Verbinde …');
  await sleep(30); // Layout setzen, damit die Stage-Groesse stimmt
  const { w, h } = computeRemote();
  let ticket;
  try {
    const resp = await fetch('/api/remote/ticket', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-JulOS': '1' },
      body: JSON.stringify({ connection: S.conn.id, password: S.password, width: w, height: h, dpi: 96 }) });
    const data = await resp.json().catch(() => ({}));
    if (resp.status === 401) return leave('Die Sitzung ist abgelaufen. Bitte neu anmelden.', 'auth');
    if (!resp.ok) throw new Error(data.error || 'Fehler ' + resp.status);
    ticket = data.ticket;
  } catch (e) {
    if (!S.everConnected) return leave(e.message);
    return showOverlay(e.message);
  }

  const tunnel = new Guacamole.WebSocketTunnel((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/api/remote/ws');
  const client = new Guacamole.Client(tunnel);
  const display = client.getDisplay();
  S.tunnel = tunnel; S.client = client; S.display = display; S.lastSent = { w, h };
  el.display.appendChild(display.getElement());
  display.onresize = () => { fitDisplay(); dbg('remote-size', { w: display.getWidth(), h: display.getHeight() }); };
  // Textfeld-Erkennung: Der Mauszeiger der VM wird zum "I-Balken" (schmal, hoch, Hotspot mittig)
  display.oncursor = (canvas, hx, hy) => {
    const c = cursorInfo(canvas, hx, hy);
    if (c.sig !== S.cursor.sig && cursorLogN++ < 40) dbg('cursor', { w: c.w, h: c.h, bw: c.bw, bh: c.bh, hx: c.hx, hy: c.hy, ibeam: c.ibeam });
    S.cursor = c;
  };

  client.onstatechange = (st) => {
    if (S.client !== client) return;
    if (st === 3) { // verbunden
      S.connected = true; S.everConnected = true; S.retry = 0; S.connectedAt = Date.now(); startWindowBar(); fitDisplay(); pushSize(true); waitForFrame(client);
      setTimeout(() => { if (S.client === client) S.firstLogin = false; }, 12000);
    } else if (st === 5) { // getrennt
      S.connected = false;
      if (S.intentional) return;
      if (!S.everConnected) return; // Fehler wird von onerror gemeldet
      // sofort nach dem Anmelden getrennt = xrdp hat das Passwort abgelehnt: nicht wiederholen
      if (S.firstLogin && Date.now() - S.connectedAt < 12000) return leave('Anmeldung fehlgeschlagen. Bitte Passwort prüfen.');
      scheduleReconnect();
    }
  };
  client.onerror = (err) => {
    if (S.client !== client) return;
    // guacd meldet meist nur "Aborted. See logs."; der Statuscode sagt mehr
    const known = ERROR_TEXT[err && err.code];
    const msg = known || ((err && err.message) ? err.message : 'Verbindungsfehler');
    S.connected = false;
    if (!S.everConnected) leave(known || 'Verbindung nicht möglich: ' + msg);
    else if (S.firstLogin && Date.now() - S.connectedAt < 12000) leave('Anmeldung fehlgeschlagen. Bitte Passwort prüfen.');
    else showOverlay(msg);
  };
  tunnel.onerror = (st) => { if (S.client === client && !S.everConnected) leave('Keine Verbindung zum Server: ' + (st && st.message ? st.message : '')); };
  client.onclipboard = (stream, mimetype) => {
    if (!/^text\//.test(mimetype)) return;
    const reader = new Guacamole.StringReader(stream);
    let data = '';
    reader.ontext = (t) => { data += t; };
    reader.onend = () => { el.remoteClip.value = data; toast('Text aus der VM kopiert (Symbol Zwischenablage)', 2200); };
  };

  setupMouse(client, display);
  client.connect('ticket=' + encodeURIComponent(ticket));
}

/* Nach dem Verbinden bleibt die Ladeanzeige stehen, bis wirklich ein Bild da ist (sonst ist alles schwarz).
   Kommt keins, fordert eine kleine Groessenaenderung vom Server ein neues Vollbild an. */
function nudgeSize(delta = 2) {
  if (!S.client || !S.connected) return;
  const { w, h } = computeRemote();
  S.lastSent = { w, h: h - delta }; S.client.sendSize(w, h - delta);
  setTimeout(() => { if (S.client && S.connected) { S.lastSent = { w, h }; S.client.sendSize(w, h); } }, 350);
}
function waitForFrame(client) {
  const started = Date.now();
  let nudges = 0, probes = 0, sizedAt = 0, released = false;
  showBusy('Bild wird geladen …');
  const probe = document.createElement('canvas'); probe.width = probe.height = 8;
  const pc = probe.getContext('2d', { willReadFrequently: true });
  let seen = { w: 0, h: 0, max: 0 };
  const hasContent = () => {
    try {
      const cv = S.display.flatten();                       // alle Ebenen zusammengesetzt (die Standardebene allein kann leer sein)
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
  const release = (why) => { if (released) return; released = true; dbg('overlay-weg', { ms: Date.now() - started, why }); el.overlay.hidden = true; el.spinner.hidden = true; };
  const tick = () => {
    if (S.client !== client || !S.connected) return;
    const age = Date.now() - started;
    const ok = hasContent();
    if (seen.w > 0 && !sizedAt) sizedAt = Date.now();
    if (probes++ % 4 === 0 && probes < 40) dbg('frameprobe', { ms: age, nudges, ...seen });
    if (sizedAt || age > 4000) release(sizedAt ? 'groesse' : 'zeit');
    if (ok || age > 12000) { dbg('firstframe', { ms: age, nudges, ok, ...seen }); release('bild'); return; }
    // Anstupsen (die VM zeichnet nach einer echten Groessenaenderung alles neu) erst, wenn es eine Bildgroesse gibt:
    // vorher geht die Aenderung ins Leere, weil die VM noch gar nicht verbunden ist
    if (sizedAt) {
      const since = Date.now() - sizedAt;
      if (nudges === 0 && since > 700) { nudges = 1; nudgeSize(2); }
      else if (nudges === 1 && since > 2500) { nudges = 2; nudgeSize(64); }
      else if (nudges === 2 && since > 5000) { nudges = 3; nudgeSize(128); }
    }
    setTimeout(tick, 250);
  };
  tick();
}

function scheduleReconnect() {
  if (S.retry >= 3) { showOverlay('Verbindung getrennt'); return; }
  S.retry += 1;
  const wait = 1500 * S.retry;
  showBusy(`Verbindung getrennt – neuer Versuch in ${Math.round(wait / 1000)} s …`);
  clearTimeout(S.retryTimer);
  S.retryTimer = setTimeout(() => { if (!S.intentional && S.password) connect(); }, wait);
}

el.reconnectBtn.addEventListener('click', () => { S.retry = 0; if (S.password) connect(); else leave(); });
el.toLoginBtn.addEventListener('click', () => leave());

/* =====================================================================
 *  Maus (Desktop-Browser) und Touch (Handy)
 * ===================================================================== */
function remoteXY(cx, cy) {
  const r = el.display.getBoundingClientRect();
  const s = S.display ? S.display.getScale() : 1;
  return { x: Math.round((cx - r.left) / s), y: Math.round((cy - r.top) / s) };
}
function mouseSend(x, y, l = false, m = false, r = false, up = false, down = false) {
  if (S.client) S.client.sendMouseState(new Guacamole.Mouse.State(x, y, l, m, r, up, down));
}

function setupMouse(client, display) {
  // echte Maus (nur Desktop-Browser); Touch laeuft ueber die eigene Gestenlogik
  const mouse = new Guacamole.Mouse(el.touch);
  S.mouse = mouse;
  const fwd = (state) => {
    if (S.client !== client) return;
    const s = display.getScale() || 1;
    client.sendMouseState(new Guacamole.Mouse.State(state.x / s, state.y / s, state.left, state.middle, state.right, state.up, state.down));
  };
  mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = fwd;
  mouse.onmouseout = () => {};
  display.showCursor(true);
}

const G = { pts: new Map(), mode: 'idle', timer: null, sx: 0, sy: 0, st: 0, lx: 0, ly: 0, d0: 0, k0: 0, mid0: null, midLast: null,
  decided: null, scrollAcc: 0, pressed: false, px: 0, py: 0, newK: 0, moved: false };

let tapLogN = 0;
const dist2 = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
const mid2 = (a, b) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });

async function clickAt(x, y, button = 'left') {
  mouseSend(x, y);
  await sleep(10);
  mouseSend(x, y, button === 'left', false, button === 'right');
  await sleep(35);
  mouseSend(x, y, false, false, false);
  consumeOnce();
}
function padPoint() {
  if (!G.px && !G.py && S.display) { G.px = S.display.getWidth() / 2; G.py = S.display.getHeight() / 2; }
  return { x: G.px, y: G.py };
}

el.touch.addEventListener('touchstart', (e) => {
  e.preventDefault();
  for (const t of e.changedTouches) G.pts.set(t.identifier, { x: t.clientX, y: t.clientY });
  const pts = [...G.pts.values()];
  if (pts.length === 1) {
    G.mode = 'pending'; G.sx = pts[0].x; G.sy = pts[0].y; G.lx = pts[0].x; G.ly = pts[0].y; G.st = Date.now(); G.moved = false;
    clearTimeout(G.timer);
    if (S.touchMode !== 'pad') { const q0 = remoteXY(pts[0].x, pts[0].y); mouseSend(q0.x, q0.y); } // Zeiger vorab hinbewegen: Cursorform (Textfeld?) aktualisiert sich
    G.timer = setTimeout(() => { if (G.mode === 'pending') { G.mode = 'long'; if (navigator.vibrate) navigator.vibrate(15); } }, 520);
  } else if (pts.length === 2) {
    clearTimeout(G.timer);
    if (G.pressed) { const p = remoteXY(G.lx, G.ly); mouseSend(p.x, p.y); G.pressed = false; }
    G.mode = 'two'; G.d0 = dist2(pts[0], pts[1]); G.k0 = S.k; G.newK = S.k;
    G.mid0 = mid2(pts[0], pts[1]); G.midLast = G.mid0; G.decided = null; G.scrollAcc = 0; G.moved = false; G.st = Date.now();
  }
}, { passive: false });

el.touch.addEventListener('touchmove', (e) => {
  e.preventDefault();
  for (const t of e.changedTouches) if (G.pts.has(t.identifier)) G.pts.set(t.identifier, { x: t.clientX, y: t.clientY });
  const pts = [...G.pts.values()];
  if (pts.length === 1 && (G.mode === 'pending' || G.mode === 'long' || G.mode === 'drag' || G.mode === 'pad')) {
    const p = pts[0];
    const moved = Math.hypot(p.x - G.sx, p.y - G.sy);
    if (G.mode === 'pending' && moved > 10) {
      clearTimeout(G.timer);
      if (S.touchMode === 'pad') { G.mode = 'pad'; } else { G.mode = 'drag'; const s0 = remoteXY(G.sx, G.sy); mouseSend(s0.x, s0.y); mouseSend(s0.x, s0.y, true); G.pressed = true; }
    } else if (G.mode === 'long' && moved > 10) {
      G.mode = 'drag';
      if (S.touchMode === 'pad') { const q = padPoint(); mouseSend(q.x, q.y, true); } else { const s0 = remoteXY(G.sx, G.sy); mouseSend(s0.x, s0.y); mouseSend(s0.x, s0.y, true); }
      G.pressed = true;
    }
    if (G.mode === 'drag' || G.mode === 'pad') {
      G.moved = true;
      if (S.touchMode === 'pad') {
        const s = S.display ? S.display.getScale() : 1;
        padPoint();
        G.px += ((p.x - G.lx) / s) * 1.6; G.py += ((p.y - G.ly) / s) * 1.6;
        G.px = clamp(G.px, 0, S.display ? S.display.getWidth() : 0); G.py = clamp(G.py, 0, S.display ? S.display.getHeight() : 0);
        mouseSend(G.px, G.py, G.pressed);
      } else {
        const q = remoteXY(p.x, p.y); mouseSend(q.x, q.y, true);
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
      toast('Anzeige ' + Math.round(100 / G.newK) + ' %', 600);
    } else if (G.decided === 'scroll') {
      const dy = m.y - G.midLast.y; G.midLast = m; G.scrollAcc += dy;
      const q = remoteXY(m.x, m.y);
      while (Math.abs(G.scrollAcc) >= 34) {
        const down = G.scrollAcc < 0; // Finger nach oben = nach unten scrollen
        mouseSend(q.x, q.y, false, false, false, !down, down);
        mouseSend(q.x, q.y, false, false, false, false, false);
        G.scrollAcc += down ? 34 : -34;
      }
    }
  }
}, { passive: false });

async function endTouch(e) {
  e.preventDefault();
  const before = G.pts.size;
  for (const t of e.changedTouches) G.pts.delete(t.identifier);
  if (before === 1 && G.pts.size === 0) {
    clearTimeout(G.timer);
    const dur = Date.now() - G.st;
    if (G.mode === 'pending' && dur < 380) {
      // Tippen auf ein Textfeld (Zeiger ist ein I-Balken): Tastatur gleich in der Geste oeffnen
      if (tapLogN++ < 40) dbg('tap', { ibeam: S.cursor.ibeam, kb: kbOpen, auto: S.autoKb, cur: S.cursor.sig || '' });
      if (S.autoKb && S.cursor.ibeam && !kbOpen) { dbg('autokb', { when: 'tap' }); el.kb.blur(); el.kb.focus(); }
      if (S.touchMode === 'pad') { const q = padPoint(); await clickAt(q.x, q.y, 'left'); }
      else { const q = remoteXY(G.sx, G.sy); await clickAt(q.x, q.y, 'left'); }
      // Zeigerform kommt erst nach der Mausbewegung an. Electron-Programme wie Claude liefern den I-Balken erst nach rund einer
      // Sekunde, deshalb ein Zeitfenster statt einer einzelnen Pruefung.
      const t0 = Date.now();
      const poll = () => {
        if (!S.autoKb || !S.connected || kbOpen || Date.now() - t0 > 1600) return;
        if (S.cursor.ibeam) { dbg('autokb', { when: 'late', ms: Date.now() - t0 }); el.kb.focus(); return; }
        setTimeout(poll, 100);
      };
      setTimeout(poll, 120);
    } else if (G.mode === 'long') {
      if (S.touchMode === 'pad') { const q = padPoint(); await clickAt(q.x, q.y, 'right'); }
      else { const q = remoteXY(G.sx, G.sy); await clickAt(q.x, q.y, 'right'); }
    } else if (G.mode === 'drag' || G.mode === 'pad') {
      if (G.pressed) {
        if (S.touchMode === 'pad') mouseSend(G.px, G.py, false); else { const q = remoteXY(G.lx, G.ly); mouseSend(q.x, q.y, false); }
        G.pressed = false; consumeOnce();
      }
    }
    G.mode = 'idle';
  } else if (G.mode === 'two' && G.pts.size === 0) {
    const dur = Date.now() - G.st;
    if (!G.decided && !G.moved && dur < 350) { // Zwei-Finger-Tipp = Rechtsklick
      const q = S.touchMode === 'pad' ? padPoint() : remoteXY(G.midLast.x, G.midLast.y); await clickAt(q.x, q.y, 'right');
    } else if (G.decided === 'pinch' && Math.abs(G.newK - G.k0) > 0.01) {
      setK(G.newK, false); showZoomPop(); rememberProfile();
    }
    G.mode = 'idle';
  } else if (G.pts.size === 0) { G.mode = 'idle'; }
}
el.touch.addEventListener('contextmenu', (e) => e.preventDefault());
el.touch.addEventListener('touchend', endTouch, { passive: false });
el.touch.addEventListener('touchcancel', endTouch, { passive: false });

/* =====================================================================
 *  Tastatur (Android): ohne Autokorrektur, mit Sondertasten-Unterstuetzung
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
  el.kbModeLabel.textContent = S.kbMode === 'rich' ? 'mit Bildern' : S.kbMode === 'normal' ? 'mit Vorschlägen' : 'ohne Vorschläge';
  el.touchModeLabel.textContent = S.touchMode === 'direct' ? 'Tippen = Klick' : 'Touchpad';
  el.autoKbLabel.textContent = S.autoKb ? 'an' : 'aus';
  el.autoPasteLabel.textContent = S.autoPaste ? 'an' : 'aus';
  store.set('autoPaste', S.autoPaste); store.set('autoKb', S.autoKb);
  store.set('kbMode2', S.kbMode); store.set('touchMode', S.touchMode);
}
const NAMED = { Enter: KS.enter, Backspace: KS.backspace, Tab: KS.tab, Escape: KS.esc, ArrowLeft: KS.left, ArrowRight: KS.right,
  ArrowUp: KS.up, ArrowDown: KS.down, Delete: KS.delete, Home: KS.home, End: KS.end, PageUp: KS.pageup, PageDown: KS.pagedown, Insert: KS.insert };

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
  if (txt) { e.preventDefault(); typeText(txt); }          // eingefuegten Text direkt eintippen
}
function onKbBeforeInput(e) {
  if (kbEvCount++ < 60) dbg('beforeinput', { it: e.inputType, comp: e.isComposing, len: (e.data || '').length, files: (e.dataTransfer && e.dataTransfer.files ? e.dataTransfer.files.length : 0), mode: S.kbMode });
  const files = e.dataTransfer && e.dataTransfer.files;
  if (files && files.length) { e.preventDefault(); handleKbFiles(files); }
}
function syncDiff() {
  if (!S.connected) return;
  const cur = kbVal();
  const a = [...prevValue], b = [...cur];
  let i = 0; while (i < a.length && i < b.length && a[i] === b[i]) i++;
  for (let n = 0; n < a.length - i; n++) tapKey(KS.backspace);
  const add = b.slice(i).join(''); if (add) typeText(add);
  prevValue = cur;
  if (b.length > 300) { setKbVal(''); prevValue = ''; }
}
let kbEvCount = 0;
function wireKb(node) {
  node.addEventListener('keydown', (e) => {
    if (kbEvCount++ < 60) dbg('keydown', { key: e.key.length === 1 ? 'char' : e.key, code: e.keyCode, comp: e.isComposing });
    if (!S.connected) return;
    if (e.key in NAMED) {
      // Backspace mit Text im Feld kommt ueber das input-Ereignis (Abgleich), nur bei leerem Feld direkt senden
      if (e.key === 'Backspace' && kbVal().length > 0) return;
      e.preventDefault(); lastNamedAt = Date.now();
      if (e.key === 'Enter' || e.key === 'Tab') { setKbVal(''); prevValue = ''; }
      tapKey(NAMED[e.key]); return;
    }
    // Hardware-Tastatur mit Strg/Alt: als Kombination weitergeben
    if ((e.ctrlKey || e.altKey || e.metaKey) && e.key.length === 1) {
      e.preventDefault();
      const syms = [];
      if (e.ctrlKey) syms.push(KS.ctrl);
      if (e.altKey) syms.push(KS.alt);
      if (e.metaKey) syms.push(KS.win);
      syms.push(charKeysym(e.key.toLowerCase())); sendChord(syms);
    }
  });
  node.addEventListener('beforeinput', onKbBeforeInput);
  node.addEventListener('paste', onKbPaste);
  node.addEventListener('drop', (e) => { if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length) { e.preventDefault(); handleKbFiles(e.dataTransfer.files); } });
  node.addEventListener('compositionend', () => syncDiff());
  node.addEventListener('input', () => syncDiff());
  node.addEventListener('blur', () => { setActOn('keyboard', kbOpen); if (node === el.kb) { setKbVal(''); prevValue = ''; } dbg('kbblur', {}); });
  node.addEventListener('focus', () => { setActOn('keyboard', true); dbg('kbfocus', { mode: S.kbMode }); });
}
wireKb(el.kbInput); wireKb(el.kbRich);
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
 *  Menue, Vollbild, Text senden / Zwischenablage
 * ===================================================================== */
function openMenu() { applyKbMode(); el.menuSheet.hidden = false; el.menuScrim.hidden = false; setActOn('menu', true); }
function closeMenu() { el.menuSheet.hidden = true; el.menuScrim.hidden = true; setActOn('menu', false); }
el.menuScrim.addEventListener('click', closeMenu);
// weder Fenster noch Hintergrund duerfen den Fokus nehmen: die Tastatur bleibt offen
el.menuSheet.addEventListener('mousedown', (e) => e.preventDefault());
el.menuScrim.addEventListener('mousedown', (e) => e.preventDefault());
el.menuSheet.addEventListener('click', (e) => {
  const act = e.target.closest('[data-act]')?.dataset.act;
  if (!act) return;
  if (['kbMode', 'autoKb', 'touchMode', 'autoPaste'].includes(act)) {
    if (act === 'kbMode') S.kbMode = S.kbMode === 'rich' ? 'normal' : S.kbMode === 'normal' ? 'direct' : 'rich';
    if (act === 'autoKb') S.autoKb = !S.autoKb;
    if (act === 'autoPaste') S.autoPaste = !S.autoPaste;
    if (act === 'touchMode') S.touchMode = S.touchMode === 'direct' ? 'pad' : 'direct';
    applyKbMode(); return;
  }
  closeMenu();
  if (act === 'reconnect') { S.retry = 0; if (S.password) connect(); }
  else if (act === 'editKeys') { S.editing = true; renderRows(); applyKeysOpen(); toast('Taste antippen = bearbeiten, gedrückt halten = verschieben (auch in andere Zeilen)', 3600); }
  else if (act === 'resetKeys') { if (confirm('Sondertasten auf Standard zurücksetzen?')) { rows = clone(DEFAULT_ROWS); saveRows(); renderRows(); applyKeysOpen(); } }
  else if (act === 'password') openPwDialog();
  else if (act === 'theme') openThemeDialog();
  else if (act === 'install') installApp();
  else if (act === 'logout') leave();
});
document.querySelectorAll('dialog [data-act="close"]').forEach((b) => b.addEventListener('click', () => b.closest('dialog').close()));

el.typeBtn.addEventListener('click', async () => { const t = el.sendText.value; el.clipDlg.close(); if (t) { await sleep(150); await typeText(t); toast('Eingetippt'); } });
el.pasteBtn.addEventListener('click', () => {
  if (!S.client || !el.sendText.value) return;
  const stream = S.client.createClipboardStream('text/plain');
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
    toast('In der Zwischenablage ist kein Bild. Erst ein Bild kopieren.', 3200);
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
  if (!S.connected) throw new Error('nicht verbunden');
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
  if (S.autoPaste) { await sleep(250); await sendChord([KS.ctrl, charKeysym('v')]); toast('Foto eingefügt (Strg+V)'); }
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
  if (both) { if (u) { await typeText(u); await tapKey(KS.tab); } await typeText(p); await tapKey(KS.enter); }
  else await typeText(p);
  toast('Eingetippt');
}
el.pwTypePass.addEventListener('click', () => typePw(false));
el.pwTypeBoth.addEventListener('click', () => typePw(true));
el.pwDlg.addEventListener('close', () => { el.pwUser.value = ''; el.pwPass.value = ''; });

/* =====================================================================
 *  Start
 * ===================================================================== */
window.addEventListener('beforeunload', () => { try { S.client && S.client.disconnect(); } catch { /* egal */ } });
document.addEventListener('visibilitychange', () => { if (!document.hidden && S.password && !S.connected && !S.intentional && S.everConnected) { S.retry = 0; connect(); } });

(async function init() {
  renderIcons(); applyTheme(); applyKbMode(); applyKeysOpen(); renderKeys();
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
if (location.search.includes('debug')) window.__pd = { W, S, rows: () => rows, renderWindows, renderRows, mods, cursorInfo, openPhotoDlg };

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


/* =====================================================================
 *  Schnittstelle zur Startseite (shell.js)
 * ===================================================================== */
/** Startet die Verbindung. conn = {id, name, kind}; das Passwort bleibt nur im Arbeitsspeicher dieser Seite. */
export function openRemote(conn, password) {
  S.conn = conn; S.password = password; S.retry = 0; S.everConnected = false; S.firstLogin = true;
  return connect();
}
/** Wird mit (Meldung, Grund) gerufen, wenn die Verbindung beendet wird und die Startseite wieder dran ist. */
export function setLeaveHandler(fn) { leaveHandler = fn; }
export function disconnectRemote() { if (S.client || !el.remote.hidden) leave(); }
