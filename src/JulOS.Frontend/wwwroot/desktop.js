import { remoteApi, getScheme, setTheme, setLanguage } from './app.js';
import { SCHEMES } from './scheme.js';
import { GROUPS, rowsFor, aboutValues } from './settings.js';
import { Sessions } from './sessions.js';
import { looksLikeAddress } from './quick.js';
import { filterByFolder, groupByFolder, recentList, folderIconSvg, folderName, setFavorite } from './folders.js';
import { remoteMenuNames, remoteMenuItems, remoteMenuLabel, sessionMenu } from './desktop-remote.js';
import { connectionState, dotClass, stateText, hostSections, hostStatusText, toggleClosed, readClosed, writeClosed } from './hosts.js';
import { t, tk, fmtNumber, fmtDate, fmtTime, fmtDay, getPref, LANG_CHOICES } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import { tileKind } from './terminal-programs.js';
import { hostCardHtml, menuOpen } from './hostview.js';
import { barText, barPercent, barTone, chipText, panelHtml, finishNotice } from './hosttasks.js';
import { jobFinal } from './hostactions.js';
import { loginUser, loginSaved } from './hostlogin.js';
import { filesWindowSize } from './files-explorer.js';
import { setDialogHost } from './desktop-window.js';
import { createWM } from './desktop-wm.js';
import { createPills } from './desktop-pills.js';
import { createTaskbar } from './desktop-taskbar.js';

/* =====================================================================
 * Desktop-Oberflaeche (breite Bildschirme mit Maus): Menueleiste, Dock, Launcher, Home-Fenster und ein Fenster
 * fuer die laufende Sitzung. Die Verbindungslogik bleibt in app.js; hier wird nur die Huelle gebaut.
 * shell.js laedt diese Datei nur auf solchen Bildschirmen und meldet Ereignisse (Anmeldung, Verbindungen, Sitzungsende).
 * Auf dem Handy wird nichts davon geladen.
 * ===================================================================== */

const $ = (s, r = document) => r.querySelector(s);
const BAR_H = 34;
let ctx = null;            // von shell.js: { state, esc, api, connect, edit, add, quick, folders, editHost, addHost, hostInfo, info, devices, account, nettools, alerts, signOut }
let root = null;
let wm = null;             // Fenstermanager (desktop-wm.js): Einrasten, Tastatur, Maustasten 4 und 5
let pills = null;          // duenne Pillen des eingeklappten Docks (desktop-pills.js)
let taskbar = null;        // Taskleiste der VM im Dock (desktop-taskbar.js)
const wins = new Map();    // id -> Fenster
let zTop = 10;
const sessWins = new Map(); // Sitzungskennung -> Fenster (jede Sitzung ein Fenster)
/** Das Fenster der aktiven Sitzung (die Tasten und die Menues der oberen Leiste gehoeren ihm) und ihre Verbindung */
const activeW = () => { const a = Sessions.active; return a ? sessWins.get(a.id) || null : null; };
const activeConn = () => (Sessions.active ? Sessions.active.conn : null);

const ls = {
  get(k, d) { try { const v = localStorage.getItem('pd.dk.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('pd.dk.' + k, JSON.stringify(v)); } catch { /* egal */ } },
};
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

/* ---------- Symbole (Strich-Symbole, 24 px) ---------- */
const P = {
  home: '<path d="M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
  monitor: '<rect x="2.5" y="3.5" width="19" height="13" rx="2"/><path d="M8 21h8M12 16.5V21"/>',
  terminal: '<path d="m4 17 6-6-6-6M12 19h8"/>',
  spark: '<path d="M12 3l1.9 5.6L20 10l-6.1 1.4L12 17l-1.9-5.6L4 10l6.1-1.4zM19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8z"/>',
  code: '<path d="m8 7-5 5 5 5M16 7l5 5-5 5"/>',
  sliders: '<path d="M21 4h-7M10 4H3M21 12h-9M8 12H3M21 20h-5M12 20H3M14 2v4M8 10v4M16 18v4"/>',
  folder: '<path d="M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.7-.9l-.8-1.2A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z"/>',
  server: '<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/>',
  cloud: '<path d="M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9z"/>',
  activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  box: '<path d="m21 8-9-5-9 5v8l9 5 9-5z"/><path d="m3.3 7.5 8.7 5 8.7-5M12 22V12.5"/>',
  globe: '<circle cx="12" cy="12" r="10"/><path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>',
  power: '<path d="M12 2v10M18.4 6.6a9 9 0 1 1-12.77.04"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  grid: '<path d="M3 3h7v7H3zM14 3h7v7h-7zM14 14h7v7h-7zM3 14h7v7H3z"/>',
  x: '<path d="M18 6 6 18M6 6l12 12"/>',
  star: '<path d="m12 3 2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z"/>',
  minus: '<path d="M5 12h14"/>',
  square: '<rect x="5" y="5" width="14" height="14" rx="2"/>',
  restore: '<rect x="4.5" y="8.5" width="11" height="11" rx="2"/><path d="M8.5 8.5V6.5a2 2 0 0 1 2-2h7a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2h-2"/>',
  left: '<path d="m15 18-6-6 6-6"/>',
  right: '<path d="m9 18 6-6-6-6"/>',
  edit: '<path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>',
  shield: '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>',
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
  play: '<path d="M7 4.5v15l12-7.5z"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="m10.8 12.2 8-8M16 7l3 3"/>',
  cpu: '<rect x="5" y="5" width="14" height="14" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3"/>',
  ram: '<rect x="3" y="7" width="18" height="10" rx="1.5"/><path d="M7 7V5M12 7V5M17 7V5M7 17v2M12 17v2M17 17v2"/>',
  disk: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 14.5h6"/><circle cx="17" cy="14.5" r=".6"/>',
  network: '<path d="M7 4v16m0 0-3-3m3 3 3-3M17 20V4m0 0-3 3m3-3 3 3"/>',
  chev: '<path d="m6 9 6 6 6-6"/>',
  refresh: '<path d="M20 11a8 8 0 0 0-14.5-4.5L4 8M4 4v4h4M4 13a8 8 0 0 0 14.5 4.5L20 16M20 20v-4h-4"/>',
  keyboard: '<rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>',
  file: '<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5M9 13h6M9 17h6"/>',
  download: '<path d="M12 3v12m0 0-4-4m4 4 4-4M5 21h14"/>',
  up: '<path d="m18 15-6-6-6 6"/>',
  check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
  popout: '<path d="M14 4h6v6M20 4l-8.5 8.5M18 14v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4"/>',
  blank: '',
};
const ic = (n) => `<svg class="dk-i" viewBox="0 0 24 24" aria-hidden="true">${P[n] || ''}</svg>`;
// Der Verlauf steht einmal in DEFS (versteckte SVG im Desktop); jedes Logo verweist nur darauf
const DEFS = `<svg width="0" height="0" style="position:absolute" aria-hidden="true"><defs><linearGradient id="dkl" x1="0" y1="0" x2="1" y2="1"><stop offset="0" style="stop-color:var(--dk-logo-a)"/><stop offset="1" style="stop-color:var(--dk-logo-b)"/></linearGradient></defs></svg>`;
const LOGO = `<svg viewBox="0 0 32 32" aria-hidden="true">
  <path d="M8 5.5 25.5 16 8 26.5z" fill="url(#dkl)" stroke="url(#dkl)" stroke-width="3.6" stroke-linejoin="round"/><path d="M8 5.5 25.5 16 15 16z" style="fill:var(--dk-logo-hi)" opacity=".28"/></svg>`;

/* Programme: Farben, Symbole, Ziele. soon = gibt es noch keinen Server-Teil dafuer (siehe docs/IDEAS.md). mod = nur sichtbar, wenn der Server das Modul eingeschaltet hat (docs/MODULES.md). */
const APPS = [
  { id: 'home', name: t('desktop.start'), ic: 'home', g: ['#7b78ee', '#5160e6'] },
  { id: 'desktop', name: t('desktop.desktop'), sub: 'RDP', ic: 'monitor', g: ['#3a99dc', '#2f5fe0'] },
  { id: 'vnc', name: t('desktop.vnc'), sub: t('desktop.desktop'), ic: 'monitor', g: ['#2a8f8a', '#25758f'] },
  { id: 'terminal', name: t('desktop.terminal'), sub: 'SSH', ic: 'terminal', g: ['#5b6275', '#2c3142'] },
  { id: 'claude', name: t('prog.claude'), sub: 'SSH', ic: 'spark', g: ['#c0683a', '#94491f'] },
  { id: 'codex', name: t('prog.codex'), sub: 'SSH', ic: 'code', g: ['#3f8a76', '#2c6a5c'] },
  { id: 'settings', name: t('desktop.settings'), ic: 'sliders', g: ['#7d849f', '#555b78'] },
  { id: 'files', name: t('desktop.files'), ic: 'folder', g: ['#c28418', '#a86408'] },
  { id: 'hosts', name: t('desktop.hosts'), sub: t('desktop.hostsSub'), ic: 'server', g: ['#2fa08c', '#1c7a70'] },
  { id: 'docker', name: 'Docker', ic: 'box', g: ['#3a95e0', '#1f69bf'], soon: true },
  { id: 'widgets', name: t('desktop.widgets'), sub: t('desktop.widgetsSub'), ic: 'grid', g: ['#c06a14', '#a8461a'], mod: 'services' },
  { id: 'caddy', name: 'Caddy', sub: t('desktop.caddySub'), ic: 'globe', g: ['#1fa06a', '#12804f'], mod: 'caddy' },
  { id: 'ddns', name: 'DDNS', sub: t('desktop.ddnsSub'), ic: 'cloud', g: ['#3f8fe0', '#2d66cc'], mod: 'ddns' },
  { id: 'monitor', name: t('desktop.monitor'), ic: 'activity', g: ['#cf5fae', '#8c45d6'] },
];
const appById = (id) => APPS.find((a) => a.id === id);
const appOf = (c) => appById(tileKind(c));     // Kachel einer Verbindung: Desktop, Terminal, Claude oder Codex
const isProg = (c) => c.kind === 'terminal' && tileKind(c) !== 'terminal';
/** Kurzer Hinweis fuer shell.js (Kachel angelegt, Fehler). */
export const toast = (msg) => notify(msg);
const kindName = (kind) => (kind === 'terminal' ? t('desktop.terminal') : kind === 'vnc' ? t('desktop.vnc') : t('desktop.desktop'));
const subName = (c) => (isProg(c) ? appOf(c).name : kindName(c.kind));      // Untertitel einer Verbindung: Programm oder Art
let modules = { caddy: false, ddns: false, services: false };   // vom Server (GET /api/modules), nach der Anmeldung geladen
const shownApps = () => APPS.filter((a) => !a.mod || modules[a.mod]);
const tile = (g, icon) => `<span class="dk-ti" style="--g1:${g[0]};--g2:${g[1]}">${ic(icon)}</span>`;

/* ---------- kleine Helfer ---------- */
function el(tag, cls, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html !== undefined) e.innerHTML = html; return e; }
const btn = (cls, html, label) => { const b = el('button', 'dk-b ' + (cls || ''), html); b.type = 'button'; if (label) b.setAttribute('aria-label', label); return b; };

let dockHinted = false;
let toastTimer = null;
function notify(msg, ms = 3200, action = null) {
  if (!msg) return;
  $('.dk-toast', root)?.remove();
  const t = el('div', 'dk-toast'); t.setAttribute('role', 'status');
  const span = el('span'); span.textContent = msg; t.appendChild(span);
  if (action) { const b = btn('dk-btn dk-toast-act', '', action.label); b.textContent = action.label; b.addEventListener('click', () => { t.remove(); action.run(); }); t.append(b); t.classList.add('act'); }
  root.appendChild(t);
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.remove(), ms);
}

/* ---------- Hintergrund: Pastell-Szene (Himmel, Berg, See, Bluetenzweige), erzeugt im Code ---------- */
function scene(withBlossoms) {
  let seed = 7;
  const rnd = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
  let blossoms = '';
  if (withBlossoms) {
    const cluster = (cx, cy, rx, ry, n, dir) => {
      let s = `<path d="M${cx - dir * rx * 1.2} ${cy - ry * 0.5} Q ${cx} ${cy + ry * 0.2} ${cx + dir * rx * 0.8} ${cy + ry * 0.9}" style="stroke:var(--wp-branch)" stroke-opacity=".55" stroke-width="5" fill="none" stroke-linecap="round"/>`;
      for (let i = 0; i < n; i++) {
        const a = rnd() * 6.283, d = Math.sqrt(rnd());
        const x = cx + Math.cos(a) * rx * d, y = cy + Math.sin(a) * ry * d, r = 6 + rnd() * 16;
        const tone = ['var(--wp-b1)', 'var(--wp-b2)', 'var(--wp-b3)', 'var(--wp-b4)', 'var(--wp-b5)'][Math.floor(rnd() * 5)];
        s += `<circle cx="${x.toFixed(0)}" cy="${y.toFixed(0)}" r="${r.toFixed(0)}" style="fill:${tone}" opacity="${(0.45 + rnd() * 0.5).toFixed(2)}"/>`;
      }
      return s;
    };
    blossoms = `<g filter="url(#dkb)">${cluster(150, 190, 260, 170, 150, 1)}${cluster(1480, 120, 280, 150, 130, -1)}${cluster(60, 560, 150, 190, 70, 1)}</g>
      <g>${cluster(180, 210, 200, 120, 40, 1)}${cluster(1450, 130, 210, 100, 34, -1)}</g>`;
  }
  return `<svg viewBox="0 0 1600 900" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
    <defs>
      <linearGradient id="dks" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color:var(--wp-s1)"/><stop offset=".42" style="stop-color:var(--wp-s2)"/><stop offset=".62" style="stop-color:var(--wp-s3)"/></linearGradient>
      <linearGradient id="dkw" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color:var(--wp-w1)"/><stop offset=".6" style="stop-color:var(--wp-w2)"/><stop offset="1" style="stop-color:var(--wp-w3)"/></linearGradient>
      <linearGradient id="dkm" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color:var(--wp-m1)"/><stop offset="1" style="stop-color:var(--wp-m2)"/></linearGradient>
      <radialGradient id="dkg" cx=".5" cy=".5" r=".5"><stop offset="0" style="stop-color:var(--wp-glow)" stop-opacity=".85"/><stop offset="1" style="stop-color:var(--wp-glow)" stop-opacity="0"/></radialGradient>
      <filter id="dkb" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="9"/></filter>
    </defs>
    <rect width="1600" height="900" fill="url(#dks)"/>
    <ellipse cx="860" cy="470" rx="620" ry="190" fill="url(#dkg)"/>
    <path d="M0 520 L180 450 L330 500 L520 420 L700 500 L900 440 L1100 510 L1300 440 L1480 500 L1600 460 V600 H0z" style="fill:var(--wp-far)" opacity=".75"/>
    <path d="M430 600 L760 330 Q800 300 840 330 L1170 600z" fill="url(#dkm)"/>
    <path d="M700 380 L760 330 Q800 300 840 330 L905 385 Q870 365 850 392 Q830 372 805 400 Q785 372 760 395 Q735 372 700 380z" style="fill:var(--wp-snow)" opacity=".88"/>
    <path d="M0 600 H1600 V900 H0z" fill="url(#dkw)"/>
    <path d="M430 600 L760 750 L1170 600z" style="fill:var(--wp-shade)" opacity=".22"/>
    <g style="fill:var(--wp-refl)" opacity=".35"><ellipse cx="500" cy="660" rx="260" ry="4"/><ellipse cx="1150" cy="700" rx="320" ry="5"/><ellipse cx="820" cy="760" rx="380" ry="5"/><ellipse cx="300" cy="820" rx="260" ry="5"/></g>
    ${blossoms}
  </svg>`;
}

/* =====================================================================
 *  Menues (Menueleiste, Dock, Schnellaktionen)
 * ===================================================================== */
let menuEl = null, menuOwner = null, subEl = null;
function closeSub() { subEl?.remove(); subEl = null; }
function closeMenu() { closeSub(); menuEl?.remove(); menuEl = null; if (menuOwner) menuOwner.classList.remove('open'); menuOwner = null; syncKeys(); }
/** Baut die Eintraege eines Menues. Eintrag: { label, icon, hint, disabled, run } | { sep: true } | { cap: 'Text' } | { label, sub: [Eintraege] } (Untermenue) */
function menuBody(items, isSub) {
  const m = el('div', 'dk-menu' + (isSub ? ' sub' : '')); m.setAttribute('role', 'menu');
  for (const it of items) {
    if (it.sep) { m.appendChild(el('div', 'dk-sepl')); continue; }
    if (it.cap) { m.appendChild(el('div', 'dk-cap')).textContent = it.cap; continue; }
    const b = btn('dk-it', (it.icon ? ic(it.icon) : '') + '<span></span>' + (it.hint || it.sub ? '<span class="dk-hint"></span>' : ''));
    b.setAttribute('role', 'menuitem'); b.querySelector('span:not(.dk-hint)').textContent = it.label;
    if (it.hint) b.querySelector('.dk-hint').textContent = it.hint;
    if (it.sub) b.querySelector('.dk-hint').innerHTML = ic('right');
    if (it.disabled) b.disabled = true;
    else if (it.sub) {
      const open = () => {
        if (subEl && subEl.dataset.owner === it.label) return;
        closeSub();
        const sm = menuBody(it.sub, true); sm.dataset.owner = it.label; root.appendChild(sm); subEl = sm;
        const r = b.getBoundingClientRect(), w = sm.offsetWidth, h = sm.offsetHeight;
        sm.style.left = (r.right + w + 8 > innerWidth ? Math.max(8, r.left - w - 2) : r.right + 2) + 'px';
        sm.style.top = clamp(r.top - 6, BAR_H + 4, Math.max(BAR_H + 4, innerHeight - h - 8)) + 'px';
      };
      b.addEventListener('click', open);
      b.addEventListener('mouseenter', open);
    } else {
      b.addEventListener('mouseenter', closeSub);
      b.addEventListener('click', () => { closeMenu(); it.run && it.run(); });
    }
    m.appendChild(b);
  }
  return m;
}
function showMenu(anchor, items, pos = 'below') {
  closeMenu();
  const m = menuBody(items, false);
  root.appendChild(m); menuEl = m;
  const r = anchor.getBoundingClientRect(), w = m.offsetWidth, h = m.offsetHeight;
  m.style.left = clamp(pos === 'above' ? r.left + r.width / 2 - w / 2 : r.left, 8, innerWidth - w - 8) + 'px';
  m.style.top = (pos === 'above' ? Math.max(BAR_H + 6, r.top - h - 14) : r.bottom + 4) + 'px';
  menuOwner = anchor.classList.contains('dk-mi') ? anchor : null;
  menuOwner?.classList.add('open');
  syncKeys();
}
document.addEventListener('pointerdown', (e) => {
  if (menuEl && !e.target.closest('.dk-menu') && !e.target.closest('.dk-mi')) closeMenu();
  else if (subEl && !e.target.closest('.dk-menu.sub') && !e.target.closest('.dk-it')) closeSub();
  if (launcher && !e.target.closest('.dk-launch') && !e.target.closest('[data-launcher]')) closeLauncher();
}, true);
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  if (menuEl) { closeMenu(); e.stopPropagation(); } else if (launcher) { closeLauncher(); e.stopPropagation(); }
}, true);

/* =====================================================================
 *  Fenster
 * ===================================================================== */
function viewport() { return { w: root.clientWidth, h: root.clientHeight }; }
function defaultRect(id, w, h, dx = 0, dy = 0) {
  const v = viewport();
  const reserve = dockReserve();
  w = Math.min(w, v.w - 40); h = Math.min(h, v.h - BAR_H - reserve);
  return { x: Math.round((v.w - w) / 2 + dx), y: Math.round(BAR_H + Math.max(12, (v.h - BAR_H - reserve - h) / 2) + dy), w, h };
}
function fitRect(r, minW, minH) {
  const v = viewport();
  const w = clamp(r.w, minW, v.w), h = clamp(r.h, minH, v.h - BAR_H);
  // moeglichst ganz sichtbar halten; nur wenn der Bildschirm zu klein ist, ragt etwas heraus
  return { w, h, x: clamp(r.x, 0, Math.max(0, v.w - w)), y: clamp(r.y, BAR_H, Math.max(BAR_H, v.h - h)) };
}

/** Legt ein Fenster an. opts: id, title, icon (Kachel-HTML), cls, rect, minW, minH, head (Element fuer die Titelleiste), onClose */
function createWindow(opts) {
  const w = { id: opts.id, minimized: false, maximized: false, snap: null, restore: null, opts };
  const e = el('div', 'dk-win ' + (opts.cls || ''));
  e.dataset.win = opts.id; e.setAttribute('role', 'dialog'); e.setAttribute('aria-label', opts.title);
  const head = el('div', 'dk-wh');
  const title = el('div', 'dk-wt', (opts.icon || '') + '<span></span>'); title.lastElementChild.textContent = opts.title; title.lastElementChild.dataset.keysLabel = t('desktop.keysBadge');   // Marke "Tastatur" am Fensterrand (desktop.css: content: attr)
  head.appendChild(title);
  const extra = el('div', 'dk-wx'); if (opts.head) extra.appendChild(opts.head); head.appendChild(extra);
  const ctl = el('div', 'dk-wc');
  const bMin = btn('', ic('minus'), t('desktop.minimize')), bMax = btn('', ic('square'), t('desktop.maximize')), bCl = btn('dk-cl', ic('x'), t('common.close'));
  if (opts.dialog) ctl.append(bCl); else ctl.append(bMin, bMax, bCl);        // ein Dialog-Fenster kennt weder Minimieren noch Maximieren (und keinen Dock-Eintrag)
  head.appendChild(ctl);
  const body = el('div', 'dk-wb');
  e.append(head, body);
  if (!opts.dialog || opts.resizable) for (const d of ['n', 's', 'w', 'e', 'nw', 'ne', 'sw', 'se']) { const z = el('div', 'dk-rz ' + d); z.dataset.dir = d; e.appendChild(z); }
  w.dialog = !!opts.dialog; w.owner = opts.owner || null;
  if (opts.dialog) e.tabIndex = -1;
  w.el = e; w.body = body; w.head = head; w.bMax = bMax;
  const saved = opts.dialog ? null : ls.get('rect.' + (opts.rectKey || opts.id), null);
  const rect = fitRect(saved || opts.rect, opts.minW || 320, opts.minH || 220);
  if (opts.cascade) { rect.x += opts.cascade; rect.y += opts.cascade; Object.assign(rect, fitRect(rect, opts.minW || 320, opts.minH || 220)); }
  Object.assign(e.style, { left: rect.x + 'px', top: rect.y + 'px', width: rect.w + 'px', height: opts.autoHeight ? 'auto' : rect.h + 'px' });
  if (opts.autoHeight) e.style.maxHeight = (viewport().h - BAR_H - dockReserve()) + 'px';
  root.appendChild(e); wins.set(opts.id, w);
  // Rahmen des neuen Fensters zeigt die Animation nur einmal; danach darf keine Animation die Transform-Matrix halten (Maus-Koordinaten)
  e.addEventListener('animationend', (ev) => { if (ev.target === e) e.style.animation = 'none'; });

  e.addEventListener('pointerdown', () => focusWin(w), true);
  bMin.addEventListener('click', () => minimizeWin(w));
  bMax.addEventListener('click', () => toggleMax(w));
  bCl.addEventListener('click', () => { if (!w.dialog) closeWin(w); else if (!w.opts.onRequestClose || w.opts.onRequestClose()) closeWin(w, true); });   // Dialog: Schliessen ist eine Abbruch-Anfrage, die der Dialog ablehnen darf
  if (!opts.dialog) head.addEventListener('dblclick', (ev) => { if (!ev.target.closest('button, input')) toggleMax(w); });
  wm.wireDrag(w, head); wireResize(w);
  focusWin(w);
  renderDock();
  return w;
}

/** Das Fenster ganz oben in der Kette "gehoert zu" (ein Dialog-Fenster nennt das Fenster, das es geoeffnet hat) */
function rootOf(w) { while (w.owner && wins.has(w.owner.id)) w = w.owner; return w; }
/** Die Dialog-Fenster eines Fensters (und deren Dialoge), in der Reihenfolge des Anlegens */
const descendants = (x) => [...wins.values()].filter((o) => o.owner === x).flatMap((o) => [o, ...descendants(o)]);
/** Stapelfolge einer Kette: der Eigentuemer, dann seine Dialoge; der Zweig mit dem Fenster, das nach vorn kommt, jeweils zuletzt (ein Fenster liegt nie unter seinem eigenen Dialog) */
function stackOrder(x, branch) {
  const kids = [...wins.values()].filter((o) => o.owner === x && !o.minimized).sort((a, b) => (branch.has(a) ? 1 : 0) - (branch.has(b) ? 1 : 0));
  return [x, ...kids.flatMap((k) => stackOrder(k, branch))];
}
function focusWin(w) {
  if (w.minimized) return;
  const branch = new Set(); for (let o = w; o; o = o.owner && wins.has(o.owner.id) ? o.owner : null) branch.add(o);
  for (const o of stackOrder(rootOf(w), branch)) o.el.style.zIndex = ++zTop;
  for (const o of wins.values()) if (o.opts.top && !o.minimized) o.el.style.zIndex = ++zTop;      // eine Freigabe (zweiter Faktor) bleibt vor den anderen Fenstern
  for (const o of wins.values()) o.el.classList.toggle('active', o === w);
  if (w.opts.onFocus) w.opts.onFocus(w);       // Sitzungsfenster: die Sitzung wird die aktive (Tasten gehen an sie)
  // Ein Dialog-Fenster sperrt nichts, nimmt aber den Fokus in sich auf, damit Tab und Escape dort wirken (Klick auf die Titelleiste eingeschlossen)
  if (w.dialog && !w.el.contains(document.activeElement)) w.el.focus({ preventScroll: true });
  renderBar();
}
function minimizeWin(w) {
  w.minimized = true; w.el.classList.add('min'); w.el.classList.remove('active');
  if (w.opts.onVisible) w.opts.onVisible(false);          // Sitzung im Hintergrund: pausiert
  // eine andere sichtbare Sitzung bekommt die Tasten
  const next = [...sessWins.values()].filter((o) => !o.minimized).sort((a, b) => b.el.style.zIndex - a.el.style.zIndex)[0];
  if (w.session && Sessions.active === w.session && next) focusWin(next);
  renderDock(); renderBar();
}
function restoreWin(w) { w.minimized = false; w.el.classList.remove('min'); if (w.opts.onVisible) w.opts.onVisible(true); focusWin(w); renderDock(); }
/** Maximiert (CSS-Klasse max) oder nicht; die Knopf-Beschriftung folgt. Wer maximiert oder wiederherstellt, ruft toggleMax: der Fenstermanager merkt sich dabei den Zustand davor. */
function setMax(w, on) {
  w.maximized = on; w.el.classList.toggle('max', on);
  w.bMax.innerHTML = ic(on ? 'restore' : 'square'); w.bMax.setAttribute('aria-label', on ? t('desktop.restore') : t('desktop.maximize'));
}
const toggleMax = (w) => wm.toggleMax(w);
/** Schliesst ein Fenster samt seinen Dialog-Fenstern. Ohne force fragt jeder Dialog erst (onRequestClose: false = bleibt offen, zum Beispiel der Editor mit ungespeicherten Aenderungen); dann bleibt auch das Fenster offen. */
function closeWin(w, force = false) {
  if (!wins.has(w.id)) return;
  for (const k of [...wins.values()].filter((o) => o.owner === w)) {
    if (!force && k.opts.onRequestClose && !k.opts.onRequestClose()) { focusWin([k, ...descendants(k)].sort((a, b) => a.el.style.zIndex - b.el.style.zIndex).pop()); return; }     // der Dialog hat abgelehnt (zum Beispiel eine Rueckfrage geoeffnet): das vorderste Fenster seines Zweigs zeigen
    closeWin(k, true);
  }
  w.opts.onClose && w.opts.onClose(w);
  w.el.remove(); wins.delete(w.id); renderDock(); renderBar();
}
const saveRect = (w) => { if (!w.maximized && !w.dialog && !w.snap) ls.set('rect.' + w.id, { x: w.el.offsetLeft, y: w.el.offsetTop, w: w.el.offsetWidth, h: w.el.offsetHeight }); };

function wireResize(w) {
  for (const z of w.el.querySelectorAll('.dk-rz')) z.addEventListener('pointerdown', (ev) => {
    if (ev.button !== 0) return;
    ev.preventDefault(); ev.stopPropagation(); focusWin(w); wm.forget(w);
    const d = z.dataset.dir, sx = ev.clientX, sy = ev.clientY, r0 = { x: w.el.offsetLeft, y: w.el.offsetTop, w: w.el.offsetWidth, h: w.el.offsetHeight };
    const minW = w.opts.minW || 320, minH = w.opts.minH || 220, v = viewport();
    z.setPointerCapture(ev.pointerId); w.el.classList.add('resizing');
    const move = (m) => {
      const dx = m.clientX - sx, dy = m.clientY - sy;
      let { x, y, w: ww, h: hh } = r0;
      if (d.includes('e')) ww = r0.w + dx;
      if (d.includes('s')) hh = r0.h + dy;
      if (d.includes('w')) { ww = r0.w - dx; }
      if (d.includes('n')) { hh = r0.h - dy; }
      ww = clamp(ww, minW, v.w); hh = clamp(hh, minH, v.h - BAR_H);
      if (d.includes('w')) x = r0.x + r0.w - ww;
      if (d.includes('n')) y = Math.max(BAR_H, r0.y + r0.h - hh);
      Object.assign(w.el.style, { left: x + 'px', top: y + 'px', width: ww + 'px', height: hh + 'px' });
    };
    const up = () => { z.removeEventListener('pointermove', move); z.removeEventListener('pointerup', up); z.removeEventListener('pointercancel', up); w.el.classList.remove('resizing'); saveRect(w); };
    z.addEventListener('pointermove', move); z.addEventListener('pointerup', up); z.addEventListener('pointercancel', up);
  });
}
// Browserfenster wird kleiner: Fenster zurueck in den sichtbaren Bereich holen
window.addEventListener('resize', () => {
  if (!root) return;
  for (const w of wins.values()) {
    if (w.maximized) continue;
    if (w.snap) { wm.reflow(w); continue; }          // eingerastet: bleibt in seiner Zone
    const r = fitRect({ x: w.el.offsetLeft, y: w.el.offsetTop, w: w.el.offsetWidth, h: w.el.offsetHeight }, w.opts.minW || 320, w.opts.minH || 220);
    Object.assign(w.el.style, { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px' });
    if (w.opts.autoHeight) w.el.style.maxHeight = (viewport().h - BAR_H - dockReserve()) + 'px'; else w.el.style.height = r.h + 'px';
  }
});

/* ---------- Fenster-Fabrik fuer Dialoge (desktop-window.js, docs/FILES-EXPLORER.md): ein Fenster derselben Art, aber ohne Dock-Eintrag, Minimieren und Maximieren ---------- */
let dlgSeq = 0;
/**
 * Legt ein Dialog-Fenster an: mittig ueber dem Fenster, das es geoeffnet hat (owner: ein Element darin; sonst das Fenster mit dem Fokus oder das vordere), leicht versetzt bei mehreren.
 * system: gehoert keinem Fenster (nur die Mitte richtet sich danach). top: liegt immer ueber den anderen Fenstern, auch wenn eines davon angeklickt wird (Freigabe mit dem zweiten Faktor).
 * Hoehe 0 = so hoch wie der Inhalt. onRequestClose(): darf das Fenster zu? (Schliessen-Taste und Schliessen des Eigentuemers fragen so); onClose(): es ist zu. Gibt null zurueck, solange der Desktop nicht steht.
 */
function dialogFrame({ title, w = 480, h = 0, resizable = false, owner = null, cls = '', top = false, system = false, onClose, onRequestClose }) {
  if (!root) return null;
  const from = owner || document.activeElement;
  const front = [...wins.values()].filter((o) => !o.minimized).sort((a, b) => b.el.style.zIndex - a.el.style.zIndex)[0] || null;
  const ownerWin = [...wins.values()].find((o) => from && from.nodeType && o.el.contains(from)) || (owner ? null : [...wins.values()].find((o) => o.el.classList.contains('active'))) || front;
  const link = system ? null : ownerWin;
  const n = [...wins.values()].filter((o) => o.dialog && o.owner === link).length;
  const v = viewport();
  w = Math.min(w, v.w - 40); if (h) h = Math.min(h, v.h - BAR_H - dockReserve());
  const cx = ownerWin ? ownerWin.el.offsetLeft + ownerWin.el.offsetWidth / 2 : v.w / 2, cy = ownerWin ? ownerWin.el.offsetTop + ownerWin.el.offsetHeight / 2 : (BAR_H + v.h) / 2;
  const win = createWindow({ id: 'dlg:' + (++dlgSeq), title, dialog: true, resizable, owner: link, top, cls: 'dk-dlgwin ' + cls, rect: { x: cx - w / 2, y: cy - (h || 260) / 2, w, h: h || 260 },
    minW: resizable ? 360 : 0, minH: resizable ? 240 : 0, autoHeight: !h, onClose, onRequestClose });
  const label = win.head.querySelector('.dk-wt span');
  // Der Inhalt kommt oft erst spaeter (Antwort vom Server) und macht das Fenster hoeher: es rutscht dann zurueck in den sichtbaren Bereich, statt unten abgeschnitten zu bleiben
  if (!h && typeof ResizeObserver === 'function') new ResizeObserver(() => {
    const r = fitRect({ x: win.el.offsetLeft, y: win.el.offsetTop, w: win.el.offsetWidth, h: win.el.offsetHeight }, 0, 0);
    if (r.y !== win.el.offsetTop || r.x !== win.el.offsetLeft) Object.assign(win.el.style, { left: r.x + 'px', top: r.y + 'px' });
  }).observe(win.el);
  return {
    el: win.el, body: win.body,
    setTitle(x) { if (typeof x === 'string') label.textContent = x; else label.replaceChildren(x); win.el.setAttribute('aria-label', label.textContent); },
    /** Nach dem Einfuellen des Inhalts: mittig ueber den Eigentuemer setzen (erst jetzt ist die Hoehe bekannt), jedes weitere Fenster etwas versetzt */
    place() { const r = fitRect({ x: cx - win.el.offsetWidth / 2 + 26 * n, y: cy - win.el.offsetHeight / 2 + 26 * n, w: win.el.offsetWidth, h: win.el.offsetHeight }, 0, 0); Object.assign(win.el.style, { left: r.x + 'px', top: r.y + 'px' }); },
    focus: () => focusWin(win),
    close: () => closeWin(win, true),
  };
}

/* =====================================================================
 *  Menueleiste
 * ===================================================================== */
/** Die Schluessel von MENUS sind nur Kennungen (data-menu); angezeigt wird die Beschriftung daraus */
const MENU_LABELS = { Desktop: tk('desktop.desktop'), Hosts: tk('desktop.hosts'), Dateien: tk('desktop.files'), Apps: tk('desktop.apps'), System: tk('desktop.system'), Hilfe: tk('desktop.help') };
const MENUS = () => ({
  Desktop: [
    { label: t('desktop.openStart'), icon: 'home', run: () => openHome() },
    { label: t('desktop.minimizeAll'), icon: 'minus', run: () => wins.forEach((w) => !w.dialog && minimizeWin(w)) },
    { label: t('desktop.showAll'), icon: 'restore', run: () => wins.forEach((w) => w.minimized && restoreWin(w)) },
    { label: t('desktop.sessionsSideBySide'), icon: 'grid', run: () => tileSessions(), disabled: sessWins.size < 2 },
    { label: t('desktop.sessionsOverlap'), icon: 'restore', run: () => cascadeSessions(), disabled: sessWins.size < 2 },
    { sep: true },
    { label: t('desktop.newConnectionDots'), icon: 'plus', run: () => ctx.add() },
    { label: t('desktop.quickConnectDots'), icon: 'monitor', run: () => ctx.quick() },
  ],
  Hosts: [
    ...ctx.state.hosts.map((h) => ({ label: h.name, icon: 'server', hint: stateText(!!h.online), run: () => openHome('hosts') })),
    ...(ctx.state.hosts.length ? [{ sep: true }] : []),
    { label: t('desktop.manageHosts'), icon: 'server', run: () => openHome('hosts') },
    { label: t('desktop.newHostDots'), icon: 'plus', run: () => ctx.addHost() },
  ],
  Dateien: [
    { label: t('desktop.openFiles'), icon: 'folder', run: () => openFiles() },
    { label: t('desktop.searchDots'), icon: 'search', run: () => openFiles('', true) },
  ],
  Apps: [
    { label: t('desktop.openLauncher'), icon: 'grid', run: () => openLauncher() },
    { sep: true },
    { label: t('desktop.connectDesktopDots'), icon: 'monitor', run: () => pickConnection('desktop', $('[data-menu="Apps"]', root)) },
    { label: t('desktop.connectVncDots'), icon: 'monitor', run: () => pickConnection('vnc', $('[data-menu="Apps"]', root)) },
    { label: t('desktop.connectTerminalDots'), icon: 'terminal', run: () => pickConnection('terminal', $('[data-menu="Apps"]', root)) },
    { label: t('prog.claude'), icon: 'spark', run: () => runApp('claude', $('[data-menu="Apps"]', root)) },
    { label: t('prog.codex'), icon: 'code', run: () => runApp('codex', $('[data-menu="Apps"]', root)) },
    { sep: true },
    ...shownApps().filter((a) => a.mod).map((a) => ({ label: a.name, icon: a.ic, run: () => runApp(a.id) })),
    ...APPS.filter((a) => a.soon).map((a) => ({ label: a.name, icon: a.ic, hint: t('desktop.soonAvail'), disabled: true })),
  ],
  System: [
    { label: t('desktop.accountDots'), icon: 'key', run: () => ctx.account() },
    { label: t('desktop.devicesDots'), icon: 'shield', run: () => ctx.devices() },
    { label: t('desktop.settings'), icon: 'sliders', run: () => openHome('einstellungen') },
    { label: t('desktop.systemOverview'), icon: 'activity', run: () => openHome('system') },
    { label: t('desktop.nettoolsDots'), icon: 'globe', run: () => ctx.nettools() },
    { sep: true },
    { label: t('desktop.signOut'), icon: 'power', run: () => ctx.signOut() },
  ],
  Hilfe: [
    { label: t('desktop.aboutJulos'), icon: 'info', run: () => openAbout() },
    { label: t('desktop.licenses'), icon: 'info', run: () => window.open('/licenses.txt', '_blank', 'noopener') },
    { sep: true },
    { label: t('desktop.sourceAndDocs'), icon: 'info', run: () => window.open('https://github.com/Juloc/JulOS', '_blank', 'noopener') },
  ],
});

function buildBar() {
  const bar = el('header', 'dk-bar');
  const logo = btn('dk-logo', LOGO + '<span>JulOS</span>', t('desktop.openStart')); logo.addEventListener('click', () => openHome());
  bar.appendChild(logo);
  const menus = el('div', 'dk-menus'); menus.id = 'dkMenus';
  bar.appendChild(menus);
  bar.appendChild(el('div', 'dk-sp'));
  const sess = btn('dk-ic dk-sess', '<span></span>'); sess.hidden = true; sess.id = 'dkSess'; sess.addEventListener('click', () => sessionMenu2(sess));
  const search = btn('dk-ic', ic('search'), t('common.search')); search.dataset.launcher = '1'; search.addEventListener('click', () => (launcher ? closeLauncher() : openLauncher()));
  const clock = el('div', 'dk-clock'); clock.id = 'dkClock';
  const user = btn('dk-ic', '<span class="dk-avatar"></span>', t('desktop.account')); user.id = 'dkUser';
  user.addEventListener('click', () => showMenu(user, [
    { cap: ctx.state.user || '' },
    { label: t('desktop.accountDots'), icon: 'key', run: () => ctx.account() },
    { label: t('desktop.devicesDots'), icon: 'shield', run: () => ctx.devices() },
    { label: t('desktop.settings'), icon: 'sliders', run: () => openHome('einstellungen') },
    { sep: true },
    { label: t('desktop.signOut'), icon: 'power', run: () => ctx.signOut() },
  ]));
  const keys = el('div', 'dk-keys', ic('keyboard') + '<span></span>'); keys.id = 'dkKeys'; keys.hidden = true;
  // Hintergrund-Aufgaben (hosttasks.js): ein dezenter Eintrag mit Fortschrittsbalken; ein Klick oeffnet das Panel (eine Aufgabe) oder eine Liste (mehrere)
  const tasks = btn('dk-ic dk-tasks', '<span class="dk-tt"></span><span class="dk-tbar"><i></i></span>'); tasks.hidden = true; tasks.id = 'dkTasks';
  tasks.addEventListener('click', () => tasksMenu(tasks));
  bar.append(keys, tasks, sess, search, clock, user);
  return bar;
}

/* ---------- Hintergrund-Aufgaben (hosttasks.js) ----------
 * Eine laufende Aktion sperrt nichts: der Eintrag in der Leiste zeigt Text und Balken, das Panel ist ein ganz normales verschiebbares Fenster (nicht modal), das man schliessen darf,
 * ohne dass die Aktion endet. Mehrere Aufgaben (auch auf verschiedenen Hosts) haben je ihr Fenster. */
const taskWins = new Map();           // Auftrag -> Fenster
const hostOfTask = (task) => ctx.state.hosts.find((h) => h.id === task.host.id) || null;
function renderTaskChip() {
  const b = $('#dkTasks', root); if (!b) return;
  const list = ctx.tasks ? ctx.tasks.list() : [];
  b.hidden = !list.length;
  if (!list.length) return;
  const pct = barPercent(list), running = list.some((x) => !jobFinal(x));
  b.dataset.tone = barTone(list);
  b.title = list.map(chipText).join('\n');
  b.querySelector('.dk-tt').textContent = barText(list);
  const bar = b.querySelector('.dk-tbar');
  bar.hidden = !running; bar.classList.toggle('ind', pct === null);
  bar.firstElementChild.style.width = pct === null ? '' : pct + '%';
}
function tasksMenu(anchor) {
  const list = ctx.tasks.list();
  if (list.length === 1) { openTask(list[0]); return; }
  showMenu(anchor, [{ cap: t('hosttask.menuTitle') }, ...list.map((task) => ({ label: chipText(task), icon: jobFinal(task) ? (task.state === 'failed' ? 'x' : 'check') : 'refresh', run: () => openTask(task) })),
    { sep: true }, { label: t('hosttask.clearDone'), icon: 'check', disabled: !list.some(jobFinal), run: () => ctx.tasks.dismissFinished() }]);
}
/** Das Fortschritts-Panel einer Aufgabe: ein Fenster (nicht modal) mit Live-Ausgabe, Fehlerzusammenfassung und aufklappbarem Protokoll */
export function openTask(task) {
  let w = taskWins.get(task.job);
  if (w) { w.minimized ? restoreWin(w) : focusWin(w); return; }
  w = createWindow({ id: 'task-' + task.job, rectKey: 'task', title: chipText(task), rect: defaultRect('task', 520, 460), minW: 360, minH: 260, cascade: 26 * taskWins.size, cls: 'dk-taskwin',
    onClose: () => taskWins.delete(task.job) });
  taskWins.set(task.job, w);
  w.body.innerHTML = '<div class="dk-tpb"></div>';
  w.body.addEventListener('click', (e) => {
    const a = e.target.closest('[data-task-act]'); if (!a) return;
    const cur = ctx.tasks.get(task.job), host = cur && hostOfTask(cur);
    if (a.dataset.taskAct === 'repair' && host) ctx.hostAction(host, 'updates.repair', null, { onDone: () => ctx.info.load(host, { fresh: true }) });
  });
  renderTaskWin(w, task);
  ctx.tasks.markSeen(task.job);
}
function renderTaskWin(w, task) {
  const sig = [task.state, task.progress, task.done, task.total, task.tail.length, task.tail[task.tail.length - 1] || '', task.error, task.result ? 1 : 0].join('|');
  if (w.sig === sig) return;
  w.sig = sig;
  w.el.setAttribute('aria-label', chipText(task));
  w.head.querySelector('.dk-wt span').textContent = chipText(task);
  const box = w.body.querySelector('.dk-tpb');
  const open = [...box.querySelectorAll('details')].map((d) => d.open), top = box.scrollTop;
  const live = box.querySelector('[data-live]'), atEnd = !live || live.scrollTop + live.clientHeight >= live.scrollHeight - 8;
  box.innerHTML = panelHtml(task, ctx.esc, hostOfTask(task));
  box.querySelectorAll('details').forEach((d, i) => { d.open = !!open[i]; });
  box.scrollTop = top;
  const nl = box.querySelector('[data-live]'); if (nl && atEnd) nl.scrollTop = nl.scrollHeight;
}
/** Zustand der Aufgaben hat sich geaendert (shell.js): Leiste und offene Panels nachziehen */
export function tasksChanged() {
  if (!root) return;
  renderTaskChip();
  for (const [job, w] of taskWins) { const task = ctx.tasks.get(job); if (task) renderTaskWin(w, task); else closeWin(w); }
}
/** Eine Aufgabe ist fertig: Hinweis mit Ergebnis und Weg zum Panel (bleibt laenger stehen als ein gewoehnlicher Hinweis) */
export function taskFinished(task) {
  if (!root) return;
  const n = finishNotice(task);
  notify(n.detail ? `${n.text} – ${n.detail}` : n.text, 12000, { label: t('hosttask.show'), run: () => openTask(task) });
}
/** Eine Aufgabe wurde gestartet: ein kurzer Hinweis, dass sie im Hintergrund laeuft */
export function taskStarted(task) { if (root) notify(t('hosttask.started', { action: chipText(task) }), 4200, { label: t('hosttask.show'), run: () => openTask(task) }); }

/* ---------- Tastatur: wer bekommt die Tasten? ----------
 * Solange ein Fernfenster das aktive Fenster ist (und kein Menue oder Launcher offen), gehen alle Tasten in die Fernsteuerung: app.js nimmt dazu das
 * versteckte Tastaturfeld in den Fokus (auch wenn der Fokus auf einem Knopf lag). Hier wird der Fokus nach jedem Wechsel zurueckgegeben oder genommen
 * und gezeigt, wohin getippt wird. */
const keysToRemote = () => remoteActive() && !menuEl && !launcher;
function syncKeys() {
  setTimeout(() => {
    if (!root) return;
    if (keysToRemote()) remoteApi.focus(); else remoteApi.blurKeys();
    markKeys();
  }, 0);
}
function markKeys() {
  if (!root) return;
  const has = remoteApi.hasKeys() && keysToRemote();
  const w = activeW();
  for (const o of wins.values()) o.el.classList.toggle('keys', o === w && has);
  const k = $('#dkKeys', root); if (!k) return;
  k.hidden = !w; k.classList.toggle('on', has);
  k.querySelector('span').textContent = has ? t('desktop.keyboardTo', { title: (Sessions.active && Sessions.active.title) || t('desktop.session') }) : t('desktop.keyboardNone');
  k.title = has ? t('desktop.keysToRemote') : t('desktop.keysToNone');
}

/** Das Fernfenster ist vorn und sichtbar: dann zeigt die Leiste seine Menues statt der normalen (wie das Programm-Menue am Mac) */
function remoteActive() {
  const w = activeW();
  return !!(w && !w.minimized && w.el.classList.contains('active'));
}
const remoteDeps = {
  win: () => activeW(),
  toggleMax: () => { const w = activeW(); if (w) toggleMax(w); },
  fullscreen: () => {
    const w = activeW(); if (!w) return;
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else w.el.requestFullscreen({ navigationUI: 'hide' }).catch(() => notify(t('desktop.fullscreenFail')));
  },
  end: () => { const w = activeW(); if (w) closeWin(w); },
  /** Taskleiste der VM im Dock: Menue der Verbindung (desktop-remote.js) */
  taskbar: {
    session: () => { const w = activeW(); return w && w.session && w.session.setTaskbar && w.session.display ? w.session : null; },
    mark: () => { const s = remoteDeps.taskbar.session(); if (s) taskbar.mark(s); },
    off: () => { const s = remoteDeps.taskbar.session(); if (s) s.setTaskbar(null); },
  },
};
/** Fernmenues laufen, danach bekommt das Fernbild die Tastatur zurueck */
const afterRemote = (items) => items && (items.then ? items.then(afterRemote) : items.map((it) => (it.run ? { ...it, run: () => { it.run(); setTimeout(() => remoteApi.focus(), 80); } } : it.sub ? { ...it, sub: afterRemote(it.sub) } : it)));
let barKey = '';
function renderMenus() {
  const box = $('#dkMenus', root); if (!box) return;
  const remote = remoteActive();
  const sc = remote ? activeConn() : null;
  const key = remote ? 'r:' + Sessions.active.id + ':' + sc.kind + ':' + Sessions.active.title : 'n';
  if (key === barKey) return;
  barKey = key; closeMenu(); box.replaceChildren();
  const add = (name, cls, items, label = name) => {
    const b = btn('dk-mi' + (cls ? ' ' + cls : ''), ''); b.textContent = label; b.dataset.menu = name;
    const open = async () => { const list = await items(); if (barKey === key) showMenu(b, list); };
    b.addEventListener('click', () => (menuOwner === b ? closeMenu() : open()));
    b.addEventListener('mouseenter', () => { if (menuEl && menuOwner && menuOwner !== b) open(); });   // wie in einer Menueleiste: bei offenem Menue wechselt Darueberfahren
    box.appendChild(b);
  };
  if (!remote) { const menus = MENUS(); for (const name of Object.keys(menus)) add(name, '', () => MENUS()[name], t(MENU_LABELS[name])); return; }
  add(Sessions.active.title, 'dk-appname', () => afterRemote(sessionMenu(remoteDeps)));
  for (const name of remoteMenuNames(sc.kind)) add(name, '', () => afterRemote(remoteMenuItems(name, remoteDeps)), remoteMenuLabel(name));
}
function renderBar() {
  renderMenus();
  renderPills();
  syncTaskbar();
  syncKeys();
  const s = $('#dkSess', root); if (!s) return;
  const n = sessWins.size, a = Sessions.active;
  s.hidden = !n;
  if (n) s.querySelector('span').textContent = n === 1 ? (a ? a.title : t('desktop.session')) : t('desktop.sessionsN', { n });
}
/** Taskleiste der VM: im Dock steht die der Sitzung im Fokus; Platz am Rand belegt die im Fokus oder, sonst, eine andere sichtbare (damit der Fokuswechsel die Groesse der VM nicht aendert) */
function syncTaskbar() {
  if (!taskbar) return;
  const has = [...sessWins.values()].filter((w) => !w.minimized && w.session.taskbar && w.session.taskbar());
  const act = has.find((w) => w.el.classList.contains('active')) || null;
  for (const w of sessWins.values()) w.el.classList.toggle('dk-tb-on', w === act);
  taskbar.sync(act ? act.session : null, (act || has[0] || {}).session || null);
}
/** Menue der offenen Sitzungen (Menueleiste): wechseln, anordnen */
function sessionMenu2(anchor) {
  const items = [{ cap: t('desktop.openSessions') }];
  for (const w of sessWins.values()) {
    const s = w.session;
    items.push({ label: s.title, icon: s.kind === 'terminal' ? 'terminal' : 'monitor', hint: s.stateText, run: () => { w.minimized ? restoreWin(w) : focusWin(w); } });
  }
  items.push({ sep: true }, { label: t('desktop.arrangeSideBySide'), icon: 'grid', run: () => tileSessions(), disabled: sessWins.size < 2 }, { label: t('desktop.arrangeOverlap'), icon: 'restore', run: () => cascadeSessions(), disabled: sessWins.size < 2 });
  showMenu(anchor, items);
}
const fmtClock = () => fmtDate(new Date(), { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
function tickClock() {
  const c = $('#dkClock', root); if (c) c.textContent = fmtClock();
  const d = new Date();
  for (const el0 of root.querySelectorAll('.dk-hero-time')) el0.textContent = fmtTime(d);
  for (const el0 of root.querySelectorAll('.dk-hero-date')) el0.textContent = fmtDay(d, { weekday: 'long', day: 'numeric', month: 'long' });
}

/* =====================================================================
 *  Dock
 * ===================================================================== */
function buildDock() {
  const row = el('div', 'dk-dockrow');
  const dock = el('nav', 'dk-dock'); dock.setAttribute('aria-label', t('desktop.dock'));
  const item = (id, name, a, extra) => {
    const b = btn('dk-di', `${ic(a.ic)}<span class="dk-dot"></span><span class="dk-tip"></span>`, name);
    b.querySelector('.dk-tip').textContent = name; b.dataset.dock = id; b.style.setProperty('--g1', a.g[0]); b.style.setProperty('--g2', a.g[1]);
    b.addEventListener('click', () => dockClick(id, b));
    if (extra) b.dataset.launcher = '1';
    return b;
  };
  dock.append(
    item('launcher', t('desktop.launcher'), { ic: 'grid', g: ['#8f6af0', '#6a58e6'] }, true),
    item('home', t('desktop.start'), appById('home')),
    item('files', t('desktop.files'), appById('files')),
    item('hosts', t('desktop.hosts'), appById('hosts')),
    el('div', 'dk-dsep'),
    item('desktop', t('desktop.desktopRdp'), appById('desktop')),
    item('vnc', t('desktop.desktopVnc'), appById('vnc')),
    item('terminal', t('desktop.terminalSsh'), appById('terminal')),
    item('claude', t('prog.claude'), appById('claude')),
    item('codex', t('prog.codex'), appById('codex')),
    modDock(item),
    el('div', 'dk-dsep'),
    item('settings', t('desktop.settings'), appById('settings')),
  );
  dock.addEventListener('contextmenu', (e) => { e.preventDefault(); dockMenu(dock); });
  dock.addEventListener('focusin', showDock);
  row.append(dock);
  return row;
}
/** Caddy und DDNS im Dock: erst sichtbar, wenn der Server das Modul eingeschaltet hat (applyModules) */
function modDock(item) {
  const g = el('div', 'dk-dmods'); g.hidden = true;
  g.append(el('div', 'dk-dsep'), item('widgets', t('desktop.widgets'), appById('widgets')), item('caddy', 'Caddy', appById('caddy')), item('ddns', 'DDNS', appById('ddns')));
  for (const b of g.querySelectorAll('.dk-di')) b.hidden = true;
  return g;
}
function applyModules() {
  if (!root) return;
  for (const [id, mod] of [['widgets', 'services'], ['caddy', 'caddy'], ['ddns', 'ddns']]) { const b = $(`.dk-di[data-dock="${id}"]`, root); if (b) b.hidden = !modules[mod]; }
  const g = $('.dk-dmods', root); if (g) g.hidden = !(modules.caddy || modules.ddns || modules.services);
  if (launcher) closeLauncher();
}
function dockClick(id, anchor) {
  if (id === 'launcher') return launcher ? closeLauncher() : openLauncher();
  closeLauncher();
  runApp(id, anchor);
}
function renderDock() {
  if (!root) return;
  const state = {};
  const home = wins.get('home'); if (home) state.home = home;
  const files = wins.get('files'); if (files) state.files = files;
  if (wins.get('home') && nav.view === 'hosts') state.hosts = wins.get('home');
  for (const id of ['caddy', 'ddns']) { const w = wins.get(id); if (w) state[id] = w; }
  for (const w of sessWins.values()) { const k = tileKind(w.session.conn); if (!state[k] || (state[k].minimized && !w.minimized)) state[k] = w; }
  for (const b of root.querySelectorAll('.dk-di[data-dock]')) {
    const w = state[b.dataset.dock];
    b.classList.toggle('run', !!w); b.classList.toggle('hid', !!w && w.minimized);
  }
  renderPills();
}
/** Pillen: eine je Fenster (ohne Dialog-Fenster), in der Reihenfolge des Oeffnens; Verbindungen (Sitzungen) sind farblich abgesetzt */
function renderPills() {
  if (!pills) return;
  const list = [...wins.values()].filter((w) => !w.dialog);
  pills.update(list.map((w) => ({ id: w.id, title: w.el.getAttribute('aria-label') || w.opts.title, icon: w.opts.icon || '', remote: !!w.session, active: w.el.classList.contains('active') && !w.minimized, minimized: w.minimized, hint: w.minimized ? t('desktop.minimized') : '' })));
  root.classList.toggle('dk-haspills', list.length > 0);
}
/** Klick auf eine Pille: ein minimiertes Fenster kommt zurueck, das aktive wird minimiert, jedes andere kommt nach vorn */
function pillClick(id) {
  const w = wins.get(id); if (!w) return;
  if (w.minimized) restoreWin(w); else if (w.el.classList.contains('active')) minimizeWin(w); else focusWin(w);
}
/** Rechtsklick auf eine Pille: Aktionen fuer dieses Fenster, darunter das Menue des Docks wie bisher */
function pillMenu(id, anchor) {
  const w = wins.get(id); if (!w) return;
  showMenu(anchor, [{ cap: w.el.getAttribute('aria-label') || w.opts.title }, ...wm.menuItems(w), { sep: true }, { cap: t('desktop.dock') }, ...dockItems()], 'above');
}


/* ---------- Dock: drei Modi (localStorage "pd.dockMode"): auto = erscheint am unteren Rand (Standard), always = immer sichtbar, toggle = per Knopf einklappbar ---------- */
const DOCK_MODES = [['auto', t('desktop.dockAuto')], ['always', t('desktop.dockAlways')], ['toggle', t('desktop.dockToggle')]];
const EDGE_PX = 6, EDGE_DWELL = 300, HIDE_DELAY = 700;
const PILL_STRIP = 10;     // Streifen am unteren Rand fuer die duennen Pillen (5 px und etwas Luft); maximierte und eingerastete Fenster enden darueber
let dockMode = 'auto', dockCollapsed = false, dockShown = false, edgeTimer = null, hideTimer = null;
function readDockMode() { try { const v = JSON.parse(localStorage.getItem('pd.dockMode')); return DOCK_MODES.some((m) => m[0] === v) ? v : 'auto'; } catch { return 'auto'; } }
/** Platz am unteren Rand, den Fenster freihalten (maximiert und beim Oeffnen): nur wenn der Dock dort steht */
function dockReserve() { return (dockMode === 'always' || (dockMode === 'toggle' && !dockCollapsed) ? 96 : dockMode === 'toggle' ? 30 : PILL_STRIP) + (taskbar ? taskbar.inset().bottom : 0); }
function applyDock() {
  const dock = $('.dk-dockrow', root), knob = $('.dk-knob', root); if (!dock) return;
  const away = dockMode === 'auto' ? !dockShown : dockMode === 'toggle' && dockCollapsed;
  dock.classList.toggle('away', away); root.classList.toggle('dk-folded', away);      // eingeklappt: nur die duennen Pillen
  knob.hidden = dockMode !== 'toggle'; knob.classList.toggle('closed', dockCollapsed);
  knob.setAttribute('aria-label', dockCollapsed ? t('desktop.dockExpand') : t('desktop.dockCollapse')); knob.setAttribute('aria-expanded', dockCollapsed ? 'false' : 'true');
  root.style.setProperty('--dk-reserve', dockReserve() + 'px');
  const ti = taskbar ? taskbar.inset() : { top: 0, right: 0, bottom: 0, left: 0 };       // Platz, den die Taskleiste der VM belegt (desktop-taskbar.js)
  for (const [k, v] of [['t', ti.top], ['r', ti.right], ['b', ti.bottom], ['l', ti.left]]) root.style.setProperty('--dk-tb-' + k, v + 'px');
  root.dataset.dock = dockMode;
}
function setDockMode(m, quiet) {
  if (!DOCK_MODES.some((x) => x[0] === m)) return;
  try { localStorage.setItem('pd.dockMode', JSON.stringify(m)); } catch { /* egal */ }
  dockMode = m; dockShown = m !== 'auto'; clearTimeout(edgeTimer); clearTimeout(hideTimer); edgeTimer = hideTimer = null;
  applyDock(); renderHome_ifOpen();
  if (!quiet && m === 'auto') notify(t('desktop.dockHint'), 4200);
}
function showDock() { if (dockMode !== 'auto') return; clearTimeout(hideTimer); hideTimer = null; if (!dockShown) { dockShown = true; applyDock(); } }       // auch ein schon geplantes Ausblenden entfaellt (Tastaturfokus im Dock)
function hideDock() {
  hideTimer = null;
  if (dockMode !== 'auto' || !dockShown) return;
  if (launcher || (menuEl && !menuOwner)) { hideTimer = setTimeout(hideDock, HIDE_DELAY); return; }   // Menue oder Launcher vom Dock offen: dableiben
  dockShown = false; applyDock();
}
/** Hover am Rand: nur ein schmaler Streifen (6 px) unten und nur nach kurzer Verweilzeit; ohne gedrueckte Taste, damit Ziehen im Fernbild nichts ausloest.
 *  Kein eigenes Element ueber dem Fenster, das Fernfenster bekommt die Maus bis zum letzten Pixel. */
function wireDockHover() {
  document.addEventListener('pointermove', (e) => {
    if (dockMode !== 'auto' || !root || root.hidden) return;
    const edge = e.clientY >= innerHeight - EDGE_PX - taskbar.inset().bottom && !(e.target.closest && e.target.closest('.dk-tb'));      // mit Taskleiste unten: der schmale Streifen ueber ihr ist der Rand, nicht sie selbst
    const keep = edge || (e.target.closest && e.target.closest('.dk-dockrow, .dk-pills, .dk-menu, .dk-launch'));
    if (dockShown) {
      if (keep) { clearTimeout(hideTimer); hideTimer = null; } else if (!hideTimer) hideTimer = setTimeout(hideDock, HIDE_DELAY);
    } else if (edge && e.buttons === 0) { if (!edgeTimer) edgeTimer = setTimeout(() => { edgeTimer = null; showDock(); }, EDGE_DWELL); }
    else { clearTimeout(edgeTimer); edgeTimer = null; }
  }, true);
  document.documentElement.addEventListener('mouseleave', () => { clearTimeout(edgeTimer); edgeTimer = null; if (dockShown && !hideTimer) hideTimer = setTimeout(hideDock, HIDE_DELAY); });
}
/** Die Eintraege des Dock-Menues (Modus waehlen, einklappen); auch unter dem Menue einer Pille */
function dockItems() {
  const items = DOCK_MODES.map(([id, label]) => ({ label, icon: id === dockMode ? 'check' : 'blank', run: () => setDockMode(id) }));
  if (dockMode === 'toggle') items.push({ sep: true }, { label: dockCollapsed ? t('desktop.expand') : t('desktop.collapse'), icon: 'blank', run: toggleDockCollapsed });
  return items;
}
function dockMenu(anchor) { showMenu(anchor, [{ cap: t('desktop.dock') }, ...dockItems()], 'above'); }
function toggleDockCollapsed() { dockCollapsed = !dockCollapsed; ls.set('dockCollapsed', dockCollapsed); applyDock(); }
function buildKnob() {
  const k = btn('dk-knob', ic('chev'), t('desktop.dockCollapse')); k.hidden = true;
  k.addEventListener('click', toggleDockCollapsed);
  k.addEventListener('contextmenu', (e) => { e.preventDefault(); dockMenu(k); });
  return k;
}

/** Startet ein Programm: Fenster oeffnen oder nach vorn holen, Verbindung waehlen */
function runApp(id, anchor) {
  const a = appById(id);
  if (a && a.soon) return notify(t('desktop.comingLater', { name: a.name }));
  if (id === 'home') return openHome();
  if (id === 'settings') return openHome('einstellungen');
  if (id === 'monitor') return openHome('system');
  if (id === 'files') return openFiles();
  if (id === 'hosts') return openHome('hosts');
  if (a && a.mod) return modules[a.mod] ? openModule(id) : notify(t('desktop.moduleOff', { name: a.name }));
  if (id === 'desktop' || id === 'terminal' || id === 'vnc') return pickConnection(id, anchor);
  if (id === 'claude' || id === 'codex') return ctx.program(id, (list) => chooseProgram(id, list, anchor));
}

/** Mehrere Kacheln mit diesem Programm: Menue zur Auswahl (ein Tipp auf "Claude" oeffnet sonst direkt). */
function chooseProgram(id, list, anchor) {
  showMenu(anchor, [{ cap: t('desktop.connectKindTo', { kind: appById(id).name }) }, ...list.map((c) => ({ label: c.name, icon: appById(id).ic, hint: Sessions.find(c.id).length ? t('desktop.nOpen', { n: Sessions.find(c.id).length }) : c.host, run: () => ctx.connect(c) }))],
    anchor && anchor.classList.contains('dk-di') ? 'above' : 'below');
}

/** Verbindung fuer Desktop oder Terminal waehlen: eine = direkt, mehrere = Menue, keine = neue anlegen */
function pickConnection(kind, anchor) {
  const label = kind === 'desktop' ? t('desktop.desktop') : kind === 'vnc' ? t('desktop.vnc') : t('desktop.terminal');
  const list = ctx.state.connections.filter((c) => c.kind === kind);
  const running = [...sessWins.values()].filter((w) => w.session.kind === kind);
  if (running.length && list.length <= 1) { const w = running.find((x) => x.session === Sessions.active) || running[0]; w.minimized ? restoreWin(w) : focusWin(w); return; }
  if (!list.length) { notify(t('desktop.noConnKind', { kind: label })); return ctx.add(); }
  if (list.length === 1) return ctx.connect(list[0]);
  showMenu(anchor, [{ cap: t('desktop.connectKindTo', { kind: label }) }, ...list.map((c) => ({ label: c.name, icon: kind === 'terminal' ? 'terminal' : 'monitor', hint: Sessions.find(c.id).length ? t('desktop.nOpen', { n: Sessions.find(c.id).length }) : c.host, run: () => ctx.connect(c) })),
    ...(list.some((c) => Sessions.find(c.id).length) ? [{ sep: true }, { cap: t('desktop.anotherSessionTo') }, ...list.filter((c) => Sessions.find(c.id).length).map((c) => ({ label: c.name, icon: 'plus', run: () => ctx.connect(c, { extra: true }) }))] : [])],
    anchor.classList.contains('dk-di') ? 'above' : 'below');
}

/* =====================================================================
 *  Launcher
 * ===================================================================== */
let launcher = null;
function closeLauncher() { launcher?.remove(); launcher = null; syncKeys(); }
function openLauncher() {
  closeMenu(); closeLauncher();
  const L = el('div', 'dk-launch'); L.setAttribute('role', 'dialog'); L.setAttribute('aria-label', t('desktop.launcherTitle'));
  L.innerHTML = `<h2>${LOGO}<span>${t('desktop.launcherTitle')}</span></h2>
    <label class="dk-q">${ic('search')}<input type="text" placeholder="${t('desktop.searchPlaceholder')}" autocomplete="off" spellcheck="false"></label>
    <div class="dk-res"></div>
    <div class="dk-foot"><button type="button" class="dk-b" data-f="signout" aria-label="${t('desktop.signOut')}" title="${t('desktop.signOut')}">${ic('power')}</button><button type="button" class="dk-b" data-f="devices" aria-label="${t('desktop.devices')}" title="${t('desktop.devices')}">${ic('user')}</button></div>`;
  root.appendChild(L); launcher = L; syncKeys();
  const input = $('input', L), res = $('.dk-res', L);
  let sel = 0, hits = [];
  const render = () => {
    const q = input.value.trim().toLowerCase();
    res.replaceChildren();
    if (!q) {
      const g = el('div', 'dk-grid'); hits = [];
      for (const a of shownApps()) {
        const b = btn('dk-app', tile(a.g, a.ic) + '<span class="dk-n"></span>' + (a.soon ? `<span class="dk-tag">${t('desktop.soon')}</span>` : '')); b.querySelector('.dk-n').textContent = a.name;
        if (a.soon) { b.setAttribute('aria-disabled', 'true'); b.title = t('desktop.soonTitle'); }
        b.addEventListener('click', () => { closeLauncher(); runApp(a.id, b); });
        g.appendChild(b);
      }
      res.appendChild(g); return;
    }
    hits = [
      ...shownApps().filter((a) => a.name.toLowerCase().includes(q) || (a.sub || '').toLowerCase().includes(q)).map((a) => ({ name: a.name, sub: a.soon ? t('desktop.soonAvail') : a.sub || t('desktop.program'), g: a.g, ic: a.ic, soon: a.soon, run: (anchor) => runApp(a.id, anchor) })),
      ...ctx.state.connections.filter((c) => (c.name + ' ' + c.host).toLowerCase().includes(q)).map((c) => ({ name: c.name, sub: subName(c), g: appOf(c).g, ic: appOf(c).ic, run: () => ctx.connect(c) })),
      // Sieht die Eingabe nach einer Adresse aus: Quick Connect ohne Speichern anbieten
      ...(looksLikeAddress(input.value) ? [{ name: t('desktop.quickConnectTo', { address: input.value.trim() }), sub: t('desktop.withoutSaving'), g: appById('desktop').g, ic: 'plus', run: () => ctx.quick(input.value) }] : []),
    ];
    sel = clamp(sel, 0, Math.max(0, hits.length - 1));
    if (!hits.length) { res.appendChild(el('div', 'dk-none')).textContent = t('desktop.nothingFound'); return; }
    const l = el('div', 'dk-sl');
    hits.forEach((h, i) => {
      const b = btn(i === sel ? 'sel' : '', tile(h.g, h.ic) + '<span></span><i></i>'); b.querySelector('span:not(.dk-ti)').textContent = h.name; b.querySelector('i').textContent = h.sub;
      if (h.soon) b.style.opacity = '.55';
      b.addEventListener('click', () => { closeLauncher(); h.run(b); });
      l.appendChild(b);
    });
    res.appendChild(l);
  };
  input.addEventListener('input', () => { sel = 0; render(); });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); sel = clamp(sel + (e.key === 'ArrowDown' ? 1 : -1), 0, Math.max(0, hits.length - 1)); render(); }
    else if (e.key === 'Enter' && hits[sel]) { e.preventDefault(); const h = hits[sel]; closeLauncher(); h.run($('.dk-dock', root)); }
  });
  L.addEventListener('click', (e) => {
    const f = e.target.closest('[data-f]')?.dataset.f;
    if (f === 'signout') { closeLauncher(); ctx.signOut(); } else if (f === 'devices') { closeLauncher(); ctx.devices(); }
  });
  render(); input.focus();
}

/* =====================================================================
 *  Home-Fenster
 * ===================================================================== */
const NAV = [
  { id: 'start', name: t('desktop.start'), ic: 'home' }, { id: 'apps', name: t('desktop.apps'), ic: 'grid' }, { id: 'dienste', name: t('desktop.services'), ic: 'monitor' }, { id: 'hosts', name: t('desktop.hosts'), ic: 'server' },
  { id: 'dateien', name: t('desktop.files'), ic: 'folder' }, { id: 'system', name: t('desktop.system'), ic: 'activity' }, { id: 'einstellungen', name: t('desktop.settings'), ic: 'sliders' },
];
const nav = { view: 'start', back: [], fwd: [], hostId: null, panel: null };
const greeting = () => { const h = new Date().getHours(); return h < 5 ? t('desktop.greetNight') : h < 11 ? t('desktop.greetMorning') : h < 17 ? t('desktop.greetDay') : h < 22 ? t('desktop.greetEvening') : t('desktop.greetNight'); };

/** Groesse des Start-Fensters aus der Anzeige: gut vier Fuenftel in der Breite und Hoehe, mindestens 960 x 640 und hoechstens 1280 x 860 (eine gemerkte Groesse gilt weiter) */
const homeSize = () => { const v = viewport(); return [Math.max(960, Math.min(1280, Math.round(v.w * 0.82))), Math.max(640, Math.min(860, Math.round(v.h * 0.82)))]; };

function openHome(view) {
  let w = wins.get('home');
  if (!w) {
    const head = el('div', 'dk-wx');
    head.style.flex = '1';
    const back = btn('dk-nv', ic('left'), t('common.back')), fwd = btn('dk-nv', ic('right'), t('desktop.forward'));
    back.dataset.nv = 'back'; fwd.dataset.nv = 'fwd';
    const find = btn('dk-find', ic('search') + `<span>${t('desktop.searchPlaceholder')}</span>`, t('common.search')); find.dataset.launcher = '1';
    find.addEventListener('click', () => openLauncher());
    head.append(back, fwd, find);
    const goBack = () => { if (nav.back.length) { nav.fwd.push(nav.view); nav.view = nav.back.pop(); renderHome(); } };
    const goFwd = () => { if (nav.fwd.length) { nav.back.push(nav.view); nav.view = nav.fwd.pop(); renderHome(); } };
    back.addEventListener('click', goBack); fwd.addEventListener('click', goFwd);
    w = createWindow({ id: 'home', title: t('desktop.start'), icon: `<span class="dk-ti" style="--g1:#7b78ee;--g2:#5160e6;width:22px;height:22px;border-radius:7px">${ic('home')}</span>`,
      rect: defaultRect('home', ...homeSize(), -30, 0), minW: 640, minH: 420, head, onNavigate: (dir) => (dir < 0 ? goBack() : goFwd()), onClose: () => { stopOverview(); ctx.hostPage.unmount(); nav.view = 'start'; nav.back = []; nav.fwd = []; } });
    w.head.querySelector('.dk-wt').style.minWidth = '60px';
    w.body.innerHTML = `<div class="dk-home"><nav class="dk-side" aria-label="${t('desktop.areas')}"></nav><div class="dk-main"></div></div>`;
    w.side = $('.dk-side', w.body); w.main = $('.dk-main', w.body);
    w.side.addEventListener('click', (e) => {
      const b = e.target.closest('[data-nav]'); if (!b) return;
      if (b.getAttribute('aria-disabled') === 'true') return notify(t('desktop.comingLater', { name: b.dataset.name }));
      if (b.dataset.nav === 'dateien') return openFiles();
      goto(b.dataset.nav);
    });
    w.main.addEventListener('click', onMainClick);
    if (view) nav.view = view;
    renderHome();
  } else {
    if (w.minimized) restoreWin(w); else focusWin(w);
    if (view && view !== nav.view) goto(view);
  }
  return w;
}
function goto(view) { if (view === nav.view) return; nav.back.push(nav.view); nav.fwd = []; nav.view = view; renderHome(); }

function renderHome() {
  const w = wins.get('home'); if (!w) return;
  w.side.replaceChildren();
  NAV.forEach((n, i) => {
    if (n.id === 'dateien') w.side.appendChild(document.createElement('hr'));
    const b = btn('dk-nav' + (n.id === (nav.view === 'host' ? 'hosts' : nav.view) ? ' sel' : ''), ic(n.ic) + '<span></span>' + (n.soon ? `<span class="dk-tag">${t('desktop.soon')}</span>` : '')); b.querySelector('span').textContent = n.name;
    b.dataset.nav = n.id; b.dataset.name = n.name; if (n.soon) b.setAttribute('aria-disabled', 'true');
    w.side.appendChild(b);
  });
  const hist = w.head.querySelectorAll('.dk-nv');
  hist[0].disabled = !nav.back.length; hist[1].disabled = !nav.fwd.length;
  const m = w.main; m.scrollTop = 0;
  stopOverview();
  if (nav.view !== 'host') ctx.hostPage.unmount();
  ({ start: renderStart, apps: renderAppsView, dienste: renderServices, hosts: renderHosts, host: renderHostPage, system: renderSystem, einstellungen: renderSettings }[nav.view] || renderStart)(m);
  tickClock();
}

const connTile = (c) => {
  const n = Sessions.find(c.id).length, open = n > 0;
  const a = appOf(c);
  return `<button type="button" class="dk-b dk-tile" data-conn="${ctx.esc(c.id)}">${tile(a.g, a.ic)}<span class="dk-tt"><b><span class="dk-sd ${dotClass(connectionState(ctx.state.hosts, c))}" title="${stateText(connectionState(ctx.state.hosts, c))}"></span>${ctx.esc(c.name)}</b><i class="${open ? 'on' : ''}">${open ? (n > 1 ? t('desktop.sessionsNOpen', { n }) : t('desktop.sessionOpen')) : subName(c) + ' · ' + ctx.esc(c.host)}</i></span></button>`;
};
function renderStart(m) {
  const conns = [...ctx.state.connections.filter((c) => c.favorite), ...ctx.state.connections.filter((c) => !c.favorite)];     // Favoriten (vom Server) zuerst
  const recent = recentList(ctx.state.connections, 4);
  m.innerHTML = `
    <section class="dk-hero"><div class="dk-wp">${scene(false)}</div>
      <div class="dk-hi"><h2>${greeting()}</h2><p>${t('desktop.tagline')}</p></div>
      <div class="dk-hd"><span class="dk-hero-date"></span><b class="dk-hero-time"></b></div></section>
    <h3>${t('desktop.yourServices')} <button type="button" class="dk-b dk-more" data-go="dienste">${t('desktop.allServices')}</button></h3>
    <div class="dk-tiles">${conns.map(connTile).join('')}<button type="button" class="dk-b dk-tile add" data-act="add"><span class="dk-ti" style="--g1:#7b78ee;--g2:#5160e6;box-shadow:none">${ic('plus')}</span><span class="dk-tt"><b>${t('desktop.tileConnection')}</b><i>${t('desktop.tileAdd')}</i></span></button></div>
    ${recent.length ? `<h3>${t('desktop.recent')}</h3><div class="dk-tiles">${recent.map(connTile).join('')}</div>` : ''}
    <h3>${t('desktop.quickActions')}</h3>
    <div class="dk-qa">
      <button type="button" class="dk-b" data-act="add">${ic('plus')}<span>${t('desktop.newConnection')}</span></button>
      <button type="button" class="dk-b" data-act="quick">${ic('monitor')}<span>${t('desktop.quickConnect')}</span></button>
      <button type="button" class="dk-b" data-act="desktop">${ic('monitor')}<span>${t('desktop.connectDesktop')}</span></button>
      <button type="button" class="dk-b" data-act="terminal">${ic('terminal')}<span>${t('desktop.openTerminal')}</span></button>
      <button type="button" class="dk-b" data-act="claude">${ic('spark')}<span>${t('prog.claude')}</span></button>
      <button type="button" class="dk-b" data-act="codex">${ic('code')}<span>${t('prog.codex')}</span></button>
      <button type="button" class="dk-b" data-act="devices">${ic('shield')}<span>${t('desktop.devicesShort')}</span></button>
      <button type="button" class="dk-b" data-act="settings">${ic('sliders')}<span>${t('desktop.settings')}</span></button>
    </div>
    <h3 class="dk-heimh" hidden>${t('heimnetz.title')}</h3><div class="dk-heim"></div>
    <h3>${t('desktop.systemOverview')} <button type="button" class="dk-b dk-more" data-go="system">${t('desktop.openMonitor')}</button></h3>
    ${overviewCards()}`;
  watchOverview(m);
  mountHeim(m);
}

/** Karte "Heimnetz" (heimnetz.js): nur, wenn der Server mindestens ein Modul eingeschaltet hat */
let heimCtl = null;
function mountHeim(m) {
  const box = $('.dk-heim', m); if (!box) return;
  heimCtl?.destroy(); heimCtl = null;
  if (!(modules.caddy || modules.ddns || modules.services)) return;
  $('.dk-heimh', m).hidden = false;
  import('./heimnetz.js').then((h) => {
    if (!box.isConnected) return;
    heimCtl = h.mountHeimnetz(box, { api: ctx.api, mods: modules, esc: ctx.esc, open: (k) => runApp({ services: 'widgets', caddy: 'caddy', ddns: 'ddns' }[k]) });
  });
}

/* ---------- System-Uebersicht: echte Messwerte des Rechners, auf dem der Server laeuft (GET /api/system/overview) ---------- */
const GIB = 1024 ** 3;
const num = (n, d = 1) => fmtNumber(n, d);
const gib = (b) => num(b / GIB, b / GIB >= 100 ? 0 : 1);
const rate = (b) => (b >= 1e6 ? num(b / 1e6) + ' MB/s' : b >= 1e3 ? num(b / 1e3, 0) + ' kB/s' : num(b, 0) + ' B/s');
const NET_TIP = t('desktop.netTip');
const pct = (a, b) => (b > 0 ? Math.round((100 * a) / b) : 0);
function overviewCards() {
  const card = (key, icon, name, tip) => `<div class="dk-sysc" data-ov="${key}"${tip ? ` title="${tip}"` : ''}><b>${ic(icon)}${name}</b><div class="dk-sv">…</div><div class="dk-bar0"><i></i></div></div>`;
  return `<div class="dk-sys">${card('cpu', 'cpu', 'CPU')}${card('ram', 'ram', 'RAM')}${card('disk', 'disk', t('desktop.disk'))}${card('net', 'network', t('desktop.networkJulos'), NET_TIP)}</div>`;
}
/** Setzt die Werte in die Karten (CPU, RAM, Festplatte, Netzwerk) und, falls vorhanden, in die Zeile mit Einzelheiten */
function fillOverview(root0, o) {
  const set = (key, text, p) => {
    const c = $(`[data-ov="${key}"]`, root0); if (!c) return;
    $('.dk-sv', c).textContent = text;
    const bar = $('.dk-bar0 i', c); if (bar) { bar.style.width = (p === null ? 0 : p) + '%'; bar.parentElement.classList.toggle('off', p === null); bar.parentElement.classList.toggle('hot', p !== null && p >= 90); }
    c.classList.toggle('off', text === '–');
  };
  if (!o) { for (const k of ['cpu', 'ram', 'disk', 'net']) set(k, '–', null); return; }
  set('cpu', o.cpuPercent == null ? t('desktop.measuring') : t('desktop.percent', { n: Math.round(o.cpuPercent) }), o.cpuPercent == null ? null : o.cpuPercent);
  set('ram', o.memoryTotal == null ? '–' : t('desktop.usage', { used: gib(o.memoryUsed), total: gib(o.memoryTotal), pct: pct(o.memoryUsed, o.memoryTotal) }), o.memoryTotal == null ? null : pct(o.memoryUsed, o.memoryTotal));
  set('disk', o.diskTotal == null ? '–' : t('desktop.usage', { used: gib(o.diskUsed), total: gib(o.diskTotal), pct: pct(o.diskUsed, o.diskTotal) }), o.diskTotal == null ? null : pct(o.diskUsed, o.diskTotal));
  set('net', o.rxBytesPerSecond == null ? t('desktop.measuring') : `↓ ${rate(o.rxBytesPerSecond)}  ↑ ${rate(o.txBytesPerSecond)}`, null);
  const d = $('[data-ov-details]', root0);
  if (d) d.textContent = o.sampledAt ? (o.cores ? t('desktop.lastMeasuredCores', { time: fmtTime(o.sampledAt, {}), cores: o.cores }) : t('desktop.lastMeasured', { time: fmtTime(o.sampledAt, {}) })) : '';
}
let ovTimer = null;
function stopOverview() { clearInterval(ovTimer); ovTimer = null; }
/** Holt alle 3 s neue Werte, solange die Ansicht offen ist (nicht bei minimiertem Fenster oder versteckter Seite) */
function watchOverview(m) {
  const tick = async () => {
    const w = wins.get('home');
    if (!w || w.minimized || document.hidden || !m.isConnected) return;
    try { fillOverview(m, await ctx.api('/api/system/overview')); }
    catch (e) { if (m.isConnected) fillOverview(m, null); if (e && e.status === 401) stopOverview(); }
  };
  tick(); ovTimer = setInterval(tick, 3000);
}
function renderSystem(m) {
  m.innerHTML = `<h3 style="margin-top:14px">${t('desktop.system')}</h3>
    ${overviewCards()}
    <p class="dk-lead" data-ov-details></p>
    <div class="dk-card" style="margin-top:14px"><h4>${t('desktop.measuredTitle')}</h4>
      <p><b>${t('desktop.measCpuRamLabel')}</b> ${t('desktop.measCpuRam')}</p>
      <p><b>${t('desktop.measDiskLabel')}</b> ${t('desktop.measDisk')}</p>
      <p style="margin-bottom:2px"><b>${t('desktop.measNetLabel')}</b> ${t('desktop.measNet', { code: '<code>network_mode: host</code>' })}</p></div>`;
  watchOverview(m);
}
const hostFilter = { id: '' };      // leer = alle Hosts (Auswahl in der Dienste-Ansicht, nur solange das Fenster lebt)
const serviceRow = (c) => {
  const a = appOf(c), n = Sessions.find(c.id).length, open = n > 0, st = connectionState(ctx.state.hosts, c);
  return `<div class="dk-row">${tile(a.g, a.ic)}<span class="dk-tt"><b><span class="dk-sd ${dotClass(st)}" title="${stateText(st)}"></span>${ctx.esc(c.name)}${open ? ` <span class="dk-tag">${n > 1 ? t('desktop.sessionsN', { n }) : t('desktop.sessionOpen')}</span>` : ''}</b><i>${isProg(c) ? appOf(c).name + ' (SSH)' : c.kind === 'terminal' ? t('desktop.terminalSsh') : c.kind === 'vnc' ? t('desktop.desktopVnc') : t('desktop.desktopRdp')} · ${ctx.esc(loginUser(c) ? loginUser(c) + '@' : '')}${ctx.esc(c.host)}:${c.port}${folderName(ctx.state.folders || [], c) ? ' · ' + ctx.esc(folderName(ctx.state.folders, c)) : ''}${loginSaved(c) ? ' · ' + t('desktop.passwordSaved') : ''}</i></span>
    <button type="button" class="dk-b dk-btn sq dk-star${c.favorite ? ' on' : ''}" data-fav="${ctx.esc(c.id)}" aria-pressed="${!!c.favorite}" aria-label="${c.favorite ? t('desktop.unfavorite') : t('desktop.pinFavorite')}" title="${c.favorite ? t('desktop.favoriteEverywhere') : t('desktop.pinFavorite')}">${ic('star')}</button>
    <button type="button" class="dk-b dk-btn pri" data-conn="${ctx.esc(c.id)}">${ic('play')}${open ? t('desktop.open') : t('desktop.connect')}</button>
    ${open ? `<button type="button" class="dk-b dk-btn sq" data-extra="${ctx.esc(c.id)}" aria-label="${t('desktop.anotherSession')}" title="${t('desktop.anotherSessionTarget')}">${ic('plus')}</button>` : ''}
    <button type="button" class="dk-b dk-btn sq" data-edit="${ctx.esc(c.id)}" aria-label="${t('common.edit')}" title="${t('common.edit')}">${ic('edit')}</button></div>`;
};
const folderFilter = { id: 'all', by: '' };   // all | fav | recent | none | Ordner-Kennung; by: 'host' | 'folder' (leer = Ordner, wenn es welche gibt)
function renderServices(m) {
  const hosts = ctx.state.hosts, conns = ctx.state.connections, folders = ctx.state.folders || [];
  if (hostFilter.id && !hosts.some((h) => h.id === hostFilter.id)) hostFilter.id = '';
  if (!['all', 'fav', 'recent', 'none'].includes(folderFilter.id) && !folders.some((f) => f.id === folderFilter.id)) folderFilter.id = 'all';
  const shown = filterByFolder(conns, folderFilter.id, folders).filter((c) => !hostFilter.id || c.hostId === hostFilter.id);
  const byFolder = folders.length > 0 && folderFilter.id === 'all' && folderFilter.by === 'folder';       // Standard: ein Abschnitt je Host; nach Ordnern nur auf Wunsch
  // Die Auswahl des Hosts gibt es erst, wenn es mehr als einen gibt; die Gruppenkoepfe zeigen den Zustand des Hosts
  const chips = hosts.length > 1 ? `<div class="dk-chips" role="group" aria-label="${t('desktop.chooseHost')}">${[{ id: '', name: t('desktop.allHosts') }, ...hosts].map((h) => `<button type="button" class="dk-b dk-chip${h.id === hostFilter.id ? ' sel' : ''}" data-hostfilter="${ctx.esc(h.id)}" aria-pressed="${h.id === hostFilter.id}">${h.id ? `<span class="dk-sd ${dotClass(h.online)}"></span>` : ''}${ctx.esc(h.name)}</button>`).join('')}</div>` : '';
  // Ordner, Favoriten, zuletzt benutzt: die Auswahl gibt es, sobald es einen Ordner, einen Favoriten oder eine Benutzung gibt
  const fchip = (id, label, svg) => `<button type="button" class="dk-b dk-chip${folderFilter.id === id ? ' sel' : ''}" data-folderfilter="${ctx.esc(id)}" aria-pressed="${folderFilter.id === id}">${svg ? `<span class="dk-fi2">${svg}</span>` : ''}${ctx.esc(label)}</button>`;
  const fchips = folders.length || conns.some((c) => c.favorite || c.lastUsedAt)
    ? `<div class="dk-chips" role="group" aria-label="${t('desktop.folder')}">${fchip('all', t('desktop.all'), '')}${fchip('fav', t('desktop.favorites'), folderIconSvg('star'))}${fchip('recent', t('desktop.recent'), '')}${folders.map((f) => fchip(f.id, f.name, folderIconSvg(f.icon))).join('')}${folders.length ? fchip('none', t('desktop.noFolder'), '') : ''}`
      + `${folders.length && folderFilter.id === 'all' ? `<button type="button" class="dk-b dk-chip" data-groupby="${byFolder ? 'host' : 'folder'}" title="${t('desktop.switchGrouping')}">${byFolder ? t('desktop.groupedByFolder') : t('desktop.groupedByHost')}</button>` : ''}</div>` : '';
  const folderHead = (g) => `<h4 class="dk-gh">${g.folder ? `<span class="dk-fi2">${folderIconSvg(g.folder.icon)}</span>` : ''}${ctx.esc(g.folder ? g.folder.name : t('desktop.noFolder'))}</h4>`;
  // Ein Abschnitt je Host: Kopf mit Zustand (online, Agent), darunter die Verbindungen und, getrennt, die Programme (Claude Code, Codex); ein Klick auf den Kopf klappt zu (pro Geraet gemerkt)
  const closed = readClosed(localStorage);
  const hostBlock = (g) => {
    const open = !closed.includes(g.key), n = g.plain.length + g.programs.length;
    const head = `<h4 class="dk-gh"><button type="button" class="dk-b dk-gh-t" data-hostclose="${ctx.esc(g.key)}" aria-expanded="${open}" aria-label="${ctx.esc((g.host ? g.host.name : t('desktop.noHost')) + ' – ' + t(open ? 'desktop.collapse' : 'desktop.expand'))}">${ic(open ? 'chev' : 'right')}${g.host ? `<span class="dk-sd ${dotClass(g.host.online)}"></span>` : ''}<b>${ctx.esc(g.host ? g.host.name : t('desktop.noHost'))}</b><span class="dk-sub">${g.host ? ctx.esc(hostStatusText(g.host)) : ''}</span><span class="dk-count">${n}</span></button></h4>`;
    if (!open) return `<section class="dk-hsec closed">${head}</section>`;
    const list = (arr) => `<div class="dk-list">${arr.map(serviceRow).join('')}</div>`;
    const body = !n ? `<p class="dk-lead">${t('desktop.noConnectionsOnHost')}</p>`
      : `${g.plain.length ? (g.programs.length ? `<h5 class="dk-ph">${t('desktop.services')}</h5>` : '') + list(g.plain) : ''}${g.programs.length ? `<h5 class="dk-ph">${t('desktop.programs')}</h5>` + list(g.programs) : ''}`;
    return `<section class="dk-hsec">${head}${body}</section>`;
  };
  const filtered = folderFilter.id !== 'all' || !!hostFilter.id;
  const sections = hostSections(hosts, shown, { hideEmpty: filtered }).filter((g) => !hostFilter.id || (g.host && g.host.id === hostFilter.id));
  m.innerHTML = `<h3 style="margin-top:14px">${t('desktop.services')} <button type="button" class="dk-b dk-btn" style="margin-left:auto" data-act="folders">${ic('folder')}${t('desktop.folder')}</button><button type="button" class="dk-b dk-btn pri" data-act="add">${ic('plus')}${t('desktop.addConnection')}</button></h3>
    ${fchips}${chips}
    ${conns.length ? '' : `<p class="dk-lead">${t('desktop.noConnections')}</p>`}
    ${byFolder
    ? groupByFolder(folders, shown).map((g) => `${folderHead(g)}<div class="dk-list">${g.connections.map(serviceRow).join('') || `<p class="dk-lead">${t('desktop.noConnectionsOnHost')}</p>`}</div>`).join('')
    : sections.map(hostBlock).join('')}`;
}

/** Hosts: Rechner und Server als Karten (Zustand, Art des Agenten, Kennzahlen, Hinweise, Verbindungen); hier wird auch der Agent je Host eingerichtet. Karte: hostview.js */
function renderHosts(m) {
  const hosts = ctx.state.hosts;
  ctx.info.ensure(hosts);                // nur diese Ansicht fragt nach Systemwerten und Updates (hoechstens alle 55 s), sonst niemand
  const connection = (id) => ctx.state.connections.find((x) => x.id === id);
  m.innerHTML = `<h3 style="margin-top:14px">${t('desktop.hosts')} <button type="button" class="dk-b dk-btn pri" style="margin-left:auto" data-act="addhost">${ic('plus')}${t('desktop.addHost')}</button></h3>
    ${hosts.length ? '' : `<p class="dk-lead">${t('desktop.noHosts')}</p>`}
    <div class="hu-grid">${hosts.map((h) => hostCardHtml(h, ctx.info.get(h.id), { esc: ctx.esc, connection })).join('')}</div>`;
}
/** Die Seite eines Hosts (hostpage.js) in der Ansicht des Start-Fensters; der Pfeil bringt zur Liste zurueck */
function renderHostPage(m) {
  const h = ctx.state.hosts.find((x) => x.id === nav.hostId);
  if (!h) { nav.view = 'hosts'; renderHost_list(m); return; }
  ctx.hostPage.mount(m, h, nav.panel, { onBack: () => { const w = wins.get('home'); const b = w && w.head.querySelector('[data-nv=back]'); if (b && !b.disabled) b.click(); else goto('hosts'); } });
}
const renderHost_list = (m) => renderHosts(m);
/** Oeffnet die Seite eines Hosts (optional gleich in einem Abschnitt) im Start-Fenster */
export function openHost(host, panel) {
  nav.hostId = host.id; nav.panel = panel || null;
  const same = nav.view === 'host' && wins.get('home');
  openHome('host');
  if (same) renderHome();
}
function renderAppsView(m) {
  m.innerHTML = `<h3 style="margin-top:14px">${t('desktop.apps')}</h3><div class="dk-grid">${shownApps().map((a) => `<button type="button" class="dk-b dk-app" data-app="${a.id}"${a.soon ? ` aria-disabled="true" title="${t('desktop.soonTitle')}"` : ''}>${tile(a.g, a.ic)}<span class="dk-n">${a.name}</span>${a.soon ? `<span class="dk-tag">${t('desktop.soon')}</span>` : ''}</button>`).join('')}</div>
    <p class="dk-lead">${t('desktop.appsGrayed')}</p>`;
}
/* Einstellungen: dieselben Gruppen wie am Handy (settings.js); die Angaben des Servers (Version, Backup) kommen von /api/about */
let aboutCache = null, aboutAt = 0;
const dkChoice = (attr, opts, cur, label) => `<span class="dk-btns" role="group" aria-label="${ctx.esc(label)}">${opts.map(([id, l]) => `<button type="button" class="dk-b dk-btn${id === cur ? ' pri' : ''}" data-${attr}="${id}" aria-pressed="${id === cur}">${l}</button>`).join('')}</span>`;
function dkSettingRow(r, ab) {
  const label = t(r.label); let help = t(r.help), ctl = '';
  switch (r.id) {
    case 'user': ctl = `<b>${ctx.esc(ctx.state.user || '')}</b>`; break;
    case 'scheme': ctl = dkChoice('scheme', SCHEMES, getScheme(), label); break;
    case 'lang': ctl = dkChoice('lang', LANG_CHOICES.map(([id, l]) => [id, id === 'system' ? t('lang.system') : l]), getPref(), label); break;
    case 'dock': ctl = dkChoice('dockmode', DOCK_MODES, dockMode, label); break;
    case 'backup': case 'backupCopy': {
      const info = r.id === 'backup' ? ab.backup : ab.copy; if (!info) return '';
      if (!info.next && !info.details) help = info.line;
      ctl = `<span class="dk-tag dk-t-${info.tone}">${ctx.esc(info.chip)}</span>`; break;
    }
    case 'version': case 'build': case 'transfer': { const v = ab[r.id]; if (!v && r.optional) return ''; ctl = `<span class="dk-val">${ctx.esc(v || t('common.loading'))}</span>`; break; }
    case 'licenses': ctl = `<a class="dk-btn" href="/licenses.txt" target="_blank" rel="noopener" aria-label="${ctx.esc(label + ': ' + t(r.action))}">${t(r.action)}</a>`; break;
    case 'source': if (!ab.repository) return ''; ctl = `<a class="dk-btn" href="${ctx.esc(ab.repository)}" target="_blank" rel="noopener" aria-label="${ctx.esc(label + ': ' + t(r.action))}">${t(r.action)}</a>`; break;
    default: if (r.ctl === 'button') ctl = `<button type="button" class="dk-b dk-btn${r.danger ? ' dk-danger' : ''}" data-act="${r.id === 'password' ? 'account' : r.id}" aria-label="${ctx.esc(label + ': ' + t(r.action))}">${t(r.action)}</button>`;
  }
  return `<div class="dk-srow"><div class="dk-st"><b>${ctx.esc(label)}</b><i>${ctx.esc(help)}</i></div>${ctl ? `<div class="dk-sc">${ctl}</div>` : ''}</div>`;
}
const dkBackupNotes = (ab) => [ab.backup, ab.copy].filter((i) => i && (i.next || i.details)).map((i) => `<div class="dk-msg dk-t-${i.tone}" role="${i.tone === 'error' ? 'alert' : 'status'}"><b>${ctx.esc(i.line)}</b>${i.next ? `<span>${ctx.esc(i.next)}</span>` : ''}${i.details ? `<details><summary>${t('set.details')}</summary><pre>${ctx.esc(i.details)}</pre></details>` : ''}</div>`).join('');
function renderSettings(m) {
  const ab = aboutValues(aboutCache);
  m.innerHTML = `<h3 style="margin-top:14px">${t('desktop.settings')}</h3>${GROUPS.map((g) => {
    const rows = rowsFor(g, 'desktop').map((r) => dkSettingRow(r, ab)).filter(Boolean);
    if (!rows.length) return '';
    return `<section class="dk-sg${g.danger ? ' dk-danger-zone' : ''}" aria-labelledby="dk-set-${g.id}"><h4 id="dk-set-${g.id}">${ctx.esc(t(g.title))}</h4><p class="dk-sh">${ctx.esc(t(g.help))}</p><div class="dk-card dk-rows">${rows.join('')}</div>${g.id === 'backup' ? dkBackupNotes(ab) : ''}</section>`;
  }).join('')}`;
  if (!aboutCache || Date.now() - aboutAt > 60000) {
    aboutAt = Date.now();
    ctx.api('/api/about').then((a) => {
      aboutCache = a;
      const w = wins.get('home'); if (!w || nav.view !== 'einstellungen') return;
      const top = w.main.scrollTop; renderHome(); w.main.scrollTop = top;
    }, () => { /* ohne Angaben bleiben die Werte leer */ });
  }
}
function onMainClick(e) {
  const t = e.target.closest('[data-conn],[data-edit],[data-extra],[data-act],[data-go],[data-app],[data-dockmode],[data-scheme],[data-lang],[data-hostfilter],[data-folderfilter],[data-groupby],[data-hostclose],[data-fav],[data-hostedit],[data-hostagent],[data-hostinfo],[data-hcact]'); if (!t) return;
  const find = (id) => ctx.state.connections.find((c) => c.id === id);
  const host = (id) => ctx.state.hosts.find((h) => h.id === id);
  if (t.dataset.lang) { if (t.dataset.lang !== getPref()) setLanguage(t.dataset.lang); }
  else if (t.dataset.scheme) setTheme({ scheme: t.dataset.scheme });   // das Ereignis julos:scheme (app.js) zeichnet die Einstellungen neu
  else if (t.dataset.dockmode) setDockMode(t.dataset.dockmode);
  else if (t.dataset.hostfilter !== undefined) { hostFilter.id = t.dataset.hostfilter; renderHome(); }
  else if (t.dataset.folderfilter !== undefined) { folderFilter.id = t.dataset.folderfilter; renderHome(); }
  else if (t.dataset.groupby) { folderFilter.by = t.dataset.groupby; renderHome(); }
  else if (t.dataset.hostclose !== undefined) { const mm = wins.get('home')?.main, y = mm ? mm.scrollTop : 0; writeClosed(localStorage, toggleClosed(readClosed(localStorage), t.dataset.hostclose)); renderHome(); if (mm) mm.scrollTop = y; }
  else if (t.dataset.fav) { const c = find(t.dataset.fav); c && setFavorite(ctx.api, c, !c.favorite).then(() => renderHome(), (err) => notify(serverMsg(err.message))); }
  else if (t.dataset.act === 'folders') ctx.folders();
  else if (t.dataset.hostedit || t.dataset.hostagent) { const h = host(t.dataset.hostedit || t.dataset.hostagent); h && ctx.editHost(h); }
  else if (t.dataset.hcact) { const h = host(t.dataset.host); h && ctx.hostAction(h, t.dataset.hcact, t.dataset.htarget || null, { onDone: () => ctx.info.load(h, { fresh: true }) }); }
  else if (t.dataset.hostinfo) { const h = host(t.dataset.hostinfo); h && ctx.hostInfo(h, t.dataset.hpage || null); }
  else if (t.dataset.act === 'addhost') ctx.addHost();
  else if (t.dataset.conn) { const c = find(t.dataset.conn); c && ctx.connect(c); }
  else if (t.dataset.extra) { const c = find(t.dataset.extra); c && ctx.connect(c, { extra: true }); }
  else if (t.dataset.edit) { const c = find(t.dataset.edit); c && ctx.edit(c); }
  else if (t.dataset.go) goto(t.dataset.go);
  else if (t.dataset.app) runApp(t.dataset.app, t);
  else if (t.dataset.act === 'add') ctx.add();
  else if (t.dataset.act === 'quick') ctx.quick();
  else if (t.dataset.act === 'devices') ctx.devices();
  else if (t.dataset.act === 'nettools') ctx.nettools();
  else if (t.dataset.act === 'alerts') ctx.alerts();
  else if (t.dataset.act === 'account') ctx.account();
  else if (t.dataset.act === 'security') ctx.security();
  else if (t.dataset.act === 'signout') ctx.signOut();
  else if (t.dataset.act === 'settings') goto('einstellungen');
  else if (t.dataset.act === 'desktop' || t.dataset.act === 'terminal' || t.dataset.act === 'vnc') pickConnection(t.dataset.act, t);
  else if (t.dataset.act === 'claude' || t.dataset.act === 'codex') runApp(t.dataset.act, t);
}


/* =====================================================================
 *  Dateien-Fenster (gemeinsamer Ordner und Hosts per SFTP; der Inhalt steckt in desktop-files.js und files-app.js)
 * ===================================================================== */
async function openFiles(path, focusSearch) {
  const old = wins.get('files');
  if (old) { old.minimized ? restoreWin(old) : focusWin(old); if (path !== undefined && old.files) old.files.go(path); if (focusSearch) $('.f-search input', old.el)?.focus(); return old; }
  const a = appById('files');
  const w = createWindow({ id: 'files', title: t('desktop.files'), icon: `<span class="dk-ti" style="--g1:${a.g[0]};--g2:${a.g[1]};width:22px;height:22px;border-radius:7px">${ic(a.ic)}</span>`,
    rect: (() => { const v = viewport(), sz = filesWindowSize({ w: v.w - 40, h: v.h - BAR_H - dockReserve() }); return defaultRect('files', sz.w, sz.h, 20, 12); })(), minW: 560, minH: 340, cls: 'dk-files' });   // 70 % der Flaeche, mindestens 860 x 540; eine gemerkte Groesse (rect.files) gilt weiter
  w.body.innerHTML = `<div class="dk-fempty">${t('common.loading')}</div>`;
  try {
    const m = await import('./desktop-files.js');
    if (!wins.has('files')) return w;   // schon wieder geschlossen
    w.files = m.mount(w.body, { api: ctx.api, getState: () => ({ connections: ctx.state.connections, hosts: ctx.state.hosts }), notify });
    w.onNavigate = (dir) => w.files.navigate(dir);        // Maustasten 4 und 5: der Verlauf der Ordner (wie Alt+Links und Alt+Rechts)
    if (focusSearch) $('.f-search input', w.el)?.focus();
  } catch { w.body.innerHTML = `<div class="dk-fempty">${t('desktop.filesLoadFailed')}</div>`; }
  return w;
}

/* =====================================================================
 *  Module Caddy und DDNS (nur lesen; der Inhalt steckt in desktop-modules.js)
 * ===================================================================== */
async function openModule(id) {
  const old = wins.get(id);
  if (old) { old.minimized ? restoreWin(old) : focusWin(old); old.mod?.reload(); return old; }
  const a = appById(id);
  const w = createWindow({ id, title: a.name, icon: `<span class="dk-ti" style="--g1:${a.g[0]};--g2:${a.g[1]};width:22px;height:22px;border-radius:7px">${ic(a.ic)}</span>`,
    rect: defaultRect(id, 680, 480, 30, 20), minW: 460, minH: 300, cls: 'dk-modwin' });
  w.body.innerHTML = `<div class="dk-fempty">${t('common.loading')}</div>`;
  try {
    const m = await import('./desktop-modules.js');
    if (!wins.has(id)) return w;   // schon wieder geschlossen
    w.mod = m.mountModule(id, w.body, { el, btn, ic, api: ctx.api });
  } catch { w.body.innerHTML = `<div class="dk-fempty">${t('desktop.moduleLoadFailed', { name: a.name })}</div>`; }
  return w;
}

/* ---------- Ueber JulOS ---------- */
const backupText = (b) => !b ? '' : b.state === 'ok' ? `${fmtDate(b.lastAt)}, ${b.bytes < 1048576 ? Math.max(1, Math.round(b.bytes / 1024)) + ' KB' : (b.bytes / 1048576).toFixed(1) + ' MB'}`
  : b.state === 'off' ? t('desktop.backupOff') : b.state === 'pending' ? t('desktop.backupPending', { time: b.time }) : b.state === 'failed' ? t('desktop.backupFailed', { message: b.message || '' }) : t('desktop.backupNotSetUp', { message: b.message || '' });
const copyText = (b) => { const c = b && b.copy; if (!c || c.state === 'off') return '';
  const at = c.lastAt ? fmtDate(c.lastAt) : '';
  return c.state === 'ok' ? t('desktop.copyOk', { at }) : c.state === 'failed' ? t('desktop.copyFailed', { message: c.message || '' }) + (at ? t('desktop.copyLastOk', { at }) : '') : t('desktop.copyNone'); };
async function openAbout() {
  const old = wins.get('about'); if (old) return old.minimized ? restoreWin(old) : focusWin(old);
  const w = createWindow({ id: 'about', title: t('desktop.aboutJulos'), icon: LOGO, rect: defaultRect('about', 420, 460), minW: 340, minH: 340 });
  let a = null;
  try { const r = await fetch('/api/about'); if (r.ok) a = await r.json(); } catch { /* ohne Angaben */ }
  const e = ctx.esc;
  const row = (k, v) => v ? `<div class="dk-ab-row"><span>${k}</span><b>${e(v)}</b></div>` : '';
  const built = a && a.built ? fmtDate(a.built) : '';
  const stream = a ? (a.engines.stream ? `Stream (${a.engines.encoder || 'H.264'})` : t('desktop.notAvailable')) : '';
  w.body.innerHTML = `<div class="dk-about"><div class="dk-ab-logo">${LOGO}</div><h2>JulOS</h2>
    <p class="dk-lead">${a ? t('desktop.version', { v: e(a.version) }) : t('desktop.versionUnknown')}</p>
    <p class="dk-lead">${t('desktop.aboutText')}</p>
    <div class="dk-ab-rows">${row('Build', a && [a.commit, built].filter(Boolean).join(' · '))}${row(t('desktop.aboutStream'), stream)}${row(t('desktop.aboutBackup'), a && backupText(a.backup))}${row(t('desktop.aboutBackupCopy'), a && copyText(a.backup))}${row(t('desktop.aboutRuntime'), a && a.runtime)}${row(t('desktop.system'), a && a.os)}${row(t('desktop.aboutLicense'), a && a.license + ' ' + t('desktop.seeLicenses'))}</div>
    <p class="dk-ab-links"><a href="/licenses.txt" target="_blank" rel="noopener">${t('desktop.licenses')}</a><a href="${a ? e(a.repository) : 'https://github.com/Juloc/JulOS'}" target="_blank" rel="noopener">${t('desktop.sourceAndDocs')}</a></p></div>`;
}

/* =====================================================================
 *  Sitzungsfenster: jede Sitzung (app.js, sessions.js) haengt in einem eigenen Fenster
 * ===================================================================== */
const SESSION_RECT = { w: 1000, h: 680 };
const sessionIcon = (c) => { const a = appOf(c); return `<span class="dk-ti" style="--g1:${a.g[0]};--g2:${a.g[1]};width:22px;height:22px;border-radius:7px">${ic(a.ic)}</span>`; };
const stateClass = (st) => (st === 'connected' ? 'ok' : st === 'connecting' || st === 'reconnecting' ? 'busy' : 'bad');
/** Wird bei jeder neuen Sitzung gerufen (vom Verwalter, vor dem Verbinden): Fenster anlegen, Sitzung einhaengen. n = wie viele Sitzungen es jetzt gibt. */
export function sessionOpened(session, n) {
  const c = session.conn;
  const w = createWindow({ id: 's:' + session.id, rectKey: 'session.' + c.id, title: session.title, cls: 'dk-session', icon: sessionIcon(c),
    rect: defaultRect('session', SESSION_RECT.w, SESSION_RECT.h, 20, 0), cascade: Math.max(0, sessWins.size) * 36, minW: 520, minH: 360,
    onFocus: () => Sessions.activate(session.id),
    onVisible: (on) => session.setVisible(on),
    onClose: () => { Sessions.close(session.id); } });
  w.session = session; sessWins.set(session.id, w);
  // Eigenes Browserfenster (Pop-out): nur Fernsteuer-Sitzungen, und dieses Layout gibt es nur auf grossen Bildschirmen mit Maus
  if (typeof session.popOut === 'function') {
    const pop = btn('dk-po', ic('popout'), t('desktop.openOwnWindow')); pop.title = t('desktop.openOwnWindow');
    pop.addEventListener('click', (ev) => { ev.stopPropagation(); if (session.popped) session.popBack(); else session.popOut(); });
    w.head.querySelector('.dk-wc').prepend(pop);
  }
  const dot = el('span', 'dk-sdot busy'); dot.title = session.stateText; w.head.querySelector('.dk-wt').prepend(dot);
  w.dot = dot;
  session.addEventListener('titlechange', (e) => { w.head.querySelector('.dk-wt span:last-child').textContent = e.detail.title; w.el.setAttribute('aria-label', e.detail.title); renderBar(); });
  session.addEventListener('taskbarchange', () => renderBar());
  session.addEventListener('statechange', (e) => { dot.className = 'dk-sdot ' + stateClass(e.detail.state); dot.title = e.detail.text; });
  session.mount(w.body);
  renderBar(); renderDock(); renderHome_ifOpen();
}
/** Die aktive Sitzung hat sich geaendert (Klick auf ein Fenster, neue Sitzung, anderes Fenster nach dem Schliessen). reveal = soll gezeigt werden. */
export function sessionActivated(session, reveal) {
  const w = sessWins.get(session.id); if (!w) return;
  if (reveal && w.minimized) restoreWin(w);
  else if (!w.minimized && !w.el.classList.contains('active')) focusWin(w);
  else if (reveal) focusWin(w);
  for (const o of sessWins.values()) o.dot && (o.el.classList.toggle('dk-sfocus', o === w));
  session.focus();
  renderBar();
}
/** Eine Sitzung ist zu Ende (Fenster geschlossen, Fehler, "Sitzung beenden"): ihr Fenster schliessen und die Meldung zeigen */
export function sessionClosed(session, message) {
  const w = sessWins.get(session.id);
  sessWins.delete(session.id);
  if (w) { w.opts.onClose = null; closeWin(w); }
  renderBar(); renderDock(); renderHome_ifOpen();
  const a = Sessions.active; const aw = a && sessWins.get(a.id);
  if (aw && !aw.minimized) { focusWin(aw); a.focus(); }
  notify(message);
}
export function sessionsChanged() { renderBar(); renderHome_ifOpen(); }
const renderHome_ifOpen = () => { if (wins.has('home')) renderHome(); };

/* Anordnen: nebeneinander (Kacheln) oder ueberlappend (versetzt). Betrifft nur die Sitzungsfenster, die nicht minimiert sind. */
function placeWindows(list, rects) {
  list.forEach((w, i) => {
    wm.forget(w);
    Object.assign(w.el.style, { left: rects[i].x + 'px', top: rects[i].y + 'px', width: rects[i].w + 'px', height: rects[i].h + 'px' });
    saveRect(w);
  });
}
function tileSessions() {
  const list = [...sessWins.values()].filter((w) => !w.minimized);
  if (!list.length) return;
  const v = viewport(), gap = 8, top = BAR_H + gap, bottom = v.h - 110, width = v.w - gap * 2;
  const cols = list.length <= 3 ? list.length : Math.ceil(Math.sqrt(list.length)), rowsN = Math.ceil(list.length / cols);
  const cw = Math.floor((width - gap * (cols - 1)) / cols), ch = Math.floor((bottom - top - gap * (rowsN - 1)) / rowsN);
  placeWindows(list, list.map((_, i) => ({ x: gap + (i % cols) * (cw + gap), y: top + Math.floor(i / cols) * (ch + gap), w: cw, h: ch })));
}
function cascadeSessions() {
  const list = [...sessWins.values()].filter((w) => !w.minimized);
  const r0 = defaultRect('session', SESSION_RECT.w, SESSION_RECT.h, -80, -20);
  placeWindows(list, list.map((_, i) => fitRect({ x: r0.x + i * 36, y: r0.y + i * 36, w: r0.w, h: r0.h }, 520, 360)));
  list.forEach((w) => focusWin(w));
}
/** Sitzungsfenster anordnen ('tile' nebeneinander, 'cascade' ueberlappend); auch fuer Tests. */
export function arrange(mode) { if (mode === 'tile') tileSessions(); else cascadeSessions(); }
export function isRunning(c) { return Sessions.find(c.id).length > 0; }

/* =====================================================================
 *  Schnittstelle zu shell.js
 * ===================================================================== */
export async function init(context) {
  ctx = context;
  await Promise.all(['desktop.css', 'dialogwin.css'].map((href) => new Promise((done) => {
    const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = href; l.onload = l.onerror = done; document.head.appendChild(l);
  })));
  document.documentElement.classList.add('desktop');
  setDialogHost(dialogFrame);
  // Die Wahl Hell / Dunkel / System (auch vom Handy oder vom Server) steht in den Einstellungen: nur die Knoepfe nachziehen, der Fokus bleibt
  document.addEventListener('julos:scheme', () => {
    const cur = getScheme();
    for (const b of document.querySelectorAll('#desktop [data-scheme]')) { const on = b.dataset.scheme === cur; b.classList.toggle('pri', on); b.setAttribute('aria-pressed', String(on)); }
  });
  root = $('#desktop');
  root.replaceChildren();
  wm = createWM({ root, wins, barH: BAR_H, view: viewport, area: () => { const v = viewport(), ti = taskbar.inset(); return { x: ti.left, y: BAR_H + ti.top, w: v.w - ti.left - ti.right, h: v.h - BAR_H - ti.top - dockReserve() }; }, fit: fitRect,
    focusWin, minimizeWin, restoreWin, closeWin: (w) => closeWin(w), rootOf, setMax, saveRect, keysToRemote: () => keysToRemote(), notify: (m, ms) => notify(m, ms), ls });
  pills = createPills({ label: t('desktop.pillsLabel'), onClick: pillClick, onMenu: pillMenu });
  taskbar = createTaskbar({ barH: BAR_H, focusRemote: () => remoteApi.focus(), notify: (m, ms) => notify(m, ms), onInset: () => applyDock() });
  pills.el.addEventListener('focusin', showDock);          // mit der Tastatur auf eine Pille: der Dock klappt auf
  const wp = el('div', 'dk-wp'); wp.innerHTML = scene(true);
  root.insertAdjacentHTML('afterbegin', DEFS);
  dockMode = readDockMode(); dockCollapsed = ls.get('dockCollapsed', false); dockShown = dockMode !== 'auto';
  root.append(wp, buildBar(), buildDock(), pills.el, taskbar.el, buildKnob());
  applyDock(); renderBar(); wireDockHover();
  remoteApi.setKeyOwner((e) => keysToRemote() && !(e && wm.claims(e)), () => setTimeout(markKeys, 0));   // die Tasten zum Wechseln des Fensters gehen nie ins Fernfenster (kein Tastaturfang)
  document.addEventListener('focusin', () => setTimeout(markKeys, 0));
  document.addEventListener('focusout', () => setTimeout(markKeys, 0));
  document.addEventListener('close', () => syncKeys(), true);      // ein Dialog (zum Beispiel Text senden, Kombination bearbeiten) wurde geschlossen
  // Ein Klick auf einen Knopf, die Leiste, den Dock oder eine Titelleiste darf den Fokus nicht vom Tastaturfeld wegnehmen
  root.addEventListener('mousedown', (e) => { if (e.target.closest && e.target.closest('button, .dk-bar, .dk-dockrow, .dk-wh, .dk-menu') && !e.target.closest('input, textarea, select, [contenteditable="true"]')) e.preventDefault(); });
  setInterval(tickClock, 15000); tickClock();
  // Beim Wegklicken auf den Hintergrund: Menues zu (Fenster nehmen den Fokus selbst)
  wp.addEventListener('pointerdown', () => { for (const w of wins.values()) w.el.classList.remove('active'); renderBar(); });
}
/** Nach Anmeldung (oder wenn die Seite die Startansicht zeigt): Konto setzen, Home-Fenster oeffnen */
export function userChanged(user, message) {
  import('./modules.js').then((m) => m.loadModules(ctx.api)).then((m) => { modules = m; applyModules(); const hw = wins.get('home'); if (hw && nav.view === 'start') mountHeim(hw.main); });
  const av = $('#dkUser .dk-avatar', root); if (av) av.textContent = (user || '?').slice(0, 1);
  if (!wins.has('home') && !wins.size) openHome();
  if (dockMode === 'auto' && !dockHinted) { dockHinted = true; showDock(); hideTimer = setTimeout(hideDock, 3500); }
  notify(message);
}
export function connectionsChanged() { if (root) { renderHome_ifOpen(); wins.get('files')?.files?.refreshPlaces(); } }
/** Neue Zustaende der Hosts (kommen alle 30 s): die Ansichten, die sie zeigen, neu zeichnen, ohne dass das Fenster nach oben springt */
export function hostsChanged() {
  if (!root || menuOpen()) return;       // ein offenes Menue einer Host-Karte wuerde beim Neuzeichnen zuklappen
  const w = wins.get('home');
  if (w && ['start', 'dienste', 'hosts'].includes(nav.view)) { const top = w.main.scrollTop; renderHome(); w.main.scrollTop = top; }
  if (menuEl && menuOwner?.dataset.menu === 'Hosts') { const o = menuOwner; closeMenu(); o.click(); }
}
/** Neue Systemwerte oder Updates eines Hosts (hostinfo.js): die Host-Ansicht neu zeichnen, ohne dass das Fenster nach oben springt */
export function hostInfoChanged() {
  const w = wins.get('home');
  if (root && w && nav.view === 'hosts' && !menuOpen()) { const top = w.main.scrollTop; renderHome(); w.main.scrollTop = top; }
}
/** Abgemeldet oder Anmeldung noetig: alle Fenster zu */
export function reset() {
  if (!root) return;
  closeMenu(); closeLauncher(); wm.endMove();
  for (const w of [...wins.values()]) { if (!w.dialog) w.opts.onClose = null; closeWin(w, true); }     // ein Dialog-Fenster meldet beim Schliessen "abgebrochen" (sein Versprechen endet)
  sessWins.clear(); taskWins.clear(); renderTaskChip();
  modules = { caddy: false, ddns: false, services: false }; applyModules();
  heimCtl?.destroy(); heimCtl = null; import('./heimnetz.js').then((h) => h.resetHeimnetz());
  nav.view = 'start'; nav.back = []; nav.fwd = [];
}
