import { test } from 'node:test';
import assert from 'node:assert/strict';
import { GROUPS, rowsFor, backupInfo, copyInfo, aboutValues } from '../../src/JulOS.Frontend/wwwroot/settings.js';
import { hasKey } from '../../src/JulOS.Frontend/wwwroot/i18n.js';

const CTL = ['button', 'choice', 'swatches', 'value', 'status', 'link', 'note'];

test('jede Gruppe hat Titel und eine Zeile Hilfe, jede Zeile Beschriftung, Erklaerung und genau ein Bedienelement (oder keins bei "note")', () => {
  const ids = new Set();
  for (const g of GROUPS) {
    assert.ok(hasKey(g.title) && hasKey(g.help), g.id);
    assert.ok(g.rows.length > 0, g.id);
    for (const r of g.rows) {
      assert.ok(!ids.has(r.id), 'doppelt: ' + r.id); ids.add(r.id);
      assert.ok(CTL.includes(r.ctl), r.id);
      assert.ok(hasKey(r.label) && hasKey(r.help), r.id);
      if (r.ctl === 'button' || r.ctl === 'link') assert.ok(hasKey(r.action), 'Taste ohne Beschriftung: ' + r.id);
      else assert.equal(r.action, undefined, r.id);
    }
  }
});

test('die Gruppen stehen in der festen Reihenfolge, Gefaehrliches steht allein am Ende', () => {
  assert.deepEqual(GROUPS.map((g) => g.id), ['account', 'security', 'appearance', 'workspace', 'connections', 'touch', 'programs', 'alerts', 'sync', 'backup', 'about', 'danger']);
  assert.deepEqual(GROUPS.filter((g) => g.danger).map((g) => g.id), ['danger']);
  assert.equal(GROUPS.at(-1).danger, true);
});

test('Handy und Desktop zeigen dieselben Gruppen; die Farbe nur am Handy, der Dock nur am Desktop', () => {
  const ids = (p) => GROUPS.flatMap((g) => rowsFor(g, p).map((r) => r.id));
  assert.ok(ids('phone').includes('color') && !ids('phone').includes('dock'));
  assert.ok(ids('desktop').includes('dock') && !ids('desktop').includes('color'));
  for (const g of GROUPS) { assert.ok(rowsFor(g, 'desktop').length > 0 || g.id === 'touch', g.id); assert.ok(rowsFor(g, 'phone').length > 0 || g.id === 'workspace', g.id); }   // die Fenster dieses Geraets gibt es nur am Desktop
});

test('Backup: ein kurzer Satz mit naechstem Schritt, der Rohtext nur in den Details', () => {
  const raw = 'In den Backup-Ordner /backup darf der Server nicht schreiben';
  const f = backupInfo({ state: 'failed', message: raw, time: '02:00' });
  assert.equal(f.tone, 'error');
  assert.ok(f.line.length < 60 && !f.line.includes('/backup'));
  assert.ok(f.next.includes('Rechte'));
  assert.equal(f.details, raw);
  assert.ok(backupInfo({ state: 'failed', message: 'Platte voll' }).next.includes('Platz'));
  assert.equal(backupInfo({ state: 'nodir', message: 'kein Ordner' }).tone, 'warn');
  assert.equal(backupInfo({ state: 'off' }).tone, 'info');
  assert.equal(backupInfo({ state: 'ok', lastAt: '2026-11-19T02:00:00Z', bytes: 3 * 1048576 }).tone, 'ok');
  assert.ok(backupInfo({ state: 'pending', time: '02:00' }).line.includes('02:00'));
  assert.equal(backupInfo(null).tone, 'info');
});

test('zweites Ziel: nur wenn eingerichtet; ein Fehler sagt, dass das lokale Backup in Ordnung ist', () => {
  assert.equal(copyInfo({ state: 'ok' }), null);
  assert.equal(copyInfo({ state: 'ok', copy: { state: 'off' } }), null);
  const c = copyInfo({ copy: { state: 'failed', message: 'Timeout', lastAt: '2026-11-18T02:00:00Z' } });
  assert.equal(c.tone, 'error'); assert.equal(c.details, 'Timeout'); assert.ok(c.next.includes('lokale Backup'));
  assert.equal(copyInfo({ copy: { state: 'ok', lastAt: '2026-11-18T02:00:00Z' } }).tone, 'ok');
});

test('Angaben des Servers: Version, Build, Bildübertragung als Text, ohne Fachwort "Stream"', () => {
  const v = aboutValues({ version: '0.10.3', commit: 'abc123', built: '2026-11-19T10:00:00Z', engines: { stream: true, encoder: 'H.264 auf der CPU' }, backup: { state: 'off' }, repository: 'https://example.invalid/repo' });
  assert.equal(v.version, 'JulOS 0.10.3');
  assert.ok(v.build.startsWith('abc123 · '));
  assert.ok(v.transfer.includes('H.264 auf der CPU') && !/stream/i.test(v.transfer));
  assert.equal(aboutValues({ version: '1', engines: { stream: false } }).transfer, 'Nicht verfügbar');
  assert.equal(aboutValues(null).version, '');
});
