/* =====================================================================
 * JulOS – natives SSH-Terminal
 *  Echter Text im Browser (xterm.js, lokal in lib/xterm): Auswahl, Kopieren, Einfuegen, Scrollback, scharfe Schrift.
 *  Der Server haelt die SSH-Verbindung (/api/remote/ssh, Protokoll siehe SshProtocol.cs); hier geht nur Text und Groesse hin und her.
 *  Eine TerminalView ist die Ansicht fuer eine Verbindung vom Typ "terminal" und kennt app.js nicht: sie bekommt einen Container
 *  und meldet ihren Zustand ueber Rueckrufe. Die Sondertasten-Leiste ruft sendKeyEvent(gedrueckt, X11-Keysym) wie beim Desktop-Client.
 * ===================================================================== */

import { t } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import { noticeTexts } from './terminal-programs.js';
import { LinkHealth, PROBE_MS } from './link-health.js';

const LIB = '/lib/xterm/';
let libPromise = null;
/** xterm.js erst beim ersten Terminal laden (das Handy braucht es sonst nicht). */
function loadLib() {
  libPromise ??= (async () => {
    const [x, fit, gl, uni] = await Promise.all([
      import(LIB + 'xterm.mjs'), import(LIB + 'addon-fit.mjs'), import(LIB + 'addon-webgl.mjs'), import(LIB + 'addon-unicode11.mjs'),
    ]);
    return { Terminal: x.Terminal, FitAddon: fit.FitAddon, WebglAddon: gl.WebglAddon, Unicode11Addon: uni.Unicode11Addon };
  })().catch((e) => { libPromise = null; throw e; });
  return libPromise;
}

// Protokoll (siehe SshProtocol.cs)
const TO = { data: 1, resize: 2, ping: 3, pause: 4, resume: 5 };
const FROM = { data: 1, ready: 2, end: 3, pong: 4 };   // "Bereit" mit link = 1: der Server beantwortet Pings (TO.ping mit 4 Byte Nummer) mit pong; ohne link sendet der Browser nichts davon (alter Server)
const END_HOSTKEY = 800, END_SESSION_EXPIRED = 802;
const MAX_FRAME = 16 * 1024;                          // Eingaben werden in Stuecken dieser Groesse gesendet (der Server nimmt bis 64 kB)
const HIGH_WATER = 256 * 1024, LOW_WATER = 64 * 1024; // so viel unverarbeitete Ausgabe, dann bittet der Browser den Server um Pause

const FONT = '"JetBrains Mono", "Cascadia Mono", "Cascadia Code", "SF Mono", "Roboto Mono", "DejaVu Sans Mono", "Liberation Mono", Menlo, Consolas, "Noto Sans Mono", monospace';
const store = {
  get(k, d) { try { const v = localStorage.getItem('pd.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('pd.' + k, JSON.stringify(v)); } catch { /* ignorieren */ } },
};
const enc = new TextEncoder();
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

/* ---------- Tasten: X11-Keysym (wie in app.js) -> Bytes fuer das Terminal ---------- */
const MODKEY = { 0xffe1: 'shift', 0xffe2: 'shift', 0xffe3: 'ctrl', 0xffe4: 'ctrl', 0xffe9: 'alt', 0xffea: 'alt', 0xffeb: 'meta', 0xffec: 'meta' };
// CSI-Tasten: [Endbuchstabe] oder [Zahl, '~']
const CSI_KEYS = {
  0xff51: ['D'], 0xff52: ['A'], 0xff53: ['C'], 0xff54: ['B'], 0xff50: ['H'], 0xff57: ['F'],
  0xff63: [2, '~'], 0xffff: [3, '~'], 0xff55: [5, '~'], 0xff56: [6, '~'],
  0xffc2: [15, '~'], 0xffc3: [17, '~'], 0xffc4: [18, '~'], 0xffc5: [19, '~'], 0xffc6: [20, '~'], 0xffc7: [21, '~'], 0xffc8: [23, '~'], 0xffc9: [24, '~'],
};
const SS3_KEYS = { 0xffbe: 'P', 0xffbf: 'Q', 0xffc0: 'R', 0xffc1: 'S' };            // F1 bis F4
const APP_CURSOR = new Set([0xff51, 0xff52, 0xff53, 0xff54, 0xff50, 0xff57]);        // im Anwendungsmodus ESC O statt ESC [

/** Zeichen mit Strg/Alt: Strg+Buchstabe = Steuerzeichen, Alt = ESC davor (wie xterm). */
export function charBytes(ch, mods = {}) {
  const cp = ch.codePointAt(0);
  let out = ch;
  if (mods.ctrl) {
    if (cp >= 0x61 && cp <= 0x7a) out = String.fromCharCode(cp - 0x60);
    else if (cp >= 0x41 && cp <= 0x5a) out = String.fromCharCode(cp - 0x40);
    else if (cp === 0x20 || cp === 0x40) out = '\0';
    else if (cp >= 0x5b && cp <= 0x5f) out = String.fromCharCode(cp - 0x40);          // [ \ ] ^ _
    else if (cp === 0x3f) out = '\x7f';
    else if (cp === 0x2f) out = '\x1f';
  } else if (mods.shift && cp >= 0x61 && cp <= 0x7a) {
    out = ch.toUpperCase();                                                          // die Leiste sendet Kleinbuchstaben mit gehaltener Umschalttaste
  }
  return mods.alt ? '\x1b' + out : out;
}

/** Die Bytes (als Text) fuer eine Taste. mods = {ctrl, alt, shift}; null, wenn die Taste im Terminal nichts bedeutet. */
export function keysymBytes(sym, mods = {}, appCursor = false) {
  const m = 1 + (mods.shift ? 1 : 0) + (mods.alt ? 2 : 0) + (mods.ctrl ? 4 : 0);
  const csi = CSI_KEYS[sym];
  if (csi) {
    if (csi.length === 1) return m > 1 ? `\x1b[1;${m}${csi[0]}` : (appCursor && APP_CURSOR.has(sym) ? '\x1bO' : '\x1b[') + csi[0];
    return m > 1 ? `\x1b[${csi[0]};${m}~` : `\x1b[${csi[0]}~`;
  }
  const ss3 = SS3_KEYS[sym];
  if (ss3) return m > 1 ? `\x1b[1;${m}${ss3}` : '\x1bO' + ss3;
  let s;
  switch (sym) {
    case 0xff0d: s = '\r'; break;
    case 0xff08: s = mods.ctrl ? '\x08' : '\x7f'; break;
    case 0xff09: return mods.shift ? '\x1b[Z' : (mods.alt ? '\x1b\t' : '\t');
    case 0xff1b: s = '\x1b'; break;
    default: {
      const cp = sym < 0x100 ? sym : sym >= 0x01000000 && sym <= 0x0110ffff ? sym - 0x01000000 : null;
      if (cp === null || cp < 0x20 || cp === 0x7f) return null;
      return charBytes(String.fromCodePoint(cp), mods);
    }
  }
  return mods.alt ? '\x1b' + s : s;
}

/* ---------- Farben: dem App-Design folgen (m3.css, sonst die --md-Werte aus app.js) ---------- */
const ANSI_DARK = ['#2e3436', '#ef6b6b', '#8ae234', '#fce94f', '#729fcf', '#c58af9', '#34e2e2', '#d3d7cf', '#6e7679', '#ff8c8c', '#a6f562', '#fff06e', '#9bbfe6', '#d9a8ff', '#6af0f0', '#ffffff'];
const ANSI_LIGHT = ['#1f2328', '#c4271b', '#2a7a1e', '#8a6a00', '#1f5fbf', '#8a3fbf', '#0e7c86', '#6e7681', '#57606a', '#e5251f', '#1a7f37', '#9a6700', '#0969da', '#a371f7', '#1b7c83', '#32383f'];
let probeCtx = null;
/** Beliebige CSS-Farbe (auch oklch aus m3.css) in #rrggbb umrechnen; der Browser rechnet, xterm bekommt nur einfache Werte. */
function toHex(css, fallback) {
  try {
    probeCtx ??= Object.assign(document.createElement('canvas'), { width: 1, height: 1 }).getContext('2d', { willReadFrequently: true });
    probeCtx.clearRect(0, 0, 1, 1); probeCtx.fillStyle = '#000'; probeCtx.fillStyle = css; probeCtx.fillRect(0, 0, 1, 1);
    const d = probeCtx.getImageData(0, 0, 1, 1).data;
    return '#' + [d[0], d[1], d[2]].map((v) => v.toString(16).padStart(2, '0')).join('');
  } catch { return fallback; }
}
function cssColor(el, names, fallback) {
  const probe = document.createElement('i');
  probe.style.cssText = 'position:absolute;width:0;height:0;visibility:hidden';
  el.appendChild(probe);
  try {
    for (const n of names) {
      probe.style.color = ''; probe.style.color = `var(${n})`;
      if (!getComputedStyle(document.documentElement).getPropertyValue(n).trim()) continue;
      return toHex(getComputedStyle(probe).color, fallback);
    }
  } finally { probe.remove(); }
  return fallback;
}
export function themeFor(host) {
  const dark = getComputedStyle(document.documentElement).colorScheme.includes('dark') || (!getComputedStyle(document.documentElement).colorScheme.includes('light') && matchMedia('(prefers-color-scheme: dark)').matches);
  const bg = cssColor(host, ['--m3-surface-lowest', '--m3-surface', '--md-surface'], dark ? '#14141a' : '#fbfbfd');
  const fg = cssColor(host, ['--m3-on-surface', '--md-on-surface'], dark ? '#e6e6ec' : '#1b1b20');
  const sel = cssColor(host, ['--m3-primary-container', '--md-primary-container'], dark ? '#3a3f6b' : '#cfd6ff');
  const cur = cssColor(host, ['--m3-primary', '--md-primary'], dark ? '#a8b4ff' : '#3f51b5');
  const a = dark ? ANSI_DARK : ANSI_LIGHT;
  return {
    dark, background: bg, foreground: fg, cursor: cur, cursorAccent: bg, selectionBackground: sel, selectionInactiveBackground: sel,
    black: a[0], red: a[1], green: a[2], yellow: a[3], blue: a[4], magenta: a[5], cyan: a[6], white: a[7],
    brightBlack: a[8], brightRed: a[9], brightGreen: a[10], brightYellow: a[11], brightBlue: a[12], brightMagenta: a[13], brightCyan: a[14], brightWhite: a[15],
  };
}

const el = (tag, cls, text) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text) n.textContent = text; return n; };

/**
 * Die Terminal-Ansicht (xterm.js plus Verbindung zum Server); die Sitzungsart fuer den Verwalter ist terminal-session.js.
 * opts: { conn: {id, name}, sid, password, container, onState(state, info), onTitle(text), onKeyUsed(), onAuthExpired(), onTap(), externalKeyboard }
 * sid: Sitzungskennung des Browsers (Slot auf dem Server, siehe docs/SESSIONS.md). externalKeyboard: die Bildschirmtastatur gehoert der Seite
 * (ein eigenes Eingabefeld): ein Tippen oeffnet sie ueber onTap, das Eingabefeld von xterm nimmt dann keinen Fokus und zeigt keine Tastatur.
 * Zustaende: 'connecting' | 'connected' | 'closed' ({message, code, fatal}) | 'error' ({message, fatal}); fatal = nicht wiederholen (Anmeldung abgelehnt, Schluessel abgelehnt).
 */
export class TerminalView {
  constructor(opts) {
    this.opts = opts;
    this.conn = opts.conn;
    this.password = opts.password || null;
    this.host = el('div', 'term-host');
    this.host.hidden = true;
    opts.container.appendChild(this.host);
    this.term = null; this.fit = null; this.ws = null;
    this.held = { ctrl: false, alt: false, shift: false };       // Umschalter der Sondertasten-Leiste
    this.state = 'idle'; this.connected = false; this.destroyed = false; this.active = true;
    this.pending = 0; this.paused = false; this.generation = 0;
    this.link = new LinkHealth(); this.linkTimer = 0; this.pingId = 0;        // Lebenszeichen der Leitung (link-health.js)
    this.lastClose = null;
    this.fontSize = clamp(store.get('termFont', matchMedia('(pointer: coarse)').matches ? 13 : 14), 8, 30);
    this.lastCols = 0; this.lastRows = 0; this.resizeTimer = 0; this.fitFrame = 0;
  }

  get ready() { return this.connected; }

  /* ---------- Verbindung ---------- */
  async connect({ acceptHostKey } = {}) {
    if (this.destroyed) return;
    const gen = ++this.generation;
    this._closeSocket();
    this._setState('connecting', { message: t('term.connecting') });
    try { await this._ensureTerminal(); } catch (e) { console.error('Terminal konnte nicht gestartet werden', e); return this._setState('error', { message: t('term.loadFailed'), fatal: false }); }
    if (gen !== this.generation) return;
    this.host.hidden = false;
    this._fitNow();
    const cols = this.term.cols, rows = this.term.rows;
    let ticket;
    try {
      const resp = await fetch('/api/remote/ticket', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-JulOS': '1' }, signal: AbortSignal.timeout(10000),
        body: JSON.stringify({ ...(this.conn.quick ? { quick: this.conn.quick } : { connection: this.conn.id }), sid: this.opts.sid || undefined, password: this.password || undefined, cols, rows, acceptHostKey: acceptHostKey || undefined }),
      });
      const data = await resp.json().catch(() => ({}));
      if (gen !== this.generation) return;
      if (resp.status === 401) return this.opts.onAuthExpired?.();
      if (!resp.ok) return this._setState('error', { message: serverMsg(data.error) || t('term.errorStatus', { status: resp.status }), fatal: true });
      ticket = data.ticket;
    } catch (e) {
      if (gen !== this.generation) return;
      return this._setState('error', { message: e.name === 'TimeoutError' || e.name === 'TypeError' ? t('term.noServerCheckNet') : e.message, fatal: false });
    }
    const ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/api/remote/ssh?ticket=' + encodeURIComponent(ticket));
    ws.binaryType = 'arraybuffer';
    this.ws = ws;
    let ended = false;
    this.link.reset(); clearInterval(this.linkTimer);
    this.linkTimer = setInterval(() => this._linkTick(), 1000);
    ws.onmessage = (ev) => { if (gen === this.generation) this._onMessage(new Uint8Array(ev.data), (e) => { ended = e; }); };
    ws.onclose = (ev) => {
      if (gen !== this.generation || this.destroyed) return;
      this.connected = false; clearInterval(this.linkTimer);
      this.lastClose = { code: ev.code, clean: ev.wasClean, at: Date.now(), reason: ev.wasClean ? 'closed' : 'dropped' };
      if (ended) return;                                               // der Server hat schon gesagt, warum
      this._setState(this.everConnected ? 'closed' : 'error', { message: this.everConnected ? t('term.closedShellEnded') : t('term.noServer'), fatal: false });
    };
    ws.onerror = () => { /* onclose folgt */ };
  }

  _linkTick() {
    const a = this.link.tick();
    if (a === 'ping') this._ping(); else if (a === 'dead') this._lineDead('silence');
  }

  _ping() { const id = ++this.pingId >>> 0; this._sendRaw(TO.ping, new Uint8Array([id >>> 24, (id >>> 16) & 255, (id >>> 8) & 255, id & 255])); }

  /** Kein Lebenszeichen mehr: die Leitung aktiv schliessen und wie einen Abriss melden (ohne Code: bei tmux haengt sich die Sitzung wieder an). */
  _lineDead(reason) {
    this.lastClose = { code: 0, clean: false, at: Date.now(), reason };
    const gen = this.generation;
    this._closeSocket();
    if (this.destroyed || gen !== this.generation) return;
    this._setState(this.everConnected ? 'closed' : 'error', { message: this.everConnected ? t('term.closedShellEnded') : t('term.noServer'), fatal: false });
  }

  /** Seite wieder sichtbar nach awayMs: 'ping' (Probe laeuft), 'reconnect' (Server ohne Lebenszeichen) oder null. */
  probe(awayMs) {
    const a = this.link.visible(awayMs);
    if (a === 'ping') { this._ping(); setTimeout(() => { if (this.ws) this._linkTick(); }, PROBE_MS + 60); }
    return a;
  }

  _onMessage(m, markEnded) {
    this.link.touchRx();
    switch (m[0]) {
      case FROM.pong: break;
      case FROM.data: {
        const chunk = m.subarray(1);
        this.pending += chunk.length;
        this.term.write(chunk, () => {
          this.pending -= chunk.length;
          if (this.paused && this.pending < LOW_WATER) { this.paused = false; this._sendRaw(TO.resume); }
        });
        if (!this.paused && this.pending > HIGH_WATER) { this.paused = true; this._sendRaw(TO.pause); }
        break;
      }
      case FROM.ready: {
        const info = JSON.parse(new TextDecoder().decode(m.subarray(1)));
        this.connected = true; this.everConnected = true; this.fingerprint = info.fingerprint;
        if (info.link) this.link.enable();
        if (info.hostKey === 'new') this._banner(t('term.hostKeyNew', { fingerprint: info.fingerprint }));
        else if (info.hostKey === 'changed-accepted') this._banner(t('term.hostKeyAccepted', { fingerprint: info.fingerprint }));
        // Programm-Sitzung: was der Server gefunden hat (fehlendes Programm, Verzeichnis oder tmux) nie still lassen: als Hinweis und als Zeile im Terminal
        const notices = noticeTexts(info.notice, this.conn, t);
        if (notices.length) {
          this.term.write(notices.map((n) => `\r\n\x1b[33m[JulOS] ${n}\x1b[0m`).join('') + '\r\n');
          this._banner(notices.join(' '), 15000);
        }
        this.lastCols = 0;                                              // die Groesse nach dem Verbinden einmal sicher melden
        this._setState('connected', { hostKey: info.hostKey, fingerprint: info.fingerprint, program: info.program, keep: info.keep, notices });
        this._sendResize(true);
        if (this.active) this.focus();
        break;
      }
      case FROM.end: {
        markEnded(true);
        const info = JSON.parse(new TextDecoder().decode(m.subarray(1)));
        this.connected = false;
        if (info.code === END_HOSTKEY) return this._hostKeyDialog(info);
        if (info.code === END_SESSION_EXPIRED) return this.opts.onAuthExpired?.();
        const wasUp = this.everConnected;
        this._setState(wasUp ? 'closed' : 'error', { message: serverMsg(info.message), code: info.code, fatal: !wasUp && (info.code === 769) });
        break;
      }
    }
  }

  _closeSocket() {
    const ws = this.ws; this.ws = null; clearInterval(this.linkTimer);
    if (ws) { ws.onmessage = ws.onclose = ws.onerror = null; try { ws.close(); } catch { /* egal */ } }
    this.connected = false; this.pending = 0; this.paused = false;
  }

  disconnect() { this.generation++; this._closeSocket(); }

  destroy() {
    this.destroyed = true; this.generation++;
    this._closeSocket();
    clearTimeout(this.resizeTimer); clearTimeout(this.fitFrame); cancelAnimationFrame(this.flingFrame);
    this.resizeObserver?.disconnect(); this.themeObserver?.disconnect();
    this.mql?.removeEventListener('change', this.onScheme);
    try { this.term?.dispose(); } catch { /* egal */ }
    this.host.remove();
  }

  _setState(state, info = {}) {
    this.state = state;
    this.opts.onState?.(state, info);
  }

  /* ---------- Senden ---------- */
  _sendRaw(kind, payload) {
    const ws = this.ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) return false;
    const m = new Uint8Array(1 + (payload ? payload.length : 0));
    m[0] = kind;
    if (payload) m.set(payload, 1);
    this.link.touchTx();
    ws.send(m);
    return true;
  }
  /** Text an die Shell (UTF-8), in Stuecken; bei sehr langem Text nicht den ganzen WebSocket-Puffer fuellen. */
  _sendText(text) {
    if (!this.connected || !text) return;
    const bytes = enc.encode(text);
    for (let i = 0; i < bytes.length; i += MAX_FRAME) {
      let end = Math.min(bytes.length, i + MAX_FRAME);
      while (end < bytes.length && (bytes[end] & 0xc0) === 0x80) end--;       // nicht mitten in einem UTF-8-Zeichen trennen
      this._sendRaw(TO.data, bytes.subarray(i, end));
      i = end - MAX_FRAME;
    }
  }
  _sendResize(force = false) {
    if (!this.connected || !this.term) return;
    const { cols, rows } = this.term;
    if (!force && cols === this.lastCols && rows === this.lastRows) return;
    this.lastCols = cols; this.lastRows = rows;
    this._sendRaw(TO.resize, new Uint8Array([cols >> 8, cols & 255, rows >> 8, rows & 255]));
  }

  /** Text an das Programm (Schnelltasten und Eingabeleiste); gesendet wird nur bei stehender Verbindung. */
  sendText(text) { this._sendText(text); this.opts.onKeyUsed?.(); }
  /** Kurzer Hinweis oben im Terminal. */
  notify(text, ms = 5000) { this._banner(text, ms); }

  /** Eingaben der Sondertasten-Leiste: X11-Keysym, wie beim Stream-Client. */
  sendKeyEvent(pressed, sym) {
    const mod = MODKEY[sym];
    if (mod) { if (mod !== 'meta') this.held[mod] = !!pressed; return; }
    if (!pressed) return;
    const bytes = keysymBytes(sym, this.held, !!this.term?.modes.applicationCursorKeysMode);
    if (bytes) { this._sendText(bytes); this.opts.onKeyUsed?.(); }
  }
  /** Text wie getippt (Strg/Alt der Leiste gelten fuer ein einzelnes Zeichen). */
  _typed(data) {
    if ((this.held.ctrl || this.held.alt) && [...data].length === 1) { this._sendText(charBytes(data, this.held)); this.opts.onKeyUsed?.(); return; }
    this._sendText(data);
  }

  /* ---------- Terminal ---------- */
  async _ensureTerminal() {
    if (this.term) return;
    const { Terminal, FitAddon, WebglAddon, Unicode11Addon } = await loadLib();
    if (this.destroyed || this.term) return;
    const theme = themeFor(this.host);
    this.dark = theme.dark;
    const term = new Terminal({
      fontFamily: FONT, fontSize: this.fontSize, lineHeight: 1.12, cursorBlink: true, cursorStyle: 'block', scrollback: 10000,
      allowProposedApi: true, macOptionIsMeta: true, theme, minimumContrastRatio: 3, drawBoldTextInBrightColors: false,
      rightClickSelectsWord: false, fastScrollModifier: 'shift',
    });
    this.term = term;
    this.fit = new FitAddon();
    term.loadAddon(this.fit);
    term.loadAddon(new Unicode11Addon());
    term.unicode.activeVersion = '11';
    this.host.hidden = false;                                       // xterm misst beim Oeffnen: ein verstecktes Element hat keine Groesse
    term.open(this.host);
    if (store.get('termGl', true)) {
      try {
        const gl = new WebglAddon();
        gl.onContextLoss(() => { try { gl.dispose(); } catch { /* egal */ } });
        term.loadAddon(gl);
      } catch { /* kein WebGL: der normale Renderer zeichnet */ }
    }
    this.host.style.background = theme.background;
    // Mit der Bildschirmtastatur der Seite soll ein Tippen ins Terminal nicht die Tastatur von xterm oeffnen
    if (this.opts.externalKeyboard) term.textarea?.setAttribute('inputmode', 'none');
    term.onData((d) => this._typed(d));
    term.onBinary((d) => { if (this.connected) this._sendRaw(TO.data, Uint8Array.from(d, (c) => c.charCodeAt(0))); });
    term.onBell(() => this._bell());
    term.onTitleChange((t) => this.opts.onTitle?.(t));
    term.attachCustomKeyEventHandler((e) => this._keyHandler(e));
    // Hat das Programm die SGR-Mauskodierung (1006) eingeschaltet? Davon haengt ab, wie das Wischen als Mausrad gemeldet wird
    term.parser.registerCsiHandler({ prefix: '?', final: 'h' }, (p) => { if (p.includes(1006)) this.sgrMouse = true; return false; });
    term.parser.registerCsiHandler({ prefix: '?', final: 'l' }, (p) => { if (p.includes(1006)) this.sgrMouse = false; return false; });
    this.resizeObserver = new ResizeObserver(() => this._scheduleFit());
    this.resizeObserver.observe(this.host);
    // Design: folgt App-Farben (Stil der Wurzel aendert sich) und dem System (hell/dunkel)
    this.onScheme = () => this._applyTheme();
    this.mql = matchMedia('(prefers-color-scheme: dark)');
    this.mql.addEventListener('change', this.onScheme);
    this.themeObserver = new MutationObserver(() => this._applyTheme());
    this.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'class', 'data-theme'] });
    this._setupSelection();
  }

  _applyTheme() {
    if (!this.term) return;
    const t = themeFor(this.host);
    const sig = JSON.stringify(t);
    if (sig === this.themeSig) return;
    this.themeSig = sig;
    this.term.options.theme = t;
    this.host.style.background = t.background;
  }

  _fitNow() {
    if (!this.term || !this.active || this.host.hidden || !this.host.clientWidth || !this.host.clientHeight) return;
    const dims = this.fit.proposeDimensions();
    if (!dims || !Number.isFinite(dims.cols) || !Number.isFinite(dims.rows)) return;
    const cols = Math.max(2, dims.cols), rows = Math.max(1, dims.rows);
    if (cols !== this.term.cols || rows !== this.term.rows) this.term.resize(cols, rows);
  }
  /** Groesse folgt dem Behaelter: zuerst lokal (billig), die Meldung an den Server gesammelt (Tastatur-Animationen melden viele Zwischengroessen). */
  _scheduleFit() {
    if (this.fitFrame) return;
    // Zeitgeber statt requestAnimationFrame: der laeuft in Hintergrund-Tabs gar nicht, und die Groesse soll trotzdem stimmen, wenn der Tab zurueckkommt
    this.fitFrame = setTimeout(() => {
      this.fitFrame = 0;
      this._fitNow();
      clearTimeout(this.resizeTimer);
      this.resizeTimer = setTimeout(() => this._sendResize(), 180);
    }, 30);
  }
  fitSoon() { this._scheduleFit(); }

  /** Ansicht im Hintergrund (versteckt): nicht anpassen, die Verbindung bleibt. */
  setActive(active) {
    this.active = active;
    this.host.style.visibility = active ? '' : 'hidden';
    if (active) { this._scheduleFit(); this.focus(); }
  }

  /** Tastatur an das Terminal (mit externer Bildschirmtastatur: nichts, die Seite verwaltet ihr Eingabefeld). */
  focus() { if (!this.opts.externalKeyboard) this.focusInput(); }
  /** Das Eingabefeld von xterm bekommt den Fokus (echte Tastatur, Einfuegen). */
  focusInput({ force = false } = {}) {
    // Der Fokus im Textfeld der Eingabeleiste wird nicht von Aktivierungen (Fenster nach vorn, Verbinden) weggenommen; ein Tippen ins Terminal (force) holt ihn
    if (!force && this.host.parentElement?.querySelector('.term-bar textarea:focus')) return;
    try { this.term?.focus(); } catch { /* egal */ }
  }
  blur() { try { this.term?.blur(); } catch { /* egal */ } }
  get keyboardFocused() { return !!this.term && this.host.contains(document.activeElement); }

  /** Schriftgroesse (Zoom der Leiste, Zwei-Finger-Geste): Schritte in Punkten. */
  zoom(delta) { this.setFontSize(this.fontSize + delta); }
  setFontSize(px) {
    px = clamp(Math.round(px), 8, 30);
    if (px === this.fontSize || !this.term) return;
    this.fontSize = px; store.set('termFont', px);
    this.term.options.fontSize = px;
    // die neue Zeichengroesse steht erst nach dem naechsten Zeichnen fest: danach noch einmal anpassen
    this._scheduleFit();
    setTimeout(() => this._scheduleFit(), 120);
    this._banner(px + ' pt', 900);
  }

  _bell() {
    this.host.classList.add('bell');
    setTimeout(() => this.host.classList.remove('bell'), 140);
    try { navigator.vibrate?.(18); } catch { /* egal */ }
  }

  _banner(text, ms = 7000) {
    this.bannerEl?.remove();
    const b = el('div', 'term-banner', text);
    this.host.appendChild(b);
    this.bannerEl = b;
    setTimeout(() => { if (this.bannerEl === b) { b.remove(); this.bannerEl = null; } }, ms);
  }

  /* ---------- Kopieren und Einfuegen ---------- */
  getSelection() { return this.term ? this.term.getSelection() : ''; }

  async copy() {
    const text = this.getSelection();
    if (!text) return false;
    try { await navigator.clipboard.writeText(text); }
    catch {
      // Ohne Zwischenablage-Zugriff (kein HTTPS, Berechtigung): der alte Weg ueber ein verstecktes Textfeld
      const ta = Object.assign(document.createElement('textarea'), { value: text });
      ta.style.cssText = 'position:fixed;opacity:0;left:-1000px';
      document.body.appendChild(ta); ta.select();
      let ok = false; try { ok = document.execCommand('copy'); } catch { /* egal */ }
      ta.remove(); this.focus();
      if (!ok) { this._banner(t('term.copyFailed')); return false; }
    }
    this._banner(t('term.copied'), 900);
    return true;
  }
  paste(text) { if (text) this.term?.paste(text); }
  async pasteFromClipboard() {
    try { this.paste(await navigator.clipboard.readText()); }
    catch { this._banner(t('term.pasteDenied'), 6000); }
    this.focus();
  }

  _keyHandler(e) {
    if (e.type !== 'keydown') return true;
    const k = e.key.toLowerCase();
    const mod = e.ctrlKey || e.metaKey;
    // Umschalt+Strg+C / Strg+Einfg: kopieren; Umschalt+Strg+V / Umschalt+Einfg: einfuegen (das Einfuegen kommt dann als paste-Ereignis des Browsers)
    if (mod && e.shiftKey && k === 'c') { this.copy(); return false; }
    if (mod && e.shiftKey && k === 'v') return false;
    if (e.ctrlKey && !e.shiftKey && e.key === 'Insert') { this.copy(); return false; }
    if (e.shiftKey && !e.ctrlKey && e.key === 'Insert') return false;
    if (e.metaKey && !e.ctrlKey && k === 'c' && this.term.hasSelection()) { this.copy(); return false; }
    // Strg+C mit Auswahl kopiert (wie im Windows-Terminal), ohne Auswahl ist es das Unterbrechen (^C)
    if (e.ctrlKey && !e.shiftKey && !e.altKey && k === 'c' && this.term.hasSelection()) { this.copy().then(() => this.term.clearSelection()); return false; }
    return true;
  }

  /* ---------- Auswahl per Finger und Rechtsklick-Menue ---------- */
  _cellAt(x, y) {
    const screen = this.host.querySelector('.xterm-screen');
    const r = screen.getBoundingClientRect();
    const cw = r.width / this.term.cols, ch = r.height / this.term.rows;
    const col = clamp(Math.floor((x - r.left) / cw), 0, this.term.cols - 1), vrow = clamp(Math.floor((y - r.top) / ch), 0, this.term.rows - 1);
    return { col, row: this.term.buffer.active.viewportY + vrow, cw, ch };
  }
  _wordAt(col, row) {
    const line = this.term.buffer.active.getLine(row);
    const text = line ? line.translateToString(false) : '';
    const sep = ' \t()[]{}\'"<>`,;|';
    if (!text || sep.includes(text[col] ?? ' ')) return { start: col, length: 1 };
    let a = col, b = col;
    while (a > 0 && !sep.includes(text[a - 1])) a--;
    while (b < text.length - 1 && !sep.includes(text[b + 1])) b++;
    return { start: a, length: b - a + 1 };
  }
  _selectRange(a, b) {                                   // a, b = {col, row}, in beliebiger Reihenfolge
    const [s, e] = (a.row < b.row || (a.row === b.row && a.col <= b.col)) ? [a, b] : [b, a];
    this.term.select(s.col, s.row, (e.row - s.row) * this.term.cols + (e.col - s.col) + 1);
  }

  /**
   * Wischen = scrollen. Im normalen Puffer der Scrollback; in der Alternativ-Ansicht (vim, less) Pfeiltasten wie beim Mausrad im Terminal;
   * meldet das Programm die Maus (tmux, htop), geht es als Mausrad-Ereignis hin (SGR-Kodierung, wenn das Programm sie eingeschaltet hat).
   * Synthetische wheel-Ereignisse nimmt xterm.js nicht an, darum wird hier selbst gerechnet.
   */
  _scrollBy(dy, x, y) {
    const t = this.term;
    const cell = this._cellAt(x, y);
    this.scrollAcc = (this.scrollAcc || 0) + dy / cell.ch;
    const n = Math.trunc(this.scrollAcc);
    if (!n) return;
    this.scrollAcc -= n;
    if (t.modes.mouseTrackingMode !== 'none') {
      const up = n < 0, col = cell.col + 1, row = clamp(Math.floor((y - this.host.querySelector('.xterm-screen').getBoundingClientRect().top) / cell.ch) + 1, 1, t.rows);
      for (let i = 0; i < Math.min(Math.abs(n), 8); i++) {
        this._sendText(this.sgrMouse ? `\x1b[<${up ? 64 : 65};${col};${row}M` : `\x1b[M${String.fromCharCode(32 + (up ? 64 : 65), 32 + col, 32 + row)}`);
      }
    } else if (t.buffer.active.type === 'alternate') {
      const key = (n < 0 ? 'A' : 'B'), seq = (t.modes.applicationCursorKeysMode ? '\x1bO' : '\x1b[') + key;
      this._sendText(seq.repeat(Math.min(Math.abs(n), 12)));
    } else {
      t.scrollLines(n);
    }
  }
  _fling(vy, x, y) {
    cancelAnimationFrame(this.flingFrame);
    let v = vy;
    const step = () => {
      if (Math.abs(v) < 0.4 || this.destroyed) return;
      this._scrollBy(v, x, y); v *= 0.94;
      this.flingFrame = requestAnimationFrame(step);
    };
    this.flingFrame = requestAnimationFrame(step);
  }

  _tap(x, y) {
    const t = this.term;
    if (t.modes.mouseTrackingMode !== 'none' && this.connected) {
      const c = this._cellAt(x, y), col = c.col + 1, row = c.row - t.buffer.active.viewportY + 1;
      this._sendText(this.sgrMouse ? `\x1b[<0;${col};${row}M\x1b[<0;${col};${row}m` : `\x1b[M${String.fromCharCode(32, 32 + col, 32 + row)}\x1b[M${String.fromCharCode(35, 32 + col, 32 + row)}`);
    }
    if (this.opts.onTap) this.opts.onTap(); else this.focusInput({ force: true });
  }

  _menu(x, y, { all = true } = {}) {
    this._closeMenu();
    const menu = el('div', 'term-menu');
    menu.setAttribute('role', 'menu');
    const add = (label, fn) => {
      const b = el('button', '', label); b.type = 'button';
      b.addEventListener('pointerdown', (e) => e.preventDefault());           // der Fokus bleibt im Terminal (die Tastatur bleibt offen)
      b.addEventListener('click', (e) => { e.stopPropagation(); this._closeMenu(); fn(); });
      menu.appendChild(b);
    };
    if (this.term.hasSelection()) add(t('common.copy'), () => { this.copy(); this.focus(); });
    add(t('term.paste'), () => this.pasteFromClipboard());
    if (all) add(t('term.selectAll'), () => { this.term.selectAll(); this._menu(x, y, { all: false }); });
    this.host.appendChild(menu);
    const hr = this.host.getBoundingClientRect(), mw = menu.offsetWidth, mh = menu.offsetHeight;
    menu.style.left = clamp(x - hr.left - mw / 2, 4, Math.max(4, hr.width - mw - 4)) + 'px';
    menu.style.top = clamp(y - hr.top - mh - 14, 4, Math.max(4, hr.height - mh - 4)) + 'px';
    this.menuEl = menu;
  }
  _closeMenu() { this.menuEl?.remove(); this.menuEl = null; }

  _setupSelection() {
    const host = this.host;
    host.addEventListener('contextmenu', (e) => { e.preventDefault(); this._menu(e.clientX, e.clientY); });
    host.addEventListener('mousedown', (e) => { if (e.button === 0 && !e.target.closest('.term-menu')) this._closeMenu(); }, true);
    // Finger: ziehen = scrollen (als Mausrad, dann funktionieren auch Programme mit Mausunterstuetzung), lange tippen = Wort waehlen und
    // dann ziehen = Auswahl erweitern, zwei Finger = Schriftgroesse. Ein kurzes Tippen bleibt ein Klick (Fokus, Tastatur).
    let g = null;
    const touchPoint = (t) => ({ x: t.clientX, y: t.clientY });
    host.addEventListener('touchstart', (e) => {
      if (e.target.closest('.term-menu, .term-dialog')) return;
      this._closeMenu();
      if (e.touches.length === 2) {
        clearTimeout(g?.timer);
        const [a, b] = e.touches;
        g = { mode: 'pinch', d0: Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY), f0: this.fontSize };
        return;
      }
      if (e.touches.length !== 1) return;
      const p = touchPoint(e.touches[0]);
      g = { mode: 'wait', sx: p.x, sy: p.y, lx: p.x, ly: p.y, rest: 0, timer: setTimeout(() => {
        if (!g || g.mode !== 'wait') return;
        const c = this._cellAt(g.sx, g.sy), w = this._wordAt(c.col, c.row);
        g.mode = 'select'; g.anchor = { col: w.start, row: c.row }; g.anchorEnd = { col: w.start + w.length - 1, row: c.row };
        this.term.select(w.start, c.row, w.length);
        try { navigator.vibrate?.(12); } catch { /* egal */ }
      }, 450) };
    }, { passive: true, capture: true });
    host.addEventListener('touchmove', (e) => {
      if (!g) return;
      if (g.mode === 'pinch' && e.touches.length === 2) {
        const [a, b] = e.touches;
        this.setFontSize(g.f0 * Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY) / g.d0);
        e.preventDefault(); return;
      }
      if (e.touches.length !== 1) return;
      const p = touchPoint(e.touches[0]);
      if (g.mode === 'wait') {
        if (Math.hypot(p.x - g.sx, p.y - g.sy) < 9) return;
        clearTimeout(g.timer); g.mode = 'scroll';
      }
      if (g.mode === 'scroll') {
        const dy = p.y - g.ly; g.ly = p.y; g.lx = p.x;
        const now = performance.now();
        g.v = dy / Math.max(1, now - (g.lt || now - 16)); g.lt = now;       // Geschwindigkeit fuer das Nachlaufen
        this._scrollBy(-dy, p.x, p.y);
        e.preventDefault(); e.stopPropagation();
      } else if (g.mode === 'select') {
        const c = this._cellAt(p.x, p.y);
        const before = c.row < g.anchor.row || (c.row === g.anchor.row && c.col < g.anchor.col);
        this._selectRange(before ? g.anchorEnd : g.anchor, { col: c.col, row: c.row });
        e.preventDefault(); e.stopPropagation();
      }
    }, { passive: false, capture: true });
    const end = (e) => {
      if (!g) return;
      clearTimeout(g.timer);
      if (g.mode === 'scroll' && Math.abs(g.v || 0) > 0.3 && performance.now() - g.lt < 80) this._fling(-g.v * 16, g.lx, g.ly);   // Wischen: laeuft noch ein Stueck nach
      if (g.mode === 'wait' && e.type === 'touchend' && !e.touches.length) {
        // kurzes Tippen: bei Mausunterstuetzung des Programms ein Klick dorthin, sonst Tastatur (die Seite entscheidet) -- und kein Fokuswechsel durch xterm
        e.preventDefault();
        this._tap(g.sx, g.sy);
      }
      if (g.mode === 'select') {
        const p = { x: g.lx || g.sx, y: g.ly || g.sy };
        this._menu(e.changedTouches?.[0]?.clientX ?? p.x, e.changedTouches?.[0]?.clientY ?? p.y);
        e.preventDefault();
      }
      if (!e.touches.length) g = null;
    };
    host.addEventListener('touchend', end, { capture: true });
    host.addEventListener('touchcancel', end, { capture: true });
  }

  /* ---------- Warnung bei geaendertem Host-Schluessel ---------- */
  _hostKeyDialog(info) {
    const wrap = el('div', 'term-dialog');
    const box = el('div', 'box');
    box.setAttribute('role', 'alertdialog');
    box.appendChild(el('h2', '', t('term.keyChangedTitle')));
    box.appendChild(el('p', '', t('term.keyChangedBody')));
    const row = (label, value) => { const r = el('p', 'fp'); r.appendChild(el('span', '', label)); r.appendChild(el('code', '', value || '–')); box.appendChild(r); };
    row(t('term.keyPrevious'), info.previous);
    row(t('term.keyNow'), info.fingerprint);
    box.appendChild(el('p', 'hint', t('term.keyChangedHint')));
    const cancel = el('button', 'primary', t('common.cancel')); cancel.type = 'button';
    const trust = el('button', 'danger', t('term.keyTrust')); trust.type = 'button';
    cancel.addEventListener('click', () => { wrap.remove(); this._setState('error', { message: t('term.keyChangedAborted'), fatal: true }); });
    trust.addEventListener('click', () => { wrap.remove(); this.connect({ acceptHostKey: info.fingerprint }); });
    const actions = el('div', 'actions'); actions.append(cancel, trust);
    box.appendChild(actions);
    wrap.appendChild(box);
    this.host.appendChild(wrap);
    cancel.focus();
    this._setState('error', { message: t('term.keyChanged'), code: END_HOSTKEY, fatal: true, dialog: true });
  }
}
