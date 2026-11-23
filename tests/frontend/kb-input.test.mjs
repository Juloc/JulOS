// Tests der Eingabe-Abgleichs der Bildschirmtastatur: Tippen, Autokorrektur, Diktieren (ganze Woerter und Saetze auf einmal, Zwischenstaende): node --test tests/frontend
import test from 'node:test';
import assert from 'node:assert/strict';
import { cleanKbText, diffText, KbSync, RESET_AT, graphemes } from '../../src/JulOS.Frontend/wwwroot/kb-input.js';

/** Das Ziel: ein Textpuffer, auf den die Rueckschritte und das Getippte wirken (Rueckschritt loescht ein ganzes Zeichen wie der Anwender es sieht). */
function remote() {
  const seg = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
  let text = '';
  return {
    apply({ back, add }) {
      const parts = [...seg.segment(text)].map((s) => s.segment);
      parts.length = Math.max(0, parts.length - back);
      text = parts.join('') + add;
    },
    get text() { return text; },
  };
}
/** Spielt die Staende des Eingabefelds durch KbSync ab und gibt zurueck, was im Ziel steht. */
function replay(states, opts = {}) {
  const sync = new KbSync(), r = remote();
  for (const s of states) {
    const value = typeof s === 'string' ? s : s.value, composing = typeof s === 'string' ? false : !!s.composing;
    const d = sync.feed(value, { composing });
    r.apply(d);
    if (d.reset && opts.onReset) opts.onReset();
  }
  return r.text;
}

test('Abgleich: gemeinsamer Anfang bleibt, der Rest wird ersetzt', () => {
  assert.deepEqual(diffText('Hallo', 'Hallo Welt'), { back: 0, add: ' Welt' });
  assert.deepEqual(diffText('Hallo Welt', 'Hallo'), { back: 5, add: '' });
  assert.deepEqual(diffText('Hallo Welt', 'Hallo Wald'), { back: 3, add: 'ald' });
  assert.deepEqual(diffText('', ''), { back: 0, add: '' });
  assert.deepEqual(diffText('abc', 'abc'), { back: 0, add: '' });
});

test('Tippen Zeichen fuer Zeichen mit Autokorrektur: das Ziel hat am Ende genau das, was im Feld steht', () => {
  assert.equal(replay(['H', 'Ha', 'Hal', 'Hall', 'Hallo', 'Hallo ', 'Hallo W', 'Hallo Wel', 'Hallo Welt']), 'Hallo Welt');
  // die Tastatur ersetzt "teh" durch "the" beim Leerzeichen
  assert.equal(replay(['t', 'te', 'teh', 'the ']), 'the ');
  // Wort per Vorschlag ersetzt
  assert.equal(replay(['Ich bin hir', 'Ich bin hier ']), 'Ich bin hier ');
});

test('Diktieren: ganze Saetze kommen auf einmal an, nichts doppelt, nichts verloren', () => {
  assert.equal(replay(['Heute ist ein schoener Tag und ich schreibe diesen Satz per Sprache.']), 'Heute ist ein schoener Tag und ich schreibe diesen Satz per Sprache.');
  assert.equal(replay(['Erster Satz. ', 'Erster Satz. Zweiter Satz mit mehreren Woertern auf einmal. ']), 'Erster Satz. Zweiter Satz mit mehreren Woertern auf einmal. ');
});

test('Diktieren mit Zwischenstaenden der Spracherkennung (Composition): die Zwischenstaende werden ersetzt, am Ende steht der letzte Stand', () => {
  const states = [
    { value: 'wir', composing: true }, { value: 'wir wollen', composing: true }, { value: 'wir wollen hier fliegen', composing: true },
    { value: 'wir wollen heute fliegen', composing: true },            // Korrektur eines Wortes mitten im Zwischenstand
    { value: 'Wir wollen heute fliegen.', composing: false },          // abgeschlossen, mit grossem Anfang und Punkt
    { value: 'Wir wollen heute fliegen. Und', composing: true }, { value: 'Wir wollen heute fliegen. Und morgen auch', composing: false },
  ];
  assert.equal(replay(states), 'Wir wollen heute fliegen. Und morgen auch');
});

test('Korrektur der Tastatur weit vorn im Text (Autokorrektur eines frueheren Wortes) ersetzt nur den Rest ab dort', () => {
  assert.equal(replay(['Das ist ein Tst', 'Das ist ein Test ', 'Das ist ein Test mit ', 'Das ist ein Test mit mehr']), 'Das ist ein Test mit mehr');
  assert.deepEqual(diffText('Wir sind hier und dort', 'Wir sind heir und dort'), { back: 12, add: 'eir und dort' });
});

test('Umlaute, Emoji und Zeichen ausserhalb der Grundebene werden nicht zerrissen', () => {
  assert.equal(replay(['Gruess', 'Grüß', 'Grüß Gott 😀', 'Grüß Gott 😀😀']), 'Grüß Gott 😀😀');
  assert.equal(replay(['a👨‍👩‍👧', 'a']), 'a', 'ein Emoji aus mehreren Teilen wird mit einem Rueckschritt entfernt');
  assert.equal(graphemes('👨‍👩‍👧'), 1);
  assert.equal(replay(['x🏳️‍🌈y', 'x']), 'x');
});

test('Festes Leerzeichen eines bearbeitbaren Elements wird zum Leerzeichen, nicht zu einem anderen Zeichen im Ziel', () => {
  assert.equal(cleanKbText('a b​'), 'a b');
  assert.equal(replay(['Hallo ', 'Hallo Welt']), 'Hallo Welt');
  assert.equal(cleanKbText('a\r\nb\rc'), 'a\nb\nc');
});

test('Zweimal derselbe Stand (input und compositionend kurz nacheinander) schickt nichts doppelt', () => {
  const sync = new KbSync();
  assert.deepEqual(sync.feed('Hallo'), { back: 0, add: 'Hallo', reset: false });
  assert.deepEqual(sync.feed('Hallo'), { back: 0, add: '', reset: false });
  assert.deepEqual(sync.feed('Hallo', { composing: true }), { back: 0, add: '', reset: false });
});

test('Langes Diktat: das Feld wird erst geleert, wenn die Tastatur nichts mehr bearbeitet, und nichts geht dabei verloren oder wird doppelt', () => {
  const sentence = 'Dies ist ein langer Satz, der diktiert wird. ';
  const states = [];
  let field = '';
  for (let i = 0; i < 12; i++) { field += sentence; states.push({ value: field, composing: i % 2 === 0 }); }
  const sync = new KbSync(), r = remote(), resets = [];
  let expected = '';
  let fieldNow = '';
  for (let i = 0; i < 12; i++) {
    fieldNow += sentence; expected += sentence;
    const d = sync.feed(fieldNow, { composing: i % 2 === 0 });
    r.apply(d);
    if (d.reset) { resets.push(i); fieldNow = ''; }      // das Feld wird geleert; der naechste Satz beginnt von vorn
  }
  assert.equal(r.text.length > 0, true);
  assert.ok(resets.length >= 1, 'es wurde geleert');
  assert.ok(resets.every((i) => i % 2 === 1), 'nie waehrend eines Zwischenstands');
  assert.equal(r.text, expected, 'im Ziel steht alles genau einmal');
  assert.ok(RESET_AT > 100);
});

test('Wegloeschen am Ende und Neuschreiben (Rueckschritte der Tastatur)', () => {
  assert.equal(replay(['Hallo Welt', 'Hallo Wel', 'Hallo We', 'Hallo W', 'Hallo Wo', 'Hallo Wor', 'Hallo Wort']), 'Hallo Wort');
});
