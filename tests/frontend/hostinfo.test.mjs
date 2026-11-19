// Host-Agent-Oberflaeche (hostinfo.js): Formatierer, Zusammenfassungen, Speicher, Einzeiler, Terminal-Wahl. Reine Logik, kein Browser noetig.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import {
  fmtBytes, fmtUptime, fmtAge, ramPercent, mainDisk, updatesLine, agentModeText, capsText, agentDetail, wantsInfo,
  errorText, stateNote, createHostInfoStore, installCommand, pickTerminal, fetchKind, FRESH_MS,
} from '../../src/JulOS.Frontend/wwwroot/hostinfo.js';
import { prefillState } from '../../src/JulOS.Frontend/wwwroot/terminal-input.js';

setPref('de', null);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const GB = 1e9;
const info = (over = {}) => ({
  hostname: 'srv', os: 'Debian GNU/Linux 12', arch: 'x86_64', kernel: '6.1.0', uptimeSeconds: 3 * 86400 + 4 * 3600 + 5, load: [0.5, 0.4, 0.3], cpuCount: 4,
  memory: { totalBytes: 8 * GB, availableBytes: 6 * GB, swapTotalBytes: 2 * GB, swapFreeBytes: 2 * GB },
  disks: [{ mount: '/data', fs: 'xfs', totalBytes: 100 * GB, usedBytes: 95 * GB, freeBytes: 5 * GB, percent: 95 }, { mount: '/', fs: 'ext4', totalBytes: 50 * GB, usedBytes: 10 * GB, freeBytes: 40 * GB, percent: 20 }],
  addresses: [{ iface: 'eth0', family: 4, address: '192.0.2.10', prefix: 24 }], rebootRequired: false, rebootPackages: [], ...over,
});
const host = (over = {}) => ({ id: 'h1', name: 'H', online: true, services: [], agent: { online: true, kind: 'host', caps: ['sysinfo', 'updates'], version: '0.5.0' }, ...over });

test('Formatierer: Bytes, Laufzeit, Alter, Prozent', () => {
  assert.equal(fmtBytes(1.5 * GB), '1,5 GB');
  assert.equal(fmtBytes(2e6), '2 MB');
  assert.equal(fmtUptime(3 * 86400 + 4 * 3600), '3 d 4 h');
  assert.equal(fmtUptime(5 * 3600 + 12 * 60), '5 h 12 min');
  assert.equal(fmtUptime(7 * 60), '7 min');
  assert.equal(fmtUptime(-1), '');
  assert.equal(fmtAge(30), '1 min');
  assert.equal(fmtAge(3 * 3600), '3 h');
  assert.equal(fmtAge(5 * 86400), '5 d');
  assert.equal(ramPercent({ totalBytes: 8 * GB, availableBytes: 2 * GB }), 75);
  assert.equal(ramPercent({ totalBytes: 0, availableBytes: 0 }), null);
  assert.equal(ramPercent(undefined), null);
});

test('die Platte der Karte: "/", sonst die voelligste', () => {
  assert.equal(mainDisk(info().disks).mount, '/');
  assert.equal(mainDisk([{ mount: '/a', percent: 10 }, { mount: '/b', percent: 80 }]).mount, '/b');
  assert.equal(mainDisk([]), null);
});

test('Updates-Zeile: n verfuegbar (m Sicherheit), keine, unbekannt', () => {
  assert.equal(updatesLine({ updates: { count: 3, security: 1, securityKnown: true } }), 'Updates: 3 verfügbar (1 Sicherheit)');
  assert.equal(updatesLine({ updates: { count: 3, security: 0, securityKnown: true } }), 'Updates: 3 verfügbar');
  assert.equal(updatesLine({ updates: { count: 3, securityKnown: false } }), 'Updates: 3 verfügbar');
  assert.equal(updatesLine({ updates: { count: 0, securityKnown: true, security: 0 } }), 'Updates: keine');
  assert.equal(updatesLine({ state: 'error', error: 'apt_failed' }), '');       // nichts behaupten, was nicht gelesen wurde
  assert.equal(updatesLine(null), '');
});

test('Modus und Faehigkeiten des Agenten', () => {
  assert.equal(agentModeText(host()), 'Host-Agent');
  assert.equal(agentModeText(host({ agent: { online: true, kind: 'desktop' } })), 'Desktop-Agent');
  assert.equal(agentModeText(host({ agent: { online: true } })), 'Desktop-Agent');        // aeltere Server melden keine Art
  assert.equal(agentModeText({ id: 'x' }), '');
  assert.equal(capsText(host({ agent: { online: true, caps: ['sysinfo', 'docker', 'boese'] } })), 'Systemwerte, Docker');
  assert.equal(agentDetail(host()), 'Host-Agent · Liest: Systemwerte, Updates');
  assert.equal(agentDetail(host({ agent: null })), '');
  assert.ok(wantsInfo(host()));
  assert.ok(!wantsInfo(host({ agent: { online: false, caps: ['sysinfo'] } })));
  assert.ok(!wantsInfo(host({ agent: { online: true, caps: [] } })));
  assert.ok(!wantsInfo(host({ agent: { online: true } })));
  assert.ok(!wantsInfo(host({ agent: null })));
});

test('Fehlercodes und Zustaende werden zu Saetzen, Unbekanntes zu einem allgemeinen Satz', () => {
  assert.match(errorText('timeout'), /rechtzeitig/);
  assert.match(errorText('apt_failed'), /apt/);
  assert.equal(errorText('etwas-neues'), 'Das Lesen ist fehlgeschlagen.');
  assert.equal(errorText(undefined), 'Das Lesen ist fehlgeschlagen.');
  assert.equal(stateNote({ state: 'ok' }), '');
  assert.match(stateNote({ state: 'offline' }), /nicht verbunden/);
  assert.match(stateNote({ state: 'no_agent' }), /kein Agent/);
  assert.match(stateNote({ state: 'unsupported' }), /kann der Agent/);
  assert.match(stateNote({ state: 'offline', stale: true, at: '2026-01-02T10:00:00Z' }), /nicht verbunden\. Stand von /);
  assert.match(stateNote(null), /gelesen/);
});

test('fetchKind: der Pfad, ?fresh=1 nur auf Wunsch', async () => {
  const seen = []; const api = async (p) => { seen.push(p); return {}; };
  await fetchKind(api, 'abc', 'status'); await fetchKind(api, 'abc', 'updates', true);
  assert.deepEqual(seen, ['/api/hosts/abc/status', '/api/hosts/abc/updates?fresh=1']);
});

test('Speicher: fragt nur Hosts mit verbundenem Agenten und Faehigkeiten, hoechstens alle 55 s', async () => {
  let clock = 1000; const calls = []; const changed = [];
  const api = async (p) => { calls.push(p); return { state: 'ok', info: info(), updates: { count: 1 } }; };
  const store = createHostInfoStore({ api, now: () => clock, onChange: (id) => changed.push(id) });
  const hosts = [host(), host({ id: 'h2', agent: { online: false, caps: ['sysinfo'] } }), host({ id: 'h3', agent: null }), host({ id: 'h4', agent: { online: true, caps: ['updates', 'docker'] } })];
  const started = store.ensure(hosts);
  assert.deepEqual(started.map((h) => h.id), ['h1', 'h4']);
  await Promise.all([store.get('h1').loading, store.get('h4').loading]);
  assert.deepEqual(calls.sort(), ['/api/hosts/h1/status', '/api/hosts/h1/updates', '/api/hosts/h4/updates']);      // docker nie von hier, nur Faehigkeiten des Hosts
  assert.deepEqual(changed.sort(), ['h1', 'h4']);
  assert.ok(store.get('h1').status && store.get('h1').updates);
  assert.equal(store.get('h2'), null);
  // innerhalb der Frist: nichts
  clock += FRESH_MS - 1; calls.length = 0;
  assert.deepEqual(store.ensure(hosts), []); assert.equal(calls.length, 0);
  // danach wieder
  clock += 2;
  assert.deepEqual(store.ensure(hosts).map((h) => h.id), ['h1', 'h4']);
  await Promise.all([store.get('h1').loading, store.get('h4').loading]);
  // ein Host, den es nicht mehr gibt, wird vergessen
  store.ensure([hosts[0]]); assert.equal(store.get('h4'), null);
  store.clear(); assert.equal(store.get('h1'), null);
});

test('Speicher: Fehler werden verschluckt, der alte Stand bleibt, ein Fehlversuch wird nicht sofort wiederholt', async () => {
  let clock = 0, fail = false, n = 0;
  const api = async () => { n++; if (fail) throw new Error('boom'); return { state: 'ok', info: info() }; };
  const store = createHostInfoStore({ api, now: () => clock });
  const h = host({ agent: { online: true, caps: ['sysinfo'] } });
  store.ensure([h]); await store.get('h1').loading;
  assert.ok(store.get('h1').status);
  clock = FRESH_MS + 1; fail = true;
  store.ensure([h]);
  const e = await store.get('h1').loading;                           // wirft nicht
  assert.ok(e.status && e.status.info, 'alter Stand bleibt');
  const before = n;
  clock += 1000; store.ensure([h]);
  assert.equal(n, before, 'nach einem Fehlversuch nicht gleich wieder');
});

test('Speicher: gleichzeitige Anforderung teilt eine Abfrage', async () => {
  let n = 0; const api = async () => { n++; await new Promise((r) => setTimeout(r, 5)); return { state: 'ok' }; };
  const store = createHostInfoStore({ api });
  const h = host({ agent: { online: true, caps: ['sysinfo'] } });
  await Promise.all([store.load(h), store.load(h), store.load(h)]);
  assert.equal(n, 1);
});

test('Einzeiler fuer den Host: Datei bleibt zum Ansehen, nichts wird in eine Shell geleitet, --allow-http nur fuer fremdes http', () => {
  const cmd = installCommand('https://julos.example.org', 'ABCDE-FGHIJ');
  assert.equal(cmd, 'curl -fsSL https://julos.example.org/agent-files/install-host-agent.sh -o julos-install-host-agent.sh && sudo sh julos-install-host-agent.sh --server https://julos.example.org --code ABCDE-FGHIJ');
  assert.doesNotMatch(cmd, /\|/);
  assert.doesNotMatch(cmd, /allow-http/);
  assert.match(installCommand('http://192.0.2.5:8080', 'ABCDE-FGHIJ'), / --allow-http$/);
  assert.doesNotMatch(installCommand('http://localhost:18191', 'ABCDE-FGHIJ'), /allow-http/);
  assert.doesNotMatch(installCommand('http://127.0.0.1', 'ABCDE-FGHIJ'), /allow-http/);
});

test('Terminal fuer die Installation: nur eine blanke Shell dieses Hosts, die zuletzt benutzte zuerst', () => {
  const c = (id, over) => ({ id, name: id, kind: 'terminal', hostId: 'h1', program: 'shell', ...over });
  const conns = [
    c('claude', { program: 'claude', lastUsedAt: '2026-05-01T00:00:00Z' }),
    c('alt', { lastUsedAt: '2026-01-01T00:00:00Z' }), c('neu', { lastUsedAt: '2026-03-01T00:00:00Z' }),
    c('fremd', { hostId: 'h2', lastUsedAt: '2027-01-01T00:00:00Z' }),
    { id: 'rdp', name: 'rdp', kind: 'desktop', hostId: 'h1' }, c('ohne'),
  ];
  assert.equal(pickTerminal(conns, 'h1').id, 'neu');
  assert.equal(pickTerminal(conns.filter((x) => x.id !== 'neu' && x.id !== 'alt'), 'h1').id, 'ohne');
  assert.equal(pickTerminal([c('claude', { program: 'claude' }), c('codex', { program: 'codex' })], 'h1'), null);   // nie in eine Claude-Kachel
  assert.equal(pickTerminal([], 'h1'), null);
  assert.equal(pickTerminal(null, 'h1'), null);
  assert.equal(pickTerminal(conns, 'h3'), null);
});

test('prefillState: Entwurf ersetzen, Leiste oeffnen, nichts senden', () => {
  const state = { draft: 'alt', open: false };
  assert.strictEqual(prefillState(state, 'sudo sh x'), state);
  assert.deepEqual(state, { draft: 'sudo sh x', open: true });
  assert.deepEqual(prefillState({}, null), { draft: '', open: true });
  assert.deepEqual(prefillState({ open: false }, 'a\nb'), { draft: 'a\nb', open: true });
});

test('die neue Datei steht im Service Worker und die Version ist erhoeht', () => {
  const sw = fs.readFileSync(new URL('../../src/JulOS.Frontend/wwwroot/sw.js', import.meta.url), 'utf8');
  assert.ok(Number(/VERSION = 'pd-v(\d+)'/.exec(sw)[1]) >= 55);
  for (const f of ['/hostinfo.js', '/hostinfo-dialog.js', '/hostui.js', '/hostview.js', '/hostui.css']) assert.ok(sw.includes(`'${f}'`), f);
  assert.ok(Number(/VERSION = 'pd-v(\d+)'/.exec(sw)[1]) >= 60);
});

// ---- Gesundheit der Platten und Netzwerk
import { healthIssues, netRate } from '../../src/JulOS.Frontend/wwwroot/hostinfo.js';

const esc2 = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

test('Platten-Gesundheit: Probleme aus SMART, RAID und ZFS; ohne lesbare Daten gibt es keine Probleme', () => {
  const bad = { health: { smart: { available: true, devices: [{ name: '/dev/sda', health: 'ok' }, { name: '/dev/sdb', health: 'failing' }] }, md: [{ name: 'md1', state: 'degraded', active: 1, total: 2 }], zfs: { healthy: false, pools: [{ name: 'tank', state: 'DEGRADED' }] } } };
  const issues = healthIssues(bad);
  assert.equal(issues.length, 3);
  assert.match(issues.join(' | '), /SMART \/dev\/sdb/);
  assert.match(issues.join(' | '), /RAID md1/);
  assert.match(issues.join(' | '), /tank: DEGRADED|tank DEGRADED|Pool tank/);
  assert.deepEqual(healthIssues({ health: { smart: { available: false, reason: 'no_access' }, md: [] } }), []);   // nichts lesbar: nichts behaupten
  assert.deepEqual(healthIssues({ health: { pending: true } }), []);
  assert.deepEqual(healthIssues({}), []);
});

test('Netzwerkrate: aus zwei Ablesungen, nicht bei Zaehlerrueckgang oder kurzem Abstand', () => {
  const at = (s) => new Date(Date.UTC(2026, 0, 1, 0, 0, s)).toISOString();
  const r = (s, rx, tx) => ({ at: at(s), info: { network: [{ iface: 'eth0', rxBytes: rx, txBytes: tx }] } });
  assert.deepEqual(netRate(r(0, 1000, 500), r(10, 11000, 1500)), { rx: 1000, tx: 100 });
  assert.equal(netRate(r(0, 1000, 500), r(2, 2000, 600)), null);             // zu kurz
  assert.equal(netRate(r(0, 5000, 500), r(10, 1000, 600)), null);            // Zaehler zurueckgesetzt (Neustart)
  assert.equal(netRate(null, r(10, 1, 1)), null);
  assert.equal(netRate(r(0, 1, 1), { at: at(10), info: {} }), null);
});
