/* =====================================================================
 * Gesten der Remote-Sitzung am Handy: eine reine Zustandsmaschine ohne DOM und ohne Zeitgeber (mit node pruefbar: tests/frontend/touch-gestures.test.mjs).
 * Sie bekommt Zeigerereignisse mit Zeit { type: 'down' | 'move' | 'up' | 'cancel', id, x, y, t } und die verstrichene Zeit (tick) und gibt Aktionen zurueck, die app.js
 * ausfuehrt (Klick, Taste halten, Zeiger bewegen, scrollen, zoomen) und touch-ui.js zeigt (Ring, Markierung). Die Zeit t ist in Millisekunden (performance.now()).
 *
 * Gesten (beide Modi, 'direct': die Stelle des Fingers ist die Stelle im Bild; 'pad': der Finger bewegt den Zeiger wie ein Touchpad):
 *   Tippen                      Linksklick (direkt an der Stelle, pad am Zeiger)
 *   Halten (Ring fuellt sich)   Rechtsklick; Bewegung ueber die Schwelle bricht ab
 *   Zwei-Finger-Tipp            Rechtsklick (wenn die Rechtsklick-Methode ihn zulaesst)
 *   Zwei Finger schieben        Scrollen senkrecht und waagerecht; Pinch erst bei klarer Abstandsaenderung
 *   Doppeltipp und halten       Ziehen: die linke Taste bleibt gedrueckt, bis der Finger hebt (pad: "Tippen und ziehen")
 *   Ein Finger schieben         direkt: ziehen (wie bisher), pad: Zeiger bewegen
 *   Pinch                       Zoom
 *   Doppeltipp mit zwei Fingern Zoom zuruecksetzen (nur wenn gezoomt ist)
 * Aktionen (type): hover, click, press, release, move, scroll, pinch, pinchEnd, zoomReset, ring, mark.
 * ===================================================================== */

export const HOLD_MIN = 300, HOLD_MAX = 1000;
export const TOUCH_DEFAULTS = Object.freeze({
  mode: 'direct',            // 'direct' | 'pad'
  holdMs: 500,               // Haltedauer fuer den Rechtsklick (300 bis 1000)
  rightClick: 'both',        // 'hold' | 'two' | 'both'
  scrollDir: 'natural',      // 'natural' (Finger nach oben = Inhalt nach oben) | 'classic' (Finger nach oben = Bildlauf nach oben)
  scrollSpeed: 'normal',     // 'slow' | 'normal' | 'fast'
  padSpeed: 1.4,             // Zeigergeschwindigkeit im Touchpad-Modus (0,6 bis 3)
  tapDrag: true,             // pad: Tippen und ziehen wie auf einem Laptop
  fingerDrag: true,          // direkt: ein Finger, der sich bewegt, zieht (Fenster, Markieren); aus: nur Doppeltipp und halten zieht
  vibrate: true,             // kurze Rueckmeldung, wo der Browser es kann
  clipChip: true,            // Anzeige der Zwischenablage des Geraets in der Sitzung
});
const ONE_OF = {
  mode: ['direct', 'pad'], rightClick: ['hold', 'two', 'both'], scrollDir: ['natural', 'classic'], scrollSpeed: ['slow', 'normal', 'fast'],
};
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

/** Prueft gespeicherte Einstellungen (lokal oder vom Server): jedes Feld einzeln, Unbekanntes faellt weg, Fehlendes ist der Standard. */
export function normalizeTouch(v) {
  const o = v && typeof v === 'object' ? v : {};
  const out = { ...TOUCH_DEFAULTS };
  for (const [k, list] of Object.entries(ONE_OF)) if (list.includes(o[k])) out[k] = o[k];
  if (Number.isFinite(o.holdMs)) out.holdMs = clamp(Math.round(o.holdMs / 50) * 50, HOLD_MIN, HOLD_MAX);
  if (Number.isFinite(o.padSpeed)) out.padSpeed = clamp(Math.round(o.padSpeed * 10) / 10, 0.6, 3);
  for (const k of ['tapDrag', 'fingerDrag', 'vibrate', 'clipChip']) if (typeof o[k] === 'boolean') out[k] = o[k];
  return out;
}
/** Nur was vom Standard abweicht (so viel schickt die Seite zum Server). */
export function compactTouch(cfg) {
  const n = normalizeTouch(cfg), out = {};
  for (const k of Object.keys(TOUCH_DEFAULTS)) if (n[k] !== TOUCH_DEFAULTS[k]) out[k] = n[k];
  return out;
}

/* ---------- Schwellen (Pixel des Bildschirms, Millisekunden) ---------- */
export const SLOP = 12;               // so weit darf der Finger beim Halten wandern
export const RING_DELAY = 140;        // der Ring erscheint erst nach dieser Zeit (ein Tippen soll nichts aufblitzen lassen)
export const DOUBLE_MS = 330;         // Doppeltipp: zweiter Finger so schnell nach dem Tippen ...
export const DOUBLE_DIST = 36;        // ... und (direkt) so nah an der Stelle
export const TWO_TAP_MS = 320;        // Zwei-Finger-Tipp: so kurz
export const TWO_DOUBLE_MS = 380;     // Doppeltipp mit zwei Fingern
export const PINCH_MIN = 32;          // Pinch: Abstandsaenderung mindestens
export const SCROLL_MIN = 14;         // Scrollen: Weg der Mitte mindestens
const PINCH_DOMINANCE = 1.3, SCROLL_DOMINANCE = 0.6;

const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
const mid = (a, b) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });

/** Pixel des Fingers je Raste des Mausrads. */
export const scrollStep = (speed) => ({ slow: 48, normal: 34, fast: 22 }[speed] || 34);

/** Zeigerbewegung im Touchpad-Modus: Grundfaktor mal eine maessige Beschleunigung (schnelle Bewegung bis 1,45 fach). */
export function padDelta(dx, dy, dt, speed) {
  const v = dt > 0 ? Math.hypot(dx, dy) / dt : 0;           // Pixel je Millisekunde
  const gain = speed * (1 + Math.min(v, 1.5) * 0.3);
  return { dx: dx * gain, dy: dy * gain };
}

/** Sammelt Pixel zu ganzen Rasten des Mausrads; der Rest bleibt fuer die naechste Bewegung. add() gibt { v, h } zurueck (positiv: nach unten und nach rechts). */
export class WheelAccum {
  constructor(step = 34) { this.step = step; this.v = 0; this.h = 0; }
  add(dx, dy) {
    this.h += dx; this.v += dy;
    const take = (key) => { const n = Math.trunc(this[key] / this.step); this[key] -= n * this.step; return n || 0; };      // n || 0: kein -0
    return { v: take('v'), h: take('h') };
  }
  reset() { this.v = 0; this.h = 0; }
}

export class TouchGestures {
  constructor(cfg = {}) {
    this.cfg = normalizeTouch(cfg);
    this.opt = { zoomed: false };
    this.fingers = new Map();
    this.phase = 'idle';
    this.lastTap = null;           // { x, y, t } des letzten Tippens (fuer Doppeltipp und halten)
    this.lastTwoTap = null;        // { t } des letzten Zwei-Finger-Tipps (fuer das Zuruecksetzen des Zooms)
    this.pendingRight = null;      // aufgeschobener Rechtsklick eines Zwei-Finger-Tipps, solange noch ein zweiter Tipp (Zoom zurueck) kommen koennte
  }

  setConfig(cfg) { this.cfg = normalizeTouch(cfg); }
  /** zoomed: das Bild ist gezoomt, ein Zwei-Finger-Doppeltipp setzt es zurueck (dann wartet der Rechtsklick des ersten Tipps kurz). */
  setOptions(opt) { Object.assign(this.opt, opt); }

  get where() { return this.cfg.mode === 'pad' ? 'pointer' : 'finger'; }
  /** Wird gerade eine Taste gehalten (Ziehen)? Dann muss sie beim Abbruch losgelassen werden. */
  get pressed() { return this.phase === 'drag'; }
  get busy() { return this.phase !== 'idle'; }

  /** Zeit (wie t der Ereignisse), zu der tick() etwas tun wuerde, oder null. */
  deadline() {
    const one = this.one;
    const list = [];
    if (this.phase === 'one' && one.holdArmed) { if (!one.ring) list.push(one.t0 + RING_DELAY); list.push(one.t0 + this.cfg.holdMs); }
    if (this.pendingRight) list.push(this.pendingRight.at);
    return list.length ? Math.min(...list) : null;
  }

  /** Die Zeit ist bis t vergangen: Ring zeigen, Rechtsklick nach dem Halten, aufgeschobenen Rechtsklick ausloesen. */
  tick(t) {
    const acts = [];
    const one = this.one;
    if (this.phase === 'one' && one.holdArmed) {
      if (!one.ring && t >= one.t0 + RING_DELAY && t < one.t0 + this.cfg.holdMs) {
        one.ring = true;
        acts.push({ type: 'ring', phase: 'start', x: one.sx, y: one.sy, ms: one.t0 + this.cfg.holdMs - t });
      }
      if (t >= one.t0 + this.cfg.holdMs) {
        acts.push(...this.fireHold());
      }
    }
    if (this.pendingRight && t >= this.pendingRight.at) {
      const p = this.pendingRight; this.pendingRight = null;
      acts.push({ type: 'click', button: 'right', where: this.where, x: p.x, y: p.y, how: 'two' });
    }
    return acts;
  }

  fireHold() {
    const one = this.one, acts = [];
    one.holdArmed = false;
    acts.push({ type: 'ring', phase: 'full', x: one.sx, y: one.sy });
    acts.push({ type: 'click', button: 'right', where: this.where, x: one.sx, y: one.sy, how: 'hold' });
    this.phase = 'fired'; this.lastTap = null;
    return acts;
  }

  /** Alles abbrechen (touchcancel, Sitzung verlassen): gehaltene Taste loslassen, Ring und Markierung weg. */
  cancel() {
    const acts = this.dropAll();
    this.fingers.clear(); this.phase = 'idle'; this.lastTap = null; this.pendingRight = null;
    return acts;
  }

  /** Ring und Markierung wegnehmen und eine gehaltene Taste loslassen (die Fingerliste bleibt). */
  dropAll() {
    const acts = [];
    if (this.phase === 'one' && this.one.ring) acts.push({ type: 'ring', phase: 'cancel' });
    if (this.phase === 'drag') {
      acts.push({ type: 'release', button: 'left', where: this.where, x: this.drag.x, y: this.drag.y });
      acts.push({ type: 'mark', on: false });
    }
    return acts;
  }

  feed(ev) {
    if (ev.type === 'cancel') return this.cancel();
    const acts = this.tick(ev.t);
    if (ev.type === 'down') acts.push(...this.down(ev));
    else if (ev.type === 'move') acts.push(...this.move(ev));
    else if (ev.type === 'up') acts.push(...this.up(ev));
    return acts;
  }

  /* ---------- Finger auf dem Schirm ---------- */
  down(ev) {
    const acts = [];
    this.fingers.set(ev.id, { x: ev.x, y: ev.y });
    const n = this.fingers.size;
    if (n === 1 && this.phase === 'idle') return this.startOne(ev);
    if (n === 2 && ['one', 'move', 'swipe', 'drag'].includes(this.phase)) {
      acts.push(...this.dropAll());
      return acts.concat(this.startTwo(ev.t));
    }
    // ein dritter Finger oder ein Finger nach dem Rechtsklick: nichts mehr tun, bis alle oben sind
    acts.push(...this.dropAll());
    this.phase = 'multi';
    return acts;
  }

  startOne(ev) {
    const acts = [];
    const double = !!this.lastTap && ev.t - this.lastTap.t <= DOUBLE_MS
      && (this.cfg.mode === 'pad' ? this.cfg.tapDrag : dist(this.lastTap, ev) <= DOUBLE_DIST);
    this.one = { sx: ev.x, sy: ev.y, x: ev.x, y: ev.y, t0: ev.t, lastT: ev.t, holdArmed: this.cfg.rightClick !== 'two', ring: false };
    this.phase = 'one';
    if (this.cfg.mode === 'direct') acts.push({ type: 'hover', x: ev.x, y: ev.y });       // Zeiger vorab hinbewegen: die Zeigerform (Textfeld?) kommt dann schon an
    if (double) {
      // Doppeltipp und halten: die linke Taste bleibt gedrueckt, bis der Finger hebt (ohne Bewegung ergibt das den zweiten Klick eines Doppelklicks)
      this.one.holdArmed = false; this.lastTap = null;
      acts.push(...this.beginDrag(ev.x, ev.y, 'double'));
    }
    return acts;
  }

  beginDrag(x, y, how) {
    this.phase = 'drag';
    this.drag = { x, y, how, moved: false, sx: x, sy: y };
    return [{ type: 'press', button: 'left', where: this.where, x, y, how }, { type: 'mark', on: true, x, y }];
  }

  startTwo(t) {
    const [a, b] = [...this.fingers.values()];
    this.phase = 'two';
    this.two = { d0: dist(a, b), m0: mid(a, b), mLast: mid(a, b), t0: t, decided: null, ex: 0, ey: 0, lastRatio: 1, started: false };
    return [];
  }

  move(ev) {
    const f = this.fingers.get(ev.id);
    if (!f) return [];
    f.x = ev.x; f.y = ev.y;
    if (this.phase === 'one' || this.phase === 'move' || this.phase === 'drag') return this.moveOne(ev);
    if (this.phase === 'two') return this.moveTwo(ev);
    return [];
  }

  moveOne(ev) {
    const acts = [], one = this.one, p = { x: ev.x, y: ev.y };
    const pad = this.cfg.mode === 'pad';
    if (this.phase === 'one') {
      if (dist(p, { x: one.sx, y: one.sy }) <= SLOP) { one.x = ev.x; one.y = ev.y; return acts; }
      // ueber die Schwelle: der Halte-Ring bricht ab
      if (one.ring) acts.push({ type: 'ring', phase: 'cancel' });
      one.holdArmed = false; one.ring = false;
      this.lastTap = null;
      if (pad) { this.phase = 'move'; one.lastT = one.t0; one.x = one.sx; one.y = one.sy; }
      else if (this.cfg.fingerDrag) { acts.push(...this.beginDrag(one.sx, one.sy, 'move')); }
      else { this.phase = 'swipe'; return acts; }
    }
    if (pad) {
      const dt = Math.max(1, ev.t - one.lastT), d = padDelta(ev.x - one.x, ev.y - one.y, dt, this.cfg.padSpeed);
      acts.push({ type: 'move', where: 'pointer', dx: d.dx, dy: d.dy });
      if (this.phase === 'drag') { this.drag.moved = true; acts.push({ type: 'mark', on: true, x: ev.x, y: ev.y }); }
    } else if (this.phase === 'drag') {
      this.drag.x = ev.x; this.drag.y = ev.y;
      if (dist(p, { x: this.drag.sx, y: this.drag.sy }) > SLOP) this.drag.moved = true;
      acts.push({ type: 'move', where: 'finger', x: ev.x, y: ev.y, pressed: true });
      acts.push({ type: 'mark', on: true, x: ev.x, y: ev.y });
    }
    one.x = ev.x; one.y = ev.y; one.lastT = ev.t;
    return acts;
  }

  moveTwo(ev) {
    const two = this.two, acts = [];
    const [a, b] = [...this.fingers.values()];
    if (this.fingers.size !== 2) return acts;
    const d = dist(a, b), m = mid(a, b);
    const dd = Math.abs(d - two.d0), mm = dist(m, two.m0);
    if (!two.decided) {
      // Pinch erst bei klarer Abstandsaenderung, die ueber die Bewegung der Mitte hinausgeht; sonst Scrollen, sobald die Mitte wandert und der Abstand sich kaum aendert
      if (dd >= PINCH_MIN && dd > mm * PINCH_DOMINANCE) two.decided = 'pinch';
      else if (mm >= SCROLL_MIN && dd < mm * SCROLL_DOMINANCE) two.decided = 'scroll';
      else if (dd >= PINCH_MIN && mm >= SCROLL_MIN) two.decided = 'scroll';
      if (two.decided === 'scroll') two.mLast = two.m0;
    }
    if (two.decided === 'pinch' && d > 10) {
      two.lastRatio = d / two.d0;
      acts.push({ type: 'pinch', ratio: two.lastRatio, d0: two.d0, d, x: m.x, y: m.y, start: !two.started });
      two.started = true;
    } else if (two.decided === 'scroll') {
      let dx = m.x - two.mLast.x, dy = m.y - two.mLast.y;
      two.mLast = m;
      // die Achse, auf der sich der Finger nicht bewusst bewegt, wird unterdrueckt (leichtes Abdriften soll nicht waagerecht scrollen)
      two.ex = two.ex * 0.9 + Math.abs(dx); two.ey = two.ey * 0.9 + Math.abs(dy);
      if (two.ex < two.ey * 0.4) dx = 0;
      if (two.ey < two.ex * 0.4) dy = 0;
      if (dx || dy) {
        const sign = this.cfg.scrollDir === 'natural' ? -1 : 1;
        acts.push({ type: 'scroll', dx: sign * dx, dy: sign * dy, fdx: dx, fdy: dy, x: m.x, y: m.y });
      }
    }
    return acts;
  }

  up(ev) {
    const acts = [];
    if (!this.fingers.has(ev.id)) return acts;
    this.fingers.delete(ev.id);
    const left = this.fingers.size;
    switch (this.phase) {
      case 'one': {
        const one = this.one;
        if (left) break;
        if (one.ring) acts.push({ type: 'ring', phase: 'cancel' });
        acts.push({ type: 'click', button: 'left', where: this.where, x: one.sx, y: one.sy, how: 'tap' });
        this.lastTap = { x: one.sx, y: one.sy, t: ev.t };
        this.phase = 'idle';
        break;
      }
      case 'move': case 'swipe': case 'fired':
        if (!left) this.phase = 'idle';
        break;
      case 'drag': {
        if (left) break;
        acts.push({ type: 'release', button: 'left', where: this.where, x: this.drag.x, y: this.drag.y });
        acts.push({ type: 'mark', on: false });
        this.phase = 'idle';
        break;
      }
      case 'two': {
        // schon beim ersten Finger, der hebt, ist die Geste zu Ende (ein Zwei-Finger-Tipp ist so schneller da); der Rest wartet, bis alle oben sind
        const two = this.two;
        this.phase = left ? 'multi' : 'idle';
        if (two.decided === 'pinch') acts.push({ type: 'pinchEnd', ratio: two.lastRatio });
        else if (!two.decided && ev.t - two.t0 <= TWO_TAP_MS) acts.push(...this.twoTap(ev.t, two));
        break;
      }
      default:
        if (!left) this.phase = 'idle';
    }
    if (!left && this.phase === 'multi') this.phase = 'idle';
    return acts;
  }

  twoTap(t, two) {
    const acts = [], at = two.m0;
    if (this.lastTwoTap && t - this.lastTwoTap.t <= TWO_DOUBLE_MS && this.opt.zoomed) {
      // zweiter Zwei-Finger-Tipp: Zoom zurueck, der Rechtsklick des ersten (der noch wartet) entfaellt
      this.lastTwoTap = null; this.pendingRight = null;
      acts.push({ type: 'zoomReset' });
      return acts;
    }
    this.lastTwoTap = { t };
    if (this.cfg.rightClick === 'hold') return acts;
    if (this.opt.zoomed) this.pendingRight = { at: t + TWO_DOUBLE_MS, x: at.x, y: at.y };
    else acts.push({ type: 'click', button: 'right', where: this.where, x: at.x, y: at.y, how: 'two' });
    return acts;
  }
}
