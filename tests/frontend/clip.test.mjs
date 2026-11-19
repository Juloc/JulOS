// Zwischenablage im Browser (clip.js): Ordner lesen, Grenzen pruefen, die Auswahl der VM als Zeilen, Fortschritt in Worten.
import test from 'node:test';
import assert from 'node:assert/strict';
import { fmtSize, filesFromTransfer, itemsFromFiles, planSend, topLevel, progressText, LIMITS } from '../../src/JulOS.Frontend/wwwroot/clip.js';

const file = (name, size = 3) => ({ name, size, type: 'text/plain' });
const fileEntry = (name, size = 3) => ({ name, isFile: true, isDirectory: false, file: (ok) => ok(file(name, size)) });
// readEntries liefert hoechstens 100 Eintraege auf einmal und dann eine leere Liste
const dirEntry = (name, children) => ({ name, isFile: false, isDirectory: true, createReader() { let left = [...children]; return { readEntries: (ok) => { const part = left.splice(0, 100); ok(part); } }; } });
const item = (entry, f = null) => ({ kind: 'file', webkitGetAsEntry: () => entry, getAsFile: () => f });

test('sizes are shown in B, kB, MB and GB', () => {
  assert.equal(fmtSize(12), '12 B');
  assert.equal(fmtSize(2048), '2 kB');
  assert.equal(fmtSize(5 * 1024 * 1024), '5,0 MB');
  assert.equal(fmtSize(3 * 1024 ** 3), '3,00 GB');
  assert.equal(fmtSize(-1), '');
});

test('the defaults are 200 files, 255 entries and 2 GiB', () => {
  assert.equal(LIMITS.files, 200); assert.equal(LIMITS.entries, 255); assert.equal(LIMITS.fileBytes, 2 * 1024 ** 3);
});

test('a dropped folder is read down to its files with relative paths, and an empty folder is kept as one', async () => {
  const tree = dirEntry('Projekt', [fileEntry('a.txt'), dirEntry('src', [fileEntry('main.c', 10), dirEntry('leer', [])]), fileEntry('b.txt')]);
  const dt = { items: [item(tree), item({ isFile: true, isDirectory: false, name: 'lose.txt' }, file('lose.txt'))] };
  const { items, skipped } = await filesFromTransfer(dt);
  assert.deepEqual(items.map((i) => i.path + (i.dir ? '/' : '')), ['Projekt/a.txt', 'Projekt/src/main.c', 'Projekt/src/leer/', 'Projekt/b.txt', 'lose.txt']);
  assert.equal(items[1].file.size, 10);
  assert.deepEqual(skipped, []);
});

test('a folder with more than 100 entries is read completely', async () => {
  const many = Array.from({ length: 250 }, (_, i) => fileEntry('f' + i));
  const { items } = await filesFromTransfer({ items: [item(dirEntry('viele', many))] });
  assert.equal(items.length, 250);
});

test('a drop without entries (old browsers) falls back to the files, and an unreadable folder is reported', async () => {
  assert.deepEqual((await filesFromTransfer({ files: [file('a.txt'), file('b.txt')] })).items.map((i) => i.path), ['a.txt', 'b.txt']);
  const broken = { name: 'kaputt', isFile: false, isDirectory: true, createReader() { return { readEntries: (ok, no) => no(new Error('x')) }; } };
  const { items, skipped } = await filesFromTransfer({ items: [item(broken)] });
  assert.deepEqual(items, []); assert.deepEqual(skipped, ['kaputt']);
  assert.deepEqual(await filesFromTransfer(null), { items: [], skipped: [] });
});

test('files from a folder picker keep their relative path', () => {
  const picked = [{ name: 'a.txt', size: 1, webkitRelativePath: 'Ordner/sub/a.txt' }, { name: 'b.txt', size: 1, webkitRelativePath: '' }];
  assert.deepEqual(itemsFromFiles(picked).map((i) => i.path), ['Ordner/sub/a.txt', 'b.txt']);
});

test('the plan accepts up to 200 files and refuses the 201st, too many entries, too big files and a doubled name', () => {
  const files = (n) => Array.from({ length: n }, (_, i) => ({ path: 'd/f' + i, file: file('f' + i) }));
  assert.equal(planSend(files(200)).ok, true);
  assert.match(planSend(files(201)).error, /Zu viele Dateien \(höchstens 200\)/);
  const dirs = Array.from({ length: 60 }, (_, i) => ({ path: 'leer' + i, dir: true }));
  assert.match(planSend([...files(200), ...dirs]).error, /Zu viele Einträge/);
  assert.match(planSend([{ path: 'a', file: file('a', 11) }], { ...LIMITS, fileBytes: 10 }).error, /Datei zu groß \(höchstens 10 B\)/);
  assert.match(planSend([{ path: 'a', file: file('a', 6) }, { path: 'b', file: file('b', 6) }], { ...LIMITS, fileBytes: 10 }).error, /zu groß/);   // zusammen
  assert.match(planSend([{ path: 'A/x', file: file('x') }, { path: 'a/X', file: file('x') }]).error, /Doppelter Name/);
  assert.match(planSend([{ path: 'a'.repeat(260), file: file('x') }]).error, /zu lang/);
  assert.equal(planSend([]).ok, false);
  assert.equal(planSend([{ path: 'leer.txt', file: file('leer.txt', 0) }]).error, 'Die Datei ist leer.');
  assert.equal(planSend([{ path: 'a/leer.txt', file: file('leer.txt', 0) }, { path: 'a/b.txt', file: file('b.txt') }]).ok, true);   // leer in einem Stapel ist erlaubt
});

test('the plan sends files first, then empty folders, and sums the bytes', () => {
  const plan = planSend([{ path: 'leer', dir: true }, { path: 'a.txt', file: file('a.txt', 5) }, { path: 'b.txt', file: file('b.txt', 7) }]);
  assert.deepEqual(plan.list.map((i) => [i.kind, i.path]), [[2, 'a.txt'], [2, 'b.txt'], [3, 'leer']]);
  assert.equal(plan.total, 12);
});

test('the selection of the VM becomes rows: files, and a folder with everything in it, also when only its files are named', () => {
  const rows = topLevel([
    { name: 'Ordner', size: 0, dir: true }, { name: 'Ordner/a.txt', size: 100 }, { name: 'Ordner/sub', size: 0, dir: true }, { name: 'Ordner/sub/b.bin', size: 50 },
    { name: 'Notiz.txt', size: 3 }, { name: 'Nur/Dateien.txt', size: 7 }, { name: 'Nur/Mehr.txt', size: -1 },
  ]);
  assert.deepEqual(rows, [
    { name: 'Ordner', path: 'Ordner', dir: true, size: 150, count: 2 },
    { name: 'Notiz.txt', path: 'Notiz.txt', dir: false, size: 3, count: 1 },
    { name: 'Nur', path: 'Nur', dir: true, size: -1, count: 2 },
  ]);
  assert.deepEqual(topLevel([]), []);
});

test('progress reads "done of total (percent)" and falls back to the amount alone', () => {
  assert.equal(progressText(5 * 1024 * 1024, 10 * 1024 * 1024), '5,0 MB von 10,0 MB (50 %)');
  assert.equal(progressText(1024, -1), '1 kB');
  assert.equal(progressText(2048, 1024), '2 kB von 1 kB (100 %)');
});
