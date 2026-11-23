// Tests der Tasten-Logik: Kuerzel pruefen und abspielen, Text als Tastenfolge, Haltetasten, Pruefung einer Taste (reine Logik): node --test tests/frontend
import test from 'node:test';
import assert from 'node:assert/strict';
import { KS, MODSYM, MODNAME, charKeysym, parseChord, splitSteps, parseSequence, sequenceOps, heldAfter, textOps, chunk, TEMPLATES, LIMITS, validateKeyDef, keyApplies, looksLikeSecret } from '../../src/JulOS.Frontend/wwwroot/keys-shortcuts.js';
import { StickyMods, MODS, DOUBLE_TAP_MS } from '../../src/JulOS.Frontend/wwwroot/keys-sticky.js';
import { hasKey } from '../../src/JulOS.Frontend/wwwroot/i18n.js';

test('Tastennamen: deutsche und englische Namen, einzelne Zeichen, Unbekanntes wirft', () => {
  assert.deepEqual(parseChord('Strg+Umschalt+T'), [KS.ctrl, KS.shift, 0x74]);
  assert.deepEqual(parseChord('ctrl+alt+delete'), [KS.ctrl, KS.alt, KS.delete]);
  assert.deepEqual(parseChord('win+d'), [KS.win, 0x64]);
  assert.equal(KS.comma, 0x2c);
  assert.throws(() => parseChord('ctrl+quatsch'), /./);
  assert.throws(() => parseChord('  '), /./);
  assert.equal(charKeysym('\n'), KS.enter);
  assert.equal(charKeysym('\t'), KS.tab);
  assert.equal(charKeysym('a'), 0x61);
  assert.equal(charKeysym('ä'), 0xe4);
  assert.equal(charKeysym('€'), 0x01000000 + 0x20ac);
  assert.equal(MODNAME.windows, 'super');
  assert.equal(MODNAME.altgr, 'altgr');
});

test('Kuerzel: Folge von Kombinationen, Komma und Semikolon trennen', () => {
  assert.deepEqual(splitSteps('ctrl+alt+end, alt+tab;win+d ,, '), ['ctrl+alt+end', 'alt+tab', 'win+d']);
  const steps = parseSequence('ctrl+alt+end, alt+tab');
  assert.equal(steps.length, 2);
  assert.deepEqual(steps[0].keys, [KS.ctrl, KS.alt, KS.end]);
  assert.equal(steps[1].text, 'alt+tab');
  assert.deepEqual(parseSequence('super')[0].keys, [KS.super], 'eine einzelne Taste (Windows-Taste) ist ein gueltiger Schritt');
  assert.deepEqual(parseSequence('shift+comma')[0].keys, [KS.shift, 0x2c]);
});

test('Kuerzel: Grenzen und Fehler (leer, zu viele Schritte, zu viele Tasten, zu lang, unbekannte Taste)', () => {
  assert.throws(() => parseSequence(''), /./);
  assert.throws(() => parseSequence(' , ; '), /./);
  assert.throws(() => parseSequence(Array(LIMITS.steps + 1).fill('a').join(',')), /./);
  assert.equal(parseSequence(Array(LIMITS.steps).fill('a').join(',')).length, LIMITS.steps);
  assert.throws(() => parseSequence('ctrl+alt+shift+win+altgr+a'), /./);
  assert.equal(parseSequence('ctrl+alt+shift+win+a').length, 1);
  assert.throws(() => parseSequence('ctrl+blub'), /blub/);
  assert.throws(() => parseSequence('a,'.repeat(150)), /./, 'laenger als 200 Zeichen');
});

test('Kuerzel abspielen: jede Kombination drueckt der Reihe nach und laesst umgekehrt los, nichts bleibt gedrueckt', () => {
  const ops = sequenceOps(parseSequence('ctrl+alt+end, alt+tab'));
  const keyOps = ops.filter((o) => o.op !== 'wait').map((o) => o.op[0] + ':' + o.sym.toString(16));
  assert.deepEqual(keyOps, ['d:ffe3', 'd:ffe9', 'd:ff57', 'u:ff57', 'u:ffe9', 'u:ffe3', 'd:ffe9', 'd:ff09', 'u:ff09', 'u:ffe9']);
  assert.deepEqual(heldAfter(ops), []);
  assert.ok(ops.some((o) => o.op === 'wait' && o.ms >= 50), 'zwischen den Kombinationen liegt eine Pause');
  // bei einem Abbruch mitten drin wissen wir, was noch loszulassen ist
  const n = ops.findIndex((o) => o.op === 'down' && o.sym === KS.end) + 1;
  assert.deepEqual(heldAfter(ops, n), [KS.ctrl, KS.alt, KS.end]);
  assert.deepEqual(heldAfter(ops, 0), []);
});

test('Kuerzel: jede Vorlage fuer Windows und Linux ist gueltig und hat einen Namen', () => {
  for (const os of ['windows', 'linux']) {
    assert.ok(TEMPLATES[os].length >= 8);
    for (const tpl of TEMPLATES[os]) {
      assert.ok(hasKey(tpl.nameKey), tpl.nameKey);
      const steps = parseSequence(tpl.value);
      assert.deepEqual(heldAfter(sequenceOps(steps)), []);
      assert.ok(tpl.value.length <= LIMITS.value);
    }
  }
  assert.ok(TEMPLATES.windows.some((x) => x.value === 'ctrl+alt+end') && TEMPLATES.windows.some((x) => x.value === 'ctrl+shift+esc'));
  assert.ok(TEMPLATES.linux.some((x) => x.value === 'alt+tab'));
});

test('Als Text tippen: Zeichen werden Tasten, Zeilenenden Enter, Tabulator Tab, Steuerzeichen und feste Leerzeichen werden bereinigt', () => {
  const r = textOps('Hi\r\nDu\tda !\u0007​');
  assert.deepEqual(r.keys.map((k) => k.ch), ['H', 'i', '\n', 'D', 'u', '\t', 'd', 'a', ' ', '!']);
  assert.deepEqual(r.keys.filter((k) => k.ch === '\n' || k.ch === '\t').map((k) => k.sym), [KS.enter, KS.tab]);
  assert.equal(r.truncated, false);
  assert.deepEqual(textOps('a\rb').keys.map((k) => k.ch), ['a', '\n', 'b'], 'ein einzelnes CR zaehlt als Zeilenende');
  assert.deepEqual(textOps('').keys, []);
  assert.deepEqual(textOps(null).keys, []);
});

test('Als Text tippen: Laengenbegrenzung, Emoji zaehlt als ein Zeichen, Abbruch in Stuecken', () => {
  const r = textOps('x'.repeat(LIMITS.typeChars + 50));
  assert.equal(r.count, LIMITS.typeChars);
  assert.equal(r.total, LIMITS.typeChars + 50);
  assert.equal(r.truncated, true);
  assert.equal(textOps('a😀b', { max: 2 }).keys.at(-1).ch, '😀');
  assert.equal(textOps('a😀b').keys[1].sym, 0x01000000 + 0x1f600);
  const parts = chunk(textOps('abcdefghij').keys, 4);
  assert.deepEqual(parts.map((p) => p.length), [4, 4, 2]);
  assert.deepEqual(chunk([], 4), []);
});

test('Taste pruefen: Art, Wert, Namen, Laengen; was der Server ablehnen wuerde, geht hier nicht durch', () => {
  const ok = (o, opts) => validateKeyDef(o, opts);
  assert.equal(ok({ label: 'Task', type: 'seq', value: 'ctrl+shift+esc' }).ok, true);
  assert.equal(ok({ label: 'x', type: 'seq', value: 'ctrl+quatsch' }).ok, false);
  assert.equal(ok({ label: 'x', type: 'quatsch', value: 'a' }).ok, false);
  assert.equal(ok({ label: 'x', type: 'text', value: '' }).ok, false);
  assert.equal(ok({ label: 'x', type: 'text', value: 'zeile\nzwei' }).ok, false, 'Steuerzeichen im Text: der Server nimmt sie nicht');
  assert.equal(ok({ label: 'x', type: 'text', value: 'a'.repeat(201) }).ok, false);
  assert.equal(ok({ label: 'x', type: 'text', value: 'a'.repeat(200) }).ok, true);
  assert.equal(ok({ label: 'y'.repeat(25), type: 'chord', value: 'a' }).ok, false);
  assert.equal(ok({ label: '', type: 'chord', value: 'ctrl+c' }).def.label, 'ctrl+c');
  assert.equal(ok({ label: 'x', type: 'mod', value: 'WIN' }).def.value, 'win');
  assert.equal(ok({ label: 'x', type: 'mod', value: 'blub' }).ok, false);
  assert.equal(ok({ label: 'x', type: 'action', value: 'zoomin' }, { actions: ['zoomin'] }).ok, true);
  assert.equal(ok({ label: 'x', type: 'action', value: 'nix' }, { actions: ['zoomin'] }).ok, false);
  const d = ok({ label: 'x', type: 'seq', value: 'win+d', icon: 'bolt', conn: 'abc-123', scope: 'desktop' }).def;
  assert.deepEqual([d.icon, d.conn, d.scope], ['bolt', 'abc-123', 'desktop']);
  assert.equal(ok({ label: 'x', type: 'seq', value: 'win+d', icon: 'Evil<script>' }).ok, false);
  assert.equal(ok({ label: 'x', type: 'seq', value: 'win+d', conn: '../x' }).ok, false);
  assert.equal(ok({ label: 'x', type: 'seq', value: 'win+d', scope: 'both' }).def.scope, undefined);
});

test('Taste gilt nur in der passenden Sitzung: Art und Verbindung', () => {
  assert.equal(keyApplies({ type: 'seq' }, { kind: 'desktop', connId: 'a' }), true);
  assert.equal(keyApplies({ scope: 'terminal' }, { kind: 'desktop', connId: 'a' }), false);
  assert.equal(keyApplies({ conn: 'a' }, { kind: 'desktop', connId: 'a' }), true);
  assert.equal(keyApplies({ conn: 'a' }, { kind: 'desktop', connId: 'b' }), false);
});

test('Textbausteine sind kein Ort fuer Passwoerter: Warnung bei Namen und bei Werten, die danach aussehen', () => {
  assert.equal(looksLikeSecret('Mein Passwort', 'hallo'), true);
  assert.equal(looksLikeSecret('PIN', 'x'), true);
  assert.equal(looksLikeSecret('Login', 'Tr0ub4dor&3'), true);
  assert.equal(looksLikeSecret('Befehl', 'ls -la /var/log'), false);
  assert.equal(looksLikeSecret('Clear', '/clear'), false);
  assert.equal(looksLikeSecret('Pfad', 'docker-compose'), false);
  assert.equal(looksLikeSecret('Mail', 'name@example.org'), false);
});

/* ---------- Haltetasten ---------- */
function rig() {
  const log = [];
  const m = new StickyMods({ down: (s) => log.push('d' + s.toString(16)), up: (s) => log.push('u' + s.toString(16)), changed: () => log.push('~') });
  return { m, log, pure: () => log.filter((x) => x !== '~') };
}

test('Haltetaste: einmal tippen gilt fuer die naechste Taste und ist danach wieder aus', () => {
  const { m, pure } = rig();
  assert.equal(m.tap('ctrl', 1000), 'once');
  assert.deepEqual(pure(), ['dffe3']);
  assert.equal(m.get('ctrl'), 'once');
  m.consume();                                         // nach dem naechsten Zeichen oder Klick
  assert.equal(m.get('ctrl'), 'off');
  assert.deepEqual(pure(), ['dffe3', 'uffe3']);
  m.consume();                                         // ein weiteres Mal tut nichts
  assert.deepEqual(pure(), ['dffe3', 'uffe3']);
});

test('Haltetaste: zweimal schnell tippen rastet ein, ein weiterer Tipp loest; eingerastet bleibt sie bei consume', () => {
  const { m, pure } = rig();
  m.tap('alt', 0);
  assert.equal(m.tap('alt', DOUBLE_TAP_MS - 1), 'lock');
  m.consume(); m.consume();
  assert.equal(m.get('alt'), 'lock');
  assert.deepEqual(pure(), ['dffe9'], 'nur einmal gedrueckt, nicht zweimal');
  assert.equal(m.tap('alt', 5000), 'off');
  assert.deepEqual(pure(), ['dffe9', 'uffe9']);
});

test('Haltetaste: ein spaeter zweiter Tipp nimmt "einmal" zurueck, statt einzurasten', () => {
  const { m, pure } = rig();
  m.tap('shift', 0);
  assert.equal(m.tap('shift', DOUBLE_TAP_MS + 1), 'off');
  assert.deepEqual(pure(), ['dffe1', 'uffe1']);
});

test('Haltetasten sind unabhaengig: Strg und Umschalt zusammen, AltGr und Windows gibt es auch', () => {
  const { m, pure } = rig();
  m.tap('ctrl', 0); m.tap('shift', 10); m.tap('super', 20); m.tap('altgr', 30);
  assert.deepEqual(m.held(), ['ctrl', 'shift', 'super', 'altgr']);
  m.consume();
  assert.equal(m.any(), false);
  assert.equal(pure().filter((x) => x[0] === 'u').length, 4);
  assert.equal(MODSYM.altgr, 0xfe03);
  assert.deepEqual(MODS, ['ctrl', 'alt', 'shift', 'super', 'altgr']);
  assert.equal(m.tap('quatsch', 1), 'off');
});

test('Beim Verlassen der Sitzung: alles gedrueckte wird losgelassen, auch Eingerastetes; der Zustand ist danach aus', () => {
  const { m, pure, log } = rig();
  m.tap('ctrl', 0); m.tap('ctrl', 100); m.tap('alt', 200);
  assert.deepEqual([m.get('ctrl'), m.get('alt')], ['lock', 'once']);
  log.length = 0;
  m.releaseAll();
  assert.deepEqual(pure().sort(), ['uffe3', 'uffe9']);
  assert.equal(m.any(), false);
  assert.ok(log.includes('~'), 'die Tasten werden neu gezeichnet');
  const before = log.length;
  m.releaseAll();                                      // nichts gedrueckt: nichts zu senden
  assert.equal(log.length, before);
});

test('Nach einem Verbindungsabriss: force sendet das Loslassen an alle fuenf Tasten (das Ziel koennte eine halten), auch ohne eigenen Zustand', () => {
  const { m, pure } = rig();
  m.releaseAll(true);
  assert.deepEqual(pure().sort(), ['uffe1', 'uffe3', 'uffe9', 'uffeb', 'ufe03'].sort());
  assert.ok(pure().every((x) => x[0] === 'u'), 'nie ein Druecken dabei');
});

test('Eingerastete Taste bleibt ueber viele Tasten gedrueckt, bis erneut getippt wird (Strg+Klick mehrfach)', () => {
  const { m, pure } = rig();
  m.tap('ctrl', 0); m.tap('ctrl', 50);
  for (let i = 0; i < 5; i++) m.consume();
  assert.equal(m.get('ctrl'), 'lock');
  assert.deepEqual(pure(), ['dffe3']);
});
