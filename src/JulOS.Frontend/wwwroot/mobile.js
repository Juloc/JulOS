import { ico, setTheme, setLanguage } from './app.js';
import { Sessions } from './sessions.js';
import { SCHEMES, normalizeScheme } from './scheme.js';
import { GROUPS, rowsFor, aboutValues } from './settings.js';
import { t, tp, tk, fmtNumber, fmtTime, fmtDay, getPref, LANG_CHOICES } from './i18n.js';
import { looksLikeAddress } from './quick.js';
import { tileKind, KIND_KEY, withProgram } from './terminal-programs.js';
import { favouriteList, recentList, filterByFolder, groupByFolder, folderIconSvg, folderName, setFavorite } from './folders.js';
import { hostCardHtml, menuOpen } from './hostview.js';
import { connectionState, dotClass as hostDot, stateText } from './hosts.js';
import { barText, barPercent, barTone, chipText, panelHtml, finishNotice } from './hosttasks.js';
import { jobFinal } from './hostactions.js';
import { icon as huIcon, button as huButton } from './hostui.js';
import { connLabels } from './conn-names.js';

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
  folder: 'M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z',
  dns: 'M20 13H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1v-6c0-.55-.45-1-1-1zM7 19c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM20 3H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1V4c0-.55-.45-1-1-1zM7 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z',
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
  tune: 'M3 17v2h6v-2H3zM3 5v2h10V5H3zm10 14v2h8v-2h-8zM7 9v2H3v2h4v2h2V9H7zm14 4v-2H11v2h10zm-6-4h2V7h4V5h-4V3h-2v6z',
  construction: 'M13.78 15.3l6 6 2.11-2.16-6-6zM17.5 10c1.93 0 3.5-1.57 3.5-3.5 0-.58-.16-1.12-.41-1.6l-2.7 2.7-1.49-1.49 2.7-2.7c-.48-.25-1.02-.41-1.6-.41C15.57 3 14 4.57 14 6.5c0 .41.08.8.21 1.16l-1.85 1.85-1.78-1.78.71-.71-1.41-1.41L12 3.49c-1.17-1.17-3.07-1.17-4.24 0L4.22 7.03l1.41 1.41H2.81l-.71.71 3.54 3.54.71-.71V9.15l1.41 1.41.71-.71 1.78 1.78-7.41 7.41 2.12 2.12L16.34 11.8c.36.13.75.2 1.16.2z',
};
// Claude und Codex: eigene einfache Zeichen (ein Funkelstern und Klammern), keine Marken
X.claude = 'M12 2l1.9 6.1L20 6l-4.1 4.9L22 12l-6.1 1.1L20 18l-6.1-2.1L12 22l-1.9-6.1L4 18l4.1-4.9L2 12l6.1-1.1L4 6l6.1 2.1z';
X.codex = 'M9.4 16.6 4.8 12l4.6-4.6L8 6l-6 6 6 6zm5.2 0 4.6-4.6-4.6-4.6L16 6l6 6-6 6z';
const icon = (n) => (X[n] ? `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${X[n]}"/></svg>` : ico(n));
const kindIcon = (k) => icon(k === 'terminal' ? 'terminal' : 'desktop');
const connIcon = (c) => icon(tileKind(c) === 'vnc' ? 'desktop' : tileKind(c));              // Terminal-Verbindungen mit Claude oder Codex bekommen ihr eigenes Symbol
const LOGO = `<svg viewBox="0 0 32 32" aria-hidden="true"><defs><linearGradient id="mlg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="var(--m3-primary)"/><stop offset="1" stop-color="var(--m3-tertiary)"/></linearGradient></defs><rect width="32" height="32" rx="10" fill="url(#mlg)"/><path d="M12 9.5v13l11-6.5z" fill="var(--m3-on-primary)"/></svg>`;
const DEST = [
  { id: 'home', label: tk('mobile.home'), ic: 'home' },
  { id: 'services', label: tk('mobile.services'), ic: 'apps', title: tk('mobile.services') },
  { id: 'files', label: tk('mobile.files'), ic: 'folder', title: tk('mobile.files') },
  { id: 'server', label: tk('mobile.hosts'), ic: 'dns', title: tk('mobile.hosts') },
  { id: 'profile', label: tk('mobile.profile'), ic: 'person', title: tk('mobile.profile') },
  { id: 'host', hidden: true, title: tk('mobile.hosts') },        // die Seite eines Hosts: gehoert zu Hosts, steht nicht in der Leiste
];
const SWATCHES = [['#9a67ea', tk('mobile.colorViolet')], ['#4f86f7', tk('mobile.colorBlue')], ['#00a67e', tk('mobile.colorGreen')], ['#0aa5b8', tk('mobile.colorTeal')], ['#e5599a', tk('mobile.colorPink')], ['#e5484d', tk('mobile.colorRed')], ['#f5a524', tk('mobile.colorOrange')]];
const SCHEME_LABEL = { light: tk('mobile.schemeLight'), dark: tk('mobile.schemeDark'), auto: tk('mobile.schemeAuto') };
const kindText = (k) => (k === 'terminal' ? t('shell.kindTerminal') : k === 'vnc' ? t('shell.kindVnc') : t('shell.kindDesktop'));
const connKindText = (c) => t(KIND_KEY[tileKind(c)]);
const DEFAULT_SEED = '#9a67ea';

let ctx = null, root = null, ready = null;
let current = 'home', shown = false;
let hostPageId = null, hostPagePanel = null;     // die Seite eines Hosts (openHost)
let user = '';
let overview = null, overviewOk = null, overviewAt = 0, overviewTimer = 0;   // overviewOk: null = unbekannt, true/false
const view = { q: '', kind: 'all', host: '', folder: 'all', allActs: false };
let snackTimer = 0;

const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const conns = () => (ctx ? ctx.state.connections : []);
const folders = () => (ctx ? ctx.state.folders || [] : []);
const hosts = () => (ctx ? ctx.state.hosts : []);
const byId = (id) => conns().find((c) => c.id === id);

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
    <button type="button" class="m3-tasks m3-state" id="mTasks" hidden><span class="tt"></span><span class="tbar"><i></i></span></button>
    <div class="m3-stage" id="mStage">
      <section class="m3-page" id="pg-home" data-page="home" aria-label="${esc(t('mobile.home'))}"></section>
      <section class="m3-page" id="pg-services" data-page="services" aria-label="${esc(t('mobile.services'))}" hidden></section>
      <section class="m3-page" id="pg-files" data-page="files" aria-label="${esc(t('mobile.files'))}" hidden></section>
      <section class="m3-page" id="pg-server" data-page="server" aria-label="${esc(t('mobile.server'))}" hidden></section>
      <section class="m3-page" id="pg-host" data-page="host" aria-label="${esc(t('mobile.hosts'))}" hidden></section>
      <section class="m3-page" id="pg-profile" data-page="profile" aria-label="${esc(t('mobile.profile'))}" hidden></section>
    </div>
    <nav class="m3-nav" id="mNav" aria-label="${esc(t('mobile.mainNav'))}"></nav>
    <section class="m3-taskpage" id="mTaskPage" role="region" aria-label="${esc(t('hosttask.menuTitle'))}" hidden>
      <header><button type="button" class="mb m3-iconbtn m3-state" id="mTaskBack" aria-label="${esc(t('common.back'))}">${huIcon('back')}</button><h2 id="mTaskTitle"></h2></header>
      <div class="tpb" id="mTaskBody"></div>
    </section>
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
  wireTasks();
  $('#mAvatar').addEventListener('click', () => go('profile'));
  for (const pg of $$('.m3-page')) {
    pg.addEventListener('scroll', () => {
      const top = pg.scrollTop;
      const head = pg.querySelector('[data-head]');
      $('#mBar').classList.toggle('scrolled', top > 4);
      $('#mBar').classList.toggle('titled', !!head && top > head.offsetTop + head.offsetHeight - 8);
    }, { passive: true });
  }
  // Delegation fuer alles, was in den Seiten gezeichnet wird
  root.addEventListener('click', onClick);
  root.addEventListener('contextmenu', (e) => { const card = e.target.closest('[data-conn]'); if (card) { e.preventDefault(); actionSheet(byId(card.dataset.conn)); } });
  window.addEventListener('popstate', (e) => { const st = e.state && e.state.m3; if (shown) go(st === 'host' ? (hostPageId ? 'host' : 'server') : st || 'home', { push: false }); });
  history.replaceState({ m3: 'home' }, '');
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => queueMicrotask(applyShellTheme));
  document.addEventListener('visibilitychange', () => { if (!document.hidden) pollOverview(); });
}

function renderNav() {
  // Seiten ohne Inhalt gibt es nicht: Monitoring fehlt, Server nur mit Auslastung oder Hosts
  const items = DEST.filter((d) => !d.hidden && (d.id !== 'server' || overviewOk || hosts().length));
  $('#mNav').innerHTML = items.map((d) => `<button type="button" class="mb m3-state" data-go="${d.id}" ${d.id === (current === 'host' ? 'server' : current) ? 'aria-current="page"' : ''}><span class="pill">${icon(d.ic)}</span><span>${esc(t(d.label))}</span></button>`).join('');
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
export function reset() { closeTaskPage(); renderTasks(); if (filesApp) { filesApp.destroy(); filesApp = null; } mods = { caddy: false, ddns: false, services: false }; modsPromise = null; srvActs = []; srvActsAt = 0; import('./heimnetz.js').then((h) => h.resetHeimnetz()); overview = null; overviewOk = null; user = ''; current = 'home'; view.q = ''; view.kind = 'all'; setShown(false); if (root) { root.hidden = true; root.removeAttribute('style'); } }

export function userChanged(name, message) {
  user = name;
  if (!root) return;
  $('#mAvatar').textContent = (name || '?').trim().slice(0, 1);
  if (message) snack(message);
  renderAll();
}
export function connectionsChanged() { if (root) { renderAll(); if (filesApp) filesApp.refreshPlaces(); } }
/* ---------- Hintergrund-Aufgaben (hosttasks.js) ----------
 * Eine laufende Aktion sperrt nichts: die Leiste unter der Kopfzeile zeigt Text und Balken, die Seite (eine Ebene mit Zurueck-Pfeil, kein Dialog) zeigt Live-Ausgabe, Fehlerzusammenfassung
 * und Protokoll. Sie zu verlassen bricht die Aktion nicht ab. Mehrere Aufgaben stehen als Liste. */
let taskJob = null, taskPageOpen = false, taskSig = '';
const taskList = () => (ctx && ctx.tasks ? ctx.tasks.list() : []);
function renderTasks() {
  const b = $('#mTasks'); if (!b) return;
  const list = taskList();
  b.hidden = !list.length;
  if (list.length) {
    const pct = barPercent(list), running = list.some((x) => !jobFinal(x));
    b.dataset.tone = barTone(list); b.querySelector('.tt').textContent = barText(list);
    const bar = b.querySelector('.tbar'); bar.hidden = !running; bar.classList.toggle('ind', pct === null); bar.firstElementChild.style.width = pct === null ? '' : pct + '%';
  }
  if (taskPageOpen) renderTaskPage();
}
function renderTaskPage() {
  const page = $('#mTaskPage'), body = $('#mTaskBody');
  const list = taskList(), task = taskJob && ctx.tasks.get(taskJob);
  if (taskJob && !task) taskJob = null;
  const sig = task ? [task.state, task.progress, task.done, task.total, task.tail.length, task.tail[task.tail.length - 1] || '', task.error, task.result ? 1 : 0].join('|') : 'list:' + list.map((x) => x.job + x.state + x.done).join(',');
  if (sig === taskSig && !page.hidden) return;
  taskSig = sig;
  const open = [...body.querySelectorAll('details')].map((d) => d.open), top = body.scrollTop;
  const live = body.querySelector('[data-live]'), atEnd = !live || live.scrollTop + live.clientHeight >= live.scrollHeight - 8;
  $('#mTaskTitle').textContent = task ? chipText(task) : t('hosttask.menuTitle');
  body.innerHTML = task ? panelHtml(task, esc, hosts().find((h) => h.id === task.host.id) || null)
    : `<ul class="hu-list">${list.map((x) => `<li><div class="hu-lt"><span class="hu-ln">${esc(chipText(x))}</span></div>${huButton(t('hosttask.show'), esc, { tone: 'tonal', sm: true, data: { 'task-open': x.job } })}</li>`).join('')}</ul>`
      + (list.some(jobFinal) ? `<div class="hu-acts">${huButton(t('hosttask.clearDone'), esc, { tone: 'text', sm: true, data: { 'task-clear': '1' } })}</div>` : '');
  body.querySelectorAll('details').forEach((d, i) => { d.open = !!open[i]; });
  body.scrollTop = top;
  const nl = body.querySelector('[data-live]'); if (nl && atEnd) nl.scrollTop = nl.scrollHeight;
  page.hidden = false;
}
function openTaskPage(job) { taskJob = job || null; taskPageOpen = true; taskSig = ''; renderTaskPage(); if (job) ctx.tasks.markSeen(job); $('#mTaskBack').focus(); }
function closeTaskPage() { taskPageOpen = false; taskJob = null; const p = $('#mTaskPage'); if (p) p.hidden = true; }
function wireTasks() {
  $('#mTasks').addEventListener('click', () => { const l = taskList(); openTaskPage(l.length === 1 ? l[0].job : null); });
  $('#mTaskBack').addEventListener('click', () => { if (taskJob && taskList().length > 1) { taskJob = null; taskSig = ''; renderTaskPage(); } else closeTaskPage(); });
  $('#mTaskBody').addEventListener('click', (e) => {
    const open = e.target.closest('[data-task-open]'), clr = e.target.closest('[data-task-clear]'), act = e.target.closest('[data-task-act]');
    if (open) openTaskPage(open.dataset.taskOpen);
    else if (clr) { ctx.tasks.dismissFinished(); if (!taskList().length) closeTaskPage(); }
    else if (act && act.dataset.taskAct === 'repair' && taskJob) {
      const task = ctx.tasks.get(taskJob), h = task && hosts().find((x) => x.id === task.host.id);
      if (h) ctx.hostAction(h, 'updates.repair', null, { onDone: () => ctx.info.load(h, { fresh: true }) });
    }
  });
  window.addEventListener('popstate', () => closeTaskPage());
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && taskPageOpen) closeTaskPage(); });
}
/** Zustand der Aufgaben hat sich geaendert (shell.js) */
export function tasksChanged() { if (root) renderTasks(); }
/** Eine Aufgabe ist fertig: Hinweis mit Ergebnis und Weg zur Seite */
export function taskFinished(task) { if (!root) return; const n = finishNotice(task); snack(n.detail ? `${n.text} – ${n.detail}` : n.text, { label: t('hosttask.show'), run: () => openTaskPage(task.job) }); }
export function taskStarted(task) { if (root) snack(t('hosttask.started', { action: chipText(task) }), { label: t('hosttask.show'), run: () => openTaskPage(task.job) }); }

/** Die Seite eines Hosts (hostpage.js) mit Zurueck-Pfeil; der Pfeil geht einen Schritt zurueck (zur Liste der Hosts) */
function renderHostPage() {
  const h = hosts().find((x) => x.id === hostPageId);
  if (!h) { go('server'); return; }
  ctx.hostPage.mount($('#pg-host'), h, hostPagePanel, { onBack: () => { if (history.state && history.state.m3 === 'host') history.back(); else go('server'); } });
}
/** Oeffnet die Seite eines Hosts (optional gleich in einem Abschnitt) */
export function openHost(host, panel) {
  hostPageId = host.id; hostPagePanel = panel || null;
  if (current === 'host') renderPage('host'); else go('host');
}
/** Neue Zustaende der Hosts (alle 30 s): die Seite neu zeichnen, die sie zeigt */
export function hostsChanged() { if (root && !menuOpen()) renderAll(); }
/** Neue Systemwerte oder Updates eines Hosts (hostinfo.js): nur die Seite Server neu zeichnen, wenn sie gerade zu sehen ist */
export function hostInfoChanged() { if (root && current === 'server' && !menuOpen()) renderServer(); }

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
    if (from === 'host') ctx.hostPage.unmount();
    if (push) { if (id === 'host') history.pushState({ m3: 'host' }, ''); else if (from === 'host') history.replaceState({ m3: id }, ''); else if (from === 'home') history.pushState({ m3: id }, ''); else if (id === 'home') { if (history.state && history.state.m3 && history.state.m3 !== 'home') { history.back(); return; } } else history.replaceState({ m3: id }, ''); }
    const a = $('#pg-' + from), b = $('#pg-' + id);
    current = id;
    $$('#mNav .mb').forEach((n) => { if (n.dataset.go === (id === 'host' ? 'server' : id)) n.setAttribute('aria-current', 'page'); else n.removeAttribute('aria-current'); });
    a.classList.remove('in'); a.classList.add('out');
    setTimeout(() => { a.hidden = true; a.classList.remove('out'); }, 90);
    setTimeout(() => {
      b.hidden = false; b.classList.add('in'); b.addEventListener('animationend', () => b.classList.remove('in'), { once: true });
      b.dispatchEvent(new Event('scroll'));
    }, 90);
    renderPage(id);
    const d = DEST.find((x) => x.id === id);
    $('#mBar').classList.toggle('home', id === 'home');
    $('#mBarName').hidden = id !== 'home'; $('#mBarTitle').textContent = d.title ? t(d.title) : ''; $('#mBarTitle').hidden = id === 'home';
    $('#mBar').classList.remove('titled', 'scrolled');
    if (id === 'server' || id === 'home') pollOverview();
    if (id === 'profile') { about = null; loadAbout(); }
  }
  if (focusSearch) setTimeout(() => $('#mSearch').focus(), 330);
}

function renderAll() { if (!root) return; renderNav(); if (current !== 'host') renderPage(current); }       // die Seite eines Hosts zeichnet sich nicht von selbst neu (Reiter und Stand blieben nicht)
function renderPage(id) {
  if (id === 'home') renderHome(); else if (id === 'services') renderServices(); else if (id === 'server') renderServer(); else if (id === 'files') renderFiles();
  else if (id === 'profile') renderProfile(); else if (id === 'host') renderHostPage();
  // Seitentitel in der Leiste: auf Home bleibt "JulOS", sonst der Titel der Seite
  $('#mBar').classList.toggle('home', current === 'home');
}

/* =====================================================================
 * Dateien (files-app.js, docs/FILES.md): der gemeinsame Ordner des Servers und jeder Host mit SSH-Zugang. Die Seite wird beim ersten Besuch geladen und bleibt danach bestehen
 * (der Ort, der Pfad und die Auswahl gehen beim Wechseln der Seite nicht verloren); die Orte folgen den Verbindungen und Hosts.
 * ===================================================================== */
let filesApp = null, filesLoading = false;
function renderFiles() {
  if (filesApp) { filesApp.refreshPlaces(); return; }
  if (filesLoading) return;
  filesLoading = true;
  import('./files-app.js').then((m) => {
    filesApp = m.mountFiles($('#pg-files'), { api: ctx.api, kit: true, getState: () => ({ connections: conns(), hosts: hosts() }), notify: (text) => snack(text) });
  }).catch(() => { $('#pg-files').textContent = t('desktop.filesLoadFailed'); }).finally(() => { filesLoading = false; });
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
/** Kachel: Symbol, Name (zwei Zeilen), darunter Statuspunkt und Host; rechts oben die Zahl offener Sitzungen oder der Stern des Favoriten. L: connLabels() der Liste */
const tileHtml = (c, pinned, L) => {
  const l = (L && L.get(c.id)) || { name: c.name, host: '' }, st = connectionState(hosts(), c), n = openCount(c);
  return `<button type="button" class="mb tile m3-state ${c.kind} k-${tileKind(c)}${n ? ' live' : ''}" data-conn="${c.id}" data-act="start" aria-label="${esc(t('mobile.tileAria', { name: l.name, kind: connKindText(c) + ', ' + l.host, state: stateText(st), action: n ? t('mobile.tileOpen', { n }) : t('mobile.tileTap') }))}">
  <span class="ti">${connIcon(c)}</span><span class="tx"><span class="tn">${esc(l.name)}</span><span class="th"><span class="hdot ${hostDot(st)}" title="${esc(stateText(st))}"></span><span>${esc(l.host)}</span></span></span>${n ? `<span class="liveb" title="${esc(t('mobile.sessionOpen'))}">${n}</span>` : pinned ? `<span class="fav" title="${esc(t('mobile.favourite'))}">${icon('star')}</span>` : ''}</button>`;
};

/* Vom Server: die letzten DNS-Aenderungen des Moduls DDNS erscheinen in den Aktivitaeten (nur wenn das Modul an ist, hoechstens einmal je Minute abgefragt) */
let srvActs = [], srvActsAt = 0;
async function loadSrvActs() {
  if (!mods.ddns || Date.now() - srvActsAt < 60000) return;
  srvActsAt = Date.now();
  try {
    const r = await ctx.api('/api/modules/ddns/history');
    srvActs = (r.entries || []).slice(0, 6).map((e) => ({ t: Date.parse(e.at), type: 'ddns', name: e.host, msg: `${e.oldIp || '–'} → ${e.newIp}`, ok: e.ok }));
    renderHome();
  } catch { /* die Aktivitaeten bleiben, wie sie sind */ }
}
const allActs = () => [...ls.get('log', []).filter((l) => l.type !== 'start' || byId(l.id)), ...srvActs].sort((a, b) => b.t - a.t);
function activityRows(limit) {
  const log = allActs().slice(0, limit);
  return log.map((l) => {
    if (l.type === 'ddns') return `<div class="m3-li static"><span class="lead ${l.ok ? 'ok' : 'warn'}">${icon(l.ok ? 'check_circle' : 'info')}</span><span class="txt"><span class="hl">${esc(t('mobile.ddnsAct', { host: l.name }))}</span><span class="sl">${esc(l.msg)}</span></span><span class="tr">${esc(whenLabel(l.t))}</span></div>`;
    const c = byId(l.id);
    const lead = l.type === 'end' ? `<span class="lead warn">${icon('info')}</span>` : `<span class="lead ok">${icon('check_circle')}</span>`;
    const sub = l.type === 'end' ? esc(l.msg) : esc(t('mobile.started', { kind: kindText(l.kind) }));
    return `<div class="m3-li static">${lead}<span class="txt"><span class="hl">${esc(c ? c.name : l.name || t('mobile.connection'))}</span><span class="sl">${sub}</span></span><span class="tr">${esc(whenLabel(l.t))}</span></div>`;
  }).join('');
}

/* Offene Sitzungen: Karten mit Zustandspunkt, ein Tipp wechselt hin, das Kreuz schliesst */
const dotClass = (st) => (st === 'connected' ? 'ok' : st === 'connecting' || st === 'reconnecting' ? 'busy' : 'bad');
const sessionRows = () => Sessions.list().map((s) => `<div class="m3-li sess ${s === Sessions.active ? 'act' : ''}" data-act="sess" data-sid="${s.id}" role="button" tabindex="0" aria-label="${esc(t('mobile.sessAria', { title: s.title, state: s.stateText }))}">
    <span class="lead">${icon(tileKind(s.conn))}</span><span class="txt"><span class="hl">${esc(s.title)}</span><span class="sl"><span class="m3-dot sdot ${dotClass(s.state)}"></span>${esc(s.stateText)} · ${esc(s.conn.name === s.title ? kindText(s.kind) : s.conn.name)}</span></span>
    <button type="button" class="mb sx m3-state" data-act="sessclose" data-sid="${s.id}" aria-label="${esc(t('mobile.sessClose', { title: s.title }))}">${icon('close')}</button></div>`).join('');
function sessionsSection() {
  if (!Sessions.count) return '';
  return `<h2 class="m3-sec">${esc(t('mobile.openSessions'))}</h2><div class="m3-card acts sesslist">${sessionRows()}</div>`;
}

/** Die grosse Zeile "Weiter": die zuletzt benutzte Verbindung mit Host und Zeitpunkt; ohne Verbindung die Hauptaktion "Neue Verbindung" */
function continueRow(L) {
  const c = lastConnection();
  if (!c) return `<div class="m3-card"><div class="m3-empty"><span class="ei">${icon('desktop')}</span><span class="et">${t('mobile.noConnectionYet')}</span><p>${t('mobile.noConnectionHelp')}</p><button type="button" class="mb m3-btn filled m3-state" data-act="add">${icon('add')}${t('shell.newConnection')}</button></div></div>`;
  const l = L.get(c.id) || { name: c.name, host: '' }, st = connectionState(hosts(), c), when = c.lastUsedAt ? whenLabel(Date.parse(c.lastUsedAt) || c.lastUsedAt) : '';
  return `<button type="button" class="mb m3-main m3-state" data-act="last" aria-label="${esc(t('mobile.continueAria', { name: l.name, host: l.host }))}">
    <span class="ti">${connIcon(c)}</span><span class="tx"><span class="cl">${t('mobile.continue')}</span><span class="cn">${esc(l.name)}</span><span class="th"><span class="hdot ${hostDot(st)}" title="${esc(stateText(st))}"></span><span>${esc([l.host, when].filter(Boolean).join(' · '))}</span></span></span><span class="go">${icon('play')}</span></button>`;
}
/** Claude Code und Codex mit einem Tipp: ein Tipp startet die passende Kachel oder legt sie an (shell.js: launchProgram) */
const PROG_NAME = { claude: tk('prog.claude'), codex: tk('prog.codex') };
const programTile = (p) => `<button type="button" class="mb tile m3-state terminal k-${p}" data-act="prog" data-prog="${p}"><span class="ti">${icon(p)}</span><span class="tx"><span class="tn">${t(PROG_NAME[p])}</span><span class="th"><span>${t('mobile.progStart')}</span></span></span></button>`;

function renderHome() {
  const pg = $('#pg-home'), top = pg.scrollTop;
  const list = conns(), favs = favourites(), L = connLabels(list, hosts());
  // Server: nur eine kleine Zeile, die Zahlen stehen auf der Seite Hosts
  const hot = overviewOk && overview && ((overview.cpu ?? 0) >= 90 || (overview.ram ?? 0) >= 90 || (overview.disk ?? 0) >= 95);
  const server = overviewOk && overview ? `<div class="m3-card acts" style="margin-top:var(--m3-sp-5)"><button type="button" class="mb m3-li m3-state" data-go="server" aria-label="${esc(t('mobile.serverDetails'))}"><span class="lead${hot ? ' warn' : ' ok'}">${icon('dns')}</span><span class="txt"><span class="hl">${esc(serverTitle(overview))}: ${hot ? t('mobile.serverBusy') : t('mobile.serverStable')}</span><span class="sl">CPU ${pct(overview.cpu)} · RAM ${pct(overview.ram)}</span></span>${icon('chevron_right')}</button></div>` : '';
  const favHtml = favs.length
    ? `<div class="grid2">${favs.map((c) => tileHtml(c, c.favorite, L)).join('')}</div>`
    : `<div class="m3-card"><div class="m3-empty inline"><span class="ei">${icon('star_o')}</span><p>${list.length ? t('mobile.favHintSome') : t('mobile.favHintNone')}</p></div></div>`;
  ensureMods().then(loadSrvActs);
  const actsAll = allActs();
  const acts = actsAll.length
    ? `<div class="m3-card acts">${activityRows(view.allActs ? 15 : 4)}</div><p class="m3-help" style="margin-top:var(--m3-sp-2)">${t('mobile.actsNote')}</p>`
    : `<div class="m3-card"><div class="m3-empty inline"><span class="ei">${icon('history')}</span><p>${t('mobile.actsEmpty')}</p></div></div>`;
  pg.innerHTML = `
    <div class="m3-h" data-head><h1>${greeting()}, ${esc(user)}</h1></div>
    ${continueRow(L)}
    ${sessionsSection()}
    <div class="m3-sec"><span>${t('mobile.myFavourites')}</span><span class="sp"></span><button type="button" class="mb m3-btn text m3-state" data-go="services">${t('mobile.allServices')}${icon('chevron_right')}</button></div>
    ${favHtml}
    <h2 class="m3-sec">${t('mobile.programs')}</h2><p class="m3-help">${t('mobile.programsHelp')}</p>
    <div class="grid2">${programTile('claude')}${programTile('codex')}</div>
    <div id="mHeimSec" hidden><h2 class="m3-sec">${t('heimnetz.title')}</h2><div id="mHeim"></div></div>
    <div class="m3-sec"><span>${t('mobile.activities')}</span><span class="sp"></span>${actsAll.length > 4 ? `<button type="button" class="mb m3-btn text m3-state" data-act="allacts">${view.allActs ? t('mobile.showLess') : t('mobile.showAll')}${icon('chevron_right')}</button>` : ''}</div>
    ${acts}
    ${server}`;
  pg.scrollTop = top;
  mountHeim();
}

/* Karte "Heimnetz" (heimnetz.js): nur, wenn der Server mindestens ein Modul eingeschaltet hat */
function mountHeim() {
  const box = $('#mHeim'); if (!box) return;
  ensureMods().then(async (m) => {
    if (!(m.caddy || m.ddns || m.services) || !box.isConnected) return;
    $('#mHeimSec').hidden = false;
    const h = await import('./heimnetz.js');
    if (box.isConnected) h.mountHeimnetz(box, { api: ctx.api, mods: m, esc, open: (k) => (k === 'services' ? go('services') : openModSheet(k)) });
  });
}
const openModSheet = (kind) => import('./mobile-modules.js').then((m) => m.openSheet(kind, { icon, esc, api: ctx.api, wireSheet, app: $('#app') }));

/* =====================================================================
 * Verbindungen: Suche und Filter in einer Zeile, darunter die Kacheln; ein einziger Haupt-Knopf "Neue Verbindung"
 * ===================================================================== */
function buildServicesPage() {
  $('#pg-services').innerHTML = `
    <div class="m3-h" data-head><div class="m3-h-top"><h1>${t('mobile.services')}</h1><span id="mNewSlot"></span></div><p>${t('mobile.servicesSub')}</p></div>
    <div class="m3-searchrow"><label class="m3-search"><span class="m3-ic" aria-hidden="true">${icon('search')}</span><input id="mSearch" type="search" placeholder="${esc(t('mobile.searchPlaceholder'))}" autocomplete="off" autocapitalize="none" spellcheck="false" enterkeyhint="search" aria-label="${esc(t('mobile.searchServices'))}"></label>
      <button type="button" class="mb m3-btn tonal big m3-state" id="mFilterBtn" data-act="filter">${icon('tune')}<span>${t('mobile.filter')}</span><span class="m3-count" id="mFilterCount" hidden></span></button></div>
    <div class="m3-chipset" id="mActive" aria-label="${esc(t('mobile.activeFilters'))}"></div>
    <div id="mGrid"></div>
    <div id="mWidgets"></div>
    <div id="mMods"></div>`;
  $('#mSearch').addEventListener('input', (e) => { view.q = e.target.value; renderGrid(); });
}
/** Die gesetzten Filter als Liste { key, label }: Art, Ordner, Host */
function activeFilters() {
  const out = [];
  if (view.kind !== 'all') out.push({ key: 'kind', label: kindFilterLabel(view.kind) });
  if (view.folder !== 'all') out.push({ key: 'folder', label: folderFilterLabel(view.folder) });
  if (view.host) { const h = hosts().find((x) => x.id === view.host); if (h) out.push({ key: 'host', label: h.name }); }
  return out;
}
const kindFilterLabel = (k) => (k === 'desktop' ? t('shell.kindDesktop') : k === 'vnc' ? t('shell.kindVnc') : k === 'terminal' ? t('shell.kindTerminal') : t('mobile.all'));
function folderFilterLabel(id) {
  return id === 'fav' ? t('mobile.favourites') : id === 'recent' ? t('mobile.recentlyUsed') : id === 'none' ? t('mobile.noFolder') : (folders().find((f) => f.id === id) || { name: '' }).name;
}
function filtered() {
  const q = view.q.trim().toLowerCase();
  const list = view.folder === 'recent' ? recentList(conns(), 12) : filterByFolder(conns(), view.folder, folders());
  return list.filter((c) => (view.kind === 'all' || c.kind === view.kind) && (!view.host || c.hostId === view.host) && (!q || `${c.name} ${c.host} ${c.username}`.toLowerCase().includes(q)));
}
function renderServices() {
  if (!$('#mGrid')) return;
  const known = new Set(['all', 'fav', 'recent', 'none', ...folders().map((f) => f.id)]);
  if (!known.has(view.folder)) view.folder = 'all';
  if (view.host && !hosts().some((h) => h.id === view.host)) view.host = '';
  // der Haupt-Knopf: im Kopf der Seite; ohne Verbindungen steht er im Leerzustand
  $('#mNewSlot').innerHTML = conns().length ? `<button type="button" class="mb m3-btn filled m3-state" data-act="add">${icon('add')}${t('shell.newConnection')}</button>` : '';
  const act = activeFilters(), cnt = $('#mFilterCount');
  cnt.hidden = !act.length; cnt.textContent = act.length;
  $('#mFilterBtn').setAttribute('aria-label', act.length ? t('mobile.filterN', { n: act.length }) : t('mobile.filter'));
  $('#mActive').hidden = !act.length;
  $('#mActive').innerHTML = act.map((f) => `<button type="button" class="mb m3-chip sel m3-state" data-act="rmfilter" data-key="${f.key}" aria-label="${esc(t('mobile.removeFilter', { name: f.label }))}">${esc(f.label)}${icon('close')}</button>`).join('');
  renderGrid(); renderModules();
}
function renderGrid() {
  const list = filtered(), L = connLabels(conns(), hosts());
  const grouped = view.folder === 'all' && !view.q.trim() && folders().length;     // mit Ordnern: unter ihren Ueberschriften
  const none = !conns().length
    ? `<div class="m3-card"><div class="m3-empty"><span class="ei">${icon('desktop')}</span><span class="et">${t('mobile.noConnectionYet')}</span><p>${t('mobile.noConnectionHelp')}</p><button type="button" class="mb m3-btn filled m3-state" data-act="add">${icon('add')}${t('shell.newConnection')}</button></div></div>`
    : `<div class="m3-card"><div class="m3-empty"><span class="ei">${icon('search')}</span><span class="et">${t('mobile.noMatch')}</span><p>${t('mobile.noMatchHelp')}</p>${activeFilters().length || view.q ? `<button type="button" class="mb m3-btn tonal m3-state" data-act="resetfilters">${t('mobile.resetFilters')}</button>` : ''}</div></div>`;
  $('#mGrid').innerHTML = grouped && list.length
    ? groupByFolder(folders(), list).map((g) => `<h2 class="m3-sec">${g.folder ? folderIconSvg(g.folder.icon) : ''}<span>${esc(g.folder ? g.folder.name : t('mobile.noFolder'))}</span></h2><div class="grid2">${g.connections.map((c) => tileHtml(c, c.favorite, L)).join('')}</div>`).join('')
    : list.length
    ? `<div class="grid2">${list.map((c) => tileHtml(c, c.favorite, L)).join('')}</div>`
    : none;
  // Sieht die Suche nach einer Adresse aus: schnell verbinden, ohne zu speichern
  if (looksLikeAddress(view.q)) $('#mGrid').insertAdjacentHTML('beforeend', `<button type="button" class="mb m3-btn tonal block m3-state" data-act="quickq" style="margin-top:var(--m3-sp-3)">${icon('bolt')}${esc(t('mobile.quickTo', { addr: view.q.trim() }))}</button>`);
}
/** Das Blatt "Filter": Art, Ordner, Host; jede Wahl gilt sofort, "Zuruecksetzen" loescht alle */
function filterSheet() {
  let dlg = $('#mFilterDlg');
  if (!dlg) { dlg = document.createElement('dialog'); dlg.id = 'mFilterDlg'; dlg.className = 'm3sheet'; $('#app').appendChild(dlg); wireSheet(dlg); }
  const chip = (attr, val, label, on, lead = '') => `<button type="button" class="mb m3-chip m3-state" data-${attr}="${esc(val)}" aria-pressed="${on}">${on ? icon('check') : lead}${esc(label)}</button>`;
  const kinds = ['desktop', 'vnc', 'terminal'].filter((k) => conns().some((c) => c.kind === k));
  const hasFolders = folders().length || conns().some((c) => c.favorite || c.lastUsedAt);
  dlg.innerHTML = `<h2>${t('mobile.filter')}</h2><p class="hint">${t('mobile.filterHint')}</p>
    <h3 class="m3-sec">${t('mobile.filterKind')}</h3><div class="m3-chipset">${chip('kind', 'all', t('mobile.all'), view.kind === 'all')}${kinds.map((k) => chip('kind', k, kindFilterLabel(k), view.kind === k)).join('')}</div>
    ${hasFolders ? `<h3 class="m3-sec">${t('mobile.folders')}</h3><div class="m3-chipset">${chip('folder', 'all', t('mobile.allFolders'), view.folder === 'all')}${chip('folder', 'fav', t('mobile.favourites'), view.folder === 'fav', icon('star_o'))}${chip('folder', 'recent', t('mobile.recentlyUsed'), view.folder === 'recent', icon('history'))}${folders().map((f) => chip('folder', f.id, f.name, view.folder === f.id, folderIconSvg(f.icon))).join('')}${folders().length ? chip('folder', 'none', t('mobile.noFolder'), view.folder === 'none') : ''}</div>` : ''}
    ${hosts().length > 1 ? `<h3 class="m3-sec">${t('mobile.hosts')}</h3><div class="m3-chipset">${chip('host', '', t('mobile.allHosts'), !view.host)}${hosts().map((h) => chip('host', h.id, h.name, view.host === h.id, `<span class="m3-dot hdot ${hostDot(h.online)}"></span>`)).join('')}</div>` : ''}
    <div class="row"><button type="button" class="ghost" data-a="reset">${t('mobile.resetFilters')}</button><button type="button" class="primary" data-a="done">${t('common.done')}</button></div>`;
  dlg.onclick = (e) => {
    const b = e.target.closest('[data-kind],[data-folder],[data-host],[data-a]'); if (!b) return;
    if (b.dataset.a === 'done') { dlg.close(); return; }
    if (b.dataset.a === 'reset') { view.kind = 'all'; view.folder = 'all'; view.host = ''; }
    else if (b.dataset.kind !== undefined) view.kind = b.dataset.kind;
    else if (b.dataset.folder !== undefined) view.folder = b.dataset.folder;
    else if (b.dataset.host !== undefined) view.host = b.dataset.host;
    renderServices(); filterSheet();
  };
  if (!dlg.open) dlg.showModal();
}
/* Module Caddy und DDNS (nur lesen): Karten nur, wenn der Server sie eingeschaltet hat (GET /api/modules); Details im Sheet, siehe mobile-modules.js */
let mods = { caddy: false, ddns: false, services: false }, modsPromise = null;
/** Welche Module der Server eingeschaltet hat: einmal je Anmeldung geladen (Startseite und Dienste-Seite brauchen es). */
const ensureMods = () => modsPromise || (modsPromise = import('./modules.js').then((m) => m.loadModules(ctx.api)).then((m) => { mods = m; return m; }));
async function renderModules() {
  const box = $('#mMods'); if (!box) return;
  await ensureMods();
  const mm = (mods.caddy || mods.ddns || mods.services) ? await import('./mobile-modules.js') : null;
  if (mods.caddy || mods.ddns) mm.renderCards(box, mods, { icon, esc, api: ctx.api }); else box.replaceChildren();
  loadSrvActs();
  // Dienste-Widgets: einmal einhaengen (sie halten sich selbst aktuell), nicht bei jedem Neuzeichnen der Seite
  const wbox = $('#mWidgets');
  if (wbox && mods.services && !wbox.querySelector('.wd-grid')) mm.mountServices(wbox, { api: ctx.api, wireSheet, icon, esc });
  else if (wbox && !mods.services) wbox.replaceChildren();
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
/** Die Hosts als Karten mit Zustand, Art des Agenten, Kennzahlen, Hinweisen und Verbindungen (docs/HOSTS.md, Karte: hostview.js); der Agent wird je Host eingerichtet */
function hostsHtml() {
  ctx.info.ensure(hosts());              // nur die Seite Server fragt nach Systemwerten und Updates (hoechstens alle 55 s), sonst niemand
  const cards = hosts().map((h) => hostCardHtml(h, ctx.info.get(h.id), { esc, connection: byId })).join('');
  return `<div class="m3-sec">${icon('dns')}<span>${t('mobile.hosts')}</span><span class="sp"></span><button type="button" class="mb m3-btn text m3-state" data-act="addhost">${icon('add')}${t('mobile.addHost')}</button></div>${cards ? `<div class="hu-grid">${cards}</div>` : `<p class="empty-hint">${t('mobile.noHost')}</p>`}`;
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

/* =====================================================================
 * Profil: die Einstellungen als Gruppen (Aufbau und Texte in settings.js, dieselben Gruppen wie am Desktop)
 * ===================================================================== */
const seg = (id, opts, val) => `<div class="m3-seg" role="radiogroup" id="${id}">${opts.map(([v, l]) => `<button type="button" class="mb m3-state" role="radio" aria-checked="${v === val}" data-v="${v}"><span class="m3-seg-check">${icon('check')}</span>${l}</button>`).join('')}</div>`;
function curSeed() { const s = readStore('seed', DEFAULT_SEED); return /^#[0-9a-f]{6}$/i.test(s) ? s.toLowerCase() : DEFAULT_SEED; }
let about = null;                                           // Antwort von /api/about (einmal je Besuch der Seite geladen)
const toneDot = (tone) => (tone === 'ok' ? 'ok' : tone === 'error' ? 'bad' : '');

/** Eine Zeile: Beschriftung, kurze Erklaerung und das eine Bedienelement rechts (Auswahl und Farben stehen darunter) */
function settingRow(r, ab) {
  const label = t(r.label); let help = t(r.help), ctl = '', stack = false;
  switch (r.id) {
    case 'user': ctl = `<span class="rv">${esc(user)}</span>`; break;
    case 'scheme': stack = true; ctl = seg('mScheme', SCHEMES.map(([v]) => [v, t(SCHEME_LABEL[v])]), normalizeScheme(readStore('scheme', 'auto'))); break;
    case 'lang': stack = true; ctl = seg('mLang', LANG_CHOICES.map(([v, l]) => [v, v === 'system' ? t('lang.system') : l]), getPref()); break;
    case 'color': stack = true; ctl = `<div class="swatches2" role="radiogroup" aria-label="${esc(label)}" id="mSwatches">${SWATCHES.map(([h, n]) => `<button type="button" class="mb sw" role="radio" aria-checked="${h === curSeed()}" aria-label="${esc(t(n))}" data-seed="${h}" style="--c:${h}">${icon('check')}</button>`).join('')}</div>`; break;
    case 'backup': case 'backupCopy': {
      const info = r.id === 'backup' ? ab.backup : ab.copy;
      if (!info) return '';
      if (!info.next && !info.details) help = info.line;      // sonst steht der Satz in der Meldung unter der Gruppe
      ctl = `<span class="m3-chip status"><span class="m3-dot ${toneDot(info.tone)}"></span>${esc(info.chip)}</span>`; break;
    }
    case 'version': case 'build': case 'transfer': {
      const v = ab[r.id]; if (!v && r.optional) return '';
      ctl = `<span class="rv">${esc(v || t('mobile.loading'))}</span>`; break;
    }
    case 'licenses': ctl = `<a class="mb m3-btn tonal sm m3-state" href="/licenses.txt" target="_blank" rel="noopener" aria-label="${esc(label + ': ' + t(r.action))}">${esc(t(r.action))}</a>`; break;
    case 'source': if (!ab.repository) return ''; ctl = `<a class="mb m3-btn tonal sm m3-state" href="${esc(ab.repository)}" target="_blank" rel="noopener" aria-label="${esc(label + ': ' + t(r.action))}">${esc(t(r.action))}</a>`; break;
    default:
      if (r.ctl === 'button') ctl = `<button type="button" class="mb m3-btn ${r.danger ? 'danger' : 'tonal'} sm m3-state" data-set="${r.id}" aria-label="${esc(label + ': ' + t(r.action))}">${esc(t(r.action))}</button>`;
  }
  return `<div class="m3-row${stack ? ' stack' : ''}"><div class="rt">${esc(label)}<small>${esc(help)}</small></div>${ctl ? `<div class="rc">${ctl}</div>` : ''}</div>`;
}
/** Unter einer Backup-Gruppe: was zu tun ist, der Rohtext des Servers nur hinter "Details" */
function backupNotes(ab) {
  return [ab.backup, ab.copy].filter((i) => i && (i.next || i.details)).map((i) => `<div class="m3-msg ${i.tone === 'info' ? '' : i.tone}" role="${i.tone === 'error' ? 'alert' : 'status'}" style="margin-top:var(--m3-sp-2)"><span class="msg-i">${icon('info')}</span><div class="msg-t"><b>${esc(i.line)}</b>${i.next ? `<span>${esc(i.next)}</span>` : ''}${i.details ? `<details><summary>${t('set.details')}</summary><pre>${esc(i.details)}</pre></details>` : ''}</div></div>`).join('');
}
function renderProfile() {
  const pg = $('#pg-profile'), top = pg.scrollTop, ab = aboutValues(about);
  const groups = GROUPS.map((g) => {
    const rows = rowsFor(g, 'phone').map((r) => settingRow(r, ab)).filter(Boolean);
    if (!rows.length) return '';
    return `<section class="${g.danger ? 'm3-danger' : ''}" aria-labelledby="set-${g.id}"><h2 class="m3-sec" id="set-${g.id}">${esc(t(g.title))}</h2><p class="m3-help">${esc(t(g.help))}</p><div class="m3-card m3-rows">${rows.join('')}</div>${g.id === 'backup' ? backupNotes(ab) : ''}</section>`;
  }).join('');
  pg.innerHTML = `<div class="m3-h" data-head><h1>${t('mobile.profile')}</h1><p>${t('mobile.profileSub')}</p></div>${groups}`;
  pg.scrollTop = top;
  const on = (id, fn) => { const e = $(id); if (e) e.addEventListener('click', fn); };
  on('#mScheme', (e) => { const b = e.target.closest('[data-v]'); if (!b) return; setTheme({ scheme: b.dataset.v }); applyShellTheme(); renderProfile(); });
  on('#mLang', (e) => { const b = e.target.closest('[data-v]'); if (b && b.dataset.v !== getPref()) setLanguage(b.dataset.v); });
  on('#mSwatches', (e) => { const b = e.target.closest('[data-seed]'); if (!b) return; setTheme({ seed: b.dataset.seed }); applyShellTheme(); renderProfile(); });
  pg.querySelectorAll('[data-set]').forEach((b) => b.addEventListener('click', () => settingAction(b.dataset.set)));
  if (current === 'profile' && !about) loadAbout();
}
const SETTING_ACTIONS = { password: () => ctx.account(), devices: () => ctx.devices(), folders: () => ctx.folders(), nettools: () => ctx.nettools(), signout: () => ctx.signOut(),
  claude: () => launchProgram('claude'), codex: () => launchProgram('codex') };
function settingAction(id) { const f = SETTING_ACTIONS[id]; if (f) f(); }
/** Claude oder Codex starten (wie der Schnellstart auf der Verbindungen-Seite) */
function launchProgram(program) {
  ctx.program(program, (list) => { view.kind = 'terminal'; view.q = ''; view.folder = 'all'; view.host = ''; go('services'); snack(t('prog.pickTile', { program: t(program === 'codex' ? 'prog.codex' : 'prog.claude') })); return list; });
}
async function loadAbout() {
  if (!ctx) return;
  try { about = await ctx.api('/api/about'); if (current === 'profile') renderProfile(); }
  catch (e) { snack(e.message); }
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
  else if (act === 'quickq') ctx.quick(view.q);
  else if (act === 'addhost') ctx.addHost();
  else if (act === 'hostedit' || act === 'hostagent') { const h = hosts().find((x) => x.id === a.dataset.host); if (h) ctx.editHost(h); }
  else if (act === 'hostinfo') { const h = hosts().find((x) => x.id === a.dataset.host); if (h) ctx.hostInfo(h, a.dataset.hpage || null); }
  else if (act === 'hostaction') { const h = hosts().find((x) => x.id === a.dataset.host); if (h) ctx.hostAction(h, a.dataset.hcact, a.dataset.htarget || null, { onDone: () => ctx.info.load(h, { fresh: true }) }); }
  else if (act === 'mod') openModSheet(a.dataset.mod);
  else if (act === 'allacts') { view.allActs = !view.allActs; renderHome(); }
  else if (act === 'last') { const c = lastConnection(); if (c) ctx.connect(c); }
  else if (act === 'filter') filterSheet();
  else if (act === 'rmfilter') { const k = a.dataset.key; if (k === 'kind') view.kind = 'all'; else if (k === 'folder') view.folder = 'all'; else view.host = ''; renderServices(); }
  else if (act === 'resetfilters') { view.kind = 'all'; view.folder = 'all'; view.host = ''; view.q = ''; const q = $('#mSearch'); if (q) q.value = ''; renderServices(); }
  else if (act === 'prog') launchProgram(a.dataset.prog);
}

/** Menue zu einer Verbindung (lange tippen): verbinden, Favorit, bearbeiten */
function actionSheet(c) {
  if (!c) return;
  let dlg = $('#mActDlg');
  if (!dlg) { dlg = document.createElement('dialog'); dlg.id = 'mActDlg'; dlg.className = 'm3sheet'; $('#app').appendChild(dlg); wireSheet(dlg); }
  const fav = !!c.favorite;
  dlg.innerHTML = `<h2>${esc(c.name)}</h2><p class="hint">${esc(connKindText(c))} · ${esc(c.username ? c.username + '@' : '')}${esc(c.host)}:${c.port}${c.hasPassword ? ' · ' + esc(t('shell.passwordSaved')) : ''}</p>
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
  for (const id of ['connDlg', 'hostDlg', 'hostActDlg', 'startDlg', 'quickDlg', 'folderDlg', 'devicesDlg', 'accountDlg', 'netDlg']) { const d = $('#' + id); if (!d) continue; d.classList.add('m3sheet'); wireSheet(d); }
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
