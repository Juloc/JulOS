/* =====================================================================
 * Taskleiste des fernen Desktops im Dock (docs/DOCK-REMOTE-TASKBAR.md): die reine Rechnung, ohne DOM.
 * tb = { side: 'bottom' | 'top' | 'left' | 'right', p } : die Leiste der VM ist ein Streifen am Rand ihres Bildes, p Pixel der VM breit (oben und unten: hoch).
 * Die VM bekommt Breite x (Hoehe + p) (oder (Breite + p) x Hoehe), das Fenster zeigt den Rest, das Dock den Streifen. Alle Masse sind Pixel der VM, ganze Zahlen.
 * Getestet in tests/frontend/desktop-taskbar.test.mjs.
 * ===================================================================== */

export const SIDES = ['bottom', 'top', 'left', 'right'];
export const MIN_P = 8;
const EDGE_TOL = 16;                    // so weit darf das gezogene Rechteck vom Bildrand entfernt enden
const horizontal = (tb) => tb.side === 'bottom' || tb.side === 'top';

/** Eine gespeicherte oder gelieferte Angabe pruefen: gueltige Seite, ganze Groesse zwischen MIN_P und 400, sonst null (keine Leiste). */
export function normalizeTb(v) {
  if (!v || typeof v !== 'object' || !SIDES.includes(v.side)) return null;
  const p = Math.round(Number(v.p));
  if (!Number.isFinite(p) || p < MIN_P || p > 400) return null;
  return { side: v.side, p };
}

/** Groesse, die an die VM geht: die Flaeche des Fensters plus die Leiste (ohne Leiste unveraendert). */
export function vmSize(want, tb) {
  if (!tb) return { w: want.w, h: want.h };
  return horizontal(tb) ? { w: want.w, h: want.h + tb.p } : { w: want.w + tb.p, h: want.h };
}

/** Der Streifen im Bild der VM: { x, y, w, h } (Quellrechteck fuer drawImage). p wird auf das Bild begrenzt (mindestens ein Pixel bleibt fuer das Fenster). */
export function stripRect(vm, tb) {
  const p = Math.min(tb.p, (horizontal(tb) ? vm.h : vm.w) - 1);
  switch (tb.side) {
    case 'bottom': return { x: 0, y: vm.h - p, w: vm.w, h: p };
    case 'top': return { x: 0, y: 0, w: vm.w, h: p };
    case 'left': return { x: 0, y: 0, w: p, h: vm.h };
    default: return { x: vm.w - p, y: 0, w: p, h: vm.h };
  }
}

/** Der Teil des Bildes fuer das Fenster (alles ausser dem Streifen). */
export function workRect(vm, tb) {
  if (!tb) return { x: 0, y: 0, w: vm.w, h: vm.h };
  const s = stripRect(vm, tb);
  switch (tb.side) {
    case 'bottom': return { x: 0, y: 0, w: vm.w, h: vm.h - s.h };
    case 'top': return { x: 0, y: s.h, w: vm.w, h: vm.h - s.h };
    case 'left': return { x: s.w, y: 0, w: vm.w - s.w, h: vm.h };
    default: return { x: 0, y: 0, w: vm.w - s.w, h: vm.h };
  }
}

/** Verschiebung des ganzen Bildes im Fenster (in Pixeln der VM, vor dem Skalieren): oben und links rutscht der Streifen aus dem Bild. */
export function displayOffset(vm, tb) {
  const r = workRect(vm, tb);
  return { x: 0 - r.x, y: 0 - r.y };      // 0 - statt Minus: kein -0
}

/**
 * Aus einem gezogenen Rechteck (Eckpunkte in Pixeln der VM, in beliebiger Reihenfolge) Seite und Groesse ableiten.
 * Das Rechteck muss an einem Rand des Bildes enden (EDGE_TOL), mindestens 60 % von dessen Laenge decken und hoechstens die halbe Tiefe haben;
 * p ist der Abstand von der inneren Kante des Rechtecks bis zum Rand. null, wenn es nach keiner Leiste aussieht.
 */
export function deriveTaskbar(a, b, vm) {
  const x0 = Math.max(0, Math.min(a.x, b.x)), x1 = Math.min(vm.w, Math.max(a.x, b.x));
  const y0 = Math.max(0, Math.min(a.y, b.y)), y1 = Math.min(vm.h, Math.max(a.y, b.y));
  const w = x1 - x0, h = y1 - y0;
  if (w <= 0 || h <= 0) return null;
  const atBottom = y1 >= vm.h - EDGE_TOL, atTop = y0 <= EDGE_TOL, atLeft = x0 <= EDGE_TOL, atRight = x1 >= vm.w - EDGE_TOL;
  let tb = null;
  if (w >= vm.w * 0.6 && h <= vm.h * 0.5 && atBottom !== atTop) tb = atBottom ? { side: 'bottom', p: vm.h - y0 } : { side: 'top', p: y1 };
  else if (h >= vm.h * 0.6 && w <= vm.w * 0.5 && atLeft !== atRight) tb = atLeft ? { side: 'left', p: x1 } : { side: 'right', p: vm.w - x0 };
  return normalizeTb(tb);
}

/**
 * Zeiger im Streifen des Docks -> Punkt in der VM. pt: Pixel im Element des Streifens, box: dessen Groesse (CSS-Pixel, der Streifen kann kleiner
 * gezeichnet sein als im Bild). Ein Ziehen darf den Streifen verlassen (die Abbildung laeuft weiter); das Ergebnis bleibt im Bild.
 */
export function stripToVm(vm, tb, pt, box) {
  const s = stripRect(vm, tb);
  const x = s.x + (pt.x / box.w) * s.w, y = s.y + (pt.y / box.h) * s.h;
  return { x: Math.min(vm.w - 1, Math.max(0, Math.round(x))), y: Math.min(vm.h - 1, Math.max(0, Math.round(y))) };
}

/** Wie gross der Streifen im Dock gezeichnet wird: im Massstab des Fensterbildes, nur verkleinert, wenn er nicht auf den Schirm passt (avail = freie Laenge). */
export function stripBox(vm, tb, scale, avail) {
  const s = stripRect(vm, tb), len = horizontal(tb) ? s.w : s.h;
  const k = Math.min(1, avail / (len * scale || 1));
  return { w: Math.max(1, Math.round(s.w * scale * k)), h: Math.max(1, Math.round(s.h * scale * k)) };
}
