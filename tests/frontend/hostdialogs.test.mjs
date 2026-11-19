// Die beiden Dialoge der Host-Aktionen (hostinfo-dialog.js, hostactions-dialog.js) mit einem winzigen Ersatz fuer das DOM: der Ablauf Taste -> Bestaetigung -> Tippen -> Ausfuehren -> Fortschritt -> Ergebnis,
// und dass nichts ohne die Bestaetigung an den Server geht. Kein Browser noetig; echte Browser-Pruefung auf dem Handy steht aus (siehe CHANGELOG).
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';

setPref('de', null);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/** Ein Element mit dem Noetigsten: Eigenschaften, Zuhoerer, showModal/close */
function el(id) {
  const listeners = {};
  const e = {
    id, innerHTML: '', textContent: '', hidden: false, disabled: false, className: '', value: '', open: false,
    addEventListener(type, fn) { (listeners[type] ||= []).push(fn); },
    fire(type, ev = {}) { for (const fn of listeners[type] || []) fn(ev); },
    showModal() { e.open = true; }, close() { e.open = false; e.fire('close'); }, focus() {},
    querySelector() { return null; },
  };
  return e;
}

function setupDom() {
  const els = new Map();
  globalThis.document = {
    querySelector(sel) { if (!els.has(sel)) els.set(sel, el(sel)); return els.get(sel); },
    documentElement: { classList: { contains: () => false } },
  };
  return els;
}

const host = { id: 'h1', name: 'Server', agent: { online: true, kind: 'host', version: '0.6.0', control: 'full', allow: ['updates', 'reboot', 'services', 'docker', 'logs'], caps: ['sysinfo', 'updates', 'services', 'docker'] } };
const click = (target) => ({ target: { closest: (sel) => (sel === '[data-hact]' && target.hact ? { dataset: { hact: target.hact, htarget: target.htarget } } : null), id: '' } });

test('Aktionsdialog: erst Bestaetigung holen, ohne Tippen nichts, mit Tippen ausfuehren, Fortschritt bis zum Ergebnis', async () => {
  const els = setupDom();
  const { initHostActionDialog } = await import('../../src/JulOS.Frontend/wwwroot/hostactions-dialog.js?t=1');
  const calls = [];
  let polls = 0;
  const api = async (path, o = {}) => {
    calls.push([o.method || 'GET', path, o.body]);
    if (path.endsWith('/prepare')) return { token: 'T', expiresIn: 60, action: 'power.reboot', target: null, host: 'Server', typeToConfirm: 'srv-01', warnings: [], readOnly: false };
    if (path.endsWith('/run')) return { job: 'J1', state: 'running' };
    polls++;
    return polls < 2 ? { job: 'J1', action: 'power.reboot', state: 'running', progress: 'x' } : { job: 'J1', action: 'power.reboot', state: 'accepted', result: { accepted: true } };
  };
  const open = initHostActionDialog({ api, esc });
  let done = null;
  const closed = open(host, 'power.reboot', null, { onDone: (j) => { done = j; } });
  await new Promise((r) => setTimeout(r, 20));
  assert.deepEqual(calls.map((c) => c[1]), ['/api/hosts/h1/actions/prepare']);           // nur die Anfrage der Bestaetigung
  assert.equal(els.get('#hostActConfirm').hidden, false);
  assert.equal(els.get('#hostActConfirm').disabled, true);                               // Name noch nicht getippt
  assert.match(els.get('#hostActBody').innerHTML, /srv-01/);
  assert.equal(els.get('#hostActTitle').textContent, 'Neustart');

  await els.get('#hostActConfirm').onclick();                                            // Klick ohne getippten Namen: nichts geht raus
  assert.equal(calls.length, 1);

  const typed = els.get('#hostActTyped');
  assert.ok(typed, 'Eingabefeld fuer den Namen');
  typed.value = 'srv-01';
  typed.fire('input');
  assert.equal(els.get('#hostActConfirm').disabled, false);
  await els.get('#hostActConfirm').onclick();
  assert.deepEqual(calls[1], ['POST', '/api/hosts/h1/actions/run', { token: 'T', action: 'power.reboot', target: null, typed: 'srv-01' }]);
  for (let i = 0; i < 50 && !done; i++) await new Promise((r) => setTimeout(r, 100));
  assert.ok(done, 'Endzustand erreicht');
  assert.match(els.get('#hostActBody').innerHTML, /startet gleich neu/);
  els.get('#hostActDlg').close();
  await closed;
});

test('Aktionsdialog: ein Fehler beim Holen der Bestaetigung zeigt den uebersetzten Grund und laesst nichts laufen', async () => {
  const els = setupDom();
  const { initHostActionDialog } = await import('../../src/JulOS.Frontend/wwwroot/hostactions-dialog.js?t=2');
  const calls = [];
  const api = async (path, o = {}) => { calls.push(path); const e = new Error('Aktionen sind für diesen Host nicht eingeschaltet.'); e.code = 'control_off'; throw e; };
  const open = initHostActionDialog({ api, esc });
  const closed = open(host, 'service.restart', 'nginx.service');
  await new Promise((r) => setTimeout(r, 20));
  assert.deepEqual(calls, ['/api/hosts/h1/actions/prepare']);
  assert.match(els.get('#hostActMsg').textContent, /nicht eingeschaltet/);
  assert.equal(els.get('#hostActConfirm').hidden, true);
  els.get('#hostActDlg').close();
  await closed;
});

test('Aktionsdialog: ein falsch getippter Name beim Ausfuehren (Server) laesst die Taste frei', async () => {
  const els = setupDom();
  const { initHostActionDialog } = await import('../../src/JulOS.Frontend/wwwroot/hostactions-dialog.js?t=3');
  const api = async (path) => {
    if (path.endsWith('/prepare')) return { token: 'T', expiresIn: 60, action: 'service.restart', target: 'a.service', host: 'S', warnings: ['julos_group'] };
    const e = new Error('x'); e.code = 'typed_mismatch'; throw e;
  };
  const open = initHostActionDialog({ api, esc });
  const closed = open(host, 'service.restart', 'a.service');
  await new Promise((r) => setTimeout(r, 20));
  assert.match(els.get('#hostActBody').innerHTML, /JulOS selbst/);                       // Warnung statt Sperre
  assert.equal(els.get('#hostActConfirm').disabled, false);                              // nichts zu tippen
  await els.get('#hostActConfirm').onclick();
  assert.match(els.get('#hostActMsg').textContent, /Name stimmt nicht/);
  assert.equal(els.get('#hostActConfirm').disabled, false);
  els.get('#hostActDlg').close();
  await closed;
});

test('Host-Details: Tasten oeffnen nur den Bestaetigungsdialog; Stufe und Pause gehen an den Server und laden neu', async () => {
  const els = setupDom();
  const { initHostInfoDialog } = await import('../../src/JulOS.Frontend/wwwroot/hostinfo-dialog.js?t=4');
  const calls = [];
  const state = { hosts: [host], actionsPaused: false };
  const api = async (path, o = {}) => {
    calls.push([o.method || 'GET', path, o.body]);
    if (path.endsWith('/status')) return { state: 'ok', info: { hostname: 'srv-01', memory: {}, disks: [] } };
    if (path.endsWith('/updates')) return { state: 'ok', updates: { manager: 'apt', count: 1, security: 0, securityKnown: true, packages: [{ name: 'libc6', from: '1', to: '2' }], keptBack: [], held: [], removals: 0, truncated: false } };
    if (path.endsWith('/services')) return { state: 'ok', services: { state: 'running', count: 0, failed: [] } };
    if (path.endsWith('/docker')) return { state: 'ok', docker: { available: true, running: 0, total: 0, restarting: 0, containers: [], truncated: false } };
    if (path.includes('/units')) return { state: 'ok', units: { count: 1, truncated: false, units: [{ unit: 'nginx.service', active: 'active', sub: 'running' }] } };
    if (path.includes('/audit')) return { entries: [{ id: 1, at: '2026-11-19T10:00:00Z', user: 'jul', action: 'updates.refresh', event: 'ok' }] };
    return {};
  };
  const opened = [];
  let reloads = 0;
  const open = initHostInfoDialog({ state, api, esc, store: { load() {} }, openAction: (...a) => { opened.push(a.slice(0, 3)); }, reloadHosts: async () => { reloads++; } });
  open(host);
  await new Promise((r) => setTimeout(r, 30));
  const body = els.get('#hostInfoBody');
  assert.match(els.get('#hostInfoBody [data-kind="updates"] .hi-body').innerHTML, /data-hact="updates.install" data-htarget="libc6"/);    // der Abschnitt wurde mit Tasten gezeichnet
  assert.match(body.innerHTML, /data-hact-open="history"/);
  assert.match(body.innerHTML, /id="hostActLevel"/);
  const reads = calls.length;
  body.fire('click', click({ hact: 'updates.install', htarget: 'libc6' }));
  assert.deepEqual(opened, [[host, 'updates.install', 'libc6']]);
  assert.equal(calls.length, reads);                                                     // die Taste selbst schickt nichts an den Server
  assert.ok(calls.every((c) => c[0] === 'GET'));

  body.fire('change', { target: { id: 'hostActLevel', value: 'updates' } });
  await new Promise((r) => setTimeout(r, 30));
  assert.ok(calls.some((c) => c[0] === 'PUT' && c[1] === '/api/hosts/h1/agent/control' && c[2].level === 'updates'));
  body.fire('change', { target: { id: 'hostActPause', checked: true } });
  await new Promise((r) => setTimeout(r, 30));
  assert.ok(calls.some((c) => c[0] === 'PUT' && c[1] === '/api/host-actions/pause' && c[2].paused === true));
  assert.equal(reloads, 2);

  // Ohne Erlaubnis keine Tasten
  const readOnly = { ...host, agent: { ...host.agent, control: 'read' } };
  state.hosts = [readOnly];
  open(readOnly);
  await new Promise((r) => setTimeout(r, 30));
  assert.ok(!els.get('#hostInfoBody').innerHTML.includes('data-hact="') && !els.get('#hostInfoBody [data-kind="updates"] .hi-body').innerHTML.includes('data-hact="'));
});
