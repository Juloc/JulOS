/* =====================================================================
 * Dialoge als Fenster (docs/FILES-EXPLORER.md, "Dialogs are windows"): auf dem Desktop oeffnet jeder Dialog der Dateien-App als bewegliches Fenster im Fenstermanager von desktop.js,
 * statt die ganze Seite mit showModal() zu sperren. Derselbe Vertrag wie modal() in files-ui.js: { el, close(result), promise, setDisabled, setLabel, setHidden }; das Versprechen
 * liefert die id der gedrueckten Taste oder null.
 *
 * Regeln (Schutz, nicht Optik):
 *  - Schliessen (Taste im Titel, Escape, Schliessen des Fensters, das den Dialog geoeffnet hat, Abmelden) heisst immer "abgebrochen": das Ergebnis ist null, nie eine Taste. Eine Bestaetigung
 *    wird so nie durch Schliessen erteilt. Ein Dialog darf das Schliessen ablehnen (Ereignis "cancel", wie bei <dialog>: preventDefault), zum Beispiel Fortschritt oder ein Editor mit Aenderungen.
 *  - Das Fenster sperrt nichts und faengt Tab nicht ein: der Fokus geht beim Oeffnen hinein und beim Schliessen zurueck zum Ausloeser.
 *  - Mit key oeffnet ein Dialog nur einmal; ein zweiter Aufruf holt das offene Fenster nach vorn.
 * desktop.js meldet die Fenster-Fabrik mit setDialogHost an; ohne sie (Handy, Seite ohne Desktop) gibt available() false und der Aufrufer nimmt sein Blatt.
 * ===================================================================== */

let frame = null;
const openKeys = new Map();

/** Von desktop.js: legt ein Dialog-Fenster an (siehe dialogFrame dort) */
export function setDialogHost(fn) { frame = fn; }
export const dialogWindowsAvailable = () => !!frame && document.documentElement.classList.contains('desktop');

const FOCUSABLE = 'input:not([disabled]):not([type=hidden]), textarea:not([disabled]), select:not([disabled]), button:not([disabled]):not([hidden])';
const node = (tag, cls) => { const e = document.createElement(tag); if (cls) e.className = cls; return e; };

/**
 * @param {{title?: string, content?: Node[], actions?: {id: string, label: string, kind?: string, disabled?: boolean, keepOpen?: boolean, onclick?: Function}[],
 *   key?: string, owner?: Element|null, width?: number, height?: number, resizable?: boolean, root?: boolean, focus?: string|Element, cls?: string}} o
 * actions: wie bei modal() (keepOpen: die Taste schliesst nicht). focus: die id einer Taste oder ein Element, das den Fokus bekommt (sonst das erste Bedienelement).
 * root: ein Dialog zu einem Root-Ort, die Titelleiste ist rot. Gibt null zurueck, wenn es keinen Desktop gibt.
 */
export function openDialogWindow({ title = '', content = [], actions = [], key = '', owner = null, width = 480, height = 0, resizable = false, root = false, focus = '', cls = '' }) {
  if (!dialogWindowsAvailable()) return null;
  if (key && openKeys.has(key)) { const old = openKeys.get(key); old.focus(); return old; }
  const opener = document.activeElement;
  const el = node('div', 'dw-body f-dlg');
  const bar = node('div', 'row dlg-btns f-dlg-btns dw-btns');
  const buttons = new Map();
  let done, finished = false, result = null, win = null;
  const promise = new Promise((r) => { done = r; });

  const requestClose = () => { const ev = new Event('cancel', { cancelable: true }); el.dispatchEvent(ev); return !ev.defaultPrevented; };
  // Der Fokus geht zum Ausloeser zurueck, wenn er in diesem Fenster lag oder nirgends (sonst tippt der Nutzer gerade woanders)
  const giveBackFocus = () => {
    const a = document.activeElement;
    if (opener && opener !== document.body && opener.isConnected && (a === document.body || !a || el.contains(a))) opener.focus({ preventScroll: true });
  };
  const finish = () => { if (finished) return; finished = true; if (key) openKeys.delete(key); giveBackFocus(); done(result); };
  const close = (r = null) => { if (finished) return; result = r; win.close(); finish(); };     // close() zuerst oder Schliessen des Fensters zuerst: einmal, mit dem ersten Ergebnis

  win = frame({ title, w: width, h: height, resizable, owner, cls: (root ? 'dw-root ' : '') + cls, onClose: finish, onRequestClose: requestClose });
  for (const a of actions) {
    const b = node('button', `${a.kind === 'primary' ? 'primary ' : a.kind === 'danger' ? 'primary danger ' : 'ghost '}f-act`.trim());
    b.type = 'button'; b.dataset.act = a.id; b.disabled = !!a.disabled; b.textContent = a.label;
    b.addEventListener('click', () => { if (a.onclick) a.onclick(b); if (!a.keepOpen) close(a.id); });
    buttons.set(a.id, b); bar.append(b);
  }
  el.append(...content);
  if (actions.length) el.append(bar);
  win.body.append(el);
  win.setTitle(title);
  win.place(); queueMicrotask(win.place);         // gleich und noch einmal, wenn der Aufrufer seinen Inhalt im selben Zug fertig gefuellt hat (die Hoehe bestimmt die Mitte)
  // Escape schliesst nur dieses Fenster (nie ein Programmfenster) und zaehlt als "abbrechen"; ein Dialog, der das Schliessen ablehnt, behaelt sein Fenster
  win.el.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || e.defaultPrevented) return;
    e.preventDefault(); e.stopPropagation();
    if (requestClose()) close(null);
  });
  const target = typeof focus === 'string' ? (focus ? buttons.get(focus) : null) : focus;
  (target || el.querySelector(FOCUSABLE) || win.el).focus({ preventScroll: true });

  const handle = {
    el, close, promise, window: win,
    setTitle: (x) => win.setTitle(x),
    focus: () => win.focus(),
    setDisabled: (id, on) => { const b = buttons.get(id); if (b) b.disabled = !!on; },
    setLabel: (id, text) => { const b = buttons.get(id); if (b) b.textContent = text; },
    setHidden: (id, on) => { const b = buttons.get(id); if (b) b.hidden = !!on; },
  };
  if (key) openKeys.set(key, handle);
  return handle;
}
