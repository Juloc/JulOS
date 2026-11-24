import test from 'node:test';
import assert from 'node:assert/strict';
import { PasteGate, pendingStep, PENDING_MAX_MS, PASTE_DELAY_MS, PASTE_TIMEOUT_MS } from '../../src/JulOS.Frontend/wwwroot/clip-sync.js';

// kleine Uhr, damit kein echtes Warten noetig ist
function clock() {
  let now = 0, id = 0; const timers = new Map();
  return {
    setTimer: (f, ms) => { timers.set(++id, { f, at: now + ms }); return id; },
    clearTimer: (h) => timers.delete(h),
    advance(ms) { now += ms; for (const [h, t] of [...timers]) if (t.at <= now) { timers.delete(h); t.f(); } },
  };
}

test('ohne Meldung des Servers bleibt es bei der festen Pause', async () => {
  const c = clock(), g = new PasteGate(c);
  const p = g.wait(); c.advance(PASTE_DELAY_MS);
  assert.equal(await p, 'timer');
});

test('mit Meldung wird erst bei "angenommen" eingefuegt, nicht nach der Pause', async () => {
  const c = clock(), g = new PasteGate(c);
  g.note({ acks: true, note: 0 });
  const p = g.wait(); c.advance(PASTE_DELAY_MS + 100);
  let done = false; p.then(() => { done = true; });
  await Promise.resolve(); assert.equal(done, false);
  g.note({ acks: true, note: 8 });
  assert.equal(await p, 'accepted');
});

test('bleibt die Meldung aus, wird nach der Frist trotzdem eingefuegt', async () => {
  const c = clock(), g = new PasteGate(c);
  g.note({ acks: true, note: 0 });
  const p = g.wait(); c.advance(PASTE_TIMEOUT_MS);
  assert.equal(await p, 'timeout');
});

test('eine Ablehnung sendet nichts', async () => {
  const c = clock(), g = new PasteGate(c);
  g.note({ acks: true, note: 0 });
  const p = g.wait(); g.note({ acks: true, note: 7 });
  assert.equal(await p, 'refused');
});

test('eine neue Verbindung vergisst, dass der Server meldet', async () => {
  const c = clock(), g = new PasteGate(c);
  g.note({ acks: true, note: 0 }); g.connected();
  const p = g.wait(); c.advance(PASTE_DELAY_MS);
  assert.equal(await p, 'timer');
});

test('wartender Inhalt: Geste schreibt, solange er frisch ist, und er bleibt bis zum Gelingen', () => {
  const item = { kind: 'text', data: 'x', at: 1000 };
  const r = pendingStep(item, 'gesture', 1000 + 5000);
  assert.equal(r.write, item); assert.equal(r.pending, item);
});

test('wartender Inhalt verfaellt nach etwa einer Minute', () => {
  const item = { kind: 'image', data: {}, at: 0 };
  assert.deepEqual(pendingStep(item, 'gesture', PENDING_MAX_MS), { pending: null, write: null });
});

test('eigenes Kopieren, Neueres aus der VM und das Ende der Sitzung verwerfen ihn, nichts wird geschrieben', () => {
  const item = { kind: 'text', data: 'x', at: 0 };
  for (const ev of ['copy', 'newer', 'end']) assert.deepEqual(pendingStep(item, ev, 10), { pending: null, write: null });
});

test('ohne wartenden Inhalt geschieht nichts', () => {
  assert.deepEqual(pendingStep(null, 'gesture', 10), { pending: null, write: null });
});
