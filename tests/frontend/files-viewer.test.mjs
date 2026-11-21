// Reine Logik der Vorschau (files-viewer.js): Typ nach Endung, Grenzen wie am Server, Nachbarn, Zoom, Adressen. Keine Optik.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { viewKind, refusal, previewUrl, neighbours, stepZoom, fitZoom, clampZoom, pageAt, ZOOM_MAX, ZOOM_MIN } from '../../src/JulOS.Frontend/wwwroot/files-viewer.js';

const MiB = 1024 * 1024;

test('viewKind: feste Liste nach der Endung, nie SVG, HTML oder Unbekanntes', () => {
  for (const [n, k] of [['a.PNG', 'image'], ['a.jpeg', 'image'], ['a.webp', 'image'], ['v.mp4', 'video'], ['v.webm', 'video'], ['s.flac', 'audio'], ['s.m4a', 'audio'], ['d.PDF', 'pdf']]) assert.equal(viewKind(n), k, n);
  for (const n of ['a.svg', 'a.html', 'a.htm', 'a.xml', 'a.js', 'a.txt', 'a.mkv', 'a', 'a.', '.png.exe', '', null, undefined]) assert.equal(viewKind(n), null, String(n));
});

test('die Liste im Client ist die des Servers', () => {
  const cs = readFileSync(new URL('../../src/JulOS.Frontend/Files/FilePreview.cs', import.meta.url), 'utf8');
  const server = [...cs.matchAll(/\("(\w+)", PreviewKind\.(\w+), "[^"]+"\)/g)].map((m) => [m[1], m[2].toLowerCase()]);
  assert.ok(server.length >= 12);
  for (const [ext, kind] of server) assert.equal(viewKind('x.' + ext), kind, ext);
});

test('refusal: dieselben Regeln wie am Server', () => {
  const lim = { previewBytes: 50 * MiB, maxBytes: 200 * MiB };
  assert.equal(refusal('a.svg', 1, lim), 'preview_type');
  assert.equal(refusal('a.png', 50 * MiB, lim), null);
  assert.equal(refusal('a.png', 50 * MiB + 1, lim), 'preview_too_large');
  assert.equal(refusal('a.pdf', 60 * MiB, lim), 'preview_too_large');
  assert.equal(refusal('v.mp4', 150 * MiB, lim), null, 'Video steht nicht unter der Vorschau-Grenze');
  assert.equal(refusal('v.mp4', 201 * MiB, lim), 'preview_too_large');
  assert.equal(refusal('v.mp4', 1, { ...lim, root: true }), 'preview_root_type');
  assert.equal(refusal('s.mp3', 1, { ...lim, root: true }), 'preview_root_type');
  assert.equal(refusal('a.png', 1, { ...lim, root: true }), null);
  assert.equal(refusal('a.png', 10 * 1024 * MiB, {}), null, 'ohne bekannte Grenze entscheidet der Server');
  assert.equal(refusal('a.png', 80 * MiB, { previewBytes: 100 * MiB, maxBytes: 70 * MiB }), 'preview_too_large', 'die harte Grenze gilt auch fuer Bilder');
});

test('previewUrl: Ordner, Host und Root mit Token, Pfad wird maskiert', () => {
  assert.equal(previewUrl({ kind: 'local' }, 'a b/ü.png'), '/api/files/preview?path=a%20b%2F%C3%BC.png');
  assert.equal(previewUrl({ kind: 'ssh', id: 'c1' }, '/x/y#1.png'), '/api/files/remote/preview?c=c1&path=%2Fx%2Fy%231.png');
  assert.equal(previewUrl({ kind: 'root', id: 'g1' }, '/x.pdf', 'tok&1'), '/api/files/remote/preview?c=g1&path=%2Fx.pdf&token=tok%261');
});

test('neighbours: nur Bilder, in der Reihenfolge der Liste, kein Umlauf', () => {
  const f = (name) => ({ name, path: '/d/' + name, kind: 'file' });
  const list = [f('a.png'), { name: 'sub', path: '/d/sub', kind: 'directory' }, f('b.txt'), f('c.JPG'), f('d.mp4'), { name: 'e.png', path: '/d/e.png', kind: 'link', resolved: 'directory' }, f('g.gif')];
  const n = neighbours(list, '/d/c.JPG');
  assert.deepEqual(n.list.map((e) => e.name), ['a.png', 'c.JPG', 'g.gif']);
  assert.equal(n.index, 1); assert.equal(n.prev.name, 'a.png'); assert.equal(n.next.name, 'g.gif');
  assert.equal(neighbours(list, '/d/a.png').prev, null);
  assert.equal(neighbours(list, '/d/g.gif').next, null);
  assert.equal(neighbours(list, '/d/fehlt.png').index, -1);
  assert.deepEqual(neighbours(undefined, '/x'), { list: [], index: -1, prev: null, next: null });
});

test('Zoom: Schritte in den Grenzen, Einpassen vergroessert nie', () => {
  assert.equal(stepZoom(1, 1), 1.25);
  assert.equal(stepZoom(1.25, -1), 1);
  assert.equal(stepZoom(ZOOM_MAX, 1), ZOOM_MAX);
  assert.equal(stepZoom(ZOOM_MIN, -1), ZOOM_MIN);
  assert.equal(clampZoom(0), ZOOM_MIN);
  assert.equal(fitZoom(2000, 1000, 1000, 1000), 0.5);
  assert.equal(fitZoom(100, 100, 1000, 1000), 1);
  assert.equal(fitZoom(1000, 4000, 2000, 1000), 0.25);
  assert.equal(fitZoom(0, 10, 100, 100), 1);
  assert.equal(fitZoom(10, 10, 0, 0), 1);
});

test('pageAt: die Seite am oberen Rand', () => {
  const tops = [0, 800, 1600, 2400];
  assert.equal(pageAt(tops, 0), 0);
  assert.equal(pageAt(tops, 700), 0);
  assert.equal(pageAt(tops, 700, 100), 1);
  assert.equal(pageAt(tops, 2500), 3);
  assert.equal(pageAt([], 10), 0);
});
