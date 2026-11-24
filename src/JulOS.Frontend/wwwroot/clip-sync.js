/* =====================================================================
 * Zwischenablage, zwei kleine Entscheidungen ohne Oberflaeche (Tests: tests/frontend/clip-sync.test.mjs):
 *  - PasteGate: wann nach "Text in die VM legen" die Einfuegen-Taste gedrueckt wird (erst wenn die VM die neue Format-Liste angenommen hat);
 *  - pendingStep: wann ein Text oder Bild aus der VM, das der Browser nicht gleich ablegen durfte, noch einmal geschrieben wird.
 * ===================================================================== */

/** Arten der Nachricht 15 des Servers, die hier zaehlen (siehe ClipNote in ClipboardBridge.cs). */
export const NOTE_REFUSED = 7, NOTE_ACCEPTED = 8;

/** Ohne Meldung des Servers (VNC, alter Server) bleibt es bei der festen Pause; mit Meldung wartet das Einfuegen hoechstens so lange, bevor es trotzdem geschieht. */
export const PASTE_DELAY_MS = 180, PASTE_TIMEOUT_MS = 600;

export class PasteGate {
  constructor({ setTimer = (f, ms) => setTimeout(f, ms), clearTimer = (h) => clearTimeout(h) } = {}) {
    this.setTimer = setTimer; this.clearTimer = clearTimer;
    this.acks = false;           // der Server dieser Verbindung meldet "Liste angenommen" (Bit 2 der Nachricht 15)
    this.waiters = new Set();
  }
  /** Eine neue Verbindung: ob der Server meldet, steht erst in ihrer ersten Nachricht 15. */
  connected() { this.acks = false; this.settleAll('timer'); }
  /** Nachricht 15: { acks, note }. */
  note(n) {
    if (n.acks) this.acks = true;
    if (n.note === NOTE_ACCEPTED) this.settleAll('accepted');
    else if (n.note === NOTE_REFUSED) this.settleAll('refused');
  }
  /** Nach dem Senden des Textes aufrufen. Ergebnis: 'accepted', 'refused', 'timeout' (Meldung blieb aus) oder 'timer' (der Server meldet nicht, feste Pause). */
  wait() {
    return new Promise((resolve) => {
      const w = { resolve, handle: null };
      const fallback = this.acks ? 'timeout' : 'timer';
      w.handle = this.setTimer(() => { this.waiters.delete(w); resolve(fallback); }, this.acks ? PASTE_TIMEOUT_MS : PASTE_DELAY_MS);
      this.waiters.add(w);
    });
  }
  settleAll(result) {
    for (const w of this.waiters) { this.clearTimer(w.handle); w.resolve(result); }
    this.waiters.clear();
  }
}

/** So lange wartet ein Inhalt aus der VM darauf, dass der Browser ihn schreiben darf. */
export const PENDING_MAX_MS = 60000;

/**
 * Was mit dem wartenden Inhalt { kind: 'text' | 'image', data, at } geschieht. Ereignisse:
 *  'gesture' (Fokus, Zeigerdruck, Tastendruck in der Sitzung): schreiben, wenn er noch frisch ist; er bleibt, bis das Schreiben gelungen ist;
 *  'copy' (der Mensch hat hier selbst kopiert oder ausgeschnitten), 'newer' (die VM hat etwas Neueres kopiert), 'end' (Sitzung zu): verwerfen.
 * Rueckgabe { pending, write }: der neue Stand und was jetzt zu schreiben ist (oder null).
 */
export function pendingStep(pending, event, now) {
  if (!pending) return { pending: null, write: null };
  if (event !== 'gesture') return { pending: null, write: null };
  if (now - pending.at >= PENDING_MAX_MS) return { pending: null, write: null };
  return { pending, write: pending };
}
