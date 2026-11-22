import { test } from 'node:test';
import assert from 'node:assert/strict';
import { iconFor, appIconFor, hostIconFor, osFamily, cleanOverride, GLYPHS, PICK_KEYS, COLORS, glyph } from '../../src/JulOS.Frontend/wwwroot/icons.js';

/* Zuordnung Symbol und Farbe (docs/ICONS.md): reine Funktion aus Verbindung, Host und Ueberschreibung. */
const term = (extra = {}) => ({ id: 'c1', kind: 'terminal', program: 'shell', ...extra });
const host = (os, extra = {}) => ({ id: 'h1', name: 'x', os, ...extra });

test('Verbindungsart und Programm bestimmen das Symbol', () => {
  assert.equal(iconFor({ conn: { kind: 'rdp' } }).key, 'monitor');
  assert.equal(iconFor({ conn: { kind: 'vnc' } }).key, 'vnc');
  assert.equal(iconFor({ conn: term() }).key, 'terminal');
  assert.equal(iconFor({ conn: term({ program: 'claude' }) }).key, 'spark');
  assert.equal(iconFor({ conn: term({ program: 'codex' }) }).key, 'code');
  assert.equal(iconFor({ conn: null }).key, 'monitor');
});

test('eigener Befehl: das erste Wort sagt, was es ist, sonst Blitz', () => {
  const c = (customCommand) => iconFor({ conn: term({ program: 'custom', customCommand }) }).key;
  assert.equal(c('docker compose logs -f'), 'containers');
  assert.equal(c('/usr/bin/htop'), 'activity');
  assert.equal(c('nvim notes.md'), 'edit');
  assert.equal(c('tmux attach'), 'grid');
  assert.equal(c('mein-skript --x'), 'bolt');
  assert.equal(c(''), 'bolt');
});

test('Betriebssystem des Hosts: nur wenn es etwas sagt', () => {
  assert.equal(osFamily(host('Debian GNU/Linux 13 (trixie)')), 'debian');
  assert.equal(osFamily(host('Ubuntu 24.04.1 LTS')), 'ubuntu');
  assert.equal(osFamily(host('Proxmox VE 8.2')), 'proxmox');
  assert.equal(osFamily(host('Home Assistant OS 13.2')), 'homeassistant');
  assert.equal(osFamily(host('Synology DSM 7.2')), 'synology');
  assert.equal(osFamily(host('Microsoft Windows 11 Pro')), 'windows');
  assert.equal(osFamily(host('', { agent: { platform: 'windows' } })), 'windows');
  assert.equal(osFamily(host('')), '');
  assert.equal(osFamily(null), '');
  // RDP auf Windows: Windows-Symbol; auf Linux oder unbekannt: Bildschirm
  assert.equal(iconFor({ conn: { kind: 'rdp' }, host: host('Windows 11') }).key, 'windows');
  assert.equal(iconFor({ conn: { kind: 'rdp' }, host: host('Debian GNU/Linux 13') }).key, 'monitor');
  // Shell: auf Debian bleibt es das Terminal (sagt nichts Neues), auf Proxmox, Home Assistant, Synology das Zeichen des Hosts
  assert.equal(iconFor({ conn: term(), host: host('Debian GNU/Linux 13') }).key, 'terminal');
  assert.equal(iconFor({ conn: term(), host: host('Proxmox VE 8.2') }).key, 'layers');
  assert.equal(iconFor({ conn: term(), host: host('Home Assistant OS') }).key, 'smarthome');
  assert.equal(iconFor({ conn: term(), host: host('Synology DSM 7') }).key, 'nas');
  // Programme behalten ihr Symbol, egal auf welchem Host
  assert.equal(iconFor({ conn: term({ program: 'claude' }), host: host('Proxmox VE 8.2') }).key, 'spark');
});

test('Symbol eines Hosts nach Betriebssystem, unbekannt ist ein Server', () => {
  assert.equal(hostIconFor(host('Debian GNU/Linux 13')).key, 'linux');
  assert.equal(hostIconFor(host('Windows 11')).key, 'windows');
  assert.equal(hostIconFor(host('Proxmox VE 8')).key, 'layers');
  assert.equal(hostIconFor(host('')).key, 'server');
  assert.equal(hostIconFor(undefined).key, 'server');
});

test('Ueberschreibung: Symbol und Farbe je fuer sich, Ungueltiges zaehlt nicht, ohne ist es automatisch', () => {
  const auto = iconFor({ conn: term() });
  assert.equal(auto.auto, true);
  const both = iconFor({ conn: term(), override: { k: 'database', c: 'red' } });
  assert.deepEqual([both.key, both.color, both.auto], ['database', 'red', false]);
  const onlyKey = iconFor({ conn: term(), override: { k: 'database' } });
  assert.deepEqual([onlyKey.key, onlyKey.color], ['database', auto.color]);
  const onlyColor = iconFor({ conn: term(), override: { c: 'gold' } });
  assert.deepEqual([onlyColor.key, onlyColor.color], [auto.key, 'gold']);
  for (const bad of [{ k: 'gibt-es-nicht' }, { c: 'neon' }, {}, null, 'x', { k: 5 }]) assert.equal(iconFor({ conn: term(), override: bad }).auto, true);
  assert.equal(cleanOverride({ k: 'database', c: 'neon', x: 1 }).c, undefined);
});

test('die Familie ist vollstaendig: jedes Symbol und jede Farbe, die vorkommen kann, gibt es', () => {
  for (const k of PICK_KEYS) assert.ok(GLYPHS[k], `Symbol ${k}`);
  const subjects = [{ conn: { kind: 'rdp' } }, { conn: { kind: 'vnc' } }, { conn: term() }, { conn: term({ program: 'claude' }) }, { conn: term({ program: 'codex' }) }, { conn: term({ program: 'custom', customCommand: 'x' }) }];
  for (const os of ['', 'Windows', 'Proxmox VE', 'Home Assistant OS', 'Synology DSM', 'Debian', 'Ubuntu', 'Fedora', 'Alpine', 'Arch', 'macOS', 'Raspbian']) {
    for (const s of subjects) { const r = iconFor({ ...s, host: host(os) }); assert.ok(GLYPHS[r.key], `${os} ${r.key}`); assert.ok(COLORS[r.color], r.color); assert.deepEqual(r.g, COLORS[r.color]); }
    const h = hostIconFor(host(os)); assert.ok(GLYPHS[h.key] && COLORS[h.color]);
  }
  for (const id of ['home', 'desktop', 'vnc', 'terminal', 'claude', 'codex', 'settings', 'files', 'hosts', 'docker', 'widgets', 'caddy', 'ddns', 'monitor', 'launcher', 'unbekannt']) assert.ok(GLYPHS[appIconFor(id).key], id);
});

test('Symbole zeichnen mit currentColor, ohne feste Farben', () => {
  for (const [k, v] of Object.entries(GLYPHS)) assert.doesNotMatch(v, /#[0-9a-fA-F]{3,8}|rgb|fill="|stroke="/, k);
  assert.match(glyph('monitor', 'dk-i'), /^<svg class="fi dk-i" viewBox="0 0 24 24"/);
  assert.equal(glyph('gibt-es-nicht').includes('<path'), false);
});

test('Kachelfarben: das weisse Symbol erreicht auf beiden Enden des Verlaufs 3:1', () => {
  const lum = (hex) => { const v = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((x) => (x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4)); return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2]; };
  const bad = [];
  for (const [name, ends] of Object.entries(COLORS)) for (const c of ends) { const r = 1.05 / (lum(c) + 0.05); if (r < 3) bad.push(`${name} ${c}: ${r.toFixed(2)}`); }
  assert.deepEqual(bad, []);
});

test('connectionIcon: Host aus der Liste, Ueberschreibung aus dem Aussehen', async () => {
  const { connectionIcon } = await import('../../src/JulOS.Frontend/wwwroot/icons.js');
  const c = term({ id: 'abc', hostId: 'h1' });
  assert.equal(connectionIcon(c, [host('Proxmox VE 8')], null).key, 'layers');
  assert.equal(connectionIcon(c, [host('Proxmox VE 8', { id: 'andere' })], null).key, 'terminal');
  const r = connectionIcon(c, [], { icons: { over: { abc: { k: 'database' } } } });
  assert.deepEqual([r.key, r.auto], ['database', false]);
  assert.equal(connectionIcon(c, undefined, { icons: { over: { anderer: { k: 'database' } } } }).auto, true);
  assert.equal(connectionIcon(null, [], null).key, 'monitor');
});
