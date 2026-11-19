import { ico, setTheme, setLanguage } from './app.js';
import { Sessions } from './sessions.js';
import { SCHEMES, normalizeScheme } from './scheme.js';
import { t, tp, tk, fmtNumber, fmtDate, fmtTime, fmtDay, getPref, LANG_CHOICES } from './i18n.js';
import { looksLikeAddress } from './quick.js';
import { favouriteList, recentList, filterByFolder, groupByFolder, folderIconSvg, folderName, setFavorite } from './folders.js';
import { connectionState, dotClass as hostDot, stateText, serviceState, hostDetail, agentSummary, servicesSummary } from './hosts.js';

/* =====================================================================
 * Handy-Oberflaeche im Stil von Material 3 (Expressive): Home, Dienste, Server, Monitoring, Profil.
 * shell.js laedt diese Datei nur auf schmalen oder Touch-Bildschirmen (der Desktop nimmt desktop.js) und meldet Ereignisse
 * (Anmeldung, Verbindungen, Sitzungsende). Die Fernsteuerung (app.js) bleibt vollbild und unveraendert: beim Verbinden blendet shell.js
 * diese Oberflaeche aus (setShown), beim Verlassen wieder ein.
 *
 * Echte Daten: Name, gespeicherte Verbindungen, Geraete und Sitzungen, Auslastung nur wenn /api/system/overview antwortet.
 * Auf dem Server (je Benutzer, siehe docs/FOLDERS.md): Ordner, Favoriten, Nutzung, zuletzt benutzt. Nur auf diesem Geraet gemerkt (localStorage, "pd.m.*"): das Protokoll fuer "Aktivitaeten".
 * Platzhalter (klar beschriftet): die Seiten Server (ohne Auslastungs-API) und Monitoring.
 * ===================================================================== */

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const ls = {
  get(k, d) { try { const v = localStorage.getItem('pd.m.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('pd.m.' + k, JSON.stringify(v)); } catch { /* egal */ } },
};
const readStore = (k, d) => { try { const v = localStorage.getItem('pd.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } };

/* ---------- Symbole: die vorhandenen aus app.js, dazu die wenigen neuen (Material, 24 px, gefuellt) ---------- */
const X = {
  home: 'M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z',
  dns: 'M20 13H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1v-6c0-.55-.45-1-1-1zM7 19c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM20 3H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1V4c0-.55-.45-1-1-1zM7 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z',
  chart: 'M3.5 18.49l6-6.01 4 4L22 6.92l-1.41-1.41-7.09 7.97-4-4L2 16.99z',
  person: 'M12 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z',
  search: 'M15.5 14h-.79l-.28-.27A6.471 6.471 0 0016 9.5 6.5 6.5 0 109.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z',
  star: 'M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z',
  star_o: 'M22 9.24l-7.19-.62L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21 12 17.27 18.18 21l-1.63-7.03L22 9.24zM12 15.4l-3.76 2.27 1-4.28-3.32-2.88 4.38-.38L12 6.1l1.71 4.04 4.38.38-3.32 2.88 1 4.28L12 15.4z',
  history: 'M13 3c-4.97 0-9 4.03-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42C8.27 19.99 10.51 21 13 21c4.97 0 9-4.03 9-9s-4.03-9-9-9zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z',
  check: 'M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z',
  check_circle: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z',
  phone: 'M17 1.01L7 1c-1.1 0-2 .9-2 2v18c0 1.1.9 2 2 2h10c1.1 0 2-.9 2-2V3c0-1.1-.9-1.99-2-1.99zM17 19H7V5h10v14z',
  shield: 'M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm0 10.99h7c-.53 4.12-3.28 7.79-7 8.94V12H5V6.3l7-3.11v8.8z',
  play: 'M8 5v14l11-7z',
  cpu: 'M15 9H9v6h6V9zm-2 4h-2v-2h2v2zm8-2V9h-2V7c0-1.1-.9-2-2-2h-2V3h-2v2h-2V3H9v2H7c-1.1 0-2 .9-2 2v2H3v2h2v2H3v2h2v2c0 1.1.9 2 2 2h2v2h2v-2h2v2h2v-2h2c1.1 0 2-.9 2-2v-2h2v-2h-2v-2h2zm-4 6H7V7h10v10z',
  disk: 'M2 20h20v-4H2v4zm2-3h2v2H4v-2zM2 4v4h20V4H2zm4 3H4V5h2v2zm-4 7h20v-4H2v4zm2-3h2v2H4v-2z',
  mem: 'M2 6h20v12H2V6zm2 2v8h16V8H4zm2 2h2v4H6v-4zm4 0h2v4h-2v-4zm4 0h2v4h-2v-4z',
  lock: 'M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM9 8V6c0-1.66 1.34-3 3-3s3 1.34 3 3v2H9z',
  info: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z',
  construction: 'M13.78 15.3l6 6 2.11-2.16-6-6zM17.5 10c1.93 0 3.5-1.57 3.5-3.5 0-.58-.16-1.12-.41-1.6l-2.7 2.7-1.49-1.49 2.7-2.7c-.48-.25-1.02-.41-1.6-.41C15.57 3 14 4.57 14 6.5c0 .41.08.8.21 1.16l-1.85 1.85-1.78-1.78.71-.71-1.41-1.41L12 3.49c-1.17-1.17-3.07-1.17-4.24 0L4.22 7.03l1.41 1.41H2.81l-.71.71 3.54 3.54.71-.71V9.15l1.41 1.41.71-.71 1.78 1.78-7.41 7.41 2.12 2.12L16.34 11.8c.36.13.75.2 1.16.2z',
};
const icon = (n) => (X[n] ? `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${X[n]}"/></svg>` : ico(n));
const kindIcon = (k) => icon(k === 'terminal' ? 'terminal' : 'desktop');
const LOGO = `<svg viewBox="0 0 32 32" aria-hidden="true"><defs><linearGradient id="mlg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="var(--m3-primary)"/><stop offset="1" stop-color="var(--m3-tertiary)"/></linearGradient></defs><rect width="32" height="32" rx="10" fill="url(#mlg)"/><path d="M12 9.5v13l11-6.5z" fill="var(--m3-on-primary)"/></svg>`;
// Zierbild im Kopf der Startseite: Berg und See in den Toenen des Themas (keine Bilddatei)
const ART = `<svg class="art" viewBox="0 0 400 96" preserveAspectRatio="xMidYMax slice" aria-hidden="true">
  <path d="M0 96V70l60-18 50 14 70-40 36-12 50 34 40-10 94 40v18z" fill="var(--m3-primary-container)" opacity=".95"/>
  <path d="M150 38l30-12 22 14-14-3-10 7-8-6-12 6z" fill="var(--m3-surface-lowest)" opacity=".9"/>
  <path d="M0 96V80l70-14 64 12 90-18 70 14 106-6v28z" fill="var(--m3-secondary-container)" opacity=".95"/>
  <path d="M0 96V88c60-8 120-6 200-2s140 0 200-6v16z" fill="var(--m3-tertiary-container)" opacity=".8"/></svg>`;

const DEST = [
  { id: 'home', label: tk('mobile.home'), ic: 'home' },
  { id: 'services', label: tk('mobile.services'), ic: 'apps', title: tk('mobile.services') },
  { id: 'server', label: tk('mobile.server'), ic: 'dns', title: tk('mobile.server') },
  { id: 'monitoring', label: tk('mobile.monitoring'), ic: 'chart', title: tk('mobile.monitoring') },
  { id: 'profile', label: tk('mobile.profile'), ic: 'person', title: tk('mobile.profile') },
];
const SWATCHES = [['#9a67ea', tk('mobile.colorViolet')], ['#4f86f7', tk('mobile.colorBlue')], ['#00a67e', tk('mobile.colorGreen')], ['#0aa5b8', tk('mobile.colorTeal')], ['#e5599a', tk('mobile.colorPink')], ['#e5484d', tk('mobile.colorRed')], ['#f5a524', tk('mobile.colorOrange')]];
const SCHEME_LABEL = { light: tk('mobile.schemeLight'), dark: tk('mobile.schemeDark'), auto: tk('mobile.schemeAuto') };
const kindText = (k) => (k === 'terminal' ? t('shell.kindTerminal') : t('shell.kindDesktop'));
const DEFAULT_SEED = '#9a67ea';

let ctx = null, root = null, ready = null;
let current = 'home', shown = false;
let user = '';
let overview = null, overviewOk = null, overviewAt = 0, overviewTimer = 0;   // overviewOk: null = unbekannt, true/false
const view = { q: '', kind: 'all', host: '', folder: 'all', allActs: false };
let snackTimer = 0;

const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const conns = () => (ctx ? ctx.state.connections : []);
const folders = () => (ctx ? ctx.state.folders || [] : []);
const hosts = () => (ctx ? ctx.state.hosts : []);
const byId = (id) => conns().find((c) => c.id === id);
const showPlaceholders = () => ls.get('planned', true);

/* ---------- Start ---------- */
export function init(c) {
  ctx = c;
  ready = (async () => {
    await Promise.all(['m3.css', 'mobile.css'].map((href) => new Promise((res) => {
      const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = href; l.onload = l.onerror = res; document.head.appendChild(l);
    })));
    document.documentElement.classList.add('m3');
    build();
    applyShellTheme();
    enhanceSheets();
  })();
  return ready;
}

function build() {
  root = $('#mobile');
  root.innerHTML = `
    <header class="m3-bar" id="mBar">
      <div class="logo"><span class="m3-ic" style="width:30px;height:30px;display:block">${LOGO}</span><span class="bt" id="mBarName">JulOS</span><span class="bt sub" id="mBarTitle" aria-hidden="true"></span></div>
      <button type="button" class="mb m3-iconbtn m3-state" id="mSearchBtn" aria-label="${esc(t('mobile.searchServices'))}">${icon('search')}</button>
      <button type="button" class="mb avatar m3-state" id="mAvatar" aria-label="${esc(t('mobile.profile'))}"></button>
    </header>
    <div class="m3-stage" id="mStage">
      <section class="m3-page" id="pg-home" data-page="home" aria-label="${esc(t('mobile.home'))}"></section>
      <section class="m3-page" id="pg-services" data-page="services" aria-label="${esc(t('mobile.services'))}" hidden></section>
      <section class="m3-page" id="pg-server" data-page="server" aria-label="${esc(t('mobile.server'))}" hidden></section>
      <section class="m3-page" id="pg-monitoring" data-page="monitoring" aria-label="${esc(t('mobile.monitoring'))}" hidden></section>
      <section class="m3-page" id="pg-profile" data-page="profile" aria-label="${esc(t('mobile.profile'))}" hidden></section>
    </div>
    <button type="button" class="mb m3-fab m3-state" id="mFab" aria-label="${esc(t('mobile.addConnection'))}">${icon('add')}<span class="fl">${esc(t('common.add'))}</span></button>
    <nav class="m3-nav" id="mNav" aria-label="${esc(t('mobile.mainNav'))}"></nav>
    <div class="m3-snack" id="mSnack" role="status" aria-live="polite"><span class="st" id="mSnackText"></span><button type="button" class="mb m3-state" id="mSnackAct" hidden></button></div>`;
  root.hidden = true;

  buildServicesPage();
  renderNav();
  $('#mNav').addEventListener('click', (e) => { const b = e.target.closest('[data-go]'); if (b) go(b.dataset.go); });
  $('#mNav').addEventListener('keydown', (e) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    const bs = $$('#mNav .mb'), i = bs.indexOf(document.activeElement);
    if (i < 0) return;
    bs[(i + (e.key === 'ArrowRight' ? 1 : bs.length - 1)) % bs.length].focus(); e.preventDefault();
  });
  $('#mSearchBtn').addEventListener('click', () => go('services', { focusSearch: true }));
  $('#mAvatar').addEventListener('click', () => go('profile'));
  $('#mFab').addEventListener('click', () => ctx.add());
  for (const pg of $$('.m3-page')) {
    let last = 0;
    pg.addEventListener('scroll', () => {
      const top = pg.scrollTop;
      const head = pg.querySelector('[data-head]');
      $('#mBar').classList.toggle('scrolled', top > 4);
      $('#mBar').classList.toggle('titled', !!head && top > head.offsetTop + head.offsetHeight - 8);
      if (top > last + 6 && top > 48) $('#mFab').classList.add('small'); else if (top < last - 6 || top < 24) $('#mFab').classList.remove('small');
      last = top;
    }, { passive: true });
  }
  // Delegation fuer alles, was in den Seiten gezeichnet wird
  root.addEventListener('click', onClick);
  root.addEventListener('contextmenu', (e) => { const card = e.target.closest('[data-conn]'); if (card) { e.preventDefault(); actionSheet(byId(card.dataset.conn)); } });
  window.addEventListener('popstate', (e) => { if (shown) go((e.state && e.state.m3) || 'home', { push: false }); });
  history.replaceState({ m3: 'home' }, '');
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => queueMicrotask(applyShellTheme));
  document.addEventListener('visibilitychange', () => { if (!document.hidden) pollOverview(); });
}

function renderNav() {
  const items = DEST.filter((d) => showPlaceholders() || (d.id !== 'server' && d.id !== 'monitoring') || (d.id === 'server' && (overviewOk || hosts().length)));
  $('#mNav').innerHTML = items.map((d) => `<button type="button" class="mb m3-state" data-go="${d.id}" ${d.id === current ? 'aria-current="page"' : ''}><span class="pill">${icon(d.ic)}</span><span>${esc(t(d.label))}</span></button>`).join('');
}

/* ---------- Sichtbarkeit (von shell.js): Fernsteuerung an, Oberflaeche aus und umgekehrt ---------- */
export function setShown(on) {
  if (!root || on === shown) return;
  shown = on;
  root.classList.remove('leaving', 'entering');
  if (on) {
    root.hidden = false; root.classList.add('entering');
    root.addEventListener('animationend', () => root.classList.remove('entering'), { once: true });
    renderAll(); pollOverview();
  } else {
    // Ausblenden ueber der Fernsteuerung, damit deren Layout sofort stimmt (sie misst ihre Bildflaeche beim Verbinden)
    root.classList.add('leaving'); root.style.position = 'absolute'; root.style.inset = '0'; root.style.zIndex = '60'; root.style.pointerEvents = 'none';
    setTimeout(() => { if (!shown) { root.hidden = true; root.classList.remove('leaving'); root.removeAttribute('style'); } }, 170);
    clearTimeout(overviewTimer);
  }
}
export function reset() { mods = { caddy: false, ddns: false }; modsLoaded = false; overview = null; overviewOk = null; user = ''; current = 'home'; view.q = ''; view.kind = 'all'; setShown(false); if (root) { root.hidden = true; root.removeAttribute('style'); } }

export function userChanged(name, message) {
  user = name;
  if (!root) return;
  $('#mAvatar').textContent = (name || '?').trim().slice(0, 1);
  if (message) snack(message);
  renderAll();
}
export function connectionsChanged() { if (root) renderAll(); }
/** Neue Zustaende der Hosts (alle 30 s): die Seite neu zeichnen, die sie zeigt */
export function hostsChanged() { if (root) renderAll(); }

/** Merkt sich (nur auf diesem Geraet), was gestartet wurde, fuer "Aktivitaeten"; Haeufigkeit und letzte Benutzung zaehlt der Server (shell.js). */
export function started(c) {
  const log = ls.get('log', []); log.unshift({ t: Date.now(), id: c.id, name: c.name, kind: c.kind, type: 'start' }); ls.set('log', log.slice(0, 40));
}
export function ended(message, reason) {
  if (!message || reason === 'auth') return;
  const log = ls.get('log', []); const last = log.find((l) => l.type === 'start');
  log.unshift({ t: Date.now(), id: last ? last.id : null, name: last ? last.name : '', kind: last ? last.kind : 'desktop', type: 'end', msg: message }); ls.set('log', log.slice(0, 40));
}

/* ---------- Navigation: Fade-through, Verlauf fuer die Zurueck-Geste ---------- */
export function go(id, { push = true, focusSearch = false } = {}) {
  if (!DEST.some((d) => d.id === id)) id = 'home';
  const from = current;
  if (id !== from) {
    if (push) { if (from === 'home') history.pushState({ m3: id }, ''); else if (id === 'home') { if (history.state && history.state.m3 && history.state.m3 !== 'home') { history.back(); return; } } else history.replaceState({ m3: id }, ''); }
    const a = $('#pg-' + from), b = $('#pg-' + id);
    current = id;
    $$('#mNav .mb').forEach((n) => { if (n.dataset.go === id) n.setAttribute('aria-current', 'page'); else n.removeAttribute('aria-current'); });
    a.classList.remove('in'); a.classList.add('out');
    setTimeout(() => { a.hidden = true; a.classList.remove('out'); }, 90);
    setTimeout(() => {
      b.hidden = false; b.classList.add('in'); b.addEventListener('animationend', () => b.classList.remove('in'), { once: true });
      b.dispatchEvent(new Event('scroll'));
    }, 90);
    renderPage(id);
    $('#mFab').classList.toggle('gone', !(id === 'home' || id === 'services'));
    $('#mFab').classList.remove('small');
    const d = DEST.find((x) => x.id === id);
    $('#mBar').classList.toggle('home', id === 'home');
    $('#mBarName').hidden = id !== 'home'; $('#mBarTitle').textContent = d.title ? t(d.title) : ''; $('#mBarTitle').hidden = id === 'home';
    $('#mBar').classList.remove('titled', 'scrolled');
    if (id === 'server' || id === 'home') pollOverview();
    if (id === 'profile') loadSessions();
  }
  if (focusSearch) setTimeout(() => $('#mSearch').focus(), 330);
}

function renderAll() { if (!root) return; renderNav(); renderPage(current); $('#mFab').classList.toggle('gone', !(current === 'home' || current === 'services')); }
function renderPage(id) {
  if (id === 'home') renderHome(); else if (id === 'services') renderServices(); else if (id === 'server') renderServer();
  else if (id === 'monitoring') renderMonitoring(); else if (id === 'profile') renderProfile();
  // Seitentitel in der Leiste: auf Home bleibt "JulOS", sonst der Titel der Seite
  $('#mBar').classList.toggle('home', current === 'home');
}

/* =====================================================================
 * Auslastung des Servers: nur wenn /api/system/overview existiert (Adapter fuer dessen JSON)
 * ===================================================================== */
const num = (v) => (typeof v === 'number' && isFinite(v) ? v : null);
function adaptOverview(o) {
  if (!o || typeof o !== 'object') return null;
  const ratio = (used, total) => (num(used) !== null && num(total) > 0 ? Math.round((used / total) * 1000) / 10 : null);
  const cpu = num(o.cpuPercent ?? o.cpu?.percent), ram = ratio(o.memoryUsed ?? o.memory?.used, o.memoryTotal ?? o.memory?.total) ?? num(o.memory?.percent);
  const disk = ratio(o.diskUsed ?? o.disk?.used, o.diskTotal ?? o.disk?.total) ?? num(o.disk?.percent);
  if (cpu === null && ram === null && disk === null) return null;
  return { cpu, ram, disk, cores: num(o.cores ?? o.cpu?.cores), name: o.name || o.host?.name || o.hostname || null, os: o.os || o.host?.os || null,
    memTotal: num(o.memoryTotal ?? o.memory?.total), memUsed: num(o.memoryUsed ?? o.memory?.used), diskTotal: num(o.diskTotal ?? o.disk?.total), diskUsed: num(o.diskUsed ?? o.disk?.used),
    rx: num(o.rxBytesPerSecond), tx: num(o.txBytesPerSecond) };
}
async function pollOverview() {
  clearTimeout(overviewTimer);
  if (!shown || document.hidden || !ctx || (overviewOk === false && Date.now() - overviewAt < 300000)) return;
  if (current !== 'home' && current !== 'server') { overviewTimer = setTimeout(pollOverview, 15000); return; }
  try {
    const o = adaptOverview(await ctx.api('/api/system/overview'));
    overviewOk = !!o; overview = o;
  } catch (e) { overviewOk = e.status === 401 ? overviewOk : false; overview = null; }
  overviewAt = Date.now();
  if (shown) { renderNav(); if (current === 'home' || current === 'server') renderPage(current); }
  if (overviewOk) overviewTimer = setTimeout(pollOverview, 15000);
}
const fmtBytes = (b) => (b >= 1e12 ? fmtNumber(b / 1e12, 1) + ' TB' : b >= 1e9 ? fmtNumber(b / 1e9, 1) + ' GB' : b >= 1e6 ? fmtNumber(b / 1e6) + ' MB' : fmtNumber(b / 1e3) + ' kB');
const pct = (v) => (v === null ? '–' : Math.round(v) + ' %');
function metersHtml(o) {
  const m = (ic, label, v, extra = '') => `<div class="meter${v !== null && v >= 90 ? ' warn' : ''}"><div class="ml">${icon(ic)}${label}</div><div class="mv">${pct(v)}${extra}</div><div class="bar" role="progressbar" aria-label="${label}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${v === null ? '' : Math.round(v)}"><i style="width:${v === null ? 0 : Math.max(0, Math.min(100, v))}%"></i></div></div>`;
  return `<div class="meters">${m('cpu', 'CPU', o.cpu)}${m('mem', 'RAM', o.ram)}${m('disk', t('mobile.storage'), o.disk)}</div>`;
}
const serverTitle = (o) => o.name || t('mobile.server');
const serverSub = (o) => [o.os, o.cores ? t('mobile.cores', { n: o.cores }) : '', o.memTotal ? fmtBytes(o.memTotal) + ' RAM' : ''].filter(Boolean).join(' · ') || t('mobile.thisServer');

/* =====================================================================
 * Home
 * ===================================================================== */
function greeting() { const h = new Date().getHours(); return h < 5 ? t('mobile.greetNight') : h < 11 ? t('mobile.greetMorning') : h < 17 ? t('mobile.greetDay') : h < 22 ? t('mobile.greetEvening') : t('mobile.greetNight'); }
function whenLabel(ts) {
  const d = new Date(ts), n = new Date();
  const day = Math.round((new Date(n.getFullYear(), n.getMonth(), n.getDate()) - new Date(d.getFullYear(), d.getMonth(), d.getDate())) / 864e5);
  const time = fmtTime(d);
  return day === 0 ? t('mobile.today', { time }) : day === 1 ? t('mobile.yesterday', { time }) : t('mobile.dayTime', { date: fmtDay(d, { day: '2-digit', month: '2-digit' }), time });
}
const favourites = () => favouriteList(conns(), 6);
const openCount = (c) => Sessions.find(c.id).length;
const tileHtml = (c, pinned) => `<button type="button" class="mb tile m3-state ${c.kind}${openCount(c) ? ' live' : ''}" data-conn="${c.id}" data-act="start" aria-label="${esc(t('mobile.tileAria', { name: c.name, kind: kindText(c.kind), state: stateText(connectionState(hosts(), c)), action: openCount(c) ? t('mobile.tileOpen', { n: openCount(c) }) : t('mobile.tileTap') }))}">
  <span class="hdot ${hostDot(connectionState(hosts(), c))}" title="${stateText(connectionState(hosts(), c))}"></span>${openCount(c) ? `<span class="liveb" title="${esc(t('mobile.sessionOpen'))}">${openCount(c)}</span>` : ''}${pinned ? `<span class="fav" title="${esc(t('mobile.favourite'))}">${icon('star')}</span>` : ''}${c.hasPassword ? `<span class="badge" title="${esc(t('shell.passwordSaved'))}">${icon('key')}</span>` : ''}
  <span class="ti">${kindIcon(c.kind)}</span><span class="tn">${esc(c.name)}</span></button>`;

function activityRows(limit) {
  const log = ls.get('log', []).filter((l) => l.type !== 'start' || byId(l.id)).slice(0, limit);
  return log.map((l) => {
    const c = byId(l.id);
    const lead = l.type === 'end' ? `<span class="lead warn">${icon('info')}</span>` : `<span class="lead ok">${icon('check_circle')}</span>`;
    const sub = l.type === 'end' ? esc(l.msg) : esc(t('mobile.started', { kind: kindText(l.kind) }));
    return `<div class="m3-li static">${lead}<span class="txt"><span class="hl">${esc(c ? c.name : l.name || t('mobile.connection'))}</span><span class="sl">${sub}</span></span><span class="tr">${esc(whenLabel(l.t))}</span></div>`;
  }).join('');
}

/* Offene Sitzungen: Karten mit Zustandspunkt, ein Tipp wechselt hin, das Kreuz schliesst */
const dotClass = (st) => (st === 'connected' ? 'ok' : st === 'connecting' || st === 'reconnecting' ? 'busy' : 'bad');
const sessionRows = () => Sessions.list().map((s) => `<div class="m3-li sess ${s === Sessions.active ? 'act' : ''}" data-act="sess" data-sid="${s.id}" role="button" tabindex="0" aria-label="${esc(t('mobile.sessAria', { title: s.title, state: s.stateText }))}">
    <span class="lead">${kindIcon(s.kind)}</span><span class="txt"><span class="hl">${esc(s.title)}</span><span class="sl"><span class="m3-dot sdot ${dotClass(s.state)}"></span>${esc(s.stateText)} · ${esc(s.conn.name === s.title ? kindText(s.kind) : s.conn.name)}</span></span>
    <button type="button" class="mb sx m3-state" data-act="sessclose" data-sid="${s.id}" aria-label="${esc(t('mobile.sessClose', { title: s.title }))}">${icon('close')}</button></div>`).join('');
function sessionsSection() {
  if (!Sessions.count) return '';
  return `<div class="m3-sec">${icon('layers')}<span>${esc(t('mobile.openSessions'))}</span></div><div class="m3-card acts sesslist">${sessionRows()}</div>`;
}

function renderHome() {
  const pg = $('#pg-home'), top = pg.scrollTop;
  const list = conns(), favs = favourites();
  let chip;
  if (overviewOk && overview) {
    const hot = (overview.cpu ?? 0) >= 90 || (overview.ram ?? 0) >= 90 || (overview.disk ?? 0) >= 95;
    chip = `<button type="button" class="mb m3-chip status m3-state" data-go="server"><span class="m3-dot ${hot ? '' : 'ok'}" style="${hot ? 'background:var(--m3-error)' : ''}"></span><span>${hot ? t('mobile.serverBusy') : t('mobile.serverStable')}</span>${icon('chevron_right')}</button>`;
  } else if (!list.length) {
    chip = `<button type="button" class="mb m3-chip status m3-state" data-act="add"><span class="m3-dot"></span><span>${t('mobile.noConnectionAdd')}</span>${icon('chevron_right')}</button>`;
  } else {
    chip = `<button type="button" class="mb m3-chip status m3-state" data-go="services"><span class="m3-dot"></span><span>${tp('mobile.connsSaved', list.length)}</span>${icon('chevron_right')}</button>`;
  }
  const server = overviewOk && overview ? `<button type="button" class="mb m3-card srv m3-state" data-go="server" aria-label="${esc(t('mobile.serverDetails'))}">
      <div class="top"><span class="lead">${icon('dns')}</span><span class="t">${esc(serverTitle(overview))}<small>${esc(serverSub(overview))}</small></span>${icon('chevron_right')}</div>${metersHtml(overview)}</button>` : '';
  const favHtml = favs.length
    ? `<div class="grid3">${favs.map((c) => tileHtml(c, c.favorite)).join('')}</div>`
    : `<div class="m3-card empty-card"><span class="lead">${icon('star_o')}</span><span>${list.length ? t('mobile.favHintSome') : t('mobile.favHintNone')}</span></div>`;
  const actsAll = ls.get('log', []).filter((l) => l.type !== 'start' || byId(l.id));
  const acts = actsAll.length
    ? `<div class="m3-card acts">${activityRows(view.allActs ? 15 : 4)}</div><p class="note">${t('mobile.actsNote')}</p>`
    : '<div class="m3-card empty-card"><span class="lead">' + icon('history') + '</span><span>' + t('mobile.actsEmpty') + '</span></div>';
  pg.innerHTML = `
    <div class="hero">${ART}
      <div class="greet" data-head>${greeting()}, ${esc(user)}</div>
      <h1>${t('mobile.welcome')}</h1>
      ${chip}
    </div>
    ${sessionsSection()}
    ${server}
    <div class="m3-sec">${icon('star_o')}<span>${t('mobile.myFavourites')}</span><span class="sp"></span><button type="button" class="mb m3-btn text m3-state" data-go="services">${t('mobile.allServices')}${icon('chevron_right')}</button></div>
    ${favHtml}
    <div class="m3-sec">${icon('history')}<span>${t('mobile.activities')}</span><span class="sp"></span>${actsAll.length > 4 ? `<button type="button" class="mb m3-btn text m3-state" data-act="allacts">${view.allActs ? t('mobile.showLess') : t('mobile.showAll')}${icon('chevron_right')}</button>` : ''}</div>
    ${acts}`;
  pg.scrollTop = top;
}

/* =====================================================================
 * Dienste
 * ===================================================================== */
function buildServicesPage() {
  $('#pg-services').innerHTML = `
    <div class="m3-h" data-head><h1>${t('mobile.services')}</h1><p>${t('mobile.servicesSub')}</p></div>
    <label class="m3-search"><span class="m3-ic" aria-hidden="true">${icon('search')}</span><input id="mSearch" type="search" placeholder="${esc(t('mobile.searchPlaceholder'))}" autocomplete="off" autocapitalize="none" spellcheck="false" enterkeyhint="search" aria-label="${esc(t('mobile.searchServices'))}"></label>
    <div class="chips" role="group" aria-label="${esc(t('mobile.filter'))}" id="mChips"></div>
    <div class="chips" role="group" aria-label="${esc(t('mobile.folders'))}" id="mFolderChips" hidden></div>
    <div class="chips" role="group" aria-label="${esc(t('mobile.chooseHost'))}" id="mHostChips" hidden></div>
    <div id="mGrid"></div>
    <div id="mMods"></div>
    <div class="m3-sec">${icon('bolt')}<span>${t('mobile.quickActions')}</span></div>
    <div class="quick" id="mQuick"></div>`;
  $('#mSearch').addEventListener('input', (e) => { view.q = e.target.value; renderGrid(); });
  $('#mChips').addEventListener('click', (e) => { const b = e.target.closest('[data-kind]'); if (b) { view.kind = b.dataset.kind; renderServices(); } });
  $('#mFolderChips').addEventListener('click', (e) => { const b = e.target.closest('[data-folder]'); if (b) { view.folder = b.dataset.folder; renderServices(); } });
  $('#mHostChips').addEventListener('click', (e) => { const b = e.target.closest('[data-host]'); if (b) { view.host = b.dataset.host; renderServices(); } });
}
function filtered() {
  const q = view.q.trim().toLowerCase();
  const list = view.folder === 'recent' ? recentList(conns(), 12) : filterByFolder(conns(), view.folder, folders());
  return list.filter((c) => (view.kind === 'all' || c.kind === view.kind) && (!view.host || c.hostId === view.host) && (!q || `${c.name} ${c.host} ${c.username}`.toLowerCase().includes(q)));
}
function renderServices() {
  if (!$('#mChips')) return;
  const cnt = (k) => conns().filter((c) => k === 'all' || c.kind === k).length;
  $('#mChips').innerHTML = [['all', tk('mobile.all'), 'apps'], ['desktop', tk('shell.kindDesktop'), 'desktop'], ['terminal', tk('shell.kindTerminal'), 'terminal']].map(([k, l, ic]) =>
    `<button type="button" class="mb m3-chip m3-state" data-kind="${k}" aria-pressed="${view.kind === k}">${view.kind === k ? icon('check') : icon(ic)}${esc(t(l))} <span style="opacity:.7">${cnt(k)}</span></button>`).join('');
  // Ordner, Favoriten und zuletzt benutzt (vom Server): die Auswahl gibt es, sobald es einen Ordner, einen Favoriten oder eine Benutzung gibt
  const known = new Set(['all', 'fav', 'recent', 'none', ...folders().map((f) => f.id)]);
  if (!known.has(view.folder)) view.folder = 'all';
  const fc = $('#mFolderChips');
  fc.hidden = !(folders().length || conns().some((c) => c.favorite || c.lastUsedAt));
  const folderChip = (id, label, ic) => `<button type="button" class="mb m3-chip m3-state" data-folder="${esc(id)}" aria-pressed="${view.folder === id}">${view.folder === id ? icon('check') : ic}${esc(label)}</button>`;
  fc.innerHTML = fc.hidden ? '' : [folderChip('all', t('mobile.allFolders'), icon('apps')), folderChip('fav', t('mobile.favourites'), icon('star_o')), folderChip('recent', t('mobile.recentlyUsed'), icon('history')),
    ...folders().map((f) => folderChip(f.id, f.name, folderIconSvg(f.icon))), ...(folders().length ? [folderChip('none', t('mobile.noFolder'), icon('close'))] : [])].join('');
  // Die Auswahl des Hosts gibt es erst, wenn es mehr als einen gibt
  if (view.host && !hosts().some((h) => h.id === view.host)) view.host = '';
  const hc = $('#mHostChips');
  hc.hidden = hosts().length < 2;
  hc.innerHTML = hc.hidden ? '' : [{ id: '', name: t('mobile.allHosts') }, ...hosts()].map((h) => `<button type="button" class="mb m3-chip m3-state" data-host="${esc(h.id)}" aria-pressed="${view.host === h.id}">${view.host === h.id ? icon('check') : h.id ? `<span class="m3-dot hdot ${hostDot(h.online)}"></span>` : icon('dns')}${esc(h.name)}</button>`).join('');
  renderGrid(); renderModules();
  const last = lastConnection();
  $('#mQuick').innerHTML = `
    <button type="button" class="mb m3-btn filled m3-state" data-act="add">${icon('add')}${t('shell.newConnection')}</button>
    <button type="button" class="mb m3-btn tonal m3-state" data-act="last" ${last ? '' : 'disabled'} ${last ? `aria-label="${esc(t('mobile.lastOpenedName', { name: last.name }))}"` : ''}>${icon('history')}${last ? esc(t('mobile.lastName', { name: last.name.length > 14 ? last.name.slice(0, 13) + '…' : last.name })) : t('mobile.lastOpened')}</button>
    <button type="button" class="mb m3-btn tonal m3-state" data-act="ssh">${icon('terminal')}${t('mobile.openSsh')}</button>
    <button type="button" class="mb m3-btn tonal m3-state" data-act="quick">${icon('bolt')}${t('mobile.quickConnect')}</button>
    <button type="button" class="mb m3-btn tonal m3-state" data-act="folders">${folderIconSvg('folder')}${t('mobile.folders')}</button>`;
}
/* Module Caddy und DDNS (nur lesen): Karten nur, wenn der Server sie eingeschaltet hat (GET /api/modules); Details im Sheet, siehe mobile-modules.js */
let mods = { caddy: false, ddns: false }, modsLoaded = false;
async function renderModules() {
  const box = $('#mMods'); if (!box) return;
  if (!modsLoaded) { modsLoaded = true; mods = await import('./modules.js').then((m) => m.loadModules(ctx.api)); }
  if (mods.caddy || mods.ddns) (await import('./mobile-modules.js')).renderCards(box, mods, { icon, esc, api: ctx.api }); else box.replaceChildren();
}
function renderGrid() {
  const list = filtered();
  const grouped = view.folder === 'all' && !view.q.trim() && folders().length;     // mit Ordnern: unter ihren Ueberschriften
  $('#mGrid').innerHTML = grouped && list.length
    ? groupByFolder(folders(), list).map((g) => `<div class="m3-sec">${g.folder ? folderIconSvg(g.folder.icon) : icon('apps')}<span>${esc(g.folder ? g.folder.name : t('mobile.noFolder'))}</span></div><div class="grid4">${g.connections.map((c) => tileHtml(c, c.favorite)).join('')}</div>`).join('')
    : list.length
    ? `<div class="grid4">${list.map((c) => tileHtml(c, c.favorite)).join('')}</div>`
    : `<p class="empty-hint">${conns().length ? t('mobile.noMatch') : t('mobile.noConnectionYet')}</p>`;
  // Sieht die Suche nach einer Adresse aus: Quick Connect ohne Speichern anbieten
  if (looksLikeAddress(view.q)) $('#mGrid').insertAdjacentHTML('beforeend', `<button type="button" class="mb m3-btn tonal block m3-state" data-act="quickq" style="margin-top:12px">${icon('bolt')}${esc(t('mobile.quickTo', { addr: view.q.trim() }))}</button>`);
}
function lastConnection() {
  const r = recentList(conns(), 1)[0];                       // vom Server, auf jedem Geraet gleich
  if (r) return r;
  for (const l of ls.get('log', [])) if (l.type === 'start') { const c = byId(l.id); if (c) return c; }
  return null;
}

/* =====================================================================
 * Server und Monitoring
 * ===================================================================== */
function soonHtml(ic, title, text) {
  return `<div class="m3-h" data-head><h1>${title}</h1></div>
    <div class="m3-card soon"><span class="big">${icon(ic)}</span><span class="m3-chip status"><span class="m3-dot"></span>${t('mobile.comingSoon')}</span><p>${text}</p></div>`;
}
/** Die Hosts mit Zustand, Agent und Diensten (docs/HOSTS.md); der Agent wird je Host eingerichtet */
function hostsHtml() {
  const rows = hosts().map((h) => `<div class="m3-card host" data-host-card="${esc(h.id)}">
      <div class="m3-li static"><span class="lead">${icon('dns')}</span><span class="txt"><span class="hl"><span class="m3-dot hdot ${hostDot(h.online)}"></span>${esc(h.name)} <small>${stateText(h.online)}</small></span><span class="sl">${esc([hostDetail(h), agentSummary(h), servicesSummary(h)].filter(Boolean).join(' · '))}</span></span></div>
      <div class="svcs">${h.services.map((s) => { const c = byId(s.connection); return c ? `<button type="button" class="mb m3-chip m3-state" data-act="start" data-conn="${esc(c.id)}"><span class="m3-dot hdot ${hostDot(serviceState(h, c.id))}"></span>${esc(c.name)}</button>` : ''; }).join('')}</div>
      <div class="acts-row"><button type="button" class="mb m3-btn tonal m3-state" data-act="hostagent" data-host="${esc(h.id)}">${icon('download')}${h.agent ? t('mobile.agentManage') : t('shell.agentSetup')}</button>
        <button type="button" class="mb m3-btn text m3-state" data-act="hostedit" data-host="${esc(h.id)}">${icon('edit')}${t('common.edit')}</button></div></div>`).join('');
  return `<div class="m3-sec">${icon('dns')}<span>${t('mobile.hosts')}</span><span class="sp"></span><button type="button" class="mb m3-btn text m3-state" data-act="addhost">${icon('add')}${t('mobile.addHost')}</button></div>${rows || `<p class="empty-hint">${t('mobile.noHost')}</p>`}`;
}
function renderServer() {
  const pg = $('#pg-server');
  if (overviewOk && overview) {
    const o = overview;
    const net = (o.rx !== null || o.tx !== null) ? `<div class="m3-card acts" style="margin-top:12px"><div class="m3-li static"><span class="lead">${icon('download')}</span><span class="txt"><span class="hl">${t('mobile.network')}</span><span class="sl">${esc(t('mobile.netRates', { rx: o.rx !== null ? fmtBytes(o.rx) + '/s' : '–', tx: o.tx !== null ? fmtBytes(o.tx) + '/s' : '–' }))}</span></span></div></div>` : '';
    pg.innerHTML = `<div class="m3-h" data-head><h1>${t('mobile.server')}</h1><p>${esc(serverSub(o))}</p></div>
      <div class="m3-card srv" style="margin-top:0"><div class="top"><span class="lead">${icon('dns')}</span><span class="t">${esc(serverTitle(o))}<small>${t('mobile.loadRefresh')}</small></span></div>${metersHtml(o)}</div>
      <div class="m3-card acts" style="margin-top:12px">
        <div class="m3-li static"><span class="lead">${icon('mem')}</span><span class="txt"><span class="hl">${t('mobile.memory')}</span><span class="sl">${o.memUsed !== null && o.memTotal !== null ? esc(t('mobile.ofTotal', { used: fmtBytes(o.memUsed), total: fmtBytes(o.memTotal) })) : '–'}</span></span></div>
        <div class="m3-li static"><span class="lead">${icon('disk')}</span><span class="txt"><span class="hl">${t('mobile.storage')}</span><span class="sl">${o.diskUsed !== null && o.diskTotal !== null ? esc(t('mobile.ofTotal', { used: fmtBytes(o.diskUsed), total: fmtBytes(o.diskTotal) })) : '–'}</span></span></div>
      </div>${net}${hostsHtml()}`;
  } else {
    pg.innerHTML = soonHtml('dns', t('mobile.server'), overviewOk === false ? t('mobile.serverSoon') : t('mobile.loadChecking')) + hostsHtml();
  }
}
function renderMonitoring() {
  $('#pg-monitoring').innerHTML = soonHtml('chart', t('mobile.monitoring'), t('mobile.monitoringSoon'));
}

/* =====================================================================
 * Profil: Konto, Darstellung, Verbindungen, Geraete, Abmelden
 * ===================================================================== */
const seg = (id, opts, val) => `<div class="m3-seg" role="radiogroup" id="${id}">${opts.map(([v, l]) => `<button type="button" class="mb m3-state" role="radio" aria-checked="${v === val}" data-v="${v}"><span class="m3-seg-check">${icon('check')}</span>${l}</button>`).join('')}</div>`;
function curSeed() { const s = readStore('seed', DEFAULT_SEED); return /^#[0-9a-f]{6}$/i.test(s) ? s.toLowerCase() : DEFAULT_SEED; }
function renderProfile() {
  const pg = $('#pg-profile'), top = pg.scrollTop, seed = curSeed(), scheme = normalizeScheme(readStore('scheme', 'auto'));
  const planned = showPlaceholders();
  pg.innerHTML = `
    <div class="m3-h" data-head><h1>${t('mobile.profile')}</h1><p>${t('mobile.profileSub')}</p></div>
    <div class="m3-card me"><span class="avatar">${esc((user || '?').trim().slice(0, 1))}</span><span class="t">${esc(user)}<small>${t('mobile.pwAuth')}</small></span></div>
    <button type="button" class="mb m3-btn tonal block m3-state" id="mAccount">${icon('lock')}${t('mobile.pwAuthChange')}</button>

    <div class="grp-t">${t('mobile.appearance')}</div>
    <div class="m3-card grp">
      <div class="setrow"><div class="lab">${t('mobile.brightness')}</div>${seg('mScheme', SCHEMES.map(([v]) => [v, t(SCHEME_LABEL[v])]), scheme)}</div>
      <div class="setrow"><div class="lab">${t('lang.title')}</div>${seg('mLang', LANG_CHOICES.map(([v, l]) => [v, v === 'system' ? t('lang.system') : l]), getPref())}</div>
      <div class="setrow"><div class="lab">${t('mobile.color')}</div><div class="swatches2" role="radiogroup" aria-label="${esc(t('mobile.color'))}" id="mSwatches">${SWATCHES.map(([h, n]) => `<button type="button" class="mb sw" role="radio" aria-checked="${h === seed}" aria-label="${esc(t(n))}" data-seed="${h}" style="background:${h}">${icon('check')}</button>`).join('')}</div></div>
      <div class="setrow line"><div class="lab">${t('mobile.showPlanned')}<small>${t('mobile.showPlannedSub')}</small></div><button type="button" class="mb m3-switch m3-state" role="switch" aria-checked="${planned}" id="mPlanned" aria-label="${esc(t('mobile.showPlanned'))}"></button></div>
    </div>

    <div class="grp-t">${t('mobile.tools')}</div>
    <button type="button" class="mb m3-btn tonal block m3-state" id="mNet">${icon('dns')}${t('mobile.netTools')}</button>

    <div class="grp-t">${t('mobile.aboutJulos')}</div>
    <div class="m3-card grp" id="mAbout"><div class="setrow"><div class="lab">${t('mobile.version')}<small>${t('mobile.loading')}</small></div></div></div>
    <button type="button" class="mb m3-btn danger block big logout m3-state" id="mOut">${icon('logout')}${t('mobile.signOut')}</button>`;
  pg.scrollTop = top;
  $('#mScheme').addEventListener('click', (e) => { const b = e.target.closest('[data-v]'); if (!b) return; setTheme({ scheme: b.dataset.v }); applyShellTheme(); renderProfile(); });
  $('#mLang').addEventListener('click', (e) => { const b = e.target.closest('[data-v]'); if (b && b.dataset.v !== getPref()) setLanguage(b.dataset.v); });
  $('#mSwatches').addEventListener('click', (e) => { const b = e.target.closest('[data-seed]'); if (!b) return; setTheme({ seed: b.dataset.seed }); applyShellTheme(); renderProfile(); });
  $('#mPlanned').addEventListener('click', (e) => { const on = e.currentTarget.getAttribute('aria-checked') !== 'true'; ls.set('planned', on); e.currentTarget.setAttribute('aria-checked', on); renderNav(); });
  $('#mAccount').addEventListener('click', () => ctx.account());
  $('#mNet').addEventListener('click', () => ctx.nettools());
  $('#mOut').addEventListener('click', () => ctx.signOut());
  if (current === 'profile') { loadSessions(); loadAbout(); }
}
const backupText = (b) => !b ? '' : b.state === 'ok' ? `${fmtDate(b.lastAt)}, ${b.bytes < 1048576 ? Math.max(1, Math.round(b.bytes / 1024)) + ' KB' : fmtNumber(b.bytes / 1048576, 1) + ' MB'}`
  : b.state === 'off' ? t('mobile.backupOff') : b.state === 'pending' ? t('mobile.backupPending', { time: b.time }) : b.state === 'failed' ? t('mobile.backupFailed', { message: b.message || '' }) : t('mobile.backupNotSetUp', { message: b.message || '' });
async function loadAbout() {
  const box = $('#mAbout'); if (!box || !ctx) return;
  try {
    const a = await ctx.api('/api/about');
    const built = a.built ? fmtDate(a.built) : '';
    const row = (k, v, extra = '') => `<div class="setrow line"><div class="lab">${k}<small>${esc(v)}</small></div>${extra}</div>`;
    box.innerHTML = row(t('mobile.version'), `JulOS ${a.version}`) + (a.commit || built ? row(t('mobile.build'), [a.commit, built].filter(Boolean).join(' · ')) : '')
      + row(t('mobile.imageTransfer'), a.engines.stream ? t('mobile.streamEngine', { encoder: a.engines.encoder || 'H.264' }) : t('mobile.notAvailable'))
      + row(t('mobile.lastBackup'), backupText(a.backup))
      + `<div style="padding:4px 12px 10px;display:flex;gap:8px"><a class="mb m3-btn tonal block m3-state" href="/licenses.txt" target="_blank" rel="noopener">${t('mobile.licenses')}</a><a class="mb m3-btn tonal block m3-state" href="${esc(a.repository)}" target="_blank" rel="noopener">${t('mobile.sourceCode')}</a></div>`;
  } catch (e) { box.innerHTML = `<p class="empty-hint">${esc(e.message)}</p>`; }
}
async function loadSessions() {
  const box = $('#mSessions'); if (!box || !ctx) return;
  try {
    const { sessions } = await ctx.api('/api/auth/sessions');
    box.innerHTML = sessions.map((s) => `<div class="m3-li static"><span class="lead ${s.current ? 'cur' : ''}">${icon('phone')}</span><span class="txt"><span class="hl">${esc(ctx.describeAgent(s.userAgent))}${s.current ? ' · ' + esc(t('shell.thisDevice')) : ''}</span><span class="sl">${esc(s.ip || '')} · ${esc(t('shell.lastSeen', { when: ctx.ago(s.lastSeen) }))}</span></span></div>`).join('')
      + (sessions.length > 1 ? `<div style="padding:4px 12px 10px"><button type="button" class="mb m3-btn tonal block m3-state" id="mOthers">${t('mobile.signOutOthers')}</button></div>` : '');
    const o = $('#mOthers');
    if (o) o.addEventListener('click', async () => {
      try { const r = await ctx.api('/api/auth/logout-others', { method: 'POST' }); snack(t('shell.sessionsEnded', { n: r.ended })); loadSessions(); } catch (e) { snack(e.message); }
    });
  } catch (e) { box.innerHTML = `<p class="empty-hint">${esc(e.message)}</p>`; }
}

/* =====================================================================
 * Aktionen, Sheets, Snackbar
 * ===================================================================== */
function onClick(e) {
  const g = e.target.closest('[data-go]'); if (g && !g.closest('#mNav')) { go(g.dataset.go); return; }
  const a = e.target.closest('[data-act]'); if (!a) return;
  const act = a.dataset.act;
  if (act === 'sessclose') { e.stopPropagation(); Sessions.close(a.dataset.sid); return; }
  if (act === 'sess') { Sessions.activate(a.dataset.sid, { reveal: true }); return; }
  if (act === 'start') { const c = byId(a.dataset.conn); if (c) ctx.connect(c); }
  else if (act === 'add') ctx.add();
  else if (act === 'folders') ctx.folders();
  else if (act === 'quick') ctx.quick();
  else if (act === 'quickq') ctx.quick(view.q);
  else if (act === 'addhost') ctx.addHost();
  else if (act === 'hostedit' || act === 'hostagent') { const h = hosts().find((x) => x.id === a.dataset.host); if (h) ctx.editHost(h); }
  else if (act === 'mod') import('./mobile-modules.js').then((m) => m.openSheet(a.dataset.mod, { icon, esc, api: ctx.api, wireSheet, app: $('#app') }));
  else if (act === 'allacts') { view.allActs = !view.allActs; renderHome(); }
  else if (act === 'last') { const c = lastConnection(); if (c) ctx.connect(c); }
  else if (act === 'ssh') {
    const terms = conns().filter((c) => c.kind === 'terminal');
    if (terms.length === 1) ctx.connect(terms[0]);
    else if (terms.length > 1) { view.kind = 'terminal'; renderServices(); snack(t('mobile.pickTerminal')); $('#mGrid').scrollIntoView({ block: 'center', behavior: 'smooth' }); }
    else ctx.add('terminal');
  }
}

/** Menue zu einer Verbindung (lange tippen): verbinden, Favorit, bearbeiten */
function actionSheet(c) {
  if (!c) return;
  let dlg = $('#mActDlg');
  if (!dlg) { dlg = document.createElement('dialog'); dlg.id = 'mActDlg'; dlg.className = 'm3sheet'; $('#app').appendChild(dlg); wireSheet(dlg); }
  const fav = !!c.favorite;
  dlg.innerHTML = `<h2>${esc(c.name)}</h2><p class="hint">${esc(kindText(c.kind))} · ${esc(c.username ? c.username + '@' : '')}${esc(c.host)}:${c.port}</p>
    <div class="m3-list" style="margin:0 -18px">
      <button type="button" class="mb m3-li m3-state" data-a="start"><span class="lead">${icon('play')}</span><span class="txt"><span class="hl">${openCount(c) ? t('mobile.switchSession') : t('mobile.connect')}</span>${openCount(c) ? `<span class="sl">${esc(t('mobile.openCount', { n: openCount(c) }))}</span>` : ''}</span></button>
      ${openCount(c) ? `<button type="button" class="mb m3-li m3-state" data-a="extra"><span class="lead">${icon('add')}</span><span class="txt"><span class="hl">${t('mobile.anotherSession')}</span><span class="sl">${t('mobile.anotherSessionSub')}</span></span></button>` : ''}
      <button type="button" class="mb m3-li m3-state" data-a="fav"><span class="lead">${icon(fav ? 'star' : 'star_o')}</span><span class="txt"><span class="hl">${fav ? t('mobile.unpin') : t('mobile.pin')}</span><span class="sl">${t('mobile.allDevices')}</span></span></button>
      <button type="button" class="mb m3-li m3-state" data-a="edit"><span class="lead">${icon('edit')}</span><span class="txt"><span class="hl">${t('common.edit')}</span></span></button>
    </div>`;
  dlg.onclick = (e) => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    dlg.close();
    if (b.dataset.a === 'start') ctx.connect(c);
    else if (b.dataset.a === 'extra') ctx.connect(c, { extra: true });
    else if (b.dataset.a === 'edit') ctx.edit(c);
    else setFavorite(ctx.api, c, !fav).then(() => { renderAll(); snack(fav ? t('mobile.unpinned') : t('mobile.pinned')); }, (err) => snack(err.message));
  };
  dlg.showModal();
}

export function snack(text, action) {
  if (!text || !root) return;
  const s = $('#mSnack'), b = $('#mSnackAct');
  $('#mSnackText').textContent = text;
  b.hidden = !action; if (action) { b.textContent = action.label; b.onclick = () => { s.classList.remove('show'); root.classList.remove('snacking'); action.run(); }; }
  s.classList.add('show'); root.classList.add('snacking');
  clearTimeout(snackTimer); snackTimer = setTimeout(() => { s.classList.remove('show'); root.classList.remove('snacking'); }, action ? 6000 : 4000);
}

/* ---------- Die vorhandenen Dialoge werden zu Bottom Sheets ---------- */
function wireSheet(dlg) {
  // Tippen auf den Hintergrund schliesst
  dlg.addEventListener('click', (e) => { if (e.target === dlg) { const r = dlg.getBoundingClientRect(); if (e.clientY < r.top || e.clientX < r.left || e.clientX > r.right) dlg.close(); } });
  // Nach unten wischen am oberen Rand (Griff) schliesst
  let y0 = null, dy = 0;
  dlg.addEventListener('pointerdown', (e) => { const r = dlg.getBoundingClientRect(); if (e.clientY - r.top < 36 && dlg.scrollTop === 0) { y0 = e.clientY; dy = 0; dlg.setPointerCapture(e.pointerId); dlg.classList.add('dragging'); } });
  dlg.addEventListener('pointermove', (e) => { if (y0 === null) return; dy = Math.max(0, e.clientY - y0); dlg.style.transform = `translateY(${dy}px)`; });
  const end = () => { if (y0 === null) return; y0 = null; dlg.classList.remove('dragging'); dlg.style.transform = ''; if (dy > 90) dlg.close(); };
  dlg.addEventListener('pointerup', end); dlg.addEventListener('pointercancel', end);
}
function enhanceSheets() {
  for (const id of ['connDlg', 'hostDlg', 'startDlg', 'quickDlg', 'folderDlg', 'devicesDlg', 'accountDlg', 'netDlg']) { const d = $('#' + id); if (!d) continue; d.classList.add('m3sheet'); wireSheet(d); }
  // Art und Bildübertragung: segmentierte Tasten statt Auswahlliste; das select bleibt als Wertspeicher bestehen (shell.js liest und setzt es)
  const segFor = (sel) => {
    const label = sel.closest('label'), box = document.createElement('div');
    box.className = 'm3-seg'; box.setAttribute('role', 'radiogroup');
    box.innerHTML = [...sel.options].map((o) => `<button type="button" class="mb m3-state" role="radio" data-v="${o.value}"><span class="m3-seg-check">${icon('check')}</span>${esc(o.textContent)}</button>`).join('');
    label.classList.add('m3-seglabel'); label.appendChild(box); sel.hidden = true;
    const sync = () => $$('.mb', box).forEach((b) => b.setAttribute('aria-checked', b.dataset.v === sel.value));
    box.addEventListener('click', (e) => { const b = e.target.closest('[data-v]'); if (!b) return; sel.value = b.dataset.v; sel.dispatchEvent(new Event('change')); sync(); });
    new MutationObserver(sync).observe(sel.closest('dialog'), { attributes: true, attributeFilter: ['open'] });
    sel.addEventListener('change', sync); sync();
    // das Wort vor dem Feld ist keine Beschriftung zum Ausfuellen mehr
    const lbl = [...label.childNodes].find((n) => n.nodeType === 3 && n.textContent.trim());
    if (lbl) { const sp = document.createElement('span'); sp.textContent = lbl.textContent.trim(); label.replaceChild(sp, lbl); }
  };
  segFor($('#connKind'));
  segFor($('#quickKind'));
}

/* ---------- Thema: Grundfarbe und Theme-Color ---------- */
export function applyShellTheme() {
  document.documentElement.style.setProperty('--m3-seed', curSeed());
  const probe = document.createElement('div'); probe.style.cssText = 'position:fixed;left:-9px;top:0;width:1px;height:1px;background:var(--m3-surface)';
  document.body.appendChild(probe);
  try {
    const c = document.createElement('canvas'); c.width = c.height = 1; const g = c.getContext('2d', { willReadFrequently: true });
    g.fillStyle = '#000'; g.fillStyle = getComputedStyle(probe).backgroundColor; g.fillRect(0, 0, 1, 1);
    const [r, gg, b] = g.getImageData(0, 0, 1, 1).data;
    const hex = '#' + [r, gg, b].map((x) => x.toString(16).padStart(2, '0')).join('');
    for (const m of $$('meta[name=theme-color]')) m.setAttribute('content', hex);
  } finally { probe.remove(); }
}

/* =====================================================================
 * Sitzungen (mehrere gleichzeitig): Leiste oben in der Fernsteuerung (ab zwei Sitzungen) und Umschalter als Bottom Sheet
 * ===================================================================== */
let strip = null;
function buildStrip() {
  const remote = $('#remote');
  if (!remote || strip) return;
  strip = document.createElement('div'); strip.className = 'm3-sstrip'; strip.hidden = true; strip.setAttribute('role', 'tablist'); strip.setAttribute('aria-label', t('mobile.openSessions'));
  remote.prepend(strip);
  strip.addEventListener('click', (e) => {
    const b = e.target.closest('[data-s]'); if (!b) return;
    if (b.dataset.s === 'home') ctx.home();
    else if (b.dataset.s === 'all') openSwitcher();
    else Sessions.activate(b.dataset.s, { reveal: true });
  });
}
/** Wird bei jeder Aenderung der Sitzungen gerufen (neue, geschlossene, Zustand, Wechsel): Leiste, Startseite, Umschalter neu zeichnen. */
export function sessionsChanged() {
  if (!root) return;
  buildStrip();
  const list = Sessions.list();
  strip.hidden = list.length < 2;
  strip.innerHTML = `<button type="button" class="mb m3-state sb-home" data-s="home" aria-label="${esc(t('mobile.toHome'))}">${icon('home')}</button>
    <div class="sb-chips">${list.map((s) => `<button type="button" role="tab" aria-selected="${s === Sessions.active}" class="mb sb-chip m3-state${s === Sessions.active ? ' on' : ''}" data-s="${s.id}" title="${esc(s.stateText)}"><span class="m3-dot sdot ${dotClass(s.state)}"></span><span class="n">${esc(s.title)}</span></button>`).join('')}</div>
    <button type="button" class="mb m3-state sb-all" data-s="all" aria-label="${esc(t('mobile.showAllSessions'))}">${icon('layers')}<span class="cnt">${list.length}</span></button>`;
  const on = strip.querySelector('.sb-chip.on'); if (on) on.scrollIntoView({ inline: 'center', block: 'nearest' });
  if (shown) renderAll();
  const dlg = $('#mSessDlg'); if (dlg && dlg.open) fillSwitcher(dlg);
}
function fillSwitcher(dlg) {
  dlg.innerHTML = `<h2>${t('mobile.sessions')}</h2><p class="hint">${Sessions.count ? t('mobile.switcherHint') : t('mobile.noSession')}</p>
    <div class="m3-card acts sesslist" style="margin:0">${sessionRows()}</div>
    <div class="row" style="margin-top:14px"><button type="button" class="mb m3-btn tonal m3-state" data-a="home">${icon('home')}${t('mobile.startPage')}</button><button type="button" class="mb m3-btn filled m3-state" data-a="new">${icon('add')}${t('mobile.newSession')}</button></div>`;
}
export function openSwitcher() {
  let dlg = $('#mSessDlg');
  if (!dlg) {
    dlg = document.createElement('dialog'); dlg.id = 'mSessDlg'; dlg.className = 'm3sheet'; $('#app').appendChild(dlg); wireSheet(dlg);
    dlg.addEventListener('click', (e) => {
      const x = e.target.closest('[data-act="sessclose"]');
      if (x) { e.stopPropagation(); Sessions.close(x.dataset.sid); if (!Sessions.count) dlg.close(); return; }
      const card = e.target.closest('[data-act="sess"]');
      if (card) { dlg.close(); Sessions.activate(card.dataset.sid, { reveal: true }); return; }
      const b = e.target.closest('[data-a]'); if (!b) return;
      dlg.close();
      ctx.home();
      if (b.dataset.a === 'new') go('services');
    });
  }
  fillSwitcher(dlg);
  if (!dlg.open) dlg.showModal();
}
