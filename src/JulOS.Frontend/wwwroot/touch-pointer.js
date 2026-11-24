/* =====================================================================
 * Zeigerrechnung des Touchpad-Modus und Zusammenfassen der Mausbewegungen. Reine Funktionen, kein DOM (Test: tests/frontend/touch-pointer.test.mjs).
 * ===================================================================== */

/** Neue Zeigerstelle im Touchpad-Modus. pos ist null, solange noch keine gesetzt ist (dann Mitte des Bildes); 0/0 ist eine ganz normale Stelle (obere linke Ecke).
 *  dx, dy: Finger-Pixel; scale: Finger-Pixel je VM-Pixel; w, h: Groesse des Bildes in VM-Pixeln. */
export function padStep(pos, dx, dy, scale, w, h) {
  const s = scale > 0 ? scale : 1;
  const x0 = pos ? pos.x : w / 2, y0 = pos ? pos.y : h / 2;
  const clamp = (v, max) => Math.max(0, Math.min(Math.max(0, max), v));
  return { x: clamp(x0 + dx / s, w), y: clamp(y0 + dy / s, h) };
}

/** Muss eine wartende Bewegung vor diesem Zustand hinaus? Ja bei jeder Taste, die sich aendert, und bei jedem Radschritt: so bleiben Reihenfolge und Klickstelle genau. */
export function flushesMove(state, last) {
  if (state.wheel || state.hwheel || state.up || state.down) return true;
  return ['left', 'middle', 'right', 'back', 'forward'].some((k) => !!state[k] !== !!last[k]);
}

/** Haelt die letzte Zeigerstelle bis zum naechsten Bild. Absolute Stellen: die letzte gilt (die Summe der relativen Schritte steckt schon darin, siehe padStep). */
export class MoveCoalescer {
  constructor() { this.pending = null; }
  push(x, y) { this.pending = { x, y }; }
  /** Die wartende Stelle (oder null) und leert den Speicher. */
  take() { const p = this.pending; this.pending = null; return p; }
}
