/* =====================================================================
 * Text aus der Bildschirmtastatur des Handys (Tippen, Autokorrektur, Diktieren mit der Spracheingabe der Tastatur): reine Logik ohne DOM
 * (mit node pruefbar: tests/frontend/kb-input.test.mjs). Das unsichtbare Eingabefeld haelt den Text; jede Aenderung (auch ein ganzer Satz auf einmal,
 * ein ersetztes Wort oder ein Zwischenstand der Spracherkennung) wird mit dem vorigen Stand verglichen und als "so viele Rueckschritte, dann dieser Text"
 * an die VM gegeben. Beschreibung: docs/TOUCH.md (Diktieren).
 * ===================================================================== */

/** Das feste Leerzeichen, das ein bearbeitbares Element statt eines Leerzeichens liefern kann, wird ein normales; Zeilenenden werden LF; Nullbreiten-Zeichen fallen weg. */
export const cleanKbText = (s) => String(s ?? '').replace(/ /g, ' ').replace(/\r\n?/g, '\n').replace(/[​-‍⁠﻿]/g, '');

/** So viele Zeichen darf das Feld wachsen, bevor es geleert wird (dann wieder von vorn). */
export const RESET_AT = 300;

const segmenter = typeof Intl !== 'undefined' && Intl.Segmenter ? new Intl.Segmenter(undefined, { granularity: 'grapheme' }) : null;
/** Zahl der Zeichen, wie der Anwender sie zaehlt (ein Emoji mit mehreren Teilen ist eines): so viele Rueckschritte loeschen es im Ziel. */
export const graphemes = (s) => (segmenter ? [...segmenter.segment(s)].length : [...s].length);

/** Vergleich zweier Staende: gemeinsamer Anfang (nach Zeichen, nicht nach UTF-16-Einheiten), der Rest vorn muss weg, der Rest hinten kommt dazu. */
export function diffText(prev, next) {
  const a = [...prev], b = [...next];
  let i = 0;
  while (i < a.length && i < b.length && a[i] === b[i]) i++;
  return { back: graphemes(a.slice(i).join('')), add: b.slice(i).join('') };
}

/**
 * Der Abgleich des Eingabefelds. feed(wert, { composing }) gibt { back, add, reset } zurueck: back Rueckschritte, dann add tippen; reset: das Feld darf geleert werden
 * (zu lang geworden; nie waehrend die Tastatur noch einen Zwischenstand bearbeitet, sonst setzt sie ihn nach dem Leeren wieder ein und alles steht doppelt).
 */
export class KbSync {
  constructor() { this.prev = ''; }
  feed(value, { composing = false } = {}) {
    const next = cleanKbText(value);
    const d = diffText(this.prev, next);
    this.prev = next;
    const reset = !composing && [...next].length > RESET_AT;
    if (reset) this.prev = '';
    return { ...d, reset };
  }
  /** Das Feld wurde von aussen geleert (Sitzungswechsel, Enter, Fokus weg). */
  clear() { this.prev = ''; }
}
