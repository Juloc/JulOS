import { test } from 'node:test';
import assert from 'node:assert/strict';
import { decide, reclaim, remember, recall, forget, MAX_WAITS } from '../../src/JulOS.Frontend/wwwroot/kept.js';

const memory = () => { const m = new Map(); return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, v), removeItem: (k) => m.delete(k), size: () => m.size }; };
const CONN = 'c1';
const noSleep = () => Promise.resolve();

test('decide: nichts gemerkt oder keine Liste heisst neue Sitzung', () => {
  assert.deepEqual(decide([{ id: 'a', unwatchedSince: 't' }], null), { action: 'new' });
  assert.deepEqual(decide(null, 'a'), { action: 'new' });
});

test('decide: nur die eigene, unbeobachtete Sitzung wird genommen', () => {
  assert.deepEqual(decide([{ id: 'a', unwatchedSince: 't' }, { id: 'b', unwatchedSince: 't' }], 'b'), { action: 'take', id: 'b' });
  assert.deepEqual(decide([{ id: 'a', unwatchedSince: 't' }], 'x'), { action: 'new' });            // eine einzige fremde unbeobachtete wird nicht still genommen (Schritt 3 fragt)
  assert.deepEqual(decide([{ id: 'a', unwatchedSince: null }], 'a'), { action: 'wait' });          // noch angesehen: gleich noch einmal fragen
});

test('Merken, Erinnern, Vergessen je Verbindung', () => {
  const s = memory();
  remember(s, CONN, 'h1'); remember(s, 'c2', 'h2');
  assert.equal(recall(s, CONN), 'h1');
  forget(s, CONN);
  assert.equal(recall(s, CONN), null);
  assert.equal(recall(s, 'c2'), 'h2');
  remember(s, CONN, ''); remember(null, CONN, 'x');                       // leer oder ohne Speicher: kein Fehler
  assert.equal(recall(s, CONN), null);
});

test('reclaim: ohne gemerkte Kennung wird nicht einmal gefragt', async () => {
  let asked = 0;
  const got = await reclaim(CONN, { storage: memory(), sleep: noSleep, list: async () => { asked++; return []; }, take: async () => ({ ticket: 't' }) });
  assert.equal(got, null); assert.equal(asked, 0);
});

test('reclaim: die eigene unbeobachtete Sitzung kommt mit Ticket, Layout und Laufwerk zurueck', async () => {
  const s = memory(); remember(s, CONN, 'h1');
  const taken = [];
  const got = await reclaim(CONN, { storage: s, sleep: noSleep, list: async () => [{ id: 'h1', unwatchedSince: 't' }], take: async (id) => { taken.push(id); return { ticket: 'T', layout: 'de', drive: 'ro' }; } });
  assert.deepEqual(got, { ticket: 'T', layout: 'de', drive: 'ro' });
  assert.deepEqual(taken, ['h1']);
});

test('reclaim: noch "angesehen" wartet kurz und nimmt, sobald der Server den alten Socket los ist', async () => {
  const s = memory(); remember(s, CONN, 'h1');
  let n = 0, sleeps = 0;
  const got = await reclaim(CONN, { storage: s, sleep: async () => { sleeps++; }, list: async () => [{ id: 'h1', unwatchedSince: ++n >= 3 ? 't' : null }], take: async () => ({ ticket: 'T' }) });
  assert.equal(got.ticket, 'T'); assert.equal(sleeps, 2);
});

test('reclaim: dauerhaft angesehen (kopierter Tab) gibt nach MAX_WAITS auf, ohne die Kennung zu verwerfen', async () => {
  const s = memory(); remember(s, CONN, 'h1');
  let asked = 0, took = 0;
  const got = await reclaim(CONN, { storage: s, sleep: noSleep, list: async () => { asked++; return [{ id: 'h1', unwatchedSince: null }]; }, take: async () => { took++; return { ticket: 'T' }; } });
  assert.equal(got, null); assert.equal(asked, MAX_WAITS + 1); assert.equal(took, 0);
  assert.equal(recall(s, CONN), 'h1');
});

test('reclaim: Sitzung weg oder Nehmen abgelehnt heisst neue Sitzung und vergisst die Kennung', async () => {
  const gone = memory(); remember(gone, CONN, 'h1');
  assert.equal(await reclaim(CONN, { storage: gone, sleep: noSleep, list: async () => [], take: async () => ({ ticket: 'T' }) }), null);
  assert.equal(recall(gone, CONN), null);
  const refused = memory(); remember(refused, CONN, 'h1');
  assert.equal(await reclaim(CONN, { storage: refused, sleep: noSleep, list: async () => [{ id: 'h1', unwatchedSince: 't' }], take: async () => { throw new Error('404'); } }), null);
  assert.equal(recall(refused, CONN), null);
});

test('reclaim: ein Netzfehler bei der Liste ist keine Ausnahme, nur "neue Sitzung" (und die Kennung bleibt)', async () => {
  const s = memory(); remember(s, CONN, 'h1');
  assert.equal(await reclaim(CONN, { storage: s, sleep: noSleep, list: async () => { throw new TypeError('offline'); }, take: async () => ({ ticket: 'T' }) }), null);
  assert.equal(recall(s, CONN), 'h1');
});
