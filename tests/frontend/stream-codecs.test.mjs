// StreamClient (stream.js) mit gespieltem Browser: welche Codecs es dem Server meldet, wie es H.264 und H.265 dekodiert und wie es nach einem
// Fehler des H.265-Dekodierers auf H.264 zurueckgeht. Nur die Browser-Schnittstellen (WebSocket, VideoDecoder, document) sind nachgebaut.
import test from 'node:test';
import assert from 'node:assert/strict';

const decoders = [];
class FakeDecoder {
  static support = { h264: true, hevc: true };
  static async isConfigSupported(c) { return { supported: c.codec.startsWith('avc1') ? FakeDecoder.support.h264 : FakeDecoder.support.hevc }; }
  constructor(init) { this.init = init; this.state = 'unconfigured'; this.chunks = []; decoders.push(this); }
  configure(c) { this.config = c; this.state = 'configured'; }
  decode(chunk) { this.chunks.push(chunk); }
  close() { this.state = 'closed'; }
}
class FakeChunk { constructor(o) { Object.assign(this, o); } }
class FakeSocket {
  static OPEN = 1;
  constructor(url) { this.url = url; this.readyState = 1; this.sent = []; FakeSocket.last = this; }
  send(b) { this.sent.push(b instanceof ArrayBuffer ? new Uint8Array(b) : Uint8Array.from(b)); }
  close() { this.readyState = 3; }
}
const el = () => ({ style: {}, append() {}, getContext: () => ({ drawImage() {}, putImageData() {}, clearRect() {} }), appendChild() {} });
globalThis.document = { createElement: el, hidden: false, addEventListener() {}, removeEventListener() {} };
globalThis.VideoDecoder = FakeDecoder;
globalThis.EncodedVideoChunk = FakeChunk;
globalThis.WebSocket = FakeSocket;
globalThis.ImageData = class {};

// the client's watchdog interval must not keep node alive when a test ends early
const realSetInterval = globalThis.setInterval;
globalThis.setInterval = (f, ms) => { const t = realSetInterval(f, ms); t.unref?.(); return t; };

// stream.js remembers what the browser supports for the whole module, so every variant of the browser gets its own copy of the module
let copy = 0;
async function loadModule(support) {
  FakeDecoder.support = support;
  const { StreamClient } = await import(`../../src/JulOS.Frontend/wwwroot/stream.js?copy=${copy++}`);
  await StreamClient.supported();
  return StreamClient;
}
const { HEVC_SPS, H264_SPS } = {
  H264_SPS: [0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28, 0xac, 0xd9, 0x40, 0, 0, 0, 1, 0x65, 0x88],
  HEVC_SPS: [0, 0, 0, 1, 0x40, 0x01, 0x0c, 0x01, 0, 0, 0, 1, 0x42, 0x01, 0x01, 0x01, 0x60, 0, 0, 3, 0, 0x90, 0, 0, 3, 0, 0, 3, 0, 0x78, 0xa0, 0x03, 0xc0, 0, 0, 0, 1, 0x26, 0x01, 0xaf],
};

// eine Bildnachricht: Art 1, flags, Folgenummer, Daten
const picture = (sequence, key, hevc, data) => {
  const m = new Uint8Array(6 + data.length); const v = new DataView(m.buffer);
  m[0] = 1; m[1] = (key ? 1 : 0) | (hevc ? 2 : 0); v.setInt32(2, sequence); m.set(data, 6);
  return m.buffer;
};
const sentTypes = (s) => s.sent.map((m) => m[0]);

async function connected(support) {
  const StreamClient = await loadModule(support);
  decoders.length = 0;
  const c = new StreamClient('ws://x/api/remote/screen');
  c.connect('ticket=abc');
  return { c, s: FakeSocket.last, StreamClient };
}

test('the address of the WebSocket says which codecs this browser decodes', async () => {
  const { c, s, StreamClient } = await connected({ h264: true, hevc: true });
  assert.match(s.url, /^ws:\/\/x\/api\/remote\/screen\?ticket=abc&codecs=3$/);
  assert.deepEqual(StreamClient.codecs(), { h264: true, hevc: true });
  c.disconnect();
});

test('an H.265 key frame configures an hvc1 decoder from the SPS, and the next pictures are decoded as delta frames', async () => {
  const { c } = await connected({ h264: true, hevc: true });
  c.receive(picture(1, true, true, HEVC_SPS));
  const d = decoders.at(-1);
  assert.equal(d.config.codec, 'hvc1.1.6.L120.90');
  assert.equal(d.config.hardwareAcceleration, 'prefer-hardware');
  assert.equal(d.chunks[0].type, 'key');
  c.receive(picture(2, false, true, [0, 0, 0, 1, 0x02, 0x01, 1]));
  assert.equal(d.chunks[1].type, 'delta');
  assert.equal(c.hevc, true);
  c.disconnect();
});

test('when the H.265 decoder fails the client tells the server H.264 only and asks for a key frame, and drops H.265 that is still on the way', async () => {
  const { c, s } = await connected({ h264: true, hevc: true });
  c.receive(picture(1, true, true, HEVC_SPS));
  decoders.at(-1).init.error(new Error('decode failed'));
  const sent = s.sent.map((m) => [...m]);
  assert.ok(sent.some((m) => m[0] === 30 && m[1] === 1), 'message 30 with H.264 only');
  assert.ok(sent.some((m) => m[0] === 8), 'key frame request');
  assert.equal(c.hevcOk, false);
  assert.equal(c.failures, 0, 'a failed H.265 decoder does not count towards giving up on the stream engine');
  // a picture the server sent before it switched: dropped and acknowledged, no decoder is opened for it
  const before = decoders.length;
  s.sent.length = 0;
  c.receive(picture(2, false, true, [0, 0, 0, 1, 0x02, 0x01, 1]));
  c.receive(picture(3, true, true, HEVC_SPS));
  assert.equal(decoders.length, before);
  assert.deepEqual(sentTypes(s), [1, 1]);
  // and then the H.264 key frame arrives: a new decoder, avc1
  c.receive(picture(4, true, false, H264_SPS));
  assert.equal(decoders.at(-1).config.codec, 'avc1.640028');
  assert.equal(decoders.at(-1).chunks[0].type, 'key');
  assert.equal(c.hevc, false);
  c.disconnect();
});

test('a browser that cannot decode H.265 says so in the address and an H.264 stream is decoded as before', async () => {
  const { c, s } = await connected({ h264: true, hevc: false });
  assert.match(s.url, /codecs=1$/);
  c.receive(picture(1, true, false, H264_SPS));
  assert.equal(decoders.at(-1).config.codec, 'avc1.640028');
  assert.deepEqual(decoders.at(-1).config.avc, { format: 'annexb' });
  assert.equal(decoders.at(-1).config.optimizeForLatency, true);
  c.disconnect();
});

test('the encoder message is kept for display', async () => {
  const { c } = await connected({ h264: true, hevc: true });
  const name = new TextEncoder().encode('VAAPI Intel iHD');
  c.receive(Uint8Array.of(30, 2, 1, ...name).buffer);
  assert.deepEqual(c.encoder, { codec: 'H.265', hardware: true, name: 'VAAPI Intel iHD' });
  assert.equal(c.stats.encoder, 'VAAPI Intel iHD (H.265, GPU)');
  c.disconnect();
});
