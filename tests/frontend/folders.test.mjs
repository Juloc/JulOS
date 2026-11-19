import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FOLDER_ICON_KEYS, folderIconSvg, mergePlacements, groupByFolder, filterByFolder, favouriteList, recentList, localOrganisation, takeOverLocal } from '../../src/JulOS.Frontend/wwwroot/folders.js';

const c = (id, extra = {}) => ({ id, name: 'C' + id, ...extra });
const mem = (init = {}) => { const m = new Map(Object.entries(init)); return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)), removeItem: (k) => m.delete(k), m }; };

test('die Symbole sind die, die der Server kennt', () => {
  assert.deepEqual(FOLDER_ICON_KEYS, ['folder', 'star', 'home', 'work', 'server', 'cloud', 'lock', 'bolt', 'game', 'school', 'build', 'globe']);
  assert.match(folderIconSvg('home'), /<path d="M10 20v-6/);
  assert.equal(folderIconSvg('gibt-es-nicht'), folderIconSvg('folder'));
});

test('die Einsortierung des Servers kommt auf die Verbindungen, Unbekanntes bleibt ohne Ordner', () => {
  const list = mergePlacements([c('a'), c('b'), c('x')], [{ id: 'a', folderId: 'f1', favorite: true, lastUsedAt: '2026-01-02T00:00:00Z', useCount: 3 }, { id: 'b', favorite: false, useCount: 0 }]);
  assert.deepEqual([list[0].folderId, list[0].favorite, list[0].useCount], ['f1', true, 3]);
  assert.deepEqual([list[1].folderId, list[1].favorite, list[1].lastUsedAt], [null, false, null]);
  assert.deepEqual([list[2].folderId, list[2].favorite], [null, false]);
});

test('gruppieren: Ordner in Reihenfolge, Lose ans Ende, Verbindung mit unbekanntem Ordner gilt als lose', () => {
  const folders = [{ id: 'f1', name: 'Büro' }, { id: 'f2', name: 'Heim' }];
  const list = [c('a', { folderId: 'f2' }), c('b', { folderId: null }), c('d', { folderId: 'f1' }), c('e', { folderId: 'weg' })];
  const g = groupByFolder(folders, list);
  assert.deepEqual(g.map((x) => [x.folder && x.folder.name, x.connections.map((y) => y.id)]), [['Büro', ['d']], ['Heim', ['a']], [null, ['b', 'e']]]);
  assert.equal(groupByFolder([...folders, { id: 'f3', name: 'Leer' }], list).length, 3);                // leere Ordner fallen weg
  assert.equal(groupByFolder([...folders, { id: 'f3', name: 'Leer' }], list, { showEmpty: true }).length, 4);
  assert.deepEqual(groupByFolder([], [c('z')]).map((x) => x.folder), [null]);
  assert.deepEqual(groupByFolder(folders, []), []);
});

test('filtern nach Favoriten, zuletzt benutzt, ohne Ordner und Ordner', () => {
  const folders = [{ id: 'f1', name: 'Büro' }];
  const list = [c('a', { favorite: true, folderId: 'f1' }), c('b', { lastUsedAt: '2026-02-01T00:00:00Z' }), c('d', { folderId: 'weg' })];
  assert.equal(filterByFolder(list, 'all', folders).length, 3);
  assert.deepEqual(filterByFolder(list, 'fav', folders).map((x) => x.id), ['a']);
  assert.deepEqual(filterByFolder(list, 'recent', folders).map((x) => x.id), ['b']);
  assert.deepEqual(filterByFolder(list, 'none', folders).map((x) => x.id), ['b', 'd']);
  assert.deepEqual(filterByFolder(list, 'f1', folders).map((x) => x.id), ['a']);
});

test('Favoriten zuerst, dann meistgenutzte; zuletzt benutzt nach Zeit', () => {
  const list = [c('a', { useCount: 1 }), c('b', { favorite: true, useCount: 0 }), c('d', { useCount: 5 }), c('e', { favorite: true }), c('f')];
  assert.deepEqual(favouriteList(list).map((x) => x.id), ['b', 'e', 'd', 'a']);
  assert.deepEqual(favouriteList(list, 2).map((x) => x.id), ['b', 'e']);
  const t = [c('a', { lastUsedAt: '2026-01-01T00:00:00Z' }), c('b', { lastUsedAt: '2026-03-01T00:00:00Z' }), c('d')];
  assert.deepEqual(recentList(t).map((x) => x.id), ['b', 'a']);
});

test('die lokalen Favoriten werden fuer die Uebernahme gelesen, Ungueltiges faellt weg', () => {
  assert.equal(localOrganisation(mem()), null);
  assert.equal(localOrganisation(mem({ 'pd.m.fav': 'kaputt' })), null);
  const s = mem({ 'pd.m.fav': JSON.stringify(['a', 5, 'b']), 'pd.m.use': JSON.stringify({ a: 3, b: 0, c: 'x' }),
    'pd.m.log': JSON.stringify([{ type: 'end', id: 'a', t: 99 }, { type: 'start', id: 'a', t: 10 }, { type: 'start', id: 'a', t: 20 }, { type: 'start', id: 'b', t: 5 }]) });
  assert.deepEqual(localOrganisation(s), { favorites: ['a', 'b'], uses: { a: 3 }, last: { a: 20, b: 5 } });
});

test('die Uebernahme geschieht einmal, merkt sich das und raeumt die lokale Kopie weg', async () => {
  const s = mem({ 'pd.m.fav': JSON.stringify(['a']), 'pd.m.use': JSON.stringify({ a: 2 }), 'pd.m.log': JSON.stringify([{ type: 'start', id: 'a', t: 7 }]) });
  const calls = [];
  const api = async (path, o) => { calls.push([path, o]); };
  assert.equal(await takeOverLocal(s, api), true);
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], '/api/folders/import-local');
  assert.deepEqual(calls[0][1].body.favorites, ['a']);
  assert.equal(s.getItem('pd.m.fav'), null);
  assert.ok(s.getItem('pd.m.log'));                                  // das Protokoll bleibt auf dem Geraet
  assert.equal(await takeOverLocal(s, api), false);                  // zweiter Aufruf: nichts mehr
  assert.equal(calls.length, 1);
});

test('schlaegt die Uebernahme fehl, bleibt alles lokal und es wird erneut versucht', async () => {
  const s = mem({ 'pd.m.fav': JSON.stringify(['a']) });
  assert.equal(await takeOverLocal(s, async () => { throw new Error('offline'); }), false);
  assert.ok(s.getItem('pd.m.fav'));
  assert.equal(s.getItem('pd.m.orgImported'), null);
  let n = 0;
  assert.equal(await takeOverLocal(s, async () => { n++; }), true);
  assert.equal(n, 1);
});

test('ohne lokale Daten wird nichts gesendet, aber es ist erledigt', async () => {
  const s = mem();
  let n = 0;
  assert.equal(await takeOverLocal(s, async () => { n++; }), false);
  assert.equal(n, 0);
  assert.equal(s.getItem('pd.m.orgImported'), '1');
});
