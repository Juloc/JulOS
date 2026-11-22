import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ZONES, zoneRect, zoneAt, snapStep, resizeRect, dragPosition, unsnapAt, nextWindow } from '../../src/JulOS.Frontend/wwwroot/desktop-snap.js';
import { DEFAULT_BINDINGS, resolveBindings, matchBinding, firesInTextField, moveKey, bindingLabel } from '../../src/JulOS.Frontend/wwwroot/desktop-keys.js';

const AREA = { x: 0, y: 34, w: 1501, h: 855 };     // ungerade Masse: die Haelften duerfen keine Luecke lassen
const key = (code, mods = [], extra = {}) => ({ code, ctrlKey: mods.includes('ctrl'), altKey: mods.includes('alt'), shiftKey: mods.includes('shift'), metaKey: mods.includes('meta'), ...extra });

test('Haelften und Viertel fuellen den Bereich ohne Luecke und ohne Ueberlappung', () => {
  const l = zoneRect('left', AREA), r = zoneRect('right', AREA);
  assert.equal(l.x, 0); assert.equal(l.x + l.w, r.x); assert.equal(r.x + r.w, AREA.x + AREA.w); assert.equal(l.h, AREA.h);
  const [tl, tr, bl, br] = ['tl', 'tr', 'bl', 'br'].map((z) => zoneRect(z, AREA));
  assert.equal(tl.y + tl.h, bl.y); assert.equal(bl.y + bl.h, AREA.y + AREA.h); assert.equal(tl.x + tl.w, tr.x); assert.equal(tr.x + tr.w, AREA.x + AREA.w);
  assert.deepEqual(zoneRect('max', AREA), AREA);
  assert.equal(zoneRect('nope', AREA), null);
  assert.deepEqual(ZONES.map((z) => zoneRect(z, AREA) !== null), ZONES.map(() => true));
});

test('Zone unter der Maus: Rand, Ecke, oben Mitte, unten nur Ecken', () => {
  const at = (x, y) => zoneAt({ x, y }, AREA);
  assert.equal(at(750, 400), null, 'mitten im Bild: nichts');
  assert.equal(at(2, 400), 'left'); assert.equal(at(1499, 400), 'right');
  assert.equal(at(750, 36), 'max', 'oben in der Mitte');
  assert.equal(at(750, 5), 'max', 'ueber der Menueleiste zaehlt als oberer Rand');
  assert.equal(at(3, 40), 'tl', 'oben links in der Ecke'); assert.equal(at(1498, 40), 'tr');
  assert.equal(at(3, 880), 'bl'); assert.equal(at(1498, 880), 'br');
  assert.equal(at(30, 36), 'tl', 'am oberen Rand nahe der Ecke ist ein Viertel'); assert.equal(at(1470, 36), 'tr');
  assert.equal(at(3, 60), 'tl', 'am linken Rand nahe der Ecke ist ein Viertel');
  assert.equal(at(750, 886), null, 'unten in der Mitte rastet nichts ein');
  assert.equal(at(200, 886), null);
  assert.equal(at(-20, 400), 'left', 'auch ausserhalb des Bildes');
});

test('Pfeile wie bei Windows: Haelften, Ecken, maximieren, wiederherstellen, minimieren', () => {
  assert.equal(snapStep(null, 'left'), 'left'); assert.equal(snapStep(null, 'right'), 'right');
  assert.equal(snapStep(null, 'up'), 'max'); assert.equal(snapStep(null, 'down'), 'minimize');
  assert.equal(snapStep('max', 'down'), 'restore'); assert.equal(snapStep('max', 'left'), 'left'); assert.equal(snapStep('max', 'up'), 'max');
  assert.equal(snapStep('left', 'up'), 'tl'); assert.equal(snapStep('left', 'down'), 'bl'); assert.equal(snapStep('left', 'right'), 'right');
  assert.equal(snapStep('tl', 'up'), 'max'); assert.equal(snapStep('tl', 'right'), 'tr'); assert.equal(snapStep('br', 'down'), 'restore');
  // Jeder Zustand kennt jede Richtung, und das Ergebnis ist immer ein bekannter Zustand
  for (const z of [null, 'max', 'left', 'right', 'tl', 'tr', 'bl', 'br']) for (const d of ['left', 'right', 'up', 'down']) {
    const n = snapStep(z, d);
    assert.ok(ZONES.includes(n) || n === 'restore' || n === 'minimize', `${z} ${d} -> ${n}`);
  }
  assert.equal(snapStep('left', 'sideways'), null);
});

test('Groesse mit der Tastatur: Mindestgroesse, Bereich, Rand schiebt zurueck', () => {
  const min = { w: 320, h: 220 }, area = { x: 0, y: 34, w: 1000, h: 700 };
  const r = { x: 100, y: 100, w: 500, h: 400 };
  assert.deepEqual(resizeRect(r, 'right', 32, min, area), { x: 100, y: 100, w: 532, h: 400 });
  assert.deepEqual(resizeRect(r, 'left', 32, min, area), { x: 100, y: 100, w: 468, h: 400 });
  assert.deepEqual(resizeRect(r, 'down', 32, min, area), { x: 100, y: 100, w: 500, h: 432 });
  assert.equal(resizeRect({ ...r, w: 330 }, 'left', 32, min, area).w, 320, 'nicht unter die Mindestbreite');
  assert.equal(resizeRect({ ...r, h: 230 }, 'up', 32, min, area).h, 220);
  const big = resizeRect({ x: 700, y: 100, w: 290, h: 400 }, 'right', 32, min, area);
  assert.equal(big.w, 322); assert.equal(big.x, 678, 'rutscht zurueck, ragt nicht heraus');
  assert.equal(resizeRect(r, 'right', 5000, min, area).w, 1000, 'nie groesser als der Bereich');
  assert.equal(resizeRect({ x: 0, y: 400, w: 500, h: 300 }, 'down', 200, min, area).y + 500, 734, 'unten ebenso');
});

test('Ziehen: ein Stueck der Titelleiste bleibt sichtbar, oben endet es an der Leiste', () => {
  const view = { w: 1500, h: 900 }, opt = { barH: 34 };
  assert.deepEqual(dragPosition({ x: 100, y: 100 }, { dx: 50, dy: 20 }, { w: 800 }, view, opt), { x: 150, y: 120 });
  assert.equal(dragPosition({ x: 100, y: 100 }, { dx: 5000, dy: 0 }, { w: 800 }, view, opt).x, 1380);
  assert.equal(dragPosition({ x: 100, y: 100 }, { dx: -5000, dy: 0 }, { w: 800 }, view, opt).x, 120 - 800);
  assert.equal(dragPosition({ x: 100, y: 100 }, { dx: 0, dy: -500 }, { w: 800 }, view, opt).y, 34);
  assert.equal(dragPosition({ x: 100, y: 100 }, { dx: 0, dy: 5000 }, { w: 800 }, view, opt).y, 840);
});

test('Ein eingerastetes Fenster zieht die alte Groesse mit, der Griff bleibt an derselben Stelle', () => {
  const cur = { x: 0, y: 34, w: 1000, h: 800 }, restore = { x: 300, y: 200, w: 600, h: 400 };
  assert.deepEqual(unsnapAt(restore, cur, 500), { x: 200, y: 34, w: 600, h: 400 }, 'Mitte bleibt Mitte');
  assert.equal(unsnapAt(restore, cur, 0).x, 0, 'ganz links bleibt links');
  assert.equal(unsnapAt(restore, cur, 1000).x, 400, 'ganz rechts bleibt rechts');
});

test('naechstes Fenster: reihum, mit und ohne aktives', () => {
  const ids = ['a', 'b', 'c'];
  assert.equal(nextWindow(ids, 'a', 1), 'b'); assert.equal(nextWindow(ids, 'c', 1), 'a');
  assert.equal(nextWindow(ids, 'a', -1), 'c'); assert.equal(nextWindow(ids, 'b', -1), 'a');
  assert.equal(nextWindow(ids, null, 1), 'a'); assert.equal(nextWindow(ids, 'zzz', -1), 'c');
  assert.equal(nextWindow(['a'], 'a', 1), 'a'); assert.equal(nextWindow([], 'a', 1), null);
});

/* ---------- Tastenbelegung ---------- */
test('die Vorgabe hat keine doppelte Taste und jede Belegung ist gueltig', () => {
  const seen = new Set();
  for (const b of DEFAULT_BINDINGS) {
    const k = b.code + '+' + [...b.mods].sort().join('+');
    assert.ok(!seen.has(k), 'doppelt: ' + k); seen.add(k);
    assert.ok(b.mods.includes('ctrl') && b.mods.includes('alt'), b.id + ': Strg+Alt als Ersatz fuer die Windows-Taste');
  }
  assert.equal(resolveBindings().length, DEFAULT_BINDINGS.length);
});

test('Strg+Alt+Pfeil und Strg+Alt+Umschalt+Pfeil sind verschieden, die Modifikatoren muessen genau stimmen', () => {
  const list = resolveBindings();
  assert.equal(matchBinding(list, key('ArrowLeft', ['ctrl', 'alt'])).id, 'snap.left');
  assert.equal(matchBinding(list, key('ArrowLeft', ['ctrl', 'alt', 'shift'])).id, 'size.left');
  assert.equal(matchBinding(list, key('ArrowDown', ['ctrl', 'alt'])).id, 'snap.down');
  assert.equal(matchBinding(list, key('KeyM', ['ctrl', 'alt'])).id, 'move');
  assert.equal(matchBinding(list, key('PageDown', ['ctrl', 'alt'])).id, 'next');
  assert.equal(matchBinding(list, key('ArrowLeft', ['alt'])), null, 'Alt+Pfeil gehoert dem Feld und der Dateien-App (Zurueck)');
  assert.equal(matchBinding(list, key('ArrowLeft', ['ctrl'])), null);
  assert.equal(matchBinding(list, key('ArrowLeft', ['ctrl', 'alt', 'meta'])), null);
  assert.equal(matchBinding(list, key('ArrowLeft')), null);
  assert.equal(matchBinding(list, key('ArrowLeft', ['ctrl', 'alt'], { altGraph: true })), null, 'AltGr schreibt Zeichen');
  assert.equal(matchBinding(list, { ctrlKey: true, altKey: true }), null);
});

test('eigene Belegung: Aenderung gilt, Ungueltiges und Doppeltes werden ueberhoert, null schaltet ab', () => {
  const list = resolveBindings({ 'snap.left': { code: 'KeyH', mods: ['meta', 'alt'] }, 'snap.right': { code: 'not valid!', mods: ['ctrl'] }, 'snap.up': { code: 'KeyZ', mods: ['hyper'] }, move: null });
  assert.equal(matchBinding(list, key('KeyH', ['meta', 'alt'])).id, 'snap.left');
  assert.equal(matchBinding(list, key('ArrowLeft', ['ctrl', 'alt'])), null, 'die alte Taste ist frei');
  assert.equal(matchBinding(list, key('ArrowRight', ['ctrl', 'alt'])).id, 'snap.right', 'ungueltig: Vorgabe bleibt');
  assert.equal(matchBinding(list, key('ArrowUp', ['ctrl', 'alt'])).id, 'snap.up');
  assert.equal(matchBinding(list, key('KeyM', ['ctrl', 'alt'])), null, 'abgeschaltet');
  // dieselbe Taste fuer zwei Dinge: der erste Eintrag behaelt sie
  const dup = resolveBindings({ 'size.left': { code: 'ArrowLeft', mods: ['ctrl', 'alt'] } });
  assert.equal(dup.filter((b) => b.code === 'ArrowLeft' && b.mods.length === 2).length, 1);
  assert.equal(matchBinding(dup, key('ArrowLeft', ['ctrl', 'alt'])).id, 'snap.left');
  assert.equal(resolveBindings(null).length, DEFAULT_BINDINGS.length);
});

test('in einem Textfeld greifen nur Tasten, die dort nichts tun', () => {
  const list = resolveBindings();
  const byId = (id) => list.find((b) => b.id === id);
  assert.equal(firesInTextField(byId('snap.left')), true, 'Strg+Alt+Pfeil');
  assert.equal(firesInTextField(byId('size.left')), true);
  assert.equal(firesInTextField(byId('next')), true);
  assert.equal(firesInTextField(byId('move')), false, 'M schreibt ein Zeichen (AltGr+M ist ein µ)');
  assert.equal(firesInTextField({ code: 'ArrowLeft', mods: ['alt'] }), false, 'Alt+Pfeil springt im Feld ueber Woerter');
  assert.equal(firesInTextField({ code: 'ArrowLeft', mods: ['shift'] }), false);
  assert.equal(firesInTextField({ code: 'ArrowLeft', mods: ['alt', 'shift'] }), false);
});

test('nur die Tasten zum Fensterwechsel gelten auch im Fernfenster', () => {
  assert.deepEqual(DEFAULT_BINDINGS.filter((b) => b.global).map((b) => b.id).sort(), ['next', 'prev']);
});

test('Verschiebe-Modus: Pfeile bewegen, Umschalt vergroebert, Enter und Esc beenden, Rest nichts', () => {
  assert.deepEqual(moveKey(key('ArrowLeft')), { dx: -1, dy: 0 });
  assert.deepEqual(moveKey(key('ArrowDown', ['shift'])), { dx: 0, dy: 4 });
  assert.equal(moveKey(key('Enter')), 'end'); assert.equal(moveKey(key('Escape')), 'end');
  assert.equal(moveKey(key('ArrowLeft', ['ctrl'])), null);
  assert.equal(moveKey(key('KeyA')), null);
});

test('Tastenkuerzel als Text fuer Menues', () => {
  const names = { ctrl: 'Strg', alt: 'Alt', shift: 'Umschalt' };
  const by = (id) => DEFAULT_BINDINGS.find((b) => b.id === id);
  assert.equal(bindingLabel(by('snap.left'), names), 'Strg+Alt+←');
  assert.equal(bindingLabel(by('size.down'), names), 'Strg+Alt+Umschalt+↓');
  assert.equal(bindingLabel(by('move'), names), 'Strg+Alt+M');
  assert.equal(bindingLabel(by('next'), names), 'Strg+Alt+PgDn');
});
