// Hilfen fuer Hosts (hosts.js): Zustand, Gruppierung, Texte. Reine Logik, kein Browser noetig.
import test from 'node:test';
import assert from 'node:assert/strict';
import { serviceState, connectionState, dotClass, stateText, groupConnections, hostDetail, agentSummary, hostById, isProgramTile, hostSections, toggleClosed, readClosed, writeClosed, CLOSED_KEY, hostStatusText } from '../../src/JulOS.Frontend/wwwroot/hosts.js';

const host = (id, extra = {}) => ({ id, name: 'H' + id, online: false, services: [], ...extra });
const conn = (id, hostId) => ({ id, hostId, name: 'C' + id });

test('a service is up, down or unknown (not probed counts as unknown, not as down)', () => {
  const h = host('a', { services: [{ connection: 'x', kind: 'terminal', online: true }, { connection: 'y', kind: 'desktop', online: false }, { connection: 'z', kind: 'desktop' }] });
  assert.equal(serviceState(h, 'x'), 'up');
  assert.equal(serviceState(h, 'y'), 'down');
  assert.equal(serviceState(h, 'z'), 'unknown');
  assert.equal(serviceState(h, 'fehlt'), 'unknown');
  assert.equal(serviceState(null, 'x'), 'unknown');
});

test('a connection takes its state from its own host', () => {
  const hosts = [host('a', { services: [{ connection: 'x', online: true }] }), host('b', { services: [{ connection: 'y', online: false }] })];
  assert.equal(connectionState(hosts, conn('x', 'a')), 'up');
  assert.equal(connectionState(hosts, conn('y', 'b')), 'down');
  assert.equal(connectionState(hosts, conn('x', 'b')), 'unknown');     // die Verbindung gehoert nicht zu diesem Host
  assert.equal(connectionState(hosts, conn('q', 'weg')), 'unknown');
});

test('the status dot is green only for online and grey otherwise', () => {
  assert.equal(dotClass('up'), 'ok'); assert.equal(dotClass(true), 'ok');
  for (const s of ['down', 'unknown', false, undefined, null]) assert.equal(dotClass(s), 'off');
  assert.equal(stateText('up'), 'online'); assert.equal(stateText(false), 'offline'); assert.equal(stateText('unknown'), 'unbekannt');
});

test('connections are grouped by host in the order of the hosts, empty hosts stay, unknown hosts go last', () => {
  const hosts = [host('a'), host('b'), host('c')];
  const groups = groupConnections(hosts, [conn(1, 'b'), conn(2, 'a'), conn(3, 'b'), conn(4, 'weg')]);
  assert.deepEqual(groups.map((g) => [g.host && g.host.id, g.connections.map((c) => c.id)]), [['a', [2]], ['b', [1, 3]], ['c', []], [null, [4]]]);
  assert.equal(groupConnections(hosts, []).length, 3);
  assert.equal(hostById(hosts, 'b').id, 'b'); assert.equal(hostById(hosts, 'x'), null);
});

test('host details and the agent sentence', () => {
  assert.equal(hostDetail({}), '');
  assert.equal(hostDetail({ hostname: 'werkbank', os: 'Debian GNU/Linux 13' }), 'werkbank · Debian GNU/Linux 13');
  assert.equal(hostDetail({ os: 'Debian' }), 'Debian');
  assert.equal(agentSummary(host('a')), 'Kein Agent');
  assert.equal(agentSummary(host('a', { agent: { online: true, version: '0.3.0', platform: 'linux-x11', outdated: false } })), 'Agent verbunden – Agent 0.3.0, linux-x11');
  assert.match(agentSummary(host('a', { agent: { online: false, lastSeen: '2026-01-02T10:00:00Z', outdated: false } })), /^Agent getrennt \(zuletzt /);
  assert.equal(agentSummary(host('a', { agent: { online: false, outdated: false } })), 'Agent getrennt');
  assert.match(agentSummary(host('a', { agent: { online: true, version: '0.2.0', outdated: true } })), /zu alt/);
  assert.equal(agentSummary(host('a', { agent: { online: true, version: '0.3.0', platform: 'linux-x11', outdated: false, updateAvailable: true, latest: '0.4.0' } })), 'Agent verbunden – Agent 0.3.0, linux-x11 – Update verfügbar (0.4.0)');
  assert.doesNotMatch(agentSummary(host('a', { agent: { online: true, version: '0.4.0', outdated: false, updateAvailable: false, latest: '0.4.0' } })), /Update/);
  assert.doesNotMatch(agentSummary(host('a', { agent: { online: true, version: '0.2.0', outdated: true, updateAvailable: true, latest: '0.4.0' } })), /Update verfügbar/);   // zu alt: erst neu einrichten
});

test('ein Host-Agent: der Satz zum Agenten bleibt gleich, Modus und Faehigkeiten stehen in hostinfo.js', () => {
  const h = host('a', { agent: { online: true, version: '0.5.0', platform: 'linux-host', outdated: false, kind: 'host', caps: ['sysinfo', 'updates'], control: 'read' } });
  assert.equal(agentSummary(h), 'Agent verbunden – Agent 0.5.0, linux-host');
});

test('Verbindungen nach Host: normale Verbindungen und Programm-Kacheln getrennt, leere Hosts nur ohne Filter', () => {
  const hosts = [host('a'), host('b')];
  const t = (id, hostId, extra = {}) => ({ id, hostId, kind: 'terminal', name: 'C' + id, ...extra });
  const conns = [t(1, 'a'), t(2, 'a', { program: 'claude' }), t(3, 'a', { program: 'codex' }), t(4, 'a', { keepAlive: true }), { id: 5, hostId: 'a', kind: 'desktop' }, t(6, 'weg', { program: 'custom' })];
  assert.deepEqual(conns.map((c) => isProgramTile(c)), [false, true, true, false, false, true]);   // tmux (keepAlive) und Desktops sind normale Verbindungen
  const s = hostSections(hosts, conns);
  assert.deepEqual(s.map((x) => [x.key, x.plain.map((c) => c.id), x.programs.map((c) => c.id)]), [['a', [1, 4, 5], [2, 3]], ['b', [], []], ['', [], [6]]]);
  assert.equal(s[2].host, null);
  assert.deepEqual(hostSections(hosts, conns, { hideEmpty: true }).map((x) => x.key), ['a', '']);
});

test('Eingeklappte Hosts: umschalten, lesen und schreiben ohne Fehler bei kaputten Daten', () => {
  assert.deepEqual(toggleClosed(['a'], 'b'), ['a', 'b']);
  assert.deepEqual(toggleClosed(['a', 'b'], 'a'), ['b']);
  const mem = new Map(), store = { getItem: (k) => (mem.has(k) ? mem.get(k) : null), setItem: (k, v) => mem.set(k, v) };
  assert.deepEqual(readClosed(store), []);
  writeClosed(store, ['x', 'y']);
  assert.deepEqual(readClosed(store), ['x', 'y']);
  mem.set(CLOSED_KEY, '{kaputt'); assert.deepEqual(readClosed(store), []);
  mem.set(CLOSED_KEY, JSON.stringify(['a', 3, null, 'b'])); assert.deepEqual(readClosed(store), ['a', 'b']);
  assert.deepEqual(readClosed({ getItem() { throw new Error('gesperrt'); } }), []);
  assert.equal(hostStatusText({ online: true, agent: { online: true } }), 'online · Agent verbunden');
  assert.equal(hostStatusText({ online: false, agent: null }), 'offline · Kein Agent');
});
