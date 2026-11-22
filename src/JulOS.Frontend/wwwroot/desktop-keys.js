/* =====================================================================
 * Tastenbelegung des Fenstermanagers (docs/MULTI-SCREEN.md, Schritt 1): nur Daten und Vergleiche, kein DOM.
 * Die Windows-Taste gehoert dem Betriebssystem, deshalb ist der Ersatz-Modifikator Strg+Alt. Die Belegung ist eine Einstellung: DEFAULT_BINDINGS ist die Vorgabe,
 * eine eigene Belegung ueberschreibt einzelne Eintraege (resolveBindings; gespeichert wird sie in desktop-wm.js, ein Einstellungsdialog fehlt noch).
 * Ein Eintrag: { id, code (KeyboardEvent.code, unabhaengig von Layout und Umschalt), mods: ['ctrl'|'alt'|'shift'|'meta'], global }.
 * global: gilt auch, solange ein Fernfenster alle Tasten bekommt (ausser im Vollbild). Das sind nur die Tasten zum Wechseln des Fensters, sonst waere man im Fernfenster
 * mit der Tastatur gefangen.
 * ===================================================================== */

export const MODIFIERS = ['ctrl', 'alt', 'shift', 'meta'];

const CA = ['ctrl', 'alt'], CAS = ['ctrl', 'alt', 'shift'];
export const DEFAULT_BINDINGS = Object.freeze([
  // Haelften, Ecken, maximieren, wiederherstellen und minimieren, wie Win+Pfeil
  { id: 'snap.left', code: 'ArrowLeft', mods: CA }, { id: 'snap.right', code: 'ArrowRight', mods: CA },
  { id: 'snap.up', code: 'ArrowUp', mods: CA }, { id: 'snap.down', code: 'ArrowDown', mods: CA },
  // Groesse: links und oben verkleinern, rechts und unten vergroessern
  { id: 'size.left', code: 'ArrowLeft', mods: CAS }, { id: 'size.right', code: 'ArrowRight', mods: CAS },
  { id: 'size.up', code: 'ArrowUp', mods: CAS }, { id: 'size.down', code: 'ArrowDown', mods: CAS },
  // Verschieben: ein Modus (Pfeile bewegen, Enter oder Esc beendet), wie Alt+Leertaste, V bei Windows
  { id: 'move', code: 'KeyM', mods: CA },
  // naechstes und voriges Fenster (Alt+Tab gehoert dem Betriebssystem)
  { id: 'next', code: 'PageDown', mods: CA, global: true }, { id: 'prev', code: 'PageUp', mods: CA, global: true },
].map((b) => Object.freeze({ global: false, ...b, mods: Object.freeze([...b.mods]) })));

const sameMods = (a, b) => a.length === b.length && a.every((m) => b.includes(m));
const validOverride = (o) => o && typeof o.code === 'string' && /^[A-Za-z0-9]{1,16}$/.test(o.code) && Array.isArray(o.mods) && o.mods.every((m) => MODIFIERS.includes(m));

/**
 * Die gueltige Belegung: die Vorgabe mit den Aenderungen aus overrides ({ id: { code, mods } }; null schaltet einen Eintrag ab). Ungueltiges wird ueberhoert; zwei Eintraege mit
 * derselben Taste behalten nur den ersten (die Reihenfolge der Vorgabe), damit eine Taste nie zwei Dinge tut.
 */
export function resolveBindings(overrides = {}) {
  const out = [];
  for (const d of DEFAULT_BINDINGS) {
    const o = overrides ? overrides[d.id] : undefined;
    if (o === null) continue;
    const b = validOverride(o) ? { ...d, code: o.code, mods: [...new Set(o.mods)] } : d;
    if (out.some((x) => x.code === b.code && sameMods(x.mods, b.mods))) continue;
    out.push(b);
  }
  return out;
}

/** Welcher Eintrag passt zu dieser Taste? ev: { code, ctrlKey, altKey, shiftKey, metaKey, altGraph }. Die Modifikatoren muessen genau stimmen. AltGr zaehlt nie (damit tippt man Zeichen). */
export function matchBinding(list, ev) {
  if (!ev || !ev.code || ev.altGraph) return null;
  const mods = MODIFIERS.filter((m) => ev[m + 'Key']);
  return list.find((b) => b.code === ev.code && sameMods(b.mods, mods)) || null;
}

const PRINTABLE = /^(Key|Digit|Numpad|Minus|Equal|Bracket|Semicolon|Quote|Backquote|Backslash|Comma|Period|Slash|Space|IntlBackslash)/;
/**
 * Darf die Taste auch greifen, waehrend ein Textfeld den Fokus hat? Nur wenn sie dort nichts tut: keine Taste, die ein Zeichen schreibt (Strg+Alt ist AltGr),
 * und mindestens zwei von Strg, Alt, Meta (Alt+Pfeil allein springt im Feld ueber Woerter). Sonst gehoert die Taste dem Feld.
 */
export function firesInTextField(b) {
  return !PRINTABLE.test(b.code) && b.mods.filter((m) => m !== 'shift').length >= 2;
}

/**
 * Tasten im Verschiebe-Modus: Pfeile bewegen ({ dx, dy } in Schritten, mit Umschalt vier mal so gross), Enter und Esc beenden ('end'), alles andere null (das beendet den Modus
 * und wirkt dann wie sonst). Mit Strg, Alt oder Meta ist es keine Bewegung.
 */
export function moveKey(ev) {
  if (!ev) return null;
  if (ev.code === 'Enter' || ev.code === 'NumpadEnter' || ev.code === 'Escape') return 'end';
  if (ev.ctrlKey || ev.altKey || ev.metaKey) return null;
  const k = ev.shiftKey ? 4 : 1;
  switch (ev.code) {
    case 'ArrowLeft': return { dx: -k, dy: 0 };
    case 'ArrowRight': return { dx: k, dy: 0 };
    case 'ArrowUp': return { dx: 0, dy: -k };
    case 'ArrowDown': return { dx: 0, dy: k };
    default: return null;
  }
}

const KEY_NAMES = { ArrowLeft: '←', ArrowRight: '→', ArrowUp: '↑', ArrowDown: '↓', PageDown: 'PgDn', PageUp: 'PgUp' };
/** Text fuer Menues: "Strg+Alt+←". names bringt die Namen der Modifikatoren in der Sprache der Oberflaeche ({ ctrl, alt, shift, meta }). */
export function bindingLabel(b, names = {}) {
  const key = KEY_NAMES[b.code] || b.code.replace(/^(Key|Digit)/, '');
  return [...MODIFIERS.filter((m) => b.mods.includes(m)).map((m) => names[m] || m), key].join('+');
}
