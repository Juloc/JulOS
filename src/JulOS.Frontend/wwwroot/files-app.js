/* =====================================================================
 * Dateien-App (docs/FILES.md): die Oberflaeche fuer Desktop-Fenster und Handy-Seite. Zeigt Orte (der gemeinsame Ordner des Servers und jeder Host mit SSH-Zugang), eine Liste mit Brotkruemeln,
 * Mehrfachauswahl und die Aktionen: Text ansehen, herunterladen, hochladen (auswaehlen oder hierher ziehen), neuer Ordner, umbenennen, kopieren, verschieben (auch auf einen anderen Host), loeschen.
 * Jede schreibende Aktion geht durch einen Bestaetigungsdialog, der Host, Pfad und Anzahl nennt; Ersetzen und Ordner loeschen brauchen ein ausdrueckliches Haekchen. Die Logik steht in files-core.js.
 * Alles, was von Server oder Ziel kommt (Namen, Pfade, Text), wird mit textContent gesetzt, nie als HTML. deps: { api, kit, getState, notify }.
 * ===================================================================== */
import { t, tp, tk } from './i18n.js';
import * as C from './files-core.js';

const ICONS = {
  dns: 'M20 13H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1v-6c0-.55-.45-1-1-1zM7 19c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM20 3H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1V4c0-.55-.45-1-1-1zM7 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z',
  folder: 'M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z',
  file: 'M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z',
  link: 'M3.9 12c0-1.71 1.39-3.1 3.1-3.1h4V7H7c-2.76 0-5 2.24-5 5s2.24 5 5 5h4v-1.9H7c-1.71 0-3.1-1.39-3.1-3.1zM8 13h8v-2H8v2zm9-6h-4v1.9h4c1.71 0 3.1 1.39 3.1 3.1s-1.39 3.1-3.1 3.1h-4V17h4c2.76 0 5-2.24 5-5s-2.24-5-5-5z',
  upload: 'M9 16h6v-6h4l-7-7-7 7h4v6zm-4 2h14v2H5v-2z',
  download: 'M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z',
  newfolder: 'M20 6h-8l-2-2H4c-1.11 0-1.99.89-1.99 2L2 18c0 1.11.89 2 2 2h16c1.11 0 2-.89 2-2V8c0-1.11-.89-2-2-2zm-1 8h-3v3h-2v-3h-3v-2h3V9h2v3h3v2z',
  edit: 'M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04c.39-.39.39-1.02 0-1.41l-2.34-2.34c-.39-.39-1.02-.39-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z',
  copy: 'M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z',
  move: 'M20 6h-8l-2-2H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm-8 12v-3H8v-2h4V10l4 4-4 4z',
  delete: 'M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z',
  refresh: 'M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z',
  up: 'M4 12l1.41 1.41L11 7.83V20h2V7.83l5.58 5.59L20 12l-8-8-8 8z',
  back: 'M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z',
  more: 'M7 10l5 5 5-5z',
  close: 'M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
  history: 'M13 3c-4.97 0-9 4.03-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42C8.27 19.99 10.51 21 13 21c4.97 0 9-4.03 9-9s-4.03-9-9-9zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z',
  view: 'M12 4.5C7 4.5 2.73 7.61 1 12c1.73 4.39 6 7.5 11 7.5s9.27-3.11 11-7.5c-1.73-4.39-6-7.5-11-7.5zM12 17c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5zm0-8c-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3-1.34-3-3-3z',
  search: 'M15.5 14h-.79l-.28-.27A6.471 6.471 0 0016 9.5 6.5 6.5 0 109.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z',
  checkOn: 'M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-9 14l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z',
  checkOff: 'M19 5v14H5V5h14m0-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2z',
  root: 'M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4z',
  lock: 'M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM9 8V6c0-1.66 1.34-3 3-3s3 1.34 3 3v2H9z',
};
const SVG = 'http://www.w3.org/2000/svg';
/** Ein Symbol als SVG (nur aus der festen Tabelle oben, nie aus Daten) */
export function icon(name) {
  const s = document.createElementNS(SVG, 'svg'); s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('aria-hidden', 'true'); s.setAttribute('class', 'f-ic');
  const p = document.createElementNS(SVG, 'path'); p.setAttribute('d', ICONS[name] || ICONS.file); s.appendChild(p);
  return s;
}

/** Ein Element mit Eigenschaften und Kindern; Text immer als Textknoten */
export function h(tag, props = {}, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') e.className = v;
    else if (k === 'text') e.textContent = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v === true ? '' : String(v));
  }
  for (const kid of kids.flat(Infinity)) if (kid !== null && kid !== undefined && kid !== false) e.append(typeof kid === 'object' && kid.nodeType ? kid : document.createTextNode(String(kid)));
  return e;
}
const btn = (label, cls, onclick, extra = {}) => h('button', { type: 'button', class: cls, onclick, ...extra }, label);

/** Eine Taste mit Symbol und Text; auf dem Handy die kleinen Tasten der Bausteinsammlung (docs/ui-kit.html) */
const tool = (kit, name, label, onclick, cls = '') => h('button', { type: 'button', class: `f-tool ${kit ? 'm3-iconbtn m3-state mb' : 'dk-btn sq'} ${cls}`.trim(), title: label, 'aria-label': label, onclick }, icon(name));

/* ---------- Dialoge ---------- */
/**
 * Ein modaler Dialog. content: Knoten; actions: [{ id, label, kind: 'primary'|'danger'|'ghost', disabled, keepOpen, onclick }] (keepOpen: die Taste schliesst nicht). Gibt { el, close(result), setDisabled(id, on), promise } zurueck;
 * das Versprechen liefert die id der gedrueckten Taste oder null (Escape, Schliessen). Auf dem Handy ein Bottom Sheet, auf dem Desktop ein gewoehnlicher Dialog.
 */
export function modal({ title, content = [], actions = [], kit = false, id = '' }) {
  const dlg = h('dialog', { class: 'f-dlg' + (kit ? ' m3sheet' : ''), 'aria-labelledby': 'fdlgT' + (id || ''), id: id ? 'fdlg-' + id : undefined });
  const buttons = new Map();
  const bar = h('div', { class: 'row dlg-btns f-dlg-btns' });
  let done;
  const promise = new Promise((r) => { done = r; });
  const close = (result = null) => { if (dlg.open) dlg.close(); dlg.dispatchEvent(new CustomEvent('f-result', { detail: result })); };
  for (const a of actions) {
    const b = h('button', { type: 'button', class: `${a.kind === 'primary' ? 'primary ' : a.kind === 'danger' ? 'primary danger ' : 'ghost '}${kit ? 'mb ' : ''}f-act`.trim(), 'data-act': a.id, disabled: !!a.disabled, onclick: () => { if (a.onclick) a.onclick(b); if (!a.keepOpen) close(a.id); } }, a.label);
    buttons.set(a.id, b); bar.append(b);
  }
  dlg.append(h('h2', { id: 'fdlgT' + (id || ''), text: title }), ...content, bar);
  let result = null;
  dlg.addEventListener('f-result', (e) => { result = e.detail; });
  dlg.addEventListener('close', () => { dlg.remove(); done(result); });
  dlg.addEventListener('click', (e) => { if (e.target === dlg) { const r = dlg.getBoundingClientRect(); if (e.clientY < r.top || e.clientY > r.bottom || e.clientX < r.left || e.clientX > r.right) close(null); } });
  document.body.appendChild(dlg);
  dlg.showModal();
  return { el: dlg, close, promise, setDisabled: (aid, on) => { const b = buttons.get(aid); if (b) b.disabled = !!on; }, setLabel: (aid, text) => { const b = buttons.get(aid); if (b) b.textContent = text; }, setHidden: (aid, on) => { const b = buttons.get(aid); if (b) b.hidden = !!on; } };
}

const rowsEl = (rows) => h('dl', { class: 'f-rows' }, rows.map(([k, v]) => [h('dt', { text: k }), h('dd', { text: v })]));
const noteEl = (text, kind = '') => h('p', { class: 'f-msg ' + kind, role: kind === 'warn' ? 'alert' : undefined, text });

/**
 * Bestaetigung einer Aktion (Schritt 2 der Zweischritt-Bestaetigung): der Satz, die Zeilen (Host, Pfad, Anzahl), eine Warnung und, wenn der Server es verlangt, ein Haekchen ("Ja, ersetzen"). Alles stammt aus der
 * Antwort des Servers (prepare). Die Bestaetigung gilt nur kurz (expiresIn Sekunden): danach ist die Taste aus und der Dialog sagt es. Gibt { ack } oder null (abgebrochen).
 */
export async function confirmDialog(kit, model, { expiresIn = 0 } = {}) {
  const wrap = h('div', { class: 'f-confirm' });
  const dlg = modal({ title: model.title, kit, content: [wrap], actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: model.confirm, kind: model.danger ? 'danger' : 'primary', disabled: !!model.ack }] });
  let check = null, timer = null;
  if (model.root) wrap.append(h('div', { class: 'f-rootnote', role: 'alert' }, icon('root'), h('span', { text: model.rootNote })));
  wrap.append(h('p', { class: 'f-sentence', text: model.sentence }), rowsEl(model.rows));
  if (model.note) wrap.append(noteEl(model.note));
  if (model.warning) wrap.append(noteEl(model.warning, 'warn'));
  if (model.ack) {
    check = h('input', { type: 'checkbox', id: 'fAck' });
    check.addEventListener('change', () => dlg.setDisabled('yes', !check.checked));
    wrap.append(h('label', { class: 'f-ack check', for: 'fAck' }, check, ' ', model.ack));
  }
  if (expiresIn > 0) timer = setTimeout(() => { dlg.setDisabled('yes', true); wrap.append(noteEl(C.errText('confirmation_expired'), 'warn')); }, expiresIn * 1000);
  const which = await dlg.promise;
  clearTimeout(timer);
  return which === 'yes' ? { ack: !!(check && check.checked) } : null;
}

/** Ein Name wird abgefragt (neuer Ordner, Umbenennen); nur Eingabe, geschrieben wird erst nach der Bestaetigung des Servers. Gibt den Namen oder null. */
export async function inputDialog(kit, { title, label, value = '', confirm }) {
  const field = h('input', { type: 'text', class: 'f-input', value, autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': label, maxlength: 255 });
  const dlg = modal({ title, kit, content: [h('label', { class: 'f-field' }, h('span', { text: label }), field)], actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: confirm, kind: 'primary', disabled: !C.validName(value) }] });
  const refresh = () => { const ok = C.validName(field.value); dlg.setDisabled('yes', !ok); if (field.value && !ok) field.setAttribute('aria-invalid', 'true'); else field.removeAttribute('aria-invalid'); };
  field.addEventListener('input', refresh);
  field.addEventListener('keydown', (e) => { if (e.key === 'Enter' && C.validName(field.value)) { e.preventDefault(); dlg.close('yes'); } });
  setTimeout(() => { field.focus(); field.select(); }, 60);
  const which = await dlg.promise;
  return which === 'yes' ? field.value.trim() : null;
}

/** Fortschritt: ein Dialog mit Balken und Text; update() waehrend der Arbeit, finish() am Ende (dann kommt "Schliessen"); onCancel() fuer "Abbrechen" */
export function progressDialog(kit, title, { onCancel } = {}) {
  const bar = h('progress', { class: 'f-progress', 'aria-label': title });
  const line = h('p', { class: 'f-line', role: 'status', 'aria-live': 'polite' });
  const result = h('div', { class: 'f-result' });
  let finished = false;
  const dlg = modal({ title, kit, content: [bar, line, result], actions: [
    { id: 'cancel', label: t('common.cancel'), kind: 'ghost', keepOpen: true, onclick: (b) => { if (onCancel) onCancel(); b.disabled = true; b.textContent = t('files.cancelling'); } },
    { id: 'close', label: t('common.close'), kind: 'primary' }] });
  dlg.setHidden('close', true);
  dlg.el.addEventListener('cancel', (e) => { if (!finished) e.preventDefault(); });          // Escape bricht nicht ab, nur die Taste
  return {
    update({ percent = null, text = '' }) { if (percent === null) bar.removeAttribute('value'); else { bar.max = 100; bar.value = percent; } line.textContent = text; },
    finish({ ok, text, details = [] }) {
      finished = true; bar.max = 100; bar.value = ok ? 100 : bar.value || 0; line.textContent = '';
      result.replaceChildren(noteEl(text, ok ? 'ok' : 'warn'), ...details.map((d) => noteEl(d)));
      dlg.setHidden('cancel', true); dlg.setHidden('close', false);
      return dlg.promise;
    },
    close: () => dlg.close(),
    promise: dlg.promise,
  };
}

/* ---------- Die App ---------- */
export function mountFiles(box, deps) {
  const { api, kit = false, getState = () => ({ connections: [], hosts: [] }), notify = () => {} } = deps;
  const fapi = C.makeFilesApi(api, deps.createXhr ? { createXhr: deps.createXhr } : undefined);
  let info = { local: false, write: false, maxBytes: 0 };
  let places = C.buildPlaces({ info });
  const st = { place: null, path: '', entries: [], truncated: false, hidden: 0, sel: new C.Selection(), back: [], fwd: [], seq: 0, query: '', home: new Map(), busy: false, dead: false };
  const store = { get: () => { try { return localStorage.getItem('pd.files.place'); } catch { return null; } }, set: (v) => { try { localStorage.setItem('pd.files.place', v); } catch { /* egal */ } } };

  box.replaceChildren();
  const root = h('div', { class: 'fapp' + (kit ? ' fkit' : ' fdesk') });
  const side = h('nav', { class: 'f-side', 'aria-label': t('desktopFiles.places') });
  const main = h('div', { class: 'f-main' });
  const head = h('div', { class: 'f-head' });
  const placeBtn = h('button', { type: 'button', class: 'f-place ' + (kit ? 'm3-btn tonal mb m3-state' : 'dk-btn'), 'aria-haspopup': 'dialog' });
  const crumbs = h('nav', { class: 'f-crumbs', 'aria-label': t('desktopFiles.path') });
  const tools = h('div', { class: 'f-tools' });
  const searchBox = h('label', { class: 'f-search' }, icon('search'), h('input', { type: 'text', autocomplete: 'off', spellcheck: 'false', 'aria-label': t('desktopFiles.searchByName') }));
  const searchInput = searchBox.querySelector('input');
  const banner = h('div', { class: 'f-banner', role: 'status' });
  const selbar = h('div', { class: 'f-selbar', hidden: true, 'aria-label': t('files.selection') });
  const view = h('div', { class: 'f-view', 'aria-live': 'polite' });
  const stat = h('div', { class: 'f-stat' });
  const fileInput = h('input', { type: 'file', multiple: true, hidden: true });
  const drop = h('div', { class: 'f-drop', hidden: true }, icon('upload'), h('span', { text: t('files.dropHere') }));
  const top = h('div', { class: 'f-top' }, head, banner, selbar);
  main.append(top, view, stat, drop, fileInput);
  root.append(side, main);
  box.appendChild(root);

  const writable = () => !!(st.place && !C.isLocal(st.place) && info.write);
  const label = () => (st.place ? st.place.label : t('files.choosePlace'));
  const say = (text) => notify(text);

  /* ----- Orte ----- */
  const refreshPlaces = () => { places = C.buildPlaces({ ...getState(), info }); renderSide(); renderHead(); };
  function placeList(onPick) {
    const list = h('div', { class: kit ? 'm3-list f-places' : 'f-places', role: 'list' });
    const item = (p, sub) => h('button', { type: 'button', class: 'f-pl ' + (kit ? 'm3-li mb m3-state' : 'dk-nav') + (st.place && st.place.id === p.id ? ' sel' : '') + (p.kind === 'root' ? ' f-pl-root' : ''), onclick: () => onPick(p), 'aria-current': st.place && st.place.id === p.id ? 'true' : undefined },
      h('span', { class: 'lead f-pl-ic' }, icon(p.kind === 'local' ? 'folder' : p.kind === 'root' ? 'root' : 'dns')), h('span', { class: 'txt' }, h('span', { class: 'hl', text: p.label }), h('span', { class: 'sl', text: sub })));
    if (places.local) list.append(item(places.local, t('files.localNote')));
    for (const g of places.groups) for (const p of g.places) list.append(item(p, p.kind === 'root' ? p.address : [p.address, p.hasPassword ? t('files.pwSaved') : t('files.pwAsk')].join(' · ')));
    if (!list.childNodes.length) list.append(h('p', { class: 'f-empty', text: t('files.noPlaces') }));
    return list;
  }
  function renderSide() { side.replaceChildren(h('h3', { class: 'f-side-h', text: t('desktopFiles.places') }), placeList((p) => enter(p))); }
  async function pickPlace() {
    refreshPlaces();
    const list = placeList((p) => { dlg.close(); enter(p); });
    const dlg = modal({ title: t('files.choosePlace'), kit, content: [list], actions: [{ id: 'x', label: t('common.close'), kind: 'ghost' }] });
  }

  /* ----- Oeffnen eines Orts: Passwort und Host-Schluessel nur mit ausdruecklicher Bestaetigung ----- */
  const ask = {
    password: (place, wrong) => passwordDialog(kit, place, wrong),
    hostKey: (place, data, changed) => hostKeyDialog(kit, place, data, changed),
  };
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
  async function guarded(fn) {
    try { return await fn(); } catch (e) {
      if (st.place && !C.isLocal(st.place) && ['password_required', 'password_unreadable', 'host_key_new', 'host_key_changed', 'connection_lost'].includes(e.code)) { await ensureOpen(st.place); return fn(); }
      throw e;
    }
  }

  async function enter(place, path) {
    if (place.id !== (st.place && st.place.id)) { st.back = []; st.fwd = []; st.loaded = false; st.path = ''; st.entries = []; }
    try { await ensureOpen(place); }
    catch (e) { if (!C.isCancelled(e)) say(C.errorMessage(e)); return; }
    st.place = place; store.set(place.id); st.sel.clear(); st.query = ''; searchInput.value = '';
    renderHead();
    renderSide();
    await load(path !== undefined ? path : C.isLocal(place) ? '' : (st.home.get(place.id) || '/'));
  }

  /* ----- Liste ----- */
  async function load(path, { record = true } = {}) {
    const my = ++st.seq;
    view.classList.add('busy');
    try {
      const l = await guarded(() => fapi.list(st.place, path));
      if (my !== st.seq) return false;
      if (record && st.entries && st.path !== l.path && st.loaded) { st.back.push(st.path); st.fwd = []; }
      st.path = l.path; st.entries = l.entries; st.truncated = l.truncated; st.hidden = l.hidden; st.loaded = true;
      st.sel.clear();
      renderAll();
      return true;
    } catch (e) {
      if (my !== st.seq) return false;
      if (C.isCancelled(e)) { renderAll(); return false; }
      showFailure(C.errorMessage(e), () => load(path, { record }));
      return false;
    } finally { if (my === st.seq) view.classList.remove('busy'); }
  }
  const reload = () => load(st.path, { record: false });
  function showFailure(text, retry) {
    view.replaceChildren(h('div', { class: 'f-empty f-fail', role: 'alert' }, h('p', { text }), btn(t('files.retry'), kit ? 'm3-btn tonal mb m3-state' : 'dk-btn', retry)));
    stat.textContent = '';
  }

  function renderHead() {
    placeBtn.replaceChildren(icon(st.place && C.isRoot(st.place) ? 'root' : st.place && !C.isLocal(st.place) ? 'dns' : 'folder'), h('span', { class: 'f-place-t', text: label() }), icon('more'));
    placeBtn.onclick = pickPlace;
    crumbs.replaceChildren();
    if (st.place) {
      const parts = C.crumbsOf(st.path, st.place);
      parts.forEach((c, i) => {
        if (i) crumbs.append(h('span', { class: 'f-sep', 'aria-hidden': 'true', text: '›' }));
        crumbs.append(h('button', { type: 'button', class: 'f-crumb' + (i === parts.length - 1 ? ' cur' : ''), onclick: () => go(c.path), 'aria-current': i === parts.length - 1 ? 'location' : undefined, text: c.label }));
      });
      crumbs.scrollLeft = crumbs.scrollWidth;
    }
    tools.replaceChildren();
    if (st.place) {
      tools.append(tool(kit, 'back', t('common.back'), goBack, 'f-t-back'), tool(kit, 'up', t('desktopFiles.up'), () => go(C.parentOf(st.path, st.place)), 'f-t-up'), tool(kit, 'refresh', t('files.refresh'), () => reload()));
      if (info.write && !C.isLocal(st.place)) tools.append(tool(kit, 'newfolder', t('files.newFolder'), newFolder), tool(kit, 'upload', t('files.upload.action'), () => fileInput.click()));
      if (!C.isLocal(st.place)) tools.append(tool(kit, 'history', t('files.history'), showHistory));
    }
    searchBox.hidden = !(st.place && C.isLocal(st.place));
    searchInput.placeholder = t('desktopFiles.searchIn', { name: C.nameOf(st.path) || t('desktopFiles.root') });
    root.classList.toggle('f-has-place', !!st.place);
    const back = tools.querySelector('.f-t-back'); if (back) back.disabled = !st.back.length;
    const up = tools.querySelector('.f-t-up'); if (up) up.disabled = !st.path || st.path === '/';
    banner.replaceChildren();
    root.classList.toggle('f-is-root', C.isRoot(st.place));
    if (C.isRoot(st.place)) banner.append(h('div', { class: 'f-rootbar', role: 'note' }, icon('root'), h('span', { text: t('files.root.banner', { host: st.place.name }) })));
    if (st.place && !C.isLocal(st.place) && !info.write) banner.append(noteEl(t('files.writeOff'), 'hint'));
    banner.hidden = !banner.childNodes.length;
  }
  head.replaceChildren(placeBtn, crumbs, tools, searchBox);

  async function go(path) { if (!st.place) return; await load(path); }
  async function goBack() { if (!st.back.length) return; const to = st.back.pop(); const was = st.path; if (await load(to, { record: false })) st.fwd.push(was); else st.back.push(to); renderHead(); }

  function row(e) {
    const selected = st.sel.has(e.path);
    const canSelect = !!st.place;
    const iconName = e.kind === 'directory' || (e.kind === 'link' && e.resolved === 'directory') ? 'folder' : e.kind === 'link' ? 'link' : 'file';
    const check = h('button', { type: 'button', class: 'f-check', role: 'checkbox', 'aria-checked': String(selected), 'aria-label': t('files.select', { name: e.name }), onclick: (ev) => { ev.stopPropagation(); st.sel.toggle(e.path); renderSelection(); } }, icon(selected ? 'checkOn' : 'checkOff'));
    const main = h('button', { type: 'button', class: 'f-open', onclick: () => open(e), ondblclick: () => dbl(e), 'aria-label': e.openable ? t('files.openFolder', { name: e.name }) : e.name },
      h('span', { class: 'f-ic-wrap' + (iconName === 'folder' ? ' dir' : ''), }, icon(iconName)), h('span', { class: 'f-name', text: e.name, title: e.name }), h('span', { class: 'f-meta', text: C.entryMeta(e) }));
    return h('div', { class: 'f-row' + (selected ? ' sel' : '') + (e.openable ? ' dir' : ''), 'data-path': e.path, role: 'listitem' }, canSelect ? check : null, main);
  }
  function renderList() {
    view.replaceChildren();
    if (!st.place) { view.append(h('div', { class: 'f-empty' }, h('p', { text: t('files.chooseHint') }), kit ? placeList((p) => enter(p)) : null)); stat.textContent = ''; return; }
    if (st.query) return;
    if (!st.entries.length) view.append(h('div', { class: 'f-empty', text: t('desktopFiles.empty') }));
    else view.append(h('div', { class: 'f-list', role: 'list' }, st.entries.map(row)));
    const dirs = st.entries.filter((e) => e.openable).length;
    let line = tp('desktopFiles.items', st.entries.length);
    if (dirs) line = tp('desktopFiles.itemsDirs', dirs, { text: line });
    if (st.truncated) line = t('desktopFiles.itemsCut', { text: line, n: st.entries.length });
    if (st.hidden) line += ' · ' + tp('files.hidden', st.hidden);
    stat.textContent = line;
  }
  function renderAll() { renderHead(); renderList(); renderSelection(); }

  /* ----- Auswahl und ihre Aktionen ----- */
  function renderSelection() {
    for (const r of view.querySelectorAll('.f-row')) {
      const on = st.sel.has(r.dataset.path);
      r.classList.toggle('sel', on);
      const c = r.querySelector('.f-check'); if (c) { c.setAttribute('aria-checked', String(on)); c.replaceChildren(icon(on ? 'checkOn' : 'checkOff')); }
    }
    const sel = st.sel.entries(st.entries);
    selbar.hidden = !sel.length;
    selbar.replaceChildren();
    if (!sel.length) return;
    const one = sel.length === 1 ? sel[0] : null;
    const local = C.isLocal(st.place);
    const act = (name, label, fn, cls = '') => h('button', { type: 'button', class: `f-act-btn ${kit ? 'm3-btn tonal sm mb m3-state' : 'dk-btn'} ${cls}`.trim(), title: label, 'aria-label': label, onclick: fn }, icon(name), h('span', { class: 'f-act-t', text: label }));
    selbar.append(h('span', { class: 'f-count', text: t('files.selected', { n: sel.length }) }));
    if (one && C.isFileLike(one)) {
      if (!local) selbar.append(act('view', t('files.view'), () => viewText(one)));
      selbar.append(act('download', t('desktopFiles.download'), () => download(one)));
    }
    if (writable()) {
      if (one) selbar.append(act('edit', t('files.rename.action'), () => rename(one)));
      selbar.append(act('copy', t('files.copy.action'), () => copyOrMove('copy', sel)), act('move', t('files.move.action'), () => copyOrMove('move', sel)), act('delete', t('files.delete.action'), () => remove(sel), 'danger'));
    }
    selbar.append(act('close', t('files.clearSelection'), () => { st.sel.clear(); renderSelection(); }));
  }

  function open(e) {
    if (e.openable) return go(e.path);
    st.sel.toggle(e.path);
    renderSelection();
  }
  function dbl(e) { if (!e.openable && C.isFileLike(e)) { if (C.isLocal(st.place)) download(e); else viewText(e); } }

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

  async function viewText(e) {
    const ok = await readConfirmed('text', e);
    if (!ok) return;
    let r;
    try { r = await guarded(() => fapi.text(st.place, e.path, ok.token)); }
    catch (err) {
      if (C.isCancelled(err)) return;
      if (err.code === 'not_text') return say(t('files.notText', { name: e.name }));
      return say(C.errorMessage(err));
    }
    const pre = h('pre', { class: 'f-pre', tabindex: '0', text: r.text });          // Text, nie HTML
    const content = [h('p', { class: 'f-sub', text: `${e.path} · ${C.fileSize(r.size)}` }), pre];
    if (r.truncated) content.push(noteEl(t('files.textCut', { size: C.fileSize(1024 * 1024) }), 'hint'));
    const dlg = modal({ title: e.name, kit, content, id: 'text', actions: [{ id: 'dl', label: t('desktopFiles.download'), kind: 'ghost' }, { id: 'x', label: t('common.close'), kind: 'primary' }] });
    if ((await dlg.promise) === 'dl') download(e);
  }

  /* ----- Schreiben: zwei Schritte. Erst prepare (der Server prueft und beschreibt, was geschaehe), dann der Dialog nach dieser Antwort, dann die Aktion mit dem Token ----- */
  /** Schritt 1 mit Fehlermeldung; null bei einem Fehler oder Abbruch */
  async function prepare(action, args) {
    try { return await guarded(() => fapi.prepare(action, st.place, args)); }
    catch (e) { if (!C.isCancelled(e)) say(C.errorMessage(e)); return null; }
  }

  async function newFolder() {
    const name = await inputDialog(kit, { title: t('files.mkdir.title'), label: t('files.mkdir.nameLabel'), confirm: t('files.mkdir.next') });
    if (!name) return;
    const prep = await prepare('mkdir', { dir: st.path, name });
    if (!prep || !(await confirmDialog(kit, C.mkdirModel(prep), { expiresIn: prep.expiresIn }))) return;
    await runWrite(() => guarded(() => fapi.mkdir(st.place, prep.dir, prep.name, prep.token)), t('files.mkdir.done', { name: prep.name }));
  }
  async function rename(e) {
    const name = await inputDialog(kit, { title: t('files.rename.title'), label: t('files.rename.nameLabel'), value: e.name, confirm: t('files.mkdir.next') });
    if (!name || name === e.name) return;
    const prep = await prepare('rename', { paths: [e.path], name });
    const r = prep && await confirmDialog(kit, C.renameModel(prep), { expiresIn: prep.expiresIn });
    if (!r) return;
    await runWrite(() => guarded(() => fapi.rename(st.place, prep.paths[0], prep.name, r.ack, prep.token)), t('files.rename.done', { name: prep.name }));
  }
  async function runWrite(fn, doneText) {
    try { await fn(); say(doneText); await reload(); }
    catch (e) { if (!C.isCancelled(e)) say(C.errorMessage(e)); }
  }

  async function remove(sel) {
    const prep = await prepare('delete', { paths: sel.map((e) => e.path) });
    const model = prep && C.deleteModel(prep);
    const r = model && await confirmDialog(kit, model, { expiresIn: prep.expiresIn });
    if (!r) return;
    await runJob(model.title, 'delete', () => guarded(() => fapi.remove(st.place, prep.paths, prep.nonEmpty && r.ack, prep.token)));
    await reload();
  }

  /** Das Ziel waehlen (auch ein anderer Host), dann vorbereiten, bestaetigen, als Auftrag laufen lassen */
  async function copyOrMove(action, sel) {
    const target = await chooseTarget(action);
    if (!target) return;
    const prep = await prepare(action, { paths: sel.map((e) => e.path), target });
    const model = prep && C.copyModel(action, prep);
    const r = model && await confirmDialog(kit, model, { expiresIn: prep.expiresIn });
    if (!r) return;
    await runJob(model.title, action, () => guarded(() => fapi.copy(st.place, prep.paths, target, { move: action === 'move', overwrite: prep.conflicts.length > 0 && r.ack, token: prep.token })));
    if (target.place.id === st.place.id || action === 'move') await reload();
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
    w = C.watchJob(fapi, started.job, { onProgress: (j) => prog.update({ percent: C.jobPercent(j), text: [j.current || '', j.total ? `${j.done}/${j.total}` : '', j.bytesTotal ? `${C.fileSize(j.bytes)} / ${C.fileSize(j.bytesTotal)}` : ''].filter(Boolean).join(' · ') }) });
    
    let job;
    try { job = await w.promise; }
    catch (e) { await prog.finish({ ok: false, text: C.errorMessage(e) }); return; }
    const ok = job.state === 'done';
    await prog.finish({ ok, text: C.jobSummary(job, action), details: [] });
  }

  /* ----- Hochladen ----- */
  fileInput.addEventListener('change', () => { const files = [...fileInput.files]; fileInput.value = ''; if (files.length) upload(files); });
  let dragDepth = 0;
  const hasFiles = (e) => e.dataTransfer && [...(e.dataTransfer.types || [])].includes('Files');
  main.addEventListener('dragenter', (e) => { if (!hasFiles(e) || !writable()) return; e.preventDefault(); dragDepth++; drop.hidden = false; });
  main.addEventListener('dragover', (e) => { if (hasFiles(e) && writable()) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
  main.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; drop.hidden = true; } });
  main.addEventListener('drop', (e) => { if (!hasFiles(e)) return; e.preventDefault(); dragDepth = 0; drop.hidden = true; if (writable()) upload([...e.dataTransfer.files].filter((f) => f.type || f.size || f.name)); else say(t('files.writeOff')); });

  async function upload(files) {
    if (!writable()) return say(t('files.writeOff'));
    const dir = st.path, place = st.place;
    const tooBig = files.filter((f) => info.maxBytes && f.size > info.maxBytes);
    if (tooBig.length) say(t('files.err.too_large'));
    files = files.filter((f) => !tooBig.includes(f));
    if (!files.length) return;
    if (files.length > 100) return say(t('files.err.too_many_items'));
    const names = [...new Set(files.map((f) => f.name))];
    files = names.map((n) => files.find((f) => f.name === n));
    const prep = await prepare('upload', { dir, names });
    const r = prep && await confirmDialog(kit, C.uploadModel(prep, files), { expiresIn: prep.expiresIn });
    if (!r) return;
    const overwrite = new Set(prep.conflicts);
    let aborted = false, current = null;
    const prog = progressDialog(kit, t('files.upload.title'), { onCancel: () => { aborted = true; if (current) current.abort(); } });
    let okN = 0; const failed = [];
    const send = (f, onProgress) => (current = fapi.upload(place, prep.dir, f, { overwrite: overwrite.has(f.name) && r.ack, token: prep.token, onProgress }));
    for (let i = 0; i < files.length && !aborted; i++) {
      const f = files[i];
      prog.update({ percent: 0, text: `${f.name} (${i + 1}/${files.length})` });
      const progress = (done, total) => prog.update({ percent: total ? Math.round((done / total) * 100) : null, text: `${f.name} (${i + 1}/${files.length}) · ${C.fileSize(done)} / ${C.fileSize(total)}` });
      try { await retryOpen(place, () => send(f, progress).promise, () => send(f, progress).promise); okN++; }
      catch (e) { if (C.isCancelled(e) && aborted) break; failed.push(`${f.name}: ${C.errorMessage(e)}`); }
    }
    const ok = !failed.length && !aborted;
    await prog.finish({ ok, text: aborted ? t('files.upload.cancelled', { n: okN }) : ok ? t('files.upload.done', { n: tp('files.nFiles', okN) }) : t('files.upload.partial', { ok: okN, failed: failed.length }), details: failed });
    if (st.place && st.place.id === place.id && st.path === dir) await reload();
  }
  /** Fehlt die Sitzung beim Start des Hochladens (Leerlauf), wird der Ort neu geoeffnet und die Datei noch einmal gesendet: die Bestaetigung ist dann noch nicht eingeloest (das passiert erst mit der Sitzung) */
  async function retryOpen(place, first, again) {
    try { return await first(); } catch (e) {
      if (['password_required', 'password_unreadable', 'host_key_new', 'host_key_changed'].includes(e.code)) { await ensureOpen(place); return again(); }
      throw e;
    }
  }

  /* ----- Zielauswahl: ein kleiner Browser nur fuer Ordner ----- */
  async function chooseTarget(action) {
    const targetPlaces = [...places.flat];
    if (!targetPlaces.length) return null;
    let place = targetPlaces.find((p) => p.id === st.place.id) || targetPlaces[0];
    let dir = st.path;
    const select = h('select', { class: 'f-tsel', 'aria-label': t('files.targetPlace') }, targetPlaces.map((p) => h('option', { value: p.id, text: p.label })));
    select.value = place.id;
    const crumbsEl = h('nav', { class: 'f-crumbs f-tcrumbs', 'aria-label': t('desktopFiles.path') });
    const listEl = h('div', { class: 'f-tlist', role: 'list' });
    const msg = h('p', { class: 'f-msg warn', role: 'alert', hidden: true });
    const dlg = modal({ title: t(action === 'move' ? tk('files.move.target') : tk('files.copy.target')), kit, id: 'target', content: [h('label', { class: 'f-field' }, h('span', { text: t('files.targetPlace') }), select), crumbsEl, listEl, msg],
      actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: t('files.target.here'), kind: 'primary', disabled: true }] });
    let seq = 0;
    const show = async (p, path) => {
      const my = ++seq; dlg.setDisabled('yes', true); msg.hidden = true;
      try {
        if (p.id !== place.id || !st.home.has(p.id)) await ensureOpen(p);
        const l = await guarded0(p, () => fapi.list(p, path));
        if (my !== seq) return;
        place = p; dir = l.path;
        crumbsEl.replaceChildren();
        C.crumbsOf(dir, place).forEach((c, i, all) => { if (i) crumbsEl.append(h('span', { class: 'f-sep', 'aria-hidden': 'true', text: '›' })); crumbsEl.append(h('button', { type: 'button', class: 'f-crumb' + (i === all.length - 1 ? ' cur' : ''), onclick: () => show(place, c.path), text: c.label })); });
        const folders = l.entries.filter((e) => e.openable);
        listEl.replaceChildren(...folders.map((e) => h('button', { type: 'button', class: 'f-trow', role: 'listitem', onclick: () => show(place, e.path) }, h('span', { class: 'f-ic-wrap dir' }, icon('folder')), h('span', { class: 'f-name', text: e.name }))), ...(folders.length ? [] : [h('p', { class: 'f-empty', text: t('files.noFolders') })]));
        dlg.setDisabled('yes', false);
      } catch (e) {
        if (my !== seq) return;
        if (C.isCancelled(e)) { select.value = place.id; return; }
        msg.textContent = C.errorMessage(e); msg.hidden = false;
      }
    };
    const guarded0 = async (p, fn) => { try { return await fn(); } catch (e) { if (['password_required', 'password_unreadable', 'host_key_new', 'host_key_changed', 'connection_lost'].includes(e.code)) { await ensureOpen(p); return fn(); } throw e; } };
    select.addEventListener('change', () => { const p = targetPlaces.find((x) => x.id === select.value); show(p, st.home.get(p.id) || ''); });
    show(place, dir);
    const which = await dlg.promise;
    return which === 'yes' ? { place, dir } : null;
  }

  /* ----- Verlauf ----- */
  async function showHistory() {
    const list = h('div', { class: 'f-hist' }, h('p', { text: t('hostinfo.stLoading') }));
    const dlg = modal({ title: t('files.history'), kit, id: 'hist', content: [list], actions: [{ id: 'x', label: t('common.close'), kind: 'primary' }] });
    try {
      const r = await fapi.audit(40);
      const rows = r.entries.map(C.auditLine);
      list.replaceChildren(...(rows.length ? rows.map((l) => h('div', { class: 'f-hrow' + (l.failed ? ' bad' : '') },
        h('span', { class: 'f-hwhen', text: `${l.when} · ${l.host}` }), h('span', { class: 'f-hwhat', text: [l.action, l.path, l.items].filter(Boolean).join(' · ') }), h('span', { class: 'f-hres', text: l.result + (l.detail ? ` (${l.detail})` : '') }))) : [h('p', { text: t('files.historyEmpty') })]));
    } catch (e) { list.replaceChildren(noteEl(C.errorMessage(e), 'warn')); }
    await dlg.promise;
  }

  /* ----- Suche (nur im gemeinsamen Ordner) ----- */
  let timer = null;
  async function search() {
    const text = searchInput.value.trim();
    if (!text) { if (st.query) { st.query = ''; await reload(); } return; }
    const my = ++st.seq; st.query = text; view.classList.add('busy');
    try {
      const r = await fapi.search(st.path, text);
      if (my !== st.seq) return;
      const entries = r.results.map((e) => C.normalizeListing(st.place, { entries: [e], path: '' }).entries[0]);
      st.entries = entries;
      view.replaceChildren(entries.length ? h('div', { class: 'f-list', role: 'list' }, entries.map(row)) : h('div', { class: 'f-empty', text: t('desktopFiles.noMatch', { text }) }));
      const hits = tp('desktopFiles.hits', entries.length);
      stat.textContent = r.truncated ? t('desktopFiles.hitsCut', { text: hits }) : hits;
    } catch (e) { if (my === st.seq) showFailure(C.errorMessage(e) || t('desktopFiles.searchFailed'), search); }
    finally { if (my === st.seq) view.classList.remove('busy'); }
  }
  searchInput.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(search, 300); });
  searchInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') { clearTimeout(timer); search(); } else if (e.key === 'Escape' && searchInput.value) { searchInput.value = ''; e.stopPropagation(); search(); } });

  /* ----- Start ----- */
  (async () => {
    try { info = await fapi.info(); } catch { info = { local: true, write: false, maxBytes: 0 }; }
    refreshPlaces();
    renderAll();
    const last = store.get();
    const startPlace = (info.local && !kit && (!last || last === C.LOCAL_ID)) ? places.local : null;      // am Desktop gleich der gemeinsame Ordner; ein Host wird nie ungefragt geoeffnet
    if (startPlace) enter(startPlace);
  })();

  return {
    refreshPlaces,
    /** Oeffnet einen Ort (oder den ersten Host, den es gibt) */
    enter: (id, path) => { const p = C.findPlace(places, id); if (p) return enter(p, path); },
    reload: () => (st.place ? reload() : Promise.resolve()),
    destroy() { st.dead = true; clearInterval(keepAlive); if (st.place && !C.isLocal(st.place)) fapi.close(st.place.id); box.replaceChildren(); },
    state: st,
  };
}

/* ---------- Abfragen: Passwort und Host-Schluessel ---------- */
function passwordDialog(kit, place, wrong) {
  const field = h('input', { type: 'password', class: 'f-input', autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': t('files.password.label'), maxlength: 256 });
  const dlg = modal({ title: t('files.password.title', { host: place.label }), kit, id: 'pw', content: [
    h('p', { class: 'f-sentence', text: t('files.password.sentence', { address: place.address }) }),
    wrong ? noteEl(t('files.err.logon_failed'), 'warn') : null,
    h('label', { class: 'f-field' }, h('span', { text: t('files.password.label') }), field),
    noteEl(t('files.password.note'), 'hint'),
  ], actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: t('files.password.connect'), kind: 'primary', disabled: true }] });
  field.addEventListener('input', () => dlg.setDisabled('yes', !field.value));
  field.addEventListener('keydown', (e) => { if (e.key === 'Enter' && field.value) { e.preventDefault(); dlg.close('yes'); } });
  field.focus();
  return dlg.promise.then((r) => { const v = r === 'yes' ? field.value : null; field.value = ''; return v; });
}
function hostKeyDialog(kit, place, data, changed) {
  const dlg = modal({ title: t(changed ? tk('files.hostKey.changedTitle') : tk('files.hostKey.newTitle'), { host: place.label }), kit, id: 'hk', content: [
    h('p', { class: 'f-sentence', text: t(changed ? tk('files.hostKey.changedText') : tk('files.hostKey.newText'), { host: place.label }) }),
    rowsEl([[t('files.hostKey.algorithm'), data.algorithm], [t('files.hostKey.fingerprint'), data.fingerprint], ...(changed && data.known ? [[t('files.hostKey.known'), data.known]] : [])]),
    changed ? noteEl(t('files.hostKey.changedWarn'), 'warn') : noteEl(t('files.hostKey.newNote'), 'hint'),
  ], actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: t(changed ? tk('files.hostKey.acceptChanged') : tk('files.hostKey.accept')), kind: changed ? 'danger' : 'primary' }] });
  return dlg.promise.then((r) => r === 'yes');
}
