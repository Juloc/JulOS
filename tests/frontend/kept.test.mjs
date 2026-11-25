import { test } from 'node:test';
import assert from 'node:assert/strict';
import { decide, reclaim, remember, recall, forget, MAX_WAITS } from '../../src/JulOS.Frontend/wwwroot/kept.js';

const memory = () => { const m = new Map(); return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, v), removeItem: (k) => m.delete(k), size: () => m.size }; };
const CONN = 'c1';
const noSleep = () => Promise.resolve();

test('decide: keine Liste oder keine Sitzung heisst neue Sitzung', () => {
  assert.deepEqual(decide(null, 'a'), { action: 'new' });
  assert.deepEqual(decide([], 'a'), { action: 'new' });
  assert.deepEqual(decide([], null), { action: 'new' });
});

test('decide: nur die eigene, unbeobachtete Sitzung wird still genommen, jede andere wird erfragt (die zuletzt begonnene)', () => {
  assert.deepEqual(decide([{ id: 'a', unwatchedSince: 't' }, { id: 'b', unwatchedSince: 't' }], 'b'), { action: 'take', id: 'b' });
  assert.deepEqual(decide([{ id: 'a', unwatchedSince: null }], 'a'), { action: 'wait' });          // noch angesehen: gleich noch einmal fragen
  const other = { id: 'a', since: '2026-11-25T08:00:00Z', unwatchedSince: 't' };
  assert.deepEqual(decide([other], 'x'), { action: 'ask', item: other });                         // ein anderes Geraet: nie still nehmen
  assert.deepEqual(decide([other], null), { action: 'ask', item: other });                        // das Handy ohne sessionStorage
  const watched = { id: 'b', since: '2026-11-25T09:00:00Z', unwatchedSince: null, device: 'Firefox, Linux' };
  assert.deepEqual(decide([other, watched], null), { action: 'ask', item: watched });
  // dieses Geraet hatte sie zuletzt und niemand sieht sie an (Tab zu und wieder auf): still; sieht ein anderer Tab desselben Geraets sie an: fragen
  assert.deepEqual(decide([{ ...other, own: true }, watched], null), { action: 'take', id: 'a' });
  assert.deepEqual(decide([{ ...watched, own: true }], null), { action: 'ask', item: { ...watched, own: true } });
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

test('reclaim: ohne erhaltene Sitzung gibt es keinen Dialog, nur eine neue Sitzung', async () => {
  let dialogs = 0;
  const got = await reclaim(CONN, { storage: memory(), sleep: noSleep, list: async () => [], take: async () => ({ ticket: 't' }), ask: async () => { dialogs++; return 'take'; } });
  assert.equal(got, null); assert.equal(dialogs, 0);
});

test('Uebergabe: "Hierher holen" nimmt mit Bestaetigung, "Neue Sitzung" beendet die alte zuerst, "Abbrechen" baut nichts auf', async () => {
  const desktop = { id: 'd1', since: 't0', unwatchedSince: null, device: 'Firefox, Linux' };
  const run = async (answer) => {
    const calls = [];
    const got = await reclaim(CONN, { storage: memory(), sleep: noSleep, list: async () => [desktop],
      take: async (id, confirm) => { calls.push(['take', id, confirm]); return { ticket: 'T' }; },
      end: async (id) => { calls.push(['end', id]); },
      ask: async (item) => { calls.push(['ask', item.device]); return answer; } });
    return { got, calls };
  };
  assert.deepEqual(await run('take'), { got: { ticket: 'T' }, calls: [['ask', 'Firefox, Linux'], ['take', 'd1', true]] });
  assert.deepEqual(await run('new'), { got: null, calls: [['ask', 'Firefox, Linux'], ['end', 'd1']] });
  assert.deepEqual(await run('cancel'), { got: { cancelled: true }, calls: [['ask', 'Firefox, Linux']] });
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

test('reclaim: dauerhaft angesehen (kopierter Tab) fragt nach MAX_WAITS, statt still eine neue Sitzung aufzubauen', async () => {
  const s = memory(); remember(s, CONN, 'h1');
  let listed = 0, took = 0, dialogs = 0;
  const got = await reclaim(CONN, { storage: s, sleep: noSleep, list: async () => { listed++; return [{ id: 'h1', unwatchedSince: null }]; }, take: async () => { took++; return { ticket: 'T' }; }, ask: async () => { dialogs++; return 'cancel'; } });
  assert.deepEqual(got, { cancelled: true }); assert.equal(listed, MAX_WAITS + 1); assert.equal(took, 0); assert.equal(dialogs, 1);
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
