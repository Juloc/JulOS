import { test } from 'node:test';
import assert from 'node:assert/strict';
import { programOf, isAssistant, tileKind, withProgram, launchTarget, validWorkDir, validCustom, quickKeys, primaryKeys, noticeTexts, PROGRAMS } from '../../src/JulOS.Frontend/wwwroot/terminal-programs.js';
import { setPref, t } from '../../src/JulOS.Frontend/wwwroot/i18n.js';

const term = (id, program, extra = {}) => ({ id, name: id, kind: 'terminal', program, ...extra });

test('programOf: Standard ist die Shell, Desktops haben kein Programm, Unbekanntes faellt auf die Shell', () => {
  assert.equal(programOf(term('a')), 'shell');
  assert.equal(programOf(term('a', 'claude')), 'claude');
  assert.equal(programOf(term('a', 'codex')), 'codex');
  assert.equal(programOf(term('a', 'custom')), 'custom');
  assert.equal(programOf(term('a', 'rm -rf')), 'shell');
  assert.equal(programOf({ kind: 'desktop', program: 'claude' }), 'shell');
  assert.equal(programOf(null), 'shell');
  assert.deepEqual(PROGRAMS, ['shell', 'claude', 'codex', 'custom']);
  assert.equal(isAssistant(term('a', 'claude')), true);
  assert.equal(isAssistant(term('a', 'custom')), false);
});

test('tileKind: Claude und Codex bekommen ihr Symbol, andere Terminals das Terminal, Desktops den Desktop', () => {
  assert.equal(tileKind(term('a', 'claude')), 'claude');
  assert.equal(tileKind(term('a', 'codex')), 'codex');
  assert.equal(tileKind(term('a', 'custom')), 'terminal');
  assert.equal(tileKind(term('a')), 'terminal');
  assert.equal(tileKind({ kind: 'desktop' }), 'desktop');
  assert.equal(tileKind(undefined), 'desktop');
});

test('launchTarget: oeffnen, waehlen, aus einem Terminal anlegen, neu anlegen', () => {
  const desk = { id: 'd', kind: 'desktop' };
  assert.deepEqual(launchTarget([desk], 'claude'), { action: 'add' });
  const sh = term('sh'), sh2 = term('sh2', 'shell'), cl = term('cl', 'claude'), cl2 = term('cl2', 'claude'), cx = term('cx', 'codex');
  assert.deepEqual(launchTarget([desk, cl, sh], 'claude'), { action: 'open', conn: cl });
  assert.equal(launchTarget([cl, cl2, sh], 'claude').action, 'choose');
  assert.deepEqual(launchTarget([cl, cl2], 'claude').list, [cl, cl2]);
  const create = launchTarget([desk, cx, sh, sh2], 'claude');
  assert.equal(create.action, 'create');
  assert.equal(create.from, sh);                                   // bevorzugt eine gewoehnliche Shell-Verbindung
  assert.equal(launchTarget([cx], 'claude').from, cx);             // sonst irgendein Terminal
  assert.deepEqual(withProgram([desk, cl, cx, sh], 'codex'), [cx]);
});

test('validWorkDir: sichere Pfade ja, alles mit Sonderzeichen nein', () => {
  for (const ok of ['', '~', '~/', '~/projekte/app', '/srv/app', '/', 'projekte/app-1.2_x', 'a+b@c%d:e,f=g']) assert.equal(validWorkDir(ok), true, ok);
  for (const bad of ['-rf', '~root', '~/a b', 'a;b', 'a`b`', '$(id)', 'a$HOME', "a'b", 'a"b', 'a\nb', 'a&b', 'a|b', 'a>b', 'a*b', 'a\\b', '~/../..;x', 'x'.repeat(201), '/a b', '(a)']) assert.equal(validWorkDir(bad), false, JSON.stringify(bad));
});

test('validCustom: eine Zeile ohne Steuerzeichen', () => {
  assert.equal(validCustom('claude --continue'), true);
  assert.equal(validCustom("cd ~/x && codex 'a b'"), true);
  assert.equal(validCustom('a\nb'), false);
  assert.equal(validCustom('a\x1bb'), false);
  assert.equal(validCustom('x'.repeat(501)), false);
});

test('quickKeys: Voreinstellungen je Programm enthalten die geforderten Tasten', () => {
  const ids = (p) => quickKeys(p).map((k) => k.id);
  for (const p of ['claude', 'codex', 'shell', 'custom']) {
    for (const need of ['esc', 'tab', 'ctrlC', 'ctrlD', 'up', 'down', 'left', 'right', 'pgUp', 'pgDn']) assert.ok(ids(p).includes(need), `${p}: ${need}`);
  }
  assert.ok(ids('claude').includes('shiftTab'), 'Claude: Umschalt+Tab wechselt die Betriebsart');
  assert.ok(ids('claude').includes('enter') && ids('codex').includes('enter'));
  assert.ok(ids('claude').includes('slash') && ids('codex').includes('slash'));
  assert.deepEqual(ids('unbekannt'), ids('shell'));                // Unbekanntes: Shell-Voreinstellung
  for (const k of quickKeys('claude')) { assert.ok(k.label && k.aria, k.id); assert.ok(k.sym !== undefined || k.text !== undefined, k.id); }
  assert.equal(new Set(ids('claude')).size, ids('claude').length, 'keine doppelten Tasten');
});

test('noticeTexts: verstaendliche Hinweise, Programm und Verzeichnis eingesetzt, Unbekanntes faellt weg', () => {
  setPref('de', null);
  const conn = term('a', 'claude', { workDir: '~/x' });
  assert.deepEqual(noticeTexts('', conn, t), []);
  assert.deepEqual(noticeTexts(undefined, conn, t), []);
  assert.equal(noticeTexts('program-missing', conn, t)[0], 'claude ist auf diesem Rechner nicht installiert oder nicht im PATH. Es läuft eine normale Shell.');
  assert.match(noticeTexts('dir-missing', conn, t)[0], /~\/x/);
  const both = noticeTexts('program-missing tmux-missing', conn, t);
  assert.equal(both.length, 2);
  assert.match(both[1], /tmux/);
  assert.equal(noticeTexts('quatsch probe-failed', conn, t).length, 1);
  assert.equal(noticeTexts(['program-missing'], term('b', 'codex'), t)[0].startsWith('codex ist'), true);
  setPref('en', null);
  assert.match(noticeTexts('program-missing', conn, t)[0], /^claude is not installed on this machine or not in the PATH/);
  setPref('de', null);
});

test('primaryKeys: hoechstens sechs Tasten in der Zeile der Leiste, alle auch im Blatt "Mehr Tasten", Esc und Strg+C immer dabei', () => {
  for (const p of ['claude', 'codex', 'shell', 'custom', 'unbekannt']) {
    const prim = primaryKeys(p).map((k) => k.id), all = quickKeys(p).map((k) => k.id);
    assert.ok(prim.length <= 6, p);
    for (const id of prim) assert.ok(all.includes(id), `${p}: ${id}`);
    assert.ok(prim.includes('esc') && prim.includes('ctrlC'), p);
  }
  assert.ok(primaryKeys('claude').some((k) => k.id === 'shiftTab'));
});
