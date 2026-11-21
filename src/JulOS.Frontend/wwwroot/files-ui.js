/* =====================================================================
 * Bausteine der Dateien-App (files-app.js, files-menu.js, files-editor.js, files-tree.js): Symbole (SVG, nie Emojis), Elemente bauen, Dialoge (Bestaetigung, Namensabfrage, Fortschritt).
 * Alles, was von Server oder Ziel kommt (Namen, Pfade, Text), wird mit textContent gesetzt, nie als HTML.
 * ===================================================================== */
import { t } from './i18n.js';
import * as C from './files-core.js';
import { confirmReady } from './confirm.js';

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
  forward: 'M12 4l-1.41 1.41L16.17 11H4v2h12.17l-5.58 5.59L12 20l8-8z',
  chevR: 'M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z',
  chevD: 'M16.59 8.59L12 13.17 7.41 8.59 6 10l6 6 6-6z',
  chevU: 'M7.41 15.41L12 10.83l4.59 4.58L18 14l-6-6-6 6z',
  cut: 'M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm0 12c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm6-7.5c-.28 0-.5-.22-.5-.5s.22-.5.5-.5.5.22.5.5-.22.5-.5.5zM19 3l-6 6 2 2 7-7V3z',
  paste: 'M19 2h-4.18C14.4.84 13.3 0 12 0c-1.3 0-2.4.84-2.82 2H5c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm7 18H5V4h2v3h10V4h2v16z',
  pin: 'M16 9V4h1c.55 0 1-.45 1-1s-.45-1-1-1H7c-.55 0-1 .45-1 1s.45 1 1 1h1v5c0 1.66-1.34 3-3 3v2h5.97v7l1 1 1-1v-7H19v-2c-1.66 0-3-1.34-3-3z',
  newfile: 'M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 14h-3v3h-2v-3H8v-2h3v-3h2v3h3v2zm-3-7V3.5L18.5 9H13z',
  info: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z',
  save: 'M17 3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z',
  list: 'M3 14h4v-4H3v4zm0 5h4v-4H3v4zM3 9h4V5H3v4zm5 5h13v-4H8v4zm0 5h13v-4H8v4zM8 5v4h13V5H8z',
  tiles: 'M3 3v8h8V3H3zm6 6H5V5h4v4zm-6 4v8h8v-8H3zm6 6H5v-4h4v4zm4-16v8h8V3h-8zm6 6h-4V5h4v4zm-6 4v8h8v-8h-8zm6 6h-4v-4h4v4z',
  sort: 'M3 18h6v-2H3v2zM3 6v2h18V6H3zm0 7h12v-2H3v2z',
  check: 'M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z',
  home: 'M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z',
  add: 'M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z',
  open: 'M20 6h-8l-2-2H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm0 12H4V8h16v10z',
  blank: 'M6 2c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6H6zm7 7V3.5L18.5 9H13z',
  image: 'M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z',
  code: 'M9.4 16.6L4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0l4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4z',
  archive: 'M20.54 5.23l-1.39-1.68C18.88 3.21 18.47 3 18 3H6c-.47 0-.88.21-1.16.55L3.46 5.23C3.17 5.57 3 6.02 3 6.5V19c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V6.5c0-.48-.17-.93-.46-1.27zM12 17.5L6.5 12H10v-2h4v2h3.5L12 17.5zM5.12 5l.81-1h12l.94 1H5.12z',
  sel: 'M3 5h2V3c-1.1 0-2 .9-2 2zm0 8h2v-2H3v2zm4 8h2v-2H7v2zM3 9h2V7H3v2zm10-6h-2v2h2V3zm6 0v2h2c0-1.1-.9-2-2-2zM5 21v-2H3c0 1.1.9 2 2 2zm-2-4h2v-2H3v2zM9 3H7v2h2V3zm2 18h2v-2h-2v2zm8-8h2v-2h-2v2zm0 8c1.1 0 2-.9 2-2h-2v2zm0-12h2V7h-2v2zm0 8h2v-2h-2v2zm-4 4h2v-2h-2v2zm0-16h2V3h-2v2z',
  dots: 'M6 10c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm12 0c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm-6 0c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z',
  sidebar: 'M3 4h18c.55 0 1 .45 1 1v14c0 .55-.45 1-1 1H3c-.55 0-1-.45-1-1V5c0-.55.45-1 1-1zm1 2v12h4V6H4zm6 0v12h10V6H10z',
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
export const btn = (label, cls, onclick, extra = {}) => h('button', { type: 'button', class: cls, onclick, ...extra }, label);

/** Eine Taste mit Symbol und Text; auf dem Handy die kleinen Tasten der Bausteinsammlung (docs/ui-kit.html) */
export const tool = (kit, name, label, onclick, cls = '') => h('button', { type: 'button', class: `f-tool ${kit ? 'm3-iconbtn m3-state mb' : 'dk-btn sq'} ${cls}`.trim(), title: label, 'aria-label': label, onclick }, icon(name));

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
  let finished = false, result = null;
  const finish = () => { if (finished) return; finished = true; dlg.remove(); done(result); };       // einmal, egal ob "close" oder close() zuerst kommt (ein verdecktes Fenster liefert das Ereignis spaet)
  const close = (r = null) => { result = r; if (dlg.open) dlg.close(); finish(); };
  for (const a of actions) {
    const b = h('button', { type: 'button', class: `${a.kind === 'primary' ? 'primary ' : a.kind === 'danger' ? 'primary danger ' : 'ghost '}${kit ? 'mb ' : ''}f-act`.trim(), 'data-act': a.id, disabled: !!a.disabled, onclick: () => { if (a.onclick) a.onclick(b); if (!a.keepOpen) close(a.id); } }, a.label);
    buttons.set(a.id, b); bar.append(b);
  }
  dlg.append(h('h2', { id: 'fdlgT' + (id || ''), text: title }), ...content, bar);
  dlg.addEventListener('close', finish);                                                            // Escape schliesst ohne unser close(): Ergebnis bleibt null
  dlg.addEventListener('click', (e) => { if (e.target === dlg) { const r = dlg.getBoundingClientRect(); if (e.clientY < r.top || e.clientY > r.bottom || e.clientX < r.left || e.clientX > r.right) close(null); } });
  document.body.appendChild(dlg);
  dlg.showModal();
  return { el: dlg, close, promise, setDisabled: (aid, on) => { const b = buttons.get(aid); if (b) b.disabled = !!on; }, setLabel: (aid, text) => { const b = buttons.get(aid); if (b) b.textContent = text; }, setHidden: (aid, on) => { const b = buttons.get(aid); if (b) b.hidden = !!on; } };
}

export const rowsEl = (rows) => h('dl', { class: 'f-rows' }, rows.map(([k, v]) => [h('dt', { text: k }), h('dd', { text: v })]));
export const noteEl = (text, kind = '') => h('p', { class: 'f-msg ' + kind, role: kind === 'warn' ? 'alert' : undefined, text });

/**
 * Bestaetigung einer Aktion (Schritt 2 der Zweischritt-Bestaetigung): der Satz, die Zeilen (Host, Pfad, Anzahl), die Warnung der Stufe (gelb, bei "sehr gefaehrlich" dazu eine rote Zeile) und, wenn der Server es
 * verlangt, ein Haekchen ("Ich habe die Warnung gelesen") oder der Rechnername. Routine zeigt nur Satz, Zeilen und die Taste. Alles stammt aus der Antwort des Servers (prepare); der Server prueft es noch einmal.
 * Die Bestaetigung gilt nur kurz (expiresIn Sekunden): danach ist die Taste aus und der Dialog sagt es. Gibt { ack, typed, answer } oder null (abgebrochen); answer ist, was die Aktion mitschicken soll.
 */
export async function confirmDialog(kit, model, { expiresIn = 0 } = {}) {
  const wrap = h('div', { class: 'f-confirm' });
  const dlg = modal({ title: model.title, kit, content: [wrap], actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: model.confirm, kind: model.danger ? 'danger' : 'primary', disabled: !!model.ack || !!model.typed }] });
  let check = null, field = null, timer = null;
  const ready = () => (!model.ack || !!(check && check.checked)) && (!model.typed || confirmReady({ needsCheck: false, typed: model.typed }, { typed: field ? field.value : '' }));
  if (model.root) wrap.append(h('div', { class: 'f-rootnote', role: 'alert' }, icon('root'), h('span', { text: model.rootNote })));
  wrap.append(h('p', { class: 'f-sentence', text: model.sentence }), rowsEl(model.rows));
  if (model.note) wrap.append(noteEl(model.note));
  if (model.levelWarn) wrap.append(noteEl(model.levelWarn, 'caution'));
  if (model.criticalWarn) wrap.append(noteEl(model.criticalWarn, 'warn'));
  if (model.warning) wrap.append(noteEl(model.warning, 'caution'));
  if (model.ack) {
    check = h('input', { type: 'checkbox', id: 'fAck' });
    check.addEventListener('change', () => dlg.setDisabled('yes', !ready()));
    wrap.append(h('label', { class: 'f-ack check', for: 'fAck' }, check, ' ', model.ack));
  }
  if (model.typed) {
    field = h('input', { type: 'text', id: 'fTyped', autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': t('hostact.typeLabel') });
    field.addEventListener('input', () => dlg.setDisabled('yes', !ready()));
    wrap.append(h('label', { class: 'f-typed', for: 'fTyped' }, h('span', { text: t('hostact.typePrompt', { name: model.typed }) }), field));
  }
  if (expiresIn > 0) timer = setTimeout(() => { dlg.setDisabled('yes', true); wrap.append(noteEl(C.errText('confirmation_expired'), 'warn')); }, expiresIn * 1000);
  const which = await dlg.promise;
  clearTimeout(timer);
  if (which !== 'yes') return null;
  const ack = !!(check && check.checked), typed = field ? field.value : '';
  return { ack, typed, answer: { ...(check ? { acknowledged: ack } : {}), ...(model.typed ? { typed } : {}) } };
}

/** Ein Name wird abgefragt (neuer Ordner, Umbenennen); nur Eingabe, geschrieben wird erst nach der Bestaetigung des Servers. Gibt den Namen oder null. */
export async function inputDialog(kit, { title, label, value = '', confirm, selectBase = false }) {
  const field = h('input', { type: 'text', class: 'f-input', value, autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': label, maxlength: 255 });
  const dlg = modal({ title, kit, content: [h('label', { class: 'f-field' }, h('span', { text: label }), field)], actions: [{ id: 'no', label: t('common.cancel'), kind: 'ghost' }, { id: 'yes', label: confirm, kind: 'primary', disabled: !C.validName(value) }] });
  const refresh = () => { const ok = C.validName(field.value); dlg.setDisabled('yes', !ok); if (field.value && !ok) field.setAttribute('aria-invalid', 'true'); else field.removeAttribute('aria-invalid'); };
  field.addEventListener('input', refresh);
  field.addEventListener('keydown', (e) => { if (e.key === 'Enter' && C.validName(field.value)) { e.preventDefault(); dlg.close('yes'); } });
  setTimeout(() => { field.focus(); const dot = selectBase ? value.lastIndexOf('.') : -1; if (dot > 0) field.setSelectionRange(0, dot); else field.select(); }, 60);
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
