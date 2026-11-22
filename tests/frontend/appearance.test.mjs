import { test } from 'node:test';
import assert from 'node:assert/strict';
import { normalizeAppearance, compactAppearance, SURFACES, resetArea, isDefaultArea, withOverride, accentVars, contrast, ACCENTS, WALLPAPERS, appearanceVars, MAX_OVERRIDES, dockHeight } from '../../src/JulOS.Frontend/wwwroot/appearance.js';

/* Normalisieren, Zuruecksetzen und Kontrastlage der Akzentfarbe (docs/APPEARANCE.md). Kein Test fuer die Optik. */
const ID = '3f2b8a52-6d1e-4c9a-9a57-0c1c2a7e9d10';

test('normalisieren: Unbekanntes und Ungueltiges faellt weg, der Rest bekommt den Standard', () => {
  const d = normalizeAppearance(undefined);
  assert.deepEqual(d, { accent: null, bg: 'scene', dock: { mode: 'auto', size: 'm' }, icons: { size: 'm', style: 'outline', over: {} } });
  for (const bad of [null, 5, 'x', [1], { accent: 'red' }, { accent: '#fff' }, { bg: 'neon', dock: 'x', icons: [] }, { dock: { mode: 'weg', size: 'xl' }, icons: { size: 9, style: 'neon' } }]) assert.deepEqual(normalizeAppearance(bad), d, JSON.stringify(bad));
  const n = normalizeAppearance({ accent: '#2F6FE0', bg: 'ocean', evil: 1, dock: { mode: 'always', size: 'l', x: 1 }, icons: { size: 's', style: 'filled', over: { [ID]: { k: 'database', c: 'red', z: 1 }, 'a b': { k: 'bolt' }, [ID + 'x']: { k: 'nix' } } } });
  assert.equal(n.accent, '#2f6fe0'); assert.equal(n.bg, 'ocean'); assert.deepEqual(n.dock, { mode: 'always', size: 'l' });
  assert.deepEqual(n.icons, { size: 's', style: 'filled', over: { [ID]: { k: 'database', c: 'red' } } });
  assert.equal('evil' in n, false);
});

test('Ueberschreibungen sind begrenzt', () => {
  const over = {}; for (let i = 0; i < MAX_OVERRIDES + 50; i++) over['c' + i] = { k: 'bolt' };
  assert.equal(Object.keys(normalizeAppearance({ icons: { over } }).icons.over).length, MAX_OVERRIDES);
});

test('gespeichert wird nur, was vom Standard abweicht', () => {
  assert.equal(compactAppearance(null), null);
  assert.equal(compactAppearance({ bg: 'scene', dock: { mode: 'auto' } }), null);
  assert.deepEqual(compactAppearance({ accent: '#2f6fe0', dock: { mode: 'always', size: 'm' }, icons: { style: 'filled' } }), { accent: '#2f6fe0', dock: { mode: 'always' }, icons: { style: 'filled' } });
  const a = normalizeAppearance({ accent: '#2f6fe0', bg: 'dots' });
  assert.deepEqual(normalizeAppearance(compactAppearance(a)), a);       // hin und zurueck
});

test('Zuruecksetzen je Bereich und insgesamt; Symbole je Verbindung nur auf eigenen Wunsch', () => {
  const a = withOverride({ accent: '#2f6fe0', bg: 'dots', dock: { mode: 'always', size: 'l' }, icons: { size: 's', style: 'filled' } }, ID, { k: 'bolt' });
  assert.equal(resetArea(a, 'accent').accent, null); assert.equal(resetArea(a, 'accent').bg, 'dots');
  assert.equal(resetArea(a, 'bg').bg, 'scene');
  assert.deepEqual(resetArea(a, 'dock').dock, { mode: 'auto', size: 'm' });
  const i = resetArea(a, 'icons'); assert.deepEqual([i.icons.size, i.icons.style], ['m', 'outline']); assert.ok(i.icons.over[ID]);
  assert.deepEqual(resetArea(a, 'iconsOver').icons.over, {});
  assert.deepEqual(resetArea(a, 'all'), normalizeAppearance(null));
  assert.equal(isDefaultArea(a, 'bg'), false); assert.equal(isDefaultArea(resetArea(a, 'bg'), 'bg'), true); assert.equal(isDefaultArea(null, 'all'), true);
});

test('Symbol je Verbindung setzen und auf automatisch zuruecksetzen', () => {
  let a = withOverride(null, ID, { k: 'database', c: 'red' });
  assert.deepEqual(a.icons.over[ID], { k: 'database', c: 'red' });
  a = withOverride(a, ID, { c: 'gold' }); assert.deepEqual(a.icons.over[ID], { c: 'gold' });
  a = withOverride(a, ID, null); assert.deepEqual(a.icons.over, {});
  assert.deepEqual(withOverride(null, 'kaputt id', { k: 'bolt' }).icons.over, {});
});

// Die Kontrastlage der Akzentfarbe: gegen die ungunstigsten Flaechen (theme-css.test.mjs prueft dieselben Farben noch einmal gegen die echten Variablen des Stylesheets)
const EDGE = ['#ffff00', '#ffffff', '#000000', '#808080', '#00ffff', '#ff00ff', '#0000ff', '#ff0000', '#00ff00', '#f5d90a', '#101010'];
for (const dark of [false, true]) {
  test(`Akzentfarbe (${dark ? 'dunkel' : 'hell'}): Text 4,5:1, Symbole 3:1, Hauptknopf 4,5:1 fuer feste und eigene Farben`, () => {
    const surf = SURFACES[dark ? 'dark' : 'light'];
    const bad = [];
    for (const hex of [...ACCENTS.map((a) => a[1]), ...EDGE]) {
      const v = accentVars(hex, dark), at = (name, bgs, min) => bgs.forEach((b) => { const r = contrast(v[name], b); if (r < min) bad.push(`${hex} ${name} auf ${b}: ${r.toFixed(2)}`); });
      at('--dk-accent-text', surf, 4.5); at('--dk-accent-strong', surf, 4.5); at('--dk-accent', surf, 3);
      for (const p of ['--dk-pri-a', '--dk-pri-b']) { const r = contrast(v['--dk-pri-fg'], v[p]); if (r < 4.5) bad.push(`${hex} ${p}: ${r.toFixed(2)}`); }
      const r = contrast(v['--dk-on-primary-container'], v['--dk-primary-container']); if (r < 4.5) bad.push(`${hex} Container: ${r.toFixed(2)}`);
      const q = contrast(v['--dk-on-primary'], v['--dk-primary']); if (q < 4.5) bad.push(`${hex} on-primary: ${q.toFixed(2)}`);
    }
    assert.deepEqual(bad, []);
  });
}

test('Variablen: Groessen und Hintergrund; ohne Akzent keine Akzentvariablen', () => {
  const none = appearanceVars(null, false);
  assert.equal(none.vars['--ic-s'], '1'); assert.equal('--dk-accent' in none.vars, false); assert.equal('--dk-bg-image' in none.vars, false);
  assert.deepEqual(none.attrs, { 'data-bg': 'scene', 'data-icon-style': 'outline', 'data-dock-size': 'm' });
  const v = appearanceVars({ accent: '#2f6fe0', bg: 'dots', dock: { size: 'l' }, icons: { size: 's', style: 'filled' } }, true);
  assert.equal(v.vars['--ic-s'], '0.85'); assert.equal(v.vars['--dk-dock-s'], '1.2'); assert.match(v.vars['--dk-bg-image'], /radial-gradient/); assert.ok(v.vars['--dk-accent']);
  assert.equal(v.attrs['data-icon-style'], 'filled');
  assert.equal(dockHeight('m'), 96); assert.ok(dockHeight('l') > 96 && dockHeight('s') < 96);
  for (const w of WALLPAPERS.filter((x) => x.light)) for (const dark of [false, true]) assert.match(appearanceVars({ bg: w.id }, dark).vars['--dk-bg-image'], /gradient/);
});
