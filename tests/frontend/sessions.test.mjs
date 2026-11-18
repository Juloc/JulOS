// Verwalter und Basisklasse der Sitzungen (sessions.js): reine Logik, kein Browser noetig.
import test from 'node:test';
import assert from 'node:assert/strict';
import { Session, Sessions, newSid } from '../../src/JulOS.Frontend/wwwroot/sessions.js';

class Fake extends Session {
  constructor(conn, o) { super({ id: o.sid, conn, title: o.title }); this.log = []; }
  async connect(pw) { this.log.push('connect:' + pw); }
  dispose() { this.log.push('dispose'); }
}
Sessions.registerFactory('desktop', (c, o) => new Fake(c, o));
Sessions.registerFactory('terminal', (c, o) => new Fake(c, o));

const conn = (id, name, kind = 'desktop') => ({ id, name, kind });
const events = () => { const log = []; for (const t of ['opened', 'closed', 'activated']) Sessions.addEventListener(t, (e) => log.push([t, e.detail.session.title, !!e.detail.reveal])); return log; };

test('session ids are safe for the server slot (8 to 40 chars of letters, digits, - and _)', () => {
  for (let i = 0; i < 50; i++) assert.match(newSid(), /^[A-Za-z0-9_-]{8,40}$/);
  assert.notEqual(newSid(), newSid());
});

test('open connects, activates and numbers a second session to the same target', () => {
  Sessions.closeAll();
  const log = events();
  const a = Sessions.open(conn('1', 'Server'), { password: 'pw' });
  assert.equal(a.title, 'Server');
  assert.deepEqual(a.log, ['connect:pw']);
  assert.equal(Sessions.active, a);
  assert.equal(Sessions.open(conn('1', 'Server')), a, 'already open: the same session is brought forward');
  const b = Sessions.open(conn('1', 'Server'), { extra: true });
  assert.equal(b.title, 'Server 2');
  assert.notEqual(a.id, b.id);
  assert.equal(Sessions.open(conn('1', 'Server'), { extra: true }).title, 'Server 3');
  assert.equal(Sessions.count, 3);
  assert.deepEqual(Sessions.find('1').map((s) => s.title), ['Server', 'Server 2', 'Server 3']);
  assert.ok(log.some(([t, title, reveal]) => t === 'activated' && title === 'Server' && reveal), 'reveal on re-open');
  Sessions.closeAll();
});

test('closing frees the title number and activates the most recently used session', () => {
  Sessions.closeAll();
  const a = Sessions.open(conn('1', 'A'));
  const b = Sessions.open(conn('2', 'B', 'terminal'));
  const c = Sessions.open(conn('3', 'C'));
  Sessions.activate(a.id);
  Sessions.activate(c.id);
  Sessions.close(c.id, 'weg', 'x');
  assert.equal(Sessions.active, a, 'a was used more recently than b');
  assert.deepEqual(c.log.slice(-1), ['dispose']);
  const again = Sessions.open(conn('3', 'C'), { extra: true });
  assert.equal(again.title, 'C');
  Sessions.close(b.id);
  Sessions.close(a.id);
  assert.equal(Sessions.active, again);
  Sessions.closeAll();
  assert.equal(Sessions.count, 0);
  assert.equal(Sessions.active, null);
});

test('activate only reports a change, unless the session should be revealed', () => {
  Sessions.closeAll();
  const a = Sessions.open(conn('1', 'A'));
  const log = events(); log.length = 0;
  Sessions.activate(a.id);
  assert.equal(log.length, 0);
  Sessions.activate(a.id, { reveal: true });
  assert.deepEqual(log, [['activated', 'A', true]]);
  Sessions.closeAll();
});

test('a closerequest from a session closes it and carries the message', () => {
  Sessions.closeAll();
  const seen = [];
  Sessions.addEventListener('closed', (e) => seen.push([e.detail.message, e.detail.reason]));
  const a = Sessions.open(conn('1', 'A'));
  a.requestClose('Anmeldung fehlgeschlagen', 'login');
  assert.equal(Sessions.count, 0);
  assert.deepEqual(seen.at(-1), ['Anmeldung fehlgeschlagen', 'login']);
});

test('state and title changes are reported', () => {
  Sessions.closeAll();
  const a = Sessions.open(conn('1', 'A'));
  const seen = [];
  Sessions.addEventListener('statechange', () => seen.push(a.state));
  a.setState('connected', 'Verbunden');
  a.setState('connected', 'Verbunden');   // unchanged: no event
  a.setState('error', 'Fehler');
  assert.deepEqual(seen, ['connected', 'error']);
  Sessions.closeAll();
});
