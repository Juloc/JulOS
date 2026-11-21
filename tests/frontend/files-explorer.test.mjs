// Explorer der Dateien-App (files-explorer.js, docs/FILES-EXPLORER.md): Auswahl, Buchstabensprung, Sortierung, Namen, Pins. Reine Logik, kein DOM.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as X from '../../src/JulOS.Frontend/wwwroot/files-explorer.js';

const RLO = String.fromCharCode(0x202e);
const f = (name, extra = {}) => ({ name, path: '/d/' + name, kind: 'file', size: 10, modified: '2026-01-01T10:00:00Z', openable: false, ...extra });
const d = (name, extra = {}) => ({ name, path: '/d/' + name, kind: 'directory', size: 0, modified: '2026-01-01T10:00:00Z', openable: true, ...extra });
const sel = (paths) => { const s = new X.ListSelection(); s.setPaths(paths); return s; };

test('Auswahl: Klick, Strg, Umschalt (Bereich vom Anker), Strg+A, Rechtsklick', () => {
  const s = sel(['a', 'b', 'c', 'd', 'e']);
  s.click('b'); assert.deepEqual(s.list(), ['b']);
  s.click('d', { ctrl: true }); assert.deepEqual(s.list(), ['b', 'd']);
  s.click('b', { ctrl: true }); assert.deepEqual(s.list(), ['d']);
  s.click('a'); s.click('c', { shift: true }); assert.deepEqual(s.list(), ['a', 'b', 'c']);
  s.click('e', { shift: true }); assert.deepEqual(s.list(), ['a', 'b', 'c', 'd', 'e']);         // der Anker bleibt a
  s.click('b'); s.click('d', { shift: true, ctrl: true }); s.click('e', { ctrl: true }); assert.deepEqual(s.list(), ['b', 'c', 'd', 'e']);
  s.context('c'); assert.deepEqual(s.list(), ['b', 'c', 'd', 'e'], 'Rechtsklick in der Auswahl behaelt sie');
  s.context('a'); assert.deepEqual(s.list(), ['a'], 'Rechtsklick daneben waehlt nur diesen');
  s.selectAll(); assert.equal(s.size, 5);
  s.clear(); assert.equal(s.size, 0);
});

test('Auswahl: Pfeile, Umschalt+Pfeil, Strg+Pfeil, Pos1/Ende; eine neue Liste verwirft Verschwundenes', () => {
  const s = sel(['a', 'b', 'c', 'd']);
  assert.equal(s.move(1), 'a');                                    // ohne Fokus: der erste
  assert.equal(s.move(1), 'b'); assert.deepEqual(s.list(), ['b']);
  s.move(1, { shift: true }); s.move(1, { shift: true }); assert.deepEqual(s.list(), ['b', 'c', 'd']);
  s.move(-1, { shift: true }); assert.deepEqual(s.list(), ['b', 'c']);
  assert.equal(s.move(-9), 'a'); assert.equal(s.move(9), 'd');       // bleibt in der Liste
  s.move('first'); s.move(1, { ctrl: true }); assert.deepEqual(s.list(), ['a'], 'Strg+Pfeil verschiebt nur den Fokus');
  assert.equal(s.focus, 'b'); s.toggleFocus(); assert.deepEqual(s.list(), ['a', 'b']);
  s.setPaths(['b', 'x']); assert.deepEqual(s.list(), ['b']); assert.equal(s.anchor, 'b'); assert.equal(s.focus, 'b');
  assert.equal(sel([]).move(1), null);
});

test('Rahmen: was er beruehrt, wird ausgewaehlt', () => {
  const rects = [0, 1, 2, 3].map((i) => ({ path: 'p' + i, top: i * 20, bottom: i * 20 + 19, left: 0, right: 100 }));
  assert.deepEqual(X.rectsInBand(rects, { top: 15, bottom: 45, left: 10, right: 20 }), ['p0', 'p1', 'p2']);
  assert.deepEqual(X.rectsInBand(rects, { top: 45, bottom: 15, left: 20, right: 10 }), ['p0', 'p1', 'p2'], 'auch von unten nach oben gezogen');
  assert.deepEqual(X.rectsInBand(rects, { top: 0, bottom: 5, left: 200, right: 210 }), []);
  const s = sel(['p0', 'p1', 'p2']); s.click('p0'); s.setFromBand(['p1', 'p2'], s.list()); assert.deepEqual(s.list(), ['p0', 'p1', 'p2']);
});

test('Buchstabensprung: erster Treffer, Puffer von einer Sekunde, Verfeinern, reihum, Gross/Klein egal', () => {
  const names = ['Alpha', 'Beta', 'Bravo', 'docs', 'Downloads', 'dune', 'x y'];
  const ta = X.createTypeAhead();
  assert.equal(ta.press('b', 0, names, -1), 1);                     // Beta
  assert.equal(ta.press('b', 2000, names, 1), 2, 'nach einer Sekunde ein neuer Anfang: der naechste mit b');
  assert.equal(ta.press('b', 4000, names, 2), 1, 'reihum');
  ta.reset();
  assert.equal(ta.press('d', 0, names, -1), 3);
  assert.equal(ta.press('o', 300, names, 3), 3);                    // "do": docs bleibt
  assert.equal(ta.press('w', 600, names, 3), 4);                    // "dow": Downloads
  assert.equal(ta.press('x', 900, names, 4), 6, 'ohne Treffer faellt es auf den neuen Buchstaben zurueck');
  ta.reset();
  assert.equal(ta.press('D', 0, names, -1), 3, 'Grossbuchstabe');
  assert.equal(ta.press('d', 100, names, 3), 4, 'derselbe Buchstabe schnell hintereinander geht reihum weiter');
  assert.equal(ta.press('d', 200, names, 4), 5);
  assert.equal(ta.press('q', 5000, names, 5), -1, 'kein Treffer');
  assert.equal(ta.press('Enter', 6000, names, 5), -1, 'keine Zeichentaste');
  ta.reset(); assert.equal(ta.press(' ', 0, names, -1), -1, 'ein Leerzeichen am Anfang ist die Leertaste, kein Buchstabe');
});

test('Sortierung: Ordner zuerst, natuerliche Zahlen, Spalten, Richtung, versteckte ausblenden', () => {
  const list = [f('b10.txt', { size: 5, modified: '2026-03-01T00:00:00Z' }), f('b9.txt', { size: 50, modified: '2026-02-01T00:00:00Z' }), d('Zeta'), d('alpha'), f('a.png', { size: 1 }), f('.hidden')];
  const names = (o) => X.sortEntries(list, o).map((e) => e.name);
  assert.deepEqual(names({ col: 'name', dir: 'asc' }), ['alpha', 'Zeta', '.hidden', 'a.png', 'b9.txt', 'b10.txt']);
  assert.deepEqual(names({ col: 'name', dir: 'desc' }).slice(0, 2), ['Zeta', 'alpha'], 'Ordner bleiben oben, auch absteigend');
  assert.deepEqual(names({ col: 'size', dir: 'desc' }).slice(2), ['b9.txt', '.hidden', 'b10.txt', 'a.png']);
  assert.deepEqual(names({ col: 'date', dir: 'asc' }).slice(-2), ['b9.txt', 'b10.txt']);
  assert.deepEqual(names({ col: 'type', dir: 'asc' }).slice(2), ['.hidden', 'a.png', 'b9.txt', 'b10.txt'], 'ohne Endung zuerst');
  assert.deepEqual(X.visibleEntries(list, { showHidden: false }).map((e) => e.name), ['alpha', 'Zeta', 'a.png', 'b9.txt', 'b10.txt']);
  assert.equal(list[0].name, 'b10.txt', 'die Eingabe bleibt unveraendert');
  assert.deepEqual(X.nextSort({ sortCol: 'name', sortDir: 'asc' }, 'name'), { sortCol: 'name', sortDir: 'desc' });
  assert.deepEqual(X.nextSort({ sortCol: 'name', sortDir: 'desc' }, 'size'), { sortCol: 'size', sortDir: 'asc' });
});

test('Namen: Endung immer, Typ, Editor nur fuer kleine Textdateien; Steuer- und Richtungszeichen werden sichtbar', () => {
  assert.deepEqual(X.splitName('a.tar.gz'), { base: 'a.tar', ext: 'gz' });
  assert.deepEqual(X.splitName('.bashrc'), { base: '.bashrc', ext: '' });
  assert.deepEqual(X.splitName('Makefile'), { base: 'Makefile', ext: '' });
  assert.deepEqual(X.splitName('end.'), { base: 'end.', ext: '' });
  assert.equal(X.typeKey(f('a.PNG')).ext, 'PNG'); assert.equal(X.typeKey(d('x')).key, 'files.type.dir'); assert.equal(X.typeKey(f('Makefile')).key, 'files.type.file');
  assert.equal(X.kindOf(f('a.png')), 'image'); assert.equal(X.kindOf(f('a.sh')), 'code'); assert.equal(X.kindOf({ ...f('l'), kind: 'link', resolved: 'directory' }), 'dir');
  assert.ok(X.editable(f('notes.txt'))); assert.ok(X.editable(f('Makefile'))); assert.ok(X.editable(f('a.svg')));
  assert.ok(!X.editable(f('a.png'))); assert.ok(!X.editable(f('big.txt', { size: X.EDIT_MAX_BYTES + 1 }))); assert.ok(X.editable(f('ok.txt', { size: X.EDIT_MAX_BYTES })));
  assert.ok(!X.editable(d('dir'))); assert.ok(!X.editable({ ...f('l'), kind: 'link', resolved: 'directory' })); assert.ok(!X.editable({ ...f('p'), kind: 'other' }));
  assert.equal(X.safeName('gpj.' + RLO + 'txt'), 'gpj.<U+202E>txt');
  assert.equal(X.safeName('a\nb' + String.fromCharCode(0) + 'c' + String.fromCharCode(0x2066)), 'a<U+000A>b<U+0000>c<U+2066>');
  assert.equal(X.safeName('Ünï ✓ 日本'), 'Ünï ✓ 日本'); assert.equal(X.safeName(null), '');
  assert.ok(X.hasUnsafe('x' + RLO) && !X.hasUnsafe('x'));
  assert.ok(X.isHiddenName('.git') && !X.isHiddenName('a.b'));
});

test('Tastatur: dieselben Kurzbefehle wie im Explorer', () => {
  const k = (key, o = {}) => X.actionForKey({ key, ...o });
  assert.equal(k('a', { ctrlKey: true }), 'selectAll'); assert.equal(k('C', { ctrlKey: true }), 'copy'); assert.equal(k('x', { metaKey: true }), 'cut'); assert.equal(k('v', { ctrlKey: true }), 'paste');
  assert.equal(k('s', { ctrlKey: true }), 'save'); assert.equal(k('F2'), 'rename'); assert.equal(k('Delete'), 'delete'); assert.equal(k('F5'), 'refresh'); assert.equal(k('Escape'), 'clear');
  assert.equal(k('Backspace'), 'up'); assert.equal(k('ArrowLeft', { altKey: true }), 'back'); assert.equal(k('ArrowRight', { altKey: true }), 'forward'); assert.equal(k('ArrowUp', { altKey: true }), 'up');
  assert.equal(k('ArrowDown'), 'move'); assert.equal(k('ArrowDown', { ctrlKey: true }), 'open'); assert.equal(k('ArrowUp', { ctrlKey: true }), 'ctrlMove'); assert.equal(k('Enter'), 'open');
  assert.equal(k('l', { ctrlKey: true }), 'address'); assert.equal(k('n', { ctrlKey: true, shiftKey: true }), 'newFolder'); assert.equal(k('q', { ctrlKey: true }), null); assert.equal(k('a'), null);
});

test('Ansicht: Standard kompakte Liste mit Endungen und sichtbaren versteckten Dateien; Unbekanntes faellt auf den Standard', () => {
  const v = X.normalizeView(undefined);
  assert.deepEqual([v.mode, v.density, v.sortCol, v.sortDir, v.showHidden], ['list', 'compact', 'name', 'asc', true]);
  const w = X.normalizeView({ mode: 'x', density: 'comfy', sortCol: 'size', sortDir: 'up', showHidden: false, cols: { name: 5, date: 9999, type: 'a', size: 100.4 } });
  assert.deepEqual([w.mode, w.density, w.sortCol, w.sortDir, w.showHidden], ['list', 'comfy', 'size', 'asc', false]);
  assert.deepEqual(w.cols, { name: X.COL_MIN, date: X.COL_MAX, type: X.DEFAULT_COLS.type, size: 100 });
});

test('Pins: ohne Doppelte, begrenzt, Reihenfolge je Ort, zugeklappte Knoten', () => {
  const pins = X.normalizePins([{ place: 'c1', path: '/a', name: 'A' }, { place: 'c1', path: '/a' }, { place: 'c2', path: '/b' }, { place: 'c1', path: '/c' }, { path: '/x' }, null, { place: 'c1', path: 'x'.repeat(5000) }]);
  assert.deepEqual(pins.map((p) => p.path), ['/a', '/b', '/c']);
  assert.ok(X.isPinned(pins, 'c1', '/c') && !X.isPinned(pins, 'c2', '/c'));
  assert.equal(X.addPin(pins, { place: 'c1', path: '/a' }), pins);
  assert.deepEqual(X.removePin(pins, 'c1', '/a').map((p) => p.path), ['/b', '/c']);
  assert.deepEqual(X.movePin(pins, 'c1', '/c', -1).map((p) => p.path), ['/c', '/b', '/a'], 'nur unter den Pins desselben Orts');
  assert.equal(X.movePin(pins, 'c1', '/a', -1), pins);
  assert.deepEqual(X.reorderPin(pins, pins[2], pins[0]).map((p) => p.path), ['/c', '/a', '/b']);
  assert.equal(X.normalizePins(Array.from({ length: 150 }, (_, i) => ({ place: 'c', path: '/' + i }))).length, X.MAX_PINS);
  assert.deepEqual(X.toggleClosed(['a'], 'b'), ['a', 'b']); assert.deepEqual(X.toggleClosed(['a', 'b'], 'a'), ['b']);
  assert.deepEqual(X.normalizePrefs(null), { view: X.DEFAULT_VIEW, pins: [], closed: [] });
});

test('Zwischenablage, Ziehen, getippter Pfad, Auswahlgroesse', () => {
  const clip = X.makeClip('cut', 'c1', [f('a'), f('b')]);
  assert.deepEqual(clip, { mode: 'cut', place: 'c1', paths: ['/d/a', '/d/b'] }); assert.equal(X.makeClip('copy', 'c1', []), null);
  const parent = (p) => p.slice(0, p.lastIndexOf('/')) || '/';
  assert.ok(X.pasteIsNoop(clip, 'c1', '/d', parent)); assert.ok(!X.pasteIsNoop(clip, 'c1', '/e', parent)); assert.ok(!X.pasteIsNoop(clip, 'c2', '/d', parent)); assert.ok(!X.pasteIsNoop({ ...clip, mode: 'copy' }, 'c1', '/d', parent));
  assert.ok(X.dropIntoSelf(['/a/b'], '/a/b')); assert.ok(X.dropIntoSelf(['/a/b'], '/a/b/c')); assert.ok(!X.dropIntoSelf(['/a/b'], '/a/bc')); assert.ok(!X.dropIntoSelf(['/a/b'], '/a'));
  assert.equal(X.parseTypedPath(' /a/b/../c/ ', false), '/a/c'); assert.equal(X.parseTypedPath('', false), '/'); assert.equal(X.parseTypedPath('a\\b', true), 'a/b'); assert.equal(X.parseTypedPath('~/x', false), '~/x'); assert.equal(X.parseTypedPath('/../..', false), '/');
  assert.deepEqual(X.selectionStats([f('a', { size: 5 }), f('b', { size: 7 }), d('c')]), { count: 3, files: 2, dirs: 1, bytes: 12 });
});

test('Seitenleiste und Fenstergroesse: Breite begrenzt, Zustand nur true, Fenster 70 Prozent der Flaeche mit Mindestgroesse', () => {
  assert.deepEqual(X.normalizeSide(null), { w: 200, off: false });
  assert.deepEqual(X.normalizeSide({ w: 20, off: 'ja' }), { w: X.SIDE_MIN, off: false });
  assert.deepEqual(X.normalizeSide({ w: 9999, off: true }), { w: X.SIDE_MAX, off: true });
  assert.deepEqual(X.normalizeSide({ w: 'x' }), { w: 200, off: false });
  assert.deepEqual(X.filesWindowSize({ w: 1920, h: 1000 }), { w: 1344, h: 700 });
  assert.deepEqual(X.filesWindowSize({ w: 1200, h: 700 }), { w: 860, h: 540 });                      // 70 % waeren kleiner als das Minimum
  assert.deepEqual(X.filesWindowSize({ w: 800, h: 480 }), { w: 800, h: 480 });                       // nie groesser als die Flaeche
});

test('Adressleiste: Brotkruemel fallen von links weg, der letzte bleibt; Vorschlaege aus dem Elternordner nach Anfang', () => {
  const w = [60, 50, 50, 50];                                           // Wurzel, a, b, c (mit Trenner)
  assert.equal(X.visibleCrumbCount(w, 500, 30), 4);                      // alles passt: keine "<"-Taste noetig
  assert.equal(X.visibleCrumbCount(w, 210, 30), 4);                      // 60+50+50+50 = 210 passt gerade
  assert.equal(X.visibleCrumbCount(w, 200, 30), 3);                      // eins zu wenig: der Anfang fällt weg, dafür steht die Taste
  assert.equal(X.visibleCrumbCount(w, 180, 30), 3);
  assert.equal(X.visibleCrumbCount(w, 130, 30), 2);
  assert.equal(X.visibleCrumbCount(w, 110, 30), 1);
  assert.equal(X.visibleCrumbCount(w, 20, 30), 1);                       // der aktuelle Ordner bleibt immer, auch wenn er nicht passt
  assert.equal(X.visibleCrumbCount([80], 10, 30), 1);
  assert.deepEqual(X.splitTyped('/a/b/c', false), { dir: '/a/b', prefix: 'c' });
  assert.deepEqual(X.splitTyped('/a/b/', false), { dir: '/a/b', prefix: '' });
  assert.deepEqual(X.splitTyped('/', false), { dir: '/', prefix: '' });
  assert.deepEqual(X.splitTyped('/a/bc', true), { dir: 'a', prefix: 'bc' });
  assert.deepEqual(X.splitTyped('/Do', true), { dir: '', prefix: 'Do' });
  assert.equal(X.splitTyped('~/x', false), null);
  const list = [d('Documents'), d('docs'), f('do.txt'), d('.dotdir'), d('Downloads'), d('other'), { ...d('lnk'), kind: 'link', resolved: 'directory' }];
  assert.deepEqual(X.filterSuggestions(list, 'do'), ['docs', 'Documents', 'Downloads']);   // nur Ordner, nach Anfang, ohne Punkt-Ordner
  assert.deepEqual(X.filterSuggestions(list, '.'), ['.dotdir']);
  assert.deepEqual(X.filterSuggestions(list, '', 2), ['docs', 'Documents']);
  assert.deepEqual(X.filterSuggestions(list, 'zzz'), []);
});
