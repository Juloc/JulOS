// Netzwerkwerkzeuge im Browser (nettools.js): Port-Eingabe pruefen und die Ausgabe der drei Werkzeuge als Text.
import test from 'node:test';
import assert from 'node:assert/strict';
import { parsePorts, defaultPorts, describePing, describeDns, describePorts } from '../../src/JulOS.Frontend/wwwroot/nettools.js';

test('ports: separators, duplicates, limits', () => {
  assert.deepEqual(parsePorts('22, 3389 443;80').ports, [22, 3389, 443, 80]);
  assert.deepEqual(parsePorts('22,22,22').ports, [22]);
  for (const bad of ['', '  ', '0', '65536', 'abc', '22 x', '-1', '1.5', '123456']) assert.ok(parsePorts(bad).error, `"${bad}" is refused`);
  assert.equal(parsePorts('1 2 3 4 5 6 7 8 9 10').ports.length, 10);
  assert.ok(parsePorts('1 2 3 4 5 6 7 8 9 10 11').error);
});

test('the port field starts with the ports of the saved connections to that host, else SSH and RDP', () => {
  const list = [{ host: 'Nas', port: 22 }, { host: 'nas', port: 445 }, { host: 'nas', port: 22 }, { host: 'pc', port: 3389 }];
  assert.equal(defaultPorts(list, 'NAS'), '22, 445');
  assert.equal(defaultPorts(list, 'pc'), '3389');
  assert.equal(defaultPorts(list, 'unbekannt'), '22, 3389');
  assert.equal(defaultPorts(undefined, 'x'), '22, 3389');
});

test('ping text: replies, summary and the note when ICMP is not possible', () => {
  const ok = describePing({ host: 'nas', address: '10.0.0.5', sent: 3, received: 2, minMs: 1, avgMs: 2, maxMs: 3,
    replies: [{ seq: 1, status: 'ok', ms: 1 }, { seq: 2, status: 'timeout' }, { seq: 3, status: 'ok', ms: 3 }] });
  assert.equal(ok, 'PING nas (10.0.0.5)\n1: 1 ms\n2: keine Antwort (Zeitüberschreitung)\n3: 3 ms\n3 gesendet, 2 empfangen, kleinste/mittlere/größte Zeit 1/2/3 ms');
  const none = describePing({ host: '10.0.0.5', address: '10.0.0.5', sent: 1, received: 0, replies: [{ seq: 1, status: 'unavailable' }], note: 'Ping (ICMP) ist auf diesem Server nicht möglich.' });
  assert.equal(none, 'PING 10.0.0.5\n1: nicht möglich\n1 gesendet, 0 empfangen\nPing (ICMP) ist auf diesem Server nicht möglich.');
  assert.equal(describePing({ host: 'x', replies: [], sent: 0, received: 0, note: 'Der Name lässt sich nicht auflösen.' }), 'PING x\nDer Name lässt sich nicht auflösen.');
});

test('dns text', () => {
  assert.equal(describeDns({ host: 'nas', addresses: ['10.0.0.5', 'fe80::1'], ms: 4 }), 'nas → 10.0.0.5, fe80::1  (4 ms)');
  assert.equal(describeDns({ host: 'nas', addresses: [], ms: 9, note: 'Der Name lässt sich nicht auflösen.' }), 'nas: keine Adresse\nDer Name lässt sich nicht auflösen.');
});

test('port text', () => {
  assert.equal(describePorts({ host: 'nas', results: [{ port: 22, state: 'open', ms: 3 }, { port: 80, state: 'closed', ms: 1 }, { port: 3389, state: 'filtered' }] }),
    'PORTS nas\n22  offen  (3 ms)\n80  geschlossen (abgelehnt)\n3389  keine Antwort (gesperrt oder Ziel aus)');
});
