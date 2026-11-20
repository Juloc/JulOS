/* =====================================================================
 * Seitenleiste der Dateien-App als Baum (docs/FILES-EXPLORER.md): jeder Host klappt zu seinen Orten (Home, Root wenn erlaubt) und zu seinen angehefteten Ordnern auf;
 * der gemeinsame Ordner steht fuer sich. Auf dem Handy dient derselbe Baum als Auswahl. Zugeklappte Knoten und Pins speichert die App auf dem Server (Einstellung "files").
 * Tastatur: Pfeile hoch und runter, rechts klappt auf (oder geht hinein), links klappt zu (oder geht zum Eltern), Enter oeffnet, Menue-Taste oeffnet das Menue.
 * ===================================================================== */
import { t, tk } from './i18n.js';
import * as C from './files-core.js';
import { h, icon } from './files-ui.js';
import { safeName } from './files-explorer.js';

/** Die Knoten des Baums in Reihenfolge: { id, level, type: 'host'|'place'|'pin', label, ic, place?, pin?, expandable, open, root? } */
export function buildTree(places, pins, closed) {
  const out = [];
  const pinsOf = (ids) => pins.filter((p) => ids.includes(p.place));
  const addPins = (ids, level, byId) => {
    for (const pin of pinsOf(ids)) {
      const place = byId.get(pin.place); if (!place) continue;
      out.push({ id: 'pin:' + pin.place + ':' + pin.path, level, type: 'pin', label: pin.name || C.nameOf(pin.path) || '/', ic: 'folder', place, pin, path: pin.path, root: C.isRoot(place) });
    }
  };
  const byId = new Map(places.flat.map((p) => [p.id, p]));
  if (places.local) {
    byId.set(places.local.id, places.local);
    const kids = pinsOf([places.local.id]);
    const open = !closed.includes('local');
    out.push({ id: 'local', level: 1, type: 'place', label: places.local.label, ic: 'folder', place: places.local, path: '', expandable: kids.length > 0, open });
    if (open) addPins([places.local.id], 2, byId);
  }
  for (const g of places.groups) {
    const ids = g.places.map((p) => p.id), id = 'host:' + g.id, open = !closed.includes(id);
    out.push({ id, level: 1, type: 'host', label: g.name, ic: 'dns', expandable: true, open, place: g.places.find((p) => !C.isRoot(p)) || g.places[0] });
    if (!open) continue;
    for (const p of g.places) out.push({ id: 'place:' + p.id, level: 2, type: 'place', label: C.isRoot(p) ? t('files.tree.root') : g.places.filter((x) => !C.isRoot(x)).length > 1 ? p.name : t('files.tree.home'), ic: C.isRoot(p) ? 'root' : 'home', place: p, root: C.isRoot(p) });
    addPins(ids, 2, byId);
  }
  return out;
}

/**
 * Zeichnet den Baum in `box`.
 * o: { places, pins, closed, current: { place, path }, home(placeId) -> Pfad|undefined, kit, onOpen(place, path), onToggle(id), onMenu(event|{x,y}, node), dnd: { target(el, () => {place, dir}|null), pin(el, pin) } }
 */
export function renderTree(box, o) {
  const had = box.querySelector('[tabindex="0"]');
  const keepId = had && had.dataset.id, hadFocus = !!had && had === document.activeElement;
  const nodes = buildTree(o.places, o.pins, o.closed);
  const tree = h('div', { class: 'f-tree', role: 'tree', 'aria-label': t('desktopFiles.places') });
  const isCur = (n) => {
    if (!o.current.place) return false;
    if (n.type === 'pin') return n.place.id === o.current.place.id && n.path === o.current.path;
    if (n.type === 'place') { const home = o.home(n.place.id); return n.place.id === o.current.place.id && (n.place.kind === 'local' ? o.current.path === '' : home === undefined || o.current.path === home); }
    return false;
  };
  const rows = [];
  nodes.forEach((n) => {
    const cur = isCur(n);
    const row = h('div', { class: 'f-tn' + (cur ? ' sel' : '') + (n.root ? ' root' : '') + (n.type === 'host' ? ' host' : ''), role: 'treeitem', tabindex: '-1', 'data-id': n.id, 'aria-level': n.level, 'aria-selected': String(cur), 'aria-expanded': n.expandable ? String(n.open) : undefined, style: `--lvl:${n.level - 1}` },
      n.expandable ? h('button', { type: 'button', class: 'f-tn-tg', tabindex: '-1', 'aria-label': t(n.open ? tk('files.tree.collapse') : tk('files.tree.expand'), { name: safeName(n.label) }), onclick: (e) => { e.stopPropagation(); o.onToggle(n.id); } }, icon(n.open ? 'chevD' : 'chevR')) : h('span', { class: 'f-tn-tg none' }),
      h('span', { class: 'f-tn-ic' }, icon(n.ic)), h('span', { class: 'f-tn-t', text: safeName(n.label), title: n.type === 'pin' ? safeName(n.path) : undefined }));
    row.addEventListener('click', () => activate(n));
    row.addEventListener('contextmenu', (e) => { e.preventDefault(); row.focus({ preventScroll: true }); o.onMenu(e, n); });
    if (o.dnd) {
      if (n.type === 'pin') { row.setAttribute('draggable', 'true'); o.dnd.pin(row, n.pin); }
      if (n.type !== 'host' || n.place) o.dnd.target(row, () => {
        const p = n.place; if (!p) return null;
        if (n.type === 'pin') return { place: p, dir: n.path };
        if (n.type === 'host') return null;
        const dir = C.isLocal(p) ? '' : o.home(p.id);
        return dir === undefined ? null : { place: p, dir };
      }, n.type === 'pin' ? n.pin : null);
    }
    rows.push({ n, row }); tree.append(row);
  });
  function activate(n) {
    if (n.type === 'host') { o.onToggle(n.id); return; }
    o.onOpen(n.place, n.type === 'pin' ? n.path : undefined);
  }
  const focusRow = (i) => { const r = rows[Math.max(0, Math.min(rows.length - 1, i))]; if (!r) return; for (const x of rows) x.row.tabIndex = -1; r.row.tabIndex = 0; r.row.focus({ preventScroll: false }); };
  tree.addEventListener('keydown', (e) => {
    const i = rows.findIndex((r) => r.row === document.activeElement || r.row.contains(document.activeElement)); if (i < 0) return;
    const { n, row } = rows[i];
    if (e.key === 'ArrowDown') { e.preventDefault(); focusRow(i + 1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); focusRow(i - 1); }
    else if (e.key === 'Home') { e.preventDefault(); focusRow(0); }
    else if (e.key === 'End') { e.preventDefault(); focusRow(rows.length - 1); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); if (n.expandable && !n.open) o.onToggle(n.id); else if (n.expandable) focusRow(i + 1); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); if (n.expandable && n.open) o.onToggle(n.id); else { for (let j = i - 1; j >= 0; j--) if (rows[j].n.level < n.level) { focusRow(j); break; } } }
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(n); }
    else if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) { e.preventDefault(); const r = row.getBoundingClientRect(); o.onMenu({ clientX: r.left + 24, clientY: r.bottom, preventDefault() {} }, n); }
  });
  box.replaceChildren(tree);
  const at = rows.findIndex((r) => r.n.id === keepId), cur = rows.findIndex((r) => r.row.classList.contains('sel'));
  const tab = at >= 0 ? at : cur >= 0 ? cur : 0;
  if (rows[tab]) rows[tab].row.tabIndex = 0;
  if (hadFocus && at >= 0) rows[at].row.focus({ preventScroll: true });
  return { nodes };
}
