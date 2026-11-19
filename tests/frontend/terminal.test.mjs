import { test } from 'node:test';
import assert from 'node:assert/strict';
import { keysymBytes, charBytes } from '../../src/JulOS.Frontend/wwwroot/terminal.js';

const K = { enter: 0xff0d, bs: 0xff08, tab: 0xff09, esc: 0xff1b, up: 0xff52, down: 0xff54, right: 0xff53, left: 0xff51, home: 0xff50, end: 0xff57, pgup: 0xff55, del: 0xffff, f1: 0xffbe, f5: 0xffc2 };

test('plain keys', () => {
  assert.equal(keysymBytes(K.enter), '\r');
  assert.equal(keysymBytes(K.bs), '\x7f');
  assert.equal(keysymBytes(K.tab), '\t');
  assert.equal(keysymBytes(K.esc), '\x1b');
  assert.equal(keysymBytes(0x61), 'a');
  assert.equal(keysymBytes(0xe4), 'ä');                          // Latin-1 Keysym
  assert.equal(keysymBytes(0x01000000 + 0x20ac), '€');           // Unicode-Keysym
  assert.equal(keysymBytes(0xffe1), null);                      // Umschalter allein: nichts
  assert.equal(keysymBytes(0x20 - 1), null);                    // Steuerzeichen kommen nicht als Zeichen
});

test('arrows and navigation, normal and application cursor mode', () => {
  assert.equal(keysymBytes(K.up), '\x1b[A');
  assert.equal(keysymBytes(K.up, {}, true), '\x1bOA');
  assert.equal(keysymBytes(K.home), '\x1b[H');
  assert.equal(keysymBytes(K.end, {}, true), '\x1bOF');
  assert.equal(keysymBytes(K.pgup), '\x1b[5~');
  assert.equal(keysymBytes(K.del), '\x1b[3~');
  assert.equal(keysymBytes(K.f1), '\x1bOP');
  assert.equal(keysymBytes(K.f5), '\x1b[15~');
});

test('modifiers on special keys use the xterm parameter', () => {
  assert.equal(keysymBytes(K.right, { ctrl: true }), '\x1b[1;5C');
  assert.equal(keysymBytes(K.left, { alt: true }), '\x1b[1;3D');
  assert.equal(keysymBytes(K.up, { shift: true, ctrl: true }), '\x1b[1;6A');
  assert.equal(keysymBytes(K.up, { ctrl: true }, true), '\x1b[1;5A');       // mit Umschaltern gilt auch im Anwendungsmodus die CSI-Form
  assert.equal(keysymBytes(K.del, { ctrl: true }), '\x1b[3;5~');
  assert.equal(keysymBytes(K.f1, { shift: true }), '\x1b[1;2P');
  assert.equal(keysymBytes(K.tab, { shift: true }), '\x1b[Z');
  assert.equal(keysymBytes(K.bs, { ctrl: true }), '\x08');
  assert.equal(keysymBytes(K.enter, { alt: true }), '\x1b\r');
});

test('ctrl and alt on characters', () => {
  assert.equal(keysymBytes(0x63, { ctrl: true }), '\x03');                   // Strg+C
  assert.equal(keysymBytes(0x43, { ctrl: true }), '\x03');                   // grosses C wie kleines
  assert.equal(keysymBytes(0x64, { ctrl: true }), '\x04');
  assert.equal(keysymBytes(0x7a, { ctrl: true }), '\x1a');
  assert.equal(keysymBytes(0x5b, { ctrl: true }), '\x1b');                   // Strg+[
  assert.equal(keysymBytes(0x20, { ctrl: true }), '\0');
  assert.equal(keysymBytes(0x61, { alt: true }), '\x1ba');
  assert.equal(keysymBytes(0x66, { ctrl: true, alt: true }), '\x1b\x06');
  assert.equal(keysymBytes(0x74, { ctrl: true, shift: true }), '\x14');      // Strg+Umschalt+T: Umschalt ist bei Strg ohne Wirkung
  assert.equal(keysymBytes(0x61, { shift: true }), 'A');                     // Leiste: Umschalt gehalten + Kleinbuchstabe
  assert.equal(keysymBytes(0x31, { shift: true }), '1');
});

test('charBytes handles typed text for the key bar toggles', () => {
  assert.equal(charBytes('c', { ctrl: true }), '\x03');
  assert.equal(charBytes('x', { alt: true }), '\x1bx');
  assert.equal(charBytes('ä', { ctrl: true }), 'ä');
  assert.equal(charBytes('€', {}), '€');
});
