// Robuste Host-Updates (hostactions.js, hostview.js, hostinfo.js): Bestaetigung mit Proxmox und "full-upgrade", Fehlerzusammenfassung, Reparieren, ZFS "nicht lesbar" statt Pool "?", SMART mit
// Temperatur und dem Hinweis auf das Wort disks, Kernel-Neustart. Reine Funktionen, kein Browser noetig.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { confirmHtml, confirmModel, confirmOptions, runAction, jobHtml, summaryHtml, errText, actionLabel, makeAct, canAct } from '../../src/JulOS.Frontend/wwwroot/hostactions.js';
import { healthIssues, zfsState, zfsLines } from '../../src/JulOS.Frontend/wwwroot/hostinfo.js';
import { healthCard, PANELS, attention } from '../../src/JulOS.Frontend/wwwroot/hostview.js';

setPref('de', null);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const host = (agent = {}) => ({ id: 'h1', name: 'pve', online: true, agent: { online: true, kind: 'host', version: '0.6.0', control: 'full', allow: ['updates', 'reboot', 'services', 'docker', 'logs'], caps: ['host', 'sysinfo', 'updates', 'services'], hostSupport: 'yes', ...agent } });
const conf = (over = {}) => ({ token: 'T', expiresIn: 60, action: 'updates.install', target: 'all', targetName: 'all', host: 'pve', typeToConfirm: 'pve-01', warnings: [], readOnly: false, ...over });

test('Bestaetigung "alle Updates" auf Proxmox: Hinweis, kein Haken, Entfernungen sichtbar', () => {
  const c = conf({ warnings: ['proxmox_full'], options: { proxmox: true, fullChoice: false, full: true, keptBack: ['proxmox-ve'], removals: 2 } });
  const h = confirmHtml(c, esc);
  assert.match(h, /Proxmox wird vollständig aktualisiert/);
  assert.match(h, /Laufende VMs laufen weiter/);
  assert.match(h, /Neustart nötig/);
  assert.doesNotMatch(h, /hostActFull/);                                                         // auf Proxmox gibt es keine Wahl
  assert.match(h, /id="hostActRemovals">/);                                                       // und die Entfernungen stehen offen da
  assert.match(h, /entfernt 2 Pakete/);
  assert.match(h, /hostActTyped/);                                                                // "alle Updates" bleibt mit getipptem Namen
});

test('Bestaetigung ohne Proxmox: "full-upgrade" ist eine ausdrueckliche Wahl, Standard aus, Entfernungen erst mit dem Haken', () => {
  const c = conf({ warnings: ['kept_back'], options: { proxmox: false, fullChoice: true, full: false, keptBack: ['linux-image-amd64', 'python3-held'], removals: 1 } });
  const h = confirmHtml(c, esc);
  assert.match(h, /<input type="checkbox" id="hostActFull">/);
  assert.doesNotMatch(h, /id="hostActFull" checked/);
  assert.match(h, /Auch Pakete mit neuen Abhängigkeiten installieren \(full-upgrade\)/);
  assert.match(h, /linux-image-amd64, python3-held/);
  assert.match(h, /id="hostActRemovals" hidden/);                                                 // erst der Haken zeigt, was entfernt wuerde
  assert.match(h, /entfernt 1 Paket\./);
  // ohne zurueckgehaltene Pakete keine Wahl
  assert.doesNotMatch(confirmHtml(conf({ options: { proxmox: false, fullChoice: false, full: false, keptBack: [], removals: 0 } }), esc), /hostActFull|hostActRemovals/);
  assert.equal(confirmOptions(conf()), null);
  assert.equal(confirmModel(c).options.fullChoice, true);
});

test('Ausfuehren: full geht nur mit ausdruecklichem true mit, sonst bleibt der Koerper wie frueher', async () => {
  const bodies = [];
  const api = async (path, o) => { bodies.push(o.body); return { job: 'J' }; };
  await runAction(api, 'h1', conf(), 'pve-01');
  await runAction(api, 'h1', conf(), 'pve-01', false);
  await runAction(api, 'h1', conf(), 'pve-01', true);
  assert.deepEqual(bodies[0], { token: 'T', action: 'updates.install', target: 'all', typed: 'pve-01' });
  assert.deepEqual(bodies[1], bodies[0]);
  assert.deepEqual(bodies[2], { ...bodies[0], full: true });
});

test('Reparieren: eigene Aktion mit Namen, Recht updates, nie rot, ohne Tippen', () => {
  assert.equal(actionLabel('updates.repair'), 'Pakete reparieren');
  assert.equal(canAct(host(), 'updates'), true);
  const act = makeAct(host(), false, esc);
  assert.match(act.btn('updates.repair', '', 'Reparieren'), /data-hact="updates.repair"[^>]*>/);
  assert.doesNotMatch(act.btn('updates.repair', '', 'Reparieren'), /danger/);
  assert.equal(makeAct(host({ allow: ['logs'] }), false, esc).btn('updates.repair', '', 'x'), '');
  const m = confirmModel(conf({ action: 'updates.repair', target: null, targetName: null, typeToConfirm: null }));
  assert.equal(m.typed, null);
  assert.equal(m.danger, false);
});

test('Fehlertexte fuer die neuen Codes sind uebersetzt und verstaendlich', () => {
  assert.match(errText('dpkg_failed'), /dpkg/);
  assert.match(errText('dpkg_broken'), /reparieren/i);
  assert.match(errText('proxmox_whole'), /Ganzes/);
  assert.match(errText('apt_locked'), /belegt/);
  assert.doesNotMatch(errText('rm -rf'), /rm -rf/);
});

test('Zusammenfassung: betroffene Pakete, E:-Zeilen, Sperre und Frage; leer bleibt leer', () => {
  assert.equal(summaryHtml({ summary: { failedPackages: [], keptBack: [], errors: [], locked: false, configQuestion: false } }, esc), '');
  assert.equal(summaryHtml({}, esc), '');
  const h = summaryHtml({ summary: { failedPackages: ['pve-manager'], keptBack: ['proxmox-ve'], errors: ['E: <b>x</b>'], locked: true, configQuestion: true } }, esc);
  assert.match(h, /Nicht fertig eingerichtet/);
  assert.match(h, /pve-manager/);
  assert.match(h, /Zurückgehalten/);
  assert.match(h, /E: &lt;b&gt;x&lt;\/b&gt;/);
  assert.match(h, /gesperrt/);
  assert.match(h, /Konfigurationsdatei/);
});

test('Fehlgeschlagen: Zusammenfassung oben, Angebot zum Reparieren nur mit opts.repair, das Protokoll darunter und nie offen', () => {
  const job = { action: 'updates.install', state: 'failed', error: 'dpkg_failed', result: { needsRepair: true, summary: { failedPackages: ['pve-manager'], keptBack: [], errors: ['E: Sub-process /usr/bin/dpkg returned an error code (1)'] },
    log: ['Errors were encountered while processing:', ' pve-manager', 'E: Sub-process /usr/bin/dpkg returned an error code (1)'] } };
  const h = jobHtml(job, esc, { repair: true });
  assert.ok(h.indexOf('Nicht fertig eingerichtet') < h.indexOf('<details'));
  assert.match(h, /data-task-act="repair"/);
  assert.match(h, /Protokoll \(3 Zeilen\)/);
  assert.match(h, /\n pve-manager/);                                                              // Einrueckung bleibt im Protokoll
  assert.doesNotMatch(jobHtml(job, esc), /data-task-act/);
  // alte Antworten ohne Zusammenfassung: wie frueher die letzten Zeilen
  assert.match(jobHtml({ action: 'updates.install', state: 'failed', error: 'apt_failed', result: { output: 'E: kaputt' } }, esc), /E: kaputt/);
});

test('ZFS: nicht lesbar oder keine Pools ist kein Problem und nie ein Pool "?"; ein echter Pool zeigt Name, Zustand, Fehler und Pruefung', () => {
  const info = (zfs) => ({ health: { smart: null, md: [], zfs } });
  assert.deepEqual(healthIssues(info({ state: 'unknown', healthy: false, pools: [], message: 'The ZFS modules are not loaded.' })), []);
  assert.deepEqual(healthIssues(info({ state: 'none', healthy: true, pools: [] })), []);
  assert.deepEqual(healthIssues(info({ healthy: false, pools: [] })), []);                         // aelterer Agent: nicht gesund ohne Pool heisst nicht lesbar, kein "?"
  const bad = { state: 'problem', healthy: false, pools: [{ name: 'tank', state: 'DEGRADED', errors: '3 data errors', scan: 'scrub repaired 0B' }] };
  assert.deepEqual(healthIssues(info(bad)), ['ZFS-Pool tank: DEGRADED']);
  assert.equal(zfsState({ healthy: true, pools: [] }), 'healthy');
  assert.equal(zfsState(null), null);
  assert.deepEqual(zfsLines(bad), ['tank: DEGRADED · Fehler: 3 data errors · Prüfung: scrub repaired 0B']);
  const card = (zfs, smart) => healthCard({ smart, md: [], zfs }, info(zfs), esc);
  const unread = card({ state: 'unknown', healthy: false, pools: [], message: 'The ZFS modules are not loaded.' }, { available: true, devices: [{ name: '/dev/sda', health: 'ok' }] });
  assert.match(unread, /ZFS/);
  assert.match(unread, /nicht lesbar: The ZFS modules are not loaded\./);
  assert.doesNotMatch(unread, /Problem bei den Platten|\?/);
  assert.match(unread, /Platten in Ordnung/);                                                       // SMART ist gut, ZFS nur nicht lesbar: kein falscher Alarm
  assert.doesNotMatch(card({ state: 'none', healthy: true, pools: [] }, { available: true, devices: [] }), /ZFS/);
  const prob = card(bad, { available: true, devices: [{ name: '/dev/sda', health: 'ok' }] });
  assert.match(prob, /Problem bei den Platten/);
  assert.match(prob, /tank: DEGRADED/);
});

test('SMART: Temperatur, der Hinweis auf das Wort disks und die neuen Gruende', () => {
  const card = (smart) => healthCard({ smart, md: [], zfs: null }, { health: { smart, md: [], zfs: null } }, esc);
  assert.match(card({ available: true, devices: [{ name: '/dev/sda', health: 'ok', temp: 34 }, { name: '/dev/sdb', health: 'ok' }] }), /\/dev\/sda: in Ordnung · 34 °C/);
  assert.match(card({ available: false, reason: 'no_access' }), /--allow disks/);
  assert.match(card({ available: false, reason: 'helper_unavailable' }), /Root-Helfer/);
  assert.match(card({ available: false, reason: 'read_failed' }), /konnte die Platten nicht abfragen/);
  assert.match(card({ available: false, reason: 'no_smartctl' }), /smartctl/);
});

test('Updates auf Proxmox: Hinweis, kein Knopf je Paket, Reparieren neben den anderen Tasten', () => {
  const upd = (over) => ({ state: 'ok', updates: { manager: 'apt', count: 2, security: 0, securityKnown: true, listsAgeSeconds: 60, keptBack: [], held: [], removals: 0, truncated: false, packages: [{ name: 'pve-manager', from: '1', to: '2' }, { name: 'bash', from: '1', to: '2' }], ...over } });
  const html = (over) => PANELS.find((p) => p.id === 'updates').html({ esc, host: host(), paused: false, data: { updates: upd(over) }, act: makeAct(host(), false, esc) });
  const px = html({ proxmox: true });
  assert.match(px, /Proxmox wird als Ganzes aktualisiert/);
  assert.doesNotMatch(px, /data-htarget="pve-manager"/);
  assert.match(px, /data-hact="updates.install" data-htarget="all"/);
  assert.match(px, /data-hact="updates.repair"/);
  const plain = html({ proxmox: false, keptBack: ['linux-image-amd64'] });
  assert.match(plain, /data-htarget="pve-manager"/);
  assert.doesNotMatch(plain, /Proxmox wird als Ganzes/);
  assert.match(plain, /linux-image-amd64/);
  assert.match(plain, /full-upgrade/);
});

test('Neustart nach neuem Kernel: Hinweis nennt die Version', () => {
  const data = { status: { info: { rebootRequired: true, newKernel: '6.17.2-1-pve', rebootPackages: [], disks: [], health: null } } };
  const a = attention(data, host()).find((x) => x.id === 'reboot');
  assert.match(a.detail, /Neuer Kernel installiert: 6\.17\.2-1-pve/);
});

test('Halb eingerichtete Pakete: Warnung mit Namen und Reparieren, Updates unbekannt statt "keine"; in der Uebersicht als Hinweis', () => {
  const upd = (over) => ({ state: 'ok', updates: { manager: 'apt', count: 0, security: 0, securityKnown: true, listsAgeSeconds: 60, keptBack: [], held: [], removals: 0, truncated: false, packages: [], ...over } });
  const html = (over) => PANELS.find((p) => p.id === 'updates').html({ esc, host: host(), paused: false, data: { updates: upd(over) }, act: makeAct(host(), false, esc) });
  const h = html({ broken: ['pve-manager', 'proxmox-mail-forward'], unreadable: true });
  assert.match(h, /2 Pakete sind halb eingerichtet/);
  assert.match(h, /pve-manager, proxmox-mail-forward/);
  assert.match(h, /data-hact="updates.repair"/);
  assert.doesNotMatch(h, /Updates: keine/);                                                     // unbekannt ist nicht "keine"
  assert.match(html({ broken: ['pve-manager'] }), /1 Paket ist halb eingerichtet/);
  assert.doesNotMatch(html({ broken: [] }), /halb eingerichtet/);
  const a = attention({ updates: upd({ broken: ['pve-manager'], unreadable: true }) }, host()).find((x) => x.id === 'broken');
  assert.equal(a.panel, 'updates');
  assert.equal(a.tone, 'bad');
});
