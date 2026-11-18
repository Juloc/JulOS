/* =====================================================================
 * Bildschirm-Stream: der Server verbindet sich per RDP und schickt H.264, der Browser dekodiert es mit WebCodecs.
 * StreamClient spricht dieselbe kleine Schnittstelle wie der Guacamole-Client (connect, sendMouseState, sendKeyEvent, sendSize,
 * disconnect, getDisplay und die Rueckrufe onstatechange/onerror), damit Touch, Tastatur und Menues in app.js unveraendert bleiben.
 *
 * Nachrichten (alle binaer, Zahlen big endian), siehe ScreenProtocol auf dem Server:
 *   Server -> Browser: 1 Bild (flags, folge u32, H.264), 2 Zeiger, 3 Groesse, 4 Fehler, 5 uebernommen, 6 Zwischenablage (UTF-8-Text)
 *   Browser -> Server: 1 Bestaetigung, 2 Maus bewegt, 3 Maustaste, 4 Mausrad, 5 Taste (Scancode), 6 Zeichen, 7 Fenstergroesse, 8 Schluesselbild,
 *                      9 Zwischenablage (UTF-8-Text, bis 1 MiB; der ferne Rechner holt ihn, wenn er einfuegt)
 *
 * Zwischenablage: wie bei Guacamole als Strom (onclipboard(stream, 'text/plain') mit onblob/onend, createClipboardStream() mit sendBlob/sendEnd),
 * damit Guacamole.StringReader und StringWriter in app.js unveraendert funktionieren. Nur Text.
 * ===================================================================== */

const IN = { picture: 1, cursor: 2, size: 3, error: 4, taken: 5, clipboard: 6 };
const OUT = { ack: 1, move: 2, button: 3, wheel: 4, key: 5, unicode: 6, resize: 7, keyframe: 8, clipboard: 9 };

/* ---------- Tasten: X11-Keysyms (so arbeitet app.js) -> RDP-Scancodes ---------- */
// [scancode, erweitert]
const SPECIAL = new Map([
  [0xff1b, [0x01, 0]], [0xff09, [0x0f, 0]], [0xff08, [0x0e, 0]], [0xff0d, [0x1c, 0]], [0xff8d, [0x1c, 1]], [0x20, [0x39, 0]],
  [0xffff, [0x53, 1]], [0xff63, [0x52, 1]], [0xff50, [0x47, 1]], [0xff57, [0x4f, 1]], [0xff55, [0x49, 1]], [0xff56, [0x51, 1]],
  [0xff51, [0x4b, 1]], [0xff52, [0x48, 1]], [0xff53, [0x4d, 1]], [0xff54, [0x50, 1]],
  [0xffe1, [0x2a, 0]], [0xffe2, [0x36, 0]], [0xffe3, [0x1d, 0]], [0xffe4, [0x1d, 1]], [0xffe9, [0x38, 0]], [0xffea, [0x38, 1]], [0xfe03, [0x38, 1]],
  [0xffeb, [0x5b, 1]], [0xffec, [0x5c, 1]], [0xff67, [0x5d, 1]], [0xffe5, [0x3a, 0]], [0xff61, [0x37, 1]],
]);
for (let i = 0; i < 10; i++) SPECIAL.set(0xffbe + i, [0x3b + i, 0]);             // F1 bis F10
SPECIAL.set(0xffc8, [0x57, 0]); SPECIAL.set(0xffc9, [0x58, 0]);                    // F11, F12
const MODIFIERS = { 0xffe3: 'ctrl', 0xffe4: 'ctrl', 0xffe9: 'alt', 0xffea: 'alt', 0xffeb: 'super', 0xffec: 'super' };

// Buchstaben und Ziffern fuer Tastenkombinationen (Strg+C ...): die Taste der deutschen Belegung; auf der englischen sind y und z vertauscht
const LETTER_SCANCODES = { q: 0x10, w: 0x11, e: 0x12, r: 0x13, t: 0x14, z: 0x15, u: 0x16, i: 0x17, o: 0x18, p: 0x19,
  a: 0x1e, s: 0x1f, d: 0x20, f: 0x21, g: 0x22, h: 0x23, j: 0x24, k: 0x25, l: 0x26, y: 0x2c, x: 0x2d, c: 0x2e, v: 0x2f, b: 0x30, n: 0x31, m: 0x32,
  1: 0x02, 2: 0x03, 3: 0x04, 4: 0x05, 5: 0x06, 6: 0x07, 7: 0x08, 8: 0x09, 9: 0x0a, 0: 0x0b, ',': 0x33, '.': 0x34, '-': 0x35, '+': 0x1b, '#': 0x2b, '<': 0x56 };

// Zeichen, die nur die deutsche Belegung kennt: [Scancode, Umschalt, AltGr]. Manche Server (xrdp) nehmen fuer solche Zeichen keine
// Unicode-Eingabe an, deshalb gehen sie ueber ihre Taste; alles andere geht als Unicode.
const GERMAN_KEYS = {
  'ü': [0x1a, 0, 0], 'Ü': [0x1a, 1, 0], 'ö': [0x27, 0, 0], 'Ö': [0x27, 1, 0], 'ä': [0x28, 0, 0], 'Ä': [0x28, 1, 0], 'ß': [0x0c, 0, 0],
  '°': [0x29, 1, 0], '§': [0x04, 1, 0], '²': [0x03, 0, 1], '³': [0x04, 0, 1], '€': [0x12, 0, 1], 'µ': [0x32, 0, 1],
};

/* ---------- Anzeige: ein Canvas mit dem dekodierten Bild und ein zweites fuer den Mauszeiger des fernen Rechners ---------- */
class StreamDisplay {
  constructor() {
    this.element = document.createElement('div');
    this.element.style.cssText = 'position:absolute;left:0;top:0;transform-origin:0 0;';
    this.canvas = document.createElement('canvas');
    this.canvas.style.cssText = 'display:block;';
    this.ctx = this.canvas.getContext('2d', { alpha: false, desynchronized: true });
    this.cursorCanvas = document.createElement('canvas');
    this.cursorCanvas.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;display:none;';
    this.element.append(this.canvas, this.cursorCanvas);
    this.width = 0; this.height = 0; this.scaleValue = 1;
    this.cursorHot = { x: 0, y: 0 }; this.pointer = { x: 0, y: 0 }; this.cursorShown = false; this.softCursor = false;
    this.onresize = null; this.oncursor = null;
  }
  getElement() { return this.element; }
  getWidth() { return this.width; }
  getHeight() { return this.height; }
  getScale() { return this.scaleValue; }
  scale(s) { this.scaleValue = s; this.element.style.transform = `scale(${s})`; }
  flatten() { return this.canvas; }
  showCursor(on) { this.softCursor = !!on; this.updateCursor(); }

  resize(w, h) {
    if (w === this.width && h === this.height) return;
    this.width = w; this.height = h;
    this.canvas.width = w; this.canvas.height = h;
    if (this.onresize) this.onresize(w, h);
  }

  setPointer(x, y) { this.pointer.x = x; this.pointer.y = y; this.updateCursor(); }

  setCursorShape(hotX, hotY, w, h, rgba) {
    this.cursorCanvas.width = w; this.cursorCanvas.height = h;
    this.cursorCanvas.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(rgba), w, h), 0, 0);
    this.cursorHot = { x: hotX, y: hotY }; this.cursorShown = true;
    this.updateCursor();
    if (this.oncursor) this.oncursor(this.cursorCanvas, hotX, hotY);
  }
  hideCursor() { this.cursorShown = false; this.updateCursor(); }
  defaultCursor() {
    // Standardpfeil, wenn der ferne Rechner keine eigene Form meldet
    const c = this.cursorCanvas; c.width = 12; c.height = 19;
    const g = c.getContext('2d'); g.clearRect(0, 0, 12, 19);
    g.beginPath(); g.moveTo(0.5, 0.5); g.lineTo(0.5, 15.5); g.lineTo(4, 12); g.lineTo(7, 18.5); g.lineTo(9.5, 17.5); g.lineTo(6.5, 11.5); g.lineTo(11, 11.5); g.closePath();
    g.fillStyle = '#fff'; g.fill(); g.strokeStyle = '#000'; g.lineWidth = 1; g.stroke();
    this.cursorHot = { x: 0, y: 0 }; this.cursorShown = true; this.updateCursor();
    if (this.oncursor) this.oncursor(c, 0, 0);
  }
  updateCursor() {
    const on = this.softCursor && this.cursorShown;
    this.cursorCanvas.style.display = on ? 'block' : 'none';
    if (on) this.cursorCanvas.style.transform = `translate(${this.pointer.x - this.cursorHot.x}px, ${this.pointer.y - this.cursorHot.y}px)`;
  }
}

/* ---------- der Client ---------- */
let supportCheck = null;

export class StreamClient {
  /** Ob dieser Browser H.264 mit WebCodecs dekodieren kann (braucht eine sichere Seite: https oder localhost). */
  static supported() {
    if (!supportCheck) {
      supportCheck = (async () => {
        try {
          if (typeof VideoDecoder === 'undefined' || typeof EncodedVideoChunk === 'undefined') return false;
          const r = await VideoDecoder.isConfigSupported({ codec: 'avc1.42e01f', avc: { format: 'annexb' }, optimizeForLatency: true });
          return !!r.supported;
        } catch { return false; }
      })();
    }
    return supportCheck;
  }

  constructor(url, layout = 'de') {
    this.url = url;
    this.display = new StreamDisplay();
    this.onstatechange = null; this.onerror = null; this.onclipboard = null;
    this.state = 0;
    this.socket = null; this.decoder = null; this.codec = '';
    this.connected = false; this.closing = false;
    this.last = { x: -1, y: -1, left: false, middle: false, right: false, up: false, down: false };
    this.mods = { ctrl: false, alt: false, super: false };
    this.layout = layout;
    this.stats = { pictures: 0, dropped: 0 };
    this.failures = 0;
    this.drawn = false;                 // ob schon ein Bild gezeichnet wurde (davor darf app.js auf Guacamole ausweichen)
  }

  getDisplay() { return this.display; }

  setState(s) { if (this.state === s) return; this.state = s; if (this.onstatechange) this.onstatechange(s); }

  connect(query) {
    this.setState(1);
    const socket = new WebSocket(this.url + '?' + query);
    socket.binaryType = 'arraybuffer';
    this.socket = socket;
    socket.onmessage = (e) => { if (this.socket === socket) this.receive(e.data); };
    socket.onclose = () => { if (this.socket === socket) this.finish(); };
    socket.onerror = () => { if (this.socket === socket && !this.connected && this.onerror) this.onerror({ code: 520, message: 'Keine Verbindung zum Server.' }); };
  }

  disconnect() {
    this.closing = true;
    const s = this.socket; this.socket = null;
    if (s) { try { s.close(); } catch { /* egal */ } }
    this.closeDecoder();
    this.setState(5);
  }

  finish() {
    this.socket = null; this.closeDecoder();
    if (!this.closing) this.setState(5);
  }

  closeDecoder() {
    const d = this.decoder; this.decoder = null; this.codec = '';
    if (d && d.state !== 'closed') { try { d.close(); } catch { /* egal */ } }
  }

  /* ---- vom Server ---- */
  receive(buffer) {
    const v = new DataView(buffer);
    switch (v.getUint8(0)) {
      case IN.picture: this.picture(v, buffer); break;
      case IN.cursor: {
        const kind = v.getUint8(1);
        if (kind === 1) this.display.hideCursor();
        else if (kind === 2) this.display.defaultCursor();
        else {
          const w = v.getUint16(6), h = v.getUint16(8);
          if (w && h && buffer.byteLength >= 10 + w * h * 4) this.display.setCursorShape(v.getUint16(2), v.getUint16(4), w, h, new Uint8Array(buffer, 10, w * h * 4));
        }
        break;
      }
      case IN.size:
        this.display.resize(v.getUint16(1), v.getUint16(3));
        if (!this.connected) { this.connected = true; this.setState(3); }
        break;
      case IN.error: {
        const code = v.getUint16(1), message = new TextDecoder().decode(new Uint8Array(buffer, 3));
        this.closing = true; this.setState(5);
        if (this.onerror) this.onerror({ code, message });
        break;
      }
      case IN.taken:
        this.closing = true;
        if (this.onerror) this.onerror({ code: 777, message: 'Sitzung übernommen.' });
        this.setState(5);
        break;
      case IN.clipboard: this.incomingClipboard(new Uint8Array(buffer, 1)); break;
      default: break;
    }
  }

  // Text vom fernen Rechner: als Guacamole-artiger Strom (ein Blob, base64, dann Ende) an onclipboard
  incomingClipboard(bytes) {
    if (!this.onclipboard || !bytes.length) return;
    let bin = ''; for (let i = 0; i < bytes.length; i += 8192) bin += String.fromCharCode(...bytes.subarray(i, i + 8192));
    const stream = { onblob: null, onend: null };
    this.onclipboard(stream, 'text/plain');
    if (stream.onblob) stream.onblob(btoa(bin));
    if (stream.onend) stream.onend();
  }

  // Codec-Zeichenfolge aus dem SPS des Schluesselbilds (Profil, Einschraenkungen, Stufe)
  codecOf(bytes) {
    for (let i = 0; i + 6 < Math.min(bytes.length, 64); i++) {
      if (bytes[i] === 0 && bytes[i + 1] === 0 && (bytes[i + 2] === 1 || (bytes[i + 2] === 0 && bytes[i + 3] === 1))) {
        const n = bytes[i + 2] === 1 ? i + 3 : i + 4;
        if ((bytes[n] & 0x1f) === 7) return 'avc1.' + [bytes[n + 1], bytes[n + 2], bytes[n + 3]].map((b) => b.toString(16).padStart(2, '0')).join('');
      }
    }
    return 'avc1.42e01f';
  }

  picture(v, buffer) {
    const isKey = (v.getUint8(1) & 1) !== 0;
    const sequence = v.getInt32(2);
    const data = new Uint8Array(buffer, 6);
    if (isKey) {
      const codec = this.codecOf(data);
      if (!this.decoder || this.decoder.state === 'closed' || codec !== this.codec) this.openDecoder(codec);
    }
    const d = this.decoder;
    if (!d || d.state !== 'configured') { this.stats.dropped++; this.send1(OUT.keyframe); return; }   // ohne Schluesselbild nichts zu dekodieren: eins anfordern
    try {
      d.decode(new EncodedVideoChunk({ type: isKey ? 'key' : 'delta', timestamp: sequence, data }));
      this.stats.pictures++;
    } catch {
      this.decoderFailed();
    }
  }

  // Der Dekodierer ist ausgestiegen: ein neues Schluesselbild anfordern. Klappt es vor dem ersten gezeichneten Bild mehrmals nicht, geht es
  // hier nicht (Browser ohne passenden H.264-Dekodierer): dann Fehler melden, damit app.js auf Guacamole ausweichen kann.
  decoderFailed() {
    this.stats.dropped++; this.closeDecoder();
    if (!this.drawn && ++this.failures >= 3) {
      this.closing = true;
      const s = this.socket; this.socket = null;
      if (s) { try { s.close(); } catch { /* egal */ } }
      if (this.onerror) this.onerror({ code: 1001, message: 'Der Browser kann das Bild nicht dekodieren.' });
      this.setState(5);
      return;
    }
    this.send1(OUT.keyframe);
  }

  openDecoder(codec) {
    this.closeDecoder();
    const d = new VideoDecoder({
      output: (frame) => {
        const g = this.display;
        if (frame.displayWidth !== g.width || frame.displayHeight !== g.height) g.resize(frame.displayWidth, frame.displayHeight);
        g.ctx.drawImage(frame, 0, 0);
        frame.close();
        this.drawn = true;
        this.send1(OUT.ack);                       // erst nach dem Zeichnen: so laeuft der Server dem Browser nie davon
      },
      error: () => { if (this.decoder === d) this.decoderFailed(); },
    });
    d.configure({ codec, avc: { format: 'annexb' }, optimizeForLatency: true });
    this.decoder = d; this.codec = codec;
  }

  /* ---- zum Server ---- */
  send(bytes) { const s = this.socket; if (s && s.readyState === WebSocket.OPEN) s.send(bytes); }
  send1(tag) { this.send(Uint8Array.of(tag)); }

  sendSize(w, h) { const b = new DataView(new ArrayBuffer(5)); b.setUint8(0, OUT.resize); b.setUint16(1, w); b.setUint16(3, h); this.send(b.buffer); }

  // Maus: dieselben Zustandsobjekte wie bei Guacamole (x, y, Tasten, Rad hoch/runter); es gehen nur die Aenderungen hinaus
  sendMouseState(s) {
    const l = this.last;
    const x = Math.max(0, Math.min(65535, Math.round(s.x))), y = Math.max(0, Math.min(65535, Math.round(s.y)));
    if (x !== l.x || y !== l.y) {
      const b = new DataView(new ArrayBuffer(5)); b.setUint8(0, OUT.move); b.setUint16(1, x); b.setUint16(3, y); this.send(b.buffer);
      this.display.setPointer(x, y); l.x = x; l.y = y;
    }
    for (const [name, button] of [['left', 0], ['middle', 1], ['right', 2]]) {
      if (!!s[name] !== l[name]) { this.send(Uint8Array.of(OUT.button, button, s[name] ? 1 : 0)); l[name] = !!s[name]; }
    }
    for (const [name, delta] of [['up', 120], ['down', -120]]) {
      if (s[name] && !l[name]) { const b = new DataView(new ArrayBuffer(4)); b.setUint8(0, OUT.wheel); b.setInt16(1, delta); b.setUint8(3, 0); this.send(b.buffer); }
      l[name] = !!s[name];
    }
  }

  // Tasten: Sondertasten ueber ihren Scancode, Zeichen als Unicode (unabhaengig von der Belegung des fernen Rechners); mit Strg, Alt oder
  // Windows-Taste gehen Buchstaben und Ziffern ueber ihre Taste, damit Kombinationen wie Strg+C ankommen
  sendKeyEvent(pressed, keysym) {
    const mod = MODIFIERS[keysym]; if (mod) this.mods[mod] = !!pressed;
    const special = SPECIAL.get(keysym);
    if (special) { this.key(special[0], special[1], pressed); return; }
    const cp = keysym >= 0x01000000 ? keysym - 0x01000000 : keysym;
    if (cp < 0x20 || cp > 0x10ffff) return;
    const char = String.fromCodePoint(cp);
    if (this.mods.ctrl || this.mods.alt || this.mods.super) {
      let low = char.toLowerCase();
      let code = LETTER_SCANCODES[low];
      if (code !== undefined && this.layout !== 'de') { if (low === 'y') code = 0x15; else if (low === 'z') code = 0x2c; }
      if (code !== undefined) { this.key(code, 0, pressed); return; }
    }
    const german = this.layout === 'de' ? GERMAN_KEYS[char] : undefined;
    if (german) { if (pressed) this.tapLayoutKey(german); return; }
    if (pressed) for (const unit of char) { for (let i = 0; i < unit.length; i++) { this.send(Uint8Array.of(OUT.unicode, unit.charCodeAt(i) >> 8, unit.charCodeAt(i) & 255)); } }
  }

  tapLayoutKey([code, shift, altgr]) {
    if (altgr) this.key(0x38, 1, true);
    if (shift) this.key(0x2a, 0, true);
    this.key(code, 0, true); this.key(code, 0, false);
    if (shift) this.key(0x2a, 0, false);
    if (altgr) this.key(0x38, 1, false);
  }

  key(scancode, extended, pressed) { this.send(Uint8Array.of(OUT.key, scancode, (extended ? 1 : 0) | (pressed ? 2 : 0))); }

  // Text fuer die Zwischenablage des fernen Rechners: die base64-Teile (UTF-8) werden gesammelt und am Ende als eine Nachricht gesendet
  createClipboardStream(mimetype) {
    const parts = [];
    const client = this;
    return {
      sendBlob(base64) {
        if (!/^text\//.test(mimetype || 'text/plain')) return;
        const bin = atob(base64); const part = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) part[i] = bin.charCodeAt(i);
        parts.push(part);
      },
      sendEnd() {
        const size = parts.reduce((n, p) => n + p.length, 0);
        if (!size) return;
        const message = new Uint8Array(1 + size); message[0] = OUT.clipboard;
        let at = 1; for (const p of parts) { message.set(p, at); at += p.length; }
        parts.length = 0;
        client.send(message);
      },
    };
  }
}
