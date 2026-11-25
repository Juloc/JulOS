/* =====================================================================
 * Gesten der Remote-Sitzung am Handy: eine reine Zustandsmaschine ohne DOM und ohne Zeitgeber (mit node pruefbar: tests/frontend/touch-gestures.test.mjs).
 * Sie bekommt Zeigerereignisse mit Zeit { type: 'down' | 'move' | 'up' | 'cancel', id, x, y, t } und die verstrichene Zeit (tick) und gibt Aktionen zurueck, die app.js
 * ausfuehrt (Klick, Taste halten, Zeiger bewegen, scrollen, zoomen) und touch-ui.js zeigt (Ring, Markierung). Die Zeit t ist in Millisekunden (performance.now()).
 *
 * Modus 'tablet' (Vorgabe am Handy): ein Finger wischt = scrollt (mit Nachlauf), Tippen = Klick, doppelt tippen = Doppelklick, Halten = Ziehen bereit, laenger = Rechtsklick bereit,
 * zwei Finger = optischer Zoom und Bild verschieben. Die Regeln stehen in docs/TOUCH.md und in den Tests (tests/frontend/touch-tablet.test.mjs).
 * Gesten der Modi 'direct' (Oberflaeche: "Maus": die Stelle des Fingers ist die Stelle im Bild) und 'pad' (der Finger bewegt den Zeiger wie ein Touchpad):
 *   Tippen                      Linksklick (direkt an der Stelle, pad am Zeiger)
 *   Halten (Ring fuellt sich)   Rechtsklick; Bewegung ueber die Schwelle bricht ab
 *   Zwei-Finger-Tipp            Rechtsklick (wenn die Rechtsklick-Methode ihn zulaesst)
 *   Zwei Finger schieben        Scrollen senkrecht und waagerecht; Pinch erst bei klarer Abstandsaenderung
 *   Doppeltipp und halten       Ziehen: die linke Taste bleibt gedrueckt, bis der Finger hebt (pad: "Tippen und ziehen")
 *   Ein Finger schieben         direkt: ziehen (wie bisher), pad: Zeiger bewegen
 *   Pinch                       Zoom
 *   Doppeltipp mit zwei Fingern Zoom zuruecksetzen (nur wenn gezoomt ist)
 * Aktionen (type): hover, click, press, release, move, scroll, pinch, pinchEnd (Aufloesung), zoom, zoomEnd, pan (optisch), zoomReset, ring, mark, log (Zusammenfassung einer Geste fuers Protokoll).
 * ===================================================================== */

export const HOLD_MIN = 300, HOLD_MAX = 1000;
export const TOUCH_DEFAULTS = Object.freeze({
  mode: 'tablet',            // 'tablet' | 'direct' (Maus) | 'pad' (Touchpad)
  pinch: 'off',              // zwei Finger spreizen: 'off' (nichts; gezoomt wird mit den Tasten der Leiste, zwei Finger scrollen nur) | 'view' (Tablet: Bild im Browser vergroessern) | 'size' (Aufloesung der VM)
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
  mode: ['tablet', 'direct', 'pad'], pinch: ['off', 'view', 'size'], rightClick: ['hold', 'two', 'both'], scrollDir: ['natural', 'classic'], scrollSpeed: ['slow', 'normal', 'fast'],
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
/* Tablet: Wischen, Nachlauf */
export const FLING_MIN = 0.3;         // Nachlauf nur ab dieser Geschwindigkeit beim Heben (Pixel je Millisekunde)
export const FLING_MAX = 3.5;
export const FLING_TAU = 350;         // die Geschwindigkeit klingt mit dieser Zeitkonstante ab (ms)
export const FLING_STOP = 0.04;       // darunter ist der Nachlauf zu Ende
export const FLING_FRAME = 16;
export const LOG_MAX = 60;            // so viele Gesten-Eintraege je Sitzung hoechstens ins Protokoll (app.js)
const PINCH_DOMINANCE = 1.3, SCROLL_DOMINANCE = 0.6;

const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
const mid = (a, b) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });
/** Tablet: ab wann "Ziehen bereit" gilt (der Ring ist halb voll): in der Mitte zwischen Ring-Beginn und voller Haltedauer. */
export const dragReadyMs = (holdMs) => Math.round(RING_DELAY + (holdMs - RING_DELAY) / 2);

/** Pixel des Fingers je Raste des Mausrads: eine Raste verschiebt am Ziel etwa 40 Pixel der VM (am Terminal des Testziels gemessen: gut 2 Zeilen), mal dem Massstab des Bildes (Finger-Pixel je VM-Pixel),
 *  damit der Inhalt moeglichst mit dem Finger geht. Ohne Massstab (Tests, Touchpad) wie bisher feste Werte. */
export const scrollStep = (speed, scale) => {
  if (!(scale > 0)) return { slow: 48, normal: 34, fast: 22 }[speed] || 34;
  const vm = { slow: 56, normal: 40, fast: 28 }[speed] || 48;
  return clamp(vm * scale, 14, 90);
};

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

/* ---------- Optischer Zoom (Tablet): reine Rechnung, getrennt von der Anzeige ----------
 * Das Bild wird im Browser vergroessert (CSS scale um die obere linke Ecke), die Aufloesung der VM bleibt. Ansicht v = { z, x, y }: z der Faktor (1 bis 4),
 * (x, y) die zusaetzliche Verschiebung in Pixeln der Bildflaeche. Stelle auf dem Schirm = Bildflaeche + base + (x, y) + z * (Massstab * VM-Pixel).
 * stage = { w, h } die Bildflaeche, content = { w, h } das Bild bei z = 1 (VM-Groesse mal Massstab), base = { x, y } die Grundverschiebung (Taskleiste, Tastatur). */
export const ZOOM_MAX = 4;
export const VIEW_ONE = Object.freeze({ z: 1, x: 0, y: 0 });
/** Haelt die Ansicht so, dass das Bild die Flaeche ausfuellt (keine leeren Raender); bei z = 1 ist es die Grundstellung. */
export function clampView(v, { stage, content, base = { x: 0, y: 0 } }) {
  const z = clamp(Number.isFinite(v.z) ? v.z : 1, 1, ZOOM_MAX);
  if (z < 1.01) return { z: 1, x: 0, y: 0 };
  const fit = (val, viewLen, contLen, b) => {
    const hi = -b, lo = viewLen - z * contLen - b;
    return (lo > hi ? (lo + hi) / 2 : clamp(val, lo, hi)) || 0;      // || 0: kein -0
  };
  return { z, x: fit(v.x, stage.w, content.w, base.x), y: fit(v.y, stage.h, content.h, base.y) };
}
/** Pinch: neuer Faktor aus dem Verhaeltnis seit Beginn; der Bildpunkt, der zu Beginn unter der Mitte der Finger lag (from), liegt danach unter der aktuellen Mitte (to). Mitten in Bildflaechen-Pixeln. */
export function zoomAbout(v0, ratio, from, to, b) {
  const z = clamp(v0.z * ratio, 1, ZOOM_MAX), base = b.base || { x: 0, y: 0 };
  const u = { x: (from.x - base.x - v0.x) / v0.z, y: (from.y - base.y - v0.y) / v0.z };
  return clampView({ z, x: to.x - base.x - z * u.x, y: to.y - base.y - z * u.y }, b);
}
/** Zwei Finger schieben das vergroesserte Bild: es folgt dem Finger. */
export const panView = (v, dx, dy, b) => clampView({ z: v.z, x: v.x + dx, y: v.y + dy }, b);
/** Fingerstelle (Fensterkoordinaten) -> Pixel der VM. rect: Rechteck des Bildes im Fenster (enthaelt Verschiebung und Zoom schon), scale: Massstab des Bildes, z: optischer Faktor. */
export const toVm = (cx, cy, rect, scale, z = 1) => ({ x: Math.round((cx - rect.left) / (scale * z)), y: Math.round((cy - rect.top) / (scale * z)) });

export class TouchGestures {
  constructor(cfg = {}) {
    this.cfg = normalizeTouch(cfg);
    this.opt = { zoomed: false, viewZoomed: false, reducedMotion: false };
    this.fingers = new Map();
    this.phase = 'idle';
    this.lastTap = null;           // { x, y, t } des letzten Tippens (fuer Doppeltipp und halten)
    this.lastTwoTap = null;        // { t } des letzten Zwei-Finger-Tipps (fuer das Zuruecksetzen des Zooms)
    this.pendingRight = null;      // aufgeschobener Rechtsklick eines Zwei-Finger-Tipps, solange noch ein zweiter Tipp (Zoom zurueck) kommen koennte
    this.fling = null;             // Nachlauf nach dem Wischen (Tablet): { vx, vy, t0, last, x, y }
  }

  setConfig(cfg) { const was = this.cfg.mode; this.cfg = normalizeTouch(cfg); if (was !== this.cfg.mode) this.fling = null; }
  /** zoomed: das Bild ist gezoomt, ein Zwei-Finger-Doppeltipp setzt es zurueck (dann wartet der Rechtsklick des ersten Tipps kurz); im Tablet-Modus verschieben zwei Finger dann das Bild.
   *  reducedMotion: kein Nachlauf. */
  setOptions(opt) { Object.assign(this.opt, opt); }

  get tablet() { return this.cfg.mode === 'tablet'; }
  get where() { return this.cfg.mode === 'pad' ? 'pointer' : 'finger'; }
  /** Wird gerade eine Taste gehalten (Ziehen)? Dann muss sie beim Abbruch losgelassen werden. */
  get pressed() { return this.phase === 'drag'; }
  get busy() { return this.phase !== 'idle'; }
  /** Tablet: Zeit, ab der "Ziehen bereit" gilt, und ab der der Rechtsklick bereit ist (null: Rechtsklick durch Halten ist aus). */
  get readyMs() { return dragReadyMs(this.cfg.holdMs); }
  get ctxMs() { return this.cfg.rightClick === 'two' ? null : this.cfg.holdMs; }

  /** Zeit (wie t der Ereignisse), zu der tick() etwas tun wuerde, oder null. */
  deadline() {
    const one = this.one;
    const list = [];
    if (this.phase === 'one' && this.tablet) {
      if (!one.ring) list.push(one.t0 + RING_DELAY);
      if (one.stage < 1) list.push(one.t0 + this.readyMs);
      if (one.stage < 2 && this.ctxMs !== null) list.push(one.t0 + this.ctxMs);
    } else if (this.phase === 'one' && one.holdArmed) { if (!one.ring) list.push(one.t0 + RING_DELAY); list.push(one.t0 + this.cfg.holdMs); }
    if (this.pendingRight) list.push(this.pendingRight.at);
    if (this.fling) list.push(this.fling.last + FLING_FRAME);
    return list.length ? Math.min(...list) : null;
  }

  /** Die Zeit ist bis t vergangen: Ring zeigen, Rechtsklick nach dem Halten, aufgeschobenen Rechtsklick ausloesen, Nachlauf weiterlaufen lassen. */
  tick(t) {
    const acts = [];
    const one = this.one;
    if (this.phase === 'one' && this.tablet) acts.push(...this.tickHold(t));
    else if (this.phase === 'one' && one.holdArmed) {
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
    if (this.fling) acts.push(...this.tickFling(t));
    return acts;
  }

  /** Tablet, Finger liegt still: Ring (ab 140 ms), "Ziehen bereit" (Ring halb voll, kurze Vibration), "Rechtsklick bereit" (Ring voll, laengere Vibration). */
  tickHold(t) {
    const one = this.one, acts = [], end = this.ctxMs ?? this.readyMs;
    if (!one.ring && t >= one.t0 + RING_DELAY && t < one.t0 + end) {
      one.ring = true;
      acts.push({ type: 'ring', phase: 'start', x: one.sx, y: one.sy, ms: one.t0 + end - t });
    }
    if (one.stage < 1 && t >= one.t0 + this.readyMs) {
      one.stage = 1; one.ring = true;
      acts.push({ type: 'ring', phase: 'ready', x: one.sx, y: one.sy });
    }
    if (one.stage < 2 && this.ctxMs !== null && t >= one.t0 + this.ctxMs) {
      one.stage = 2;
      acts.push({ type: 'ring', phase: 'armed', x: one.sx, y: one.sy });
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

  /** Nachlauf: die Geschwindigkeit klingt exponentiell ab; je Aufruf der Weg seit dem letzten. */
  tickFling(t) {
    const f = this.fling;
    if (t <= f.last) return [];
    const el = t - f.t0, k = FLING_TAU * (Math.exp(-(f.last - f.t0) / FLING_TAU) - Math.exp(-el / FLING_TAU));
    f.last = t;
    const dx = f.vx * k, dy = f.vy * k, acts = [];
    if (dx || dy) acts.push(this.scrollAct(dx, dy, f.x, f.y, true));
    if (Math.hypot(f.vx, f.vy) * Math.exp(-el / FLING_TAU) < FLING_STOP || el > 3000) {
      acts.push(this.logAct('fling', 1, f.t0, t, Math.hypot(f.vx, f.vy) * FLING_TAU));
      this.fling = null;
    }
    return acts;
  }

  /** Eine Scroll-Aktion: (dx, dy) in Pixeln des Fingers (Richtung des Fingers), das Vorzeichen je nach Scrollrichtung kommt hier dazu. */
  scrollAct(fdx, fdy, x, y, fling = false) {
    const sign = this.cfg.scrollDir === 'natural' ? -1 : 1;
    return { type: 'scroll', dx: sign * fdx, dy: sign * fdy, fdx, fdy, x, y, fling };
  }
  /** Zusammenfassung einer Geste fuers Protokoll (keine Inhalte): Art, Fingerzahl, Dauer, Weg. Die Zahl der Rad-Schritte setzt app.js dazu. */
  logAct(kind, fingers, t0, t, px = 0) {
    return { type: 'log', kind, fingers, ms: Math.max(0, Math.round(t - t0)), px: Math.round(px) };
  }

  /** Alles abbrechen (touchcancel, Sitzung verlassen): gehaltene Taste loslassen, Ring und Markierung weg, Nachlauf stoppen. */
  cancel() {
    const acts = this.dropAll();
    if (this.tablet && this.phase !== 'idle') acts.push({ type: 'log', kind: 'cancel', fingers: this.fingers.size, ms: 0, px: 0, was: this.phase });
    this.fingers.clear(); this.phase = 'idle'; this.lastTap = null; this.pendingRight = null; this.fling = null;
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
    if (this.fling) { this.stopped = true; this.fling = null; }           // ein neuer Finger stoppt den Nachlauf sofort (und klickt dann nicht)
    const stopped = this.stopped; this.stopped = false;
    if (n === 1 && this.phase === 'idle') return this.tablet ? this.startOneTablet(ev, stopped) : this.startOne(ev);
    if (n === 2 && ['one', 'move', 'swipe', 'drag', 'tscroll'].includes(this.phase)) {
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

  /** Tablet: ein Finger kommt auf. Ein zweites Tippen nahe am ersten wird auf dessen Stelle gelegt, damit das Ziel zwei Klicks an derselben Stelle (einen Doppelklick) sieht. */
  startOneTablet(ev, stopped) {
    const double = !stopped && !!this.lastTap && ev.t - this.lastTap.t <= DOUBLE_MS && dist(this.lastTap, ev) <= DOUBLE_DIST;
    const sx = double ? this.lastTap.x : ev.x, sy = double ? this.lastTap.y : ev.y;
    this.one = { sx, sy, ax: ev.x, ay: ev.y, x: ev.x, y: ev.y, t0: ev.t, lastT: ev.t, ring: false, stage: 0, double, noTap: stopped, samples: [{ t: ev.t, x: ev.x, y: ev.y }] };
    this.phase = 'one';
    return [{ type: 'hover', x: sx, y: sy }];       // Zeiger vorab hinbewegen: Zeigerform (Textfeld?) kommt an, und das Fenster unter dem Finger ist das aktive fuers Rad
  }

  beginDrag(x, y, how) {
    this.phase = 'drag';
    this.drag = { x, y, how, moved: false, sx: x, sy: y, t0: this.one ? this.one.t0 : 0, path: 0 };
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
    if (this.phase === 'one' && this.tablet) return this.moveTablet(ev);
    if (this.phase === 'tscroll') return this.moveScroll(ev);
    if (this.phase === 'one' || this.phase === 'move' || this.phase === 'drag') return this.moveOne(ev);
    if (this.phase === 'two') return this.moveTwo(ev);
    return [];
  }

  /** Tablet, erste Bewegung ueber die Schwelle: vor "Ziehen bereit" ein Wisch (scrollen), danach ziehen (linke Taste an der Startstelle). */
  moveTablet(ev) {
    const one = this.one, acts = [];
    one.x = ev.x; one.y = ev.y;
    if (dist(ev, { x: one.ax, y: one.ay }) <= SLOP) { this.sample(one, ev); return acts; }
    if (one.ring) acts.push({ type: 'ring', phase: 'cancel' });
    one.ring = false;
    this.lastTap = null;
    if (one.stage >= 1) {
      acts.push(...this.beginDrag(one.sx, one.sy, 'hold'));
      this.drag.how = one.stage >= 2 ? 'hold' : 'ready';
      this.drag.x = ev.x; this.drag.y = ev.y; this.drag.moved = true;
      acts.push({ type: 'move', where: 'finger', x: ev.x, y: ev.y, pressed: true }, { type: 'mark', on: true, x: ev.x, y: ev.y });
      return acts;
    }
    // Wisch: Achse festlegen, ab dem ersten Aufsetzen mitnehmen, damit der Inhalt dem Finger folgt
    this.phase = 'tscroll';
    this.sw = { lock: null, lx: one.ax, ly: one.ay, ax: 0, ay: 0, px: one.sx, py: one.sy, samples: one.samples, t0: one.t0, path: 0, started: false };
    return acts.concat(this.moveScroll(ev));
  }

  sample(o, ev) { o.samples.push({ t: ev.t, x: ev.x, y: ev.y }); if (o.samples.length > 8) o.samples.shift(); }

  /** Wisch mit einem Finger: Achsen-Sperre (leichtes Abdriften scrollt nicht quer) und eine Verstaerkung bei schnellem Wischen. */
  moveScroll(ev) {
    const sw = this.sw;
    let dx = ev.x - sw.lx, dy = ev.y - sw.ly;
    sw.lx = ev.x; sw.ly = ev.y;
    sw.path += Math.hypot(dx, dy);
    sw.ax += Math.abs(dx); sw.ay += Math.abs(dy);
    this.sample(sw, ev);
    if (!sw.lock) sw.lock = sw.ay >= sw.ax * 1.2 ? 'v' : sw.ax >= sw.ay * 1.2 ? 'h' : 'free';
    else if (sw.lock !== 'free') {
      // wer deutlich quer weiterwischt, entsperrt die Achse
      const cross = sw.lock === 'v' ? sw.ax : sw.ay, main = sw.lock === 'v' ? sw.ay : sw.ax;
      if (cross > 48 && cross > main * 0.7) sw.lock = 'free';
    }
    if (sw.lock === 'v') dx = 0; else if (sw.lock === 'h') dy = 0;
    if (!dx && !dy) return [];
    const gain = 1 + Math.min(this.speed(sw, ev.t), 2) * 0.6;
    sw.started = true;
    const a = this.scrollAct(dx * gain, dy * gain, sw.px, sw.py);
    a.fdx = dx; a.fdy = dy;                    // ohne Verstaerkung: so viel hat sich der Finger wirklich bewegt (Bild verschieben bei offener Tastatur)
    return [a];
  }

  /** Geschwindigkeit des Fingers (Pixel je Millisekunde) aus den letzten Messpunkten (hoechstens 100 ms zurueck). */
  speed(o, t) { const v = this.velocity(o, t); return Math.hypot(v.vx, v.vy); }
  velocity(o, t) {
    const s = o.samples, last = s[s.length - 1];
    let first = last;
    for (let i = s.length - 1; i >= 0 && last.t - s[i].t <= 100; i--) first = s[i];
    const dt = last.t - first.t;
    if (dt < 8 || t - last.t > 70) return { vx: 0, vy: 0 };
    return { vx: (last.x - first.x) / dt, vy: (last.y - first.y) / dt };
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
      this.drag.path += Math.hypot(ev.x - this.drag.x, ev.y - this.drag.y);
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
    // Pinch aus (Standard): zwei Finger koennen nur scrollen. So wird aus einem Scrollen nie ein Zoom, auch wenn die Finger dabei auseinander wandern.
    const noPinch = this.cfg.pinch === 'off';
    if (!two.decided && noPinch) {
      if (mm >= SCROLL_MIN) { two.decided = 'scroll'; two.mLast = two.m0; }
    } else if (!two.decided) {
      // Pinch erst bei klarer Abstandsaenderung, die ueber die Bewegung der Mitte hinausgeht; sonst Scrollen, sobald die Mitte wandert und der Abstand sich kaum aendert
      if (dd >= PINCH_MIN && dd > mm * PINCH_DOMINANCE) two.decided = 'pinch';
      else if (mm >= SCROLL_MIN && dd < mm * SCROLL_DOMINANCE) two.decided = 'scroll';
      else if (dd >= PINCH_MIN && mm >= SCROLL_MIN) two.decided = 'scroll';
      if (two.decided === 'scroll') two.mLast = two.m0;
    }
    const view = this.tablet && this.cfg.pinch === 'view';
    if (two.decided === 'pinch' && d > 10) {
      two.lastRatio = d / two.d0;
      // optisch: Verhaeltnis seit Beginn und beide Mitten (Start und jetzt); das Bild bleibt unter den Fingern. Sonst: Aufloesung der VM
      acts.push(view ? { type: 'zoom', ratio: two.lastRatio, from: two.m0, x: m.x, y: m.y, start: !two.started }
        : { type: 'pinch', ratio: two.lastRatio, d0: two.d0, d, x: m.x, y: m.y, start: !two.started });
      two.started = true;
    } else if (two.decided === 'scroll') {
      let dx = m.x - two.mLast.x, dy = m.y - two.mLast.y;
      two.mLast = m;
      if (this.tablet && this.opt.viewZoomed) {
        // optisch vergroessertes Bild: zwei Finger schieben es (das Bild folgt den Fingern). Nur dann: eine geaenderte Anzeigegroesse (Aufloesung) hat nichts zu verschieben,
        // dort scrollen zwei Finger wie sonst auch.
        if (dx || dy) { two.panned = true; acts.push({ type: 'pan', dx, dy, x: m.x, y: m.y }); }
        return acts;
      }
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
        if (this.tablet) { acts.push(...this.upTablet(ev, one)); this.phase = 'idle'; break; }
        acts.push({ type: 'click', button: 'left', where: this.where, x: one.sx, y: one.sy, how: 'tap' });
        this.lastTap = { x: one.sx, y: one.sy, t: ev.t };
        this.phase = 'idle';
        break;
      }
      case 'tscroll': {
        if (left) break;
        acts.push(...this.endScroll(ev));
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
        if (this.tablet) acts.push(this.logAct('drag-' + this.drag.how, 1, this.drag.t0, ev.t, this.drag.path));
        this.phase = 'idle';
        break;
      }
      case 'two': {
        // schon beim ersten Finger, der hebt, ist die Geste zu Ende (ein Zwei-Finger-Tipp ist so schneller da); der Rest wartet, bis alle oben sind
        const two = this.two;
        this.phase = left ? 'multi' : 'idle';
        if (two.decided === 'pinch') {
          acts.push(this.tablet && this.cfg.pinch === 'view' ? { type: 'zoomEnd', ratio: two.lastRatio } : { type: 'pinchEnd', ratio: two.lastRatio });
          if (this.tablet) acts.push(this.logAct('pinch', 2, two.t0, ev.t, Math.abs(two.lastRatio - 1) * 100));
        } else if (!two.decided && ev.t - two.t0 <= TWO_TAP_MS) {
          acts.push(...this.twoTap(ev.t, two));
          if (this.tablet) acts.push(this.logAct('two-tap', 2, two.t0, ev.t));
        } else if (this.tablet && two.decided) acts.push(this.logAct(two.panned ? 'pan' : 'two-scroll', 2, two.t0, ev.t, dist(two.mLast, two.m0)));
        break;
      }
      default:
        if (!left) this.phase = 'idle';
    }
    if (!left && this.phase === 'multi') this.phase = 'idle';
    return acts;
  }

  /** Tablet, der Finger hebt ohne Wisch und ohne Ziehen: Tippen, Doppeltipp, langes Tippen (Ziehen bereit, aber nicht bewegt) oder Rechtsklick (Ring voll). */
  upTablet(ev, one) {
    const acts = [], at = { x: one.sx, y: one.sy }, ms = ev.t - one.t0;
    if (one.noTap) { acts.push(this.logAct('stop', 1, one.t0, ev.t)); return acts; }
    if (one.stage >= 2) {
      acts.push({ type: 'click', button: 'right', where: 'finger', ...at, how: 'hold' });
      this.lastTap = null;
      acts.push(this.logAct('right', 1, one.t0, ev.t));
    } else if (one.stage === 1) {
      acts.push({ type: 'click', button: 'left', where: 'finger', ...at, how: 'long' });
      this.lastTap = null;
      acts.push(this.logAct('long', 1, one.t0, ev.t));
    } else {
      acts.push({ type: 'click', button: 'left', where: 'finger', ...at, how: one.double ? 'double' : 'tap' });
      this.lastTap = one.double ? null : { ...at, t: ev.t };
      acts.push(this.logAct(one.double ? 'double' : 'tap', 1, one.t0, ev.t));
    }
    return acts;
  }

  /** Tablet, der wischende Finger hebt: Nachlauf mit der letzten Geschwindigkeit (nicht bei reduzierter Bewegung), sonst Ende. */
  endScroll(ev) {
    const sw = this.sw, acts = [];
    const v = this.velocity(sw, ev.t);
    let { vx, vy } = v;
    if (sw.lock === 'v') vx = 0; else if (sw.lock === 'h') vy = 0;
    const sp = Math.hypot(vx, vy);
    if (!this.opt.reducedMotion && sp >= FLING_MIN) {
      const k = Math.min(sp, FLING_MAX) / sp;
      this.fling = { vx: vx * k, vy: vy * k, t0: ev.t, last: ev.t, x: sw.px, y: sw.py };
      acts.push(this.logAct('swipe', 1, sw.t0, ev.t, sw.path));
    } else acts.push(this.logAct('swipe', 1, sw.t0, ev.t, sw.path));
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
