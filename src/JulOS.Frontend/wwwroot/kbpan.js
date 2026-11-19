/*
 * Bildschirmtastatur am Handy: was passiert mit dem Bild, wenn sich der sichtbare Bereich aendert? Zwei Modi (Einstellung "kbResize"):
 *   'pan'    (Standard) Die Aufloesung bleibt, wie sie ohne Tastatur war. Das Bild behaelt seine Groesse, die Flaeche davor wird nur kleiner;
 *            das Bild wird so verschoben, dass die Stelle mit dem Fokus (zuletzt angetippt) sichtbar bleibt, und ist mit zwei Fingern verschiebbar.
 *   'follow' Die Remote-Aufloesung folgt der Flaeche (nach kurzer Entprellung, siehe SizeGate in layoutlog.js).
 * Hier steht nur Rechnen ohne Browser (deploy/dev/test.sh startet die Tests mit node).
 */
import { t } from './i18n.js';

export const KB_MODES = ['pan', 'follow'];
export const KB_SETTLE_MS = 400;     // so lange muss sich die gewuenschte Groesse nicht mehr aendern, bevor sie an die VM geht

/** Gespeicherter Wert -> gueltiger Modus (alles Unbekannte ist der Standard). */
export const normalizeKbMode = (v) => (KB_MODES.includes(v) ? v : 'pan');
export const nextKbMode = (v) => KB_MODES[(KB_MODES.indexOf(normalizeKbMode(v)) + 1) % KB_MODES.length];
export const kbModeLabel = (v) => (normalizeKbMode(v) === 'pan' ? t('term.kbPan') : t('term.kbFollow'));

/**
 * Merkt sich, wie gross die Bildflaeche ohne Tastatur ist. Ohne Tastatur gilt immer die aktuelle Groesse; waehrend die Tastatur faehrt
 * (hold) oder offen ist, wird sie nicht kleiner (Zwischengroessen der Animation duerfen sie nicht verfaelschen). Neue Breite (Drehen) = neue Basis.
 */
export class StageBase {
  constructor() { this.w = 0; this.h = 0; }
  note(w, h, { kbOpen = false, holding = false } = {}) {
    if (!this.w || Math.abs(w - this.w) > 40) { this.w = w; this.h = h; return; }       // erste Messung oder gedreht
    if (kbOpen) return;
    this.w = w;
    this.h = holding ? Math.max(this.h, h) : h;
  }
  size() { return this.w ? { w: this.w, h: this.h } : null; }
}

/** Welche Flaeche liefert die Groesse fuer die VM? Im Modus 'pan' mit offener Tastatur die ohne Tastatur, sonst die gemessene. */
export function effectiveStage(mode, kbOpen, stage, base) {
  if (normalizeKbMode(mode) === 'pan' && kbOpen && base && Math.abs(base.w - stage.w) <= 40 && base.h > stage.h) return { w: stage.w, h: base.h };
  return stage;
}

/**
 * Verschiebung des Bildes nach oben (>= 0, in CSS-Pixeln). contentH = Hoehe des Bildes, viewH = sichtbare Hoehe, focusY = Stelle mit dem Fokus
 * im Bild (CSS-Pixel vom oberen Bildrand, null = unbekannt), current = bisherige Verschiebung. Bleibt die Stelle im sichtbaren Teil
 * (mit Rand), aendert sich nichts; sonst wird sie ins obere Drittel gerueckt.
 */
export function panOffset({ contentH, viewH, focusY, current = 0, margin = 48 }) {
  const max = Math.max(0, contentH - viewH);
  let off = Math.min(Math.max(current, 0), max);
  if (focusY === null || focusY === undefined || max === 0) return off;
  const m = Math.min(margin, viewH / 4);
  if (focusY < off + m || focusY > off + viewH - m) off = focusY - viewH / 3;
  return Math.min(Math.max(off, 0), max);
}

/** Zwei-Finger-Zug um dy (nach unten > 0): das Bild folgt dem Finger, was am Rand nicht mehr geht, bleibt als rest uebrig (geht ans Scrollen der VM). */
export function panDrag(offset, max, dy) {
  const next = Math.min(Math.max(offset - dy, 0), Math.max(0, max));
  return { offset: next, rest: dy - (offset - next) };
}
