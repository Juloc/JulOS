/* =====================================================================
 * Sitzungsart "terminal": das native SSH-Terminal (terminal.js) als Sitzung fuer den Verwalter (sessions.js, docs/SESSIONS.md).
 * Sie erweitert RemoteSession, damit Sondertasten-Leiste, Menue, Ueberlagerung und Tasten-Warteschlange dieselben bleiben; ersetzt wird nur,
 * wie verbunden, gezeichnet und gemessen wird. Die Leiste sendet X11-Keysyms an this.client; das ist hier die TerminalView, die sie in Bytes
 * fuer die Shell umsetzt.
 * ===================================================================== */
import { RemoteSession, softKeyboard } from './app.js';
import { Sessions } from './sessions.js';
import { TerminalView } from './terminal.js';
import { TerminalBar, prefillState } from './terminal-input.js';
import { programOf } from './terminal-programs.js';
import { t } from './i18n.js';

// Maus und echte Tastatur: xterm nimmt die Tasten selbst an. ?softkb in der Adresse erzwingt zum Testen die Handy-Tastatur (Eingabefeld der Seite).
const HW_KB = matchMedia('(pointer: fine) and (hover: hover)').matches && !location.search.includes('softkb');
// Verbindungsabbrueche, nach denen eine tmux-Sitzung von selbst wieder angehaengt wird: kein Code (Leitung weg) oder ein Verbindungsfehler (Server ausgelastet, Zeit, Ziel nicht erreichbar)
const RETRY_CODES = new Set([513, 514, 515, 520]);
const KEEP_SECONDS = 600;           // so lange wird nach einem Abriss versucht, die tmux-Sitzung wieder zu nehmen (das Ziel haelt sie ohnehin weiter)

export class TerminalSession extends RemoteSession {
  constructor(conn, opts = {}) {
    super(conn, opts);
    this.view = null; this.bar = null;
    this.barState = { draft: '', open: undefined };      // gehoert der Sitzung: ein Neuverbinden behaelt den Entwurf und ob die Leiste offen ist
    this.wantBarFocus = false;                            // nach einem prefill: den Fokus in die Eingabeleiste, sobald verbunden (Enter sendet dann)
    this.keep = false;                                    // laeuft die Sitzung in tmux (Antwort des Servers)? Dann haengt sich ein Neuverbinden wieder an
    this.element.classList.add('sv-terminal');
    this.engine = 'ssh';
  }

  /* ---------- Verbindung ---------- */
  async start() {
    this.intentional = false; this.fatal = false; this.connectStarted = Date.now();
    this.teardown();
    const reconnecting = this.everConnected;
    this.setState(reconnecting ? 'reconnecting' : 'connecting', reconnecting ? t('term.reconnecting') : t('term.connecting'));
    this.showBusy(reconnecting ? t('term.reconnecting') : t('term.connecting'));
    const view = new TerminalView({
      conn: this.conn, sid: this.id, password: this.password, container: this.el.stage,
      externalKeyboard: !HW_KB,
      onState: (state, info) => this.onViewState(view, state, info),
      onKeyUsed: () => this.consumeOnce(),
      onTap: () => { if (!HW_KB && softKeyboard.auto() && !softKeyboard.isOpen()) softKeyboard.open(); else if (HW_KB) view.focusInput({ force: true }); },
      onAuthExpired: () => this.requestClose(t('term.authExpired'), 'auth'),
    });
    this.view = view;
    this.client = view;                       // die Leiste ruft this.client.sendKeyEvent(gedrueckt, keysym)
    this.bar?.destroy();
    this.bar = new TerminalBar({ view, container: this.el.stage, program: programOf(this.conn), state: this.barState });   // Eingabeleiste und Schnelltasten (terminal-input.js)
    view.setActive(this.visible);
    await view.connect();
  }

  onViewState(view, state, info) {
    if (this.view !== view || this.disposed) return;
    if (state === 'connecting') return;
    if (state === 'connected') {
      this.connected = true; this.everConnected = true; this.connectedAt = Date.now();
      this.bar?.setProgram(info.program || programOf(this.conn));
      // Laeuft die Sitzung in tmux, bleibt die Arbeit auf dem Ziel erhalten: nach einem Abriss wird sie wieder angehaengt (Fristen: resume.js, wie beim Stream)
      this.keep = info.keep === 'tmux';
      if (this.keep) { this.resume.offered('tmux', KEEP_SECONDS); this.resume.confirm(); } else this.resume.reset();
      this.resume.connected();
      this.el.overlay.hidden = true; this.el.spinner.hidden = true;
      this.setState('connected', t('term.connected'));
      if (this.wantBarFocus) { this.wantBarFocus = false; if (Sessions.active === this) this.bar?.focusInput(); }
      else if (Sessions.active === this && HW_KB) view.focusInput();
      return;
    }
    this.connected = false;
    if (info.dialog) { this.el.overlay.hidden = true; this.setState('error', t('term.keyChangedState')); return; }   // die Warnung steht in der Ansicht selbst
    if (info.code === 777) { this.fatal = true; this.setState('taken', t('term.taken')); return this.showOverlay(info.message); }
    if (info.code === 797) { this.fatal = true; return this.requestClose(info.message); }                      // Obergrenze: Meldung des Servers
    if (state === 'error' && (info.fatal || !this.everConnected)) { this.fatal = !!info.fatal; return this.requestClose(info.message); }
    // tmux: die Arbeit lebt auf dem Ziel weiter, also wieder anhaengen (der Browser versucht es mit wachsenden Pausen, bis die Frist ablaeuft)
    const lost = state === 'closed' ? (!info.code || RETRY_CODES.has(info.code)) : state === 'error' && !info.fatal;
    if (this.keep && lost && this.scheduleKeepReconnect()) return;
    // Verbindung stand, ist aber weg: die Shell auf dem Ziel endet mit der Verbindung, darum kein stilles Neuverbinden (das ergaebe eine leere neue Shell)
    this.setState('disconnected', state === 'closed' && info.code ? t('term.ended') : t('term.disconnected'));
    this.showOverlay(info.message || t('term.connectionClosed'));
  }

  /** Eine getrennte tmux-Sitzung wieder anhaengen: true, wenn ein Versuch geplant ist (false: die Frist ist um, die Ueberlagerung zeigt es). */
  scheduleKeepReconnect() {
    this.resume.lost();
    if (!this.resume.usable() || this.intentional || this.disposed) return false;
    this.setState('reconnecting', t('term.reconnecting'));
    this.showBusy(t('term.reconnecting'));
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => { if (!this.intentional && !this.disposed) this.start(); }, this.resume.nextDelay());
    return true;
  }

  teardown() {
    const view = this.view;
    this.view = null;
    this.bar?.destroy(); this.bar = null;
    super.teardown();                         // trennt this.client (= die Ansicht), raeumt Zeitgeber und Umschalter auf
    view?.destroy();
  }

  /**
   * Zurueck aus dem Hintergrund (Handy gesperrt): eine bestehende Verbindung bleibt, eine getrennte zeigt ihre Ueberlagerung; kein Neustart von selbst.
   * Ausnahme tmux: die Arbeit lebt auf dem Ziel, also wird nach laengerer Pause oder bei getrennter Leitung wieder angehaengt (auch beim Signal "Netz wieder da").
   */
  onPageVisible(away) {
    if (!this.keep || this.intentional || this.fatal || this.disposed || !this.everConnected) return;
    if (this.view && !this.connected && Date.now() - this.connectStarted < 10000) return;      // es wird gerade verbunden
    if (!this.connected || away > 15000) { clearTimeout(this.retryTimer); this.start(); }
  }

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

  /**
   * Text in die Eingabeleiste legen, ohne ihn zu senden (docs/HOST-AGENT.md: der Installationsbefehl mit frischem Einrichtungscode; der Benutzer sieht ihn,
   * startet ihn mit Enter und tippt sein sudo-Passwort selbst im Terminal). Die Leiste entsteht erst beim Verbinden: der Entwurf liegt solange im Zustand der Sitzung.
   */
  prefill(text) {
    prefillState(this.barState, text);
    this.bar?.setDraft(text);
    if (this.connected) this.bar?.focusInput(); else this.wantBarFocus = true;       // schon verbunden: gleich; sonst sobald die Verbindung steht
  }

  /* ---------- Einfuegen: Text geht als Einfuegen ins Terminal (mit Klammer-Modus, wenn die Shell ihn kann) ---------- */
  pasteToRemote(text) { this.view?.paste(text); }
  pasteText(text) { this.view?.paste(text); }
}

// Fuer 'terminal' gilt nur noch diese Sitzungsart (die Fernsteuerung in app.js kennt nur Desktops)
Sessions.registerFactory('terminal', (conn, o) => new TerminalSession(conn, o));
