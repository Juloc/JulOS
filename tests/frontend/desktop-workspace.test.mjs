import { test } from 'node:test';
import assert from 'node:assert/strict';
import { newDeviceId, isDeviceId, deviceName, buildDoc, planRestore, fitIntoView, resolveConflict, retryDelay } from '../../src/JulOS.Frontend/wwwroot/desktop-workspace.js';

const C1 = '3f2b8c1e-5a47-4d0b-9c36-0a1b2c3d4e5f', C2 = '7a9d1e22-1b3c-4f55-8e60-123456789abc';
const app = (id, a, over = {}) => ({ id, kind: 'app', app: a, x: 10, y: 40, w: 800, h: 600, z: 5, ...over });
const conn = (id, c, over = {}) => ({ id, kind: 'connection', connectionId: c, x: 0, y: 34, w: 1000, h: 680, z: 6, ...over });
const VIEW = { w: 1500, h: 900 };

test('Geraetekennung: zufaellig, 22 Zeichen im erlaubten Alphabet, zwei Aufrufe verschieden', () => {
  const rnd = (b) => { for (let i = 0; i < b.length; i++) b[i] = Math.floor(Math.random() * 256); };
  const a = newDeviceId(rnd), b = newDeviceId(rnd);
  assert.match(a, /^[A-Za-z0-9_-]{22}$/); assert.ok(isDeviceId(a)); assert.notEqual(a, b);
  assert.equal(isDeviceId('kurz'), false); assert.equal(isDeviceId('mit leerzeichen drin'), false); assert.equal(isDeviceId(null), false);
});

test('Geraetename aus dem User-Agent: Browser und System, hoechstens 40 Zeichen, nie leer', () => {
  assert.equal(deviceName('Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36'), 'Chrome · Linux');
  assert.equal(deviceName('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130 Safari/537.36 Edg/130'), 'Edge · Windows');
  assert.equal(deviceName('', 'MacIntel'), 'MacIntel');
  assert.equal(deviceName('', ''), 'Browser');
  assert.ok(deviceName('x', 'y'.repeat(100)).length <= 40);
});

test('Dokument: Geometrie, Zustand, Stapelfolge als 1..n, Fokus, Dock-Reihenfolge', () => {
  const doc = buildDoc([
    app('home', 'home', { z: 12, active: true }),
    app('files', 'files', { z: 11, maximized: true, restore: { x: 20, y: 50, w: 700.4, h: 500.6 } }),
    conn('s:abc', C1.toUpperCase(), { z: 13, snap: 'left', restore: { x: 5, y: 5, w: 400, h: 300 } }),
    app('caddy', 'caddy', { z: 14, minimized: true, maximized: true, snap: 'max', restore: { x: 1, y: 2, w: 3, h: 4 } }),
  ], VIEW, true);
  assert.deepEqual(doc.screens, [{ id: 's1', x: 0, y: 0, w: 1500, h: 900 }]);
  assert.deepEqual(doc.windows.map((w) => [w.id, w.z, w.state]), [['home', 2, 'normal'], ['files', 1, 'max'], ['s:abc', 3, 'snapped'], ['caddy', 4, 'min']]);
  assert.deepEqual(doc.windows[1].restore, { x: 20, y: 50, w: 700, h: 501 });
  assert.equal(doc.windows[1].snap, undefined);                      // maximiert: die Zone steht schon im Zustand
  assert.equal(doc.windows[2].snap, 'left'); assert.equal(doc.windows[2].connectionId, C1);
  assert.equal(doc.windows[3].snap, 'max');                          // minimiert merkt sich, was darunter war
  assert.equal(doc.focus, 'home');
  assert.deepEqual(doc.dock, { collapsed: true, side: 'bottom', order: ['home', 'files', 's:abc', 'caddy'] });
});

test('Dokument: was der Server ablehnen wuerde, bleibt draussen; nie ein Passwort oder Ziel', () => {
  const doc = buildDoc([
    app('ok', 'files'), app('ok', 'files'),                           // doppelte Kennung
    app('bad id!', 'files'), app('x', 'Files With Space'),            // Kennung und App ungueltig
    conn('q', 'quick:desktop:host:3389:user'),                        // Quick Connect: keine UUID, nicht ins Dokument
    { ...conn('s:1', C2), password: 'geheim', host: '10.0.0.5', target: 'a@b:22' },
  ], VIEW);
  assert.deepEqual(doc.windows.map((w) => w.id), ['ok', 's:1']);
  const text = JSON.stringify(doc);
  assert.ok(!/geheim|10\.0\.0\.5|a@b|password|host|target/.test(text));
  assert.equal(doc.focus, undefined);
  assert.equal(buildDoc(Array.from({ length: 40 }, (_, i) => app('w' + i, 'files')), VIEW).windows.length, 32);
  assert.deepEqual(buildDoc([app('a', 'files', { w: 0, h: -5, x: NaN })], VIEW).windows[0], { id: 'a', kind: 'app', app: 'files', x: 0, y: 40, w: 1, h: 1, z: 1, state: 'normal' });
});

test('Wiederherstellen: erst Apps, dann Verbindungen, je in der Reihenfolge des Docks; Unbekanntes und Geloeschtes faellt weg', () => {
  const doc = { windows: [
    { id: 'c1', kind: 'connection', connectionId: C1, z: 3 }, { id: 'home', kind: 'app', app: 'settings', z: 1 }, { id: 'c2', kind: 'connection', connectionId: C2, z: 4 },
    { id: 'f', kind: 'app', app: 'files', z: 2 }, { id: 'old', kind: 'app', app: 'neue-app', z: 5 }, { id: 'home2', kind: 'app', app: 'hosts', z: 6 },
  ], focus: 'c1', dock: { order: ['f', 'c2', 'c1', 'home'] } };
  const appKey = (a) => ({ settings: 'home', hosts: 'home', files: 'files' })[a] || null;
  const plan = planRestore(doc, { appKey, connections: new Set([C1]) });          // C2 wurde geloescht
  assert.deepEqual(plan.apps.map((w) => w.id), ['f', 'home']);                    // 'hosts' meint dasselbe Fenster wie 'settings': nur eines
  assert.deepEqual(plan.conns.map((w) => w.id), ['c1']);
  assert.deepEqual(plan.order, ['f', 'home', 'c1']);
  assert.equal(plan.focus, 'c1');
  assert.equal(planRestore({ windows: [{ id: 'c2', kind: 'connection', connectionId: C2 }], focus: 'c2' }, { appKey, connections: new Set() }).focus, null);
  assert.deepEqual(planRestore(null, { appKey, connections: new Set() }), { apps: [], conns: [], order: [], focus: null });
});

test('Ins Sichtbare schieben: kleinerer Bildschirm, negative Werte, Leiste oben, Mindestgroesse', () => {
  const v = { w: 1000, h: 700 };
  assert.deepEqual(fitIntoView({ x: 900, y: 600, w: 800, h: 600 }, v, 34, 320, 220), { w: 800, h: 600, x: 200, y: 100 });     // 1500x900 -> 1000x700: nach links oben geschoben
  assert.deepEqual(fitIntoView({ x: -50, y: 0, w: 400, h: 300 }, v, 34, 320, 220), { w: 400, h: 300, x: 0, y: 34 });
  assert.deepEqual(fitIntoView({ x: 0, y: 0, w: 1800, h: 1200 }, v, 34, 320, 220), { w: 1000, h: 666, x: 0, y: 34 });            // zu gross: passt sich an
  assert.deepEqual(fitIntoView({ x: 10, y: 50, w: 100, h: 100 }, v, 34, 320, 220), { w: 320, h: 220, x: 10, y: 50 });            // zu klein: Mindestgroesse
  assert.deepEqual(fitIntoView({ x: 100, y: 100, w: 400, h: 300 }, v, 34, 320, 220), { w: 400, h: 300, x: 100, y: 100 });        // liegt gut: bleibt
});

test('Konflikt: die neuere Aenderung gilt; ein vergessenes Geraet wird neu angelegt', () => {
  assert.deepEqual(resolveConflict({ pendingAgeMs: 900, serverAgeMs: 60000, version: 7, hasDoc: true }), { action: 'overwrite', version: 7 });     // der Server ist alt: wir schreiben darueber
  assert.deepEqual(resolveConflict({ pendingAgeMs: 5000, serverAgeMs: 200, version: 8, hasDoc: true }), { action: 'adopt', version: 8 });          // der andere schrieb deutlich nach unserer Aenderung
  assert.deepEqual(resolveConflict({ pendingAgeMs: 1000, serverAgeMs: 0, version: 8, hasDoc: true }), { action: 'overwrite', version: 8 });        // fast gleichzeitig: wir (Rundung der Sekunden)
  assert.deepEqual(resolveConflict({ pendingAgeMs: 100, serverAgeMs: NaN, version: 3, hasDoc: true }), { action: 'overwrite', version: 3 });        // unlesbare Zeit: lieber den eigenen Stand sichern
  assert.deepEqual(resolveConflict({ pendingAgeMs: 100, serverAgeMs: 5, version: 0, hasDoc: false }), { action: 'overwrite', version: 0 });
});

test('Wartezeit nach Fehlern waechst bis 60 s; Retry-After gilt mindestens', () => {
  assert.deepEqual([1, 2, 3, 4, 5, 6, 9].map((n) => retryDelay(n)), [2000, 4000, 8000, 16000, 32000, 60000, 60000]);
  assert.equal(retryDelay(1, 30), 30000);
  assert.equal(retryDelay(1, 99999), 300000);
  assert.equal(retryDelay(7, 10), 60000);
});
