// Module Caddy und DDNS (modules.js): was die Oberflaeche aus den Antworten des Servers macht.
import test from 'node:test';
import assert from 'node:assert/strict';
import { certClass, certStateText, certDetail, certSummary, caddyHealthText, caddyHealthClass, logClass, TEMPLATES, templateById, fromTemplate, originOf, stateClass, stateText, errorText, msText, fmtMetric, fmtBytes, svcIcon, ICON_KEYS, loadModules, anyModule, routeParts, reachMap, reachText, reachClass, ddnsStateLabel, ddnsHeadline, ago, sourceLabel } from '../../src/JulOS.Frontend/wwwroot/modules.js';

test('modules: only what the server switched on is shown, every error means off', async () => {
  const off = { caddy: false, ddns: false, services: false, modes: { caddy: 'off', ddns: 'off', services: 'off' } };
  assert.deepEqual(await loadModules(async () => ({ caddy: 'read', ddns: 'off', services: 'control' })),
    { caddy: true, ddns: false, services: true, modes: { caddy: 'read', ddns: 'off', services: 'control' } });
  assert.deepEqual(await loadModules(async () => ({ caddy: 'off', ddns: 'off' })), off);          // ein Server ohne das Modul Dienste
  assert.deepEqual(await loadModules(async () => ({ caddy: 'write', ddns: 5, services: 'off' })), off);   // unbekannte Werte gelten als aus
  assert.deepEqual(await loadModules(async () => { throw Object.assign(new Error('Nicht angemeldet.'), { status: 401 }); }), off);
  assert.deepEqual(await loadModules(async () => null), off);
  assert.equal(anyModule(off), false);
  assert.equal(anyModule({ ...off, services: true }), true);
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

test('services: templates build the addresses from the origin of the service', () => {
  assert.deepEqual(TEMPLATES.map((x) => x.id), ['jularr', 'jularr-library', 'caddyui', 'http']);
  const j = fromTemplate(templateById('jularr'), 'http://192.0.2.10:8080/irgendwas?x=1');
  assert.equal(j.openUrl, 'http://192.0.2.10:8080/');
  assert.equal(j.healthUrl, 'http://192.0.2.10:8080/health/ready');
  assert.equal(j.metricsUrl, 'http://192.0.2.10:8080/api/v1/admin/activity/summary');
  assert.equal(j.headerName, 'Authorization');
  assert.deepEqual(j.metrics.map((m) => [m.path, m.format]), [['activeCount', 'plain'], ['bytesPerSecond', 'rate'], ['importCount', 'plain'], ['errorCount', 'plain']]);
  assert.equal(j.metrics[0].label, 'Aktive Downloads');
  const g = fromTemplate(templateById('http'), 'https://nas.lan');
  assert.equal(g.healthUrl, 'https://nas.lan/');
  assert.equal(g.metricsUrl, '');
  assert.equal(fromTemplate(templateById('http'), 'kein url').healthUrl, '');
  assert.equal(originOf('ftp://x'), '');
  assert.equal(templateById('gibt-es-nicht'), null);
});

test('services: state, error and response time texts', () => {
  assert.deepEqual([stateClass({ state: 'up' }), stateClass({ state: 'down' }), stateClass({ state: 'unknown' }), stateClass(undefined)], ['ok', 'bad', 'idle', 'idle']);
  assert.equal(stateText({ state: 'up' }), 'läuft');
  assert.equal(stateText(undefined), 'unbekannt');
  assert.equal(errorText({ error: 'status', httpStatus: 503 }), 'Fehlerstatus (503)');
  assert.equal(errorText({ error: 'timeout' }), 'Zeitüberschreitung');
  assert.equal(errorText({ error: 'etwas-neues' }), '');
  assert.equal(errorText({}), '');
  assert.equal(msText({ ms: 12 }), '12 ms');
  assert.equal(msText({ ms: 0 }), '0 ms');
  assert.equal(msText({}), '');
});

test('services: figures are shown by their format and a missing value is a dash', () => {
  assert.equal(fmtMetric({ number: 3, format: 'plain', unit: '' }), '3');
  assert.equal(fmtMetric({ number: 3, format: 'plain', unit: 'Stk' }), '3 Stk');
  assert.equal(fmtMetric({ number: 1048576, format: 'rate' }), '1,0 MB/s');
  assert.equal(fmtMetric({ number: 1536, format: 'bytes' }), '1,5 KB');
  assert.equal(fmtMetric({ number: 42.5, format: 'percent' }), '42,5 %');
  assert.equal(fmtMetric({ text: 'ok', unit: '' }), 'ok');
  assert.equal(fmtMetric({ format: 'plain' }), '–');
  assert.equal(fmtMetric(undefined), '–');
  assert.equal(fmtBytes(512), '512 B');
  assert.equal(fmtBytes(5 * 1024 ** 3), '5,0 GB');
});

test('services: every icon the server allows has a drawing, unknown names fall back to the globe', () => {
  assert.equal(ICON_KEYS.length, 12);
  for (const k of ICON_KEYS) assert.match(svcIcon(k), /<svg class="wd-i"[^>]*>.+<\/svg>/);
  assert.equal(svcIcon('gibt-es-nicht'), svcIcon('globe'));
});

test('services: the icons of the interface are the ones the server accepts', async () => {
  const fs = await import('node:fs');
  const cs = fs.readFileSync(new URL('../../src/JulOS.Backend/Modules/ServiceRules.cs', import.meta.url), 'utf8');
  const server = /Icons = \[(.*?)\]/.exec(cs)[1].split(',').map((x) => x.trim().replace(/"/g, ''));
  assert.deepEqual([...ICON_KEYS].sort(), server.sort());
});

test('caddy: certificate states, details and the summary that feeds the warning', () => {
  const day = (d) => String(d).slice(0, 10);
  const ok = { state: 'ok', issuer: 'Let\'s Encrypt', notAfter: '2027-01-30T00:00:00Z', daysLeft: 72, trusted: true };
  assert.equal(certStateText(ok), 'Gültig');
  assert.equal(certClass(ok), 'ok');
  assert.equal(certDetail(ok, day), 'Aussteller Let\'s Encrypt · läuft in 72 Tagen ab (2027-01-30)');
  assert.equal(certDetail({ ...ok, daysLeft: 1, state: 'warn' }, day).includes('in 1 Tag ab'), true);
  assert.equal(certClass({ state: 'warn' }), 'warn');
  assert.equal(certDetail({ state: 'expired', notAfter: '2026-11-01T00:00:00Z', daysLeft: -18, issuer: 'CA', trusted: true }, day), 'Aussteller CA · abgelaufen am 2026-11-01');
  assert.equal(certDetail({ state: 'error', error: 'timeout' }, day), 'Zeitüberschreitung');
  assert.match(certDetail({ ...ok, trusted: false }, day), /Kette nicht vertrauenswürdig/);
  assert.deepEqual(certSummary([ok, { state: 'warn' }, { state: 'expired' }, { state: 'error' }, { state: 'skipped' }]), { warn: 1, bad: 2, total: 4 });
  assert.deepEqual(certSummary(undefined), { warn: 0, bad: 0, total: 0 });
});

test('caddy: health and log classes', () => {
  assert.equal(caddyHealthText({ state: 'up', ms: 12 }), 'Caddy läuft (12 ms)');
  assert.equal(caddyHealthText({ state: 'down' }), 'Caddy antwortet nicht');
  assert.equal(caddyHealthText(undefined), 'Zustand unbekannt');
  assert.deepEqual([{ state: 'up' }, { state: 'down' }, { state: 'unknown' }, undefined].map(caddyHealthClass), ['ok', 'bad', 'idle', 'idle']);
  assert.deepEqual([200, 301, 404, 503].map(logClass), ['ok', 'ok', 'warn', 'bad']);
});
