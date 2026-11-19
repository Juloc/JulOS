/* =====================================================================
 * Sitzungserhalt, Seite des Browsers: die Entscheidung, ob nach einem Abriss der Leitung die erhaltene Sitzung des Servers wieder
 * aufgenommen werden kann (Token aus Nachricht 11 des Streams), und wie lange und wie oft das versucht wird. Reine Logik ohne DOM
 * (getestet in tests/frontend/resume.test.mjs); app.js benutzt es in RemoteSession.
 *
 * Der Server haelt die RDP-Sitzung "keep" Sekunden ohne Browser (JULOS_STREAM_KEEP_SECONDS) und nimmt sie mit dem Token wieder an.
 * Ein Token gilt nur, wenn schon einmal ein Bild gezeichnet wurde: davor kann es sein, dass die Anmeldung am Ziel scheiterte, und dann
 * waere "wieder aufnehmen" eine Schleife aus Versuchen mit falschem Passwort.
 * ===================================================================== */

const MARGIN_MS = 5000;     // der Server zaehlt ab seiner Beobachtung des Abrisses, die spaeter ist als unsere: etwas frueher aufgeben

export class Resume {
  constructor(now = () => Date.now()) { this.now = now; this.reset(); }

  reset() { this.token = null; this.keepMs = 0; this.lostAt = 0; this.confirmed = false; this.tries = 0; }

  /** Der Server hat ein Token genannt (Nachricht 11); keepSeconds 0 = er erhaelt nichts. */
  offered(token, keepSeconds) {
    if (!token || !(keepSeconds > 0)) { this.token = null; this.keepMs = 0; return; }
    this.token = String(token); this.keepMs = keepSeconds * 1000;
  }

  /** Ein Bild wurde gezeichnet: die Sitzung am Ziel gibt es wirklich. */
  confirm() { this.confirmed = true; }

  /** Die Verbindung steht (wieder). */
  connected() { this.lostAt = 0; this.tries = 0; }

  /** Die Leitung ist weg; die Zeit zaehlt ab dem ersten Mal. */
  lost() { if (!this.lostAt) this.lostAt = this.now(); }

  /** Der Server kennt das Token nicht (mehr): neu aufbauen. */
  refused() { this.reset(); }

  /** Darf jetzt versucht werden, die erhaltene Sitzung zu nehmen? */
  usable() {
    if (!this.token || !this.confirmed) return false;
    return !this.lostAt || this.now() - this.lostAt < Math.max(0, this.keepMs - MARGIN_MS);
  }

  /** Wie lange bis zum naechsten Versuch (ms): 1 s, 1,5 s, 2 s ... bis 3 s. */
  nextDelay() { return Math.min(1000 + 500 * this.tries++, 3000); }
}
