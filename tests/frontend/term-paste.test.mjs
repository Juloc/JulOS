import test from 'node:test';
import assert from 'node:assert/strict';
import { parseOsc7, sameHost, shellQuote, quotePaths, pasteName, cleanName, planNames, chooseClipboard } from '../../src/JulOS.Frontend/wwwroot/term-paste.js';

test('OSC 7: ein absoluter Pfad wird gelesen und Prozent-Kodierung aufgeloest', () => {
  assert.deepEqual(parseOsc7('file://box/home/ich/mein%20Ordner'), { host: 'box', path: '/home/ich/mein Ordner' });
  assert.deepEqual(parseOsc7('file:///srv/ä%C3%B6'), { host: '', path: '/srv/äö' });
  assert.deepEqual(parseOsc7('file://Box/'), { host: 'box', path: '/' });
});

test('OSC 7: alles, was kein absoluter Pfad auf diesem Rechner ist, wird abgelehnt', () => {
  for (const bad of ['http://box/home', 'file://box', 'file://box/a/../b', 'file://box/a%2F..%2Fb/../c', 'file://box/a%0Ab', 'file://box/a%00b', 'file://box/%E0%A4%A', 'relativ/pfad', '', 'file://box/' + 'a'.repeat(5000)]) {
    assert.equal(parseOsc7(bad), null, bad);
  }
  assert.equal(parseOsc7(undefined), null);
});

test('OSC 7: der Host muss zu dieser Sitzung passen', () => {
  assert.equal(sameHost('', '', ''), true);
  assert.equal(sameHost('localhost', '', ''), true);
  assert.equal(sameHost('box', 'box', ''), true);
  assert.equal(sameHost('anderer', 'box', 'box.lan'), false);
  assert.equal(sameHost('box.lan', '', 'Box.LAN'), true);
});

test('Quoting: sichere Pfade bleiben, der Rest bekommt einfache Anfuehrungszeichen', () => {
  assert.equal(shellQuote('/home/ich/paste-20261124-101500.png'), '/home/ich/paste-20261124-101500.png');
  assert.equal(shellQuote('/home/ich/mein Bild.png'), "'/home/ich/mein Bild.png'");
  assert.equal(shellQuote("/tmp/it's.png"), "'/tmp/it'\\''s.png'");
  assert.equal(shellQuote('/tmp/$HOME`id`.png'), "'/tmp/$HOME`id`.png'");
  assert.equal(shellQuote('/tmp/a"b\\c;d&e|f.png'), "'/tmp/a\"b\\c;d&e|f.png'");
  assert.equal(shellQuote('/tmp/ünï/日本.png'), "'/tmp/ünï/日本.png'");
  assert.equal(shellQuote('/tmp/a\nb'), "'/tmp/a\nb'");   // in einfachen Anfuehrungszeichen ein Zeichen; quotePaths lehnt es trotzdem ab
});

test('Quoting: ein fuehrender Bindestrich wird nie ein Schalter, ~ wird nie aufgeloest', () => {
  assert.equal(shellQuote('-rf'), './-rf');
  assert.equal(shellQuote('-a b'), "'./-a b'");
  assert.equal(shellQuote('~root'), "'~root'");
  assert.equal(shellQuote(''), "''");
});

test('Pfade fuer die Kommandozeile: getrennt, mit einem Leerzeichen am Ende, Steuerzeichen und relative Pfade nie', () => {
  assert.equal(quotePaths(['/a/x.png', '/a/y z.png']), "/a/x.png '/a/y z.png' ");
  assert.equal(quotePaths(['/a/x\ny']), null);
  assert.equal(quotePaths(['/a/x\ty']), null);
  assert.equal(quotePaths(['/a/x\r']), null);
  assert.equal(quotePaths(['relativ']), null);
  assert.equal(quotePaths([]), null);
});

test('Namen: paste-Zeit, bereinigt, eindeutig', () => {
  assert.equal(pasteName(new Date(2026, 10, 24, 9, 5, 7)), 'paste-20261124-090507.png');
  assert.equal(cleanName('a\nb/c\u0007.txt'), 'a_b_c_.txt');
  assert.equal(cleanName('..'), 'datei');
  assert.equal(cleanName(''), 'datei');
  assert.ok(new TextEncoder().encode(cleanName('ä'.repeat(300))).length <= 255);
  const now = new Date(2026, 10, 24, 9, 5, 7);
  const r = planNames([{ name: 'image.png', fromClipboard: true }, { name: 'image.png', fromClipboard: true }, { name: 'a.png' }, { name: 'a.png' }, { name: 'b' }, { name: 'b' }], now).map((x) => x.name);
  assert.deepEqual(r, ['paste-20261124-090507.png', 'paste-20261124-090507-2.png', 'a.png', 'a-2.png', 'b', 'b-2']);
});

test('Zwischenablage: Text bleibt Text, ausser eine Datei, die kein Bild ist, liegt dabei', () => {
  assert.equal(chooseClipboard({ files: [], text: 'x' }), 'text');
  assert.equal(chooseClipboard({ files: [{ type: 'image/png' }], text: '' }), 'files');
  assert.equal(chooseClipboard({ files: [{ type: 'image/png' }], text: 'zelle' }), 'text');
  assert.equal(chooseClipboard({ files: [{ type: 'application/pdf' }], text: 'x.pdf' }), 'files');
  assert.equal(chooseClipboard({ files: [{ type: '' }], text: 'x' }), 'files');
});
