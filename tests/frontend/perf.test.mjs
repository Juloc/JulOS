// Leistungsanzeige (perf.js): Bilder je Sekunde, Datenrate, Reaktionszeit und die Stufen der Bildrate.
import test from 'node:test';
import assert from 'node:assert/strict';
import { StreamPerf, formatRate, perfItems, QUALITIES, normalizeQuality, nextQuality, qualityOf, REACTION_WINDOW_MS } from '../../src/JulOS.Frontend/wwwroot/perf.js';

test('frames and bytes per second come from the counters between two samples', () => {
  const p = new StreamPerf();
  p.sample(1000);
  for (let i = 0; i < 60; i++) p.drawn(1000 + i * 16, i + 1);
  p.received(250_000);
  const s = p.sample(2000);
  assert.equal(Math.round(s.fps), 60);
  assert.equal(Math.round(s.kbps), 2000);                      // 250 000 Byte in einer Sekunde = 2 Mbit/s
  assert.equal(s.reaction, null);
});

test('a sample that is asked for too soon repeats the last values and a quiet second gives zero', () => {
  const p = new StreamPerf();
  p.sample(0);
  p.drawn(100, 1); p.drawn(200, 2);
  assert.equal(Math.round(p.sample(1000).fps), 2);
  p.drawn(1100, 3);
  assert.equal(Math.round(p.sample(1100).fps), 2);             // 100 ms: noch nichts Neues
  assert.equal(p.sample(2000).fps, 1 / 1);
  assert.equal(p.sample(3000).fps, 0);
});

test('the reaction time runs from an input to the first picture that arrived after it', () => {
  const p = new StreamPerf();
  p.input(1000, 10);                                           // Klick, zuletzt empfangenes Bild war Nr. 10
  p.drawn(1010, 10);                                           // ein Bild, das schon unterwegs war: zaehlt nicht
  assert.equal(p.reaction(1010), null);
  p.drawn(1040, 11);
  assert.equal(p.reaction(1040), 40);
  p.drawn(1100, 12);                                           // ohne neue Eingabe aendert sich nichts
  assert.equal(p.reaction(1100), 40);
  assert.equal(p.reaction(1040 + 25_000), null);               // lange her: nicht mehr zeigen
});

test('a picture that comes long after the input is not its answer', () => {
  const p = new StreamPerf();
  p.input(0, 1);
  p.drawn(REACTION_WINDOW_MS + 500, 2);
  assert.equal(p.reaction(REACTION_WINDOW_MS + 500), null);
});

test('the first of several quick inputs counts, and the median ignores one outlier', () => {
  const p = new StreamPerf();
  p.input(0, 1); p.input(20, 1); p.input(40, 1);
  p.drawn(60, 2);
  assert.equal(p.reaction(60), 60);
  for (const ms of [30, 35, 32, 900]) { p.input(10_000, 2); p.drawn(10_000 + ms, 3 + ms); }
  assert.ok(p.reaction(11_000) < 100, 'Median, nicht Mittelwert: ' + p.reaction(11_000));
});

test('rates read as kbit/s below a megabit and as Mbit/s above', () => {
  assert.equal(formatRate(0), '0 kbit/s');
  assert.equal(formatRate(640.4), '640 kbit/s');
  assert.equal(formatRate(2400), '2,4 Mbit/s');
  assert.equal(formatRate(15_000), '15 Mbit/s');
});

test('the items hold what the bar shows, with dashes where nothing is known yet', () => {
  const items = perfItems({ sample: { fps: 58.4, kbps: 3100, reaction: 24 }, width: 1920, height: 1080, encoder: 'OpenH264 (H.264)', quality: 'Sparsam' });
  const by = Object.fromEntries(items.map((i) => [i.id, i.value]));
  assert.deepEqual(by, { fps: '58', reaction: '24 ms', rate: '3,1 Mbit/s', size: '1920×1080', encoder: 'OpenH264 (H.264)', quality: 'Sparsam' });
  const empty = Object.fromEntries(perfItems({}).map((i) => [i.id, i.value]));
  assert.equal(empty.reaction, '–'); assert.equal(empty.size, '–'); assert.equal(empty.encoder, '–'); assert.equal(empty.quality, 'Automatisch');
});

test('the steps match the bytes of message 31 and unknown names fall back to automatic', () => {
  assert.deepEqual(QUALITIES.map((q) => [q.id, q.byte]), [['auto', 0], ['60', 1], ['30', 2], ['eco', 3]]);
  assert.equal(normalizeQuality('30'), '30');
  assert.equal(normalizeQuality('120'), 'auto'); assert.equal(normalizeQuality(null), 'auto');
  assert.equal(qualityOf('eco').label, 'Sparsam');
  assert.equal(nextQuality('auto'), '60'); assert.equal(nextQuality('eco'), 'auto');
});
