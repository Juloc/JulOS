// Host-Oberflaeche (hostview.js, hostui.js): Karte der Hostliste und Abschnitte des Dialogs "Host-Details". Reine Funktionen, kein Browser noetig.
// Geprueft werden die Struktur, das Escaping aller Werte vom Agenten, die Gruppierung der Netzwerk-Schnittstellen und die drei Korrekturen (Hinweis auf apt update, Logs-Auswahl, Ausgabe mit Umbruch).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { cardModel, hostCardHtml, meters, attention, interfaces, healthCard, panelsFor, PANELS, clampLines, LOG_LINES } from '../../src/JulOS.Frontend/wwwroot/hostview.js';
import { makeAct } from '../../src/JulOS.Frontend/wwwroot/hostactions.js';
import { bar, meter, listBlock, icon, ICON_NAMES, empty } from '../../src/JulOS.Frontend/wwwroot/hostui.js';

setPref('de', null);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const GB = 1e9;
const EVIL = '<img src=x onerror=alert(1)>';
const info = (over = {}) => ({
  hostname: 'srv', os: 'Debian GNU/Linux 13', arch: 'x86_64', kernel: '6.12', uptimeSeconds: 3 * 86400 + 4 * 3600, load: [0.5, 0.4, 0.3], cpuCount: 4,
  memory: { totalBytes: 8 * GB, availableBytes: 6 * GB, swapTotalBytes: 2 * GB, swapFreeBytes: 2 * GB },
  disks: [{ mount: '/', fs: 'ext4', totalBytes: 50 * GB, usedBytes: 10 * GB, freeBytes: 40 * GB, percent: 20 }, { mount: '/data', fs: 'xfs', totalBytes: 100 * GB, usedBytes: 95 * GB, freeBytes: 5 * GB, percent: 95 }],
  addresses: [], network: [], rebootRequired: false, rebootPackages: [], ...over,
});
const host = (over = {}, agent = {}) => ({ id: 'h1', name: 'Server', online: true, services: [], hostname: 'srv', os: 'Debian', agent: { online: true, kind: 'host', version: '0.6.0', control: 'full', allow: ['updates', 'reboot', 'services', 'docker', 'logs'], caps: ['sysinfo', 'updates', 'services', 'docker'], ...agent }, ...over });
const O = { esc, connection: () => null };
const panel = (id, ctx) => PANELS.find((p) => p.id === id).html({ esc, host: host(), paused: false, data: {}, ...ctx });
const A = (over, paused = false) => makeAct(host({}, over), paused, esc);

test('Karte: Art des Agenten, Kennzahlen, Hinweise nur wenn vorhanden', () => {
  const none = cardModel(host({ agent: undefined }), null);
  assert.equal(none.kind, 'none'); assert.deepEqual(none.meters, []); assert.deepEqual(none.flags, []);
  assert.equal(cardModel(host({}, { kind: 'desktop', caps: [] }), null).kind, 'desktop');
  const entry = { status: { info: info({ rebootRequired: true }) }, updates: { updates: { count: 3, security: 2, securityKnown: true } }, services: { services: { count: 1 } } };
  const m = cardModel(host(), entry);
  assert.equal(m.kind, 'host');
  assert.deepEqual(m.meters.map((x) => x.id), ['cpu', 'ram', 'disk']);
  assert.equal(m.meters.find((x) => x.id === 'ram').pct, 25);
  assert.equal(m.meters.find((x) => x.id === 'disk').sub.startsWith('/ ·'), true);          // die Platte "/" steht auf der Karte
  assert.deepEqual(m.flags.map((f) => f.id), ['updates', 'reboot', 'services']);
  assert.equal(m.flags[0].tone, 'bad');                                                        // Sicherheitsupdates hervorgehoben
  assert.match(m.flags[0].detail, /2 davon Sicherheit/);
  // Updates ohne Sicherheit: keine Warnfarbe; keine Updates: kein Hinweis
  assert.equal(cardModel(host(), { updates: { updates: { count: 1, security: 0, securityKnown: true } } }).flags[0].tone, '');
  assert.deepEqual(cardModel(host(), { updates: { updates: { count: 0 } } }).flags, []);
  assert.equal(cardModel(host({}, { updateAvailable: true }), null).flags[0].id, 'agent');
  assert.equal(cardModel(host({}, { outdated: true }), null).flags[0].tone, 'warn');
});

test('Kennzahlen: Last je Kern, Speicher ohne Zwischenspeicher, voelligste Platte nur wenn "/" fehlt', () => {
  const m = meters(info({ load: [6, 1, 1] }));
  assert.equal(m[0].pct, 150);                                                                  // 6 Aufgaben auf 4 Kernen
  assert.equal(m[0].warn, false);                                                               // erst ab 1,5 je Kern
  assert.equal(meters(info({ load: [7, 1, 1] }))[0].warn, true);
  assert.equal(m[1].pct, 25);
  assert.equal(meters(info({ disks: [{ mount: '/a', totalBytes: 1, usedBytes: 1, percent: 40 }, { mount: '/b', totalBytes: 1, usedBytes: 1, percent: 90 }] })).find((x) => x.id === 'disk').sub.startsWith('/b'), true);
  assert.deepEqual(meters(null), []);
  assert.deepEqual(meters({}), []);
});

test('Host-Karte: Markup mit Name, Zustand, Aktionen; Tasten tragen Attribute fuer Handy und Desktop', () => {
  const html = hostCardHtml(host(), { status: { info: info() } }, O);
  assert.match(html, /class="hu-card hu-host"/);
  assert.match(html, /<div class="hu-hn" role="heading" aria-level="3">Server<\/div>/);
  assert.match(html, /class="hu-state ok"/);
  assert.match(html, /data-act="hostinfo" data-host="h1" data-hostinfo="h1"/);               // Hauptaktion: Details
  assert.match(html, /data-hmenu aria-haspopup="menu" aria-expanded="false"/);                // Nebenaktionen im Menue
  assert.match(html, /role="menuitem"[^>]*data-hostagent="h1"/);
  assert.match(html, /role="menuitem"[^>]*data-hostedit="h1"/);
  assert.match(html, /role="meter"/);                                                          // Balken sind vorlesbar
  assert.doesNotMatch(html, /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}]/u);                        // nie Emojis als Symbole
  const off = hostCardHtml(host({ online: false }), null, O);
  assert.match(off, /hu-host off/); assert.match(off, /class="hu-state off"/);
});

test('Host-Karte ohne Agent: klare naechste Handlung statt leerer Zeilen; Desktop-Agent und getrennter Agent erklaeren sich', () => {
  const none = hostCardHtml(host({ agent: undefined }), null, O);
  assert.match(none, /Noch kein Agent/);
  assert.match(none, /class="hu-btn filled"[^>]*data-hostagent="h1"/);                       // Agent einrichten
  assert.doesNotMatch(none, /hostinfo=/);                                                      // nichts zu lesen: keine Details-Taste
  assert.match(hostCardHtml(host({}, { kind: 'desktop', caps: [] }), null, O), /läuft in einer Desktop-Sitzung/);
  assert.match(hostCardHtml(host({}, { online: false, lastSeen: '2026-01-02T10:00:00Z' }), null, O), /nicht verbunden/);
  const dis = hostCardHtml(host({}, { online: false }), null, O);
  assert.match(dis, /data-hostagent="h1"/);                                                    // Hauptaktion dann: Agent verwalten
  assert.doesNotMatch(dis, /data-hostinfo/);
});

test('Host-Karte: Verbindungen als Chips, alle Werte vom Agenten und Nutzer geschuetzt', () => {
  const h = host({ name: EVIL, hostname: EVIL, os: EVIL, services: [{ connection: 'c1', online: true }, { connection: 'c2', online: false }, { connection: 'weg' }] });
  const conns = { c1: { id: 'c1', name: EVIL, host: EVIL, port: 22 }, c2: { id: 'c2', name: 'b', host: 'x', port: 3389 } };
  const html = hostCardHtml(h, { status: { info: info({ hostname: EVIL }) }, updates: { updates: { count: 1, security: 0 } } }, { esc, connection: (id) => conns[id] });
  assert.doesNotMatch(html, /<img/); assert.match(html, /&lt;img/);
  assert.equal((html.match(/data-act="start"/g) || []).length, 2);                            // die Verbindung ohne Eintrag entfaellt
  assert.match(html, /data-conn="c1"/);
  assert.match(html, /hu-dot ok/);
});

test('Hinweise des Ueberblicks: Sicherheit zuerst, Neustart, Dienste, volle Platte, Gesundheit', () => {
  const data = { status: { info: info({ rebootRequired: true, rebootPackages: ['linux-image'], health: { smart: { available: true, devices: [{ name: '/dev/sda', health: 'failing' }] }, md: [] } }) },
    updates: { updates: { count: 2, security: 1, securityKnown: true } }, services: { services: { count: 2 } }, docker: { docker: { available: true, restarting: 1 } } };
  const a = attention(data, host());
  assert.deepEqual(a.map((x) => x.id), ['updates', 'reboot', 'services', 'docker', 'health', 'full']);
  assert.equal(a[0].tone, 'bad'); assert.equal(a[0].panel, 'updates');
  assert.equal(a.find((x) => x.id === 'full').panel, 'disks');
  assert.deepEqual(attention({ status: { info: info({ disks: [], memory: {} }) } }, host()), []);
  const html = panel('overview', { data });
  assert.match(html, /data-goto="updates"/); assert.match(html, /Braucht Aufmerksamkeit/);
  assert.match(panel('overview', { data: { status: { info: info({ disks: [] }) } } }), /Alles in Ordnung/);
  assert.match(panel('overview', { data: { status: { state: 'offline' } } }), /nicht verbunden/);
});

test('Auslastung und Datentraeger: Balken mit Prozent, Warnstufen, Gesundheit mit klarer Aussage', () => {
  const load = panel('load', { data: { status: { info: info() } } });
  assert.match(load, /Letzte Minute/); assert.match(load, /Letzte 15 Minuten/); assert.match(load, /Auslagerung/);
  assert.match(load, /ohne Zwischenspeicher/);
  assert.match(panel('load', { data: { status: { info: info({ memory: { totalBytes: 8 * GB, availableBytes: 8 * GB, swapTotalBytes: 0, swapFreeBytes: 0 } }) } } }), /Keine Auslagerung/);
  const disks = panel('disks', { data: { status: { info: info() } } });
  assert.match(disks, /aria-valuenow="95"/);
  assert.match(disks, /hu-bar bad/);                                                          // ab 90 Prozent rot
  assert.match(bar(85, esc), /hu-bar warn/);
  assert.match(bar(10, esc, 'x'), /class="hu-bar"/);
  assert.match(bar(500, esc), /aria-valuenow="100"/);                                          // nie ueber 100
  assert.match(healthCard({ smart: { available: true, devices: [{ name: 'sda', health: 'ok' }] }, md: [], zfs: null }, info(), esc), /Platten in Ordnung/);
  const bad = healthCard({ smart: { available: true, devices: [{ name: EVIL, health: 'failing' }] }, md: [] }, { health: { smart: { available: true, devices: [{ name: EVIL, health: 'failing' }] }, md: [] } }, esc);
  assert.match(bad, /Problem bei den Platten/); assert.doesNotMatch(bad, /<img/);
  assert.match(healthCard({ smart: { available: false, reason: 'no_smartctl' }, md: [] }, info(), esc), /nicht lesbar/);
  assert.match(healthCard({ pending: true }, null, esc), /Wird gelesen/);
});

test('Netzwerk: echte Schnittstellen zuerst, Docker- und Bridge-Schnittstellen eingeklappt, fe80 zusammengefasst, alles geschuetzt', () => {
  const i = info({
    addresses: [{ iface: 'br-aaaa', family: 4, address: '172.18.0.1', prefix: 16 }, { iface: 'docker0', family: 4, address: '172.17.0.1', prefix: 16 }, { iface: 'ens18', family: 4, address: '192.0.2.10', prefix: 24 },
      { iface: 'ens18', family: 6, address: 'fe80::1', prefix: 64 }, { iface: 'ens18', family: 6, address: '2001:db8::10', prefix: 64 }, { iface: EVIL, family: 4, address: EVIL, prefix: 8 }],
    network: [{ iface: 'ens18', rxBytes: 2 * GB, txBytes: GB }, { iface: 'veth123', rxBytes: 1, txBytes: 1 }],
  });
  const list = interfaces(i);
  assert.deepEqual(list.filter((e) => !e.virtual).map((e) => e.iface).sort(), [EVIL, 'ens18'].sort());
  assert.ok(list.findIndex((e) => e.virtual) > list.findLastIndex((e) => !e.virtual));                // echte vor virtuellen
  assert.deepEqual(list.filter((e) => e.virtual).map((e) => e.iface).sort(), ['br-aaaa', 'docker0', 'veth123']);
  const html = panel('network', { data: { status: { info: i } }, rate: { rx: 1000, tx: 500 } });
  assert.match(html, /<details class="hu-fold"/);
  assert.match(html, /3 Docker- und Bridge-Schnittstellen/);
  assert.ok(html.indexOf('ens18') < html.indexOf('<details'));                                  // die echte Schnittstelle steht vor dem Einklappbaren
  assert.ok(html.indexOf('docker0') > html.indexOf('<details'));
  assert.match(html, /192\.0\.2\.10\/24/); assert.match(html, /2001:db8::10/);
  assert.doesNotMatch(html, /fe80::1/);                                                         // lokale IPv6 nur als Zahl
  assert.match(html, /\+ 1 lokale IPv6/);
  assert.doesNotMatch(html, /<img/); assert.match(html, /&lt;img/);
  assert.match(html, /↓ 1,0 kB\/s|↓ 1 kB\/s|↓/);                                                // Rate, wenn bekannt
  assert.doesNotMatch(panel('network', { data: { status: { info: info({ addresses: [{ iface: 'ens18', family: 4, address: '192.0.2.1', prefix: 24 }] }) } } }), /<details/);   // ohne virtuelle: nichts einzuklappen
});

test('Updates: Sicherheit zuerst, Hinweis auf "sudo apt update" nur wenn der Knopf fehlt', () => {
  const updates = { state: 'ok', updates: { manager: 'apt', count: 3, security: 1, securityKnown: true, listsAgeSeconds: 3600, keptBack: [], held: [], removals: 0, truncated: false,
    packages: [{ name: 'aaa', from: '1', to: '2' }, { name: EVIL, from: EVIL, to: EVIL, security: true }, { name: 'zzz', to: '1', new: true }] } };
  const html = (act) => panel('updates', { data: { updates }, act });
  const on = html(A());
  assert.ok(on.indexOf('Sicherheitsupdates') < on.indexOf('Weitere Updates'));
  assert.ok(on.indexOf('&lt;img') < on.indexOf('>aaa<'));                                       // das Sicherheitspaket steht oben, obwohl es nicht das erste der Antwort ist
  assert.doesNotMatch(on, /<img/);
  assert.match(on, /data-hact="updates.refresh"/);
  assert.doesNotMatch(on, /sudo apt update/);                                                   // Knopf da: kein Hinweis auf die Handarbeit
  for (const act of [A({ control: 'read' }), A({ allow: ['reboot'] }), A({}, true), undefined]) {
    const off = html(act);
    assert.match(off, /sudo apt update/, 'Hinweis, wenn die Aktion nicht verfuegbar ist');
    assert.doesNotMatch(off, /data-hact="updates.refresh"/);
  }
  assert.match(html(A()), /Paketlisten zuletzt vor 60 min aktualisiert\./);
  assert.match(panel('updates', { data: { updates: { state: 'ok', updates: { count: 0, packages: [], securityKnown: true } } } }), /Updates: keine/);
});

test('Logs: die Auswahl kommt aus der Liste des Agenten, es gibt keine freie Eingabe, Zeilen 10 bis 200', () => {
  assert.deepEqual([LOG_LINES.min, LOG_LINES.max], [10, 200]);
  assert.equal(clampLines(5000), 200); assert.equal(clampLines(1), 10); assert.equal(clampLines('abc'), 50); assert.equal(clampLines('77.4'), 77); assert.equal(clampLines(undefined), 50);
  const units = { state: 'ok', units: { count: 2, truncated: false, units: [{ unit: 'nginx.service', active: 'active', sub: 'running' }, { unit: `${EVIL}.service`, active: 'failed', sub: 'failed' }] } };
  const html = panel('logs', { act: A(), units });
  assert.match(html, /<select id="hostLogUnit">/);
  assert.match(html, /<option value="nginx.service">nginx.service · active<\/option>/);
  assert.doesNotMatch(html, /<img/); assert.match(html, /&lt;img/);
  assert.doesNotMatch(html, /type="text"|<textarea/);                                           // keine freie Namenseingabe
  assert.match(html, /<input id="hostLogLines" type="number"[^>]*min="10" max="200"/);
  assert.match(html, /data-hlogs="1"/);
  assert.match(panel('logs', { act: A(), units: null }), /disabled/);                           // Liste noch nicht da
  assert.match(panel('logs', { act: A(), units: { error: 'control_off' } }), /nicht eingeschaltet/);
  // ohne das Wort services (oder Stufe "alles") gibt es die Liste nicht: die Grenze steht im Dialog, die Logs bleiben ueber die Eintraege erreichbar
  assert.match(panel('logs', { act: A({ allow: ['logs'] }) }), /Auswahl der Dienste ist hier nicht möglich/);
  assert.match(panel('logs', { act: A({ control: 'updates', allow: ['updates', 'logs', 'services'] }) }), /Auswahl der Dienste ist hier nicht möglich/);
  assert.match(panel('logs', { act: A({ allow: [] }) }), /Logs lesen ist nicht erlaubt/);
  assert.match(panel('logs', { act: A({}, true) }), /Logs lesen ist nicht erlaubt/);
});

test('Aktionen: Stufe, Pause, Erlaubnis; Neustart nur mit Erlaubnis', () => {
  const html = panel('actions', { host: host(), act: A() });
  assert.match(html, /id="hostActLevel"/); assert.match(html, /id="hostActPause"/);
  assert.match(html, /data-hact="power.reboot"/); assert.match(html, /data-hact="power.poweroff"/);
  const read = panel('actions', { host: host({}, { control: 'read', allow: [] }), act: A({ control: 'read', allow: [] }) });
  assert.doesNotMatch(read, /data-hact=/);
  assert.match(read, /--allow/);                                                                // Hinweis auf den Installer
});

test('Abschnitte je Host: nach Faehigkeit des Agenten, Logs/Aktionen/Verlauf nur beim Host-Agenten ab 0.6.0', () => {
  assert.deepEqual(panelsFor(host()).map((p) => p.id), ['overview', 'load', 'disks', 'network', 'updates', 'services', 'docker', 'logs', 'actions', 'history']);
  assert.deepEqual(panelsFor(host({}, { caps: ['updates'] })).map((p) => p.id), ['updates', 'logs', 'actions', 'history']);
  assert.deepEqual(panelsFor(host({}, { version: '0.5.0' })).map((p) => p.id), ['overview', 'load', 'disks', 'network', 'updates', 'services', 'docker']);
  assert.deepEqual(panelsFor(host({}, { kind: 'desktop', caps: [] })).map((p) => p.id), []);
  // jeder Abschnitt hat Namen und Symbol, jedes Symbol gibt es
  for (const p of PANELS) { assert.ok(p.title && p.html && ICON_NAMES.includes(p.icon), p.id); }
  // Zaehler an der Navigation: Updates mit Sicherheit rot, sonst gelb
  const upd = PANELS.find((p) => p.id === 'updates');
  assert.deepEqual(upd.badge({ updates: { updates: { count: 4, security: 1 } } }), { text: '4', tone: 'bad' });
  assert.deepEqual(upd.badge({ updates: { updates: { count: 4, security: 0 } } }), { text: '4', tone: 'warn' });
  assert.equal(upd.badge({}), null);
});

test('Dienste und Docker: Listen mit Zustand, alles geschuetzt, Gruende bei fehlendem Docker', () => {
  const svc = panel('services', { data: { services: { state: 'ok', services: { count: 1, failed: [{ unit: `${EVIL}.service`, description: EVIL }] } } } });
  assert.doesNotMatch(svc, /<img/); assert.match(svc, /1 Dienst fehlgeschlagen/);
  assert.match(panel('services', { data: { services: { state: 'ok', services: { count: 0, failed: [] } } } }), /Keine fehlgeschlagenen Dienste/);
  const dock = panel('docker', { data: { docker: { state: 'ok', docker: { available: true, running: 1, total: 2, restarting: 1, containers: [{ id: 'a', name: EVIL, image: EVIL, state: 'restarting', status: EVIL }], truncated: false } } } });
  assert.doesNotMatch(dock, /<img/); assert.match(dock, /hu-chip warn/);
  assert.match(panel('docker', { data: { docker: { state: 'ok', docker: { available: false, reason: 'no_access' } } } }), /Kein Zugriff auf Docker/);
  assert.match(panel('docker', { data: { docker: { state: 'ok', docker: { available: false, reason: 'no_socket' } } } }), /nicht vorhanden/);
});

test('Bausteine: Kennzahl, Liste und leerer Zustand schuetzen Text; Symbole ohne Emojis', () => {
  assert.doesNotMatch(meter({ label: EVIL, value: EVIL, pct: 50, sub: EVIL }, esc), /<img/);
  assert.doesNotMatch(listBlock([{ name: EVIL, detail: EVIL, badge: EVIL }, { name: 'b', badge: { text: EVIL, tone: 'warn' } }], esc), /<img/);
  assert.doesNotMatch(empty(EVIL, EVIL, esc, '', 'server'), /<img/);
  assert.equal(meter({ label: 'x', value: '1', pct: null }, esc).includes('hu-bar'), false);
  for (const n of ICON_NAMES) assert.match(icon(n), /^<svg class="hu-i" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="[^"]+"\/><\/svg>$/);
});

test('Dateien: Karte und Dialog sind im Service Worker, hostui.css wird geladen, Texte >= 13 px', () => {
  const www = new URL('../../src/JulOS.Frontend/wwwroot/', import.meta.url);
  const html = fs.readFileSync(new URL('index.html', www), 'utf8');
  assert.match(html, /<link rel="stylesheet" href="hostui.css">/);
  assert.match(html, /<nav id="hostInfoNav" class="hu-nav" role="tablist"/);
  const css = fs.readFileSync(new URL('hostui.css', www), 'utf8');
  const small = [...css.matchAll(/font-size:\s*(\d+(?:\.\d+)?)px/g)].map((m) => Number(m[1])).filter((n) => n < 13);
  assert.deepEqual([...new Set(small)], [12]);                                                  // nur die Zaehler der Navigation (12 px, fett)
  assert.ok(!/#[0-9a-fA-F]{3,8}\b/.test(css.replace(/light-dark\([^)]*\)/g, '')), 'keine festen Farben ausser in light-dark()');
});
