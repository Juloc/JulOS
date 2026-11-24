// Lebenszeichen der Leitung (link-health.js): Fristen, Hintergrund, stille gegen laute Wiederaufnahme, mit kuenstlicher Zeit.
import test from 'node:test';
import assert from 'node:assert/strict';
import { LinkHealth, LinkStats, linkSummary, PING_EVERY_MS, DEAD_AFTER_MS, PROBE_MS, QUIET_MS } from '../../src/JulOS.Frontend/wwwroot/link-health.js';

const clock = () => { const c = { t: 1_000_000, now: () => c.t, add: (ms) => { c.t += ms; } }; return c; };
// Sekundentakt ueber ms Millisekunden; gibt die Aktionen zurueck
const run = (h, c, ms, rx = () => false) => {
  const out = [];
  for (let i = 0; i < ms / 1000; i++) { c.add(1000); if (rx(c.t)) h.touchRx(); const a = h.tick(); if (a) out.push([c.t, a]); }
  return out;
};

test('without the server announcing it nothing is sent and nothing is declared dead', () => {
  const c = clock(), h = new LinkHealth(c.now);
  assert.deepEqual(run(h, c, 120_000), []);
});

test('quiet line: ping after 5 s, dead 10 s later without any answer', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable();
  const acts = run(h, c, 20_000);
  assert.deepEqual(acts.map((a) => a[1]), ['ping', 'dead']);
  assert.equal(acts[1][0] - acts[0][0], DEAD_AFTER_MS);
});

test('an answer to the ping keeps the line, the next ping comes 5 s later', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable();
  let acts = run(h, c, 5000); assert.deepEqual(acts.map((a) => a[1]), ['ping']);
  c.add(100); h.touchRx();                                  // pong
  acts = run(h, c, 12_000); assert.deepEqual(acts.map((a) => a[1]), ['ping']);   // one new ping, no dead
});

test('traffic in both directions means no ping at all', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable();
  const out = [];
  for (let i = 0; i < 60; i++) { c.add(1000); h.touchRx(); h.touchTx(); const a = h.tick(); if (a) out.push(a); }
  assert.deepEqual(out, []);
});

test('incoming only (sound) still pings so the server hears from us', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable();
  const out = [];
  for (let i = 0; i < 12; i++) { c.add(1000); h.touchRx(); const a = h.tick(); if (a) out.push(a); if (a === 'ping') h.touchRx(); }
  assert.ok(out.includes('ping'));
  assert.ok(!out.includes('dead'));
});

test('any traffic counts as an answer, not only a pong', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable();
  run(h, c, 5000);                                           // ping out
  c.add(8000); h.touchRx();                                  // a picture arrives late but arrives
  assert.ok(!run(h, c, 3000).some((a) => a[1] === 'dead'));
});

test('back from background: short absence does nothing, longer sends a probe with a short deadline', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable(); h.touchRx();
  assert.equal(h.visible(500), null);
  assert.equal(h.visible(30_000), 'ping');
  // no answer: dead after PROBE_MS, not after 10 s
  const acts = run(h, c, 3000);
  assert.deepEqual(acts.map((a) => a[1]), ['dead']);
  assert.ok(acts[0][0] - 1_000_000 <= PROBE_MS + 1000);
});

test('back from background: an answer within the probe keeps the line', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable();
  assert.equal(h.visible(60_000), 'ping');
  c.add(300); h.touchRx();
  assert.deepEqual(run(h, c, 4000), []);
});

test('back from background with a ping already open only shortens its deadline', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable();
  run(h, c, 5000);                                           // normal ping open, deadline +10 s
  assert.equal(h.visible(20_000), null);
  assert.deepEqual(run(h, c, 3000).map((a) => a[1]), ['dead']);
});

test('without the server announcing it, coming back keeps the old rule (reconnect after more than 4 s)', () => {
  const h = new LinkHealth(clock().now);
  assert.equal(h.visible(3000), null);
  assert.equal(h.visible(5000), 'reconnect');
});

test('a frozen page (timers stood still) probes at once with a short deadline instead of waiting', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable(); h.touchRx();
  c.add(120_000);                                            // no tick for two minutes
  assert.equal(h.tick(), 'ping');
  assert.equal(h.deadline - c.t, PROBE_MS);
});

test('a frozen page with a ping already open gets a fresh short deadline, not an instant "dead"', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable();
  run(h, c, 5000);                                           // ping open
  c.add(300_000);
  assert.equal(h.tick(), null);                              // the answer may be waiting in the queue
  assert.equal(h.deadline - c.t, PROBE_MS);
  h.touchRx(); h.touchTx();
  assert.deepEqual(run(h, c, 3000), []);
});

test('clock going backwards does not make the line look dead', () => {
  const c = clock(), h = new LinkHealth(c.now); h.enable(); h.touchRx();
  c.t -= 600_000;
  assert.equal(h.tick(), null);
});

test('reset forgets the announcement (a new connection starts as old-server until told)', () => {
  const h = new LinkHealth(clock().now); h.enable(); h.reset();
  assert.equal(h.enabled, false);
});

test('stats: one interruption counts once, however many events report it', () => {
  const c = clock(), s = new LinkStats(c.now);
  assert.equal(s.lost('silence'), true);
  assert.equal(s.lost('closed'), false);
  assert.equal(s.count, 1); assert.equal(s.lastReason, 'silence');
  c.add(900); assert.equal(s.restored(), 900);
  assert.equal(s.restored(), 0);
  s.lost('closed'); assert.equal(s.count, 2); assert.equal(s.lastReason, 'closed');
});

test('stats: resumed within the quiet window stays quiet, later it turns loud', () => {
  const c = clock(), s = new LinkStats(c.now);
  s.lost('silence');
  assert.equal(s.loud(), false); assert.equal(s.untilLoud(), QUIET_MS);
  c.add(1000); assert.equal(s.loud(), false); assert.equal(s.untilLoud(), QUIET_MS - 1000);
  c.add(QUIET_MS - 1000); assert.equal(s.loud(), true); assert.equal(s.untilLoud(), 0);
  s.restored(); assert.equal(s.loud(), false); assert.equal(s.lastQuiet, false);
  s.lost('closed'); c.add(1500); s.restored(); assert.equal(s.lastQuiet, true);
});

test('summary: empty until the first reconnect, then count and reason', () => {
  const c = clock(), s = new LinkStats(c.now);
  assert.equal(linkSummary(s, () => 'x'), '');
  s.lost('silence');
  assert.equal(linkSummary(s, (r) => r === 'silence' ? 'Netz still' : ''), '1 · Netz still');
  assert.equal(linkSummary(s, () => ''), '1 · silence');
});
