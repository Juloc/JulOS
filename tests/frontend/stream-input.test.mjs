// Eingabe im StreamClient (stream.js): Maus-Zusatztasten, Rad (senkrecht, waagerecht, fein) und das Loslassen aller Tasten und Maustasten,
// wenn der Browser den Fokus verliert. Wie in stream-codecs.test.mjs sind nur die Browser-Schnittstellen nachgebaut.
import test from 'node:test';
import assert from 'node:assert/strict';

class FakeSocket {
  static OPEN = 1;
  constructor(url) { this.url = url; this.readyState = 1; this.sent = []; FakeSocket.last = this; }
  send(b) { this.sent.push(b instanceof ArrayBuffer ? Array.from(new Uint8Array(b)) : Array.from(b)); }
  close() { this.readyState = 3; }
}
const el = () => ({ style: {}, append() {}, getContext: () => ({ drawImage() {}, putImageData() {}, clearRect() {} }), appendChild() {} });
const listeners = { window: {}, document: {} };
const target = (name) => ({
  addEventListener: (type, fn) => { (listeners[name][type] ||= new Set()).add(fn); },
  removeEventListener: (type, fn) => { listeners[name][type]?.delete(fn); },
});
globalThis.document = { createElement: el, hidden: false, ...target('document') };
globalThis.window = target('window');
globalThis.WebSocket = FakeSocket;
globalThis.ImageData = class {};
Object.defineProperty(globalThis, 'localStorage', { value: { getItem: () => null, setItem() {} }, configurable: true });
const realSetInterval = globalThis.setInterval;
globalThis.setInterval = (f, ms) => { const t = realSetInterval(f, ms); t.unref?.(); return t; };
const fire = (name, type) => { for (const fn of [...(listeners[name][type] || [])]) fn({ type }); };

const { StreamClient } = await import('../../src/JulOS.Frontend/wwwroot/stream.js');
const { mouseState } = await import('../../src/JulOS.Frontend/wwwroot/mouse.js');

function client() {
  const c = new StreamClient('ws://x/screen');
  c.connect('a=1');
  const sent = FakeSocket.last.sent;
  sent.length = 0;
  return { c, sent };
}
const OUT = { button: 3, wheel: 4, key: 5 };

test('back and forward are sent as buttons 3 and 4, once per change', () => {
  const { c, sent } = client();
  c.sendMouseState(mouseState(0, 0, false, false, false, false, false, true));
  c.sendMouseState(mouseState(0, 0, false, false, false, false, false, true));          // unchanged: nothing more
  c.sendMouseState(mouseState(0, 0, false, false, false, false, false, false, true));   // back up, forward down
  const buttons = sent.filter((m) => m[0] === OUT.button);
  assert.deepEqual(buttons, [[OUT.button, 3, 1], [OUT.button, 3, 0], [OUT.button, 4, 1]]);
});

test('the wheel goes out in Windows units, vertical and horizontal, fine steps included', () => {
  const { c, sent } = client();
  c.sendMouseState({ x: 0, y: 0, wheel: 120 });
  c.sendMouseState({ x: 0, y: 0, wheel: -7 });
  c.sendMouseState({ x: 0, y: 0, hwheel: 120 });
  c.sendMouseState({ x: 0, y: 0, hwheel: -30 });
  const wheel = sent.filter((m) => m[0] === OUT.wheel);
  const dec = (m) => [((m[1] << 8 | m[2]) << 16) >> 16, m[3]];     // signed 16 bit, horizontal flag
  assert.deepEqual(wheel.map(dec), [[120, 0], [-7, 0], [120, 1], [-30, 1]]);
  c.sendMouseState({ x: 0, y: 0, up: true });                     // the touch path keeps its notch of 120
  assert.deepEqual(dec(sent.filter((m) => m[0] === OUT.wheel).at(-1)), [120, 0]);
});

test('losing the focus releases held keys and mouse buttons at the target', () => {
  const { c, sent } = client();
  c.sendKeyEvent(true, 0xffe9);                 // Alt
  c.sendKeyEvent(true, 0xffe3);                 // Ctrl
  c.sendKeyEvent(true, 0xff09);                 // Tab
  c.sendKeyEvent(false, 0xff09);                // Tab released normally
  c.sendMouseState(mouseState(5, 5, true, false, false, false, false, true));
  sent.length = 0;
  fire('window', 'blur');
  const keys = sent.filter((m) => m[0] === OUT.key);
  assert.equal(keys.length, 2, 'Alt and Ctrl, not the already released Tab');
  assert.ok(keys.every((m) => (m[2] & 2) === 0), 'all are key-up messages');
  assert.deepEqual(sent.filter((m) => m[0] === OUT.button).sort(), [[OUT.button, 0, 0], [OUT.button, 3, 0]]);
  sent.length = 0;
  fire('window', 'blur');
  assert.deepEqual(sent, [], 'nothing held: nothing sent');
  // a letter with Ctrl no longer counts Ctrl as held
  c.sendKeyEvent(true, 0x63);
  assert.equal(sent.some((m) => m[0] === OUT.unicode), false);
});

test('a hidden tab releases too, a visible one does not, and a closed client stops listening', () => {
  const { c, sent } = client();
  c.sendKeyEvent(true, 0xffe1);                 // Shift
  sent.length = 0;
  fire('document', 'visibilitychange');         // still visible
  assert.deepEqual(sent, []);
  document.hidden = true; fire('document', 'visibilitychange'); document.hidden = false;
  assert.equal(sent.filter((m) => m[0] === OUT.key).length, 1);
  c.sendKeyEvent(true, 0xffe1); sent.length = 0;
  c.disconnect();
  sent.length = 0;
  fire('window', 'blur');
  assert.deepEqual(sent, [], 'after disconnect the listeners are gone');
});
