/* =====================================================================
 * Dateien-App als Explorer (docs/FILES-EXPLORER.md, docs/FILES.md): die Oberflaeche fuer Desktop-Fenster und Handy-Seite. Orte: der gemeinsame Ordner des Servers und jeder Host mit SSH-Zugang
 * (und "<Host> (Root)", wo der Agent es erlaubt). Einfachklick waehlt, Doppelklick oeffnet; Auswahl, Tastatur, Buchstabensprung, Rechtsklick-Menue, Ziehen, Zwischenablage; Seitenleiste als Baum
 * mit angehefteten Ordnern; ein einfacher Editor. Jede schreibende Aktion geht durch den Bestaetigungsdialog des Servers (eine Bestaetigung je Aktion, nie je Datei).
 * Die reine Logik steht in files-core.js und files-explorer.js. Alles, was von Server oder Ziel kommt (Namen, Pfade, Text), wird mit textContent gesetzt, nie als HTML.
 * deps: { api, kit, getState, notify }
 * ===================================================================== */
import { t, tp, tk } from './i18n.js';
import * as C from './files-core.js';
import * as X from './files-explorer.js';
import { h, icon, modal, confirmDialog, inputDialog, progressDialog, btn, rowsEl, noteEl } from './files-ui.js';
import { openMenu, closeMenu } from './files-menu.js';
import { openEditor } from './files-editor.js';
import { renderTree } from './files-tree.js';

export { icon, h, modal, confirmDialog, inputDialog, progressDialog };

/* Gemeinsam fuer alle Fenster dieser Seite: die Zwischenablage (nur Pfade) und das laufende Ziehen */
let clip = null;          // { mode: 'copy'|'cut', place: Kennung, placeObj, paths }
let drag = null;          // { kind: 'files', place, paths } oder { kind: 'pin', pin }
let uid = 0;

const PREF_LS = 'pd.files.prefs';
const REOPEN = ['password_required', 'password_unreadable', 'host_key_new', 'host_key_changed', 'connection_lost'];

export function mountFiles(box, deps) {
  const { api, kit = false, getState = () => ({ connections: [], hosts: [] }), notify = () => {} } = deps;
  const me = ++uid;
  const fapi = C.makeFilesApi(api, deps.createXhr ? { createXhr: deps.createXhr } : undefined);
  let info = { local: false, write: false, maxBytes: 0 };
  let places = C.buildPlaces({ info });
  const st = { place: null, path: '', entries: [], shown: [], byPath: new Map(), truncated: false, hidden: 0, sel: new X.ListSelection(), back: [], fwd: [], seq: 0, query: '', filter: '', home: new Map(), dead: false, multi: false, rows: new Map(), loaded: false, edit: false, pendingSelect: null };
  const store = { get: () => { try { return localStorage.getItem('pd.files.place'); } catch { return null; } }, set: (v) => { try { localStorage.setItem('pd.files.place', v); } catch { /* egal */ } } };
  const say = (text) => notify(text);

  /* ----- Einstellungen: Ansicht, Pins, zugeklappte Knoten. Lokal als Rueckfall, auf dem Server pro Benutzer (Einstellung "files") ----- */
  let prefs = X.normalizePrefs((() => { try { return JSON.parse(localStorage.getItem(PREF_LS)); } catch { return null; } })());
  let saveTimer = null, dirty = false;
  const flushPrefs = () => { clearTimeout(saveTimer); if (!dirty) return; dirty = false; api('/api/settings/files', { method: 'PUT', body: { value: prefs } }).catch(() => { dirty = true; }); };
  function savePrefs() { try { localStorage.setItem(PREF_LS, JSON.stringify(prefs)); } catch { /* egal */ } dirty = true; clearTimeout(saveTimer); saveTimer = setTimeout(flushPrefs, 600); }
  async function loadPrefs() {
    try {
      const r = await api('/api/settings');
      if (st.dead) return;
      if (r && r.settings && r.settings.files) { prefs = X.normalizePrefs(r.settings.files); try { localStorage.setItem(PREF_LS, JSON.stringify(prefs)); } catch { /* egal */ } applyView(); renderSide(); if (st.place) { applyList(); renderList(); } }
      else if (prefs.pins.length || JSON.stringify(prefs.view) !== JSON.stringify(X.DEFAULT_VIEW)) savePrefs();   // erster Aufruf: der Server bekommt, was dieses Geraet hat
    } catch { /* Rueckfall: die lokale Kopie */ }
  }

  /* ----- Aufbau ----- */
  box.replaceChildren();
  const root = h('div', { class: 'fapp' + (kit ? ' fkit' : ' fdesk') });
  const side = h('nav', { class: 'f-side', 'aria-label': t('desktopFiles.places') });
  const main = h('div', { class: 'f-main' });
  const placeBtn = h('button', { type: 'button', class: 'f-place ' + (kit ? 'm3-btn tonal mb m3-state' : 'dk-btn'), 'aria-haspopup': 'dialog' });
  // Am Desktop nimmt ein Mausklick auf eine Taste der Leiste der Liste den Fokus nicht weg (wie im Explorer); Tab und Enter gehen weiter
  const keepFocus = kit ? {} : { onmousedown: (e) => e.preventDefault() };
  const tb = (name, label, onclick, extra = {}) => h('button', { type: 'button', class: 'f-tool ' + (kit ? 'm3-iconbtn m3-state mb' : ''), title: label, 'aria-label': label, onclick, ...keepFocus, ...extra }, icon(name));
  const tl = (name, label, onclick, extra = {}) => (kit ? tb(name, label, onclick, extra) : h('button', { type: 'button', class: 'f-tbtn', title: label, onclick, ...keepFocus, ...extra }, icon(name), h('span', { class: 'f-lbl', text: label }), extra['aria-haspopup'] ? icon('more') : null));
  const btnSide = tb('sidebar', t('files.sidebar.toggle'), () => setSide({ off: !side$.off }), { 'aria-pressed': 'true' });
  const btnBack = tb('back', t('common.back'), () => goBack()), btnFwd = tb('forward', t('files.forward'), () => goForward()), btnUp = tb('up', t('desktopFiles.up'), () => goUp()), btnRef = tb('refresh', t('files.refresh'), () => reload());
  const addr = h('div', { class: 'f-addr', role: 'group', 'aria-label': t('desktopFiles.path') });
  const searchBox = h('label', { class: 'f-search' }, icon('search'), h('input', { type: 'text', autocomplete: 'off', spellcheck: 'false', 'aria-label': t('desktopFiles.searchByName') }));
  const searchInput = searchBox.querySelector('input');
  const btnView = tl('list', t('files.menu.view'), () => viewMenu(btnView), { 'aria-haspopup': 'menu' });
  const btnNew = tl('add', t('files.menu.new'), () => newMenu(btnNew), { 'aria-haspopup': 'menu' });
  const btnUpload = tl('upload', t('files.upload.action'), () => fileInput.click());
  const btnHist = tb('history', t('files.history'), () => showHistory());
  const bar = h('div', { class: 'f-bar' }, h('div', { class: 'f-nav' }, kit ? null : [btnSide, btnBack, btnFwd], btnUp, btnRef), kit ? placeBtn : null, addr, searchBox, h('div', { class: 'f-acts' }, btnView, btnNew, btnUpload, btnHist));
  const banner = h('div', { class: 'f-banner', role: 'status' });
  const selbar = h('div', { class: 'f-selbar', hidden: true, role: 'toolbar', 'aria-label': t('files.selection') });
  const view = h('div', { class: 'f-view' });
  const statL = h('span', { class: 'f-stat-l', 'aria-live': 'polite' }), statR = h('span', { class: 'f-stat-r' });
  const stat = h('div', { class: 'f-stat' }, statL, statR);
  const fileInput = h('input', { type: 'file', multiple: true, hidden: true });
  const drop = h('div', { class: 'f-drop', hidden: true }, icon('upload'), h('span', { text: t('files.dropHere') }));
  main.append(h('div', { class: 'f-top' }, bar, banner), view, selbar, stat, drop, fileInput);
  const sideGrip = h('div', { class: 'f-sidegrip', role: 'separator', 'aria-orientation': 'vertical', 'aria-label': t('files.sidebar.resize'), tabindex: '0' });
  root.append(side, main, ...(kit ? [] : [sideGrip]));
  box.appendChild(root);

  /* ----- Seitenleiste (Desktop): Breite ziehbar, einklappbar; beides pro Geraet gemerkt (localStorage, nicht auf dem Server) ----- */
  const SIDE_LS = 'pd.files.side';
  let side$ = X.normalizeSide((() => { try { return JSON.parse(localStorage.getItem(SIDE_LS)); } catch { return null; } })());
  function setSide(patch, save = true) {
    side$ = X.normalizeSide({ ...side$, ...patch });
    root.style.setProperty('--fside', side$.off ? '0px' : side$.w + 'px'); root.classList.toggle('side-off', side$.off);
    btnSide.setAttribute('aria-pressed', String(!side$.off));
    if (save) { try { localStorage.setItem(SIDE_LS, JSON.stringify(side$)); } catch { /* egal */ } }
  }
  if (!kit) {
    setSide({}, false);
    sideGrip.addEventListener('pointerdown', (e) => {
      e.preventDefault(); sideGrip.setPointerCapture(e.pointerId); root.classList.add('side-drag');
      const x0 = e.clientX, w0 = side$.w;
      const move = (ev) => setSide({ w: w0 + ev.clientX - x0 }, false);
      const up = () => { sideGrip.removeEventListener('pointermove', move); sideGrip.removeEventListener('pointerup', up); sideGrip.removeEventListener('pointercancel', up); root.classList.remove('side-drag'); setSide({}); };
      sideGrip.addEventListener('pointermove', move); sideGrip.addEventListener('pointerup', up); sideGrip.addEventListener('pointercancel', up);
    });
    sideGrip.addEventListener('dblclick', () => setSide({ w: X.SIDE_DEFAULT }));
    sideGrip.addEventListener('keydown', (e) => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { e.preventDefault(); setSide({ w: side$.w + (e.key === 'ArrowRight' ? 16 : -16) }); } });
  }

  const canWrite = (p = st.place) => !!(p && !C.isLocal(p) && info.write);
  const writable = () => canWrite();
  const label = () => (st.place ? st.place.label : t('files.choosePlace'));
  const selEntries = () => st.sel.list().map((p) => st.byPath.get(p)).filter(Boolean);
  const noWriteTitle = () => (st.place && C.isLocal(st.place) ? t('files.localNote') : t('files.writeOff'));

  /* ----- Ansicht anwenden ----- */
  function applyView() {
    const v = prefs.view;
    root.classList.toggle('v-tiles', v.mode === 'tiles'); root.classList.toggle('v-comfy', v.density === 'comfy'); root.classList.toggle('v-hide', !v.showHidden);
    root.style.setProperty('--cn', v.cols.name + 'px'); root.style.setProperty('--cd', v.cols.date + 'px'); root.style.setProperty('--ct', v.cols.type + 'px'); root.style.setProperty('--cs', v.cols.size + 'px');
    btnView.replaceChildren(icon(v.mode === 'tiles' ? 'tiles' : 'list'), ...(kit ? [] : [h('span', { class: 'f-lbl', text: t('files.menu.view') }), icon('more')]));
  }
  const setView = (patch) => { prefs = { ...prefs, view: X.normalizeView({ ...prefs.view, ...patch }) }; savePrefs(); applyView(); if (st.place) { applyList(); renderList(); } };

  /* ----- Orte, Baum ----- */
  const refreshPlaces = () => { places = C.buildPlaces({ ...getState(), info }); renderSide(); renderNav(); };
  const homeOf = (id) => st.home.get(id);
  function treeOptions(onOpen) {
    return {
      places, pins: prefs.pins, closed: prefs.closed, current: { place: st.place, path: st.path }, home: homeOf, kit, onOpen,
      onToggle: (id) => { prefs = { ...prefs, closed: X.toggleClosed(prefs.closed, id) }; savePrefs(); renderSide(); },
      onMenu: (e, n) => treeMenu(e, n),
      dnd: kit ? null : { target: (el, get, pin) => dropTarget(el, get, pin), pin: (el, pin) => { el.addEventListener('dragstart', (e) => { drag = { kind: 'pin', pin }; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', ''); }); el.addEventListener('dragend', endDrag); } },
    };
  }
  function renderSide() {
    if (kit) return;
    renderTree(side, treeOptions((p, path) => enter(p, path)));
  }
  function pickPlace() {
    refreshPlaces();
    const holder = h('div', { class: 'f-picker' });
    const dlg = modal({ title: t('files.choosePlace'), kit, content: [holder], actions: [{ id: 'x', label: t('common.close'), kind: 'ghost' }] });
    const paint2 = () => renderTree(holder, { ...treeOptions((p, path) => { dlg.close(); enter(p, path); }), onToggle: (id) => { prefs = { ...prefs, closed: X.toggleClosed(prefs.closed, id) }; savePrefs(); paint2(); }, onMenu: (e, n) => { treeMenu(e, n, paint2, () => dlg.close()); } });
    paint2();
    if (!places.local && !places.groups.length) holder.replaceChildren(h('p', { class: 'f-empty', text: t('files.noPlaces') }));
  }
  function treeMenu(e, n, repaint = renderSide, done = () => {}) {
    if (n.type === 'host') { hostMenu(e, n, done); return; }
    if (n.type !== 'pin') return;
    const pin = n.pin, samePlace = prefs.pins.filter((p) => p.place === pin.place), at = samePlace.findIndex((p) => X.pinKey(p) === X.pinKey(pin));
    openMenu({ kit, x: e.clientX, y: e.clientY, label: X.safeName(n.label), items: [
      { id: 'open', label: t('files.menu.open'), icon: 'open', run: () => enter(n.place, pin.path) },
      { sep: true },
      { id: 'up', label: t('files.menu.moveUp'), icon: 'chevU', disabled: at <= 0, run: () => { prefs = { ...prefs, pins: X.movePin(prefs.pins, pin.place, pin.path, -1) }; savePrefs(); repaint(); } },
      { id: 'down', label: t('files.menu.moveDown'), icon: 'chevD', disabled: at >= samePlace.length - 1, run: () => { prefs = { ...prefs, pins: X.movePin(prefs.pins, pin.place, pin.path, 1) }; savePrefs(); repaint(); } },
      { sep: true },
      { id: 'unpin', label: t('files.menu.unpin'), icon: 'pin', run: () => { prefs = { ...prefs, pins: X.removePin(prefs.pins, pin.place, pin.path) }; savePrefs(); repaint(); renderSide(); } },
    ] });
  }
  /** Menue eines Hosts in der Seitenleiste: Home, Root und die Verbindungen mit anderer Anmeldung ("Als anderer Benutzer oeffnen") */
  function hostMenu(e, n, done) {
    const g = n.host; if (!g) return;
    const run = (p) => () => { done(); enter(p); };
    const items = g.places.map((p) => ({ id: 'p-' + p.id, label: C.isRoot(p) ? t('files.tree.root') : t('files.tree.home'), icon: C.isRoot(p) ? 'root' : 'home', run: run(p) }));
    if (g.others && g.others.length) items.push({ sep: true }, { heading: t('files.menu.asOther') }, ...g.others.map((p) => ({ id: 'o-' + p.id, label: p.user ? `${p.user} (${p.name})` : p.name, icon: 'dns', run: run(p) })));
    openMenu({ kit, x: e.clientX, y: e.clientY, label: X.safeName(g.name), items });
  }
  const hostIdOf = (place) => { const g = places.groups.find((x) => [...x.places, ...(x.others || [])].some((p) => p.id === place.id)); return g ? g.id : ''; };
  function pinHere(place, path, name) {
    const nm = name || C.nameOf(path) || '/';
    const open = hostIdOf(place) ? 'host:' + hostIdOf(place) : 'local';
    prefs = { ...prefs, pins: X.addPin(prefs.pins, { place: place.id, path, name: nm }), closed: prefs.closed.filter((x) => x !== open) };
    savePrefs(); renderSide(); say(t('files.pin.done', { name: X.safeName(nm) }));
  }
  const unpinHere = (place, path) => { prefs = { ...prefs, pins: X.removePin(prefs.pins, place.id, path) }; savePrefs(); renderSide(); };
  const pinned = (place, path) => X.isPinned(prefs.pins, place.id, path);

  /* ----- Oeffnen eines Orts: Passwort und Host-Schluessel nur mit ausdruecklicher Bestaetigung ----- */
  const ask = { password: (place, wrong, host) => passwordDialog(kit, place, wrong, host), hostKey: (place, data, changed) => hostKeyDialog(kit, place, data, changed) };
  async function ensureOpen(place) {
    if (C.isLocal(place)) return null;
    const r = await C.openPlace(fapi, place, ask);
    st.home.set(place.id, r.home);
    if (r.hostKey === 'remembered') say(t('files.keyRemembered', { host: place.label }));
    if (r.hostKey === 'replaced') say(t('files.keyReplaced', { host: place.label }));
    return r;
  }
  const keepAlive = setInterval(() => { if (st.place && !C.isLocal(st.place) && !document.hidden && !st.dead) fapi.open(st.place.id).catch(() => {}); }, 30000);
  /** Ruft etwas auf; ist die Sitzung inzwischen zu (Leerlauf, Abriss), wird der Ort neu geoeffnet (mit Abfrage) und einmal wiederholt */
  async function guarded(fn, place = st.place) {
    try { return await fn(); } catch (e) {
      if (place && !C.isLocal(place) && REOPEN.includes(e.code)) { await ensureOpen(place); return fn(); }
      throw e;
    }
  }
  async function openQuiet(place) { try { await ensureOpen(place); return true; } catch (e) { if (!C.isCancelled(e)) say(C.errorMessage(e)); return false; } }

  async function enter(place, path) {
    if (place.id !== (st.place && st.place.id)) { st.back = []; st.fwd = []; st.loaded = false; st.path = ''; st.entries = []; st.shown = []; st.sel = new X.ListSelection(); }
    if (!(await openQuiet(place))) return;
    st.place = place; store.set(place.id); st.query = ''; st.filter = ''; searchInput.value = '';
    renderNav(); renderSide();
    await load(path !== undefined ? path : C.isLocal(place) ? '' : (st.home.get(place.id) || '/'));
    focusList();
  }

  /* ----- Liste laden und zeigen ----- */
  async function load(path, { record = true, quiet = false } = {}) {
    const my = ++st.seq;
    view.classList.add('busy');
    try {
      const l = await guarded(() => fapi.list(st.place, path));
      if (my !== st.seq) return false;
      if (record && st.loaded && st.path !== l.path) { st.back.push(st.path); st.fwd = []; }
      if (st.path !== l.path) { st.sel = new X.ListSelection(); st.filter = ''; searchInput.value = ''; }
      st.path = l.path; st.entries = l.entries; st.truncated = l.truncated; st.hidden = l.hidden; st.loaded = true; st.query = '';
      applyList(); renderNav(); renderSide(); renderList();
      if (st.pendingSelect && st.byPath.has(st.pendingSelect)) { st.sel.jump(st.pendingSelect); paint(); scrollToFocus(); }
      st.pendingSelect = null;
      return true;
    } catch (e) {
      if (my !== st.seq) return false;
      if (C.isCancelled(e)) { renderList(); return false; }
      if (quiet) { say(C.errorMessage(e)); return false; }
      showFailure(C.errorMessage(e), () => load(path, { record }));
      return false;
    } finally { if (my === st.seq) view.classList.remove('busy'); }
  }
  const reload = async () => { if (st.place) { await load(st.path, { record: false }); focusList(); } };
  function applyList() {
    let items = st.entries;
    if (st.filter) { const q = st.filter.toLowerCase(); items = items.filter((e) => e.name.toLowerCase().includes(q)); }
    st.shown = X.visibleEntries(items, { showHidden: prefs.view.showHidden, sort: { col: prefs.view.sortCol, dir: prefs.view.sortDir } });
    st.byPath = new Map(st.entries.map((e) => [e.path, e]));
    st.sel.setPaths(st.shown.map((e) => e.path));
  }
  function showFailure(text, retry) {
    const plan = focusPlan();
    // Der Fehlerblock nimmt den Fokus auf, damit Zurueck, Hoch und F5 auch hier gelten (die Liste gibt es dann nicht)
    const fail = h('div', { class: 'f-empty f-fail', role: 'alert', tabindex: '-1' }, h('p', { text }), btn(t('files.retry'), kit ? 'm3-btn tonal mb m3-state' : 'dk-btn', retry));
    fail.addEventListener('keydown', (e) => { if (e.target === fail) onKey(e); });
    view.replaceChildren(fail); list = null; st.rows = new Map();
    statL.textContent = ''; statR.textContent = '';
    if (!kit && (plan.list || plan.settle)) { const w = box.closest('.dk-win'); if (!w || w.classList.contains('active')) fail.focus({ preventScroll: true }); }
  }

  /* ----- Werkzeugleiste und Adressleiste ----- */
  function renderNav() {
    placeBtn.replaceChildren(icon(st.place && C.isRoot(st.place) ? 'root' : st.place && !C.isLocal(st.place) ? 'dns' : 'folder'), h('span', { class: 'f-place-t', text: X.safeName(label()) }), icon('more'));
    placeBtn.onclick = pickPlace;
    const has = !!st.place, w = canWrite();
    root.classList.toggle('f-has-place', has);
    btnBack.disabled = !st.back.length; btnFwd.disabled = !st.fwd.length; btnUp.disabled = !has || !st.path || st.path === '/'; btnRef.disabled = !has;
    btnNew.disabled = !w; btnUpload.disabled = !w; btnHist.hidden = !has || C.isLocal(st.place); btnView.disabled = !has;
    btnNew.title = !w && has ? noWriteTitle() : t('files.menu.new'); btnUpload.title = !w && has ? noWriteTitle() : t('files.upload.action');
    searchBox.hidden = !has;
    searchInput.placeholder = C.isLocal(st.place) ? t('desktopFiles.searchIn', { name: X.safeName(C.nameOf(st.path)) || t('desktopFiles.root') }) : t('files.filterIn', { name: X.safeName(C.nameOf(st.path)) || '/' });
    banner.replaceChildren();
    root.classList.toggle('f-is-root', C.isRoot(st.place));
    if (C.isRoot(st.place)) banner.append(h('div', { class: 'f-rootbar', role: 'note' }, icon('root'), h('span', { text: t('files.root.banner', { host: st.place.name }) })));
    if (has && !C.isLocal(st.place) && !info.write) banner.append(noteEl(t('files.writeOff'), 'hint'));
    banner.hidden = !banner.childNodes.length;
    renderAddr();
  }
  /** Der Pfad als klickbare Teile (jeder Teil ist auch ein Ziel zum Ablegen); ein Klick daneben macht ihn zum Textfeld */
  function renderAddr() {
    addr.classList.remove('editing'); addr.replaceChildren();
    if (!st.place) { addr.append(h('span', { class: 'f-addr-hint', text: t('files.choosePlace') })); return; }
    const parts = C.crumbsOf(st.path, st.place);
    const seg = (c, i, text, ic) => {
      const b = h('button', { type: 'button', class: 'f-crumb' + (i === parts.length - 1 ? ' cur' : ''), 'aria-current': i === parts.length - 1 ? 'location' : undefined, onclick: (e) => { e.stopPropagation(); go(c.path); }, ...keepFocus }, ic ? icon(ic) : null, h('span', { text: X.safeName(text) }));
      if (!kit) dropTarget(b, () => ({ place: st.place, dir: c.path }));
      return b;
    };
    parts.forEach((c, i) => {
      if (i) addr.append(icon('chevR'));
      addr.append(seg(c, i, i === 0 ? (kit ? '/' : label()) : c.label, i === 0 && !kit ? (C.isRoot(st.place) ? 'root' : C.isLocal(st.place) ? 'folder' : 'dns') : null));
    });
    addr.scrollLeft = addr.scrollWidth;
  }
  function editAddr() {
    if (!st.place || addr.classList.contains('editing')) return;
    const local = C.isLocal(st.place);
    const input = h('input', { type: 'text', class: 'f-addr-in', value: local ? '/' + st.path : st.path, autocomplete: 'off', spellcheck: 'false', autocapitalize: 'off', 'aria-label': t('files.addr.label') });
    addr.classList.add('editing'); addr.replaceChildren(input);
    let done = false;
    const finish = async (ok) => { if (done) return; done = true; const v = input.value; renderAddr(); if (ok && v.trim() !== st.path) await load(X.parseTypedPath(v, local), { quiet: true }); focusList(); };
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); finish(true); } else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(false); } });
    input.addEventListener('blur', () => finish(false));
    input.focus(); input.select();
  }
  addr.addEventListener('click', (e) => { if (e.target === addr) editAddr(); });

  async function go(path) { if (st.place) { await load(path); focusList(); } }
  const goUp = () => { if (!st.place || !st.path || st.path === '/') return undefined; st.pendingSelect = st.path; return go(C.parentOf(st.path, st.place)); };   // wie im Explorer: der Ordner, aus dem man kommt, ist danach gewaehlt
  async function goBack() { if (!st.back.length) return; const to = st.back.pop(); const was = st.path; if (await load(to, { record: false })) st.fwd.push(was); else st.back.push(to); renderNav(); focusList(); }
  async function goForward() { if (!st.fwd.length) return; const to = st.fwd.pop(); const was = st.path; if (await load(to, { record: false })) st.back.push(was); else st.fwd.push(to); renderNav(); focusList(); }

  /* ----- Menues Ansicht und Neu ----- */
  function viewMenu(anchor) {
    const v = prefs.view, r = (id, text, on, patch) => ({ id, label: text, checked: on, radio: true, run: () => setView(patch) });
    openMenu({ kit, anchor, label: t('files.menu.view'), items: [
      r('list', t('files.view.list'), v.mode === 'list', { mode: 'list' }), r('tiles', t('files.view.tiles'), v.mode === 'tiles', { mode: 'tiles' }),
      { sep: true },
      r('compact', t('files.view.compact'), v.density === 'compact', { density: 'compact' }), r('comfy', t('files.view.comfy'), v.density === 'comfy', { density: 'comfy' }),
      { sep: true }, { heading: t('files.view.sortBy') },
      ...X.SORT_COLS.map((c) => r('s-' + c, colName(c), v.sortCol === c, { sortCol: c })),
      { sep: true },
      r('asc', t('files.view.asc'), v.sortDir === 'asc', { sortDir: 'asc' }), r('desc', t('files.view.desc'), v.sortDir === 'desc', { sortDir: 'desc' }),
      { sep: true },
      { id: 'hidden', label: t('files.view.hidden'), checked: v.showHidden, run: () => setView({ showHidden: !v.showHidden }) },
    ] });
  }
  const COL_KEYS = { name: tk('files.col.name'), date: tk('files.col.date'), type: tk('files.col.type'), size: tk('files.col.size') };
  const colName = (c) => t(COL_KEYS[c]);
  const ctrl = () => t('files.menu.ctrl');
  function newItems() {
    const w = canWrite(), tip = w ? undefined : noWriteTitle();
    return [
      { id: 'newfolder', label: t('files.newFolder'), icon: 'newfolder', shortcut: kit ? '' : `${ctrl()}+Shift+N`, disabled: !w, title: tip, run: () => newFolder() },
      { id: 'newfile', label: t('files.menu.newFile'), icon: 'newfile', disabled: !w, title: tip, run: () => newFile() },
    ];
  }
  const newMenu = (anchor) => openMenu({ kit, anchor, label: t('files.menu.new'), items: newItems() });

  /* ----- Die Liste ----- */
  let list = null;
  const TYPE_KEYS = { 'files.type.dir': tk('files.type.dir'), 'files.type.file': tk('files.type.file'), 'files.type.ext': tk('files.type.ext'), 'files.type.link': tk('files.type.link'), 'files.type.other': tk('files.type.other') };
  const typeText = (e) => { const k = X.typeKey(e); return t(TYPE_KEYS[k.key], { ext: k.ext }); };
  const iconOf = (e) => { const k = X.kindOf(e); return k === 'dir' ? 'folder' : k === 'link' ? 'link' : k === 'image' ? 'image' : k === 'code' ? 'code' : k === 'archive' ? 'archive' : k === 'text' ? 'file' : 'blank'; };
  function row(e, i) {
    const k = X.kindOf(e), name = X.safeName(e.name);
    const ic = h('span', { class: 'f-ic-wrap k-' + k }, icon(iconOf(e)));
    const el = h('div', { class: 'f-row' + (e.openable ? ' dir' : '') + (X.isHiddenName(e.name) ? ' hid' : ''), role: 'option', id: `fr${me}-${i}`, 'aria-selected': 'false', 'data-path': e.path, draggable: !kit && st.place && !C.isLocal(st.place) ? 'true' : undefined },
      kit
        ? [ic, h('span', { class: 'f-txt' }, h('span', { class: 'f-name', text: name }), h('span', { class: 'f-meta', text: C.entryMeta(e) }))]
        : [h('span', { class: 'c c-name' }, ic, h('span', { class: 'f-name', text: name, title: name })), h('span', { class: 'c c-date', text: e.modified ? C.fmtModified(e.modified) : '' }), h('span', { class: 'c c-type', text: typeText(e) }), h('span', { class: 'c c-size', text: e.kind === 'file' || (e.kind === 'link' && e.resolved === 'file') ? C.fileSize(e.size) : '' })]);
    if (!kit && e.openable) dropTarget(el, () => ({ place: st.place, dir: e.path }));
    return el;
  }
  function renderHeader() {
    const v = prefs.view;
    // Die Koepfe sind sortierbare Spaltenkoepfe (flacher Text), keine Tasten: Klick, Enter oder Leertaste sortieren
    const hd = h('div', { class: 'f-cols', role: 'row' }, X.SORT_COLS.map((c) => {
      const on = v.sortCol === c, sort = () => setView(X.nextSort(prefs.view, c));
      const b = h('div', { class: 'f-col' + (on ? ' on' : ''), role: 'columnheader', tabindex: '0', 'data-col': c, 'aria-sort': on ? (v.sortDir === 'asc' ? 'ascending' : 'descending') : 'none', onclick: sort,
        onkeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); e.stopPropagation(); sort(); } } }, h('span', { class: 'f-col-t', text: colName(c) }), on ? icon(v.sortDir === 'asc' ? 'chevU' : 'chevD') : null);
      const grip = h('span', { class: 'f-grip', role: 'separator', 'aria-orientation': 'vertical', 'aria-label': t('files.col.resize', { name: colName(c) }), tabindex: '0', 'data-col': c });
      return h('div', { class: 'f-colw c-' + c }, b, grip);
    }));
    for (const grip of hd.querySelectorAll('.f-grip')) wireGrip(grip);
    return hd;
  }
  function wireGrip(grip) {
    const c = grip.dataset.col, v0 = { name: '--cn', date: '--cd', type: '--ct', size: '--cs' }[c];
    const set = (w, save) => { const px = Math.max(X.COL_MIN, Math.min(X.COL_MAX, Math.round(w))); root.style.setProperty(v0, px + 'px'); if (save) { prefs = { ...prefs, view: X.normalizeView({ ...prefs.view, cols: { ...prefs.view.cols, [c]: px } }) }; savePrefs(); } return px; };
    grip.addEventListener('pointerdown', (e) => {
      e.preventDefault(); e.stopPropagation(); grip.setPointerCapture(e.pointerId);
      const x0 = e.clientX, w0 = prefs.view.cols[c]; let w = w0;
      const move = (ev) => { w = set(w0 + ev.clientX - x0, false); };
      const up = () => { grip.removeEventListener('pointermove', move); grip.removeEventListener('pointerup', up); grip.removeEventListener('pointercancel', up); set(w, true); };
      grip.addEventListener('pointermove', move); grip.addEventListener('pointerup', up); grip.addEventListener('pointercancel', up);
    });
    grip.addEventListener('click', (e) => e.stopPropagation());
    grip.addEventListener('keydown', (e) => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { e.preventDefault(); e.stopPropagation(); set(prefs.view.cols[c] + (e.key === 'ArrowRight' ? 12 : -12), true); } });
  }
  /** Wohin der Fokus nach dem Neuzeichnen soll: in die Liste (war er dort, oder nirgends), auf denselben Spaltenkopf (war er dort), sonst bleibt er, wo er ist */
  function focusPlan() {
    const a = document.activeElement;
    if (a && a.classList && a.classList.contains('f-col') && view.contains(a)) return { col: a.dataset.col };
    if (view.contains(a)) return { list: true };
    if (!a || a === document.body || !a.isConnected) return { settle: true };
    return {};
  }
  /** Bringt den Fokus nach einer Navigation zurueck in die Liste, wenn er sonst verloren waere (kein Fenster der Seite hat ihn) und dieses Fenster vorn ist */
  function settleFocus() {
    if (st.dead || kit) return;
    const w = box.closest('.dk-win');
    if (w && !w.classList.contains('active')) return;
    const a = document.activeElement;
    if (!a || a === document.body || !a.isConnected) focusList();
  }
  function renderList() {
    const plan = focusPlan();
    view.replaceChildren(); st.rows = new Map(); list = null;
    if (!st.place) {
      view.append(h('div', { class: 'f-empty' }, h('p', { text: t('files.chooseHint') }), kit ? h('button', { type: 'button', class: 'm3-btn tonal mb m3-state', onclick: pickPlace }, t('files.choosePlace')) : null));
      statL.textContent = ''; statR.textContent = ''; return;
    }
    const tiles = prefs.view.mode === 'tiles';
    list = h('div', { class: 'f-list' + (tiles ? ' tiles' : ''), role: 'listbox', 'aria-multiselectable': 'true', 'aria-label': t('files.listLabel', { name: X.safeName(C.nameOf(st.path)) || label() }), tabindex: '0' });
    st.shown.forEach((e, i) => { const el = row(e, i); st.rows.set(e.path, el); list.append(el); });
    if (!st.shown.length) list.append(h('div', { class: 'f-empty', role: 'presentation', text: st.filter ? t('desktopFiles.noMatch', { text: st.filter }) : st.query ? t('desktopFiles.noMatch', { text: st.query }) : t('desktopFiles.empty') }));
    if (!kit && !tiles) view.append(renderHeader());
    view.append(list);
    wireList();
    paint();
    if (plan.col) { const hd = view.querySelector(`.f-col[data-col="${plan.col}"]`); if (hd) hd.focus({ preventScroll: true }); }
    else if (plan.list) focusList();
    else if (plan.settle) settleFocus();
  }
  function paint() {
    for (const [p, el] of st.rows) {
      const on = st.sel.has(p);
      el.classList.toggle('sel', on); el.setAttribute('aria-selected', String(on)); el.classList.toggle('foc', st.sel.focus === p); el.classList.toggle('cut', !!clip && clip.mode === 'cut' && !!st.place && clip.place === st.place.id && clip.paths.includes(p));
    }
    const f = st.sel.focus && st.rows.get(st.sel.focus);
    if (list) { if (f) list.setAttribute('aria-activedescendant', f.id); else list.removeAttribute('aria-activedescendant'); }
    renderStatus(); renderSelbar();
  }
  const scrollToFocus = () => { const f = st.sel.focus && st.rows.get(st.sel.focus); if (f) f.scrollIntoView({ block: 'nearest' }); };
  const focusList = () => { if (list && !kit) list.focus({ preventScroll: true }); };
  function renderStatus() {
    if (!st.place) return;
    const sel = selEntries();
    let line;
    if (sel.length) { const s = X.selectionStats(sel); line = tp('files.status.selected', sel.length) + (s.files ? ' · ' + C.fileSize(s.bytes) : ''); }
    else if (st.query) line = tp('desktopFiles.hits', st.entries.length);
    else {
      const dirs = st.shown.filter((e) => e.openable).length;
      line = tp('desktopFiles.items', st.shown.length);
      if (dirs) line = tp('desktopFiles.itemsDirs', dirs, { text: line });
      if (st.truncated) line = t('desktopFiles.itemsCut', { text: line, n: st.entries.length });
      if (st.hidden) line += ' · ' + tp('files.hidden', st.hidden);
    }
    statL.textContent = line;
    statR.textContent = st.place.user ? t('files.status.as', { place: X.safeName(st.place.label), user: st.place.user }) : X.safeName(st.place.label);
  }

  /* ----- Handy: untere Aktionsleiste fuer die Auswahl ----- */
  function renderSelbar() {
    const sel = selEntries();
    const show = kit && sel.length > 0;
    selbar.hidden = !show; selbar.replaceChildren(); root.classList.toggle('f-selecting', show);
    if (!show) { if (kit) st.multi = false; return; }
    const one = sel.length === 1 ? sel[0] : null, w = canWrite();
    const act = (name, text, fn, cls = '', off = false) => h('button', { type: 'button', class: `f-act-btn m3-btn tonal sm mb m3-state ${cls}`.trim(), title: text, 'aria-label': text, disabled: off, onclick: fn }, icon(name), h('span', { class: 'f-act-t', text }));
    selbar.append(h('span', { class: 'f-count', text: t('files.selected', { n: sel.length }) }));
    if (one && C.isFileLike(one)) selbar.append(act('download', t('desktopFiles.download'), () => download(one)));
    selbar.append(act('copy', t('files.menu.copy'), () => doClip('copy'), '', !w), act('cut', t('files.menu.cut'), () => doClip('cut'), '', !w), act('delete', t('files.delete.action'), () => remove(sel), 'danger', !w),
      act('dots', t('files.menu.more'), (e) => { const r = e.currentTarget.getBoundingClientRect(); contextMenu({ clientX: r.left, clientY: r.top, preventDefault() {} }, true); }),
      act('close', t('files.clearSelection'), () => { st.sel.clear(); st.multi = false; paint(); }));
  }

  /* ----- Auswahl und Zeiger ----- */
  const inRow = (target) => (target && target.closest ? target.closest('.f-row') : null);
  let pending = null;
  function wireList() {
    const L = list;
    L.addEventListener('mousedown', (e) => {
      if (kit || e.button !== 0) return;
      const r = inRow(e.target);
      L.focus({ preventScroll: true });
      if (!r) { startBand(e); return; }
      const p = r.dataset.path, mods = { shift: e.shiftKey, ctrl: e.ctrlKey || e.metaKey };
      if (mods.shift || mods.ctrl) { st.sel.click(p, mods); paint(); }
      else if (!st.sel.has(p)) { st.sel.click(p); paint(); }
      else pending = p;                                                    // schon in der Auswahl: erst beim Loslassen (Ziehen soll sie behalten)
    });
    L.addEventListener('mouseup', (e) => { if (pending !== null) { const r = inRow(e.target); if (r && r.dataset.path === pending) { st.sel.click(pending); paint(); } pending = null; } });
    L.addEventListener('dblclick', (e) => { const r = inRow(e.target); if (!r || kit) return; const en = st.byPath.get(r.dataset.path); if (en) openEntry(en); });
    L.addEventListener('contextmenu', (e) => { if (kit) { e.preventDefault(); return; } const r = inRow(e.target); contextMenu(e, !!r, r); });
    L.addEventListener('dragstart', (e) => {
      const r = inRow(e.target); pending = null; if (!r || kit) return;
      if (!st.sel.has(r.dataset.path)) { st.sel.click(r.dataset.path); paint(); }
      drag = { kind: 'files', place: st.place, paths: st.sel.list() };
      e.dataTransfer.effectAllowed = 'copyMove'; e.dataTransfer.setData('application/x-julos-files', '1'); e.dataTransfer.setData('text/plain', '');
    });
    L.addEventListener('dragend', endDrag);
    if (!kit) dropTarget(L, () => (st.place ? { place: st.place, dir: st.path } : null));
    L.addEventListener('keydown', onKey);
    if (kit) wireTouch(L);
  }
  /** Handy: Antippen oeffnet (im Auswahlmodus schaltet es um), langes Druecken waehlt, schaltet den Auswahlmodus ein und zeigt das Menue */
  function wireTouch(L) {
    let timer = null, fired = false, x0 = 0, y0 = 0;
    const cancel = () => { clearTimeout(timer); timer = null; };
    L.addEventListener('pointerdown', (e) => {
      fired = false; const r = inRow(e.target); if (!r) return;
      x0 = e.clientX; y0 = e.clientY;
      timer = setTimeout(() => { fired = true; st.multi = true; if (!st.sel.has(r.dataset.path)) st.sel.click(r.dataset.path, { ctrl: true }); paint(); if (navigator.vibrate) navigator.vibrate(15); contextMenu({ clientX: x0, clientY: y0, preventDefault() {} }, true, r); }, 480);
    });
    L.addEventListener('pointermove', (e) => { if (timer && Math.hypot(e.clientX - x0, e.clientY - y0) > 10) cancel(); });
    for (const ev of ['pointerup', 'pointercancel', 'pointerleave']) L.addEventListener(ev, cancel);
    L.addEventListener('click', (e) => {
      if (fired) { fired = false; return; }
      const r = inRow(e.target);
      if (!r) { if (!st.multi) { st.sel.clear(); paint(); } return; }
      const en = st.byPath.get(r.dataset.path); if (!en) return;
      if (st.multi) { st.sel.click(en.path, { ctrl: true }); paint(); } else openEntry(en);
    });
  }
  /* Rahmen aufziehen */
  function startBand(e) {
    const base = e.ctrlKey || e.metaKey ? st.sel.list() : null;
    if (!base) { st.sel.clear(); paint(); }
    const vr = view.getBoundingClientRect(), x0 = e.clientX - vr.left + view.scrollLeft, y0 = e.clientY - vr.top + view.scrollTop;
    const bandEl = h('div', { class: 'f-band' }); let on = false;
    const move = (ev) => {
      const x1 = ev.clientX - vr.left + view.scrollLeft, y1 = ev.clientY - vr.top + view.scrollTop;
      if (!on && Math.hypot(x1 - x0, y1 - y0) < 4) return;
      if (!on) { on = true; view.append(bandEl); }
      const left = Math.min(x0, x1), top = Math.min(y0, y1), w = Math.abs(x1 - x0), hgt = Math.abs(y1 - y0);
      Object.assign(bandEl.style, { left: left + 'px', top: top + 'px', width: w + 'px', height: hgt + 'px' });
      const rects = [...st.rows].map(([path, el]) => { const r = el.getBoundingClientRect(); return { path, top: r.top - vr.top + view.scrollTop, bottom: r.bottom - vr.top + view.scrollTop, left: r.left - vr.left + view.scrollLeft, right: r.right - vr.left + view.scrollLeft }; });
      st.sel.setFromBand(X.rectsInBand(rects, { top, bottom: top + hgt, left, right: left + w }), base);
      paint();
    };
    const up = () => { document.removeEventListener('mousemove', move); document.removeEventListener('mouseup', up); bandEl.remove(); };
    document.addEventListener('mousemove', move); document.addEventListener('mouseup', up);
    e.preventDefault();
  }

  /* ----- Tastatur ----- */
  const typeAhead = X.createTypeAhead();
  function columnsOfTiles() { const first = list && list.querySelector('.f-row'); return first ? Math.max(1, Math.floor(list.clientWidth / first.offsetWidth)) : 1; }
  function pageRows() { const first = list && list.querySelector('.f-row'); return first ? Math.max(1, Math.floor(view.clientHeight / first.offsetHeight) - 1) : 10; }
  function onKey(e) {
    if (!st.place) return;
    const a = X.actionForKey(e);
    if (!a) {
      if (!e.ctrlKey && !e.metaKey && !e.altKey && e.key.length === 1 && e.key !== ' ') {                 // Buchstabensprung
        const names = st.shown.map((x) => x.name), from = st.sel.focus ? st.sel.index(st.sel.focus) : -1;
        const i = typeAhead.press(e.key, Date.now(), names, from);
        if (i >= 0) { st.sel.jump(st.shown[i].path); paint(); scrollToFocus(); }
        e.preventDefault();
      } else if (e.key === ' ' && !e.ctrlKey) e.preventDefault();
      return;
    }
    const sel = selEntries(), one = sel.length === 1 ? sel[0] : null, tiles = prefs.view.mode === 'tiles';
    let used = true;
    switch (a) {
      case 'move': case 'ctrlMove': {
        const k = e.key, cols = tiles ? columnsOfTiles() : 1;
        const by = k === 'ArrowDown' ? cols : k === 'ArrowUp' ? -cols : k === 'ArrowRight' ? (tiles ? 1 : 0) : k === 'ArrowLeft' ? (tiles ? -1 : 0) : k === 'PageDown' ? pageRows() * cols : k === 'PageUp' ? -pageRows() * cols : k === 'Home' ? 'first' : 'last';
        if (by === 0) break;
        st.sel.move(by, { shift: e.shiftKey, ctrl: a === 'ctrlMove' }); paint(); scrollToFocus(); break;
      }
      case 'toggle': st.sel.toggleFocus(); paint(); break;
      case 'open': if (one) openEntry(one); break;
      case 'up': goUp(); break;
      case 'back': goBack(); break;
      case 'forward': goForward(); break;
      case 'selectAll': st.sel.selectAll(); paint(); break;
      case 'clear': if (st.filter || searchInput.value) { searchInput.value = ''; clearTimeout(timer); search(); } else { st.sel.clear(); st.multi = false; paint(); } break;
      case 'rename': if (one) rename(one); break;
      case 'delete': if (sel.length) remove(sel); break;
      case 'refresh': reload(); break;
      case 'copy': doClip('copy'); break;
      case 'cut': doClip('cut'); break;
      case 'paste': pasteInto(st.place, st.path); break;
      case 'address': editAddr(); break;
      case 'search': searchInput.focus(); searchInput.select(); break;
      case 'newFolder': newFolder(); break;
      case 'menu': { const f = st.sel.focus && st.rows.get(st.sel.focus); const r = (f || list || view).getBoundingClientRect(); contextMenu({ clientX: r.left + 40, clientY: r.bottom, preventDefault() {} }, !!f, f); break; }
      default: used = false;
    }
    if (used) e.preventDefault();
  }
  // Tastenkuerzel, die nicht an der Liste haengen (Adressleiste, Suche), gelten im ganzen Fenster der App
  root.addEventListener('keydown', (e) => {
    if (e.defaultPrevented || e.target === list || !st.place) return;
    if (e.target.closest && e.target.closest('input, textarea, select, [contenteditable]')) return;
    const a = X.actionForKey(e);
    if (a === 'address') { e.preventDefault(); editAddr(); } else if (a === 'search') { e.preventDefault(); searchInput.focus(); searchInput.select(); }
  });

  /* ----- Kontextmenue ----- */
  function contextMenu(e, onEntry, rowEl = null) {
    if (e.preventDefault) e.preventDefault();
    if (!st.place) return;
    if (onEntry) { const p = rowEl ? rowEl.dataset.path : null; if (p) { st.sel.context(p); paint(); } }
    else if (!kit) { st.sel.clear(); paint(); }
    const sel = selEntries();
    openMenu({ kit, x: e.clientX, y: e.clientY, label: t('files.menu.label'), items: onEntry && sel.length ? entryItems(sel) : emptyItems() });
  }
  function entryItems(sel) {
    const one = sel.length === 1 ? sel[0] : null, w = canWrite(), tip = w ? undefined : noWriteTitle(), k = ctrl(), sc = (s) => (kit ? '' : s);
    const items = [];
    if (one) items.push({ id: 'open', label: t('files.menu.open'), icon: one.openable ? 'open' : 'blank', shortcut: sc('Enter'), run: () => openEntry(one) });
    if (one && C.isFileLike(one)) items.push({ id: 'download', label: t('desktopFiles.download'), icon: 'download', run: () => download(one) });
    items.push({ sep: true },
      { id: 'cut', label: t('files.menu.cut'), icon: 'cut', shortcut: sc(`${k}+X`), disabled: !w, title: tip, run: () => doClip('cut') },
      { id: 'copy', label: t('files.menu.copy'), icon: 'copy', shortcut: sc(`${k}+C`), disabled: !w, title: tip, run: () => doClip('copy') });
    if (one && one.openable) items.push({ id: 'paste', label: t('files.menu.paste'), icon: 'paste', shortcut: sc(`${k}+V`), disabled: !w || !clip, title: tip, run: () => pasteInto(st.place, one.path) });
    items.push({ sep: true });
    if (one) items.push({ id: 'rename', label: t('files.rename.action'), icon: 'edit', shortcut: sc('F2'), disabled: !w, title: tip, run: () => rename(one) });
    items.push({ id: 'delete', label: t('files.delete.action'), icon: 'delete', shortcut: sc(t('files.menu.del')), danger: true, disabled: !w, title: tip, run: () => remove(sel) }, { sep: true });
    if (one && one.openable) items.push(pinned(st.place, one.path) ? { id: 'unpin', label: t('files.menu.unpin'), icon: 'pin', run: () => unpinHere(st.place, one.path) } : { id: 'pin', label: t('files.menu.pin'), icon: 'pin', run: () => pinHere(st.place, one.path, one.name) });
    items.push({ id: 'path', label: t('files.menu.copyPath'), icon: 'link', run: () => copyText(sel.map((x) => x.path).join('\n')) });
    if (one) items.push({ id: 'props', label: t('files.menu.props'), icon: 'info', run: () => properties(one) });
    return items;
  }
  function emptyItems() {
    const w = canWrite(), tip = w ? undefined : noWriteTitle(), k = ctrl();
    return [
      ...newItems(), { sep: true },
      { id: 'paste', label: t('files.menu.paste'), icon: 'paste', shortcut: kit ? '' : `${k}+V`, disabled: !w || !clip, title: tip, run: () => pasteInto(st.place, st.path) },
      { id: 'upload', label: t('files.upload.action'), icon: 'upload', disabled: !w, title: tip, run: () => fileInput.click() },
      { sep: true },
      { id: 'refresh', label: t('files.refresh'), icon: 'refresh', shortcut: kit ? '' : 'F5', run: () => reload() },
      pinned(st.place, st.path) ? { id: 'unpin', label: t('files.menu.unpin'), icon: 'pin', run: () => unpinHere(st.place, st.path) } : { id: 'pin', label: t('files.menu.pin'), icon: 'pin', run: () => pinHere(st.place, st.path, C.nameOf(st.path) || st.place.label) },
      { id: 'path', label: t('files.menu.copyPath'), icon: 'link', run: () => copyText(st.path || '/') },
    ];
  }
  async function copyText(text) { try { await navigator.clipboard.writeText(text); say(t('files.copyPath.done')); } catch { say(text); } }
  function properties(e) {
    const rows = [[t('files.props.name'), X.safeName(e.name)], [t('files.rowPath'), X.safeName(C.parentOf(e.path, st.place) || '/')], [t('files.props.type'), typeText(e)]];
    if (e.kind === 'file') rows.push([t('files.props.size'), `${C.fileSize(e.size)} (${e.size} B)`]);
    if (e.modified) rows.push([t('files.props.modified'), C.fmtModified(e.modified)]);
    if (e.mode !== undefined && e.mode !== null && e.mode !== '') rows.push([t('files.props.mode'), String(e.mode)]);
    modal({ title: t('files.props.title'), kit, id: 'props', content: [rowsEl(rows)], actions: [{ id: 'x', label: t('common.close'), kind: 'primary' }] });
  }

  /* ----- Oeffnen, Lesen, Herunterladen, Editor ----- */
  function openEntry(e) {
    if (e.openable) return go(e.path);
    if (!C.isFileLike(e)) return undefined;
    if (!C.isLocal(st.place) && X.editable(e)) return editFile(e, { fallbackDownload: true });
    return download(e);
  }
  /** Auf einem Root-Ort liest der Server nur mit einer Bestaetigung (prepare, Dialog, Token); sonst gibt es kein Token. Gibt { token } oder null (abgebrochen oder Fehler). */
  async function readConfirmed(action, e) {
    if (!C.isRoot(st.place)) return { token: undefined };
    const prep = await prepare(action, { paths: [e.path] });
    if (!prep || !(await confirmDialog(kit, C.readModel(action, prep), { expiresIn: prep.expiresIn }))) return null;
    return { token: prep.token };
  }
  async function download(e) {
    const ok = await readConfirmed('download', e);
    if (!ok) return;
    const a = document.createElement('a'); a.href = fapi.downloadUrl(st.place, e.path, ok.token); a.download = ''; a.rel = 'noopener'; document.body.appendChild(a); a.click(); a.remove();
  }
  async function editFile(e, { fallbackDownload = false } = {}) {
    if (st.edit) return;
    st.edit = true;
    try {
      const place = st.place, ok = await readConfirmed('text', e);
      if (!ok) return;
      let r;
      try { r = await guarded(() => fapi.text(place, e.path, ok.token), place); }
      catch (err) {
        if (C.isCancelled(err)) return;
        if (err.code === 'not_text') { if (fallbackDownload) return download(e); return say(t('files.notText', { name: X.safeName(e.name) })); }
        return say(C.errorMessage(err));
      }
      let base = { size: e.size, modified: e.modified };
      const cut = !!r.truncated;
      const why = !info.write ? t('files.writeOff') : cut ? t('files.edit.readOnlyCut', { size: C.fileSize(X.EDIT_MAX_BYTES) }) : '';
      const ed = openEditor({
        kit, name: e.name, path: e.path, text: r.text, sizeText: C.fileSize(r.size), readOnly: !canWrite(place) || cut, why,
        onSave: async (text) => { const res = await saveText(place, e, text, base); if (res) base = res.stat; return !!res; },
      });
      await ed.promise;
    } finally { st.edit = false; }
  }
  /** Speichern: pruefen, ob die Datei sich auf dem Host geaendert hat, dann prepare, der Dialog, der Upload mit dem Token */
  async function saveText(place, e, text, base) {
    const dir = C.parentOf(e.path, place);
    let changed = false;
    try {
      const l = await guarded(() => fapi.list(place, dir), place);
      const cur = l.entries.find((x) => x.path === e.path) || null;
      changed = !cur || cur.modified !== base.modified || cur.size !== base.size;
    } catch (err) { if (C.isCancelled(err)) return null; }
    const file = new File([text], e.name, { type: 'application/octet-stream' });
    const prep = await prepare('upload', { dir, names: [e.name] }, place);
    if (!prep) return null;
    const r = await confirmDialog(kit, C.saveModel(prep, e.name, file.size, changed), { expiresIn: prep.expiresIn });
    if (!r) return null;
    try { await retryOpen(place, () => fapi.upload(place, prep.dir, file, { overwrite: prep.conflicts.includes(e.name), token: prep.token, answer: r.answer }).promise); }
    catch (err) { if (!C.isCancelled(err)) say(C.errorMessage(err)); return null; }
    say(t('files.edit.saved', { name: X.safeName(e.name) }));
    let stat = { size: file.size, modified: base.modified };
    try { const l = await guarded(() => fapi.list(place, dir), place); const now = l.entries.find((x) => x.path === e.path); if (now) stat = { size: now.size, modified: now.modified }; } catch { /* egal */ }
    if (st.place && st.place.id === place.id && st.path === dir) reload();
    return { stat };
  }

  /* ----- Schreiben: zwei Schritte. Erst prepare (der Server prueft und beschreibt, was geschaehe), dann der Dialog nach dieser Antwort, dann die Aktion mit dem Token ----- */
  /** Schritt 1 mit Fehlermeldung; null bei einem Fehler oder Abbruch */
  async function prepare(action, args, place = st.place) {
    try { return await guarded(() => fapi.prepare(action, place, args), place); }
    catch (e) { if (!C.isCancelled(e)) say(C.errorMessage(e)); return null; }
  }
  async function runWrite(fn, doneText, selectPath) {
    try { await fn(); say(doneText); if (selectPath) st.pendingSelect = selectPath; await reload(); }
    catch (e) { if (!C.isCancelled(e)) say(C.errorMessage(e)); }
  }
  async function newFolder() {
    if (!canWrite()) return say(noWriteTitle());
    const place = st.place, dir = st.path;
    const name = await inputDialog(kit, { title: t('files.mkdir.title'), label: t('files.mkdir.nameLabel'), confirm: t('files.mkdir.next') });
    if (!name) return;
    const prep = await prepare('mkdir', { dir, name }, place);
    const r = prep && await confirmDialog(kit, C.mkdirModel(prep), { expiresIn: prep.expiresIn });
    if (!r) return;
    await runWrite(() => guarded(() => fapi.mkdir(place, prep.dir, prep.name, prep.token, r.answer), place), t('files.mkdir.done', { name: X.safeName(prep.name) }), C.joinPath(prep.dir, prep.name, place));
  }
  async function newFile() {
    if (!canWrite()) return say(noWriteTitle());
    const place = st.place, dir = st.path;
    const name = await inputDialog(kit, { title: t('files.newFile.title'), label: t('files.newFile.nameLabel'), value: t('files.newFile.default'), confirm: t('files.mkdir.next'), selectBase: true });
    if (!name) return;
    const prep = await prepare('upload', { dir, names: [name] }, place);
    const r = prep && await confirmDialog(kit, C.newFileModel(prep, name), { expiresIn: prep.expiresIn });
    if (!r) return;
    const file = new File([], name, { type: 'application/octet-stream' });
    await runWrite(() => retryOpen(place, () => fapi.upload(place, prep.dir, file, { overwrite: prep.conflicts.includes(name), token: prep.token, answer: r.answer }).promise), t('files.newFile.done', { name: X.safeName(name) }), C.joinPath(prep.dir, name, place));
  }
  async function rename(e) {
    if (!canWrite()) return say(noWriteTitle());
    const place = st.place;
    const name = await inputDialog(kit, { title: t('files.rename.title'), label: t('files.rename.nameLabel'), value: e.name, confirm: t('files.mkdir.next'), selectBase: !e.openable });
    if (!name || name === e.name) return;
    const prep = await prepare('rename', { paths: [e.path], name }, place);
    const r = prep && await confirmDialog(kit, C.renameModel(prep), { expiresIn: prep.expiresIn });
    if (!r) return;
    await runWrite(() => guarded(() => fapi.rename(place, prep.paths[0], prep.name, prep.conflicts.length > 0, prep.token, r.answer), place), t('files.rename.done', { name: X.safeName(prep.name) }), C.joinPath(C.parentOf(e.path, place), prep.name, place));
  }
  async function remove(sel) {
    if (!sel.length) return;
    if (!canWrite()) return say(noWriteTitle());
    const place = st.place;
    const prep = await prepare('delete', { paths: sel.map((e) => e.path) }, place);
    const model = prep && C.deleteModel(prep);
    const r = model && await confirmDialog(kit, model, { expiresIn: prep.expiresIn });
    if (!r) return;
    await runJob(model.title, 'delete', () => guarded(() => fapi.remove(place, prep.paths, prep.nonEmpty, prep.token, r.answer), place));
    st.multi = false; await reload();
  }

  /* ----- Zwischenablage, Einfuegen, Ziehen ----- */
  function doClip(mode) {
    if (!canWrite()) return say(noWriteTitle());
    const sel = selEntries(); if (!sel.length) return;
    clip = X.makeClip(mode, st.place.id, sel); clip.placeObj = st.place;
    st.multi = false; paint();
    say(mode === 'cut' ? tp('files.clip.cut', sel.length) : tp('files.clip.copy', sel.length));
  }
  async function pasteInto(place, dir) {
    if (!clip) return;
    if (!canWrite(place)) return say(noWriteTitle());
    if (X.pasteIsNoop(clip, place.id, dir, (p) => C.parentOf(p, place))) return;
    const action = clip.mode === 'cut' ? 'move' : 'copy', used = clip;
    const done = await transfer(action, used.placeObj, used.paths, { place, dir });
    if (done && action === 'move' && clip === used) { clip = null; paint(); }
  }
  /** Kopieren oder Verschieben (auch auf einen anderen Host): ein Dialog fuer die ganze Aktion, dann ein Auftrag mit Fortschritt */
  async function transfer(action, srcPlace, paths, target) {
    if (srcPlace.id === target.place.id && X.dropIntoSelf(paths, target.dir)) { say(C.errText('into_itself')); return false; }
    if (!(await openQuiet(srcPlace)) || (target.place.id !== srcPlace.id && !(await openQuiet(target.place)))) return false;
    const prep = await prepare(action, { paths, target }, srcPlace);
    const model = prep && C.copyModel(action, prep);
    const r = model && await confirmDialog(kit, model, { expiresIn: prep.expiresIn });
    if (!r) return false;
    await runJob(model.title, action, () => guarded(() => fapi.copy(srcPlace, prep.paths, target, { move: action === 'move', overwrite: prep.conflicts.length > 0, token: prep.token, answer: r.answer }), srcPlace));
    if (st.place && (target.place.id === st.place.id || (action === 'move' && srcPlace.id === st.place.id))) await reload();
    return true;
  }
  const endDrag = () => { drag = null; for (const el of document.querySelectorAll('.f-drop-on')) el.classList.remove('f-drop-on'); };
  const hasFiles = (e) => e.dataTransfer && [...(e.dataTransfer.types || [])].includes('Files');
  /** Macht ein Element zum Ziel zum Ablegen: Dateien aus der Liste (verschieben oder kopieren), Dateien vom Rechner (hochladen), Pins (umordnen). get() gibt { place, dir } oder null. */
  function dropTarget(el, get, pin = null) {
    const effect = (e, tgt) => (drag && drag.place.id === tgt.place.id ? (e.ctrlKey || e.metaKey ? 'copy' : 'move') : (e.shiftKey ? 'move' : 'copy'));
    const accept = (tgt) => {
      if (!tgt || !canWrite(tgt.place)) return false;
      if (drag && drag.kind === 'files' && drag.place.id === tgt.place.id) return !X.dropIntoSelf(drag.paths, tgt.dir) && !X.pasteIsNoop({ mode: 'cut', place: drag.place.id, paths: drag.paths }, tgt.place.id, tgt.dir, (p) => C.parentOf(p, tgt.place));
      return true;
    };
    el.addEventListener('dragover', (e) => {
      if (drag && drag.kind === 'pin') { if (pin && pin.place === drag.pin.place && X.pinKey(pin) !== X.pinKey(drag.pin)) { e.preventDefault(); e.stopPropagation(); el.classList.add('f-drop-on'); } return; }
      if (!(drag && drag.kind === 'files') && !hasFiles(e)) return;
      const tgt = get();
      if (!accept(tgt)) return;
      e.preventDefault(); e.stopPropagation(); e.dataTransfer.dropEffect = drag ? effect(e, tgt) : 'copy'; el.classList.add('f-drop-on');
    });
    el.addEventListener('dragleave', (e) => { if (!el.contains(e.relatedTarget)) el.classList.remove('f-drop-on'); });
    el.addEventListener('drop', async (e) => {
      el.classList.remove('f-drop-on');
      if (drag && drag.kind === 'pin') { if (pin && pin.place === drag.pin.place) { e.preventDefault(); e.stopPropagation(); prefs = { ...prefs, pins: X.reorderPin(prefs.pins, drag.pin, pin) }; savePrefs(); renderSide(); } return; }
      const tgt = get();
      if (!accept(tgt)) return;
      if (drag && drag.kind === 'files') { e.preventDefault(); e.stopPropagation(); const d = drag, action = effect(e, tgt); drag = null; await transfer(action === 'move' ? 'move' : 'copy', d.place, d.paths, tgt); return; }
      if (hasFiles(e)) { e.preventDefault(); e.stopPropagation(); dragDepth = 0; drop.hidden = true; upload([...e.dataTransfer.files].filter((f) => f.type || f.size || f.name), tgt); }
    });
  }

  /* ----- Hochladen ----- */
  fileInput.addEventListener('change', () => { const files = [...fileInput.files]; fileInput.value = ''; if (files.length) upload(files); });
  let dragDepth = 0;
  main.addEventListener('dragenter', (e) => { if (!hasFiles(e) || !writable()) return; e.preventDefault(); dragDepth++; drop.hidden = false; });
  main.addEventListener('dragover', (e) => { if (hasFiles(e) && writable()) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
  main.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; drop.hidden = true; } });
  main.addEventListener('drop', (e) => { if (!hasFiles(e)) return; e.preventDefault(); dragDepth = 0; drop.hidden = true; if (writable()) upload([...e.dataTransfer.files].filter((f) => f.type || f.size || f.name)); else say(t('files.writeOff')); });

  async function upload(files, dest = { place: st.place, dir: st.path }) {
    const { place, dir } = dest;
    if (!canWrite(place)) return say(t('files.writeOff'));
    const tooBig = files.filter((f) => info.maxBytes && f.size > info.maxBytes);
    if (tooBig.length) say(t('files.err.too_large'));
    files = files.filter((f) => !tooBig.includes(f));
    if (!files.length) return;
    if (files.length > 100) return say(t('files.err.too_many_items'));
    const names = [...new Set(files.map((f) => f.name))];
    files = names.map((n) => files.find((f) => f.name === n));
    const prep = await prepare('upload', { dir, names }, place);
    const r = prep && await confirmDialog(kit, C.uploadModel(prep, files), { expiresIn: prep.expiresIn });
    if (!r) return;
    const overwrite = new Set(prep.conflicts);
    let aborted = false, current = null;
    const prog = progressDialog(kit, t('files.upload.title'), { onCancel: () => { aborted = true; if (current) current.abort(); } });
    let okN = 0; const failed = [];
    const send = (f, onProgress) => (current = fapi.upload(place, prep.dir, f, { overwrite: overwrite.has(f.name), token: prep.token, answer: r.answer, onProgress }));
    for (let i = 0; i < files.length && !aborted; i++) {
      const f = files[i];
      prog.update({ percent: 0, text: `${X.safeName(f.name)} (${i + 1}/${files.length})` });
      const progress = (done, total) => prog.update({ percent: total ? Math.round((done / total) * 100) : null, text: `${X.safeName(f.name)} (${i + 1}/${files.length}) · ${C.fileSize(done)} / ${C.fileSize(total)}` });
      try { await retryOpen(place, () => send(f, progress).promise); okN++; }
      catch (e) { if (C.isCancelled(e) && aborted) break; failed.push(`${X.safeName(f.name)}: ${C.errorMessage(e)}`); }
    }
    const ok = !failed.length && !aborted;
    await prog.finish({ ok, text: aborted ? t('files.upload.cancelled', { n: okN }) : ok ? t('files.upload.done', { n: tp('files.nFiles', okN) }) : t('files.upload.partial', { ok: okN, failed: failed.length }), details: failed });
    if (st.place && st.place.id === place.id && st.path === dir) await reload();
  }
  /** Fehlt die Sitzung beim Start des Hochladens (Leerlauf), wird der Ort neu geoeffnet und die Datei noch einmal gesendet: die Bestaetigung ist dann noch nicht eingeloest (das passiert erst mit der Sitzung) */
  async function retryOpen(place, fn) {
    try { return await fn(); } catch (e) {
      if (['password_required', 'password_unreadable', 'host_key_new', 'host_key_changed'].includes(e.code)) { await ensureOpen(place); return fn(); }
      throw e;
    }
  }

  /** Startet einen Auftrag, zeigt den Fortschritt (abbrechbar) und das Ergebnis */
  async function runJob(title, action, start) {
    let cancelJob = () => {};
    const prog = progressDialog(kit, title, { onCancel: () => cancelJob() });
    let started;
    try { started = await start(); }
    catch (e) { prog.close(); if (!C.isCancelled(e)) say(C.errorMessage(e)); return; }
    let w = null;
    cancelJob = () => { if (w) w.cancel(); };
    w = C.watchJob(fapi, started.job, { onProgress: (j) => prog.update({ percent: C.jobPercent(j), text: [j.current ? X.safeName(j.current) : '', j.total ? `${j.done}/${j.total}` : '', j.bytesTotal ? `${C.fileSize(j.bytes)} / ${C.fileSize(j.bytesTotal)}` : ''].filter(Boolean).join(' · ') }) });
    let job;
    try { job = await w.promise; }
    catch (e) { await prog.finish({ ok: false, text: C.errorMessage(e) }); return; }
    await prog.finish({ ok: job.state === 'done', text: C.jobSummary(job, action), details: [] });
  }

  /* ----- Verlauf ----- */
  async function showHistory() {
    const holder = h('div', { class: 'f-hist' }, h('p', { text: t('hostinfo.stLoading') }));
    const dlg = modal({ title: t('files.history'), kit, id: 'hist', content: [holder], actions: [{ id: 'x', label: t('common.close'), kind: 'primary' }] });
    try {
      const r = await fapi.audit(40);
      const rows = r.entries.map(C.auditLine);
      holder.replaceChildren(...(rows.length ? rows.map((l) => h('div', { class: 'f-hrow' + (l.failed ? ' bad' : '') },
        h('span', { class: 'f-hwhen', text: `${l.when} · ${l.host}` }), h('span', { class: 'f-hwhat', text: [l.action, X.safeName(l.path), l.items].filter(Boolean).join(' · ') }), h('span', { class: 'f-hres', text: l.result + (l.detail ? ` (${l.detail})` : '') }))) : [h('p', { text: t('files.historyEmpty') })]));
    } catch (e) { holder.replaceChildren(noteEl(C.errorMessage(e), 'warn')); }
    await dlg.promise;
  }

  /* ----- Suche: im gemeinsamen Ordner auf dem Server (auch in Unterordnern), auf Hosts ein Filter der Liste ----- */
  let timer = null;
  async function search() {
    const text = searchInput.value.trim();
    if (!C.isLocal(st.place)) { st.filter = text; applyList(); renderList(); return; }
    if (!text) { if (st.query) { st.query = ''; await reload(); } return; }
    const my = ++st.seq; st.query = text; view.classList.add('busy');
    try {
      const r = await fapi.search(st.path, text);
      if (my !== st.seq) return;
      st.entries = r.results.map((e) => C.normalizeListing(st.place, { entries: [e], path: '' }).entries[0]);
      st.truncated = false; st.hidden = 0; applyList(); renderList();
      if (r.truncated) statL.textContent = t('desktopFiles.hitsCut', { text: tp('desktopFiles.hits', st.entries.length) });
    } catch (e) { if (my === st.seq) showFailure(C.errorMessage(e) || t('desktopFiles.searchFailed'), search); }
    finally { if (my === st.seq) view.classList.remove('busy'); }
  }
  searchInput.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(search, st.place && C.isLocal(st.place) ? 300 : 80); });
  searchInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { clearTimeout(timer); search(); focusList(); }
    else if (e.key === 'Escape') { e.stopPropagation(); if (searchInput.value) { searchInput.value = ''; clearTimeout(timer); search(); } else focusList(); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); focusList(); if (st.shown.length && !st.sel.size) { st.sel.move(1); paint(); scrollToFocus(); } }
  });

  /* ----- Start ----- */
  applyView();
  (async () => {
    try { info = await fapi.info(); } catch { info = { local: true, write: false, maxBytes: 0 }; }
    refreshPlaces();
    renderList();
    loadPrefs();
    const last = store.get();
    const startPlace = (info.local && !kit && (!last || last === C.LOCAL_ID)) ? places.local : null;      // am Desktop gleich der gemeinsame Ordner; ein Host wird nie ungefragt geoeffnet
    if (startPlace) enter(startPlace);
  })();

  return {
    refreshPlaces,
    /** Oeffnet einen Ort (oder den ersten Host, den es gibt) */
    enter: (id, path) => { const p = C.findPlace(places, id); if (p) return enter(p, path); },
    reload: () => (st.place ? reload() : Promise.resolve()),
    destroy() { st.dead = true; clearInterval(keepAlive); flushPrefs(); closeMenu(); if (st.place && !C.isLocal(st.place)) fapi.close(st.place.id); box.replaceChildren(); },
    state: st,
  };
}

/* ---------- Abfragen: Passwort und Host-Schluessel ---------- */
function passwordDialog(kit, place, wrong, host) {
  const field = h('input', { type: 'password', class: 'f-input', autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': t('files.password.label'), maxlength: 256 });
  // Hat der Host einen Login nur mit Benutzer: der Benutzer steht da, gefragt wird nur das Passwort, und es laesst sich "fuer diesen Host speichern" (Standard aus)
  const save = host && host.canSave ? h('input', { type: 'checkbox', id: 'fSaveHost' }) : null;
  const dlg = modal({ title: t('files.password.title', { host: place.label }), kit, id: 'pw', content: [
    h('p', { class: 'f-sentence', text: host ? t('files.password.sentenceHost', { user: host.user, host: place.label }) : t('files.password.sentence', { address: place.address }) }),
    wrong ? noteEl(t('files.err.logon_failed'), 'warn') : null,
    h('label', { class: 'f-field' }, h('span', { text: t('files.password.label') }), field),
    save ? h('label', { class: 'f-ack check', for: 'fSaveHost' }, save, ' ', t('files.password.saveHost')) : null,
    noteEl(t('files.password.note'), 'hint'),
  ], actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: t('files.password.connect'), kind: 'primary', disabled: true }] });
  field.addEventListener('input', () => dlg.setDisabled('yes', !field.value));
  field.addEventListener('keydown', (e) => { if (e.key === 'Enter' && field.value) { e.preventDefault(); dlg.close('yes'); } });
  field.focus();
  return dlg.promise.then((r) => { const v = r === 'yes' ? field.value : null; const keep = !!(save && save.checked); field.value = ''; return v !== null && save ? { password: v, save: keep } : v; });
}
function hostKeyDialog(kit, place, data, changed) {
  const dlg = modal({ title: t(changed ? tk('files.hostKey.changedTitle') : tk('files.hostKey.newTitle'), { host: place.label }), kit, id: 'hk', content: [
    h('p', { class: 'f-sentence', text: t(changed ? tk('files.hostKey.changedText') : tk('files.hostKey.newText'), { host: place.label }) }),
    rowsEl([[t('files.hostKey.algorithm'), data.algorithm], [t('files.hostKey.fingerprint'), data.fingerprint], ...(changed && data.known ? [[t('files.hostKey.known'), data.known]] : [])]),
    changed ? noteEl(t('files.hostKey.changedWarn'), 'warn') : noteEl(t('files.hostKey.newNote'), 'hint'),
  ], actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: t(changed ? tk('files.hostKey.acceptChanged') : tk('files.hostKey.accept')), kind: changed ? 'danger' : 'primary' }] });
  return dlg.promise.then((r) => r === 'yes');
}
