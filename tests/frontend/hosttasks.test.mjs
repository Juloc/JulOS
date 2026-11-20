// Hintergrund-Aufgaben auf Hosts (hosttasks.js): der Speicher fragt den Server, ohne etwas zu sperren; Texte, Leiste, Panel, Neuladen. Kein Browser noetig.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { createTaskStore, chipText, barText, barPercent, barTone, finishNotice, panelHtml, POLL_MS } from '../../src/JulOS.Frontend/wwwroot/hosttasks.js';

setPref('de', null);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const host = { id: 'h1', name: 'pve', agent: { online: true, kind: 'host', version: '0.6.0', control: 'full', allow: ['updates'], caps: ['host', 'sysinfo', 'updates'], hostSupport: 'yes' } };
const conf = { action: 'updates.install', target: 'all', targetName: 'all' };

/** Zeitgeber, die erst laufen, wenn der Test es sagt */
function clock() {
  const queue = [];
  return { setTimer: (fn) => { queue.push(fn); return queue.length; }, clearTimer: () => {}, pending: () => queue.length, async tick() { const fn = queue.shift(); if (fn) await fn(); } };
}
const memory = () => { const m = new Map(); return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, v) }; };

test('Eine gestartete Aktion wird zur Aufgabe, der Speicher fragt ihren Stand, und beim Ende kommt genau eine Meldung und der Rueckruf', async () => {
  const c = clock(), calls = [], finished = [], done = [];
  let n = 0;
  const api = async (path) => {
    calls.push(path);
    n++;
    return n < 3 ? { job: 'J1', action: 'updates.install', state: 'running', progress: 'Setting up bash', done: n * 10, total: 63, tail: ['Setting up bash'] }
      : { job: 'J1', action: 'updates.install', state: 'ok', finished: new Date().toISOString(), result: { ok: true, rebootRequired: true } };
  };
  let changes = 0;
  const store = createTaskStore({ api, onChange: () => changes++, onFinish: (x) => finished.push(x.job), setTimer: c.setTimer, clearTimer: c.clearTimer, storage: memory() });
  const task = store.add(host, { job: 'J1', state: 'running' }, conf, { onDone: (x) => done.push(x.state) });
  assert.equal(task.host.name, 'pve');
  assert.deepEqual(calls, []);                                                  // erst der Zeitgeber fragt
  assert.equal(c.pending(), 1);
  assert.match(chipText(task), /Updates installieren auf pve/);
  await c.tick();
  assert.deepEqual(calls, ['/api/hosts/h1/actions/J1']);
  assert.equal(store.get('J1').done, 10);
  assert.match(chipText(store.get('J1')), /10 von 63/);
  assert.equal(barPercent(store.list()), 16);
  await c.tick(); await c.tick();
  assert.equal(store.get('J1').state, 'ok');
  assert.deepEqual(finished, ['J1']);
  assert.deepEqual(done, ['ok']);
  assert.equal(c.pending(), 0);                                                 // nichts mehr zu fragen: der Zeitgeber ruht
  assert.ok(changes >= 3);
  assert.match(chipText(store.get('J1')), /fertig/);
});

test('Mehrere Aufgaben auf verschiedenen Hosts laufen nebeneinander und werden zusammen gefragt', async () => {
  const c = clock(), calls = [];
  const api = async (path) => { calls.push(path); return { job: path.split('/').pop(), action: 'updates.refresh', state: 'running' }; };
  const store = createTaskStore({ api, setTimer: c.setTimer, clearTimer: c.clearTimer, storage: memory() });
  store.add(host, { job: 'A' }, { action: 'updates.refresh' });
  store.add({ id: 'h2', name: 'nas' }, { job: 'B' }, { action: 'docker.pull', target: 'abc' });
  await c.tick();
  assert.deepEqual(calls.sort(), ['/api/hosts/h1/actions/A', '/api/hosts/h2/actions/B']);
  assert.equal(barText(store.list()), '2 Aufgaben laufen');
  assert.equal(barPercent(store.list()), null);                                 // nichts Zaehlbares: Balken ohne Prozent
  assert.equal(barTone(store.list()), 'info');
});

test('Nach dem Neuladen findet load() laufende und eben beendete Aufgaben wieder, ohne alte Meldungen und ohne Entfernte', async () => {
  const c = clock(), finished = [];
  const hourAgo = new Date(Date.now() - 2 * 3600 * 1000).toISOString(), justNow = new Date(Date.now() - 60 * 1000).toISOString();
  const api = async (path) => {
    if (path === '/api/host-jobs') {
      return { jobs: [
        { host: 'h1', hostName: 'pve', job: { job: 'RUN', action: 'updates.install', target: 'all', state: 'running', progress: 'x', done: 5, total: 10, started: justNow, tail: ['a'] } },
        { host: 'h1', hostName: 'pve', job: { job: 'DONE', action: 'updates.refresh', state: 'ok', started: justNow, finished: justNow, result: { ok: true } } },
        { host: 'h1', hostName: 'pve', job: { job: 'OLD', action: 'updates.refresh', state: 'ok', started: hourAgo, finished: hourAgo } },
        { host: 'h2', hostName: 'nas', job: { job: 'GONE', action: 'updates.refresh', state: 'failed', error: 'apt_failed', started: justNow, finished: justNow } },
      ] };
    }
    return { job: 'RUN', action: 'updates.install', state: 'ok', finished: new Date().toISOString(), result: { ok: true } };
  };
  const storage = memory();
  storage.setItem('pd.hosttasks.dismissed', JSON.stringify(['GONE']));
  const store = createTaskStore({ api, onFinish: (x) => finished.push(x.job), setTimer: c.setTimer, clearTimer: c.clearTimer, storage });
  const added = await store.load();
  assert.deepEqual(added.map((x) => x.job).sort(), ['DONE', 'RUN']);              // OLD zu alt, GONE entfernt
  assert.equal(store.get('RUN').host.name, 'pve');
  assert.equal(store.get('RUN').done, 5);
  assert.equal(store.get('DONE').seen, true);
  assert.deepEqual(finished, []);                                                // Beendetes wird nicht noch einmal gemeldet
  assert.equal(c.pending(), 1);                                                  // die laufende wird weiter gefragt
  await c.tick();
  assert.deepEqual(finished, ['RUN']);                                           // und meldet beim Ende
  assert.equal(await store.load().then((a) => a.length), 0);                     // nichts doppelt
});

test('Entfernen: nur Beendetes, und es kommt nach einem Neuladen nicht wieder', async () => {
  const c = clock();
  const storage = memory();
  const list = { jobs: [{ host: 'h1', hostName: 'pve', job: { job: 'D1', action: 'updates.refresh', state: 'ok', started: new Date().toISOString(), finished: new Date().toISOString() } }] };
  const api = async () => list;
  const store = createTaskStore({ api, setTimer: c.setTimer, clearTimer: c.clearTimer, storage });
  await store.load();
  store.add(host, { job: 'R1' }, { action: 'updates.refresh' });
  assert.equal(store.dismiss('R1'), false);                                      // eine laufende Aufgabe bleibt
  assert.equal(store.dismiss('D1'), true);
  const again = createTaskStore({ api, setTimer: c.setTimer, clearTimer: c.clearTimer, storage });
  assert.equal((await again.load()).length, 0);
});

test('Ein Auftrag, den der Server nicht mehr kennt, bleibt nicht fuer immer "laeuft"', async () => {
  const c = clock(), finished = [];
  const api = async () => { const e = new Error('weg'); e.status = 404; throw e; };
  const store = createTaskStore({ api, onFinish: (x) => finished.push(x.error), setTimer: c.setTimer, clearTimer: c.clearTimer, storage: memory() });
  store.add(host, { job: 'X' }, { action: 'updates.install', target: 'all' });
  await c.tick();
  assert.equal(store.get('X').state, 'failed');
  assert.deepEqual(finished, ['job_not_found']);
  assert.equal(barTone(store.list()), 'bad');
});

test('Texte: fehlgeschlagen und Hinweis beim Ende nennen Host und Grund', () => {
  const failed = { action: 'updates.install', host: { id: 'h1', name: 'pve' }, state: 'failed', error: 'dpkg_failed' };
  assert.equal(chipText(failed), 'Updates installieren auf pve: fehlgeschlagen');
  const n = finishNotice(failed);
  assert.equal(n.tone, 'bad');
  assert.match(n.detail, /dpkg/);
  assert.equal(finishNotice({ ...failed, state: 'ok', error: null }).tone, 'ok');
  assert.equal(barText([failed]), chipText(failed));
  assert.match(chipText({ ...failed, state: 'lost' }), /Verbindung weg/);
});

test('Panel: laufend mit Live-Ausgabe als Text und Hinweis, dass Schliessen nichts abbricht; Fehler mit Zusammenfassung, Reparieren nur mit Wort und Stufe', () => {
  const run = { job: 'J', action: 'updates.install', host: { id: 'h1', name: 'pve' }, target: 'all', state: 'running', progress: 'Setting up bash', done: 3, total: 10, tail: ['Get:1 <b>x</b>', 'Setting up bash'] };
  const h = panelHtml(run, esc, host);
  assert.match(h, /<progress max="100" value="30">/);
  assert.match(h, /data-live/);
  assert.match(h, /Get:1 &lt;b&gt;x&lt;\/b&gt;/);                                       // Ausgabe nie als HTML
  assert.match(h, /bricht die Aktion nicht ab/);
  assert.match(h, /Host: pve · alle Updates/);
  const failed = { ...run, state: 'failed', error: 'dpkg_failed', tail: [], result: { needsRepair: true, summary: { failedPackages: ['pve-manager', 'proxmox-mail-forward'], errors: ['E: Sub-process /usr/bin/dpkg returned an error code (1)'], keptBack: [], locked: false, configQuestion: false }, log: ['Errors were encountered while processing:', ' pve-manager', 'E: Sub-process /usr/bin/dpkg returned an error code (1)'] } };
  const f = panelHtml(failed, esc, host);
  assert.match(f, /Nicht fertig eingerichtet/);
  assert.match(f, /pve-manager, proxmox-mail-forward/);
  assert.match(f, /E: Sub-process \/usr\/bin\/dpkg returned an error code \(1\)/);
  assert.match(f, /data-task-act="repair"/);
  assert.match(f, /<details class="hu-fold hu-log">/);                                  // das volle Protokoll aufklappbar, nicht offen
  assert.doesNotMatch(f, /<details[^>]* open/);
  assert.match(f, /Protokoll \(3 Zeilen\)/);
  const noWord = { ...host, agent: { ...host.agent, allow: ['logs'] } };
  assert.doesNotMatch(panelHtml(failed, esc, noWord), /data-task-act/);                 // ohne das Wort updates kein Reparieren
  assert.doesNotMatch(panelHtml(failed, esc, null), /data-task-act/);
});

test('Fertig: Neustart-Hinweis, zurueckgehaltene Pakete und das Protokoll; kein Fehler', () => {
  const ok = { job: 'J', action: 'updates.install', host: { id: 'h1', name: 'pve' }, target: 'all', state: 'ok', tail: [], result: { ok: true, rebootRequired: true, summary: { keptBack: ['linux-image-amd64'], failedPackages: [], errors: [] }, log: ['Setting up bash'] } };
  const h = panelHtml(ok, esc, host);
  assert.match(h, /Erledigt/);
  assert.match(h, /Neustart/);
  assert.match(h, /linux-image-amd64/);
  assert.doesNotMatch(h, /data-live/);
});

test('Der Zeitgeber fragt im festen Takt', () => { assert.equal(POLL_MS, 1500); });
