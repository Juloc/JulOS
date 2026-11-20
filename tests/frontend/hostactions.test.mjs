// Aktionen auf Hosts (hostactions.js, Stufe 3): wann Tasten erscheinen, Text der Bestaetigung, Tippen des Rechnernamens, Fortschritt und Ergebnis, Verlauf, Escaping, Aufrufe. Reine Logik.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import {
  canAct, hasActionAgent, blockReason, whyText, confirmModel, confirmHtml, typedOk, typedName, targetText, isDanger, actionLabel, errText, jobHtml, jobFinal, jobPercent,
  auditHtml, auditLine, controlHtml, makeAct, unitsHtml, prepareAction, runAction, fetchJob, setControl, setPause, ACTION_LABEL, RIGHTS,
} from '../../src/JulOS.Frontend/wwwroot/hostactions.js';
import { PANELS } from '../../src/JulOS.Frontend/wwwroot/hostview.js';
import de from '../../src/JulOS.Frontend/wwwroot/i18n/de.js';
import en from '../../src/JulOS.Frontend/wwwroot/i18n/en.js';

setPref('de', null);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
/** Ein Abschnitt des Dialogs (hostview.js) mit den Antworten des Servers und den Tasten nach Erlaubnis */
const panel = (id, ctx) => PANELS.find((p) => p.id === id).html({ esc, host: host(), paused: false, data: {}, ...ctx });
const host = (over = {}) => ({ id: 'h1', name: 'Server', agent: { online: true, kind: 'host', version: '0.6.0', control: 'full', allow: ['updates', 'reboot', 'services', 'docker', 'logs'], caps: ['sysinfo', 'updates', 'services', 'docker'], ...over } });

test('Tasten erscheinen nur, wenn Stufe am Server UND lokale Erlaubnis des Agenten passen, der Agent neu genug und verbunden ist und nichts pausiert ist', () => {
  assert.ok(canAct(host(), 'reboot'));
  assert.ok(!canAct(host({ control: 'read' }), 'updates'));                       // Standard: aus
  assert.ok(!canAct(host({ control: 'updates' }), 'reboot'));
  assert.ok(canAct(host({ control: 'updates' }), 'logs'));
  assert.ok(!canAct(host({ allow: ['updates'] }), 'reboot'));                     // der Agent erlaubt es lokal nicht
  assert.ok(!canAct(host({ allow: [] }), 'updates'));
  assert.ok(!canAct(host({ version: '0.5.0' }), 'updates'));
  assert.ok(canAct(host({ version: '0.10.1' }), 'updates'));
  assert.ok(!canAct(host({ online: false }), 'updates'));
  assert.ok(canAct(host({ kind: 'desktop' }), 'updates'));                       // massgeblich sind die gemeldeten Faehigkeiten, nicht der Name der Plattform
  assert.ok(!canAct(host({ kind: 'desktop', caps: [] }), 'updates'));            // ein Agent ohne Faehigkeiten kann nichts
  assert.ok(!canAct(host(), 'updates', true));                                   // Pause
  assert.ok(!canAct(host({ control: 'bogus' }), 'updates'));
  assert.ok(!canAct({ id: 'x' }, 'updates') && !canAct(null, 'updates'));
  assert.ok(hasActionAgent(host()) && hasActionAgent(host({ kind: 'desktop' })) && !hasActionAgent(host({ kind: 'desktop', caps: [] })) && !hasActionAgent(host({ version: '0.5.0' })));
});

test('Warum eine Aktion gesperrt ist: Grund und Weg zum Freischalten (Abschnitt Aktionen) oder nichts zu tun', () => {
  assert.equal(blockReason(host(), 'reboot'), null);
  assert.deepEqual(blockReason(null, 'updates'), { why: 'noAgent' });
  assert.deepEqual(blockReason(host({ online: false }), 'updates'), { why: 'offline' });
  assert.deepEqual(blockReason(host({ caps: [], kind: 'desktop' }), 'updates'), { why: 'noCaps' });
  assert.deepEqual(blockReason(host({ version: '0.5.0' }), 'updates'), { why: 'outdated' });
  assert.deepEqual(blockReason(host(), 'updates', true), { why: 'paused', link: 'actions' });
  assert.deepEqual(blockReason(host({ control: 'read' }), 'updates'), { why: 'levelOff', link: 'actions' });
  assert.deepEqual(blockReason(host({ control: 'updates' }), 'reboot'), { why: 'level', link: 'actions' });
  assert.deepEqual(blockReason(host({ allow: ['updates'] }), 'docker'), { why: 'word', word: 'docker', link: 'actions' });
  assert.match(whyText({ why: 'word', word: 'docker' }), /Wort docker.*Agent neu einrichten/);
  assert.match(whyText({ why: 'levelOff', link: 'actions' }), /unter Aktionen einschalten/);
  assert.match(whyText({ why: 'unbekannt' }), /kann das noch nicht/);                     // Unbekanntes: allgemeiner Satz
});

test('Text der Bestaetigung nennt Host, Aktion und Ziel', () => {
  const conf = { token: 't', expiresIn: 60, action: 'service.restart', target: 'nginx.service', targetName: 'nginx.service', host: 'Server', warnings: [] };
  const m = confirmModel(conf);
  assert.equal(m.title, 'Dienst neu starten');
  assert.equal(m.sentence, 'Auf Server: Dienst neu starten – nginx.service.');
  assert.deepEqual(m.rows, [['Host', 'Server'], ['Aktion', 'Dienst neu starten'], ['Ziel', 'nginx.service']]);
  assert.equal(m.typed, null);
  assert.equal(m.danger, false);
  const reboot = confirmModel({ token: 't', expiresIn: 60, action: 'power.reboot', host: 'Server', typeToConfirm: 'srv-01', warnings: [] });
  assert.equal(reboot.sentence, 'Auf Server: Neustart.');
  assert.equal(reboot.typed, 'srv-01');
  assert.ok(reboot.danger);
  assert.equal(targetText({ action: 'updates.install', target: 'all' }), 'alle Updates');
  assert.equal(targetText({ action: 'docker.stop', target: 'abcdef123456', targetName: 'web' }), 'web');
  setPref('en', null);
  assert.equal(confirmModel(conf).sentence, 'On Server: Restart service – nginx.service.');
  setPref('de', null);
});

test('Warnungen: JulOS-Gruppe und zurueckgehaltene Pakete, Unbekanntes allgemein', () => {
  const m = confirmModel({ action: 'docker.stop', target: 'aaaaaaaaaaaa', targetName: 'julos-app', host: 'S', expiresIn: 60, warnings: ['julos_group', 'kept_back', 'fremd; <b>'] });
  assert.equal(m.warnings.length, 3);
  assert.match(m.warnings[0], /JulOS selbst/);
  assert.match(m.warnings[1], /Zurückgehaltene/);
  assert.match(m.warnings[2], /genau lesen/);
  assert.ok(!m.warnings[2].includes('fremd'));                                   // kein Text des Servers ungeprueft in der Oberflaeche
});

test('Rechnername tippen: Gross/Klein und Leerraum zaehlen nicht, sonst wird nicht bestaetigt', () => {
  const conf = { typeToConfirm: 'Srv-01' };
  assert.equal(typedName(conf), 'Srv-01');
  assert.ok(typedOk(conf, ' srv-01 '));
  assert.ok(typedOk(conf, 'SRV-01'));
  assert.ok(!typedOk(conf, 'srv-0'));
  assert.ok(!typedOk(conf, ''));
  assert.ok(!typedOk(conf, null));
  assert.ok(typedOk({}, ''));                                                    // ohne Anforderung nichts zu tippen
  assert.equal(typedName({ typeToConfirm: '' }), null);
  assert.ok(isDanger('power.poweroff') && isDanger('service.stop') && isDanger('docker.stop') && isDanger('updates.install', 'all'));
  assert.ok(!isDanger('updates.install', 'libc6') && !isDanger('service.start') && !isDanger('logs.unit'));
});

test('Bestaetigung als HTML: Werte werden geschuetzt, der Name zum Tippen steht im Satz', () => {
  const conf = { token: 'x', expiresIn: 60, action: 'power.reboot', host: '<img src=x onerror=alert(1)>', typeToConfirm: '"><script>', warnings: [] };
  const html = confirmHtml(conf, esc);
  assert.ok(!html.includes('<img') && !html.includes('<script'), html);
  assert.ok(html.includes('&lt;img'));
  assert.ok(html.includes('id="hostActTyped"'));
  assert.ok(html.includes('60'));
  assert.ok(!confirmHtml({ ...conf, action: 'service.start', typeToConfirm: undefined, target: 'a.service' }, esc, false).includes('hostActTyped'));
});

test('Aktionstasten tragen nur Aktion und Ziel, geschuetzt; ohne Erlaubnis gibt es keine', () => {
  const act = makeAct(host(), false, esc, false);
  const b = act.btn('service.restart', 'x"><script>alert(1)</script>.service', 'Neustart');
  assert.ok(!b.includes('<script'));
  assert.match(b, /data-hact="service.restart"/);
  assert.match(b, /data-htarget="x&quot;&gt;&lt;script&gt;/);
  assert.match(act.btn('power.reboot', '', 'Neustart'), /class="hu-btn sm danger"/);
  assert.match(act.btn('service.restart', 'a.service', 'x'), /hu-btn sm tonal/);
  assert.match(act.btn('service.stop', 'a.service', 'x'), /hu-btn sm danger/);
  assert.equal(makeAct(host({ allow: [] }), false, esc, false).btn('service.restart', 'a.service', 'x'), '');
  assert.equal(makeAct(host(), true, esc, false).btn('service.restart', 'a.service', 'x'), '');
  assert.equal(makeAct(host(), false, esc, false).bar(''), '');
  assert.ok(RIGHTS.every((r) => ['updates', 'reboot', 'services', 'docker', 'logs'].includes(r)));
});

test('die Abschnitte zeigen ohne act keine Tasten und mit act Tasten nach Erlaubnis', () => {
  const updates = { state: 'ok', updates: { manager: 'apt', count: 2, security: 1, securityKnown: true, packages: [{ name: 'libc6', from: '1', to: '2', security: true }, { name: 'x" onclick="y', from: '1', to: '2' }], keptBack: [], held: [], removals: 0, truncated: false } };
  const A = (over) => makeAct(host(over), false, esc);
  assert.ok(!panel('updates', { data: { updates } }).includes('data-hact'));
  const on = panel('updates', { data: { updates }, act: A() });
  assert.equal((on.match(/data-hact="updates.install"/g) || []).length, 3);        // zwei Pakete und "alle"
  assert.ok(on.includes('data-hact="updates.refresh"'));
  assert.ok(on.includes('data-htarget="all"'));
  assert.ok(!on.includes('onclick="y'));                                           // der Paketname bleibt Text, kein Attribut
  assert.ok(!panel('updates', { data: { updates }, act: A({ control: 'read' }) }).includes('data-hact'));
  const empty = panel('updates', { data: { updates: { state: 'ok', updates: { ...updates.updates, count: 0, packages: [] } } }, act: A() });
  assert.ok(!empty.includes('data-htarget="all"'));                                // nichts zu installieren
  const power = panel('actions', { host: host(), act: A() });
  assert.ok(power.includes('data-hact="power.reboot"') && power.includes('data-hact="power.poweroff"'));
  assert.ok(!panel('actions', { host: host({ control: 'updates' }), act: A({ control: 'updates' }) }).includes('power.reboot'));
  const svc = panel('services', { data: { services: { state: 'ok', services: { state: 'degraded', count: 1, failed: [{ unit: 'a.service', sub: 'failed', description: '<b>x</b>' }] } } }, act: A() });
  assert.ok(svc.includes('data-hact="service.restart"') && svc.includes('data-hact="logs.unit"') && svc.includes('data-hact-open="units"'));
  assert.ok(!svc.includes('<b>x</b>'));
  const dockData = { state: 'ok', docker: { available: true, running: 1, total: 2, restarting: 0, truncated: false, containers: [{ id: 'aaaaaaaaaaaa', name: 'web', image: 'nginx:1', state: 'running', status: 'Up' }, { id: 'bbbbbbbbbbbb', name: 'old', image: 'x', state: 'exited', status: 'Exited' }] } };
  const dock = panel('docker', { data: { docker: dockData }, act: A() });
  assert.ok(dock.includes('data-hact="docker.stop" data-htarget="aaaaaaaaaaaa"') && dock.includes('data-hact="docker.restart"'));
  assert.ok(dock.includes('data-hact="docker.start" data-htarget="bbbbbbbbbbbb"'));
  assert.ok(!dock.includes('data-hact="docker.start" data-htarget="aaaaaaaaaaaa"'));
  assert.equal((dock.match(/data-hact="docker.pull"/g) || []).length, 2);
});

test('Dienstliste mit Tasten: laufende Neustart/Stopp/Logs, andere Start/Logs; fremde Namen geschuetzt', () => {
  const html = unitsHtml({ state: 'ok', units: { count: 2, truncated: false, units: [{ unit: 'nginx.service', active: 'active', sub: 'running', description: 'Web' }, { unit: '<i>b</i>.service', active: 'inactive', sub: 'dead' }] } }, esc, makeAct(host(), false, esc, false), false);
  assert.ok(html.includes('data-hact="service.restart" data-htarget="nginx.service"'));
  assert.ok(html.includes('data-hact="service.stop" data-htarget="nginx.service"'));
  assert.ok(html.includes('data-hact="service.start" data-htarget="&lt;i&gt;b&lt;/i&gt;.service"'));
  assert.ok(!html.includes('<i>b'));
  assert.match(unitsHtml({ state: 'error', error: 'control_off' }, esc, makeAct(host(), false, esc, false), false), /nicht eingeschaltet/);
});

test('Fortschritt: Text, Balken mit Zaehlern, Prozent', () => {
  assert.equal(jobPercent({ done: 1, total: 4 }), 25);
  assert.equal(jobPercent({ done: 9, total: 4 }), 100);
  assert.equal(jobPercent({ total: 0 }), null);
  assert.equal(jobPercent(null), null);
  const html = jobHtml({ action: 'updates.install', state: 'running', progress: 'Setting up <b>libc6</b>', done: 1, total: 4 }, esc, false);
  assert.ok(html.includes('value="25"') && html.includes('(1/4)'));
  assert.ok(!html.includes('<b>libc6'));
  assert.ok(jobHtml({ action: 'updates.install', state: 'running' }, esc, false).includes('<progress></progress>'));
  assert.match(jobHtml({ action: 'updates.install', state: 'lost' }, esc, false), /Verbindung zum Agenten weg/);
  assert.ok(jobFinal({ state: 'ok' }) && jobFinal({ state: 'failed' }) && jobFinal({ state: 'accepted' }) && !jobFinal({ state: 'running' }) && !jobFinal({ state: 'lost' }) && !jobFinal(null));
});

test('Ergebnis: erledigt mit Neustart-Hinweis, angenommen, fehlgeschlagen mit Code, Logs und Ausgabe als Text', () => {
  const ok = jobHtml({ action: 'updates.install', state: 'ok', result: { ok: true, rebootRequired: true, output: 'Setting up <x>' } }, esc, false);
  assert.match(ok, /Erledigt: Updates installieren/);
  assert.match(ok, /Neustart ist nötig/);
  assert.ok(ok.includes('Setting up &lt;x&gt;') && !ok.includes('<x>'));
  // Ausgabe eines Programms (apt): jede Zeile bleibt eine Zeile, als Text im <pre> (CSS pre-wrap), nie als HTML
  const apt = jobHtml({ action: 'updates.refresh', state: 'ok', result: { ok: true, output: 'Hit:1 http://x\nGet:2 <b>y</b>\nReading package lists...' } }, esc);
  assert.ok(apt.includes('<pre class="hu-pre">Hit:1 http://x\nGet:2 &lt;b&gt;y&lt;/b&gt;\nReading package lists...</pre>'), apt);
  assert.ok(!apt.includes('<b>y'));
  assert.match(jobHtml({ action: 'power.reboot', state: 'accepted', result: { accepted: true } }, esc, false), /startet gleich neu/);
  assert.match(jobHtml({ action: 'power.poweroff', state: 'accepted', result: { accepted: true } }, esc, false), /fährt gleich herunter/);
  const failed = jobHtml({ action: 'updates.refresh', state: 'failed', error: 'apt_locked', result: { output: 'E: lock' } }, esc, false);
  assert.match(failed, /Fehlgeschlagen/);
  assert.match(failed, /Paketverwaltung ist gerade belegt/);
  assert.ok(failed.includes('E: lock'));
  assert.match(jobHtml({ action: 'x', state: 'failed', error: 'Rm -rf /' }, esc, false), /Die Aktion ist fehlgeschlagen/);   // unbekannter Code: allgemeiner Text, nie roh
  const logs = jobHtml({ action: 'logs.unit', state: 'ok', result: { lines: ['a <script>', 'b'] } }, esc, true);
  assert.ok(logs.includes('a &lt;script&gt;\nb') && !logs.includes('<script>') && logs.includes('class="hu-pre"'));
  assert.match(jobHtml({ action: 'logs.unit', state: 'ok', result: { lines: [] } }, esc, false), /Keine Zeilen/);
  assert.match(jobHtml({ action: 'docker.pull', state: 'ok', result: { updated: true } }, esc, false), /neueres Image/);
  assert.match(jobHtml({ action: 'docker.pull', state: 'ok', result: { updated: false } }, esc, false), /schon aktuell/);
  assert.match(jobHtml({ action: 'docker.start', state: 'ok', result: { already: true } }, esc, false), /schon so/);
  assert.equal(jobHtml(null, esc), '');
});

test('Fehlercodes: jeder Code des Servers und des Agenten hat einen Text in beiden Sprachen, Unbekanntes wird allgemein gemeldet', () => {
  const codes = ['host_not_found', 'no_agent', 'unknown_action', 'invalid_target', 'paused', 'control_off', 'offline', 'agent_outdated', 'unsupported', 'agent_not_permitted', 'not_permitted', 'busy', 'unknown_target',
    'list_unavailable', 'protected_unit', 'too_many', 'confirmation_invalid', 'confirmation_expired', 'confirmation_mismatch', 'typed_mismatch', 'audit_failed', 'invalid_level', 'rate_limited', 'job_not_found',
    'apt_locked', 'apt_failed', 'systemctl_failed', 'journal_failed', 'docker_denied', 'docker_unavailable', 'docker_failed', 'pull_failed', 'no_registry_image', 'helper_unavailable', 'helper_closed',
    'not_implemented', 'interrupted', 'duplicate_job', 'not_installed', 'update_check_failed', 'timeout', 'invalid_answer', 'agent_error'];
  for (const c of codes) assert.ok(errText(c) && errText(c) !== errText('so-nicht'), c);
  assert.equal(errText('so-nicht'), de['hostact.err.unknown']);
  setPref('en', null);
  assert.equal(errText('paused'), 'Actions are paused.');
  setPref('de', null);
  for (const a of Object.keys(ACTION_LABEL)) assert.notEqual(actionLabel(a), de['hostact.a.unknown'], a);
  assert.equal(actionLabel('rm -rf'), 'Aktion');
});

test('Woerterbuch: alle hostact-Schluessel in beiden Sprachen mit denselben Parametern', () => {
  const keys = Object.keys(de).filter((k) => k.startsWith('hostact.'));
  assert.ok(keys.length > 90);
  for (const k of keys) { assert.ok(en[k], k); assert.deepEqual([...(en[k].match(/\{\w+\}/g) || [])].sort(), [...(de[k].match(/\{\w+\}/g) || [])].sort(), k); }
});

test('Verlauf: neueste zuerst, Ablehnungen mit Grund, Werte geschuetzt', () => {
  const entries = [
    { id: 4, at: '2026-11-19T10:00:00Z', user: 'jul', action: 'power.reboot', event: 'denied', detail: 'control_off' },
    { id: 3, at: '2026-11-19T09:00:00Z', user: 'jul', action: 'updates.install', target: 'all', event: 'ok', detail: 'reboot_required' },
    { id: 2, at: '2026-11-19T08:00:00Z', user: 'jul', action: 'docker.stop', target: 'web (aaaaaaaaaaaa)', event: 'lost', detail: 'no_answer' },
    { id: 1, at: '2026-11-19T07:00:00Z', user: '<b>x</b>', action: 'control.set', target: 'full', event: 'ok' },
  ];
  const l = auditLine(entries[0]);
  assert.deepEqual([l.text, l.event], ['Neustart', 'abgelehnt']);
  assert.match(l.detail, /nicht eingeschaltet/);
  assert.equal(auditLine(entries[1]).detail, 'Neustart nötig');
  assert.match(auditLine(entries[2]).detail, /Ergebnis wird später abgeholt/);
  assert.match(auditLine(entries[3]).text, /Stufe geändert/);
  const html = auditHtml({ entries }, esc, false);
  assert.ok(html.includes('&lt;b&gt;x&lt;/b&gt;') && !html.includes('<b>x</b>'));
  assert.ok(html.indexOf('Neustart') < html.indexOf('Updates installieren'));
  assert.match(auditHtml({ entries: [] }, esc, false), /Noch nichts im Verlauf/);
});

test('Schalter Aktionen: Stufe, lokale Erlaubnis, Pause; ohne Erlaubnis ein Hinweis auf den Installer', () => {
  const html = controlHtml(host({ control: 'updates', allow: ['updates', 'logs'] }), true, esc, false);
  assert.match(html, /<option value="updates" selected>/);
  assert.ok(html.includes('>updates</span>') && html.includes('>logs</span>') && html.includes('id="hostActPause" type="checkbox" checked'));
  assert.ok(!html.includes('--allow'));
  const none = controlHtml(host({ control: 'read', allow: [] }), false, esc, true);
  assert.ok(none.includes('--allow') && none.includes('hu-card') && !none.includes(' checked'));
});

test('Aufrufe: erst prepare, dann run mit genau der Bestaetigung; kein Weg an prepare vorbei', async () => {
  const calls = [];
  const api = async (path, o = {}) => { calls.push([path, o.method || 'GET', o.body]); return path.endsWith('/prepare') ? { token: 'T', expiresIn: 60, action: 'service.restart', target: 'a.service' } : { job: 'J' }; };
  const conf = await prepareAction(api, 'h1', 'service.restart', 'a.service');
  assert.deepEqual(calls[0], ['/api/hosts/h1/actions/prepare', 'POST', { action: 'service.restart', target: 'a.service', lines: null }]);
  await runAction(api, 'h1', conf, null);
  assert.deepEqual(calls[1], ['/api/hosts/h1/actions/run', 'POST', { token: 'T', action: 'service.restart', target: 'a.service', typed: null }]);
  await runAction(api, 'h1', { ...conf, target: undefined, action: 'power.reboot' }, 'srv-01');
  assert.deepEqual(calls[2][2], { token: 'T', action: 'power.reboot', target: null, typed: 'srv-01' });
  await fetchJob(api, 'h1', 'abc');
  await setControl(api, 'h1', 'full');
  await setPause(api, true);
  assert.deepEqual(calls.slice(3), [['/api/hosts/h1/actions/abc', 'GET', undefined], ['/api/hosts/h1/agent/control', 'PUT', { level: 'full' }], ['/api/host-actions/pause', 'PUT', { paused: true }]]);
  assert.ok(calls.every(([p]) => !/\/(reboot|install|restart|stop)\b/.test(p.replace('/actions/', '/'))));     // der Browser kennt keinen anderen Weg zum Agenten
});
