import { test } from 'node:test';
import assert from 'node:assert/strict';
import { normalizeTb, vmSize, stripRect, workRect, displayOffset, deriveTaskbar, stripToVm, stripBox } from '../../src/JulOS.Frontend/wwwroot/desktop-taskbar-geom.js';

const VM = { w: 1500, h: 940 };
const SIDES = [{ side: 'bottom', p: 40 }, { side: 'top', p: 40 }, { side: 'left', p: 48 }, { side: 'right', p: 48 }];

test('Streifen und Fensterflaeche decken das Bild ohne Luecke und ohne Ueberlappung, die VM bekommt die Leiste dazu', () => {
  for (const tb of SIDES) {
    const s = stripRect(VM, tb), w = workRect(VM, tb);
    const horiz = tb.side === 'bottom' || tb.side === 'top';
    assert.equal(s.w * s.h + w.w * w.h, VM.w * VM.h, tb.side);
    assert.equal(horiz ? s.h : s.w, tb.p);
    assert.deepEqual(vmSize({ w: w.w, h: w.h }, tb), VM, 'Fenster plus Leiste ergibt die Groesse der VM');
  }
  assert.deepEqual(vmSize({ w: 800, h: 600 }, null), { w: 800, h: 600 });
  assert.deepEqual(displayOffset(VM, { side: 'top', p: 40 }), { x: 0, y: -40 });
  assert.deepEqual(displayOffset(VM, { side: 'left', p: 48 }), { x: -48, y: 0 });
  assert.deepEqual(displayOffset(VM, { side: 'bottom', p: 40 }), { x: 0, y: 0 });
});

test('Markieren: Seite und Groesse aus dem gezogenen Rechteck, in jeder Zugrichtung', () => {
  const d = (a, b) => deriveTaskbar(a, b, VM);
  assert.deepEqual(d({ x: 0, y: 900 }, { x: 1500, y: 940 }), { side: 'bottom', p: 40 });
  assert.deepEqual(d({ x: 1499, y: 939 }, { x: 3, y: 898 }), { side: 'bottom', p: 42 }, 'von rechts unten nach links oben, knapp neben dem Rand');
  assert.deepEqual(d({ x: 0, y: 0 }, { x: 1500, y: 28 }), { side: 'top', p: 28 });
  assert.deepEqual(d({ x: 0, y: 0 }, { x: 48, y: 940 }), { side: 'left', p: 48 });
  assert.deepEqual(d({ x: 1452, y: 5 }, { x: 1500, y: 935 }), { side: 'right', p: 48 });
});

test('Markieren: was keine Leiste sein kann, ergibt nichts', () => {
  const d = (a, b) => deriveTaskbar(a, b, VM);
  assert.equal(d({ x: 400, y: 400 }, { x: 700, y: 440 }), null, 'mitten im Bild');
  assert.equal(d({ x: 0, y: 900 }, { x: 500, y: 940 }), null, 'kurz: nur ein Teil des Randes');
  assert.equal(d({ x: 0, y: 300 }, { x: 1500, y: 940 }), null, 'mehr als die halbe Tiefe');
  assert.equal(d({ x: 0, y: 0 }, { x: 1500, y: 940 }), null, 'das ganze Bild');
  assert.equal(d({ x: 0, y: 935 }, { x: 1500, y: 940 }), null, 'unter MIN_P');
  assert.equal(d({ x: 10, y: 10 }, { x: 10, y: 10 }), null, 'ein Punkt');
});

test('gespeicherte Angaben werden geprueft', () => {
  assert.deepEqual(normalizeTb({ side: 'left', p: 47.6 }), { side: 'left', p: 48 });
  for (const bad of [null, 'x', {}, { side: 'middle', p: 40 }, { side: 'top', p: 2 }, { side: 'top', p: 4000 }, { side: 'top', p: NaN }]) assert.equal(normalizeTb(bad), null);
});

test('Zeiger im Streifen werden auf die Koordinaten der VM zurueckgerechnet (auch skaliert und beim Ziehen ueber den Rand)', () => {
  const bottom = { side: 'bottom', p: 40 };
  assert.deepEqual(stripToVm(VM, bottom, { x: 10, y: 5 }, { w: 1500, h: 40 }), { x: 10, y: 905 });
  assert.deepEqual(stripToVm(VM, bottom, { x: 750, y: 20 }, { w: 750, h: 20 }), { x: 1499, y: 939 }, 'halb gross gezeichnet: die letzte Ecke bleibt im Bild');
  assert.deepEqual(stripToVm(VM, bottom, { x: 375, y: 10 }, { w: 750, h: 20 }), { x: 750, y: 920 }, 'halb gross gezeichnet: Mitte');
  assert.deepEqual(stripToVm(VM, bottom, { x: 100, y: -300 }, { w: 1500, h: 40 }), { x: 100, y: 600 }, 'gezogen ueber den Streifen hinaus nach oben');
  assert.deepEqual(stripToVm(VM, bottom, { x: -50, y: 200 }, { w: 1500, h: 40 }), { x: 0, y: 939 }, 'nie ausserhalb des Bildes');
  assert.deepEqual(stripToVm(VM, { side: 'top', p: 30 }, { x: 5, y: 5 }, { w: 1500, h: 30 }), { x: 5, y: 5 });
  assert.deepEqual(stripToVm(VM, { side: 'right', p: 48 }, { x: 3, y: 100 }, { w: 48, h: 940 }), { x: 1455, y: 100 });
});

test('Streifen im Dock: im Massstab des Bildes, nur verkleinert wenn er nicht passt', () => {
  assert.deepEqual(stripBox(VM, { side: 'bottom', p: 40 }, 1, 1900), { w: 1500, h: 40 });
  assert.deepEqual(stripBox(VM, { side: 'bottom', p: 40 }, 1, 750), { w: 750, h: 20 });
  assert.deepEqual(stripBox(VM, { side: 'left', p: 48 }, 0.5, 2000), { w: 24, h: 470 });
});
