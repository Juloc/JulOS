/* =====================================================================
 * Sitzungen: der gemeinsame Vertrag (Session) und der Verwalter (Sessions).
 * Genaue Beschreibung: docs/SESSIONS.md. Dieser Teil kennt kein DOM ausser dem Wurzelelement, das eine Sitzung selbst mitbringt;
 * die Huellen (Handy: Umschalter, Desktop: Fenster) haengen es ein und hoeren auf die Ereignisse des Verwalters.
 * ===================================================================== */

import { t } from './i18n.js';

/** Zufaellige Sitzungskennung (8 bis 40 Zeichen aus A-Z a-z 0-9 - _), die der Server als Teil des Slots nimmt. */
export function newSid() {
  const bytes = new Uint8Array(12);
  (globalThis.crypto || { getRandomValues: (a) => a.map(() => Math.floor(Math.random() * 256)) }).getRandomValues(bytes);
  return [...bytes].map((b) => b.toString(36).padStart(2, '0')).join('').slice(0, 20);
}

export const STATES = ['connecting', 'connected', 'reconnecting', 'disconnected', 'error', 'taken'];

/**
 * Eine geoeffnete Ansicht auf ein Ziel. Sitzungsarten erweitern diese Klasse (Fernsteuerung in app.js, spaeter das native Terminal).
 * Ereignisse (CustomEvent): 'statechange' {state, text}, 'titlechange' {title}, 'closerequest' {message, reason}.
 */
export class Session extends EventTarget {
  /** @param {{id?: string, conn: object, kind?: string, title?: string}} o */
  constructor({ id, conn, kind, title }) {
    super();
    this.id = id || newSid();
    this.conn = conn;
    this.kind = kind || conn.kind;
    this._title = title || conn.name;
    this.state = 'disconnected';
    this.stateText = t('app.stateDisconnected');
    this.element = null;          // Wurzelelement, gehoert der Sitzung
    this.visible = true;
  }

  get title() { return this._title; }
  set title(t) { if (t !== this._title) { this._title = t; this.dispatchEvent(new CustomEvent('titlechange', { detail: { title: t } })); } }

  /** Zustand setzen und melden (nur bei Aenderung). */
  setState(state, text) {
    if (state === this.state && text === this.stateText) return;
    this.state = state; this.stateText = text;
    this.dispatchEvent(new CustomEvent('statechange', { detail: { state, text } }));
  }

  /** Die Sitzung will geschlossen werden (Verwalter schliesst sie und zeigt die Meldung). */
  requestClose(message = '', reason = '') {
    this.dispatchEvent(new CustomEvent('closerequest', { detail: { message, reason } }));
  }

  mount(container) { if (this.element) container.appendChild(this.element); }
  unmount() { if (this.element) this.element.remove(); }
  async connect(/* password */) {}
  disconnect() {}
  resize() {}
  focus() {}
  blur() {}
  setVisible(on) { this.visible = !!on; }
  dispose() { this.disconnect(); this.unmount(); }
}

/** Der Verwalter: kennt die offenen Sitzungen und die aktive. Ereignisse: 'opened', 'closed', 'activated', 'statechange', 'switcher', 'home'. */
class SessionManager extends EventTarget {
  constructor() {
    super();
    this.sessions = [];           // in der Reihenfolge des Oeffnens
    this.active = null;
    this.factories = new Map();   // Art -> (conn, opts) => Session
    this.usedAt = new Map();      // Sitzung -> letzter Zeitpunkt, an dem sie aktiv war (fuer "naechste aktive" beim Schliessen)
  }

  emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }

  /** Pro Art eine Fabrik; die zuletzt angemeldete gilt (so ersetzt das native Terminal die Desktop-Sitzung fuer 'terminal'). */
  registerFactory(kind, make) { this.factories.set(kind, make); }

  list() { return [...this.sessions]; }
  get(id) { return this.sessions.find((s) => s.id === id) || null; }
  find(connId) { return this.sessions.filter((s) => s.conn.id === connId); }
  get count() { return this.sessions.length; }

  /** Anzeigename: "Name", bei weiteren Sitzungen zum selben Ziel "Name 2", "Name 3" (kleinste freie Nummer). */
  titleFor(conn) {
    const used = new Set(this.find(conn.id).map((s) => s.title));
    if (!used.has(conn.name)) return conn.name;
    for (let n = 2; ; n++) if (!used.has(`${conn.name} ${n}`)) return `${conn.name} ${n}`;
  }

  /**
   * Oeffnet eine Sitzung zu conn und verbindet. Ist zu diesem Ziel schon eine offen und extra nicht gesetzt, wird nur diese aktiviert.
   * @param {{password?: string, extra?: boolean}} [opts]
   * @returns {Session|null}
   */
  open(conn, opts = {}) {
    if (!opts.extra) {
      const there = this.find(conn.id);
      if (there.length) { const s = there.find((x) => x === this.active) || there[0]; this.activate(s.id, { reveal: true }); return s; }
    }
    const make = this.factories.get(conn.kind) || this.factories.get('*');
    if (!make) throw new Error(t('app.errNoSessionKind', { kind: conn.kind }));
    const session = make(conn, { sid: newSid(), title: this.titleFor(conn), password: opts.password });
    this.sessions.push(session);
    session.addEventListener('statechange', () => this.emit('statechange', { session }));
    session.addEventListener('titlechange', () => this.emit('statechange', { session }));
    session.addEventListener('closerequest', (e) => this.close(session.id, e.detail.message, e.detail.reason));
    this.emit('opened', { session });
    this.activate(session.id, { reveal: true });
    session.connect(opts.password);
    return session;
  }

  /**
   * Macht eine Sitzung zur aktiven (sie bekommt die Tastatur). 'activated' kommt nur bei einem Wechsel; mit reveal (die Sitzung soll gezeigt werden,
   * auch wenn sie schon die aktive ist, etwa nach "Zur Startseite") immer.
   */
  activate(id, { reveal = false } = {}) {
    const next = this.get(id);
    if (!next) return;
    const prev = this.active;
    if (prev === next && !reveal) return;
    this.active = next;
    this.usedAt.set(next, Date.now());
    this.emit('activated', { session: next, previous: prev, reveal });
  }

  /** Schliesst eine Sitzung. message/reason gehen an die Huelle (zum Beispiel "Anmeldung fehlgeschlagen", 'auth' = App-Sitzung abgelaufen). */
  close(id, message = '', reason = '') {
    const session = this.get(id);
    if (!session) return;
    const wasActive = this.active === session;
    this.sessions = this.sessions.filter((s) => s !== session);
    this.usedAt.delete(session);
    let next = null;
    if (wasActive) {
      next = [...this.sessions].sort((a, b) => (this.usedAt.get(b) || 0) - (this.usedAt.get(a) || 0))[0] || null;
      this.active = null;
    }
    try { session.dispose(); } catch { /* egal */ }
    this.emit('closed', { session, message, reason, next });
    if (wasActive && next) this.activate(next.id);
  }

  closeAll(message = '', reason = '') { for (const s of this.list()) this.close(s.id, message, reason); }

  /** Aus einer Sitzung heraus: den Umschalter zeigen / zur Startseite (die Huelle reagiert, die Sitzungen laufen weiter). */
  requestSwitcher() { this.emit('switcher', {}); }
  requestHome() { this.emit('home', {}); }
}

export const Sessions = new SessionManager();
