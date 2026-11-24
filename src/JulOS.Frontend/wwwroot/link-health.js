/* =====================================================================
 * Lebenszeichen der Leitung zum Server, reine Logik ohne DOM und ohne Zeitgeber (getestet in tests/frontend/link-health.test.mjs).
 * Stream (stream.js) und Terminal (terminal.js) fuettern sie mit Ereignissen und fragen sie im Sekundentakt; die Zeit kommt von aussen
 * (Date.now: sie laeuft auch ueber das Einfrieren einer Seite im Hintergrund hinweg, gezaehlte Takte taeten das nicht).
 *
 * Warum: reisst eine Leitung still (Router, Mobilfunk-NAT), merkt der Browser es erst beim naechsten Schreiben, oft nach Minuten. Darum
 * schickt er ein winziges Ping, wenn eine Weile nichts floss, und erwartet binnen einer Frist irgendeine Antwort. Bleibt sie aus, gilt die
 * Leitung als tot und wird aktiv geschlossen. Der Server muss das Lebenszeichen koennen: er sagt es beim Verbinden (enable); ein alter
 * Server tut das nie, dann bleibt alles beim Alten (nichts wird gesendet, nichts als tot erklaert).
 * ===================================================================== */

export const PING_EVERY_MS = 5000;     // so lange darf nichts fliessen (in keiner Richtung), bevor ein Ping geht
export const DEAD_AFTER_MS = 10000;    // so lange darf auf ein Ping gar nichts kommen
export const PROBE_MS = 1200;          // Frist nach dem Zurueckkommen aus dem Hintergrund
export const FROZEN_GAP_MS = 12000;    // ein Takt, der so spaet kommt: die Seite war eingefroren (Zeitgeber standen)
export const SHORT_AWAY_MS = 1000;     // kuerzer im Hintergrund: kein Probe-Ping noetig
export const LEGACY_AWAY_MS = 4000;    // ohne Lebenszeichen des Servers: wie bisher nach so langer Pause neu verbinden
export const QUIET_MS = 2500;          // so lange darf ein Wiederaufnehmen dauern, ohne dass eine Ueberlagerung erscheint

export class LinkHealth {
  constructor(now = () => Date.now()) {
    this.now = now;
    this.reset();
  }

  reset() {
    this.enabled = false;
    const t = this.now();
    this.lastRx = t; this.lastTx = t; this.lastTick = t;
    this.pending = 0;              // Zeit des offenen Pings (0: keins offen)
    this.deadline = 0;
    this.sentPings = 0;
  }

  /** Der Server hat gesagt, dass er das Lebenszeichen kann. */
  enable() { this.enabled = true; this.touchRx(); }

  /** Irgendetwas kam vom Server: die Leitung lebt. */
  touchRx() { this.lastRx = this.now(); this.pending = 0; this.deadline = 0; }

  /** Wir haben etwas gesendet. */
  touchTx() { this.lastTx = this.now(); }

  /**
   * Im Sekundentakt aufrufen. Gibt 'ping' (jetzt ein Ping senden), 'dead' (Leitung schliessen) oder null zurueck.
   * Kam der Takt viel zu spaet (Seite eingefroren), kann die Leitung nichts mehr wert sein: sofort mit kurzer Frist pruefen.
   */
  tick() {
    const t = this.now();
    const gap = t - this.lastTick;
    this.lastTick = t;
    if (!this.enabled) return null;
    if (t < this.lastRx) { this.lastRx = t; this.lastTx = Math.min(this.lastTx, t); }        // die Uhr ging zurueck
    if (gap > FROZEN_GAP_MS) {
      // Die Seite stand still: die Antwort auf ein offenes Ping liegt vielleicht nur noch nicht verarbeitet bereit. Frische Frist, nicht gleich "tot".
      if (this.pending) { this.deadline = t + PROBE_MS; return null; }
      return this.arm(PROBE_MS);
    }
    if (this.pending) { if (t < this.deadline) return null; this.enabled = false; return 'dead'; }   // nur einmal melden: der Aufrufer schliesst die Leitung
    if (t - this.lastRx >= PING_EVERY_MS || t - this.lastTx >= PING_EVERY_MS) return this.arm(DEAD_AFTER_MS);
    return null;
  }

  arm(wait) {
    const t = this.now();
    this.pending = t; this.deadline = t + wait; this.sentPings++;
    this.lastTx = t;
    return 'ping';
  }

  /**
   * Die Seite ist wieder sichtbar nach awayMs im Hintergrund.
   *  'ping'      ein Probe-Ping senden: kommt binnen PROBE_MS etwas, bleibt die Leitung (tick() sagt sonst 'dead')
   *  'reconnect' (nur ohne Lebenszeichen des Servers) wie bisher neu verbinden
   *  null        nichts tun
   */
  visible(awayMs) {
    if (!this.enabled) return awayMs > LEGACY_AWAY_MS ? 'reconnect' : null;
    if (awayMs < SHORT_AWAY_MS) return null;
    const t = this.now();
    if (this.pending) { this.deadline = Math.min(this.deadline, t + PROBE_MS); return null; }    // ein Ping ist unterwegs: nur die Frist kuerzen
    this.arm(PROBE_MS);
    return 'ping';
  }
}

/**
 * Zaehler und Grund der Neuverbindungen einer Sitzung (Statusleiste) und die Entscheidung "still oder laut":
 * Wird die Sitzung binnen QUIET_MS wieder aufgenommen, bleibt das Bild stehen und nur ein kleines Zeichen zeigt es; erst danach kommt die Ueberlagerung.
 */
export class LinkStats {
  constructor(now = () => Date.now(), quietMs = QUIET_MS) {
    this.now = now; this.quietMs = quietMs;
    this.count = 0; this.lastReason = ''; this.lastAt = 0; this.lostAt = 0; this.lastQuiet = true; this.lastMs = 0;
  }

  /** Die Leitung ist weg (reason: 'silence' | 'closed' | 'dropped' | 'hidden' | 'probe' | ...). Mehrere Meldungen derselben Unterbrechung zaehlen einmal. */
  lost(reason) {
    if (this.lostAt) return false;
    this.lostAt = this.now(); this.count++; this.lastReason = reason; this.lastAt = this.lostAt;
    return true;
  }

  /** Die Sitzung laeuft wieder. Rueckgabe: wie lange es dauerte (ms), 0 wenn nichts offen war. */
  restored() {
    if (!this.lostAt) return 0;
    const ms = this.now() - this.lostAt;
    this.lostAt = 0; this.lastMs = ms; this.lastQuiet = ms < this.quietMs;
    return ms;
  }

  /** Ob gerade neu verbunden wird. */
  get busy() { return this.lostAt !== 0; }

  /** Soll jetzt die Ueberlagerung stehen? (Es dauert schon laenger als die stille Frist.) */
  loud() { return this.lostAt !== 0 && this.now() - this.lostAt >= this.quietMs; }

  /** Wie lange noch, bis aus "still" "laut" wird (ms, 0 = jetzt). */
  untilLoud() { return this.lostAt ? Math.max(0, this.quietMs - (this.now() - this.lostAt)) : this.quietMs; }
}

/** Kurzer Text fuer die Statusleiste: "3 · Netz still" (Zahl der Neuverbindungen und der letzte Grund), leer solange es keine gab. */
export function linkSummary(stats, reasonText) {
  if (!stats.count) return '';
  return stats.count + ' · ' + (reasonText(stats.lastReason) || stats.lastReason);
}
