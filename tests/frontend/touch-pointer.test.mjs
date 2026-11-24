// Zeigerrechnung des Touchpad-Modus und Zusammenfassen der Mausbewegungen (touch-pointer.js).
import test from 'node:test';
import assert from 'node:assert/strict';
import { padStep, flushesMove, MoveCoalescer } from '../../src/JulOS.Frontend/wwwroot/touch-pointer.js';

const W = 1280, H = 720;

test('the first step starts in the middle of the picture', () => {
  assert.deepEqual(padStep(null, 10, -10, 1, W, H), { x: 650, y: 350 });
});

test('the top-left corner is a real position: it does not jump to the middle (the 0/0 bug)', () => {
  let p = padStep({ x: 5, y: 5 }, -100, -100, 1, W, H);
  assert.deepEqual(p, { x: 0, y: 0 });
  p = padStep(p, -50, -50, 1, W, H);          // pushing on stays in the corner
  assert.deepEqual(p, { x: 0, y: 0 });
  p = padStep(p, 20, 0, 1, W, H);             // and moves on from there, not from the middle
  assert.deepEqual(p, { x: 20, y: 0 });
});

test('edges clamp on every side, also at the bottom right', () => {
  assert.deepEqual(padStep({ x: 3, y: 300 }, -9, 0, 1, W, H), { x: 0, y: 300 });
  assert.deepEqual(padStep({ x: 300, y: 2 }, 0, -9, 1, W, H), { x: 300, y: 0 });
  assert.deepEqual(padStep({ x: W - 1, y: H - 1 }, 50, 50, 1, W, H), { x: W, y: H });
});

test('with a scale (zoomed picture) finger pixels are divided by it and the edges stay the picture edges', () => {
  assert.deepEqual(padStep({ x: 100, y: 100 }, 40, -40, 2, W, H), { x: 120, y: 80 });
  assert.deepEqual(padStep({ x: 10, y: 10 }, -400, -400, 4, W, H), { x: 0, y: 0 });
  assert.deepEqual(padStep({ x: 1275, y: 715 }, 400, 400, 0.5, W, H), { x: W, y: H });
});

test('a button, a wheel step or a changed button flushes a pending move; a plain move does not', () => {
  const last = { left: false, middle: false, right: false, back: false, forward: false };
  assert.equal(flushesMove({ x: 1, y: 1, left: false }, last), false);
  assert.equal(flushesMove({ x: 1, y: 1, left: true }, last), true);
  assert.equal(flushesMove({ x: 1, y: 1, left: false, wheel: 120 }, last), true);
  assert.equal(flushesMove({ x: 1, y: 1, up: true }, last), true);
  assert.equal(flushesMove({ x: 1, y: 1, left: true }, { ...last, left: true }), false);
  assert.equal(flushesMove({ x: 1, y: 1, left: false }, { ...last, left: true }), true);   // release
});

test('coalescing keeps only the latest position, relative steps added up before', () => {
  const c = new MoveCoalescer();
  let p = null;
  for (let i = 0; i < 10; i++) { p = padStep(p, 3, 2, 1, W, H); c.push(p.x, p.y); }
  assert.deepEqual(c.take(), { x: 640 + 30, y: 360 + 20 });      // ten small steps arrive as one move with the sum
  assert.equal(c.take(), null);
});
