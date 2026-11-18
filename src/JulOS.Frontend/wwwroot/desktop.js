import { Sessions } from './sessions.js';

/* =====================================================================
 * Desktop-Oberflaeche (breite Bildschirme mit Maus): Menueleiste, Dock, Launcher, Home-Fenster und ein Fenster
 * je Sitzung (mehrere gleichzeitig, jede in ihrem Fenster, Tasten nur an das Fenster mit Fokus). Die Verbindungslogik bleibt in app.js; hier wird nur die Huelle gebaut.
 * shell.js laedt diese Datei nur auf solchen Bildschirmen und meldet Ereignisse (Anmeldung, Verbindungen, Sitzungsende).
 * Auf dem Handy wird nichts davon geladen.
 * ===================================================================== */

const $ = (s, r = document) => r.querySelector(s);
const BAR_H = 34, DOCK_RESERVE = 96;
let ctx = null;            // von shell.js: { state, esc, api, connect, edit, add, devices, signOut }
let root = null;
const wins = new Map();    // id -> Fenster
let zTop = 10;
const sessWins = new Map(); // Sitzungskennung -> Fenster

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
  sliders: '<path d="M21 4h-7M10 4H3M21 12h-9M8 12H3M21 20h-5M12 20H3M14 2v4M8 10v4M16 18v4"/>',
  folder: '<path d="M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.7-.9l-.8-1.2A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z"/>',
  cloud: '<path d="M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9z"/>',
  activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  box: '<path d="m21 8-9-5-9 5v8l9 5 9-5z"/><path d="m3.3 7.5 8.7 5 8.7-5M12 22V12.5"/>',
  globe: '<circle cx="12" cy="12" r="10"/><path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>',
  power: '<path d="M12 2v10M18.4 6.6a9 9 0 1 1-12.77.04"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  grid: '<path d="M3 3h7v7H3zM14 3h7v7h-7zM14 14h7v7h-7zM3 14h7v7H3z"/>',
  x: '<path d="M18 6 6 18M6 6l12 12"/>',
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
};
const ic = (n) => `<svg class="dk-i" viewBox="0 0 24 24" aria-hidden="true">${P[n] || ''}</svg>`;
// Der Verlauf steht einmal in DEFS (versteckte SVG im Desktop); jedes Logo verweist nur darauf
const DEFS = `<svg width="0" height="0" style="position:absolute" aria-hidden="true"><defs><linearGradient id="dkl" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#a06cf0"/><stop offset="1" stop-color="#3f8cf5"/></linearGradient></defs></svg>`;
const LOGO = `<svg viewBox="0 0 32 32" aria-hidden="true">
  <path d="M8 5.5 25.5 16 8 26.5z" fill="url(#dkl)" stroke="url(#dkl)" stroke-width="3.6" stroke-linejoin="round"/><path d="M8 5.5 25.5 16 15 16z" fill="#fff" opacity=".28"/></svg>`;

/* Programme: Farben, Symbole, Ziele. soon = gibt es noch keinen Server-Teil dafuer (siehe docs/IDEAS.md). */
const APPS = [
  { id: 'home', name: 'Start', ic: 'home', g: ['#8f8cf7', '#5f6cf0'] },
  { id: 'desktop', name: 'Desktop', sub: 'RDP', ic: 'monitor', g: ['#4fb4f0', '#3b6cf0'] },
  { id: 'terminal', name: 'Terminal', sub: 'SSH', ic: 'terminal', g: ['#5b6275', '#2c3142'] },
  { id: 'settings', name: 'Einstellungen', ic: 'sliders', g: ['#a2a8be', '#6f7590'] },
  { id: 'files', name: 'Dateien', ic: 'folder', g: ['#f7c76a', '#ee9b3a'], soon: true },
  { id: 'docker', name: 'Docker', ic: 'box', g: ['#55b9f5', '#2b7bd8'], soon: true },
  { id: 'caddy', name: 'Caddy', ic: 'globe', g: ['#5fd49a', '#1fa971'], soon: true },
  { id: 'ddns', name: 'DDNS', ic: 'cloud', g: ['#8fd0ff', '#5a9cf5'], soon: true },
  { id: 'monitor', name: 'Monitor', ic: 'activity', g: ['#ee8fc8', '#a05cf0'] },
];
const appById = (id) => APPS.find((a) => a.id === id);
const tile = (g, icon) => `<span class="dk-ti" style="--g1:${g[0]};--g2:${g[1]}">${ic(icon)}</span>`;

/* ---------- kleine Helfer ---------- */
function el(tag, cls, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html !== undefined) e.innerHTML = html; return e; }
const btn = (cls, html, label) => { const b = el('button', 'dk-b ' + (cls || ''), html); b.type = 'button'; if (label) b.setAttribute('aria-label', label); return b; };

let toastTimer = null;
function notify(msg, ms = 3200) {
  if (!msg) return;
  $('.dk-toast', root)?.remove();
  const t = el('div', 'dk-toast'); t.textContent = msg; t.setAttribute('role', 'status'); root.appendChild(t);
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.remove(), ms);
}

/* ---------- Hintergrund: Pastell-Szene (Himmel, Berg, See, Bluetenzweige), erzeugt im Code ---------- */
function scene(withBlossoms) {
  let seed = 7;
  const rnd = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
  let blossoms = '';
  if (withBlossoms) {
    const cluster = (cx, cy, rx, ry, n, dir) => {
      let s = `<path d="M${cx - dir * rx * 1.2} ${cy - ry * 0.5} Q ${cx} ${cy + ry * 0.2} ${cx + dir * rx * 0.8} ${cy + ry * 0.9}" stroke="#7a5a6a" stroke-opacity=".55" stroke-width="5" fill="none" stroke-linecap="round"/>`;
      for (let i = 0; i < n; i++) {
        const a = rnd() * 6.283, d = Math.sqrt(rnd());
        const x = cx + Math.cos(a) * rx * d, y = cy + Math.sin(a) * ry * d, r = 6 + rnd() * 16;
        const tone = ['#f7c3d9', '#f3a6c6', '#fbdbe8', '#ee92b8', '#ffffff'][Math.floor(rnd() * 5)];
        s += `<circle cx="${x.toFixed(0)}" cy="${y.toFixed(0)}" r="${r.toFixed(0)}" fill="${tone}" opacity="${(0.45 + rnd() * 0.5).toFixed(2)}"/>`;
      }
      return s;
    };
    blossoms = `<g filter="url(#dkb)">${cluster(150, 190, 260, 170, 150, 1)}${cluster(1480, 120, 280, 150, 130, -1)}${cluster(60, 560, 150, 190, 70, 1)}</g>
      <g>${cluster(180, 210, 200, 120, 40, 1)}${cluster(1450, 130, 210, 100, 34, -1)}</g>`;
  }
  return `<svg viewBox="0 0 1600 900" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
    <defs>
      <linearGradient id="dks" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#b9b6ec"/><stop offset=".42" stop-color="#ecc6e0"/><stop offset=".62" stop-color="#d9dcf6"/></linearGradient>
      <linearGradient id="dkw" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#cdd6f5"/><stop offset=".6" stop-color="#d7cdee"/><stop offset="1" stop-color="#b9bde8"/></linearGradient>
      <linearGradient id="dkm" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#9aa0dd"/><stop offset="1" stop-color="#c9c4ee"/></linearGradient>
      <radialGradient id="dkg" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#fff" stop-opacity=".85"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></radialGradient>
      <filter id="dkb" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="9"/></filter>
    </defs>
    <rect width="1600" height="900" fill="url(#dks)"/>
    <ellipse cx="860" cy="470" rx="620" ry="190" fill="url(#dkg)"/>
    <path d="M0 520 L180 450 L330 500 L520 420 L700 500 L900 440 L1100 510 L1300 440 L1480 500 L1600 460 V600 H0z" fill="#c4c2ec" opacity=".75"/>
    <path d="M430 600 L760 330 Q800 300 840 330 L1170 600z" fill="url(#dkm)"/>
    <path d="M700 380 L760 330 Q800 300 840 330 L905 385 Q870 365 850 392 Q830 372 805 400 Q785 372 760 395 Q735 372 700 380z" fill="#fff" opacity=".88"/>
    <path d="M0 600 H1600 V900 H0z" fill="url(#dkw)"/>
    <path d="M430 600 L760 750 L1170 600z" fill="#9aa0dd" opacity=".22"/>
    <g fill="#fff" opacity=".35"><ellipse cx="500" cy="660" rx="260" ry="4"/><ellipse cx="1150" cy="700" rx="320" ry="5"/><ellipse cx="820" cy="760" rx="380" ry="5"/><ellipse cx="300" cy="820" rx="260" ry="5"/></g>
    ${blossoms}
  </svg>`;
}

/* =====================================================================
 *  Menues (Menueleiste, Dock, Schnellaktionen)
 * ===================================================================== */
let menuEl = null, menuOwner = null;
function closeMenu() { menuEl?.remove(); menuEl = null; if (menuOwner) menuOwner.classList.remove('open'); menuOwner = null; }
/** items: { label, icon, hint, disabled, run } oder { sep: true } oder { cap: 'Text' }; pos: 'below' (unter dem Anker) oder 'above' */
function showMenu(anchor, items, pos = 'below') {
  closeMenu();
  const m = el('div', 'dk-menu'); m.setAttribute('role', 'menu');
  for (const it of items) {
    if (it.sep) { m.appendChild(el('div', 'dk-sepl')); continue; }
    if (it.cap) { m.appendChild(el('div', 'dk-cap')).textContent = it.cap; continue; }
    const b = btn('dk-it', (it.icon ? ic(it.icon) : '') + '<span></span>' + (it.hint ? '<span class="dk-hint"></span>' : ''));
    b.setAttribute('role', 'menuitem'); b.querySelector('span').textContent = it.label;
    if (it.hint) b.querySelector('.dk-hint').textContent = it.hint;
    if (it.disabled) b.disabled = true;
    else b.addEventListener('click', () => { closeMenu(); it.run && it.run(); });
    m.appendChild(b);
  }
  root.appendChild(m); menuEl = m;
  const r = anchor.getBoundingClientRect(), w = m.offsetWidth, h = m.offsetHeight;
  m.style.left = clamp(pos === 'above' ? r.left + r.width / 2 - w / 2 : r.left, 8, innerWidth - w - 8) + 'px';
  m.style.top = (pos === 'above' ? Math.max(BAR_H + 6, r.top - h - 14) : r.bottom + 4) + 'px';
  menuOwner = anchor.classList.contains('dk-mi') ? anchor : null;
  menuOwner?.classList.add('open');
}
document.addEventListener('pointerdown', (e) => {
  if (menuEl && !e.target.closest('.dk-menu') && !e.target.closest('.dk-mi')) closeMenu();
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
  w = Math.min(w, v.w - 40); h = Math.min(h, v.h - BAR_H - DOCK_RESERVE);
  return { x: Math.round((v.w - w) / 2 + dx), y: Math.round(BAR_H + Math.max(12, (v.h - BAR_H - DOCK_RESERVE - h) / 2) + dy), w, h };
}
function fitRect(r, minW, minH) {
  const v = viewport();
  const w = clamp(r.w, minW, v.w), h = clamp(r.h, minH, v.h - BAR_H);
  // moeglichst ganz sichtbar halten; nur wenn der Bildschirm zu klein ist, ragt etwas heraus
  return { w, h, x: clamp(r.x, 0, Math.max(0, v.w - w)), y: clamp(r.y, BAR_H, Math.max(BAR_H, v.h - h)) };
}

/** Legt ein Fenster an. opts: id, title, icon (Kachel-HTML), cls, rect, minW, minH, head (Element fuer die Titelleiste), onClose */
function createWindow(opts) {
  const w = { id: opts.id, minimized: false, maximized: false, opts };
  const e = el('div', 'dk-win ' + (opts.cls || ''));
  e.dataset.win = opts.id; e.setAttribute('role', 'dialog'); e.setAttribute('aria-label', opts.title);
  const head = el('div', 'dk-wh');
  const title = el('div', 'dk-wt', (opts.icon || '') + '<span></span>'); title.lastElementChild.textContent = opts.title;
  head.appendChild(title);
  const extra = el('div', 'dk-wx'); if (opts.head) extra.appendChild(opts.head); head.appendChild(extra);
  const ctl = el('div', 'dk-wc');
  const bMin = btn('', ic('minus'), 'Minimieren'), bMax = btn('', ic('square'), 'Maximieren'), bCl = btn('dk-cl', ic('x'), 'Schließen');
  ctl.append(bMin, bMax, bCl); head.appendChild(ctl);
  const body = el('div', 'dk-wb');
  e.append(head, body);
  for (const d of ['n', 's', 'w', 'e', 'nw', 'ne', 'sw', 'se']) { const z = el('div', 'dk-rz ' + d); z.dataset.dir = d; e.appendChild(z); }
  w.el = e; w.body = body; w.head = head; w.bMax = bMax;
  const saved = ls.get('rect.' + (opts.rectKey || opts.id), null);
  const rect = fitRect(saved || opts.rect, opts.minW || 320, opts.minH || 220);
  if (opts.cascade) { rect.x += opts.cascade; rect.y += opts.cascade; Object.assign(rect, fitRect(rect, opts.minW || 320, opts.minH || 220)); }
  Object.assign(e.style, { left: rect.x + 'px', top: rect.y + 'px', width: rect.w + 'px', height: rect.h + 'px' });
  root.appendChild(e); wins.set(opts.id, w);
  // Rahmen des neuen Fensters zeigt die Animation nur einmal; danach darf keine Animation die Transform-Matrix halten (Maus-Koordinaten)
  e.addEventListener('animationend', (ev) => { if (ev.target === e) e.style.animation = 'none'; });

  e.addEventListener('pointerdown', () => focusWin(w), true);
  bMin.addEventListener('click', () => minimizeWin(w));
  bMax.addEventListener('click', () => toggleMax(w));
  bCl.addEventListener('click', () => closeWin(w));
  head.addEventListener('dblclick', (ev) => { if (!ev.target.closest('button, input')) toggleMax(w); });
  wireDrag(w, head); wireResize(w);
  focusWin(w);
  renderDock();
  return w;
}

function focusWin(w) {
  if (w.minimized) return;
  w.el.style.zIndex = ++zTop;
  for (const o of wins.values()) o.el.classList.toggle('active', o === w);
  if (w.opts.onFocus) w.opts.onFocus(w);       // Sitzungsfenster: die Sitzung wird die aktive (Tasten gehen an sie)
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
function toggleMax(w) {
  w.maximized = !w.maximized; w.el.classList.toggle('max', w.maximized);
  w.bMax.innerHTML = ic(w.maximized ? 'restore' : 'square'); w.bMax.setAttribute('aria-label', w.maximized ? 'Wiederherstellen' : 'Maximieren');
}
function closeWin(w) {
  if (!wins.has(w.id)) return;
  w.opts.onClose && w.opts.onClose(w);
  w.el.remove(); wins.delete(w.id); renderDock(); renderBar();
}
const saveRect = (w) => { if (!w.maximized) ls.set('rect.' + w.id, { x: w.el.offsetLeft, y: w.el.offsetTop, w: w.el.offsetWidth, h: w.el.offsetHeight }); };

function wireDrag(w, head) {
  head.addEventListener('pointerdown', (ev) => {
    if (ev.button !== 0 || ev.target.closest('button, input') || w.maximized) return;
    ev.preventDefault();
    const sx = ev.clientX, sy = ev.clientY, ox = w.el.offsetLeft, oy = w.el.offsetTop, v = viewport();
    head.setPointerCapture(ev.pointerId); w.el.classList.add('dragging'); root.classList.add('dk-moving');
    const move = (m) => {
      w.el.style.left = clamp(ox + m.clientX - sx, 120 - w.el.offsetWidth, v.w - 120) + 'px';
      w.el.style.top = clamp(oy + m.clientY - sy, BAR_H, v.h - 60) + 'px';
    };
    const up = () => { head.removeEventListener('pointermove', move); head.removeEventListener('pointerup', up); head.removeEventListener('pointercancel', up); w.el.classList.remove('dragging'); root.classList.remove('dk-moving'); saveRect(w); };
    head.addEventListener('pointermove', move); head.addEventListener('pointerup', up); head.addEventListener('pointercancel', up);
  });
}
function wireResize(w) {
  for (const z of w.el.querySelectorAll('.dk-rz')) z.addEventListener('pointerdown', (ev) => {
    if (ev.button !== 0) return;
    ev.preventDefault(); ev.stopPropagation(); focusWin(w);
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
    const r = fitRect({ x: w.el.offsetLeft, y: w.el.offsetTop, w: w.el.offsetWidth, h: w.el.offsetHeight }, w.opts.minW || 320, w.opts.minH || 220);
    Object.assign(w.el.style, { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px' });
  }
});

/* =====================================================================
 *  Menueleiste
 * ===================================================================== */
const MENUS = () => ({
  Desktop: [
    { label: 'Start öffnen', icon: 'home', run: () => openHome() },
    { label: 'Alle Fenster minimieren', icon: 'minus', run: () => wins.forEach(minimizeWin) },
    { label: 'Alle Fenster anzeigen', icon: 'restore', run: () => wins.forEach((w) => w.minimized && restoreWin(w)) },
    { label: 'Sitzungen nebeneinander', icon: 'grid', run: () => tileSessions(), disabled: sessWins.size < 2 },
    { label: 'Sitzungen überlappend', icon: 'restore', run: () => cascadeSessions(), disabled: sessWins.size < 2 },
    { sep: true },
    { label: 'Neue Verbindung …', icon: 'plus', run: () => ctx.add() },
  ],
  Dateien: [{ label: 'Dateien', icon: 'folder', hint: 'bald verfügbar', disabled: true }],
  Apps: [
    { label: 'Launcher öffnen', icon: 'grid', run: () => openLauncher() },
    { sep: true },
    { label: 'Desktop verbinden …', icon: 'monitor', run: () => pickConnection('desktop', $('[data-menu="Apps"]', root)) },
    { label: 'Terminal verbinden …', icon: 'terminal', run: () => pickConnection('terminal', $('[data-menu="Apps"]', root)) },
    { sep: true },
    ...APPS.filter((a) => a.soon).map((a) => ({ label: a.name, icon: a.ic, hint: 'bald verfügbar', disabled: true })),
  ],
  System: [
    { label: 'Geräte und Sitzungen …', icon: 'shield', run: () => ctx.devices() },
    { label: 'Einstellungen', icon: 'sliders', run: () => openHome('einstellungen') },
    { label: 'System-Übersicht', icon: 'activity', run: () => openHome('system') },
    { sep: true },
    { label: 'Abmelden', icon: 'power', run: () => ctx.signOut() },
  ],
  Hilfe: [{ label: 'Über JulOS', icon: 'info', run: () => openAbout() }],
});

function buildBar() {
  const bar = el('header', 'dk-bar');
  const logo = btn('dk-logo', LOGO + '<span>JulOS</span>', 'Start öffnen'); logo.addEventListener('click', () => openHome());
  bar.appendChild(logo);
  const menus = MENUS();
  for (const name of Object.keys(menus)) {
    const b = btn('dk-mi', name); b.dataset.menu = name;
    const open = () => showMenu(b, MENUS()[name]);
    b.addEventListener('click', () => (menuOwner === b ? closeMenu() : open()));
    b.addEventListener('mouseenter', () => { if (menuEl && menuOwner && menuOwner !== b) open(); });   // wie in einer Menueleiste: bei offenem Menue wechselt Darueberfahren
    bar.appendChild(b);
  }
  bar.appendChild(el('div', 'dk-sp'));
  const sess = btn('dk-ic dk-sess', '<span></span>'); sess.hidden = true; sess.id = 'dkSess'; sess.addEventListener('click', () => sessionMenu(sess));
  const search = btn('dk-ic', ic('search'), 'Suchen'); search.dataset.launcher = '1'; search.addEventListener('click', () => (launcher ? closeLauncher() : openLauncher()));
  const clock = el('div', 'dk-clock'); clock.id = 'dkClock';
  const user = btn('dk-ic', '<span class="dk-avatar"></span>', 'Konto'); user.id = 'dkUser';
  user.addEventListener('click', () => showMenu(user, [
    { cap: ctx.state.user || '' },
    { label: 'Geräte und Sitzungen …', icon: 'shield', run: () => ctx.devices() },
    { label: 'Einstellungen', icon: 'sliders', run: () => openHome('einstellungen') },
    { sep: true },
    { label: 'Abmelden', icon: 'power', run: () => ctx.signOut() },
  ]));
  bar.append(sess, search, clock, user);
  return bar;
}
function renderBar() {
  const s = $('#dkSess', root); if (!s) return;
  const n = sessWins.size, a = Sessions.active;
  s.hidden = !n;
  if (n) s.querySelector('span').textContent = n === 1 ? (a ? a.title : 'Sitzung') : `${n} Sitzungen`;
}
/** Menue der offenen Sitzungen (Menueleiste): wechseln, schliessen */
function sessionMenu(anchor) {
  const items = [{ cap: 'Offene Sitzungen' }];
  for (const w of sessWins.values()) {
    const s = w.session;
    items.push({ label: s.title, icon: s.kind === 'terminal' ? 'terminal' : 'monitor', hint: s.stateText, run: () => { w.minimized ? restoreWin(w) : focusWin(w); } });
  }
  items.push({ sep: true }, { label: 'Nebeneinander anordnen', icon: 'grid', run: () => tileSessions(), disabled: sessWins.size < 2 }, { label: 'Überlappend anordnen', icon: 'restore', run: () => cascadeSessions(), disabled: sessWins.size < 2 });
  showMenu(anchor, items);
}
const fmtClock = () => new Date().toLocaleString('de-DE', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
function tickClock() {
  const c = $('#dkClock', root); if (c) c.textContent = fmtClock();
  const d = new Date();
  for (const t of root.querySelectorAll('.dk-hero-time')) t.textContent = d.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
  for (const t of root.querySelectorAll('.dk-hero-date')) t.textContent = d.toLocaleDateString('de-DE', { weekday: 'long', day: 'numeric', month: 'long' });
}

/* =====================================================================
 *  Dock
 * ===================================================================== */
function buildDock() {
  const dock = el('nav', 'dk-dock'); dock.setAttribute('aria-label', 'Dock');
  const item = (id, name, a, extra) => {
    const b = btn('dk-di', `${ic(a.ic)}<span class="dk-dot"></span><span class="dk-tip"></span>`, name);
    b.querySelector('.dk-tip').textContent = name; b.dataset.dock = id; b.style.setProperty('--g1', a.g[0]); b.style.setProperty('--g2', a.g[1]);
    b.addEventListener('click', () => dockClick(id, b));
    if (extra) b.dataset.launcher = '1';
    return b;
  };
  dock.append(
    item('launcher', 'Launcher', { ic: 'grid', g: ['#b08cf5', '#6a58e6'] }, true),
    item('home', 'Start', appById('home')),
    el('div', 'dk-dsep'),
    item('desktop', 'Desktop (RDP)', appById('desktop')),
    item('terminal', 'Terminal (SSH)', appById('terminal')),
    el('div', 'dk-dsep'),
    item('settings', 'Einstellungen', appById('settings')),
  );
  return dock;
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
  for (const w of sessWins.values()) { const k = w.session.kind; if (!state[k] || (state[k].minimized && !w.minimized)) state[k] = w; }
  for (const b of root.querySelectorAll('.dk-di[data-dock]')) {
    const w = state[b.dataset.dock];
    b.classList.toggle('run', !!w); b.classList.toggle('hid', !!w && w.minimized);
  }
}

/** Startet ein Programm: Fenster oeffnen oder nach vorn holen, Verbindung waehlen */
function runApp(id, anchor) {
  const a = appById(id);
  if (a && a.soon) return notify(`${a.name} kommt später – dafür gibt es noch keinen Server-Teil.`);
  if (id === 'home') return openHome();
  if (id === 'settings') return openHome('einstellungen');
  if (id === 'monitor') return openHome('system');
  if (id === 'desktop' || id === 'terminal') return pickConnection(id, anchor);
}

/** Verbindung fuer Desktop oder Terminal waehlen: eine = direkt, mehrere = Menue, keine = neue anlegen */
function pickConnection(kind, anchor) {
  const label = kind === 'desktop' ? 'Desktop' : 'Terminal';
  const list = ctx.state.connections.filter((c) => c.kind === kind);
  const running = [...sessWins.values()].filter((w) => w.session.kind === kind);
  if (running.length && list.length <= 1) { const w = running.find((x) => x.session === Sessions.active) || running[0]; w.minimized ? restoreWin(w) : focusWin(w); return; }
  if (!list.length) { notify(`Noch keine ${label}-Verbindung. Lege zuerst eine an.`); return ctx.add(); }
  if (list.length === 1) return ctx.connect(list[0]);
  showMenu(anchor, [{ cap: label + ' verbinden mit' }, ...list.map((c) => ({ label: c.name, icon: kind === 'terminal' ? 'terminal' : 'monitor', hint: Sessions.find(c.id).length ? `${Sessions.find(c.id).length} offen` : c.host, run: () => ctx.connect(c) })),
    ...(list.some((c) => Sessions.find(c.id).length) ? [{ sep: true }, { cap: 'Weitere Sitzung zu' }, ...list.filter((c) => Sessions.find(c.id).length).map((c) => ({ label: c.name, icon: 'plus', run: () => ctx.connect(c, { extra: true }) }))] : [])],
    anchor.classList.contains('dk-di') ? 'above' : 'below');
}

/* =====================================================================
 *  Launcher
 * ===================================================================== */
let launcher = null;
function closeLauncher() { launcher?.remove(); launcher = null; }
function openLauncher() {
  closeMenu(); closeLauncher();
  const L = el('div', 'dk-launch'); L.setAttribute('role', 'dialog'); L.setAttribute('aria-label', 'JulOS Launcher');
  L.innerHTML = `<h2>${LOGO}<span>JulOS Launcher</span></h2>
    <label class="dk-q">${ic('search')}<input type="text" placeholder="Programme und Verbindungen suchen …" autocomplete="off" spellcheck="false"></label>
    <div class="dk-res"></div>
    <div class="dk-foot"><button type="button" class="dk-b" data-f="signout" aria-label="Abmelden" title="Abmelden">${ic('power')}</button><button type="button" class="dk-b" data-f="devices" aria-label="Geräte und Sitzungen" title="Geräte und Sitzungen">${ic('user')}</button></div>`;
  root.appendChild(L); launcher = L;
  const input = $('input', L), res = $('.dk-res', L);
  let sel = 0, hits = [];
  const render = () => {
    const q = input.value.trim().toLowerCase();
    res.replaceChildren();
    if (!q) {
      const g = el('div', 'dk-grid'); hits = [];
      for (const a of APPS) {
        const b = btn('dk-app', tile(a.g, a.ic) + '<span class="dk-n"></span>' + (a.soon ? '<span class="dk-tag">bald</span>' : '')); b.querySelector('.dk-n').textContent = a.name;
        if (a.soon) { b.setAttribute('aria-disabled', 'true'); b.title = 'Bald verfügbar'; }
        b.addEventListener('click', () => { closeLauncher(); runApp(a.id, b); });
        g.appendChild(b);
      }
      res.appendChild(g); return;
    }
    hits = [
      ...APPS.filter((a) => a.name.toLowerCase().includes(q) || (a.sub || '').toLowerCase().includes(q)).map((a) => ({ name: a.name, sub: a.soon ? 'bald verfügbar' : a.sub || 'Programm', g: a.g, ic: a.ic, soon: a.soon, run: (anchor) => runApp(a.id, anchor) })),
      ...ctx.state.connections.filter((c) => (c.name + ' ' + c.host).toLowerCase().includes(q)).map((c) => ({ name: c.name, sub: c.kind === 'terminal' ? 'Terminal' : 'Desktop', g: appById(c.kind).g, ic: appById(c.kind).ic, run: () => ctx.connect(c) })),
    ];
    sel = clamp(sel, 0, Math.max(0, hits.length - 1));
    if (!hits.length) { res.appendChild(el('div', 'dk-none')).textContent = 'Nichts gefunden.'; return; }
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
  { id: 'start', name: 'Start', ic: 'home' }, { id: 'apps', name: 'Apps', ic: 'grid' }, { id: 'dienste', name: 'Dienste', ic: 'monitor' },
  { id: 'dateien', name: 'Dateien', ic: 'folder', soon: true }, { id: 'system', name: 'System', ic: 'activity' }, { id: 'einstellungen', name: 'Einstellungen', ic: 'sliders' },
];
const nav = { view: 'start', back: [], fwd: [] };
const greeting = () => { const h = new Date().getHours(); return h < 5 ? 'Gute Nacht' : h < 11 ? 'Guten Morgen' : h < 17 ? 'Guten Tag' : h < 22 ? 'Guten Abend' : 'Gute Nacht'; };

function openHome(view) {
  let w = wins.get('home');
  if (!w) {
    const head = el('div', 'dk-wx');
    head.style.flex = '1';
    const back = btn('dk-nv', ic('left'), 'Zurück'), fwd = btn('dk-nv', ic('right'), 'Vorwärts');
    back.dataset.nv = 'back'; fwd.dataset.nv = 'fwd';
    const find = btn('dk-find', ic('search') + '<span>Programme und Verbindungen suchen …</span>', 'Suchen'); find.dataset.launcher = '1';
    find.addEventListener('click', () => openLauncher());
    head.append(back, fwd, find);
    back.addEventListener('click', () => { if (nav.back.length) { nav.fwd.push(nav.view); nav.view = nav.back.pop(); renderHome(); } });
    fwd.addEventListener('click', () => { if (nav.fwd.length) { nav.back.push(nav.view); nav.view = nav.fwd.pop(); renderHome(); } });
    w = createWindow({ id: 'home', title: 'Start', icon: `<span class="dk-ti" style="--g1:#8f8cf7;--g2:#5f6cf0;width:22px;height:22px;border-radius:7px">${ic('home')}</span>`,
      rect: defaultRect('home', 960, 640, -30, 0), minW: 640, minH: 420, head, onClose: () => { stopOverview(); nav.view = 'start'; nav.back = []; nav.fwd = []; } });
    w.head.querySelector('.dk-wt').style.minWidth = '60px';
    w.body.innerHTML = '<div class="dk-home"><nav class="dk-side" aria-label="Bereiche"></nav><div class="dk-main"></div></div>';
    w.side = $('.dk-side', w.body); w.main = $('.dk-main', w.body);
    w.side.addEventListener('click', (e) => {
      const b = e.target.closest('[data-nav]'); if (!b) return;
      if (b.getAttribute('aria-disabled') === 'true') return notify(`${b.dataset.name} kommt später – dafür gibt es noch keinen Server-Teil.`);
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
    if (i === 3) w.side.appendChild(document.createElement('hr'));
    const b = btn('dk-nav' + (n.id === nav.view ? ' sel' : ''), ic(n.ic) + '<span></span>' + (n.soon ? '<span class="dk-tag">bald</span>' : '')); b.querySelector('span').textContent = n.name;
    b.dataset.nav = n.id; b.dataset.name = n.name; if (n.soon) b.setAttribute('aria-disabled', 'true');
    w.side.appendChild(b);
  });
  const hist = w.head.querySelectorAll('.dk-nv');
  hist[0].disabled = !nav.back.length; hist[1].disabled = !nav.fwd.length;
  const m = w.main; m.scrollTop = 0;
  stopOverview();
  ({ start: renderStart, apps: renderAppsView, dienste: renderServices, system: renderSystem, einstellungen: renderSettings }[nav.view] || renderStart)(m);
  tickClock();
}

const connTile = (c) => {
  const n = Sessions.find(c.id).length, open = n > 0;
  const a = appById(c.kind);
  return `<button type="button" class="dk-b dk-tile" data-conn="${ctx.esc(c.id)}">${tile(a.g, a.ic)}<span class="dk-tt"><b>${ctx.esc(c.name)}</b><i class="${open ? 'on' : ''}">${open ? (n > 1 ? n + ' Sitzungen offen' : 'Sitzung offen') : (c.kind === 'terminal' ? 'Terminal' : 'Desktop') + ' · ' + ctx.esc(c.host)}</i></span></button>`;
};
function renderStart(m) {
  const conns = ctx.state.connections;
  m.innerHTML = `
    <section class="dk-hero"><div class="dk-wp">${scene(false)}</div>
      <div class="dk-hi"><h2>${greeting()}</h2><p>Dein Server-Arbeitsplatz an einem Ort.</p></div>
      <div class="dk-hd"><span class="dk-hero-date"></span><b class="dk-hero-time"></b></div></section>
    <h3>Deine Dienste <button type="button" class="dk-b dk-more" data-go="dienste">Alle Dienste ›</button></h3>
    <div class="dk-tiles">${conns.map(connTile).join('')}<button type="button" class="dk-b dk-tile add" data-act="add"><span class="dk-ti" style="--g1:#cfc8f7;--g2:#b3aaf2;box-shadow:none">${ic('plus')}</span><span class="dk-tt"><b>Verbindung</b><i>hinzufügen</i></span></button></div>
    <h3>Schnellaktionen</h3>
    <div class="dk-qa">
      <button type="button" class="dk-b" data-act="add">${ic('plus')}<span>Neue Verbindung</span></button>
      <button type="button" class="dk-b" data-act="desktop">${ic('monitor')}<span>Desktop verbinden</span></button>
      <button type="button" class="dk-b" data-act="terminal">${ic('terminal')}<span>Terminal öffnen</span></button>
      <button type="button" class="dk-b" data-act="devices">${ic('shield')}<span>Geräte</span></button>
      <button type="button" class="dk-b" data-act="settings">${ic('sliders')}<span>Einstellungen</span></button>
    </div>
    <h3>System-Übersicht <button type="button" class="dk-b dk-more" data-go="system">Monitor öffnen ›</button></h3>
    ${overviewCards()}`;
  watchOverview(m);
}

/* ---------- System-Uebersicht: echte Messwerte des Rechners, auf dem der Server laeuft (GET /api/system/overview) ---------- */
const GIB = 1024 ** 3;
const num = (n, d = 1) => n.toLocaleString('de-DE', { minimumFractionDigits: d, maximumFractionDigits: d });
const gib = (b) => num(b / GIB, b / GIB >= 100 ? 0 : 1);
const rate = (b) => (b >= 1e6 ? num(b / 1e6) + ' MB/s' : b >= 1e3 ? num(b / 1e3, 0) + ' kB/s' : num(b, 0) + ' B/s');
const NET_TIP = 'Netzwerk des JulOS-Containers: der Datenverkehr, den JulOS selbst verursacht (Browser, Fernsteuerung), nicht der des ganzen Rechners.';
const pct = (a, b) => (b > 0 ? Math.round((100 * a) / b) : 0);
function overviewCards() {
  const card = (key, icon, name, tip) => `<div class="dk-sysc" data-ov="${key}"${tip ? ` title="${tip}"` : ''}><b>${ic(icon)}${name}</b><div class="dk-sv">…</div><div class="dk-bar0"><i></i></div></div>`;
  return `<div class="dk-sys">${card('cpu', 'cpu', 'CPU')}${card('ram', 'ram', 'RAM')}${card('disk', 'disk', 'Festplatte')}${card('net', 'network', 'Netzwerk (JulOS)', NET_TIP)}</div>`;
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
  set('cpu', o.cpuPercent == null ? 'misst …' : Math.round(o.cpuPercent) + ' %', o.cpuPercent == null ? null : o.cpuPercent);
  set('ram', o.memoryTotal == null ? '–' : `${gib(o.memoryUsed)} / ${gib(o.memoryTotal)} GiB (${pct(o.memoryUsed, o.memoryTotal)} %)`, o.memoryTotal == null ? null : pct(o.memoryUsed, o.memoryTotal));
  set('disk', o.diskTotal == null ? '–' : `${gib(o.diskUsed)} / ${gib(o.diskTotal)} GiB (${pct(o.diskUsed, o.diskTotal)} %)`, o.diskTotal == null ? null : pct(o.diskUsed, o.diskTotal));
  set('net', o.rxBytesPerSecond == null ? 'misst …' : `↓ ${rate(o.rxBytesPerSecond)}  ↑ ${rate(o.txBytesPerSecond)}`, null);
  const d = $('[data-ov-details]', root0);
  if (d) d.textContent = o.sampledAt ? 'Zuletzt gemessen um ' + new Date(o.sampledAt).toLocaleTimeString('de-DE') + (o.cores ? ` · ${o.cores} Prozessorkerne` : '') : '';
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
  m.innerHTML = `<h3 style="margin-top:14px">System</h3>
    ${overviewCards()}
    <p class="dk-lead" data-ov-details></p>
    <div class="dk-card" style="margin-top:14px"><h4>Was hier gemessen wird</h4>
      <p><b>CPU und RAM:</b> der Rechner, auf dem Docker mit JulOS läuft (nicht eine Grenze des Containers).</p>
      <p><b>Festplatte:</b> der Datenträger, auf dem der gemeinsame Ordner (Upload-Ordner) liegt.</p>
      <p style="margin-bottom:2px"><b>Netzwerk:</b> nur der Datenverkehr des JulOS-Containers, also was Browser, Fernsteuerung und Terminal verursachen. Der Verkehr des ganzen Rechners wäre nur mit <code>network_mode: host</code> sichtbar.</p></div>`;
  watchOverview(m);
}
function renderServices(m) {
  const conns = ctx.state.connections;
  m.innerHTML = `<h3 style="margin-top:14px">Dienste <button type="button" class="dk-b dk-btn pri" style="margin-left:auto" data-act="add">${ic('plus')}Verbindung hinzufügen</button></h3>
    ${conns.length ? '' : '<p class="dk-lead">Noch keine Verbindung. Lege den Computer an, den du steuern willst.</p>'}
    <div class="dk-list">${conns.map((c) => {
      const a = appById(c.kind), n = Sessions.find(c.id).length, open = n > 0;
      return `<div class="dk-row">${tile(a.g, a.ic)}<span class="dk-tt"><b>${ctx.esc(c.name)}${open ? ` <span class="dk-tag">${n > 1 ? n + ' Sitzungen' : 'Sitzung offen'}</span>` : ''}</b><i>${c.kind === 'terminal' ? 'Terminal (SSH)' : 'Desktop (RDP)'} · ${ctx.esc(c.username ? c.username + '@' : '')}${ctx.esc(c.host)}:${c.port}${c.hasPassword ? ' · Passwort gespeichert' : ''}</i></span>
        <button type="button" class="dk-b dk-btn pri" data-conn="${ctx.esc(c.id)}">${ic('play')}${open ? 'Öffnen' : 'Verbinden'}</button>
        ${open ? `<button type="button" class="dk-b dk-btn sq" data-extra="${ctx.esc(c.id)}" aria-label="Weitere Sitzung" title="Weitere Sitzung zu diesem Ziel">${ic('plus')}</button>` : ''}
        <button type="button" class="dk-b dk-btn sq" data-edit="${ctx.esc(c.id)}" aria-label="Bearbeiten" title="Bearbeiten">${ic('edit')}</button></div>`;
    }).join('')}</div>`;
}
function renderAppsView(m) {
  m.innerHTML = `<h3 style="margin-top:14px">Apps</h3><div class="dk-grid">${APPS.map((a) => `<button type="button" class="dk-b dk-app" data-app="${a.id}"${a.soon ? ' aria-disabled="true" title="Bald verfügbar"' : ''}>${tile(a.g, a.ic)}<span class="dk-n">${a.name}</span>${a.soon ? '<span class="dk-tag">bald</span>' : ''}</button>`).join('')}</div>
    <p class="dk-lead">Ausgegraute Apps brauchen noch einen Server-Teil.</p>`;
}
function renderSettings(m) {
  m.innerHTML = `<h3 style="margin-top:14px">Einstellungen</h3>
    <div class="dk-card"><h4>Konto</h4><p>Angemeldet als <b>${ctx.esc(ctx.state.user || '')}</b>.</p>
      <div class="dk-btns"><button type="button" class="dk-b dk-btn" data-act="devices">${ic('shield')}Geräte und Sitzungen</button><button type="button" class="dk-b dk-btn" data-act="signout">${ic('power')}Abmelden</button></div></div>
    <div class="dk-card"><h4>Fernsteuerung</h4><p>Zoom, Tastatur, Farben und Text senden findest du im Menü (⋯) unten in jedem Sitzungsfenster.</p></div>
    <div class="dk-card"><h4>Darstellung</h4><p>Die Desktop-Oberfläche ist zurzeit immer hell. Ein dunkles Design folgt später.</p></div>`;
}
function onMainClick(e) {
  const t = e.target.closest('[data-conn],[data-edit],[data-extra],[data-act],[data-go],[data-app]'); if (!t) return;
  const find = (id) => ctx.state.connections.find((c) => c.id === id);
  if (t.dataset.conn) { const c = find(t.dataset.conn); c && ctx.connect(c); }
  else if (t.dataset.extra) { const c = find(t.dataset.extra); c && ctx.connect(c, { extra: true }); }
  else if (t.dataset.edit) { const c = find(t.dataset.edit); c && ctx.edit(c); }
  else if (t.dataset.go) goto(t.dataset.go);
  else if (t.dataset.app) runApp(t.dataset.app, t);
  else if (t.dataset.act === 'add') ctx.add();
  else if (t.dataset.act === 'devices') ctx.devices();
  else if (t.dataset.act === 'signout') ctx.signOut();
  else if (t.dataset.act === 'settings') goto('einstellungen');
  else if (t.dataset.act === 'desktop' || t.dataset.act === 'terminal') pickConnection(t.dataset.act, t);
}

/* ---------- Ueber JulOS ---------- */
async function openAbout() {
  const old = wins.get('about'); if (old) return old.minimized ? restoreWin(old) : focusWin(old);
  const w = createWindow({ id: 'about', title: 'Über JulOS', icon: LOGO, rect: defaultRect('about', 380, 250), minW: 320, minH: 220 });
  let version = '';
  try { version = (await (await fetch('/api/health')).json()).version || ''; } catch { /* ohne Version */ }
  w.body.innerHTML = `<div style="padding:22px 26px;text-align:center"><div style="width:64px;margin:0 auto">${LOGO}</div><h2 style="margin:10px 0 2px">JulOS</h2><p class="dk-lead" style="margin:0">${version ? 'Version ' + ctx.esc(version) : ''}</p><p class="dk-lead">Dein Server-Arbeitsplatz im Browser: Desktop und Terminal deiner Rechner, an einem Ort.</p></div>`;
}

/* =====================================================================
 *  Sitzungsfenster: jede Sitzung (app.js, sessions.js) haengt in einem eigenen Fenster
 * ===================================================================== */
const SESSION_RECT = { w: 1000, h: 680 };
const sessionIcon = (c) => { const a = appById(c.kind === 'terminal' ? 'terminal' : 'desktop'); return `<span class="dk-ti" style="--g1:${a.g[0]};--g2:${a.g[1]};width:22px;height:22px;border-radius:7px">${ic(a.ic)}</span>`; };
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
  const dot = el('span', 'dk-sdot busy'); dot.title = session.stateText; w.head.querySelector('.dk-wt').prepend(dot);
  w.dot = dot;
  session.addEventListener('titlechange', (e) => { w.head.querySelector('.dk-wt span:last-child').textContent = e.detail.title; w.el.setAttribute('aria-label', e.detail.title); renderBar(); });
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
    if (w.maximized) toggleMax(w);
    Object.assign(w.el.style, { left: rects[i].x + 'px', top: rects[i].y + 'px', width: rects[i].w + 'px', height: rects[i].h + 'px' });
    saveRect(w);
  });
}
function tileSessions() {
  const list = [...sessWins.values()].filter((w) => !w.minimized);
  if (!list.length) return;
  const v = viewport(), gap = 8, top = BAR_H + gap, bottom = v.h - DOCK_RESERVE + 28, width = v.w - gap * 2;
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
  await new Promise((done) => {
    const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = 'desktop.css'; l.onload = l.onerror = done; document.head.appendChild(l);
  });
  document.documentElement.classList.add('desktop');
  root = $('#desktop');
  root.replaceChildren();
  const wp = el('div', 'dk-wp'); wp.innerHTML = scene(true);
  root.insertAdjacentHTML('afterbegin', DEFS);
  root.append(wp, buildBar(), buildDock());
  setInterval(tickClock, 15000); tickClock();
  // Beim Wegklicken auf den Hintergrund: Menues zu (Fenster nehmen den Fokus selbst)
  wp.addEventListener('pointerdown', () => { for (const w of wins.values()) w.el.classList.remove('active'); });
}
/** Nach Anmeldung (oder wenn die Seite die Startansicht zeigt): Konto setzen, Home-Fenster oeffnen */
export function userChanged(user, message) {
  const av = $('#dkUser .dk-avatar', root); if (av) av.textContent = (user || '?').slice(0, 1);
  if (!wins.has('home') && !wins.size) openHome();
  notify(message);
}
export function connectionsChanged() { if (root) renderHome_ifOpen(); }
/** Abgemeldet oder Anmeldung noetig: alle Fenster zu */
export function reset() {
  if (!root) return;
  closeMenu(); closeLauncher();
  for (const w of [...wins.values()]) { w.opts.onClose = null; closeWin(w); }
  sessWins.clear();
  nav.view = 'start'; nav.back = []; nav.fwd = [];
}
