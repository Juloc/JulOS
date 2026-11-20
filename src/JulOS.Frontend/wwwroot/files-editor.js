/* =====================================================================
 * Einfacher Editor der Dateien-App (docs/FILES-EXPLORER.md): Text bis 1 MiB, Monospace, Zeilennummern, Strg+S, Markierung "geaendert".
 * Gespeichert wird nicht hier: onSave(text) ist die bestaetigte Schreib-Aktion der App (prepare, Dialog, Upload) und gibt true zurueck, wenn die Datei geschrieben wurde.
 * Der Text steht nur als Wert eines Textfelds, nie als HTML. Binaerdateien oeffnen nie hier (die App prueft Endung, Groesse und die Antwort des Servers).
 * ===================================================================== */
import { t } from './i18n.js';
import { h, modal, noteEl } from './files-ui.js';
import { safeName } from './files-explorer.js';

/** Zeilenende der Datei: 'crlf' wenn jedes \n ein \r\n ist, 'mixed' bei beidem, sonst 'lf'. Ein Textfeld rechnet \r\n immer zu \n um, darum merken wir es uns und schreiben es so zurueck. */
export function detectEol(text) {
  const lf = (text.match(/\n/g) || []).length, crlf = (text.match(/\r\n/g) || []).length;
  return crlf === 0 ? 'lf' : crlf === lf ? 'crlf' : 'mixed';
}
export const withEol = (text, eol) => (eol === 'crlf' ? text.replace(/\n/g, '\r\n') : text);

/**
 * @param {{kit?: boolean, name: string, path: string, text: string, sizeText?: string, readOnly?: boolean, why?: string, onSave: (text: string) => Promise<boolean>}} o
 * why: warum nur lesen (Text unter dem Titel). Gibt { promise, close } zurueck; das Versprechen endet, wenn der Editor zu ist.
 */
export function openEditor({ kit = false, name, path, text, sizeText = '', readOnly = false, why = '', onSave }) {
  const eol = detectEol(text);
  let original = text.replace(/\r\n/g, '\n');
  let busy = false, closing = false;
  const mark = h('span', { class: 'f-ed-mark', hidden: true, role: 'status', text: t('files.edit.changed') });
  const ta = h('textarea', { class: 'f-ed-text', spellcheck: 'false', autocomplete: 'off', autocapitalize: 'off', wrap: 'off', 'aria-label': t('files.edit.label', { name: safeName(name) }), readonly: readOnly });
  ta.value = original;
  const gutter = h('pre', { class: 'f-ed-gutter', 'aria-hidden': 'true' });
  const lines = () => { const n = ta.value.split('\n').length; gutter.textContent = Array.from({ length: n }, (_, i) => i + 1).join('\n'); };
  lines();
  const notes = [];
  if (readOnly && why) notes.push(noteEl(why, 'hint'));
  if (!readOnly && eol === 'mixed') notes.push(noteEl(t('files.edit.mixedEol'), 'hint'));
  const modified = () => ta.value !== original;
  const refresh = () => { mark.hidden = !modified(); dlg.setDisabled('save', readOnly || busy || !modified()); };
  const title = h('span', { class: 'f-ed-title' }, h('span', { class: 'f-ed-name', text: safeName(name) }), ' ', mark);
  const dlg = modal({ title: '', kit, id: 'edit', content: [h('p', { class: 'f-sub', text: `${safeName(path)}${sizeText ? ' · ' + sizeText : ''}` }), ...notes, h('div', { class: 'f-ed' }, gutter, ta)], actions: [
    { id: 'close', label: t('common.close'), kind: 'ghost', keepOpen: true, onclick: () => tryClose() },
    ...(readOnly ? [] : [{ id: 'save', label: t('files.edit.save'), kind: 'primary', keepOpen: true, disabled: true, onclick: () => save() }]),
  ] });
  dlg.el.classList.add('f-edit');
  dlg.el.querySelector('h2').replaceChildren(title);
  if (readOnly) dlg.setDisabled('save', true);

  ta.addEventListener('input', () => { lines(); refresh(); });
  ta.addEventListener('scroll', () => { gutter.scrollTop = ta.scrollTop; });
  dlg.el.addEventListener('keydown', (e) => { if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 's') { e.preventDefault(); save(); } });
  dlg.el.addEventListener('cancel', (e) => { e.preventDefault(); tryClose(); });                  // Escape fragt bei ungespeicherten Aenderungen nach
  const stop = (e) => { if (modified()) e.preventDefault(); };
  window.addEventListener('beforeunload', stop);
  dlg.promise.then(() => window.removeEventListener('beforeunload', stop));

  async function save() {
    if (readOnly || busy || !modified()) return;
    busy = true; refresh();
    try { if (await onSave(withEol(ta.value, eol))) original = ta.value; }
    finally { busy = false; refresh(); ta.focus(); }
  }
  async function tryClose() {
    if (closing) return;
    if (modified()) {
      closing = true;
      const ask = modal({ title: t('files.edit.discardTitle'), kit, id: 'discard', content: [h('p', { class: 'f-sentence', text: t('files.edit.discardText', { name: safeName(name) }) })], actions: [{ id: 'no', label: t('files.edit.keepEditing'), kind: 'ghost' }, { id: 'yes', label: t('files.edit.discard'), kind: 'danger' }] });
      const r = await ask.promise; closing = false;
      if (r !== 'yes') { ta.focus(); return; }
    }
    dlg.close('close');
  }
  setTimeout(() => ta.focus({ preventScroll: true }), 60);
  return { promise: dlg.promise, close: () => dlg.close('close'), modified };
}
