// Der Abschnitt "Agent" der Host-Seite (hostagent.js, hostpage.js): EINE Einrichtung mit Haken fuer jede Maschine, Befehl, Aktualisieren und Entfernen. Reine Funktionen und die Seite mit einem Ersatz fuer das DOM.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { agentPanel, newSetup, setupCommand, updateHint, ALLOW_CHOICES } from '../../src/JulOS.Frontend/wwwroot/hostagent.js';

setPref('de', null);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const agent = (o = {}) => ({ online: true, kind: 'host', version: '0.6.0', caps: ['sysinfo'], allow: [], control: 'read', canUpdate: true, autoUpdate: false, updateAvailable: false, ...o });
const host = (o = {}) => ({ id: 'h1', name: 'Server', online: true, agent: agent(o) });
const html = (h, setup = newSetup(), terminal = null) => agentPanel({ host: h, esc, setup, terminal });

test('Die Haken: nur diese fuenf Woerter, nie ein Standard, die Docker-Gruppe erst mit Docker', () => {
  assert.deepEqual(ALLOW_CHOICES.map((c) => c.word), ['updates', 'logs', 'services', 'docker', 'reboot']);
  const h = html(host());
  assert.equal((h.match(/data-allow="/g) || []).length, 5);
  assert.doesNotMatch(h, /data-allow="[a-z]+" checked/);                                       // kein Haken der Einrichtung ist vorgewaehlt
  assert.match(h, /data-hag-grouprow hidden/);                                                  // Docker-Gruppe verborgen, solange Docker nicht angehakt ist
  const s = { ...newSetup(), allow: ['docker'], dockerGroup: true };
  const on = html(host(), s);
  assert.match(on, /data-allow="docker" checked/);
  assert.doesNotMatch(on, /data-hag-grouprow hidden/);
  assert.match(on, /data-hag-group="1" checked/);
  assert.match(on, /entspricht root auf dem Rechner/);                                           // die ernste Entscheidung steht dabei
});

test('Der Befehl: ein Installer fuer jede Maschine, die Haken werden --allow, die Gruppe nur mit docker', () => {
  const c = (o) => setupCommand('https://julos.example.org', 'ABCDE-FGHIJ', { ...newSetup(), ...o });
  assert.equal(c({}), 'curl -fsSL https://julos.example.org/agent-files/install-agent.sh -o julos-install-agent.sh && sudo sh julos-install-agent.sh --server https://julos.example.org --code ABCDE-FGHIJ');
  assert.match(c({ allow: ['reboot', 'updates'] }), /--allow updates,reboot$/);
  assert.match(c({ allow: ['docker'], dockerGroup: true }), /--allow docker --docker-group$/);
  assert.doesNotMatch(c({ allow: ['logs'], dockerGroup: true }), /docker-group/);
});

test('Zustand und Aktualisieren: Hinweise, Taste gesperrt wenn offline oder der Server es nicht kann, Entfernen nur mit Agent', () => {
  assert.equal(updateHint(agent()), '');
  assert.match(updateHint(agent({ updateAvailable: true, canUpdate: false })), /./);
  assert.match(updateHint(agent({ updateAvailable: true, online: false })), /./);
  assert.equal(updateHint(null), '');
  const due = html(host({ updateAvailable: true, latest: '0.7.0' }));
  assert.match(due, /<button type="button" class="hu-btn filled"[^>]*data-hag-update="1"/);
  assert.doesNotMatch(due, /disabled class="hu-btn filled"|<button type="button" disabled class="hu-btn filled"/);
  assert.match(due, /\(0\.7\.0\)/);
  assert.match(html(host({ updateAvailable: true, online: false })), /<button type="button" disabled class="hu-btn filled"[^>]*data-hag-update/);
  assert.match(html(host()), /data-hag-remove="1"/);
  assert.match(html(host({ autoUpdate: true })), /data-hag-auto="1" checked/);
  const none = html({ id: 'h2', name: 'x', agent: undefined });
  assert.doesNotMatch(none, /data-hag-remove|data-hag-update|data-hag-auto/);                    // ohne Agent nichts zu entfernen oder zu aktualisieren
  assert.match(none, /Agent einrichten/); assert.match(none, /data-hag-copy="1"/);
});

test('Installation per SSH nur mit Terminal dieses Hosts, sonst nur der Befehl zum Kopieren; Befehl und Namen geschuetzt', () => {
  assert.doesNotMatch(html(host()), /data-hag-ssh/);
  assert.match(html(host()), /keine SSH-Verbindung/);
  const withTerm = html(host(), newSetup(), { name: '<img src=x>' });
  assert.match(withTerm, /data-hag-ssh="1"/); assert.doesNotMatch(withTerm, /<img/); assert.match(withTerm, /&lt;img/);
  const shown = html(host(), { ...newSetup(), command: 'sudo sh x --code "<b>"', note: 'n' });
  assert.match(shown, /<pre class="hu-pre" id="hostAgentCmd">sudo sh x --code &quot;&lt;b&gt;&quot;<\/pre>/);
});

test('Seite: Haken setzen den Befehl zurueck, "Befehl kopieren" holt einen frischen Code, per SSH legt den Befehl nur in die Eingabeleiste', async () => {
  const { initHostPage } = await import('../../src/JulOS.Frontend/wwwroot/hostpage.js?t=agent');
  const els = new Map();
  const mk = (id) => { const e = { id, innerHTML: '', textContent: '', hidden: false, disabled: false, className: '', value: '', dataset: {}, setAttribute() {}, getAttribute() {}, querySelector() { return null; }, remove() {}, addEventListener() {}, focus() {} }; return e; };
  globalThis.document = { querySelector: (sel) => { if (!els.has(sel)) els.set(sel, mk(sel)); return els.get(sel); }, documentElement: { classList: { contains: () => false } } };
  const ls = [];
  const rootEl = { innerHTML: '', ownerDocument: null, querySelector: (sel) => globalThis.document.querySelector(sel), querySelectorAll: () => [], addEventListener: (n, f) => ls.push([n, f]), removeEventListener: () => {}, fire: (n, ev) => { for (const [m, f] of [...ls]) if (m === n) f(ev); } };
  const calls = [], ssh = [];
  const h = { id: 'h1', name: 'Server', online: true, services: [], agent: agent({ caps: [] }) };
  const state = { hosts: [h], connections: [{ id: 'c1', hostId: 'h1', kind: 'terminal', name: 'Terminal', lastUsedAt: '2026-01-01' }], actionsPaused: false };
  const api = async (path, o = {}) => { calls.push([o.method || 'GET', path]); return path.endsWith('/agent/code') ? { code: 'ABCDE-FGHIJ', expiresIn: 600 } : {}; };
  const page = initHostPage({ state, api, esc, store: { load() {} }, openAction() {}, reloadHosts: async () => {}, installOverSsh: (c, cmd) => ssh.push([c.id, cmd]), origin: () => 'https://julos.example.org' });
  page.mount(rootEl, h, 'agent');
  await new Promise((r) => setTimeout(r, 20));
  assert.match(els.get('#hostInfoBody [data-panel="agent"]').innerHTML, /data-hag-ssh="1"/);
  assert.equal(calls.length, 0);                                                                 // nichts gelesen, nichts erzeugt, bevor jemand es verlangt
  const change = (dataset, checked) => rootEl.fire('change', { target: { dataset, checked, id: '' } });
  change({ allow: 'updates' }, true); change({ allow: 'docker' }, true); change({ allow: 'logs' }, true); change({ allow: 'logs' }, false);
  const click = (attr) => rootEl.fire('click', { target: { closest: (sel) => (sel === attr ? { dataset: {} } : null), id: '' } });
  click('[data-hag-ssh]');
  await new Promise((r) => setTimeout(r, 20));
  assert.deepEqual(calls, [['POST', '/api/hosts/h1/agent/code']]);
  assert.equal(ssh.length, 1);
  assert.equal(ssh[0][0], 'c1');
  assert.match(ssh[0][1], /^curl -fsSL https:\/\/julos\.example\.org\/agent-files\/install-agent\.sh -o julos-install-agent\.sh && sudo sh julos-install-agent\.sh --server https:\/\/julos\.example\.org --code ABCDE-FGHIJ --allow updates,docker$/);
  // ein Haken nach dem Befehl macht ihn ungueltig (er gehoert zu den alten Haken)
  els.get('.hu-cmd'); let removed = 0; els.get('.hu-cmd').remove = () => { removed++; };
  change({ allow: 'reboot' }, true);
  assert.equal(removed, 1);
  delete globalThis.document;
});
