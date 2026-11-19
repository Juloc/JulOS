// Pop-out (popout-keys.js, popout-main.js): Tasten, Adresse, Fenstergroesse und das Uebergeben der Sitzung ohne Browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { scancodeOf, isPasteShortcut } from '../../src/JulOS.Frontend/wwwroot/popout-keys.js';
import { popoutAllowed, popoutUrl, popoutFeatures, canPopOut, openPopout, REGISTRY } from '../../src/JulOS.Frontend/wwwroot/popout-main.js';

test('physical keys map to RDP scancodes, extended where the keyboard has a second key', () => {
  assert.deepEqual(scancodeOf('KeyA'), [0x1e, 0]);
  assert.deepEqual(scancodeOf('KeyZ'), [0x2c, 0]);
  assert.deepEqual(scancodeOf('Digit0'), [0x0b, 0]);
  assert.deepEqual(scancodeOf('F1'), [0x3b, 0]); assert.deepEqual(scancodeOf('F10'), [0x44, 0]); assert.deepEqual(scancodeOf('F12'), [0x58, 0]);
  assert.deepEqual(scancodeOf('Enter'), [0x1c, 0]); assert.deepEqual(scancodeOf('NumpadEnter'), [0x1c, 1]);
  assert.deepEqual(scancodeOf('ControlLeft'), [0x1d, 0]); assert.deepEqual(scancodeOf('ControlRight'), [0x1d, 1]);
  assert.deepEqual(scancodeOf('AltRight'), [0x38, 1]);                       // AltGr
  assert.deepEqual(scancodeOf('ArrowUp'), [0x48, 1]); assert.deepEqual(scancodeOf('Delete'), [0x53, 1]);
  assert.deepEqual(scancodeOf('MetaLeft'), [0x5b, 1]);
  assert.equal(scancodeOf('MediaPlayPause'), null);                         // unbekannt: nicht senden
});

test('no scancode is given to two different keys', () => {
  const seen = new Map();
  const codes = ['Escape', 'Minus', 'Equal', 'Backspace', 'Tab', 'BracketLeft', 'BracketRight', 'Enter', 'ControlLeft', 'Semicolon', 'Quote', 'Backquote', 'ShiftLeft', 'Backslash',
    'Comma', 'Period', 'Slash', 'ShiftRight', 'NumpadMultiply', 'AltLeft', 'Space', 'CapsLock', 'NumLock', 'ScrollLock', 'Numpad7', 'Numpad8', 'Numpad9', 'NumpadSubtract',
    'Numpad4', 'Numpad5', 'Numpad6', 'NumpadAdd', 'Numpad1', 'Numpad2', 'Numpad3', 'Numpad0', 'NumpadDecimal', 'IntlBackslash', 'NumpadEnter', 'ControlRight', 'NumpadDivide',
    'PrintScreen', 'AltRight', 'Home', 'ArrowUp', 'PageUp', 'ArrowLeft', 'ArrowRight', 'End', 'ArrowDown', 'PageDown', 'Insert', 'Delete', 'MetaLeft', 'MetaRight', 'ContextMenu',
    ...'QWERTYUIOPASDFGHJKLZXCVBNM'.split('').map((c) => 'Key' + c), ...Array.from({ length: 10 }, (_, i) => 'Digit' + i), ...Array.from({ length: 12 }, (_, i) => 'F' + (i + 1))];
  for (const c of codes) {
    const sc = scancodeOf(c); assert.ok(sc, c);
    const id = sc[0] + ':' + sc[1];
    assert.equal(seen.has(id), false, `${c} und ${seen.get(id)} haben denselben Scancode`);
    seen.set(id, c);
  }
});

test('paste shortcuts are recognised, other combinations are not', () => {
  assert.equal(isPasteShortcut({ key: 'v', ctrlKey: true }), true);
  assert.equal(isPasteShortcut({ key: 'V', ctrlKey: true }), true);
  assert.equal(isPasteShortcut({ key: 'Insert', shiftKey: true }), true);
  assert.equal(isPasteShortcut({ key: 'v', ctrlKey: true, altKey: true }), false);     // AltGr+V
  assert.equal(isPasteShortcut({ key: 'v' }), false);
  assert.equal(isPasteShortcut({ key: 'c', ctrlKey: true }), false);
});

test('the pop-out button is for large screens with a mouse only', () => {
  assert.equal(popoutAllowed({ width: 1400, fine: true }), true);
  assert.equal(popoutAllowed({ width: 1000, fine: true }), true);
  assert.equal(popoutAllowed({ width: 999, fine: true }), false);
  assert.equal(popoutAllowed({ width: 1920, fine: false }), false);           // Tablet mit Touch
});

test('the address names the connection and the session but never carries the token', () => {
  assert.equal(popoutUrl('c1', 's 2'), '/popout.html?c=c1&s=s%202');
  assert.doesNotMatch(popoutUrl('c1', 's2'), /token|resume/i);
});

test('the window is smaller than the screen but never tiny or huge', () => {
  assert.match(popoutFeatures({ availWidth: 1920, availHeight: 1080 }), /width=1280,height=756/);
  assert.match(popoutFeatures({ availWidth: 800, availHeight: 500 }), /width=640,height=400/);
  assert.match(popoutFeatures(), /popup=yes/);
});

// ein Hauptfenster aus Attrappen: Sitzung mit Token, Fenster-Fabrik
function fakeHost({ blocked = false } = {}) {
  const host = { screen: { availWidth: 1920, availHeight: 1080 }, opened: [], closed: false };
  host.win = { closed: false, close() { this.closed = true; } };
  host.open = (url, name, features) => { host.opened.push({ url, name, features }); return blocked ? null : host.win; };
  return host;
}
const session = (over = {}) => ({ id: 'sid1', title: 'Test', k: 1, quality: 'auto', conn: { id: 'c1', name: 'Test' }, connected: true, popped: null,
  client: { drawn: true }, resume: { token: 'tok123', keepMs: 300000 }, ...over });

test('a session can only leave when it is connected, has drawn a picture and has a token from the server', () => {
  assert.equal(canPopOut(session()), true);
  assert.equal(canPopOut(session({ connected: false })), false);
  assert.equal(canPopOut(session({ client: { drawn: false } })), false);         // vor dem ersten Bild kann ein Login gescheitert sein
  assert.equal(canPopOut(session({ resume: { token: null, keepMs: 0 } })), false);
  assert.equal(canPopOut(session({ client: null })), false);
  assert.equal(canPopOut(session({ popped: {} })), false);
});

test('opening hands the token over through the registry and not through the address', () => {
  const host = fakeHost(); let backs = 0;
  const r = openPopout(session(), () => backs++, { perf: true }, host);
  assert.equal(r.ok, true);
  assert.equal(host.opened.length, 1);
  assert.equal(host.opened[0].url, '/popout.html?c=c1&s=sid1');
  assert.equal(host.opened[0].name, 'julos-popout-sid1');
  const entry = host[REGISTRY].sid1;
  assert.equal(entry.token, 'tok123'); assert.equal(entry.keep, 300); assert.equal(entry.perf, true);
  assert.doesNotMatch(host.opened[0].url, /tok123/);
  entry.back();                                                                   // das Fenster gibt die Sitzung zurueck
  assert.equal(backs, 1);
  entry.back();                                                                   // nur einmal
  assert.equal(backs, 1);
  assert.equal(host[REGISTRY].sid1, undefined);
});

test('a blocked window leaves the session as it is', () => {
  const host = fakeHost({ blocked: true });
  const r = openPopout(session(), () => {}, {}, host);
  assert.equal(r.ok, false); assert.match(r.reason, /Pop-ups/);
  assert.equal(host[REGISTRY], undefined);
});

test('a window the browser closed is noticed even if it never said goodbye', async () => {
  const host = fakeHost(); let backs = 0;
  openPopout(session(), () => backs++, {}, host);
  host.win.closed = true;
  await new Promise((r) => setTimeout(r, 700));
  assert.equal(backs, 1);
});

test('stop closes the window without calling back, end tells the window to end the server session', () => {
  let host = fakeHost(); let backs = 0;
  let r = openPopout(session(), () => backs++, {}, host);
  r.stop();
  assert.equal(host.win.closed, true); assert.equal(backs, 0);
  assert.ok(host[REGISTRY].sid1, 'the window still reads its entry while it closes (it only cuts the line, it does not end the session)');
  host[REGISTRY].sid1.back();                                                       // a late goodbye of the window must not call back again
  assert.equal(backs, 0);
  host = fakeHost(); let ended = 0;
  r = openPopout(session(), () => backs++, {}, host);
  host[REGISTRY].sid1.ended = () => ended++;
  r.end();
  assert.equal(ended, 1); assert.equal(host.win.closed, true); assert.equal(backs, 0);
});
