/* =====================================================================
 * Fenster einrasten, bewegen, Groesse aendern, naechstes Fenster: nur Rechnen, kein DOM (docs/MULTI-SCREEN.md, Schritt 1).
 * desktop-wm.js ruft das auf; hier steht, was sich ohne Browser pruefen laesst.
 * area = der nutzbare Bereich { x, y, w, h } (unter der Menueleiste, ueber dem Dock-Streifen), Rechtecke sind { x, y, w, h } in Pixeln des Desktops.
 * ===================================================================== */

/** Die Zonen: linke und rechte Haelfte, vier Viertel (Ecken) und das ganze Bild */
export const ZONES = ['left', 'right', 'tl', 'tr', 'bl', 'br', 'max'];

/** Das Rechteck einer Zone. Hälften und Viertel fuellen den Bereich ohne Luecke (bei ungerader Breite bekommt die rechte, bei ungerader Hoehe die untere Seite den Rest). */
export function zoneRect(zone, area) {
  const lw = Math.floor(area.w / 2), th = Math.floor(area.h / 2);
  const col = (right) => (right ? { x: area.x + lw, w: area.w - lw } : { x: area.x, w: lw });
  const row = (bottom) => (bottom ? { y: area.y + th, h: area.h - th } : { y: area.y, h: th });
  switch (zone) {
    case 'max': return { x: area.x, y: area.y, w: area.w, h: area.h };
    case 'left': case 'right': return { ...col(zone === 'right'), y: area.y, h: area.h };
    case 'tl': case 'tr': case 'bl': case 'br': return { ...col(zone[1] === 'r'), ...row(zone[0] === 'b') };
    default: return null;
  }
}

/**
 * Welche Zone gehoert zu einer Mausposition beim Ziehen? Am linken oder rechten Rand die Haelfte, in einer Ecke das Viertel, am oberen Rand (Mitte) das ganze Bild,
 * am unteren Rand nur die Ecken. Sonst null (kein Einrasten). edge = Breite des Randstreifens, corner = Kantenlaenge der Eckbereiche.
 * Die Position darf auch ausserhalb des Bereichs liegen (ueber der Menueleiste zaehlt als oberer Rand).
 */
export function zoneAt(p, area, { edge = 8, corner = 64 } = {}) {
  const right = area.x + area.w, bottom = area.y + area.h;
  let col = p.x <= area.x + edge ? 0 : p.x >= right - edge ? 1 : -1;
  let row = p.y <= area.y + edge ? 0 : p.y >= bottom - edge ? 1 : -1;
  if (col < 0 && row < 0) return null;
  // am senkrechten Rand entscheidet die Naehe zur Ecke ueber Haelfte oder Viertel, am waagerechten genauso
  if (row < 0) row = p.y - area.y < corner ? 0 : bottom - p.y <= corner ? 1 : -1;
  if (col < 0) col = p.x - area.x < corner ? 0 : right - p.x <= corner ? 1 : -1;
  if (col >= 0 && row >= 0) return ['tl', 'tr', 'bl', 'br'][row * 2 + col];
  if (col >= 0) return col === 0 ? 'left' : 'right';
  return row === 0 ? 'max' : null;
}

/**
 * Der naechste Zustand nach einer Pfeiltaste, wie bei Windows (Win+Pfeil). zone: null (frei), 'max' oder eine Zone; dir: 'left' | 'right' | 'up' | 'down'.
 * Ergebnis: eine Zone, 'restore' (zurueck auf die Groesse davor) oder 'minimize'. Links und rechts springen von Haelfte zu Haelfte; hoch macht aus der Haelfte ein oberes Viertel,
 * dann das ganze Bild; runter ein unteres Viertel, dann zurueck, ein freies Fenster wird minimiert.
 */
const STEP = {
  free: { left: 'left', right: 'right', up: 'max', down: 'minimize' },
  max: { left: 'left', right: 'right', up: 'max', down: 'restore' },
  left: { left: 'left', right: 'right', up: 'tl', down: 'bl' },
  right: { left: 'left', right: 'right', up: 'tr', down: 'br' },
  tl: { left: 'tl', right: 'tr', up: 'max', down: 'bl' },
  tr: { left: 'tl', right: 'tr', up: 'max', down: 'br' },
  bl: { left: 'bl', right: 'br', up: 'tl', down: 'restore' },
  br: { left: 'bl', right: 'br', up: 'tr', down: 'restore' },
};
export function snapStep(zone, dir) {
  const row = STEP[zone || 'free'];
  return row && row[dir] ? row[dir] : null;
}

const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

/**
 * Groesse mit der Tastatur: links und oben verkleinern, rechts und unten vergroessern (die obere linke Ecke bleibt). Nicht kleiner als min, nicht groesser als der Bereich;
 * stoesst das Fenster am Rand an, rutscht es zurueck, statt herauszuragen.
 */
export function resizeRect(r, dir, step, min, area) {
  let { x, y, w, h } = r;
  if (dir === 'left') w -= step; else if (dir === 'right') w += step; else if (dir === 'up') h -= step; else if (dir === 'down') h += step;
  w = clamp(w, min.w, Math.max(min.w, area.w)); h = clamp(h, min.h, Math.max(min.h, area.h));
  x = clamp(x, area.x, Math.max(area.x, area.x + area.w - w)); y = clamp(y, area.y, Math.max(area.y, area.y + area.h - h));
  return { x, y, w, h };
}

/**
 * Ort eines Fensters beim Ziehen und beim Verschieben mit der Tastatur: ein Stueck der Titelleiste (grab) bleibt immer sichtbar, oben endet es an der Leiste (barH), unten
 * bleibt ein Rest (bottom) im Bild.
 */
export function dragPosition(origin, delta, size, view, { barH = 0, grab = 120, bottom = 60 } = {}) {
  return { x: clamp(origin.x + delta.dx, grab - size.w, view.w - grab), y: clamp(origin.y + delta.dy, barH, view.h - bottom) };
}

/**
 * Ein eingerastetes oder maximiertes Fenster wird an der Titelleiste gezogen: es bekommt seine alte Groesse zurueck und der Griff bleibt an derselben Stelle seiner Breite
 * (wer in der Mitte greift, haelt es danach in der Mitte). cur = das Rechteck, in dem es gerade steht, pointerX = x der Maus.
 */
export function unsnapAt(restore, cur, pointerX) {
  const frac = cur.w > 0 ? clamp((pointerX - cur.x) / cur.w, 0, 1) : 0.5;
  return { x: Math.round(pointerX - frac * restore.w), y: cur.y, w: restore.w, h: restore.h };
}

/** Das naechste (dir = 1) oder vorige (-1) Fenster in der Reihenfolge ids; ohne aktives Fenster das erste bzw. letzte. null, wenn es keins gibt. */
export function nextWindow(ids, activeId, dir) {
  const n = ids.length;
  if (!n) return null;
  const i = ids.indexOf(activeId);
  if (i < 0) return dir > 0 ? ids[0] : ids[n - 1];
  return ids[(i + (dir > 0 ? 1 : -1) + n) % n];
}
