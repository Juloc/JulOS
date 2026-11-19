import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SCHEMES, isScheme, normalizeScheme, isDark } from '../../src/JulOS.Frontend/wwwroot/scheme.js';

test('die drei Werte: Hell, Dunkel, System (auto) in dieser Reihenfolge', () => {
  assert.deepEqual(SCHEMES, [['light', 'Hell'], ['dark', 'Dunkel'], ['auto', 'System']]);
});

test('nur light, dark und auto sind gueltig; alles andere wird zu auto', () => {
  for (const v of ['light', 'dark', 'auto']) assert.equal(isScheme(v), true);
  for (const v of ['', 'Dark', 'system', null, undefined, 1, {}]) { assert.equal(isScheme(v), false); assert.equal(normalizeScheme(v), 'auto'); }
  assert.equal(normalizeScheme('dark'), 'dark');
});

test('dunkel: fest bei light und dark, bei auto nach dem Geraet', () => {
  assert.equal(isDark('dark', false), true);
  assert.equal(isDark('light', true), false);
  assert.equal(isDark('auto', true), true);
  assert.equal(isDark('auto', false), false);
  assert.equal(isDark('auto', undefined), false);
});
