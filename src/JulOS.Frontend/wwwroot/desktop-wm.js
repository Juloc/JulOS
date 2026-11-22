import { zoneRect, zoneAt, snapStep, resizeRect, dragPosition, unsnapAt, nextWindow } from './desktop-snap.js';
import { resolveBindings, matchBinding, firesInTextField, moveKey, bindingLabel } from './desktop-keys.js';
import { t } from './i18n.js';

/* =====================================================================
 * Fenstermanager, Teil mit dem Browser (docs/MULTI-SCREEN.md, Schritt 1): Ziehen mit Einrasten (Vorschau, Haelfte, Viertel, maximieren), Tastatur wie bei Windows
 * (Strg+Alt statt der Windows-Taste, die dem Betriebssystem gehoert), Maustasten 4 und 5 (zurueck und vor) im Fenster mit dem Fokus.
 * Gerechnet wird in desktop-snap.js und desktop-keys.js (getestet); hier steht nur das Verdrahten. desktop.js legt den Fenstermanager mit createWM an und gibt ihm, was es
 * ueber Fenster weiss (h): root, wins, barH, view(), area(), fit(), focusWin, minimizeWin, restoreWin, closeWin, rootOf, setMax(w, an), saveRect, keysToRemote(), notify, ls.
 * Ein Fenster hat hier zwei Felder: snap (null oder die Zone, in der es steht) und restore (das Rechteck davor, solange es eingerastet oder maximiert ist).
 * ===================================================================== */

const KEY_STEP = 32, MOVE_STEP = 24;     // Pixel je Tastendruck: Groesse, Verschieben
const DRAG_START = 4;                    // ein eingerastetes Fenster loest sich erst nach so viel Bewegung (sonst finge ein Doppelklick an zu ziehen)
const MODIFIER_CODE = /^(Shift|Control|Alt|Meta)(Left|Right)$/;

export function createWM(h) {
  const windows = () => [...h.wins.values()].filter((w) => !w.dialog);
  const activeAny = () => [...h.wins.values()].find((w) => w.el.classList.contains('active') && !w.minimized) || null;
  const activeWin = () => { const a = activeAny(); return a && !a.dialog ? a : null; };
  const inline = (w) => ({ x: parseFloat(w.el.style.left) || 0, y: parseFloat(w.el.style.top) || 0, w: parseFloat(w.el.style.width) || w.el.offsetWidth, h: parseFloat(w.el.style.height) || w.el.offsetHeight });
  const shown = (w) => ({ x: w.el.offsetLeft, y: w.el.offsetTop, w: w.el.offsetWidth, h: w.el.offsetHeight });
  const place = (w, r) => Object.assign(w.el.style, { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px' });
  const minSize = (w) => ({ w: w.opts.minW || 320, h: w.opts.minH || 220 });
  const bounds = () => { const v = h.view(); return { x: 0, y: h.barH, w: v.w, h: v.h - h.barH }; };
  const zoneOf = (w) => (w.maximized ? 'max' : w.snap || null);

  /* ---------- Zustand eines Fensters: frei, eingerastet oder maximiert ---------- */
  /** Der Fenstermanager vergisst Einrasten und Maximieren, ohne die Groesse zu aendern (das Fenster wird von Hand veraendert) */
  function forget(w) { if (w.maximized) h.setMax(w, false); w.snap = null; w.restore = null; }
  /** Zurueck auf Ort und Groesse vor dem Einrasten */
  function unsnap(w) {
    if (!w.maximized && !w.snap) return;
    const r = w.restore; forget(w);
    if (r) place(w, h.fit(r, minSize(w).w, minSize(w).h));
    h.saveRect(w);
  }
  /** Eine Zone (siehe desktop-snap.js ZONES), 'restore' oder 'minimize' auf ein Fenster anwenden */
  function apply(w, zone) {
    if (!w || w.dialog || !zone) return;
    if (zone === 'minimize') { h.minimizeWin(w); const next = [...windows()].filter((o) => !o.minimized).sort((a, b) => b.el.style.zIndex - a.el.style.zIndex)[0]; if (next) h.focusWin(next); return; }
    if (w.minimized) h.restoreWin(w);
    if (zone === 'restore') { unsnap(w); h.focusWin(w); return; }
    if (!w.restore) w.restore = inline(w);          // der freie Zustand, zu dem "wiederherstellen" zurueckkehrt (inline: bei maximiert stehen dort noch Ort und Groesse davor)
    if (zone === 'max') { w.snap = null; h.setMax(w, true); }
    else { h.setMax(w, false); w.snap = zone; place(w, zoneRect(zone, h.area())); }
    h.focusWin(w);
  }
  const step = (w, dir) => apply(w, snapStep(zoneOf(w), dir));
  /** Maximieren-Taste und Doppelklick auf die Titelleiste: eingerastet oder maximiert heisst zurueck, frei heisst maximieren */
  const toggleMax = (w) => apply(w, zoneOf(w) ? 'restore' : 'max');
  /** Die Anzeige wird kleiner oder groesser: ein eingerastetes Fenster bleibt in seiner Zone */
  function reflow(w) { if (w.snap) place(w, zoneRect(w.snap, h.area())); }

  function resizeKey(w, dir) {
    if (w.maximized) return;
    if (w.snap) { w.snap = null; w.restore = null; }                 // von Hand veraendert: gilt nicht mehr als eingerastet
    place(w, resizeRect(shown(w), dir, KEY_STEP, minSize(w), bounds()));
    h.saveRect(w);
  }
  function cycle(dir) {
    const a = activeAny(), ids = windows().map((w) => w.id);
    const target = h.wins.get(nextWindow(ids, a ? h.rootOf(a).id : null, dir));
    if (target) { if (target.minimized) h.restoreWin(target); else h.focusWin(target); }
  }

  /* ---------- Ziehen mit Einrasten ---------- */
  let preview = null;
  function showPreview(w, zone) {
    if (!preview) { preview = document.createElement('div'); preview.className = 'dk-snapprev'; preview.setAttribute('aria-hidden', 'true'); h.root.appendChild(preview); }
    if (zone) {
      const r = zoneRect(zone, h.area());
      Object.assign(preview.style, { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px', zIndex: String(Math.max(1, (parseInt(w.el.style.zIndex, 10) || 2) - 1)) });
    }
    preview.classList.toggle('on', !!zone);
  }
  function wireDrag(w, head) {
    head.addEventListener('pointerdown', (ev) => {
      if (ev.button !== 0 || ev.target.closest('button, input')) return;
      ev.preventDefault();
      const x0 = ev.clientX, y0 = ev.clientY;
      let sx = x0, sy = y0, origin = inline(w), zone = null;
      let started = !(w.maximized || w.snap);     // ein freies Fenster folgt sofort, ein eingerastetes erst nach ein paar Pixeln
      const begin = () => { w.el.classList.add('dragging'); h.root.classList.add('dk-moving'); };
      head.setPointerCapture(ev.pointerId);
      if (started) begin();
      const move = (m) => {
        if (!started) {
          if (Math.hypot(m.clientX - x0, m.clientY - y0) < DRAG_START) return;
          started = true; begin();
          // an der Titelleiste aus Maximiert oder Einrasten gezogen: alte Groesse zurueck, der Griff bleibt an derselben Stelle der Breite
          const cur = shown(w), r = unsnapAt(w.restore || { w: Math.min(cur.w, 900), h: Math.min(cur.h, 600) }, cur, m.clientX);
          forget(w); place(w, h.fit(r, minSize(w).w, minSize(w).h));
          origin = inline(w); sx = m.clientX; sy = m.clientY;
        }
        const p = dragPosition(origin, { dx: m.clientX - sx, dy: m.clientY - sy }, { w: w.el.offsetWidth }, h.view(), { barH: h.barH });
        w.el.style.left = p.x + 'px'; w.el.style.top = p.y + 'px';
        if (!w.dialog) { zone = zoneAt({ x: m.clientX, y: m.clientY }, h.area()); showPreview(w, zone); }
      };
      const up = (e) => {
        head.removeEventListener('pointermove', move); head.removeEventListener('pointerup', up); head.removeEventListener('pointercancel', up);
        w.el.classList.remove('dragging'); h.root.classList.remove('dk-moving');
        if (preview) preview.classList.remove('on');
        if (!started) return;
        if (zone && e.type === 'pointerup') apply(w, zone); else h.saveRect(w);
      };
      head.addEventListener('pointermove', move); head.addEventListener('pointerup', up); head.addEventListener('pointercancel', up);
    });
  }

  /* ---------- Tastatur ---------- */
  let bindings = resolveBindings(h.ls.get('keys', {}));     // eigene Belegung: localStorage pd.dk.keys = { "snap.left": { "code": "KeyH", "mods": ["alt"] } }; ein Dialog dafuer kommt spaeter
  const evOf = (e) => ({ code: e.code, ctrlKey: e.ctrlKey, altKey: e.altKey, shiftKey: e.shiftKey, metaKey: e.metaKey, altGraph: !!(e.getModifierState && e.getModifierState('AltGraph')) });
  const isTextField = (a) => !!a && a !== document.body && (a.isContentEditable || a.tagName === 'TEXTAREA' || a.tagName === 'SELECT'
    || (a.tagName === 'INPUT' && !['button', 'checkbox', 'radio', 'range', 'color', 'file', 'submit', 'reset', 'image'].includes((a.type || '').toLowerCase())));
  const visible = () => !!h.root && !h.root.hidden;

  /** Gilt die Taste dem Fenstermanager, obwohl ein Fernfenster alle Tasten bekommt? Nur der Fensterwechsel (global in der Belegung), nie im Vollbild. app.js fragt das ueber setKeyOwner. */
  const claims = (e) => { const b = matchBinding(bindings, evOf(e)); return !!(b && b.global && !document.fullscreenElement); };

  let moving = null;       // Fenster im Verschiebe-Modus (Strg+Alt+M, dann Pfeile)
  function endMove() { if (moving) { moving.el.classList.remove('kbmove'); h.saveRect(moving); moving = null; } }
  function startMove(w) {
    if (w.maximized || w.snap) unsnap(w);
    moving = w; w.el.classList.add('kbmove'); h.notify(t('desktop.moveMode'), 5000);
  }
  /** Taste im Verschiebe-Modus; true = verbraucht */
  function inMoveMode(e) {
    if (MODIFIER_CODE.test(e.code)) return false;                 // Umschalt allein beendet nichts
    if (!h.wins.has(moving.id) || !moving.el.classList.contains('active')) { endMove(); return false; }
    const k = moveKey(e);
    if (!k) { endMove(); return false; }                          // eine andere Taste: Modus aus, sie wirkt wie sonst
    e.preventDefault(); e.stopPropagation();
    if (k === 'end') { endMove(); return true; }
    const p = dragPosition(inline(moving), { dx: k.dx * MOVE_STEP, dy: k.dy * MOVE_STEP }, { w: moving.el.offsetWidth }, h.view(), { barH: h.barH });
    moving.el.style.left = p.x + 'px'; moving.el.style.top = p.y + 'px';
    return true;
  }

  function onKeyDown(e) {
    if (!visible() || e.isComposing) return;
    const b = matchBinding(bindings, evOf(e));
    if (moving && !(b && b.id === 'move') && inMoveMode(e)) return;
    if (!b) return;
    // Ein Fernfenster mit dem Fokus bekommt alle Tasten (im Vollbild auch die zum Wechseln); ein Textfeld behaelt, was dort etwas tut
    if (h.keysToRemote() && (!b.global || document.fullscreenElement)) return;
    if (isTextField(document.activeElement) && !firesInTextField(b)) return;
    const [kind, dir] = b.id.split('.');
    const w = activeWin();
    if (kind === 'next' || kind === 'prev') cycle(kind === 'next' ? 1 : -1);
    else if (!w) return;
    else if (kind === 'move') { if (moving) endMove(); else if (!e.repeat) startMove(w); }
    else if (kind === 'snap') step(w, dir);
    else if (kind === 'size') resizeKey(w, dir);
    else return;
    e.preventDefault(); e.stopPropagation();
  }

  /* ---------- Maustasten 4 und 5 (Browser zurueck und vor) ---------- */
  // Auf dem Desktop gehoeren sie JulOS: der Browser geht nie von der Seite weg (preventDefault auf mousedown, mouseup und auxclick; es gibt keine history-Eintraege).
  // Sie laufen im Fenster mit dem Fokus ueber onNavigate(dir) (dir -1 = zurueck, 1 = vor), das ein Fenster beim Anlegen (opts) oder spaeter (w.onNavigate) angeben kann.
  // Ein Fenster ohne Hook tut nichts. Ueber einer Sitzung (Fernbild, Terminal) bekommt die Sitzung die Tasten wie bisher (mouse.js sendet sie, falls das Ziel sie kennt).
  const sideButton = (e) => e.button === 3 || e.button === 4;
  function wireMouseButtons() {
    const block = (e) => { if (visible() && sideButton(e)) e.preventDefault(); };
    document.addEventListener('mousedown', block, true);
    document.addEventListener('auxclick', block, true);
    document.addEventListener('mouseup', (e) => {
      if (!visible() || !sideButton(e)) return;
      e.preventDefault();
      if (e.target.closest && e.target.closest('.dk-session')) return;
      const w = activeAny(), hook = w && (w.onNavigate || w.opts.onNavigate);
      if (hook) hook(e.button === 3 ? -1 : 1);
    }, true);
  }

  window.addEventListener('keydown', onKeyDown, true);
  document.addEventListener('pointerdown', () => endMove(), true);
  wireMouseButtons();

  /** Eintraege fuer das Menue eines Fensters (Rechtsklick auf seine Pille): der Weg mit der Maus zu den Tastenaktionen, auch fuer Fernfenster, die die Tasten selbst bekommen */
  function menuItems(w) {
    const names = { ctrl: t('desktop.keyCtrl'), alt: t('desktop.keyAlt'), shift: t('desktop.keyShift') };
    const hint = (id) => { const b = bindings.find((x) => x.id === id); return b ? bindingLabel(b, names) : ''; };
    const free = !zoneOf(w);
    return [
      { label: w.minimized ? t('desktop.restoreWindow') : t('desktop.minimize'), icon: 'minus', run: () => (w.minimized ? h.restoreWin(w) : h.minimizeWin(w)) },
      { label: t('desktop.snapLeft'), icon: 'left', hint: hint('snap.left'), run: () => apply(w, 'left') },
      { label: t('desktop.snapRight'), icon: 'right', hint: hint('snap.right'), run: () => apply(w, 'right') },
      { label: free ? t('desktop.maximizeWindow') : t('desktop.restoreWindow'), icon: free ? 'square' : 'restore', hint: free ? hint('snap.up') : hint('snap.down'), run: () => toggleMax(w) },
      { sep: true },
      { label: t('common.close'), icon: 'x', run: () => h.closeWin(w) },
    ];
  }

  return { wireDrag, toggleMax, apply, forget, reflow, claims, menuItems, endMove };
}
