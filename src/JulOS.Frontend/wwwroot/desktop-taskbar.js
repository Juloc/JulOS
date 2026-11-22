import { t } from './i18n.js';
import { MouseInput } from './mouse.js';
import { stripRect, stripBox, stripToVm, deriveTaskbar } from './desktop-taskbar-geom.js';

/* =====================================================================
 * Taskleiste der VM im Dock (docs/DOCK-REMOTE-TASKBAR.md, Schritt 1). Das Bild der VM ist ein Canvas (stream.js, display); die Leiste ist sein Randstreifen.
 * Hier steht nur die Oberflaeche: der gerahmte Streifen am Bildschirmrand (ein Ausschnitt desselben Canvas per drawImage, kein zweiter Stream), die Eingabe darauf
 * und das Markieren per Hand. Die Rechnung (Seite, Groesse, Zeiger zurueckrechnen) ist rein und steht in desktop-taskbar-geom.js; die Groesse der VM (Fenster plus
 * Leiste) und die gemerkte Markierung gehoeren der Sitzung (app.js: taskbar(), setTaskbar()).
 *
 * createTaskbar({ barH, focusRemote, notify, onInset }):
 *   sync(active, rep)  active: Sitzung im Fokus mit Leiste (oder null: dann zeigt das Dock nur die Pillen); rep: Sitzung, deren Leiste Platz am Rand belegt
 *                      (die im Fokus, sonst eine andere sichtbare mit Leiste: so aendert sich die Groesse der Fenster nicht bei jedem Fokuswechsel)
 *   inset()            { top, right, bottom, left }: so viel Rand belegt die Leiste (CSS-Pixel), fuer die Arbeitsflaeche der Fenster
 *   mark(session)      Markieren starten ("Taskleiste markieren"), cancelMark()
 * ===================================================================== */

const FRAME = 3;                       // Rahmen der Leiste in der Farbe der Verbindung (CSS-Pixel)
const TAG = 18;                        // Dicke des Namensschilds an der Innenkante des Rahmens (es liegt im Rahmen, nicht ueber den Fenstern)
const NONE = { top: 0, right: 0, bottom: 0, left: 0 };

export function createTaskbar({ barH, focusRemote, notify, onInset }) {
  const el = document.createElement('section');
  el.className = 'dk-tb'; el.hidden = true;
  const tag = document.createElement('span'); tag.className = 'dk-tb-tag';
  const canvas = document.createElement('canvas'); canvas.className = 'dk-tb-cv'; canvas.setAttribute('role', 'img');
  el.append(tag, canvas);
  const ctx = canvas.getContext('2d', { alpha: false });
  let cur = null, repSession = null, raf = 0, inset = NONE, boxKey = '';

  const vmOf = (s) => ({ w: s.display.getWidth(), h: s.display.getHeight() });
  const kindLabel = (s) => (s.conn.kind === 'vnc' ? 'VNC' : 'RDP');
  /** Wie gross die Leiste dieser Sitzung im Dock steht (CSS-Pixel, ohne Rahmen); null ohne Bild oder ohne Leiste */
  function boxOf(s) {
    const tb = s && s.taskbar && s.taskbar();
    if (!tb || !s.display || !s.display.getWidth()) return null;
    const horizontal = tb.side === 'bottom' || tb.side === 'top';
    const v = { w: document.documentElement.clientWidth, h: document.documentElement.clientHeight };
    const avail = (horizontal ? v.w - 2 * FRAME : v.h - barH - 60 - 2 * FRAME);
    return stripBox(vmOf(s), tb, s.display.getScale() || 1, avail);
  }
  function insetOf(s) {
    const b = boxOf(s); if (!b) return NONE;
    const side = s.taskbar().side, vert = side === 'left' || side === 'right';
    const w = vert ? b.w + FRAME + TAG : b.w + 2 * FRAME, h = vert ? b.h + 2 * FRAME : b.h + FRAME + TAG;      // zum Bildschirmrand hin hat der Rahmen keine Kante
    return { top: side === 'top' ? h : 0, bottom: side === 'bottom' ? h : 0, left: side === 'left' ? w : 0, right: side === 'right' ? w : 0 };
  }
  const sameInset = (a, b) => a.top === b.top && a.right === b.right && a.bottom === b.bottom && a.left === b.left;
  function updateInset() {
    const n = insetOf(repSession);
    if (!sameInset(n, inset)) { inset = n; if (onInset) onInset(); }
  }

  /** Grosse und Lage der Leiste nachziehen (jedes Bild: das Fenster kann seine Groesse, den Massstab oder die Leiste geaendert haben) */
  function layout() {
    const s = cur, tb = s.taskbar(), b = boxOf(s);
    const key = b ? `${tb.side}:${b.w}x${b.h}` : '';
    if (key !== boxKey) {
      boxKey = key;
      if (b) {
        el.className = 'dk-tb dk-tb-' + tb.side;
        const vert = tb.side === 'left' || tb.side === 'right';
        el.style.width = (vert ? b.w + FRAME + TAG : b.w + 2 * FRAME) + 'px'; el.style.height = (vert ? b.h + 2 * FRAME : b.h + FRAME + TAG) + 'px';
        canvas.style.width = b.w + 'px'; canvas.style.height = b.h + 'px';
      }
    }
    el.hidden = !b;
    updateInset();
    return b;
  }

  function frame() {
    raf = requestAnimationFrame(frame);
    const s = cur; if (!s || !s.display) return;
    const b = layout(); if (!b) return;
    const d = s.display, r = stripRect(vmOf(s), s.taskbar()), dpr = window.devicePixelRatio || 1;
    const cw = Math.round(b.w * dpr), ch = Math.round(b.h * dpr);
    if (canvas.width !== cw || canvas.height !== ch) { canvas.width = cw; canvas.height = ch; }
    ctx.drawImage(d.canvas, r.x, r.y, r.w, r.h, 0, 0, cw, ch);
    ctx.drawImage(d.overlay, r.x, r.y, r.w, r.h, 0, 0, cw, ch);          // verlustfreie Nachbesserung ruhender Teile liegt ueber dem Video
    // Mauszeiger der VM (er ist ein eigenes Canvas ueber dem Bild)
    if (d.softCursor && d.cursorShown) {
      const kx = cw / r.w, ky = ch / r.h, c = d.cursorCanvas;
      ctx.drawImage(c, (d.pointer.x - d.cursorHot.x - r.x) * kx, (d.pointer.y - d.cursorHot.y - r.y) * ky, c.width * kx, c.height * ky);
    }
  }
  const stop = () => { cancelAnimationFrame(raf); raf = 0; };

  function sync(active, rep) {
    repSession = rep;
    if (active !== cur) {
      cur = active; boxKey = '';
      if (cur) {
        const name = `${cur.title} · ${kindLabel(cur)}`;
        tag.textContent = name;
        el.setAttribute('aria-label', t('desktop.tbLabel', { name }));
        canvas.setAttribute('aria-label', t('desktop.tbLabel', { name }));
      }
    }
    if (cur) { if (!raf) frame(); } else { stop(); el.hidden = true; }
    updateInset();
  }

  /* ---------- Eingabe: Zeiger im Streifen werden zu Zeigern der VM (y oder x zurueckgerechnet) ---------- */
  const mouse = new MouseInput(canvas);
  const forward = (st) => {
    const s = cur, tb = s && s.taskbar(); if (!tb || !s.client || !s.display) return;
    const r = canvas.getBoundingClientRect();
    const p = stripToVm(vmOf(s), tb, { x: st.x, y: st.y }, { w: r.width, h: r.height });
    s.client.sendMouseState({ ...st, x: p.x, y: p.y });
  };
  mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = forward;
  // Ziehen bleibt am Streifen haengen, bis die Taste losgelassen wird (auch ausserhalb des Streifens); die Tasten gehen weiter an das Fernfenster
  canvas.addEventListener('pointerdown', (e) => { try { canvas.setPointerCapture(e.pointerId); } catch { /* egal */ } focusRemote(); });

  /* ---------- Markieren: ein Rechteck ueber die Leiste auf dem Live-Bild ziehen ---------- */
  let marking = null;
  function cancelMark(restore = true) {
    const m = marking; if (!m) return; marking = null;
    document.removeEventListener('keydown', m.onKey, true);
    m.layer.remove();
    if (restore && m.prev) m.session.setTaskbar(m.prev);
  }
  function mark(session) {
    cancelMark();
    const prev = session.taskbar() ? session.tb : null;
    if (prev) session.setTaskbar(null);                       // ohne Streifen ist das ganze Bild zu sehen, auch die alte Leiste
    const layer = document.createElement('div'); layer.className = 'dk-tb-mark';
    const hint = document.createElement('div'); hint.className = 'dk-tb-hint';
    const msg = document.createElement('span'); msg.textContent = t('desktop.tbMarkHint');
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'dk-b'; cancel.textContent = t('common.cancel');
    hint.append(msg, cancel);
    const box = document.createElement('div'); box.className = 'dk-tb-box'; box.hidden = true;
    layer.append(box, hint);
    session.el.stage.appendChild(layer);
    const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancelMark(); } };
    document.addEventListener('keydown', onKey, true);
    marking = { session, prev, layer, onKey };
    cancel.addEventListener('click', () => cancelMark());
    cancel.addEventListener('pointerdown', (e) => e.stopPropagation());
    let from = null;
    const local = (e) => { const r = layer.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
    layer.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      e.preventDefault(); layer.setPointerCapture(e.pointerId);
      from = { cx: e.clientX, cy: e.clientY, ...local(e) };
    });
    layer.addEventListener('pointermove', (e) => {
      if (!from) return;
      const p = local(e);
      Object.assign(box.style, { left: Math.min(from.x, p.x) + 'px', top: Math.min(from.y, p.y) + 'px', width: Math.abs(p.x - from.x) + 'px', height: Math.abs(p.y - from.y) + 'px' });
      box.hidden = false;
    });
    layer.addEventListener('pointerup', (e) => {
      if (!from) return;
      const a = session.remoteXY(from.cx, from.cy), b = session.remoteXY(e.clientX, e.clientY);
      from = null; box.hidden = true;
      const tb = deriveTaskbar(a, b, { w: session.display.getWidth(), h: session.display.getHeight() });
      if (!tb) { notify(t('desktop.tbMarkFail'), 4200); return; }       // das Markieren bleibt offen: noch einmal ziehen
      cancelMark(false);
      session.setTaskbar(tb);
      notify(t('desktop.tbMarked'), 3000);
      focusRemote();
    });
  }

  return { el, sync, inset: () => inset, mark, cancelMark, isMarking: () => !!marking };
}
