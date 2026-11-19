/* =====================================================================
 * Ton fuer den Bildschirm-Stream (siehe ScreenProtocol auf dem Server, Nachrichten 20 und 21).
 *
 *   Ton vom fernen Rechner  Server -> Browser: 20 (kanaele u8, rate u32, zeit u32, PCM 16 Bit). Die Stuecke gehen unveraendert an einen
 *                           AudioWorklet (stream-audio-worklet.js), der sie abspielt; hier wird nichts pro Sample gerechnet.
 *   Mikrofon                getUserMedia mit Echo- und Rauschunterdrueckung -> AudioWorklet (auf die Rate des fernen Rechners herunterrechnen,
 *                           16 Bit Mono, 20-ms-Stuecke) -> Browser -> Server: 21 (rate u32, PCM).
 *   Zustand                 Server -> Browser: 21 (flags u8: 1 der ferne Rechner liefert Ton, 2 er bietet ein Mikrofon, 4 er nimmt gerade auf; rate u32).
 *
 * Ton und Mikrofon sind aus, bis die Seite sie einschaltet (der Browser verlangt dafuer eine Eingabe des Benutzers, das Mikrofon dazu
 * eine Freigabe). Der Server schickt erst Ton, wenn "Ton an" (Nachricht 20 an den Server) gekommen ist.
 * ===================================================================== */

const TO_SERVER = { audio: 20, microphone: 21 };
const WORKLET = '/stream-audio-worklet.js';

// Das Mikrofon gehoert hoechstens einer Sitzung zur Zeit (der Browser hat eines, und der ferne Rechner bekaeme sonst zwei Ströme).
let micOwner = null;
const micWatchers = new Set();
const notifyMic = () => { for (const fn of micWatchers) { try { fn(micOwner); } catch { /* egal */ } } };

export class StreamAudio {
  /** Meldet jede Aenderung, wer das Mikrofon hat (die Sitzung oder null); gibt eine Funktion zum Abmelden zurueck. */
  static watchMic(fn) { micWatchers.add(fn); return () => micWatchers.delete(fn); }
  /** Wer gerade das Mikrofon benutzt (ein StreamAudio) oder null. */
  static get micOwner() { return micOwner; }
  /** Beendet das Mikrofon, egal in welcher Sitzung. */
  static stopMic() { if (micOwner) micOwner.stopMic(); }

  /** @param {(bytes: Uint8Array) => void} send schickt eine Nachricht an den Server */
  constructor(send) {
    this.send = send;
    this.ctx = null; this.player = null; this.gain = null;
    this.label = '';                    // Name der Sitzung (fuer Meldungen: "Das Mikrofon laeuft schon in ...")
    this.active = true;                 // nur die sichtbare, aktive Sitzung spielt Ton (setActive)
    this.wanted = false;                // der Benutzer will Ton hoeren
    this.volume = 1;
    this.remote = { output: false, microphone: false, recording: false, micRate: 16000 };
    this.stats = { bufferedMs: 0, targetMs: 0, underruns: 0, dropped: 0, peak: 0, received: 0, blocks: 0, ignored: 0 };
    this.micStarting = false;
    this.mic = null;                    // { stream, source, node, ctx } solange das Mikrofon an ist
    this.micLevel = 0; this.micChunks = 0;
    this.onchange = null; this.onmicwait = null;
    this.closed = false;
    this.lastOn = null;
    this.resume = () => this.tryResume();
    this.visible = () => { if (!document.hidden) this.tryResume(); };
  }

  /* ---- Zustand fuer die Anzeige ---- */
  get supported() { return typeof AudioContext !== 'undefined' && typeof AudioWorkletNode !== 'undefined'; }
  get micSupported() { return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia) && window.isSecureContext !== false; }
  get state() { return this.ctx ? this.ctx.state : 'none'; }
  get playing() { return this.wanted && this.active && this.state === 'running'; }
  get micOn() { return !!this.mic; }

  changed() { if (this.onchange) this.onchange(); }

  /* ---- Nachrichten vom Server ---- */
  frame(buffer) {
    // 20: kanaele(1) rate(4) zeit(4) PCM ...
    this.stats.blocks++;
    if (!this.player || !this.wanted || !this.active || !this.ctx || this.ctx.state !== 'running' || buffer.byteLength <= 10) { this.stats.ignored++; return; }
    const v = new DataView(buffer);
    const channels = v.getUint8(1), rate = v.getInt32(2);
    const length = (buffer.byteLength - 10) >> 1;
    this.player.port.postMessage({ type: 'pcm', buffer, offset: 10, length, channels, rate }, [buffer]);
  }

  status(buffer) {
    // 21: flags(1) mikrofonrate(4)
    const v = new DataView(buffer);
    const flags = v.getUint8(1);
    const remote = { output: !!(flags & 1), microphone: !!(flags & 2), recording: !!(flags & 4), micRate: v.getInt32(2) };
    // Die laufende Aufnahme behaelt ihre Rate; aendert der ferne Rechner seine, rechnet der Server um. Der Kanal kann kommen und gehen
    // (xrdp oeffnet ihn erst, wenn im Desktop ein Programm aufnimmt, Windows gleich beim Anmelden): das beendet das Mikrofon hier nicht.
    this.remote = remote;
    this.changed();
  }

  /* ---- Ton ein/aus ---- */
  /** Aufruf aus einer Eingabe des Benutzers (Antippen), damit der Browser den Ton freigibt. */
  async enable() {
    if (!this.supported) throw new Error('Dieser Browser kann keinen Ton ausgeben.');
    this.wanted = true;
    if (!this.ctx) {
      this.ctx = new AudioContext({ latencyHint: 'interactive' });
      this.gain = this.ctx.createGain(); this.gain.gain.value = this.volume;
      this.gain.connect(this.ctx.destination);
      this.ctx.onstatechange = () => { this.syncServer(); this.changed(); };
      await this.ctx.audioWorklet.addModule(WORKLET);
      if (this.closed) { this.ctx.close().catch(() => {}); return; }
      this.player = new AudioWorkletNode(this.ctx, 'jul-player', { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2] });
      this.player.port.onmessage = (e) => { if (e.data && e.data.type === 'stats') { Object.assign(this.stats, e.data); } };
      this.player.connect(this.gain);
      document.addEventListener('visibilitychange', this.visible);
      for (const type of ['pointerdown', 'keydown', 'touchend']) document.addEventListener(type, this.resume, { passive: true });
    }
    await this.tryResume();
    this.syncServer();
    this.changed();
  }

  disable() {
    this.wanted = false;
    this.syncServer();
    if (this.player) this.player.port.postMessage({ type: 'reset' });
    this.changed();
  }

  /** Nur die sichtbare, aktive Sitzung spielt Ton: sonst schickt der Server nichts (und es wird nichts gepuffert). */
  setActive(on) {
    on = !!on;
    if (this.active === on) return;
    this.active = on;
    if (on) this.tryResume();
    this.syncServer();
    this.changed();
  }

  setVolume(volume) {
    this.volume = Math.max(0, Math.min(1, volume));
    if (this.gain) this.gain.gain.setTargetAtTime(this.volume, this.ctx.currentTime, 0.02);
    this.changed();
  }

  async tryResume() {
    if (!this.ctx || this.closed || !this.wanted) return;
    if (this.ctx.state !== 'running') { try { await this.ctx.resume(); } catch { /* noch keine Eingabe: beim naechsten Antippen */ } }
  }

  // Der Server schickt nur Ton, solange wir ihn wirklich abspielen koennen (nicht bei gesperrtem oder angehaltenem AudioContext)
  syncServer() {
    const on = this.wanted && this.active && !!this.ctx && this.ctx.state === 'running' && !this.closed;
    if (on === this.lastOn) return;
    this.lastOn = on;
    this.send(Uint8Array.of(TO_SERVER.audio, on ? 1 : 0));
    if (!on && this.player) this.player.port.postMessage({ type: 'reset' });
  }

  /** Nach einer neuen Verbindung (der Server weiss nichts mehr): Zustand noch einmal melden. */
  connected() { this.lastOn = null; this.syncServer(); }

  /* ---- Mikrofon ---- */
  micProblem() {
    if (!this.micSupported) return 'Das Mikrofon braucht eine sichere Verbindung (https) und einen Browser, der es freigeben kann.';
    return '';
  }

  async startMic() {
    if (this.mic || this.micStarting) return;
    if (micOwner && micOwner !== this) throw new Error('Das Mikrofon läuft schon in der Sitzung „' + (micOwner.label || 'andere Sitzung') + '“. Dort ausschalten, dann hier einschalten.');
    const problem = this.micProblem();
    if (problem) throw new Error(problem);
    this.micStarting = true;
    micOwner = this;                    // gleich beanspruchen: zwei Antippen kurz hintereinander duerfen nicht zwei Aufnahmen starten
    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
    } catch (e) {
      this.micStarting = false; micOwner = null; notifyMic();
      const name = e && e.name;
      if (name === 'NotAllowedError' || name === 'SecurityError') throw new Error('Der Browser hat das Mikrofon nicht freigegeben. In den Einstellungen der Seite erlauben und noch einmal versuchen.');
      if (name === 'NotFoundError' || name === 'OverconstrainedError') throw new Error('Kein Mikrofon gefunden.');
      if (name === 'NotReadableError') throw new Error('Das Mikrofon wird gerade von einem anderen Programm benutzt.');
      throw new Error('Das Mikrofon lässt sich nicht öffnen.');
    }
    try {
      const rate = this.remote.micRate || 16000;
      const ctx = new AudioContext({ latencyHint: 'interactive' });
      await ctx.audioWorklet.addModule(WORKLET);
      const source = ctx.createMediaStreamSource(stream);
      const node = new AudioWorkletNode(ctx, 'jul-capture', { numberOfInputs: 1, numberOfOutputs: 0, processorOptions: { rate } });
      node.port.onmessage = (e) => this.micChunk(e.data, rate);
      source.connect(node);
      if (ctx.state !== 'running') await ctx.resume();
      this.mic = { stream, source, node, ctx, rate };
      this.micChunks = 0;
      // Der ferne Rechner verlangt Ton erst, wenn dort ein Programm aufnimmt (xrdp: dann erst oeffnet er den Kanal). Kommt nichts, den Benutzer das wissen lassen.
      setTimeout(() => { if (this.mic && !this.remote.recording && this.onmicwait) this.onmicwait(this.remote.microphone); }, 6000);
      for (const track of stream.getAudioTracks()) track.onended = () => this.stopMic();    // vom Browser oder System beendet (Anzeige "Mikrofon" am Handy)
      this.micStarting = false;
      this.changed(); notifyMic();
    } catch (e) {
      this.micStarting = false; micOwner = null; notifyMic();
      for (const track of stream.getAudioTracks()) track.stop();
      throw new Error('Das Mikrofon lässt sich nicht starten: ' + (e && e.message ? e.message : e));
    }
  }

  micChunk(m, rate) {
    if (!this.mic || m.type !== 'pcm') return;
    this.micChunks++; this.micLevel = m.peak;
    const pcm = new Uint8Array(m.buffer);
    const message = new Uint8Array(5 + pcm.length);
    message[0] = TO_SERVER.microphone;
    new DataView(message.buffer).setUint32(1, rate);
    message.set(pcm, 5);
    this.send(message);
  }

  stopMic() {
    const mic = this.mic; if (!mic) return;
    this.mic = null; this.micLevel = 0; if (micOwner === this) micOwner = null;
    for (const track of mic.stream.getTracks()) track.stop();
    try { mic.node.disconnect(); mic.source.disconnect(); } catch { /* egal */ }
    mic.ctx.close().catch(() => {});
    this.changed(); notifyMic();
  }

  /** Die Verbindung endet. */
  close() {
    this.closed = true; this.wanted = false;
    this.stopMic();
    document.removeEventListener('visibilitychange', this.visible);
    for (const type of ['pointerdown', 'keydown', 'touchend']) document.removeEventListener(type, this.resume);
    if (this.player) { try { this.player.disconnect(); } catch { /* egal */ } this.player = null; }
    if (this.ctx) { this.ctx.close().catch(() => {}); this.ctx = null; }
  }
}
