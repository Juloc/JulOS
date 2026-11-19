import { test } from 'node:test';
import assert from 'node:assert/strict';
import { connLabels, hostLabel } from '../../src/JulOS.Frontend/wwwroot/conn-names.js';

const hosts = [{ id: 'h1', name: 'host-a' }, { id: 'h2', name: 'host-b' }];
const c = (id, name, extra = {}) => ({ id, name, kind: 'terminal', host: '192.0.2.1', port: 22, hostId: 'h1', ...extra });

test('die Host-Zeile ist der Name des Hosts, ohne Host die Adresse', () => {
  assert.equal(hostLabel(hosts, c('a', 'X')), 'host-a');
  assert.equal(hostLabel(hosts, c('a', 'X', { hostId: null })), '192.0.2.1');
});

test('einmalige Namen bleiben unveraendert', () => {
  const m = connLabels([c('a', 'Codex'), c('b', 'Claude')], hosts);
  assert.equal(m.get('a').name, 'Codex'); assert.equal(m.get('b').host, 'host-a');
});

test('gleiche Namen bekommen den Host, bei gleichem Host die Art, zuletzt die Adresse', () => {
  let m = connLabels([c('a', 'Codex'), c('b', 'codex', { hostId: 'h2' })], hosts);
  assert.equal(m.get('a').name, 'Codex (host-a)'); assert.equal(m.get('b').name, 'codex (host-b)');
  m = connLabels([c('a', 'PC', { kind: 'desktop' }), c('b', 'PC', { kind: 'vnc' })], hosts);
  assert.equal(m.get('a').name, 'PC (Desktop)'); assert.equal(m.get('b').name, 'PC (VNC)');
  m = connLabels([c('a', 'PC', { port: 22 }), c('b', 'PC', { port: 2222 })], hosts);
  assert.equal(m.get('a').name, 'PC (192.0.2.1:22)'); assert.equal(m.get('b').name, 'PC (192.0.2.1:2222)');
});
