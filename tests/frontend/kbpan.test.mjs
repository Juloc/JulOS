// Tests der Tastatur-Logik des Handy-Clients (Modus, Bildflaeche ohne Tastatur, Verschieben): node --test tests/frontend
import test from 'node:test';
import assert from 'node:assert/strict';
import { normalizeKbMode, nextKbMode, kbModeLabel, StageBase, effectiveStage, panOffset, panDrag } from '../../src/JulOS.Frontend/wwwroot/kbpan.js';
import { SizeGate } from '../../src/JulOS.Frontend/wwwroot/layoutlog.js';

test('Modus: Standard ist Bild verschieben, Unbekanntes faellt darauf zurueck, Umschalten wechselt', () => {
  assert.equal(normalizeKbMode(undefined), 'pan');
  assert.equal(normalizeKbMode('quatsch'), 'pan');
  assert.equal(normalizeKbMode('follow'), 'follow');
  assert.equal(nextKbMode('pan'), 'follow');
  assert.equal(nextKbMode('follow'), 'pan');
  assert.equal(nextKbMode('?'), 'follow');
  assert.notEqual(kbModeLabel('pan'), kbModeLabel('follow'));
});

test('StageBase: ohne Tastatur gilt die aktuelle Groesse, offen oder fahrend wird sie nicht kleiner', () => {
  const b = new StageBase();
  assert.equal(b.size(), null);
  b.note(375, 600);
  assert.deepEqual(b.size(), { w: 375, h: 600 });
  b.note(375, 560, { holding: true });            // Tastatur faehrt: Zwischengroesse
  assert.deepEqual(b.size(), { w: 375, h: 600 });
  b.note(375, 300, { kbOpen: true });
  assert.deepEqual(b.size(), { w: 375, h: 600 });
  b.note(375, 540);                               // Tastatur zu, die Flaeche ist (etwa wegen der Fensterleiste) wirklich kleiner
  assert.deepEqual(b.size(), { w: 375, h: 540 });
  b.note(812, 300, { kbOpen: true });             // gedreht: neue Basis, auch bei offener Tastatur
  assert.deepEqual(b.size(), { w: 812, h: 300 });
});

test('effectiveStage: nur im Modus pan mit offener Tastatur die Flaeche ohne Tastatur', () => {
  const base = { w: 375, h: 600 }, small = { w: 375, h: 300 };
  assert.deepEqual(effectiveStage('pan', true, small, base), { w: 375, h: 600 });
  assert.deepEqual(effectiveStage('follow', true, small, base), small);
  assert.deepEqual(effectiveStage('pan', false, small, base), small);
  assert.deepEqual(effectiveStage('pan', true, small, null), small);
  assert.deepEqual(effectiveStage('pan', true, { w: 812, h: 300 }, base), { w: 812, h: 300 });   // andere Breite: Basis gilt nicht
});

test('Modus pan: Tastatur auf und zu aendert die gewuenschte Groesse nicht, es wird nichts gesendet', () => {
  const g = new SizeGate({ settleMs: 400 });
  const dpr = 3, k = 0.8, base = { w: 375, h: 600 };
  const want = (stage) => { const s = effectiveStage('pan', true, stage, base); return { w: Math.floor((s.w * dpr * k) / 4) * 4, h: Math.floor((s.h * dpr * k) / 2) * 2 }; };
  const last = want(base);
  for (const h of [600, 520, 400, 330, 300]) assert.equal(g.decide(1000, want({ w: 375, h }), last).action, 'skip');
});

test('Gate: Entprellung, die Groesse muss settleMs lang gleich bleiben', () => {
  const g = new SizeGate({ settleMs: 400 });
  const last = { w: 1100, h: 2000 };
  g.observe(0, { w: 1100, h: 1200 });
  const d = g.decide(100, { w: 1100, h: 1200 }, last);
  assert.equal(d.action, 'wait'); assert.equal(d.why, 'settle'); assert.ok(d.delay >= 300 && d.delay <= 340);
  assert.equal(g.decide(420, { w: 1100, h: 1200 }, last).action, 'send');
});

test('Gate: jede neue Zwischengroesse startet die Entprellung neu, nur die letzte wird gesendet', () => {
  const g = new SizeGate({ settleMs: 400 });
  const last = { w: 1100, h: 2000 };
  assert.equal(g.decide(0, { w: 1100, h: 1700 }, last).action, 'wait');
  assert.equal(g.decide(250, { w: 1100, h: 1400 }, last).action, 'wait');        // neue Groesse: Uhr von vorn
  assert.equal(g.decide(500, { w: 1100, h: 1400 }, last).action, 'wait');        // erst 250 ms stabil
  assert.equal(g.decide(660, { w: 1100, h: 1400 }, last).action, 'send');
});

test('Gate: kehrt die Groesse vor Ablauf zurueck, wird nichts gesendet (Adressleiste hin und her)', () => {
  const g = new SizeGate({ settleMs: 400 });
  const last = { w: 1100, h: 2000 };
  assert.equal(g.decide(0, { w: 1100, h: 1700 }, last).action, 'wait');
  assert.equal(g.decide(200, { w: 1100, h: 2004 }, last).action, 'skip');
  assert.equal(g.decide(900, { w: 1100, h: 1700 }, last).action, 'wait');         // neue Runde, die Uhr startet neu
});

test('panOffset: bleibt die Stelle sichtbar, aendert sich nichts', () => {
  assert.equal(panOffset({ contentH: 600, viewH: 300, focusY: 150, current: 0 }), 0);
  assert.equal(panOffset({ contentH: 600, viewH: 300, focusY: 400, current: 200 }), 200);
});

test('panOffset: liegt die Stelle unter dem sichtbaren Teil, rueckt sie ins obere Drittel (begrenzt)', () => {
  assert.equal(panOffset({ contentH: 600, viewH: 300, focusY: 500, current: 0 }), 300);
  assert.equal(panOffset({ contentH: 600, viewH: 300, focusY: 450, current: 0 }), 300);   // 450 - 100 = 350, Hoechstwert 300
  assert.equal(panOffset({ contentH: 600, viewH: 300, focusY: 350, current: 0 }), 250);
  assert.equal(panOffset({ contentH: 600, viewH: 300, focusY: 20, current: 300 }), 0);    // oben: zurueck an den Anfang
});

test('panOffset: Bild passt in die Flaeche oder Stelle unbekannt', () => {
  assert.equal(panOffset({ contentH: 300, viewH: 300, focusY: 250, current: 40 }), 0);
  assert.equal(panOffset({ contentH: 600, viewH: 300, focusY: null, current: 120 }), 120);
  assert.equal(panOffset({ contentH: 600, viewH: 300, focusY: null, current: 900 }), 300);
});

test('panDrag: Bild folgt dem Finger, der Rest am Rand geht ans Scrollen', () => {
  assert.deepEqual(panDrag(100, 300, -50), { offset: 150, rest: 0 });             // Finger nach oben: Bild nach oben
  assert.deepEqual(panDrag(100, 300, 40), { offset: 60, rest: 0 });
  assert.deepEqual(panDrag(280, 300, -50), { offset: 300, rest: -30 });
  assert.deepEqual(panDrag(10, 300, 50), { offset: 0, rest: 40 });
  assert.deepEqual(panDrag(0, 0, 25), { offset: 0, rest: 25 });
});
