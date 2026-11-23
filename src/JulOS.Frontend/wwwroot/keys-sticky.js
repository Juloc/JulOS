/* =====================================================================
 * Haltetasten (Strg, Alt, Umschalt, Windows, AltGr) in der Tastenleiste: reine Zustandslogik, die Taste selbst druecken und loslassen die Rueckrufe (app.js).
 *   einmal antippen              -> 'once': gilt fuer die naechste Taste oder den naechsten Klick (zum Beispiel Strg+Klick), danach wieder aus
 *   zweimal schnell antippen     -> 'lock': bleibt gedrueckt, bis man erneut tippt
 *   spaeter noch einmal antippen -> aus (aus 'once' ist das "doch nicht")
 * Die Taste ist im Ziel wirklich gedrueckt, solange der Zustand nicht 'off' ist; so wirkt sie auch auf Mausklicks. Damit im Ziel nie eine Taste haengen bleibt, loest releaseAll()
 * alle (auch beim Verlassen der Sitzung, beim Wechsel in den Hintergrund, beim Abriss und nach dem Wiederverbinden mit force: dann gehen die Loslass-Ereignisse
 * an alle fuenf Tasten, auch wenn wir meinen, sie seien nicht gedrueckt). Beschreibung: docs/TOUCH.md.
 * ===================================================================== */
import { MODSYM } from './keys-shortcuts.js';

export const MODS = ['ctrl', 'alt', 'shift', 'super', 'altgr'];
export const DOUBLE_TAP_MS = 450;     // so schnell muss der zweite Tipp kommen, damit die Taste einrastet

export class StickyMods {
  /**
   * @param {{down: (sym:number)=>void, up: (sym:number)=>void, changed?: ()=>void}} io  Tasten druecken und loslassen; changed meldet jede Aenderung des Zustands (zum Neuzeichnen der Tasten)
   */
  constructor({ down, up, changed = () => {} }) {
    this.io = { down, up, changed };
    this.state = Object.fromEntries(MODS.map((m) => [m, 'off']));
    this.at = Object.fromEntries(MODS.map((m) => [m, -Infinity]));      // Zeit des letzten Tipps
  }

  get(name) { return this.state[name] || 'off'; }
  /** Namen der Tasten, die im Ziel gerade gedrueckt sind. */
  held() { return MODS.filter((m) => this.state[m] !== 'off'); }
  any() { return this.held().length > 0; }

  /** Ein Tipp auf die Taste `name` zum Zeitpunkt `now` (ms). Gibt den neuen Zustand zurueck. */
  tap(name, now) {
    if (!MODS.includes(name)) return 'off';
    const was = this.state[name];
    const next = was === 'off' ? 'once' : was === 'once' ? (now - this.at[name] <= DOUBLE_TAP_MS ? 'lock' : 'off') : 'off';
    this.at[name] = now;
    this.set(name, next);
    return next;
  }

  /** Zustand setzen und die Taste im Ziel dazu druecken oder loslassen. */
  set(name, next) {
    const was = this.state[name];
    if (was === next) return;
    if (was === 'off') this.io.down(MODSYM[name]);
    if (next === 'off') this.io.up(MODSYM[name]);
    this.state[name] = next;
    this.io.changed();
  }

  /** Nach einer Taste oder einem Klick: was nur fuer das eine Mal galt, loslassen (eingerastete bleiben). */
  consume() { for (const m of MODS) if (this.state[m] === 'once') this.set(m, 'off'); }

  /**
   * Alles loslassen. Ohne force nur, was als gedrueckt gilt; mit force an alle fuenf Tasten ein Loslassen (nach einem Abriss wissen wir nicht, was das Ziel noch haelt).
   * Reihenfolge: umgekehrt zum Druecken der Liste (AltGr, Windows, Umschalt, Alt, Strg), damit Tastenkombinationen sauber enden.
   */
  releaseAll(force = false) {
    const list = force ? [...MODS].reverse() : this.held().reverse();
    for (const m of list) this.io.up(MODSYM[m]);
    const changed = this.any();
    for (const m of MODS) this.state[m] = 'off';
    if (changed || force) this.io.changed();
  }
}
