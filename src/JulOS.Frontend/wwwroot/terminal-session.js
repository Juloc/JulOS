/* =====================================================================
 * Sitzungsart "terminal": das native SSH-Terminal (terminal.js) als Sitzung fuer den Verwalter (sessions.js, docs/SESSIONS.md).
 * Sie erweitert RemoteSession, damit Sondertasten-Leiste, Menue, Ueberlagerung und Tasten-Warteschlange dieselben bleiben; ersetzt wird nur,
 * wie verbunden, gezeichnet und gemessen wird. Die Leiste sendet X11-Keysyms an this.client; das ist hier die TerminalView, die sie in Bytes
 * fuer die Shell umsetzt.
 * ===================================================================== */
import { RemoteSession, softKeyboard } from './app.js';
import { Sessions } from './sessions.js';
import { TerminalView } from './terminal.js';

// Maus und echte Tastatur: xterm nimmt die Tasten selbst an. ?softkb in der Adresse erzwingt zum Testen die Handy-Tastatur (Eingabefeld der Seite).
const HW_KB = matchMedia('(pointer: fine) and (hover: hover)').matches && !location.search.includes('softkb');

export class TerminalSession extends RemoteSession {
  constructor(conn, opts = {}) {
    super(conn, opts);
    this.view = null;
    this.element.classList.add('sv-terminal');
    this.engine = 'ssh';
  }

  /* ---------- Verbindung ---------- */
  async start() {
    this.intentional = false; this.fatal = false; this.connectStarted = Date.now();
    this.teardown();
    const reconnecting = this.everConnected;
    this.setState(reconnecting ? 'reconnecting' : 'connecting', reconnecting ? 'Verbinde neu …' : 'Verbinde …');
    this.showBusy(reconnecting ? 'Verbinde neu …' : 'Verbinde …');
    const view = new TerminalView({
      conn: this.conn, sid: this.id, password: this.password, container: this.el.stage,
      externalKeyboard: !HW_KB,
      onState: (state, info) => this.onViewState(view, state, info),
      onKeyUsed: () => this.consumeOnce(),
      onTap: () => { if (!HW_KB && softKeyboard.auto() && !softKeyboard.isOpen()) softKeyboard.open(); else if (HW_KB) view.focusInput(); },
      onAuthExpired: () => this.requestClose('Die Sitzung ist abgelaufen. Bitte neu anmelden.', 'auth'),
    });
    this.view = view;
    this.client = view;                       // die Leiste ruft this.client.sendKeyEvent(gedrueckt, keysym)
    view.setActive(this.visible);
    await view.connect();
  }

  onViewState(view, state, info) {
    if (this.view !== view || this.disposed) return;
    if (state === 'connecting') return;
    if (state === 'connected') {
      this.connected = true; this.everConnected = true; this.connectedAt = Date.now();
      this.el.overlay.hidden = true; this.el.spinner.hidden = true;
      this.setState('connected', 'Verbunden');
      if (Sessions.active === this && HW_KB) view.focusInput();
      return;
    }
    this.connected = false;
    if (info.dialog) { this.el.overlay.hidden = true; this.setState('error', 'Schlüssel geändert'); return; }   // die Warnung steht in der Ansicht selbst
    if (info.code === 777) { this.fatal = true; this.setState('taken', 'Übernommen'); return this.showOverlay(info.message); }
    if (info.code === 797) { this.fatal = true; return this.requestClose(info.message); }                      // Obergrenze: Meldung des Servers
    if (state === 'error' && (info.fatal || !this.everConnected)) { this.fatal = !!info.fatal; return this.requestClose(info.message); }
    // Verbindung stand, ist aber weg: die Shell auf dem Ziel endet mit der Verbindung, darum kein stilles Neuverbinden (das ergaebe eine leere neue Shell)
    this.setState('disconnected', state === 'closed' && info.code ? 'Beendet' : 'Getrennt');
    this.showOverlay(info.message || 'Die Verbindung wurde getrennt.');
  }

  teardown() {
    const view = this.view;
    this.view = null;
    super.teardown();                         // trennt this.client (= die Ansicht), raeumt Zeitgeber und Umschalter auf
    view?.destroy();
  }

  /** Zurueck aus dem Hintergrund (Handy gesperrt): eine bestehende Verbindung bleibt, eine getrennte zeigt ihre Ueberlagerung; kein Neustart von selbst. */
  onPageVisible() {}

  /* ---------- Groesse, Zoom, Sichtbarkeit: Zeichen statt Pixel, die Ansicht misst selbst ---------- */
  resize() { this.view?.fitSoon(); }
  onStageResize() { this.view?.fitSoon(); }
  scheduleSize() { this.view?.fitSoon(); }
  pushSize() {}
  fitDisplay() {}
  /** Zoom der Leiste: Schriftgroesse (ein kleineres k heisst bei der Fernsteuerung "Inhalt groesser"). */
  setK(k) { this.view?.zoom(k < this.k ? 1 : -1); }
  rememberProfile() {}
  setVisible(on) { super.setVisible(on); this.view?.setActive(!!on); }
  focus() { if (HW_KB) this.view?.focusInput(); }
  blur() { this.view?.blur(); super.blur(); }

  /* ---------- Einfuegen: Text geht als Einfuegen ins Terminal (mit Klammer-Modus, wenn die Shell ihn kann) ---------- */
  pasteToRemote(text) { this.view?.paste(text); }
  pasteText(text) { this.view?.paste(text); }
}

// Fuer 'terminal' gilt nur noch diese Sitzungsart (die Fernsteuerung in app.js kennt nur Desktops)
Sessions.registerFactory('terminal', (conn, o) => new TerminalSession(conn, o));
