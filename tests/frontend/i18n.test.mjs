import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import de from '../../src/JulOS.Frontend/wwwroot/i18n/de.js';
import en from '../../src/JulOS.Frontend/wwwroot/i18n/en.js';
import { t, tp, tk, setPref, getLang, locale, resolveLang, normalizePref, fmtNumber, applyDom, hasKey } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { SERVER_TEXTS, SERVER_PATTERNS, NEXT_STEP, serverMsg } from '../../src/JulOS.Frontend/wwwroot/i18n-errors.js';

const WWW = new URL('../../src/JulOS.Frontend/wwwroot/', import.meta.url).pathname;
const sources = fs.readdirSync(WWW).filter((f) => /\.(js|html)$/.test(f) && f !== 'i18n.js' && f !== 'sw.js').map((f) => [f, fs.readFileSync(path.join(WWW, f), 'utf8')]);
const params = (s) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');

/** Alle Schluessel, die der Code benutzt: t('..'), tp('..') (braucht .one und .other), tk('..'), data-i18n*="..". */
function usedKeys() {
  const used = new Map();   // key -> Datei
  const add = (k, f) => { if (!used.has(k)) used.set(k, f); };
  for (const [f, src] of sources) {
    for (const m of src.matchAll(/(?<![\w.$])t\(\s*(['"`])([\w.]+)\1/g)) add(m[2], f);
    for (const m of src.matchAll(/(?<![\w.$])tk\(\s*(['"`])([\w.]+)\1/g)) add(m[2], f);
    for (const m of src.matchAll(/(?<![\w.$])tp\(\s*(['"`])([\w.]+)\1/g)) { add(m[2] + '.one', f); add(m[2] + '.other', f); }
    for (const m of src.matchAll(/["'](err\.[\w.]+)["']/g)) if (f === 'i18n-errors.js') add(m[1], f);
    for (const m of src.matchAll(/data-i18n(?:-[a-z-]+)?="([\w.]+)"/g)) add(m[1], f);
  }
  return used;
}

test('Deutsch und Englisch haben dieselben Schluessel und dieselben Parameter, keine leeren Texte', () => {
  const dk = Object.keys(de).sort(), ek = Object.keys(en).sort();
  assert.deepEqual(dk.filter((k) => !(k in en)), [], 'fehlt in en.js');
  assert.deepEqual(ek.filter((k) => !(k in de)), [], 'fehlt in de.js');
  for (const k of dk) {
    assert.ok(typeof de[k] === 'string' && de[k].length > 0, `de leer: ${k}`);
    assert.ok(typeof en[k] === 'string' && en[k].length > 0, `en leer: ${k}`);
    assert.equal(params(en[k]), params(de[k]), `Parameter unterschiedlich: ${k}`);
  }
});

test('jeder im Code benutzte Schluessel steht im Woerterbuch', () => {
  const missing = [...usedKeys()].filter(([k]) => !(k in de)).map(([k, f]) => `${k} (${f})`);
  assert.deepEqual(missing, []);
});

test('kein Schluessel im Woerterbuch bleibt unbenutzt', () => {
  const used = usedKeys();
  assert.deepEqual(Object.keys(de).filter((k) => !used.has(k)), []);
});

test('t: Parameter, Rueckfall auf Deutsch, nie ein roher Schluessel', () => {
  const warn = console.warn; console.warn = () => {};
  try {
    setPref('de', null);
    assert.equal(getLang(), 'de');
    assert.equal(t('common.save'), 'Speichern');
    assert.equal(t('gibt.es.nicht'), '');
    setPref('en', null);
    assert.equal(t('common.save'), 'Save');
    assert.equal(t('gibt.es.nicht'), '');
    assert.equal(locale(), 'en-GB');
    assert.equal(fmtNumber(1.5, 1), '1.5');
    assert.equal(tk('x.y'), 'x.y');
    assert.ok(hasKey('common.save'));
  } finally { console.warn = warn; setPref('de', null); }
  assert.equal(fmtNumber(1.5, 1), '1,5');
});

test('Rueckfall: fehlt ein englischer Schluessel, kommt der deutsche Text', async () => {
  // Zustand mit unvollstaendigem en-Woerterbuch nachstellen: Schluessel voruebergehend entfernen
  const saved = en['common.save'];
  delete en['common.save'];
  try { setPref('en', null); assert.equal(t('common.save'), de['common.save']); } finally { en['common.save'] = saved; setPref('de', null); }
});

test('Parameter und Mehrzahl', () => {
  const dk = { a: 'Hallo {name}, {n} mal' };
  Object.assign(de, { 'test.hello': dk.a, 'test.items.one': '{n} Eintrag', 'test.items.other': '{n} Eintraege' });
  try {
    setPref('de', null);
    assert.equal(t('test.hello', { name: 'Ada', n: 2 }), 'Hallo Ada, 2 mal');
    assert.equal(t('test.hello'), 'Hallo ,  mal');
    assert.equal(tp('test.items', 1), '1 Eintrag');
    assert.equal(tp('test.items', 3), '3 Eintraege');
  } finally { delete de['test.hello']; delete de['test.items.one']; delete de['test.items.other']; }
});

test('Auswahl: Deutsch ist der Standard, System folgt der Browsersprache', () => {
  assert.equal(normalizePref(undefined), 'de');
  assert.equal(normalizePref('fr'), 'de');
  assert.equal(normalizePref('system'), 'system');
  assert.equal(resolveLang('de', ['en-US']), 'de');
  assert.equal(resolveLang('en', ['de-DE']), 'en');
  assert.equal(resolveLang('system', ['en-US', 'de']), 'en');
  assert.equal(resolveLang('system', ['fr-FR', 'de']), 'de');
  assert.equal(resolveLang('system', ['fr-FR']), 'de');
  assert.equal(resolveLang('system', 'EN'), 'en');
  assert.equal(resolveLang('system', undefined), 'de');
});

test('setPref merkt die Wahl im Speicher', () => {
  const m = new Map();
  const storage = { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, v) };
  assert.equal(setPref('system', storage, ['en-GB']), 'en');
  assert.equal(m.get('pd.lang'), 'system');
  setPref('de', storage);
  assert.equal(m.get('pd.lang'), 'de');
});

test('applyDom setzt Text und Attribute, Kindelemente bleiben', () => {
  // Minimales DOM ohne Bibliothek
  const mk = (attrs, kids = [], text) => {
    const el = { attrs: { ...attrs }, children: kids.filter((k) => k.nodeType !== 3), childNodes: kids, textContent: text, nodeType: 1,
      getAttribute: (a) => (a in el.attrs ? el.attrs[a] : null), setAttribute: (a, v) => { el.attrs[a] = v; } };
    return el;
  };
  const input = mk({}), tn = { nodeType: 3, nodeValue: '\n  Speichern ' };
  const label = mk({ 'data-i18n': 'common.save' }, [tn, input]);
  const btn = mk({ 'data-i18n': 'common.close', 'data-i18n-title': 'common.cancel' }, [], 'Schließen');
  const root = { querySelectorAll: () => [label, btn] };
  setPref('en', null);
  try {
    applyDom(root);
    assert.equal(tn.nodeValue, '\n  Save ');
    assert.equal(label.children[0], input);
    assert.equal(btn.textContent, 'Close');
    assert.equal(btn.attrs.title, 'Cancel');
  } finally { setPref('de', null); }
});

test('Servermeldungen: bekannte Texte werden zugeordnet, unbekannte bleiben', () => {
  for (const [text, key] of Object.entries(SERVER_TEXTS)) assert.ok(key in de, `Schluessel fehlt fuer "${text}": ${key}`);
  for (const [, key] of SERVER_PATTERNS) assert.ok(key in de, `Schluessel fehlt fuer Muster: ${key}`);
  setPref('en', null);
  try {
    assert.equal(serverMsg('Unbekannter Text, den es nicht gibt.'), 'Unbekannter Text, den es nicht gibt.');
    assert.equal(serverMsg(undefined), undefined);
    for (const text of Object.keys(SERVER_TEXTS)) assert.notEqual(serverMsg(text), '', text);
  } finally { setPref('de', null); }
  assert.equal(serverMsg('Nicht gefunden.'), 'Nicht gefunden.');
});

test('alle festen Fehlertexte des Servers sind einem Schluessel zugeordnet', () => {
  // Alle deutschen Texte aus Error(<status>, "...") und aehnlichen festen Antworten in src/*.cs
  const root = new URL('../../src/', import.meta.url).pathname;
  const files = [];
  (function walk(d) { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p); else if (p.endsWith('.cs')) files.push(p); } })(root);
  const found = new Set();
  for (const f of files) {
    const src = fs.readFileSync(f, 'utf8');
    for (const m of src.matchAll(/\b(?:Error\(\s*(?:\d{3}|[\w.]+)|Reject\(ctx,\s*\d{3}|new\(false,\s*\d{3})\s*,\s*\$?"((?:[^"\\]|\\.)*)"/g)) found.add(m[1]);
  }
  const covered = (s) => s in SERVER_TEXTS || SERVER_PATTERNS.some(([re]) => re.test(s.replace(/\{[^}]*\}/g, 'X')) || re.test(s));
  const unmapped = [...found].filter((s) => /[a-zäöüß]{3}/i.test(s) && !covered(s));
  assert.deepEqual(unmapped, []);
});

test('der Service Worker haelt Modul und Woerterbuecher in der Huelle bereit', () => {
  const sw = fs.readFileSync(path.join(WWW, 'sw.js'), 'utf8');
  for (const f of ['/i18n.js', '/i18n-errors.js', '/i18n/de.js', '/i18n/en.js']) assert.ok(sw.includes(`'${f}'`), f);
});

test('Serverfehler mit naechstem Schritt: der Hinweis steht hinter dem Satz des Servers, in Deutsch und Englisch, die uebrigen bleiben unveraendert', () => {
  for (const [key, hint] of Object.entries(NEXT_STEP)) {
    assert.ok(Object.values(SERVER_TEXTS).includes(key), 'kein Servertext zu ' + key);
    assert.ok(hasKey(hint), hint);
  }
  const text = Object.keys(SERVER_TEXTS).find((k) => SERVER_TEXTS[k] === 'err.connectFailed');
  setPref('de', null);
  assert.equal(serverMsg(text), text + ' ' + t('next.connect'));
  setPref('en', null);
  try { assert.ok(serverMsg(text).endsWith(t('next.connect')) && serverMsg(text).startsWith(t('err.connectFailed'))); } finally { setPref('de', null); }
  assert.equal(serverMsg('Nicht gefunden.'), 'Nicht gefunden.');
});
