/* =====================================================================
 * Bildschirm-Stream: der Server verbindet sich per RDP und schickt H.264, der Browser dekodiert es mit WebCodecs.
 * StreamClient ist die Schnittstelle der Sitzung (app.js) zum Server: connect, sendMouseState, sendKeyEvent, sendSize, disconnect, getDisplay und
 * die Rueckrufe onstatechange (Zustaende 3 verbunden, 5 getrennt) und onerror ({ code, message }, die Codes stehen in ScreenProtocol.cs).
 *
 * Nachrichten (alle binaer, Zahlen big endian), siehe ScreenProtocol auf dem Server:
 *   Server -> Browser: 1 Bild (flags, folge u32, H.264), 2 Zeiger, 3 Groesse, 4 Fehler, 5 uebernommen, 6 Zwischenablage (UTF-8-Text),
 *                      7 Bild aus der VM (format 1 PNG / 2 BMP), 8 Eintraege (Dateien und Ordner) der VM, 9 Dateistand (bereit mit Token, laeuft, fertig, gescheitert), 10 Ergebnis eines Sendens,
 *                      11 Sitzung (sekunden u16, Token: so lange erhaelt der Server die Sitzung ohne Browser; mit dem Token geht connect('resume=...')),
 *                      12 Token unbekannt (neu aufbauen),
 *                      14 Grenzen (maxDatei u64, maxDateien u16, maxEintraege u16: so viel darf der Browser an die Zwischenablage der VM senden),
 *                      30 Kodierer (codec, GPU ja/nein, Name: nur zum Anzeigen); ein Bild hat in flags Bit 1 gesetzt, wenn es H.265 ist
 *                      40 Nachbesserung (x, y, breite, hoehe u16, PNG: ein ruhendes Rechteck verlustfrei, kommt ueber das Video),
 *                      41 Nachbesserung weg (anzahl u16, je x, y, breite, hoehe: dort hat sich der Bildschirm geaendert)
 *                      50 Lebenszeichen moeglich (version u8: der Server beantwortet 50 des Browsers; erst danach schickt der Browser Pings, siehe link-health.js),
 *                      51 Pong (die Nummer des Pings u32)
 *   Browser -> Server: 1 Bestaetigung (Folgenummer u32: alle Bilder bis dahin), 2 Maus bewegt, 3 Maustaste (0 links, 1 Mitte, 2 rechts, 3 zurueck, 4 vor), 4 Mausrad (Betrag i16 in Windows-Einheiten, 120 je Raste; Byte: waagerecht), 5 Taste (Scancode), 6 Zeichen, 7 Fenstergroesse, 8 Schluesselbild,
 *                      9 Zwischenablage (UTF-8-Text, bis 1 MiB; der ferne Rechner holt ihn, wenn er einfuegt),
 *                      10 Senden Anfang (Bild als BMP, Datei oder leerer Ordner; bei Dateien ein relativer Pfad), 11 Stueck, 12 Ende, 13 Abbruch, 14 Datei oder Ordner der VM holen (Pfad), 15 Holen Abbruch,
 *                      31 Bildrate (Stufe: 0 automatisch, 1 hoechstens 60 Bilder/s, 2 hoechstens 30, 3 sparsam; je Sitzung),
 *                      16 Pause (1 = die Ansicht ist nicht zu sehen: der Server kodiert nicht, 0 = weiter),
 *                      17 Beenden (die Sitzung wird geschlossen: der Server erhaelt sie nicht fuer ein Wiederverbinden),
 *                      18 Keysym (u32 X11-Keysym, Byte unten; nur bei VNC-Sitzungen, sonst gehen Tasten als 5 Scancode und 6 Zeichen),
 *                      30 Codecs (Menge: 1 H.264, 2 H.265; beim Verbinden steht sie in der Adresse als codecs=, danach nur noch bei einem Wechsel zurueck auf H.264),
 *                      50 Ping (Nummer u32; nur nach Nachricht 50 des Servers, ein alter Server ignoriert sie ohnehin)
 *
 * Ton (20 und 21 in beide Richtungen: Ton vom fernen Rechner, Zustand, "Ton an", Mikrofon): siehe stream-audio.js, erreichbar als client.audio.
 *
 * Zwischenablage: Text kommt als onclipboard(text) und geht mit sendClipboardText(text) hinaus (Rueckgabe: 'sent', 'empty', 'tooLong' oder 'offline', nie still verloren).
 * Nachricht 15 (Ereignis der Text-Zwischenablage: Stand des Kanals der VM, Art, Zahl; nie Inhalt) kommt als onclipnote({ ready, files, note, count }). Bilder und Dateien haben eigene Rueckrufe
 * (onclipimage, onclipfiles, onfilestate) und Methoden (sendTransfer, requestDownload); das Format steht in docs/CLIPBOARD.md und ScreenProtocol.cs.
 * ===================================================================== */

import { StreamAudio } from './stream-audio.js';
import { StreamPerf, normalizeQuality, qualityOf } from './perf.js';
import { codecMask, probeCodecs, codecStringOf, decoderConfig, CODEC_H264, CODEC_HEVC } from './codecs.js';
import { t } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import { LinkHealth, PROBE_MS } from './link-health.js';

const IN = { picture: 1, cursor: 2, size: 3, error: 4, taken: 5, clipboard: 6, image: 7, files: 8, fileState: 9, result: 10, session: 11, resumeFailed: 12, limits: 14, clipNote: 15, audio: 20, audioStatus: 21, encoder: 30, refine: 40, refineClear: 41, link: 50, pong: 51 };
const OUT = { ack: 1, move: 2, button: 3, wheel: 4, key: 5, unicode: 6, resize: 7, keyframe: 8, clipboard: 9,
  sendBegin: 10, sendChunk: 11, sendEnd: 12, sendCancel: 13, fileRequest: 14, fileCancel: 15, pause: 16, end: 17, keysym: 18, codecs: 30, quality: 31, ping: 50 };
export const CLIP_TEXT_MAX = 1024 * 1024;   // so lang darf Text auf dem Weg zwischen Browser und Server sein (ClipboardBridge.MaxTextBytes)
const CHUNK = 128 * 1024;                 // so gross sind die Stuecke beim Senden (der Server nimmt bis 256 kB)
const BUFFER_HIGH = 1024 * 1024;          // darueber wartet das Senden, bis der Socket leer laeuft (sonst steht die Maus hinter einer Datei)

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

// Buchstaben und Ziffern fuer Tastenkombinationen (Strg+C ...): die Taste der deutschen Belegung; auf der englischen sind y und z vertauscht,
// auf der franzoesischen (AZERTY) a/q, z/w und m (siehe LETTER_OVERRIDES, je nach Layout der Verbindung)
const LETTER_SCANCODES = { q: 0x10, w: 0x11, e: 0x12, r: 0x13, t: 0x14, z: 0x15, u: 0x16, i: 0x17, o: 0x18, p: 0x19,
  a: 0x1e, s: 0x1f, d: 0x20, f: 0x21, g: 0x22, h: 0x23, j: 0x24, k: 0x25, l: 0x26, y: 0x2c, x: 0x2d, c: 0x2e, v: 0x2f, b: 0x30, n: 0x31, m: 0x32,
  1: 0x02, 2: 0x03, 3: 0x04, 4: 0x05, 5: 0x06, 6: 0x07, 7: 0x08, 8: 0x09, 9: 0x0a, 0: 0x0b, ',': 0x33, '.': 0x34, '-': 0x35, '+': 0x1b, '#': 0x2b, '<': 0x56 };

// Abweichungen der Buchstabentasten je Layout der Verbindung ("de" und "qwertz" haben keine; "en" ist der alte Name von "qwerty")
const QWERTY = { y: 0x15, z: 0x2c };
const LETTER_OVERRIDES = { qwerty: QWERTY, en: QWERTY, azerty: { y: 0x15, z: 0x11, w: 0x2c, a: 0x10, q: 0x1e, m: 0x27 } };

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
    // Nachbesserung: ruhende Teile des Bildschirms kommen zusaetzlich verlustfrei (PNG) und liegen ueber dem Video (dessen Farbe ist 4:2:0 und nie ganz scharf)
    this.overlay = document.createElement('canvas');
    this.overlay.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;';
    this.overlayCtx = this.overlay.getContext('2d');
    this.overlayChain = Promise.resolve(); this.overlayEpoch = 0;
    this.cursorCanvas = document.createElement('canvas');
    this.cursorCanvas.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;display:none;';
    this.element.append(this.canvas, this.overlay, this.cursorCanvas);
    this.width = 0; this.height = 0; this.scaleValue = 1; this.drawnOnce = false;
    this.cursorHot = { x: 0, y: 0 }; this.pointer = { x: 0, y: 0 }; this.cursorShown = false; this.softCursor = false;
    this.onresize = null; this.oncursor = null;
  }
  getElement() { return this.element; }
  getWidth() { return this.width; }
  getHeight() { return this.height; }
  getScale() { return this.scaleValue; }
  scale(s) { this.scaleValue = s; this.element.style.transform = `scale(${s})`; }
  // Video und Nachbesserung zusammengesetzt (eine Kopie)
  flatten() {
    const c = document.createElement('canvas'); c.width = this.canvas.width; c.height = this.canvas.height;
    const g = c.getContext('2d'); g.drawImage(this.canvas, 0, 0); g.drawImage(this.overlay, 0, 0);
    return c;
  }

  // Die Reihenfolge der Nachrichten bleibt erhalten, auch wenn das Dekodieren eines PNG laenger dauert als das Streichen danach
  refine(x, y, bytes) {
    const epoch = this.overlayEpoch;
    this.overlayChain = this.overlayChain.then(async () => {
      try {
        const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
        if (epoch === this.overlayEpoch) this.overlayCtx.drawImage(bitmap, x, y);
        bitmap.close();
      } catch { /* kein Bild: das Video bleibt, wie es ist */ }
    });
  }
  refineClear(rects) {
    const epoch = this.overlayEpoch;
    this.overlayChain = this.overlayChain.then(() => { if (epoch === this.overlayEpoch) for (const [x, y, w, h] of rects) this.overlayCtx.clearRect(x, y, w, h); });
  }
  showCursor(on) { this.softCursor = !!on; this.updateCursor(); }

  resize(w, h) {
    if (w === this.width && h === this.height) return;
    // Ein Canvas wird beim Aendern der Groesse leer. Das alte Bild bleibt gestreckt stehen, bis das erste Bild in der neuen Groesse da ist (kein Schwarzbild).
    let old = null;
    if (this.width && this.drawnOnce) { old = document.createElement('canvas'); old.width = this.canvas.width; old.height = this.canvas.height; old.getContext('2d').drawImage(this.canvas, 0, 0); }
    this.width = w; this.height = h;
    this.canvas.width = w; this.canvas.height = h;
    if (old) this.ctx.drawImage(old, 0, 0, old.width, old.height, 0, 0, w, h);
    this.overlay.width = w; this.overlay.height = h; this.overlayEpoch++;
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
let codecSupport = { h264: false, hevc: false };   // was isConfigSupported sagte; gilt, sobald supported() einmal fertig ist

export class StreamClient {
  /**
   * Ob dieser Browser H.264 mit WebCodecs dekodieren kann (braucht eine sichere Seite: https oder localhost). Dabei wird auch gepraeft,
   * ob er H.265 (hvc1, mit Hardware) kann; das geht beim Verbinden an den Server (siehe codecs()).
   */
  static supported() {
    if (!supportCheck) {
      supportCheck = (async () => {
        try {
          if (typeof VideoDecoder === 'undefined' || typeof EncodedVideoChunk === 'undefined') return false;
          codecSupport = await probeCodecs(VideoDecoder);
          return codecSupport.h264;
        } catch { return false; }
      })();
    }
    return supportCheck;
  }

  /** Was dieser Browser dekodieren kann: { h264, hevc } (erst nach supported() gueltig). */
  static codecs() { return { ...codecSupport }; }

  /** opts.keysyms: VNC-Sitzung, Tasten gehen als X11-Keysym (Nachricht 18) statt als Scancode oder Zeichen hinaus. */
  constructor(url, layout = 'de', opts = {}) {
    this.url = url;
    this.keysyms = !!opts.keysyms;
    this.heldKeysyms = new Set();   // gedrueckte Keysyms (VNC), damit sie beim Fokusverlust losgelassen werden koennen
    this.display = opts.display || new StreamDisplay();   // opts.display: beim stillen Wiederaufnehmen bleibt die Ansicht mit dem letzten Bild stehen
    this.resumed = !!opts.resumed;
    this.link = new LinkHealth(); this.linkTimer = 0; this.pingId = 0;   // Lebenszeichen der Leitung (link-health.js)
    this.onstatechange = null; this.onerror = null; this.onclipboard = null;
    this.onsession = null; this.onresumefailed = null;                            // Sitzungserhalt: Token des Servers, Token abgelehnt (siehe resume.js)
    this.onclipimage = null; this.onclipfiles = null; this.onclipresult = null; this.onclipnote = null;   // Bilder, Dateien und Meldungen der VM (siehe docs/CLIPBOARD.md)
    this.nextTransfer = 1; this.transfers = new Map();
    this.onfilestate = null;                       // Stand eines Downloads aus der VM: { batch, state: 'ready' | 'running' | 'done' | 'failed', done, total, text }
    this.limits = { fileBytes: 2 * 1024 ** 3, files: 200, entries: 255 };   // bis der Server die echten nennt (Nachricht 14)
    this.state = 0;
    this.socket = null; this.decoder = null; this.codec = '';
    this.connected = false; this.closing = false;
    this.last = { x: -1, y: -1, left: false, middle: false, right: false, back: false, forward: false, up: false, down: false };
    this.focusLost = (e) => { if (e.type === 'blur' || document.hidden) this.releaseAll(); };
    this.held = new Set();      // gedrueckte Tasten (Scancode und Erweiterung), damit sie beim Fokusverlust losgelassen werden koennen
    this.mods = { ctrl: false, alt: false, super: false };
    this.layout = layout;
    this.stats = { pictures: 0, dropped: 0, bytes: 0 };
    this.failures = 0;
    this.audio = new StreamAudio((bytes) => this.send(bytes));
    this.submitted = 0; this.output = 0; this.activityAt = 0;   // Folgenummern: dem Dekodierer gegeben, von ihm geliefert; Zeit des letzten Wechsels
    this.received = 0;                  // Folgenummer des zuletzt empfangenen Bildes (der Server zaehlt aufsteigend, ohne Luecken)
    this.refineOn = !/off/.test(localStorage.getItem('pd.refine') || '');   // zum Vergleichen: localStorage pd.refine = "off" ignoriert die Nachbesserung
    this.paused = false;                // die Ansicht ist im Hintergrund: der Server kodiert dann keine Bilder
    this.drawn = false;                 // ob schon ein Bild gezeichnet wurde (davor meldet ein Dekodierer-Fehler, dass es hier nicht geht)
    this.hevcOk = !!codecSupport.hevc;  // ob H.265 angeboten wird; nach einem Fehler des H.265-Dekodierers nicht mehr
    this.hevc = false;                  // ob der Dekodierer gerade H.265 dekodiert
    this.encoder = null;                // was der Server meldet: { codec, hardware, name }
    this.perf = new StreamPerf();       // Zaehler fuer die Leistungsanzeige (perf.js, docs/STATUSBAR.md)
    this.quality = 'auto';              // gewaehlte Bildrate-Stufe (Nachricht 31)
  }

  getDisplay() { return this.display; }

  // Ansicht im Hintergrund (andere Sitzung vorn, Fenster minimiert, Seite versteckt): der Server hoert auf zu kodieren, der Browser dekodiert nichts.
  // Beim Zurueckkommen schickt der Server alles, was sich inzwischen geaendert hat, als naechstes Bild (der Kodierer hat keines uebersprungen).
  setPaused(on) {
    on = !!on;
    if (this.paused === on) return;
    this.paused = on;
    if (this.connected) this.send(Uint8Array.of(OUT.pause, on ? 1 : 0));
  }

  // Bildrate und Bitrate dieser Sitzung waehlen (Nachricht 31): 'auto' | '60' | '30' | 'eco'. Der Server behaelt die Stufe bis zur naechsten Nachricht;
  // ein neuer Client schickt sie nach dem Verbinden erneut (die Sitzung der Seite kennt die Wahl).
  setQuality(id) {
    this.quality = normalizeQuality(id);
    if (this.connected) this.send(Uint8Array.of(OUT.quality, qualityOf(this.quality).byte));
  }

  // Ein Dekodierer, der Bilder angenommen hat und sie nicht mehr herausgibt (manche halten Bilder zurueck, weil sie auf spaetere warten):
  // nach einer Sekunde neu oeffnen und ein Schluesselbild holen, statt dass der Server auf die Bestaetigung wartet.
  watchDecoder() {
    if (this.decoder && this.submitted > this.output && performance.now() - this.activityAt > 1000) { this.stats.stalls = (this.stats.stalls || 0) + 1; this.decoderFailed(); }
  }

  setState(s) { if (this.state === s) return; this.state = s; if (this.onstatechange) this.onstatechange(s); }

  connect(query) {
    this.setState(1);
    // Welche Codecs der Browser dekodiert, sagt er dem Server gleich in der Adresse (der Kodierer wird danach gewaehlt)
    this.hevcOk = !!codecSupport.hevc;
    const socket = new WebSocket(this.url + '?' + query + '&codecs=' + codecMask({ hevc: this.hevcOk }));
    socket.binaryType = 'arraybuffer';
    this.socket = socket;
    this.watch = setInterval(() => this.watchDecoder(), 500);
    this.link.reset(); clearInterval(this.linkTimer); this.linkTimer = setInterval(() => this.linkTick(), 1000);
    window.addEventListener('blur', this.focusLost); document.addEventListener('visibilitychange', this.focusLost);
    socket.onmessage = (e) => { if (this.socket === socket) this.receive(e.data); };
    socket.onclose = (e) => { if (this.socket === socket) { this.lastClose = { code: e.code, clean: e.wasClean, at: Date.now(), reason: e.wasClean ? 'closed' : 'dropped' }; this.finish(); } };   // lastClose: nur fuer die Diagnose (app.js, ws-close)
    socket.onerror = () => { if (this.socket === socket && !this.connected && this.onerror) this.onerror({ code: 520, message: t('stream.noServer') }); };
  }

  // keep: nur die Leitung kappen (der Server haelt die Sitzung, ein neuer Client nimmt sie mit dem Token wieder auf); sonst "Beenden" melden
  disconnect(keep = false) {
    clearInterval(this.watch); clearInterval(this.linkTimer); this.unwatchFocus();
    this.closing = true;
    if (!keep) this.send1(OUT.end);
    const s = this.socket; this.socket = null;
    if (s) { try { s.close(); } catch { /* egal */ } }
    this.closeDecoder();
    this.audio.close();
    this.setState(5);
  }

  finish() {
    clearInterval(this.watch); clearInterval(this.linkTimer); this.unwatchFocus();
    this.socket = null; this.closeDecoder(); this.audio.close();
    for (const tr of this.transfers.values()) tr.result({ code: 4, text: t('stream.connectionEnded') });
    if (!this.closing) this.setState(5);
  }

  closeDecoder() {
    this.submitted = this.output = 0;
    const d = this.decoder; this.decoder = null; this.codec = ''; this.hevc = false;
    if (d && d.state !== 'closed') { try { d.close(); } catch { /* egal */ } }
  }

  /* ---- Lebenszeichen ---- */
  linkTick() {
    const a = this.link.tick();
    if (a === 'ping') this.sendPing(); else if (a === 'dead') this.dropLine('silence');
  }
  sendPing() { const b = new DataView(new ArrayBuffer(5)); b.setUint8(0, OUT.ping); b.setUint32(1, ++this.pingId >>> 0); this.send(b.buffer); }

  /** Die Leitung gibt kein Lebenszeichen mehr: aktiv schliessen und wie bei einem Abriss melden (ein halbtoter Socket meldet sein Ende sonst erst nach Minuten). */
  dropLine(reason) {
    this.lastClose = { code: 0, clean: false, at: Date.now(), reason };
    const s = this.socket; this.socket = null;
    if (s) { s.onmessage = s.onclose = s.onerror = null; try { s.close(); } catch { /* egal */ } }
    this.finish();
  }

  /** Seite wieder sichtbar nach awayMs: 'ping' (Probe laeuft, bei Stille meldet sich linkTick mit dropLine), 'reconnect' (Server ohne Lebenszeichen: wie bisher neu verbinden) oder null. */
  probe(awayMs) {
    const a = this.link.visible(awayMs);
    if (a === 'ping') { this.sendPing(); setTimeout(() => this.linkTick(), PROBE_MS + 60); }       // die Frist des Probe-Pings nicht erst beim naechsten Sekundentakt pruefen
    return a;
  }

  /* ---- vom Server ---- */
  receive(buffer) {
    this.link.touchRx();
    this.perf.received(buffer.byteLength);
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
        if (!this.connected) { this.connected = true; this.setState(3); if (this.resumed) this.send1(OUT.keyframe); if (this.paused) this.send(Uint8Array.of(OUT.pause, 1)); if (this.quality !== 'auto') this.setQuality(this.quality); }
        break;
      case IN.error: {
        const code = v.getUint16(1), message = new TextDecoder().decode(new Uint8Array(buffer, 3));
        this.closing = true; this.setState(5);
        // 800: das Zertifikat des Ziels ist nicht das gemerkte (Text: Fingerabdruck jetzt, Zeilenumbruch, Fingerabdruck bisher); es wurde nichts gesendet
        const [fingerprint, previous] = code === 800 ? message.split('\n') : [];
        if (this.onerror) this.onerror(code === 800 ? { code, message: t('stream.certChanged'), fingerprint, previous } : { code, message: serverMsg(message) });
        break;
      }
      case IN.link: this.link.enable(); break;
      case IN.pong: break;
      case IN.session: if (this.onsession && buffer.byteLength > 3) this.onsession({ keep: v.getUint16(1), token: new TextDecoder().decode(new Uint8Array(buffer, 3)) }); break;
      case IN.resumeFailed:
        this.closing = true;
        if (this.onresumefailed) this.onresumefailed();
        break;
      case IN.taken:
        this.closing = true;
        if (this.onerror) this.onerror({ code: 777, message: t('stream.taken') });
        this.setState(5);
        break;
      case IN.encoder:
        this.encoder = { codec: v.getUint8(1) === 2 ? 'H.265' : 'H.264', hardware: (v.getUint8(2) & 1) !== 0, name: new TextDecoder().decode(new Uint8Array(buffer, 3)) };
        this.stats.encoder = `${this.encoder.name} (${this.encoder.codec}${this.encoder.hardware ? ', GPU' : ''})`;
        break;
      case IN.refine:
        if (this.refineOn && buffer.byteLength > 9) this.display.refine(v.getUint16(1), v.getUint16(3), new Uint8Array(buffer, 9));
        break;
      case IN.refineClear:
        if (this.refineOn) {
          const n = v.getUint16(1), rects = [];
          for (let i = 0; i < n && 3 + i * 8 + 8 <= buffer.byteLength; i++) rects.push([v.getUint16(3 + i * 8), v.getUint16(5 + i * 8), v.getUint16(7 + i * 8), v.getUint16(9 + i * 8)]);
          this.display.refineClear(rects);
        }
        break;
      case IN.clipboard: this.incomingClipboard(new Uint8Array(buffer, 1)); break;
      case IN.audio: this.audio.frame(buffer); break;
      case IN.audioStatus: this.audio.status(buffer); break;
      case IN.image: if (this.onclipimage && buffer.byteLength > 2) this.onclipimage(v.getUint8(1), new Uint8Array(buffer, 2)); break;
      case IN.files: this.incomingFiles(v, buffer); break;
      case IN.fileState: this.incomingFileState(v, buffer); break;
      case IN.clipNote: if (this.onclipnote && buffer.byteLength >= 7) this.onclipnote({ ready: (v.getUint8(1) & 1) !== 0, files: (v.getUint8(1) & 2) !== 0, acks: (v.getUint8(1) & 4) !== 0, note: v.getUint8(2), count: v.getInt32(3) }); break;
      case IN.limits: if (buffer.byteLength >= 13) this.limits = { fileBytes: Number(v.getBigInt64(1)), files: v.getUint16(9), entries: v.getUint16(11) }; break;
      case IN.result: {
        const id = v.getUint32(1), code = v.getUint8(5), text = serverMsg(new TextDecoder().decode(new Uint8Array(buffer, 6)));
        const tr = this.transfers.get(id);
        if (tr) tr.result({ code, text }); else if (this.onclipresult) this.onclipresult({ id, code, text });
        break;
      }
      default: break;
    }
  }

  // Text vom fernen Rechner (UTF-8) an onclipboard
  incomingClipboard(bytes) {
    if (!this.onclipboard || !bytes.length) return;
    this.onclipboard(new TextDecoder().decode(bytes));
  }

  // Dateien und Ordner, die der ferne Rechner kopiert hat: nur die Liste (Pfad mit /, Groesse, Ordner), der Inhalt kommt erst mit requestDownload
  incomingFiles(v, buffer) {
    const batch = v.getUint32(1), count = v.getUint16(5), files = [];
    let at = 7;
    for (let i = 0; i < count && at + 11 <= buffer.byteLength; i++) {
      const dir = (v.getUint8(at) & 1) !== 0, size = Number(v.getBigInt64(at + 1)), n = v.getUint16(at + 9);
      if (at + 11 + n > buffer.byteLength) break;
      files.push({ size, dir, name: new TextDecoder().decode(new Uint8Array(buffer, at + 11, n)) });
      at += 11 + n;
    }
    if (this.onclipfiles) this.onclipfiles(batch, files);
  }

  // Stand eines Downloads: bereit (der Text ist das Token: der Browser holt ihn selbst als HTTP-Strom ab), laeuft, fertig oder gescheitert
  incomingFileState(v, buffer) {
    if (buffer.byteLength < 22) return;
    const state = ['ready', 'running', 'done', 'failed'][v.getUint8(5)];
    if (state && this.onfilestate) this.onfilestate({ batch: v.getUint32(1), state, done: Number(v.getBigInt64(6)), total: Number(v.getBigInt64(14)), text: serverMsg(new TextDecoder().decode(new Uint8Array(buffer, 22))) });
  }

  // Eine Datei oder einen Ordner der VM holen (path aus der zuletzt gemeldeten Liste, leer = alles). Die Antwort kommt als onfilestate: erst "ready".
  requestDownload(batch, path) {
    const p = new TextEncoder().encode(path || '');
    const m = new Uint8Array(5 + p.length); new DataView(m.buffer).setUint32(1, batch); m[0] = OUT.fileRequest; m.set(p, 5);
    this.send(m);
  }

  cancelDownload(batch) {
    const m = new Uint8Array(5); m[0] = OUT.fileCancel; new DataView(m.buffer).setUint32(1, batch);
    this.send(m);
  }

  /* Ein Bild (kind 1, als BMP), eine Datei (kind 2) oder einen leeren Ordner (kind 3, data null) an die Zwischenablage der VM senden; Dateien und Ordner sind Teil
     eines Stapels (index von count), name ist ihr relativer Pfad mit /. data ist ein Blob; signal bricht ab; progress(gesendet, gesamt). Antwort: { code, text } des Servers (code 0 = angenommen). */
  async sendTransfer(kind, data, name, index, count, progress, signal) {
    const socket = this.socket;
    if (!socket || socket.readyState !== 1) return { code: 4, text: t('stream.noConnection') };
    const id = this.nextTransfer++;
    const nameBytes = new TextEncoder().encode(name || '').slice(0, 1200);
    const total = data ? data.size : 0;
    const begin = new Uint8Array(16 + nameBytes.length), view = new DataView(begin.buffer);
    begin[0] = OUT.sendBegin; view.setUint32(1, id); begin[5] = kind; begin[6] = index; begin[7] = count; view.setBigUint64(8, BigInt(total));
    begin.set(nameBytes, 16);
    const answer = new Promise((resolve) => this.transfers.set(id, { result: resolve }));
    let early = null; answer.then((r) => { early = r; });
    const control = (type) => { const m = new Uint8Array(5); m[0] = type; new DataView(m.buffer).setUint32(1, id); return m; };
    try {
      this.send(begin);
      for (let at = 0; at < total; at += CHUNK) {
        // ein Fehler gleich nach dem Anfang (zu gross, nicht erlaubt ...) beendet das Senden, ohne die Daten noch zu schicken
        if ((at / CHUNK) % 4 === 0) await new Promise((r) => setTimeout(r, 0));   // dem Socket Luft lassen: eine Antwort des Servers (Fehler) kommt nur zwischen den Stuecken an
        if (early) return early;
        if (signal && signal.aborted) { this.send(control(OUT.sendCancel)); return { code: 4, text: t('stream.cancelled') }; }
        while (socket.bufferedAmount > BUFFER_HIGH) {
          await new Promise((r) => setTimeout(r, 20));
          if (this.socket !== socket) return { code: 4, text: t('stream.noConnection') };
        }
        const part = new Uint8Array(await data.slice(at, at + CHUNK).arrayBuffer());
        const m = new Uint8Array(5 + part.length); m[0] = OUT.sendChunk; new DataView(m.buffer).setUint32(1, id); m.set(part, 5);
        this.send(m);
        if (progress) progress(Math.min(at + CHUNK, total), total);
      }
      if (early) return early;
      this.send(control(OUT.sendEnd));
      let timer; const silent = new Promise((r) => { timer = setTimeout(() => r({ code: 4, text: t('stream.vmSilent') }), 20000); });
      try { return await Promise.race([answer, silent]); } finally { clearTimeout(timer); }
    } finally { this.transfers.delete(id); }
  }

  // Codec-Zeichenfolge aus dem SPS des Schluesselbilds (Profil, Einschraenkungen, Stufe; bei H.265 auch Kompatibilitaet und Stufe/Ebene), siehe codecs.js
  codecOf(bytes, hevc = false) { return codecStringOf(bytes, hevc); }

  // Jede Bestaetigung gilt fuer das Bild mit dieser Folgenummer und alle davor. So steht jedes Bild, das nie gezeichnet wird (verworfen,
  // Dekodierer neu geoeffnet oder ausgestiegen), nicht dauerhaft im Fenster des Servers: ein spaeteres Bild oder diese Notbestaetigung
  // raeumt es auf.
  ack(sequence) { const b = new DataView(new ArrayBuffer(5)); b.setUint8(0, OUT.ack); b.setInt32(1, sequence); this.send(b.buffer); }

  picture(v, buffer) {
    const flags = v.getUint8(1);
    const isKey = (flags & 1) !== 0, hevc = (flags & 2) !== 0;
    const sequence = v.getInt32(2);
    this.received = sequence;
    const data = new Uint8Array(buffer, 6);
    // H.265 vom Server, obwohl der Dekodierer dafuer ausgestiegen ist (ein Bild war schon unterwegs): verwerfen; H.264 folgt mit einem Schluesselbild
    if (hevc && !this.hevcOk) { this.stats.dropped++; this.ack(sequence); return; }
    if (isKey) {
      const codec = this.codecOf(data, hevc);
      if (!this.decoder || this.decoder.state === 'closed' || codec !== this.codec) {
        try { this.openDecoder(codec, sequence - 1); } catch { this.decoderFailed(); return; }
      }
    }
    const d = this.decoder;
    if (!d || d.state !== 'configured') { this.stats.dropped++; this.ack(sequence); this.send1(OUT.keyframe); return; }   // ohne Schluesselbild nichts zu dekodieren: eins anfordern
    try {
      d.decode(new EncodedVideoChunk({ type: isKey ? 'key' : 'delta', timestamp: sequence, data }));
      if (this.submitted <= this.output) this.activityAt = performance.now();
      this.submitted = sequence;
      this.stats.pictures++; this.stats.bytes += data.length;
    } catch {
      this.decoderFailed();
    }
  }

  // Der Dekodierer ist ausgestiegen: ein neues Schluesselbild anfordern. Klappt es vor dem ersten gezeichneten Bild mehrmals nicht, geht es
  // hier nicht (Browser ohne passenden H.264-Dekodierer): dann Fehler melden, damit app.js eine klare Meldung zeigt.
  decoderFailed() {
    const wasHevc = this.hevc;
    this.stats.dropped++; this.closeDecoder();
    this.ack(this.received);                       // was im Dekodierer steckte, kommt nie heraus
    if (wasHevc) {
      // Der H.265-Dekodierer geht nicht (oder haelt Bilder zurueck): ab jetzt H.264. Der Server wechselt den Kodierer und sendet ein Schluesselbild.
      this.hevcOk = false; this.stats.hevcFallback = (this.stats.hevcFallback || 0) + 1;
      this.send(Uint8Array.of(OUT.codecs, CODEC_H264));
      this.send1(OUT.keyframe);
      return;
    }
    if (!this.drawn && ++this.failures >= 3) {
      this.closing = true;
      this.send1(OUT.end);                          // die Sitzung ist unbrauchbar: nicht fuer ein Wiederverbinden erhalten
      const s = this.socket; this.socket = null;
      if (s) { try { s.close(); } catch { /* egal */ } }
      if (this.onerror) this.onerror({ code: 1001, message: t('stream.decodeFailed') });
      this.setState(5);
      return;
    }
    this.send1(OUT.keyframe);
  }

  // lostUpTo: die Bilder bis zu dieser Nummer, die im alten Dekodierer steckten (sie werden nie gezeichnet)
  openDecoder(codec, lostUpTo) {
    const hadDecoder = !!this.decoder;
    this.closeDecoder();
    if (hadDecoder && lostUpTo > 0) this.ack(lostUpTo);
    const d = new VideoDecoder({
      output: (frame) => {
        const g = this.display;
        const sequence = frame.timestamp;
        if (frame.displayWidth !== g.width || frame.displayHeight !== g.height) g.resize(frame.displayWidth, frame.displayHeight);
        g.ctx.drawImage(frame, 0, 0);
        g.drawnOnce = true;
        frame.close();
        this.drawn = true;
        this.output = sequence; this.activityAt = performance.now();
        this.perf.drawn(this.activityAt, sequence);
        this.ack(sequence);                        // erst nach dem Zeichnen: so laeuft der Server dem Browser nie davon
      },
      error: () => { if (this.decoder === d) this.decoderFailed(); },
    });
    d.configure(decoderConfig(codec));
    this.decoder = d; this.codec = codec; this.hevc = !codec.startsWith('avc');
  }

  /* ---- zum Server ---- */
  send(bytes) { const s = this.socket; if (s && s.readyState === WebSocket.OPEN) { this.link.touchTx(); s.send(bytes); } }
  send1(tag) { this.send(Uint8Array.of(tag)); }

  sendSize(w, h) { const b = new DataView(new ArrayBuffer(5)); b.setUint8(0, OUT.resize); b.setUint16(1, w); b.setUint16(3, h); this.send(b.buffer); }

  // Maus: Zustandsobjekte { x, y, left, middle, right, up, down } (siehe mouse.js); es gehen nur die Aenderungen hinaus
  sendMouseState(s) {
    const l = this.last;
    const x = Math.max(0, Math.min(65535, Math.round(s.x))), y = Math.max(0, Math.min(65535, Math.round(s.y)));
    if (x !== l.x || y !== l.y) {
      const b = new DataView(new ArrayBuffer(5)); b.setUint8(0, OUT.move); b.setUint16(1, x); b.setUint16(3, y); this.send(b.buffer);
      this.display.setPointer(x, y); l.x = x; l.y = y;
    }
    for (const [name, button] of [['left', 0], ['middle', 1], ['right', 2], ['back', 3], ['forward', 4]]) {
      if (!!s[name] !== l[name]) { this.send(Uint8Array.of(OUT.button, button, s[name] ? 1 : 0)); l[name] = !!s[name]; if (s[name]) this.noteInput(); }
    }
    for (const [name, delta] of [['up', 120], ['down', -120]]) {
      if (s[name] && !l[name]) { this.noteInput(); this.sendWheel(delta, false); }
      l[name] = !!s[name];
    }
    // Raddrehung in Windows-Einheiten (120 je Raste, auch feinere Schritte), senkrecht und waagerecht
    if (s.wheel) this.sendWheel(s.wheel, false);
    if (s.hwheel) this.sendWheel(s.hwheel, true);
  }

  unwatchFocus() { window.removeEventListener('blur', this.focusLost); document.removeEventListener('visibilitychange', this.focusLost); }

  sendWheel(delta, horizontal) {
    const b = new DataView(new ArrayBuffer(4)); b.setUint8(0, OUT.wheel); b.setInt16(1, Math.max(-32768, Math.min(32767, Math.round(delta)))); b.setUint8(3, horizontal ? 1 : 0); this.send(b.buffer);
  }

  // Alles loslassen, was am fernen Rechner gedrueckt ist: sonst bleibt eine Taste haengen, wenn der Browser den Fokus verliert (Alt+Tab, Tab-Wechsel,
  // Dialog), weil das Loslassen dann nie bei uns ankommt. Der ferne Rechner bekommt die Freigabe, die Tasten- und Umschaltzustaende hier werden geloescht.
  releaseAll() {
    for (const code of this.held) this.send(Uint8Array.of(OUT.key, code >> 1, (code & 1)));
    this.held.clear();
    for (const keysym of this.heldKeysyms) this.sendKeysym(false, keysym);
    this.heldKeysyms.clear();
    this.mods.ctrl = this.mods.alt = this.mods.super = false;
    const l = this.last;
    for (const [name, button] of [['left', 0], ['middle', 1], ['right', 2], ['back', 3], ['forward', 4]]) {
      if (l[name]) { this.send(Uint8Array.of(OUT.button, button, 0)); l[name] = false; }
    }
  }

  // Tasten: Sondertasten ueber ihren Scancode, Zeichen als Unicode (unabhaengig von der Belegung des fernen Rechners); mit Strg, Alt oder
  // Windows-Taste gehen Buchstaben und Ziffern ueber ihre Taste, damit Kombinationen wie Strg+C ankommen
  sendKeyEvent(pressed, keysym) {
    const mod = MODIFIERS[keysym]; if (mod) this.mods[mod] = !!pressed;
    if (this.keysyms) return this.sendKeysym(pressed, keysym);
    const special = SPECIAL.get(keysym);
    if (special) { this.key(special[0], special[1], pressed); return; }
    const cp = keysym >= 0x01000000 ? keysym - 0x01000000 : keysym;
    if (cp < 0x20 || cp > 0x10ffff) return;
    const char = String.fromCodePoint(cp);
    if (this.mods.ctrl || this.mods.alt || this.mods.super) {
      let low = char.toLowerCase();
      let code = LETTER_SCANCODES[low];
      const over = LETTER_OVERRIDES[this.layout];
      if (code !== undefined && over && over[low] !== undefined) code = over[low];
      if (code !== undefined) { this.key(code, 0, pressed); return; }
    }
    const german = this.layout === 'de' ? GERMAN_KEYS[char] : undefined;
    if (german) { if (pressed) this.tapLayoutKey(german); return; }
    if (pressed) this.noteInput();
    if (pressed) for (const unit of char) { for (let i = 0; i < unit.length; i++) { this.send(Uint8Array.of(OUT.unicode, unit.charCodeAt(i) >> 8, unit.charCodeAt(i) & 255)); } }
  }

  // VNC kennt keine Scancodes: der Server bekommt das Keysym, das app.js aus der getippten Taste gemacht hat (also das Zeichen, nicht die Taste)
  sendKeysym(pressed, keysym) {
    if (pressed) { this.heldKeysyms.add(keysym); this.noteInput(); } else this.heldKeysyms.delete(keysym);
    const b = new DataView(new ArrayBuffer(6)); b.setUint8(0, OUT.keysym); b.setUint32(1, keysym >>> 0); b.setUint8(5, pressed ? 1 : 0);
    this.send(b.buffer);
  }

  tapLayoutKey([code, shift, altgr]) {
    if (altgr) this.key(0x38, 1, true);
    if (shift) this.key(0x2a, 0, true);
    this.key(code, 0, true); this.key(code, 0, false);
    if (shift) this.key(0x2a, 0, false);
    if (altgr) this.key(0x38, 1, false);
  }

  // Eingaben, auf die ein neues Bild folgen sollte: daraus rechnet die Leistungsanzeige die Reaktionszeit (nur ein Zeitstempel)
  noteInput() { this.perf.input(performance.now(), this.received); }

  key(scancode, extended, pressed) {
    const id = (scancode << 1) | (extended ? 1 : 0);
    if (pressed) { this.held.add(id); this.noteInput(); } else this.held.delete(id);
    this.send(Uint8Array.of(OUT.key, scancode, (extended ? 1 : 0) | (pressed ? 2 : 0)));
  }

  // Text fuer die Zwischenablage des fernen Rechners (UTF-8): eine Nachricht, der ferne Rechner holt ihn, wenn er einfuegt
  // Rueckgabe: 'sent', 'empty' (nichts zu senden), 'tooLong' (mehr als 1 MiB UTF-8, der Server nimmt es nicht) oder 'offline' (keine offene Verbindung)
  sendClipboardText(text) {
    const bytes = new TextEncoder().encode(text);
    if (!bytes.length) return 'empty';
    if (bytes.length > CLIP_TEXT_MAX) return 'tooLong';
    const s = this.socket;
    if (!s || s.readyState !== WebSocket.OPEN) return 'offline';
    const message = new Uint8Array(1 + bytes.length); message[0] = OUT.clipboard; message.set(bytes, 1);
    s.send(message);
    return 'sent';
  }
}
