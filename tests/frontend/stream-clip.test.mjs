// StreamClient (stream.js), Zwischenablage: Liste der VM mit Ordnern, Stand eines Downloads, Grenzen, Download anfordern, Ordner senden.
import test from 'node:test';
import assert from 'node:assert/strict';

class FakeSocket {
  static OPEN = 1;
  constructor(url) { this.url = url; this.readyState = 1; this.sent = []; this.bufferedAmount = 0; FakeSocket.last = this; }
  send(b) { this.sent.push(b instanceof ArrayBuffer ? new Uint8Array(b) : Uint8Array.from(b)); }
  close() { this.readyState = 3; }
}
class FakeDecoder { static async isConfigSupported() { return { supported: true }; } constructor() { this.state = 'unconfigured'; } configure() { this.state = 'configured'; } decode() {} close() { this.state = 'closed'; } }
const el = () => ({ style: {}, append() {}, getContext: () => ({ drawImage() {}, putImageData() {}, clearRect() {} }), appendChild() {} });
globalThis.document = { createElement: el, hidden: false, addEventListener() {}, removeEventListener() {} };
globalThis.window = { addEventListener() {}, removeEventListener() {} };
globalThis.VideoDecoder = FakeDecoder;
globalThis.EncodedVideoChunk = class {};
globalThis.WebSocket = FakeSocket;
globalThis.ImageData = class {};
Object.defineProperty(globalThis, 'localStorage', { value: { getItem: () => null, setItem() {} }, configurable: true });
const realSetInterval = globalThis.setInterval;
globalThis.setInterval = (f, ms) => { const t = realSetInterval(f, ms); t.unref?.(); return t; };

const { StreamClient } = await import('../../src/JulOS.Frontend/wwwroot/stream.js?clip');
await StreamClient.supported();

function connected() {
  const c = new StreamClient('ws://x/api/remote/screen');
  c.connect('ticket=abc');
  return { c, s: FakeSocket.last };
}

const enc = (t) => new TextEncoder().encode(t);

// Nachricht 8: stapel(4) anzahl(2) dann je flags(1) groesse(8) namenlaenge(2) Pfad
function filesMessage(batch, entries) {
  const names = entries.map((e) => enc(e.name));
  const m = new Uint8Array(7 + entries.length * 11 + names.reduce((n, x) => n + x.length, 0)); const v = new DataView(m.buffer);
  m[0] = 8; v.setUint32(1, batch); v.setUint16(5, entries.length);
  let at = 7;
  entries.forEach((e, i) => { m[at] = e.dir ? 1 : 0; v.setBigInt64(at + 1, BigInt(e.size)); v.setUint16(at + 9, names[i].length); m.set(names[i], at + 11); at += 11 + names[i].length; });
  return m.buffer;
}

function stateMessage(batch, state, done, total, text) {
  const t = enc(text), m = new Uint8Array(22 + t.length), v = new DataView(m.buffer);
  m[0] = 9; v.setUint32(1, batch); m[5] = state; v.setBigInt64(6, BigInt(done)); v.setBigInt64(14, BigInt(total)); m.set(t, 22);
  return m.buffer;
}

test('the list of the VM has paths, folders and unknown sizes', () => {
  const { c } = connected();
  let got = null; c.onclipfiles = (batch, files) => { got = { batch, files }; };
  c.receive(filesMessage(7, [{ name: 'Ordner', size: 0, dir: true }, { name: 'Ordner/Größe ü.pdf', size: 5_000_000_000 }, { name: 'x.bin', size: -1 }]));
  assert.equal(got.batch, 7);
  assert.deepEqual(got.files, [{ name: 'Ordner', size: 0, dir: true }, { name: 'Ordner/Größe ü.pdf', size: 5_000_000_000, dir: false }, { name: 'x.bin', size: -1, dir: false }]);
  c.receive(filesMessage(8, []));
  assert.deepEqual(got, { batch: 8, files: [] });
  c.disconnect();
});

test('a cut-off list does not throw and keeps what is complete', () => {
  const { c } = connected();
  let got = null; c.onclipfiles = (batch, files) => { got = files; };
  const whole = new Uint8Array(filesMessage(1, [{ name: 'a.txt', size: 1 }, { name: 'bbbb.txt', size: 2 }]));
  c.receive(whole.slice(0, whole.length - 3).buffer);
  assert.deepEqual(got.map((f) => f.name), ['a.txt']);
  c.disconnect();
});

test('a download goes through ready (with the ticket), running, done and failed', () => {
  const { c } = connected();
  const states = []; c.onfilestate = (st) => states.push(st);
  c.receive(stateMessage(3, 0, 0, 1000, 'TICKET-abc'));
  c.receive(stateMessage(3, 1, 400, 1000, ''));
  c.receive(stateMessage(3, 2, 1000, 1000, ''));
  c.receive(stateMessage(4, 3, 10, -1, 'Die VM antwortet nicht.'));
  c.receive(stateMessage(4, 9, 0, 0, 'unbekannt'));          // ein unbekannter Zustand wird ignoriert
  c.receive(new Uint8Array([9, 0, 0]).buffer);               // zu kurz
  assert.deepEqual(states, [
    { batch: 3, state: 'ready', done: 0, total: 1000, text: 'TICKET-abc' },
    { batch: 3, state: 'running', done: 400, total: 1000, text: '' },
    { batch: 3, state: 'done', done: 1000, total: 1000, text: '' },
    { batch: 4, state: 'failed', done: 10, total: -1, text: 'Die VM antwortet nicht.' },
  ]);
  c.disconnect();
});

test('the limits of the server replace the defaults', () => {
  const { c } = connected();
  assert.deepEqual(c.limits, { fileBytes: 2 * 1024 ** 3, files: 200, entries: 255 });
  const m = new Uint8Array(13), v = new DataView(m.buffer); m[0] = 14; v.setBigInt64(1, 512n * 1024n * 1024n); v.setUint16(9, 150); v.setUint16(11, 200);
  c.receive(m.buffer);
  assert.deepEqual(c.limits, { fileBytes: 512 * 1024 * 1024, files: 150, entries: 200 });
  c.disconnect();
});

test('a download is requested by path (empty = all) and cancelled by batch', () => {
  const { c, s } = connected();
  c.requestDownload(9, 'Ordner/Größe.pdf');
  c.requestDownload(9, '');
  c.cancelDownload(9);
  const [byPath, all, cancel] = s.sent;
  assert.equal(byPath[0], 14); assert.equal(new DataView(byPath.buffer, byPath.byteOffset).getUint32(1), 9);
  assert.equal(new TextDecoder().decode(byPath.slice(5)), 'Ordner/Größe.pdf');
  assert.deepEqual([...all], [14, 0, 0, 0, 9]);
  assert.deepEqual([...cancel], [15, 0, 0, 0, 9]);
  c.disconnect();
});

test('a folder (kind 3) and a file with a path are begun with the path as name and the server answer ends the transfer', async () => {
  const { c, s } = connected();
  const folder = c.sendTransfer(3, null, 'Projekt/leer', 0, 2, null, null);
  const begin = s.sent[0], v = new DataView(begin.buffer, begin.byteOffset);
  assert.equal(begin[0], 10); assert.equal(begin[5], 3); assert.equal(begin[6], 0); assert.equal(begin[7], 2);
  assert.equal(v.getBigUint64(8), 0n);
  assert.equal(new TextDecoder().decode(begin.slice(16)), 'Projekt/leer');
  await new Promise((r) => setTimeout(r, 5));
  assert.equal(s.sent.at(-1)[0], 12);                         // sofort das Ende, es gibt nichts zu senden
  const id = v.getUint32(1);
  const answer = new Uint8Array(6); new DataView(answer.buffer).setUint32(1, id); answer[0] = 10; answer[5] = 0;
  c.receive(answer.buffer);
  assert.deepEqual(await folder, { code: 0, text: '' });

  const long = 'a/' + 'ü'.repeat(700);
  const sending = c.sendTransfer(2, new Blob(['xyz']), long, 1, 2, null, null);
  const begin2 = s.sent.find((m, i) => i > 1 && m[0] === 10);
  assert.equal(begin2.length, 16 + 1200);                     // der Pfad wird gekappt (der Server lehnt ihn dann ab, siehe ClipboardNames)
  const refusal = enc('Ungültiger Dateiname oder Pfad.'), reply = new Uint8Array(6 + refusal.length);
  new DataView(reply.buffer).setUint32(1, new DataView(begin2.buffer, begin2.byteOffset).getUint32(1)); reply[0] = 10; reply[5] = 2; reply.set(refusal, 6);
  c.receive(reply.buffer);
  assert.deepEqual(await sending, { code: 2, text: 'Ungültiger Dateiname oder Pfad.' });
  c.disconnect();
});

// ---- Text: Rueckgabe des Sendens und Nachricht 15 (Diagnose) ----

test('sendClipboardText sagt, was daraus wurde: gesendet, leer, zu lang, ohne Verbindung', () => {
  const { c, s } = connected();
  assert.equal(c.sendClipboardText('Hallo ä'), 'sent');
  assert.deepEqual([...s.sent.at(-1)], [9, ...enc('Hallo ä')]);
  const n = s.sent.length;
  assert.equal(c.sendClipboardText(''), 'empty');
  assert.equal(c.sendClipboardText('x'.repeat(1024 * 1024 + 1)), 'tooLong');
  assert.equal(c.sendClipboardText('x'.repeat(1024 * 1024)), 'sent');            // genau die Grenze des Servers geht noch
  assert.equal(s.sent.length, n + 1);
  c.disconnect();
  assert.equal(c.sendClipboardText('weg'), 'offline');
  const fresh = new StreamClient('ws://x/api/remote/screen');
  assert.equal(fresh.sendClipboardText('nie verbunden'), 'offline');
});

test('Nachricht 15 kommt als onclipnote mit Kanalstand, Art und Zahl', () => {
  const { c } = connected();
  const got = []; c.onclipnote = (n) => got.push(n);
  c.receive(Uint8Array.of(15, 3, 3, 0, 0, 1, 0x2c).buffer);
  c.receive(Uint8Array.of(15, 0, 0, 0, 0, 0, 0).buffer);
  c.receive(Uint8Array.of(15, 1).buffer);                       // zu kurz: ignoriert
  assert.deepEqual(got, [{ ready: true, files: true, note: 3, count: 300 }, { ready: false, files: false, note: 0, count: 0 }]);
  c.disconnect();
});
