import { test } from 'node:test';
import assert from 'node:assert/strict';
import { inputKeyAction, sendPlan, rowsFor, keyBytes, MAX_ROWS } from '../../src/JulOS.Frontend/wwwroot/terminal-input.js';
import { quickKeys } from '../../src/JulOS.Frontend/wwwroot/terminal-programs.js';

test('inputKeyAction: Enter sendet, Umschalt+Enter ist eine neue Zeile, waehrend der Komposition nichts', () => {
  assert.equal(inputKeyAction({ key: 'Enter' }), 'send');
  assert.equal(inputKeyAction({ key: 'Enter', ctrlKey: true }), 'send');
  assert.equal(inputKeyAction({ key: 'Enter', shiftKey: true }), null);
  assert.equal(inputKeyAction({ key: 'Enter', altKey: true }), null);
  assert.equal(inputKeyAction({ key: 'Enter', isComposing: true }), null);      // bestaetigt Diktat/Eingabemethode
  assert.equal(inputKeyAction({ key: 'Enter', keyCode: 229 }), null);           // Android meldet 229 waehrend der Komposition
  assert.equal(inputKeyAction({ key: 'a' }), null);
  assert.equal(inputKeyAction({ key: 'Escape' }), null);
});

test('sendPlan: Text als Einfuegen, danach Enter; leer sendet nur Enter; Zeilenenden am Ende fallen weg', () => {
  assert.deepEqual(sendPlan('hallo welt'), { text: 'hallo welt', enter: true });
  assert.deepEqual(sendPlan(''), { text: null, enter: true });
  assert.deepEqual(sendPlan('  \n\n'), { text: null, enter: true });
  assert.deepEqual(sendPlan(null), { text: null, enter: true });
  assert.deepEqual(sendPlan('a\nb\n\n'), { text: 'a\nb', enter: true });
  assert.deepEqual(sendPlan('a\r\nb\r'), { text: 'a\nb', enter: true });          // Zeilenenden vereinheitlicht
  assert.equal(sendPlan('  eingerueckt').text, '  eingerueckt');                   // Anfang bleibt (Code, Listen)
  assert.equal(sendPlan('x'.repeat(100000)).text.length, 100000);                  // lange Eingaben gehen ganz durch (die Ansicht teilt sie in Stuecke)
});

test('rowsFor: waechst mit den Zeilen bis zur Obergrenze', () => {
  assert.equal(rowsFor(''), 1);
  assert.equal(rowsFor('eine'), 1);
  assert.equal(rowsFor('a\nb'), 2);
  assert.equal(rowsFor('a\n'.repeat(50)), MAX_ROWS);
  assert.equal(rowsFor(undefined), 1);
});

test('keyBytes: die Schnelltasten ergeben die Bytes, die Claude Code und Codex erwarten', () => {
  const by = Object.fromEntries(quickKeys('claude').map((k) => [k.id, k]));
  assert.equal(keyBytes(by.esc), '\x1b');
  assert.equal(keyBytes(by.tab), '\t');
  assert.equal(keyBytes(by.shiftTab), '\x1b[Z');
  assert.equal(keyBytes(by.ctrlC), '\x03');
  assert.equal(keyBytes(by.ctrlD), '\x04');
  assert.equal(keyBytes(by.ctrlO), '\x0f');
  assert.equal(keyBytes(by.enter), '\r');
  assert.equal(keyBytes(by.up), '\x1b[A');
  assert.equal(keyBytes(by.up, true), '\x1bOA');                                  // Anwendungsmodus
  assert.equal(keyBytes(by.down), '\x1b[B');
  assert.equal(keyBytes(by.right), '\x1b[C');
  assert.equal(keyBytes(by.left), '\x1b[D');
  assert.equal(keyBytes(by.pgUp), '\x1b[5~');
  assert.equal(keyBytes(by.pgDn), '\x1b[6~');
  assert.equal(keyBytes(by.slash), '/');
  assert.equal(keyBytes(by.at), '@');
});

test('findLastUrl: die letzte https-Adresse, auch ueber umbrochene Zeilen, ohne Satzzeichen am Ende', async () => {
  const { findLastUrl } = await import('../../src/JulOS.Frontend/wwwroot/terminal-input.js');
  const L = (text, wrapped = false) => ({ text, wrapped });
  assert.equal(findLastUrl([]), null);
  assert.equal(findLastUrl([L('kein Link hier'), L('http://unsicher.example/x')]), null);
  assert.equal(findLastUrl([L('Browse to https://example.invalid/a'), L('später: https://example.invalid/b.')]), 'https://example.invalid/b');
  assert.equal(findLastUrl([L('Open: https://example.invalid/oauth?client=1&redirect=abc'), L('def&state=xyz   ', true), L('Paste code here')]), 'https://example.invalid/oauth?client=1&redirect=abcdef&state=xyz');
});
