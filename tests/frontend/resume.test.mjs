// Sitzungserhalt im Browser (resume.js): wann eine erhaltene Sitzung wieder aufgenommen werden darf, und wie lange.
import test from 'node:test';
import assert from 'node:assert/strict';
import { Resume } from '../../src/JulOS.Frontend/wwwroot/resume.js';

const clock = () => { const c = { t: 1_000_000, now: () => c.t, add: (ms) => { c.t += ms; } }; return c; };

test('without a token from the server nothing is resumable', () => {
  const r = new Resume(); r.confirm();
  assert.equal(r.usable(), false);
  r.offered('abc', 0);                                   // the server keeps nothing (0 seconds)
  assert.equal(r.usable(), false);
  r.offered('', 300);
  assert.equal(r.usable(), false);
});

test('a token only counts once a picture was drawn (a failed login must not loop)', () => {
  const r = new Resume(); r.offered('tok', 300);
  assert.equal(r.usable(), false);
  r.confirm();
  assert.equal(r.usable(), true);
});

test('the window closes shortly before the server gives up, counted from the first loss', () => {
  const c = clock(); const r = new Resume(c.now); r.offered('tok', 60); r.confirm();
  assert.equal(r.usable(), true, 'connected, nothing lost yet');
  r.lost();
  c.add(40_000); r.lost();                               // a second "lost" must not restart the clock
  assert.equal(r.usable(), true);
  c.add(16_000);
  assert.equal(r.usable(), false, '56 s of 60 s: the 5 s margin is used up');
});

test('being connected again resets the clock and the retry counter', () => {
  const c = clock(); const r = new Resume(c.now); r.offered('tok', 30); r.confirm();
  r.lost(); r.nextDelay(); r.nextDelay();
  c.add(20_000); r.connected();
  assert.equal(r.usable(), true);
  assert.equal(r.nextDelay(), 300);
});

test('retry delays: the first almost at once, then 1 s growing to 3 s', () => {
  const r = new Resume();
  assert.deepEqual([1, 2, 3, 4, 5].map(() => r.nextDelay()), [300, 1000, 1500, 2000, 2500]);
  assert.equal(r.nextDelay(), 3000);
  assert.equal(r.nextDelay(), 3000);
});

test('a refused token is forgotten for good', () => {
  const r = new Resume(); r.offered('tok', 300); r.confirm();
  r.refused();
  assert.equal(r.usable(), false);
  assert.equal(r.token, null);
});
