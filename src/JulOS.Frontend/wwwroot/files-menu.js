/* =====================================================================
 * Menue der Dateien-App: Rechtsklick-Menue, die Menues "Ansicht" und "Neu". Ein Menue zur Zeit (role="menu", Pfeiltasten, Enter, Esc); auf dem Handy ein Blatt von unten.
 * items: [{ id, label, icon, shortcut, disabled, danger, checked, radio, title, run }] oder { sep: true } oder { heading: 'Text' }. Texte kommen vom Aufrufer (nie ungeprueftes HTML).
 * ===================================================================== */
import { h, icon } from './files-ui.js';

let current = null;
export const closeMenu = () => { if (current) current.close(); };

/** Oeffnet ein Menue an einer Stelle (x, y in Fenster-Pixeln) oder unter einem Element (anchor). Gibt { close } zurueck. */
export function openMenu({ items, x = 0, y = 0, anchor = null, kit = false, label = '', onClose = () => {} }) {
  closeMenu();
  const back = document.activeElement;
  const menu = h('div', { class: 'f-menu' + (kit ? ' sheet' : ''), role: 'menu', tabindex: '-1', 'aria-label': label || undefined });
  const shield = kit ? h('div', { class: 'f-menu-shield' }) : null;
  const rows = [];
  let last = 'sep';
  for (const it of items) {
    if (it.sep) { if (last !== 'sep') menu.append(h('div', { class: 'f-mi-sep', role: 'separator' })); last = 'sep'; continue; }
    if (it.heading) { menu.append(h('div', { class: 'f-mi-head', role: 'presentation', text: it.heading })); last = 'head'; continue; }
    const check = it.checked !== undefined;
    const row = h('button', {
      type: 'button', class: 'f-mi' + (it.danger ? ' f-mi-danger' : ''), role: check ? (it.radio ? 'menuitemradio' : 'menuitemcheckbox') : 'menuitem', tabindex: '-1',
      'aria-checked': check ? String(!!it.checked) : undefined, 'aria-disabled': it.disabled ? 'true' : undefined, title: it.title || undefined, 'data-id': it.id || undefined,
    }, h('span', { class: 'f-mi-ic' }, check ? (it.checked ? icon('check') : null) : it.icon ? icon(it.icon) : null), h('span', { class: 'f-mi-t', text: it.label }), it.shortcut ? h('span', { class: 'f-mi-k', text: it.shortcut }) : null);
    row.addEventListener('click', () => { if (it.disabled) return; close(); if (it.run) it.run(); });
    row.addEventListener('mousemove', () => { if (!it.disabled && document.activeElement !== row) row.focus({ preventScroll: true }); });
    menu.append(row); rows.push({ row, it }); last = 'item';
  }
  const lastEl = menu.lastElementChild; if (lastEl && lastEl.classList.contains('f-mi-sep')) lastEl.remove();
  if (shield) document.body.append(shield);
  document.body.append(menu);

  // Platz: an der Stelle, in den Bildschirm geschoben; unter einem Element ausgerichtet; auf dem Handy unten
  if (!kit) {
    const r = menu.getBoundingClientRect();
    let left = x, top = y;
    if (anchor) { const a = anchor.getBoundingClientRect(); left = a.left; top = a.bottom + 2; if (left + r.width > innerWidth - 8) left = a.right - r.width; }
    if (left + r.width > innerWidth - 8) left = Math.max(8, innerWidth - r.width - 8);
    if (top + r.height > innerHeight - 8) top = anchor ? Math.max(8, anchor.getBoundingClientRect().top - r.height - 2) : Math.max(8, innerHeight - r.height - 8);
    menu.style.left = Math.max(8, left) + 'px'; menu.style.top = Math.max(8, top) + 'px';
  }

  const enabled = () => rows.filter((r) => !r.it.disabled).map((r) => r.row);
  const focusAt = (i) => { const l = enabled(); if (l.length) l[(i + l.length) % l.length].focus({ preventScroll: true }); };
  menu.addEventListener('keydown', (e) => {
    const l = enabled(), i = l.indexOf(document.activeElement);
    if (e.key === 'ArrowDown') { e.preventDefault(); focusAt(i + 1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); focusAt(i < 0 ? -1 : i - 1); }
    else if (e.key === 'Home') { e.preventDefault(); focusAt(0); }
    else if (e.key === 'End') { e.preventDefault(); focusAt(-1); }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); }
    else if (e.key === 'Tab') { e.preventDefault(); close(); }
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); if (i >= 0) l[i].click(); }
  });
  const outside = (e) => { if (!menu.contains(e.target)) close(); };
  const away = () => close();
  setTimeout(() => { document.addEventListener('pointerdown', outside, true); }, 0);
  window.addEventListener('blur', away); window.addEventListener('resize', away); document.addEventListener('scroll', away, true);
  if (shield) shield.addEventListener('click', close);

  let closed = false;
  function close() {
    if (closed) return; closed = true;
    document.removeEventListener('pointerdown', outside, true); window.removeEventListener('blur', away); window.removeEventListener('resize', away); document.removeEventListener('scroll', away, true);
    menu.remove(); if (shield) shield.remove();
    if (current === handle) current = null;
    if (back && back.isConnected && typeof back.focus === 'function') back.focus({ preventScroll: true });
    onClose();
  }
  const handle = { close };
  current = handle;
  focusAt(0);
  if (!enabled().length) menu.focus();
  return handle;
}
