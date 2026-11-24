/* =====================================================================
 * Rueckmeldung der Touch-Gesten: der Ring, der sich beim Halten um den Finger fuellt, die Markierung beim Ziehen und die kurze Vibration (wo der Browser sie kennt).
 * Eine Ebene je Sitzung ueber dem Bild (pointer-events: none, Stil in touch.css). Die Gesten selbst stehen in touch-gestures.js, das Einstellungsblatt in touch-settings.js.
 * ===================================================================== */

const SVG = 'http://www.w3.org/2000/svg';

/** Kurze Vibration, wo vorhanden und eingeschaltet (nur nach einer Geste erlaubt, der Finger liegt ja auf dem Schirm). */
export function buzz(ms, on = true) {
  try { if (on && typeof navigator !== 'undefined' && navigator.vibrate) navigator.vibrate(ms); } catch { /* egal */ }
}

export class TouchUi {
  /** @param {HTMLElement} host das Element, ueber dem die Ebene liegt (position: relative), @param {() => boolean} vibrate ob vibriert werden darf */
  constructor(host, vibrate = () => true) {
    this.host = host; this.vibrate = vibrate;
    this.layer = document.createElement('div'); this.layer.className = 'tt-layer'; this.layer.setAttribute('aria-hidden', 'true');
    host.appendChild(this.layer);
    this.ringEl = null; this.markEl = null; this.fullTimer = 0;
  }

  /** Stelle im Host aus Fensterkoordinaten. */
  local(x, y) { const r = this.host.getBoundingClientRect(); return { x: x - r.left, y: y - r.top }; }

  ringStart(x, y, ms) {
    this.ringClear();
    const p = this.local(x, y);
    const el = document.createElement('div'); el.className = 'tt-ring';
    el.style.left = p.x + 'px'; el.style.top = p.y + 'px'; el.style.setProperty('--tt-ms', Math.max(60, ms) + 'ms');
    const svg = document.createElementNS(SVG, 'svg'); svg.setAttribute('viewBox', '0 0 64 64');
    for (const cls of ['tt-track', 'tt-fill']) {
      const c = document.createElementNS(SVG, 'circle'); c.setAttribute('class', cls); c.setAttribute('cx', '32'); c.setAttribute('cy', '32'); c.setAttribute('r', '27'); svg.appendChild(c);
    }
    el.appendChild(svg); this.layer.appendChild(el); this.ringEl = el;
  }
  ringClear() { clearTimeout(this.fullTimer); if (this.ringEl) { this.ringEl.remove(); this.ringEl = null; } }
  /** Der Ring ist voll: kurzes Aufleuchten und Vibration; der Rechtsklick geht gleichzeitig hinaus. */
  ringFull(x, y) {
    if (!this.ringEl) this.ringStart(x, y, 60);
    this.ringEl.classList.add('full');
    buzz(22, this.vibrate());
    const el = this.ringEl;
    this.fullTimer = setTimeout(() => { if (this.ringEl === el) this.ringClear(); }, 420);
  }

  /** Tablet, "Ziehen bereit": der Ring ist halb voll, kurze Vibration, die Farbe wechselt. */
  ringReady(x, y) {
    if (!this.ringEl) this.ringStart(x, y, 60);
    this.ringEl.classList.add('ready');
    buzz(12, this.vibrate());
  }
  /** Tablet, "Rechtsklick bereit": der Ring ist voll und bleibt stehen, bis der Finger hebt oder zieht. */
  ringArmed(x, y) {
    if (!this.ringEl) this.ringStart(x, y, 60);
    this.ringEl.classList.add('armed');
    buzz(22, this.vibrate());
  }

  /** Markierung beim Ziehen (die linke Taste ist gedrueckt): folgt dem Finger. */
  markOn(x, y) {
    const p = this.local(x, y);
    if (!this.markEl) {
      this.markEl = document.createElement('div'); this.markEl.className = 'tt-mark'; this.layer.appendChild(this.markEl);
      buzz(12, this.vibrate());
    }
    this.markEl.style.left = p.x + 'px'; this.markEl.style.top = p.y + 'px';
  }
  markOff() { if (this.markEl) { this.markEl.remove(); this.markEl = null; } }

  /** Die Aktionen der Gesten-Maschine, die etwas zu zeigen haben. */
  show(a) {
    if (a.type === 'ring') {
      if (a.phase === 'start') this.ringStart(a.x, a.y, a.ms);
      else if (a.phase === 'full') this.ringFull(a.x, a.y);
      else if (a.phase === 'ready') this.ringReady(a.x, a.y);
      else if (a.phase === 'armed') this.ringArmed(a.x, a.y);
      else this.ringClear();
    } else if (a.type === 'mark') { if (a.on) this.markOn(a.x, a.y); else this.markOff(); }
  }

  dispose() { this.ringClear(); this.markOff(); this.layer.remove(); }
}

/**
 * Haengt die Gesten-Maschine an die Touch-Ereignisse eines Elements: Ereignisse -> Maschine -> Aktionen (onActions), und ein Zeitgeber ruft tick() zur Frist
 * (Ring, Rechtsklick nach dem Halten). Liegt noch ein Finger in der Maschine, der laut Browser laengst oben ist (verlorenes touchend), wird er zuerst abgemeldet.
 * Gibt { cancel, dispose } zurueck; cancel() bricht eine laufende Geste ab (Sitzung verlassen, Fenster ohne Fokus).
 */
export function bindSurface(el, g, onActions, { now = () => performance.now() } = {}) {
  let timer = 0;
  const out = (acts) => { if (acts.length) onActions(acts); schedule(); };
  function schedule() {
    clearTimeout(timer); timer = 0;
    const d = g.deadline();
    if (d === null) return;
    timer = setTimeout(() => { timer = 0; out(g.tick(now())); }, Math.max(0, d - now()) + 1);
  }
  const handle = (type) => (e) => {
    e.preventDefault();
    const t = now(), acts = [];
    if (type === 'down') {
      const live = new Set([...e.touches].map((x) => x.identifier));
      for (const id of [...g.fingers.keys()]) if (!live.has(id) && ![...e.changedTouches].some((x) => x.identifier === id)) acts.push(...g.feed({ type: 'up', id, x: 0, y: 0, t }));
    }
    for (const tc of e.changedTouches) acts.push(...g.feed({ type, id: tc.identifier, x: tc.clientX, y: tc.clientY, t }));
    out(acts);
  };
  const on = { touchstart: handle('down'), touchmove: handle('move'), touchend: handle('up'), touchcancel: (e) => { e.preventDefault(); out(g.cancel()); } };
  for (const [name, fn] of Object.entries(on)) el.addEventListener(name, fn, { passive: false });
  const ctx = (e) => e.preventDefault();
  el.addEventListener('contextmenu', ctx);
  return {
    cancel() { out(g.cancel()); },
    dispose() { clearTimeout(timer); for (const [name, fn] of Object.entries(on)) el.removeEventListener(name, fn); el.removeEventListener('contextmenu', ctx); },
  };
}
