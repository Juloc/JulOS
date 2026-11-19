// Module Caddy und DDNS (modules.js): was die Oberflaeche aus den Antworten des Servers macht.
import test from 'node:test';
import assert from 'node:assert/strict';
import { loadModules, routeParts, reachMap, reachText, reachClass, ddnsStateLabel, ddnsHeadline, ago, sourceLabel } from '../../src/JulOS.Frontend/wwwroot/modules.js';

test('modules: only what the server switched on is shown, every error means off', async () => {
  assert.deepEqual(await loadModules(async () => ({ caddy: 'read', ddns: 'off' })), { caddy: true, ddns: false });
  assert.deepEqual(await loadModules(async () => ({ caddy: 'off', ddns: 'off' })), { caddy: false, ddns: false });
  assert.deepEqual(await loadModules(async () => { throw Object.assign(new Error('Nicht angemeldet.'), { status: 401 }); }), { caddy: false, ddns: false });
  assert.deepEqual(await loadModules(async () => null), { caddy: false, ddns: false });
});

test('routes: domain, path and target as text', () => {
  assert.deepEqual(routeParts({ domain: 'app.example.test', path: '/api/*', kind: 'proxy', targets: ['api:8080', 'api2:8080'] }),
    { domain: 'app.example.test', path: '/api/*', target: 'api:8080, api2:8080', kind: 'Weiterleitung' });
  assert.equal(routeParts({ domain: '*', kind: 'respond', targets: [], detail: '404' }).domain, '(alle Hosts)');
  assert.equal(routeParts({ domain: 'a.test', kind: 'respond', targets: [], detail: '503' }).target, 'feste Antwort 503');
  assert.equal(routeParts({ domain: 'a.test', kind: 'files', targets: ['/srv/files'] }).target, '/srv/files');
  assert.equal(routeParts({ domain: 'a.test', kind: 'redirect', targets: ['https://b.test/'] }).target, 'https://b.test/');
  assert.equal(routeParts({ domain: 'a.test', kind: 'other', targets: [] }).target, 'nicht angezeigt');
  assert.equal(routeParts({ domain: 'a.test', kind: 'proxy', targets: [] }).target, 'kein Ziel');
});

test('reachability: texts and classes per state', () => {
  const m = reachMap({ upstreams: [{ target: 'web:3000', state: 'up', ms: 4 }, { target: 'db:5432', state: 'down' }, { target: 'unix//x', state: 'skipped' }] });
  assert.equal(reachText(m.get('web:3000')), 'erreichbar (4 ms)');
  assert.equal(reachText(m.get('db:5432')), 'nicht erreichbar');
  assert.equal(reachText(m.get('unix//x')), 'nicht prüfbar');
  assert.deepEqual(['web:3000', 'db:5432', 'unix//x', 'gibt-es-nicht'].map((t) => reachClass(m.get(t))), ['ok', 'bad', 'idle', '']);
  assert.equal(reachMap(null).size, 0);
});

test('ddns: labels and the headline', () => {
  assert.equal(ddnsStateLabel('synced'), 'Synchron');
  assert.equal(ddnsStateLabel('notSynced'), 'Nicht synchron');
  assert.equal(ddnsStateLabel('was-neues'), 'Unbekannt');
  assert.match(ddnsHeadline({ state: 'synced', hosts: [] }), /Alle Namen/);
  assert.match(ddnsHeadline({ state: 'notSynced', hosts: [{ state: 'synced' }, { state: 'notSynced' }, { state: 'unresolved' }] }), /^2 Namen/);
  assert.match(ddnsHeadline({ state: 'notSynced', hosts: [{ state: 'notSynced' }] }), /^Ein Name/);
  assert.equal(ddnsHeadline({ state: 'unknown', message: 'Keine Quelle.' }), 'Keine Quelle.');
});

test('time and source labels', () => {
  const now = Date.parse('2026-11-19T12:00:00Z');
  assert.equal(ago('2026-11-19T11:59:58Z', now), 'gerade eben');
  assert.equal(ago('2026-11-19T11:59:30Z', now), 'vor 30 s');
  assert.equal(ago('2026-11-19T11:55:00Z', now), 'vor 5 min');
  assert.equal(ago('2026-11-19T09:00:00Z', now), 'vor 3 h');
  assert.equal(sourceLabel('caddyfile'), 'Caddyfile');
});
