/* =====================================================================
 * Maus am Desktop-Browser: macht aus den Mausereignissen eines Elements Zustandsobjekte { x, y, left, middle, right, up, down }
 * (Position in Pixeln des Elements, Tasten gedrueckt oder nicht, up/down fuer einen Rastschritt des Rads), wie sie StreamClient.sendMouseState nimmt.
 * Touch laeuft nicht hierueber, sondern ueber die Gestenlogik der Sitzung (app.js, wireTouch).
 * ===================================================================== */
export function mouseState(x = 0, y = 0, left = false, middle = false, right = false, up = false, down = false) {
  return { x, y, left, middle, right, up, down };
}

export class MouseInput {
  constructor(element) {
    this.element = element;
    this.onmousedown = null; this.onmouseup = null; this.onmousemove = null;
    this.state = mouseState();
    const place = (e) => { const r = element.getBoundingClientRect(); this.state.x = e.clientX - r.left; this.state.y = e.clientY - r.top; };
    const button = (e, on) => { if (e.button === 0) this.state.left = on; else if (e.button === 1) this.state.middle = on; else if (e.button === 2) this.state.right = on; };
    element.addEventListener('mousedown', (e) => { e.preventDefault(); place(e); button(e, true); if (this.onmousedown) this.onmousedown({ ...this.state }); });
    element.addEventListener('mouseup', (e) => { e.preventDefault(); place(e); button(e, false); if (this.onmouseup) this.onmouseup({ ...this.state }); });
    element.addEventListener('mousemove', (e) => { place(e); if (this.onmousemove) this.onmousemove({ ...this.state }); });
    element.addEventListener('wheel', (e) => {
      if (!e.deltaY) return;
      e.preventDefault(); place(e);
      // ein Rastschritt je Ereignis: erst gedrueckt, dann wieder los
      this.state.up = e.deltaY < 0; this.state.down = e.deltaY > 0;
      if (this.onmousedown) this.onmousedown({ ...this.state });
      this.state.up = this.state.down = false;
      if (this.onmouseup) this.onmouseup({ ...this.state });
    }, { passive: false });
    element.addEventListener('contextmenu', (e) => e.preventDefault());
  }
}
