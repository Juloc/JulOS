// Ton und Mikrofon im Browser (stream-audio.js) mit Ersatzobjekten fuer AudioContext, AudioWorkletNode und getUserMedia:
// wann der Server "Ton an" hoert, was nur die aktive Sitzung spielt, was bei verweigertem Mikrofon und bei einem Fehler des Worklets geschieht.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';

setPref('de', null);

const log = [];
let nextState = 'running', failModule = false, gumError = null, secure = true, permState = 'prompt';

class FakeContext {
  constructor() { this.state = 'suspended'; this.currentTime = 0; this.closed = false; log.push('context'); this.audioWorklet = { addModule: async (u) => { log.push('module ' + u); if (failModule) throw new Error('kaputt'); } }; this.destination = {}; }
  createGain() { return { gain: { value: 1, setTargetAtTime() {} }, connect() {} }; }
  createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
  async resume() { log.push('resume'); this.state = nextState; if (this.onstatechange) this.onstatechange(); }
  async close() { this.closed = true; this.state = 'closed'; log.push('close'); }
}
class FakeNode {
  constructor(ctx, name) { this.name = name; this.posted = []; this.port = { postMessage: (m, tr) => this.posted.push(m), onmessage: null }; }
  connect() {} disconnect() {}
}
const tracks = [];
globalThis.AudioContext = FakeContext;
globalThis.AudioWorkletNode = FakeNode;
const listeners = {};
globalThis.document = { hidden: false, addEventListener: (type, fn) => { (listeners[type] ||= []).push(fn); }, removeEventListener: (type, fn) => { listeners[type] = (listeners[type] || []).filter((f) => f !== fn); } };
Object.defineProperty(globalThis, 'window', { value: { get isSecureContext() { return secure; } }, configurable: true });
Object.defineProperty(globalThis, 'navigator', {
  configurable: true,
  value: {
    userAgent: 'Mozilla/5.0 (X11; Linux x86_64) Chrome/120.0 Safari/537.36',
    permissions: { query: async () => ({ state: permState }) },
    mediaDevices: { getUserMedia: async () => { log.push('getUserMedia'); if (gumError) { const e = new Error('x'); e.name = gumError; throw e; } const t = { stop() { this.stopped = true; }, stopped: false }; tracks.push(t); return { getTracks: () => [t], getAudioTracks: () => [t] }; } },
  },
});

const { StreamAudio } = await import('../../src/JulOS.Frontend/wwwroot/stream-audio.js');

const fresh = () => { const sent = []; const a = new StreamAudio((b) => sent.push(Array.from(b))); return { a, sent }; };
const reset = () => { log.length = 0; nextState = 'running'; failModule = false; gumError = null; secure = true; permState = 'prompt'; tracks.length = 0; };

test('"Ton an" geht an den Server erst, wenn der AudioContext wirklich laeuft, und "aus" bei jedem Wechsel', async () => {
  reset(); nextState = 'suspended';
  const { a, sent } = fresh();
  await a.enable();
  assert.equal(a.state, 'suspended');
  assert.deepEqual(sent, [[20, 0]], 'noch gesperrt: der Server schickt nichts');
  nextState = 'running';
  a.ctx.state = 'running'; a.ctx.onstatechange();                      // die erste Eingabe hat den Browser freigegeben
  assert.deepEqual(sent.at(-1), [20, 1]);
  a.setActive(false);                                                  // eine andere Sitzung ist vorn
  assert.deepEqual(sent.at(-1), [20, 0]);
  a.setActive(true);
  assert.deepEqual(sent.at(-1), [20, 1]);
  a.disable();
  assert.deepEqual(sent.at(-1), [20, 0]);
  a.close();
});

test('Tonstuecke werden nur gespielt, wenn Ton an, die Sitzung aktiv und der Kontext laeuft; sonst gezaehlt und verworfen', async () => {
  reset();
  const { a } = fresh();
  const block = (n = 40) => { const b = new ArrayBuffer(10 + n * 2); const v = new DataView(b); v.setUint8(0, 20); v.setUint8(1, 2); v.setInt32(2, 48000); return b; };
  a.frame(block());                                                    // noch kein Spieler
  assert.equal(a.stats.ignored, 1);
  await a.enable();
  a.frame(block());
  assert.equal(a.player.posted.at(-1).type, 'pcm');
  assert.equal(a.player.posted.at(-1).rate, 48000);
  assert.equal(a.player.posted.at(-1).channels, 2);
  assert.equal(a.player.posted.at(-1).length, 40);
  a.setActive(false);
  const before = a.player.posted.length;
  a.frame(block());
  assert.equal(a.player.posted.length, before, 'in einer Sitzung im Hintergrund wird nichts gespielt');
  assert.equal(a.stats.ignored, 2);
  assert.equal(a.stats.blocks, 3);
  a.close();
});

test('ein Zustand vom Server (21) wird gelesen: Ton da, Mikrofon angeboten, Aufnahme verlangt, Rate', () => {
  reset();
  const { a } = fresh();
  const m = new ArrayBuffer(6); const v = new DataView(m); v.setUint8(0, 21); v.setUint8(1, 7); v.setInt32(2, 44100);
  let changed = 0; a.onchange = () => changed++;
  a.status(m);
  assert.deepEqual(a.remote, { output: true, microphone: true, recording: true, micRate: 44100 });
  assert.equal(changed, 1);
});

test('schlaegt das Laden des Worklets fehl, bleibt kein halber Zustand zurueck: das naechste Einschalten beginnt neu', async () => {
  reset(); failModule = true;
  const { a } = fresh();
  await assert.rejects(a.enable(), /Der Ton lässt sich nicht starten: kaputt/);
  assert.equal(a.ctx, null);
  assert.equal(a.wanted, false);
  assert.equal(log.filter((l) => l === 'close').length, 1);
  failModule = false;
  await a.enable();
  assert.ok(a.player, 'jetzt gibt es einen Spieler');
  assert.equal(a.state, 'running');
  a.close();
});

test('das Mikrofon: der AudioContext entsteht vor der Freigabe-Abfrage (Safari verlangt ihn in der Eingabe), Stuecke gehen als Nachricht 21 hinaus', async () => {
  reset();
  const { a, sent } = fresh();
  a.remote = { output: true, microphone: true, recording: true, micRate: 16000 };
  await a.startMic();
  assert.ok(log.indexOf('context') < log.indexOf('getUserMedia'), 'Reihenfolge: ' + log.join(','));
  assert.equal(a.micOn, true);
  assert.equal(StreamAudio.micOwner, a);
  const pcm = new Int16Array(320).fill(1000);
  a.mic.node.port.onmessage({ data: { type: 'pcm', buffer: pcm.buffer, peak: 0.25 } });
  const msg = sent.at(-1);
  assert.equal(msg[0], 21);
  assert.deepEqual(msg.slice(1, 5), [0, 0, 0x3e, 0x80], 'Rate 16000, big endian');
  assert.equal(msg.length, 5 + 640);
  assert.equal(a.micLevel, 0.25);
  a.stopMic();
  assert.equal(StreamAudio.micOwner, null);
  assert.equal(tracks[0].stopped, true);
});

test('Mikrofon verweigert: Meldung mit Anleitung fuer den Browser, der Kontext wird wieder geschlossen, nichts bleibt belegt', async () => {
  reset(); gumError = 'NotAllowedError';
  const { a } = fresh();
  await assert.rejects(a.startMic(), (e) => /nicht freigegeben/.test(e.message) && /Schloss oder Regler/.test(e.message));
  assert.equal(StreamAudio.micOwner, null);
  assert.ok(log.includes('close'));
  // andere Fehlernamen (hier wie Chromium ohne Oberflaeche fuer die Frage): der Zustand "verweigert" entscheidet
  gumError = 'NotSupportedError'; permState = 'denied';
  await assert.rejects(a.startMic(), /nicht freigegeben.*Schloss oder Regler/);
  permState = 'prompt';
  await assert.rejects(a.startMic(), /lässt sich nicht öffnen \(NotSupportedError\)/);
  gumError = 'NotFoundError';
  await assert.rejects(a.startMic(), /Kein Mikrofon gefunden/);
  gumError = 'NotReadableError';
  await assert.rejects(a.startMic(), /von einem anderen Programm/);
});

test('Mikrofon ohne sicheren Kontext (kein https): verstaendliche Meldung, getUserMedia wird gar nicht erst gerufen', async () => {
  reset(); secure = false;
  const { a } = fresh();
  await assert.rejects(a.startMic(), /https/);
  assert.ok(!log.includes('getUserMedia'));
});

test('das Mikrofon gehoert einer Sitzung; die zweite bekommt eine Meldung mit dem Namen der ersten', async () => {
  reset();
  const one = fresh().a, two = fresh().a;
  one.label = 'Büro';
  await one.startMic();
  await assert.rejects(two.startMic(), /schon in der Sitzung „Büro“/);
  one.stopMic();
  await two.startMic();
  two.stopMic();
});
