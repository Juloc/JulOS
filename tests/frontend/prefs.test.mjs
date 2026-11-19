import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createPrefs } from '../../src/JulOS.Frontend/wwwroot/prefs.js';

const mem = (init = {}) => { const m = new Map(Object.entries(init)); return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)), removeItem: (k) => m.delete(k) }; };
const THEME = { seed: '#4f86f7', scheme: 'dark' };
const KEYBAR = { rows: [{ id: 'keys', title: 'Tasten', items: [] }] };

/** Ein Server im Speicher; fail: alles schlaegt fehl (offline). */
function fakeServer(initial = {}) {
  const s = { data: { ...initial }, calls: [], fail: false };
  s.api = async (path, o = {}) => {
    s.calls.push([o.method || 'GET', path, o.body && o.body.value]);
    if (s.fail) throw new Error('offline');
    if (path === '/api/settings') return { settings: { ...s.data } };
    const key = path.split('/').pop();
    if (o.method === 'PUT') s.data[key] = o.body.value; else if (o.method === 'DELETE') delete s.data[key];
    return null;
  };
  return s;
}
// Zeitgeber, den der Test von Hand weiterdreht
function clock() { const q = []; return { schedule: (fn) => { q.push(fn); return q.length; }, cancel: (id) => { q[id - 1] = null; }, run: async () => { for (const fn of q.splice(0)) if (fn) await fn(); } }; }

test('eine Aenderung geht erst nach der Verzoegerung zum Server und mehrere werden zu einer', async () => {
  const srv = fakeServer(), clk = clock(), storage = mem();
  const p = createPrefs({ api: srv.api, storage, local: () => ({}), schedule: clk.schedule, cancel: clk.cancel });
  p.push('theme', { seed: '#111111', scheme: 'dark' });
  p.push('theme', THEME);
  assert.deepEqual(srv.calls, []);
  assert.match(storage.getItem('pd.prefs.pending'), /"theme":"set"/);
  await clk.run();
  assert.deepEqual(srv.calls, [['PUT', '/api/settings/theme', THEME]]);
  assert.equal(storage.getItem('pd.prefs.pending'), null);
});

test('unbekannte Schluessel werden nicht gesendet', async () => {
  const srv = fakeServer(), clk = clock();
  const p = createPrefs({ api: srv.api, storage: mem(), local: () => ({}), schedule: clk.schedule, cancel: clk.cancel });
  p.push('sonstwas', { a: 1 });
  await clk.run();
  assert.deepEqual(srv.calls, []);
});

test('Abgleich: was der Server hat, gilt', async () => {
  const srv = fakeServer({ theme: THEME, keybar: KEYBAR });
  const p = createPrefs({ api: srv.api, storage: mem(), local: () => ({ theme: { seed: 'system', scheme: 'auto' } }) });
  assert.deepEqual(await p.sync(), { theme: THEME, keybar: KEYBAR });
  assert.deepEqual(srv.calls.map((c) => c[0]), ['GET']);                       // nichts hochgeladen
});

test('Abgleich beim ersten Aufruf: hat der Server nichts, geht das Lokale hinaus', async () => {
  const srv = fakeServer();
  const p = createPrefs({ api: srv.api, storage: mem(), local: () => ({ theme: THEME, keybar: null }) });
  assert.deepEqual(await p.sync(), {});
  assert.deepEqual(srv.data, { theme: THEME });                                  // die Tastenleiste war nie veraendert: nichts hochladen
});

test('Abgleich: eine offene lokale Aenderung geht vor dem Wert des Servers', async () => {
  const srv = fakeServer({ theme: { seed: 'system', scheme: 'auto' } });
  const storage = mem({ 'pd.prefs.pending': JSON.stringify({ theme: 'set' }) });
  const p = createPrefs({ api: srv.api, storage, local: () => ({ theme: THEME }) });
  assert.deepEqual(await p.sync(), {});
  assert.deepEqual(srv.data.theme, THEME);
  assert.equal(storage.getItem('pd.prefs.pending'), null);
});

test('Zuruecksetzen: der Server vergisst den Wert, und er kommt nicht zurueck', async () => {
  const srv = fakeServer({ keybar: KEYBAR });
  const clk = clock(), storage = mem();
  const p = createPrefs({ api: srv.api, storage, local: () => ({ keybar: null }), schedule: clk.schedule, cancel: clk.cancel });
  p.reset('keybar');
  await clk.run();
  assert.deepEqual(srv.data, {});
  assert.deepEqual(await p.sync(), {});
});

test('Rueckfall: ohne Server bleibt die Aenderung vermerkt und geht beim naechsten Abgleich hinaus', async () => {
  const srv = fakeServer(), clk = clock(), storage = mem();
  let mine = {};
  const p = createPrefs({ api: srv.api, storage, local: () => mine, schedule: clk.schedule, cancel: clk.cancel });
  srv.fail = true;
  p.push('theme', THEME); mine = { theme: THEME };
  await clk.run();
  assert.match(storage.getItem('pd.prefs.pending'), /theme/);
  assert.deepEqual(await p.sync(), {});                                          // offline: nichts uebernommen, nichts verloren
  srv.fail = false;
  // neuer Start der Seite: ein frisches Objekt mit demselben Speicher
  const p2 = createPrefs({ api: srv.api, storage, local: () => mine });
  assert.deepEqual(await p2.sync(), {});
  assert.deepEqual(srv.data.theme, THEME);
  assert.equal(storage.getItem('pd.prefs.pending'), null);
});

test('Abgleich bei einem Fehler des Servers laesst alles lokal', async () => {
  const srv = fakeServer({ theme: THEME });
  srv.fail = true;
  assert.deepEqual(await createPrefs({ api: srv.api, storage: mem(), local: () => ({ theme: THEME }) }).sync(), {});
});

test('die Sprache ist eine Einstellung wie die anderen: lokal gemerkt, zum Server geschickt, vom Server uebernommen', async () => {
  const srv = fakeServer(), clk = clock(), storage = mem();
  const p = createPrefs({ api: srv.api, storage, local: () => ({ lang: { lang: 'en' } }), schedule: clk.schedule, cancel: clk.cancel });
  assert.deepEqual(await p.sync(), {});                       // der Server hat nichts: das Geraet liefert seine Wahl
  assert.deepEqual(srv.data.lang, { lang: 'en' });
  const q = createPrefs({ api: srv.api, storage: mem(), local: () => ({ lang: { lang: 'de' } }), schedule: clk.schedule, cancel: clk.cancel });
  assert.deepEqual(await q.sync(), { lang: { lang: 'en' } }); // der Server hat Vorrang
});
