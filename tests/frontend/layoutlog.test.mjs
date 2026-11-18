// Tests der Groessen-Logik des Handy-Clients (ohne Browser): node --test tests/frontend
import test from 'node:test';
import assert from 'node:assert/strict';
import { SizeGate, SizeCounter } from '../../src/JulOS.Frontend/wwwroot/layoutlog.js';

const last = { w: 1100, h: 2000 };

test('Gate: kleine Schwankungen werden ignoriert', () => {
  const g = new SizeGate();
  assert.equal(g.decide(0, { w: 1104, h: 2010 }, last).action, 'skip');
  assert.equal(g.decide(0, { w: 1100 + 12, h: 2000 }, last).action, 'send');
  assert.equal(g.decide(0, { w: 1100, h: 2000 + 20 }, last).action, 'send');
});

test('Gate: waehrend die Tastatur faehrt wird gewartet, danach gesendet', () => {
  const g = new SizeGate();
  const want = { w: 1100, h: 1200 };
  const d = g.decide(1000, want, last, { holdUntil: 1600 });
  assert.equal(d.action, 'wait'); assert.equal(d.why, 'hold'); assert.ok(d.delay >= 600);
  assert.equal(g.decide(1700, want, last, { holdUntil: 1600 }).action, 'send');
});

test('Gate: hoechstens eine Aenderung pro Sekunde', () => {
  const g = new SizeGate();
  g.sent(10000);
  const d = g.decide(10400, { w: 900, h: 1500 }, last);
  assert.equal(d.action, 'wait'); assert.equal(d.why, 'gap'); assert.ok(d.delay >= 600 && d.delay < 700);
  assert.equal(g.decide(11100, { w: 900, h: 1500 }, last).action, 'send');
});

test('Gate: Notbremse nach 6 Aenderungen in 20 s, danach wieder frei', () => {
  const g = new SizeGate();
  for (let i = 0; i < 6; i++) g.sent(i * 1500);                 // 0 ... 7.5 s
  const d = g.decide(9000, { w: 800, h: 1400 }, last);
  assert.equal(d.action, 'wait'); assert.equal(d.why, 'storm'); assert.equal(d.delay, 4000);
  assert.equal(g.decide(30000, { w: 800, h: 1400 }, last).action, 'send');
});

test('Gate: ein erzwungener Wechsel geht immer durch', () => {
  const g = new SizeGate(); g.sent(0);
  assert.equal(g.decide(10, last, last, { force: true, holdUntil: 99999 }).action, 'send');
});

test('Gate: Daueraenderung (jede 100 ms eine neue Groesse) ergibt nur wenige Sendungen', () => {
  const g = new SizeGate(); let sent = 0, lastSent = { ...last }, t = 0;
  for (let i = 0; i < 200; i++) {
    t += 100;
    const want = { w: 900 + (i % 2) * 300, h: 1500 };           // springt dauernd zwischen zwei Groessen
    const d = g.decide(t, want, lastSent);
    if (d.action === 'send') { g.sent(t); lastSent = want; sent++; }
  }
  assert.ok(sent <= 8, 'gesendet: ' + sent);
});

test('Zaehler: zaehlt je Art im 10-s-Fenster und erkennt Schleifen', () => {
  const c = new SizeCounter(10000, 12);
  for (let i = 0; i < 6; i++) c.note('stage', i * 500);
  c.note('send', 3000); c.note('remote', 3200);
  assert.deepEqual(c.counts(4000), { n: 8, send: 1, remote: 1, stage: 6, conn: 0 });
  assert.equal(c.looping(4000), false);
  for (let i = 0; i < 5; i++) c.note('conn', 4000 + i);
  assert.equal(c.looping(4010), true);
  assert.equal(c.shouldReport(4010), true);
  assert.equal(c.shouldReport(6000), false);                   // nur einmal je Fenster
  assert.equal(c.looping(20000), false);                       // alt genug: wieder ruhig
});
