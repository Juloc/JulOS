// Jede Datei in der Cache-Liste des Service Workers muss es geben: cache.addAll scheitert sonst als Ganzes,
// und die App startet danach nicht mehr offline (so geschehen, als ein Dialog durch eine Seite ersetzt wurde).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'JulOS.Frontend', 'wwwroot');
const sw = readFileSync(join(root, 'sw.js'), 'utf8');

test('alle Dateien der Cache-Liste existieren', () => {
  const list = /const SHELL = \[(.*?)\];/s.exec(sw);
  assert.ok(list, 'SHELL nicht gefunden');
  const paths = [...list[1].matchAll(/'([^']+)'/g)].map((m) => m[1]).filter((p) => p !== '/');
  assert.ok(paths.length > 30);
  const missing = paths.filter((p) => !existsSync(join(root, p)));
  assert.deepEqual(missing, []);
  assert.equal(new Set(paths).size, paths.length, 'doppelte Eintraege');
});

test('Version steht im Format pd-v<Zahl>', () => {
  assert.match(sw, /const VERSION = 'pd-v\d+';/);
});
