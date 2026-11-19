import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseQuickTarget, looksLikeAddress, quickConnection } from '../../src/JulOS.Frontend/wwwroot/quick.js';

test('Adresse mit Port, Benutzer und Schema wird gelesen', () => {
  assert.deepEqual(parseQuickTarget('nas.example'), { kind: null, host: 'nas.example', port: null, username: '' });
  assert.deepEqual(parseQuickTarget('  10.0.0.5:3389 '), { kind: 'desktop', host: '10.0.0.5', port: 3389, username: '' });
  assert.deepEqual(parseQuickTarget('root@10.0.0.5:22'), { kind: 'terminal', host: '10.0.0.5', port: 22, username: 'root' });
  assert.deepEqual(parseQuickTarget('ssh://anna@box.lan'), { kind: 'terminal', host: 'box.lan', port: null, username: 'anna' });
  assert.deepEqual(parseQuickTarget('rdp://pc.lan:3390/'), { kind: 'desktop', host: 'pc.lan', port: 3390, username: '' });
  assert.deepEqual(parseQuickTarget('mail@anna@box.lan'), { kind: null, host: 'box.lan', port: null, username: 'mail@anna' });
});

test('IPv6-Adressen gelten als Ganzes ohne Port', () => {
  assert.deepEqual(parseQuickTarget('fd00::1'), { kind: null, host: 'fd00::1', port: null, username: '' });
  assert.equal(parseQuickTarget('fd00::zz'), null);
});

test('Ungueltiges wird abgelehnt', () => {
  for (const bad of ['', '   ', 'a b', 'host:0', 'host:70000', 'host:abc', 'http://x.example', 'pfad/zu/host', 'ho$t', ':22', 'ssh://', `${'u'.repeat(65)}@h`])
    assert.equal(parseQuickTarget(bad), null, JSON.stringify(bad));
});

test('Nach einer Adresse sieht aus, was Punkt, Port, Benutzer oder Schema hat', () => {
  for (const yes of ['nas.example', '10.0.0.5', 'host:22', 'u@h', 'ssh://h', 'fd00::1']) assert.equal(looksLikeAddress(yes), true, yes);
  for (const no of ['nas', 'Büro', 'meinserver', 'a b', '']) assert.equal(looksLikeAddress(no), false, no);
});

test('Die Sitzungsbeschreibung hat eine Kennung je Ziel und kein Passwort', () => {
  const a = quickConnection({ kind: 'terminal', host: 'Box.lan', port: null, username: 'anna' });
  assert.equal(a.id, 'quick:terminal:box.lan:22:anna');
  assert.deepEqual(a.quick, { kind: 'terminal', host: 'Box.lan', port: 22, username: 'anna' });
  assert.equal(quickConnection({ kind: 'desktop', host: 'pc', port: null, username: '' }).quick.port, 3389);
  assert.ok(!JSON.stringify(a).toLowerCase().includes('password'));
});

test('VNC: Schema vnc:// und Port 5900 ergeben die Art vnc, der Standardport einer Quick-Sitzung ist 5900', () => {
  assert.deepEqual(parseQuickTarget('vnc://pc.lan'), { kind: 'vnc', host: 'pc.lan', port: null, username: '' });
  assert.deepEqual(parseQuickTarget('VNC://anna@pc.lan:5901/'), { kind: 'vnc', host: 'pc.lan', port: 5901, username: 'anna' });
  assert.deepEqual(parseQuickTarget('10.0.0.5:5900'), { kind: 'vnc', host: '10.0.0.5', port: 5900, username: '' });
  assert.equal(parseQuickTarget('10.0.0.5:5901').kind, null);
  assert.equal(quickConnection({ kind: 'vnc', host: 'pc', port: null, username: '' }).quick.port, 5900);
  assert.equal(quickConnection({ kind: 'vnc', host: 'pc', port: null, username: '' }).id, 'quick:vnc:pc:5900:');
});
