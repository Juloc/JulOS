// Die Dateien unter wwwroot/vendor sind unveraendert aus dem npm-Paket kopiert: jede steht mit ihrer SHA-256 in vendor/README.md (Tabelle "| `pfad` | `hash` |").
// Eine geaenderte, fehlende oder nicht eingetragene Datei faellt hier auf.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative, sep } from 'node:path';

const vendor = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'JulOS.Frontend', 'wwwroot', 'vendor');
const readme = readFileSync(join(vendor, 'README.md'), 'utf8');
const listed = new Map([...readme.matchAll(/^\| `([^`]+)` \| `([0-9a-f]{64})` \|$/gm)].map((m) => [m[1], m[2]]));
const files = (dir) => readdirSync(dir).flatMap((n) => { const p = join(dir, n); return statSync(p).isDirectory() ? files(p) : [p]; });
const onDisk = files(vendor).map((p) => relative(vendor, p).split(sep).join('/')).filter((p) => p !== 'README.md');

test('jede Vendor-Datei steht in der Tabelle und stimmt mit ihrer Pruefsumme ueberein', () => {
  assert.ok(listed.size > 0, 'keine Tabelle gefunden');
  assert.deepEqual(onDisk.filter((p) => !listed.has(p)), [], 'Datei ohne Eintrag in vendor/README.md');
  assert.deepEqual([...listed.keys()].filter((p) => !onDisk.includes(p)), [], 'Eintrag ohne Datei');
  const wrong = onDisk.filter((p) => createHash('sha256').update(readFileSync(join(vendor, p))).digest('hex') !== listed.get(p));
  assert.deepEqual(wrong, [], 'Pruefsumme stimmt nicht');
});
