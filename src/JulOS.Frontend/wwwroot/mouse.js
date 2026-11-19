/* =====================================================================
 * Maus am Desktop-Browser: macht aus den Mausereignissen eines Elements Zustandsobjekte { x, y, left, middle, right, up, down, back, forward, wheel, hwheel }
 * (Position in Pixeln des Elements, Tasten gedrueckt oder nicht, back/forward = die Daumentasten, up/down fuer einen Rastschritt des Rads (Touch),
 * wheel/hwheel = eine Raddrehung in Windows-Einheiten, 120 je Raste, senkrecht (positiv = hoch) und waagerecht (positiv = nach rechts)),
 * wie sie StreamClient.sendMouseState nimmt.
 * Touch laeuft nicht hierueber, sondern ueber die Gestenlogik der Sitzung (app.js, wireTouch).
 * ===================================================================== */
export function mouseState(x = 0, y = 0, left = false, middle = false, right = false, up = false, down = false, back = false, forward = false, wheel = 0, hwheel = 0) {
  return { x, y, left, middle, right, up, down, back, forward, wheel, hwheel };
}

// Raddrehung in Windows-Einheiten (120 = eine Raste): Pixel (Chrome: 100 je Raste), Zeilen (Firefox: 3 je Raste) und Seiten
const WHEEL_UNITS = [1.2, 40, 360];
const clampWheel = (n) => Math.max(-32767, Math.min(32767, n));

export class MouseInput {
  constructor(element) {
    this.element = element;
    this.onmousedown = null; this.onmouseup = null; this.onmousemove = null;
    this.state = mouseState();
    const place = (e) => { const r = element.getBoundingClientRect(); this.state.x = e.clientX - r.left; this.state.y = e.clientY - r.top; };
    const button = (e, on) => {
      const name = ['left', 'middle', 'right', 'back', 'forward'][e.button];
      if (name) this.state[name] = on;
    };
    element.addEventListener('mousedown', (e) => { e.preventDefault(); place(e); button(e, true); if (this.onmousedown) this.onmousedown({ ...this.state }); });
    element.addEventListener('mouseup', (e) => { e.preventDefault(); place(e); button(e, false); if (this.onmouseup) this.onmouseup({ ...this.state }); });
    element.addEventListener('mousemove', (e) => { place(e); if (this.onmousemove) this.onmousemove({ ...this.state }); });
    // Feine Raeder (Touchpad, glatte Maeuse) liefern viele kleine Schritte: der Rest unter einer Einheit bleibt fuer das naechste Ereignis liegen
    const rest = { x: 0, y: 0 };
    element.addEventListener('wheel', (e) => {
      const dy = e.deltaY || 0, dx = e.deltaX || 0;
      if (!dy && !dx) return;
      e.preventDefault(); place(e);
      const unit = WHEEL_UNITS[e.deltaMode] ?? WHEEL_UNITS[0];
      const vy = -dy * unit + rest.y, vx = dx * unit + rest.x;
      const whole = (v) => Math.trunc(v + Math.sign(v) * 1e-6);          // 1.9999999 aus Rundungsfehlern zaehlt als 2
      const wheel = clampWheel(whole(vy)), hwheel = clampWheel(whole(vx));
      rest.y = vy - wheel; rest.x = vx - hwheel;
      if (!wheel && !hwheel) return;
      // eine Raddrehung je Ereignis: erst mit dem Wert, dann wieder ohne
      this.state.wheel = wheel; this.state.hwheel = hwheel;
      if (this.onmousedown) this.onmousedown({ ...this.state });
      this.state.wheel = this.state.hwheel = 0;
      if (this.onmouseup) this.onmouseup({ ...this.state });
    }, { passive: false });
    element.addEventListener('contextmenu', (e) => e.preventDefault());
  }
}
