// Reine Logik der Word-/Excel-Vorschau (viewer-office.js): ZIP-Bombe-Grenze, csv, Kuerzen, Zellen, Bereinigung der Word-Ausgabe. Keine Optik.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { zipStats, zipRefusal, parseCsv, detectDelimiter, decodeText, cutTable, colName, fmtCell, keepAttr, dropTag, safeStyle, cleanCss, cssLengthPx, frameDoc, isDataImage, LIMITS, MiB } from '../../src/JulOS.Frontend/wwwroot/viewer-office.js';
import { viewKind, refusal } from '../../src/JulOS.Frontend/wwwroot/files-viewer.js';

/** Ein ZIP mit "gespeicherten" Eintraegen, deren angegebene entpackte Groesse frei waehlbar ist (so sieht eine ZIP-Bombe im Verzeichnis aus) */
function zip(entries, { comment = 0, mutate } = {}) {
  const enc = new TextEncoder(), parts = [], central = [];
  let offset = 0;
  for (const { name, size, data = new Uint8Array(0) } of entries) {
    const nm = enc.encode(name);
    const local = new Uint8Array(30 + nm.length + data.length), l = new DataView(local.buffer);
    l.setUint32(0, 0x04034b50, true); l.setUint32(18, data.length, true); l.setUint32(22, size ?? data.length, true); l.setUint16(26, nm.length, true);
    local.set(nm, 30); local.set(data, 30 + nm.length);
    const cd = new Uint8Array(46 + nm.length), c = new DataView(cd.buffer);
    c.setUint32(0, 0x02014b50, true); c.setUint32(20, data.length, true); c.setUint32(24, size ?? data.length, true); c.setUint16(28, nm.length, true); c.setUint32(42, offset, true);
    cd.set(nm, 46);
    parts.push(local); central.push(cd); offset += local.length;
  }
  const cdSize = central.reduce((a, b) => a + b.length, 0);
  const end = new Uint8Array(22 + comment), e = new DataView(end.buffer);
  e.setUint32(0, 0x06054b50, true); e.setUint16(8, entries.length, true); e.setUint16(10, entries.length, true); e.setUint32(12, cdSize, true); e.setUint32(16, offset, true); e.setUint16(20, comment, true);
  const all = new Uint8Array(offset + cdSize + end.length);
  let at = 0;
  for (const p of [...parts, ...central, end]) { all.set(p, at); at += p.length; }
  return mutate ? mutate(all) : all;
}

test('ZIP: das Verzeichnis wird gelesen, ohne zu entpacken; die Summe der entpackten Groessen entscheidet', () => {
  const small = zip([{ name: 'word/document.xml', data: new Uint8Array(100) }, { name: '[Content_Types].xml', data: new Uint8Array(50) }]);
  assert.deepEqual(zipStats(small), { ok: true, entries: 2, unpacked: 150, zip64: false });
  assert.equal(zipRefusal(zipStats(small), 'word'), null);
  assert.equal(zipRefusal(zipStats(small), 'sheet'), null);
  // eine Bombe: wenige Bytes, laut Verzeichnis 1 GiB entpackt
  const bomb = zip([{ name: 'xl/worksheets/sheet1.xml', data: new Uint8Array(10), size: 1024 * MiB }]);
  assert.equal(zipStats(bomb).unpacked, 1024 * MiB);
  assert.equal(zipRefusal(zipStats(bomb), 'word'), 'unpacked');
  assert.equal(zipRefusal(zipStats(bomb), 'sheet'), 'unpacked');
  // die Grenze: Word 200 MiB, Excel 64 MiB (genau auf der Grenze noch ja)
  assert.equal(zipRefusal(zipStats(zip([{ name: 'a', data: new Uint8Array(1), size: 64 * MiB }])), 'sheet'), null);
  assert.equal(zipRefusal(zipStats(zip([{ name: 'a', data: new Uint8Array(1), size: 64 * MiB + 1 }])), 'sheet'), 'unpacked');
  assert.equal(zipRefusal(zipStats(zip([{ name: 'a', data: new Uint8Array(1), size: 64 * MiB + 1 }])), 'word'), null);
  // viele kleine Eintraege
  assert.equal(zipRefusal(zipStats(zip(Array.from({ length: LIMITS.zipEntries + 1 }, (_, i) => ({ name: 'f' + i })))), 'word'), 'unpacked');
  // Zahlen ab 4 GiB (Zip64): zu gross
  const z64 = zip([{ name: 'a', data: new Uint8Array(1), size: 0xffffffff }]);
  assert.equal(zipStats(z64).zip64, true);
  assert.equal(zipRefusal(zipStats(z64), 'word'), 'unpacked');
  // ein Kommentar am Ende stoert das Suchen des Verzeichnisses nicht
  assert.equal(zipStats(zip([{ name: 'a', data: new Uint8Array(3) }], { comment: 40 })).unpacked, 3);
});

test('ZIP: Muell, Abgeschnittenes und ein Verzeichnis ausserhalb der Datei sind ungueltig, nie ein Absturz', () => {
  const ok = zip([{ name: 'a', data: new Uint8Array(5) }]);
  for (const bad of [new Uint8Array(0), new Uint8Array(10), new Uint8Array(100), ok.slice(0, ok.length - 5), ok.slice(10), new TextEncoder().encode('<html>'.repeat(20)),
    zip([{ name: 'a' }], { mutate: (b) => { new DataView(b.buffer).setUint32(b.length - 22 + 16, 0x7fff0000, true); return b; } }),                // Verzeichnis hinter dem Ende
    zip([{ name: 'a' }], { mutate: (b) => { new DataView(b.buffer).setUint16(b.length - 22 + 10, 9, true); return b; } })]) {                           // mehr Eintraege als vorhanden
    assert.equal(zipRefusal(zipStats(bad), 'word'), 'invalid');
  }
  assert.equal(zipRefusal(null, 'word'), 'invalid');
});

test('csv: Trennzeichen, Anfuehrungszeichen, Umbrueche in Feldern, Zeilenenden, letzte Zeile', () => {
  assert.deepEqual(parseCsv('a,b\n1,2\n').rows, [['a', 'b'], ['1', '2']]);
  assert.deepEqual(parseCsv('a;b\r\n"x;y";"say ""hi"""\r\n').rows, [['a', 'b'], ['x;y', 'say "hi"']]);
  assert.deepEqual(parseCsv('a\tb\n1\t2').rows, [['a', 'b'], ['1', '2']], 'letzte Zeile ohne Umbruch, Tab');
  assert.deepEqual(parseCsv('"a\nb",c\r"d",e').rows, [['a\nb', 'c'], ['d', 'e']], 'Umbruch im Feld, einzelnes \\r');
  assert.deepEqual(parseCsv('a,,c\n,,\n').rows, [['a', '', 'c'], ['', '', '']], 'leere Felder bleiben, eine leere letzte Zeile nicht');
  assert.deepEqual(parseCsv('').rows, []);
  assert.deepEqual(parseCsv('x"y,z').rows, [['x"y', 'z']], 'ein Anfuehrungszeichen mitten im Feld ist Text');
  assert.equal(detectDelimiter('a;b;c,d\n1,2,3'), ';');
  assert.equal(detectDelimiter('"a,b,c";d\n'), ';', 'Kommas in Anfuehrungszeichen zaehlen nicht');
  assert.equal(detectDelimiter('einspaltig\nzwei'), ',');
});

test('csv: grosse Dateien werden nur gezaehlt, nicht gespeichert (Zeilen und Spalten)', () => {
  const text = Array.from({ length: 12 }, (_, r) => Array.from({ length: 7 }, (_, c) => `${r}.${c}`).join(',')).join('\n') + '\n';
  const r = parseCsv(text, { maxRows: 5, maxCols: 3 });
  assert.equal(r.rows.length, 5);
  assert.deepEqual(r.rows[4], ['4.0', '4.1', '4.2']);
  assert.equal(r.totalRows, 12); assert.equal(r.totalCols, 7);
  const wide = parseCsv('a,b,c\n1,2,3,4,5,6\n', { maxRows: 5, maxCols: 3 });
  assert.equal(wide.totalCols, 6); assert.deepEqual(wide.rows[1], ['1', '2', '3']);
  const quoted = parseCsv('"1,2",x\n' + '"a\nb",y\n'.repeat(10), { maxRows: 2, maxCols: 5 });
  assert.equal(quoted.totalRows, 11, 'Umbrueche in Anfuehrungszeichen zaehlen als ein Feld auch hinter der Grenze');
});

test('Text: UTF-8, UTF-16 mit Kennung, sonst Windows-1252', () => {
  assert.equal(decodeText(new TextEncoder().encode('Größe;€')), 'Größe;€');
  assert.equal(decodeText(Uint8Array.from([0xef, 0xbb, 0xbf, 0x41])), 'A', 'UTF-8-Kennung fällt weg');
  assert.equal(decodeText(Uint8Array.from([0xff, 0xfe, 0x41, 0x00, 0xfc, 0x00])), 'Aü');
  assert.equal(decodeText(Uint8Array.from([0xfe, 0xff, 0x00, 0x41])), 'A');
  assert.equal(decodeText(Uint8Array.from([0x4d, 0xfc, 0x6c, 0x6c, 0x65, 0x72])), 'Müller', 'kein UTF-8: Windows-1252');
});

test('cutTable: Grenzen fuer Zeilen und Spalten, "cut" nur wenn etwas fehlt', () => {
  const data = Array.from({ length: 7 }, (_, r) => Array.from({ length: 4 }, (_, c) => r * 10 + c));
  const a = cutTable(data, { maxRows: 5, maxCols: 3 });
  assert.equal(a.rows.length, 5); assert.equal(a.rows[0].length, 3);
  assert.deepEqual([a.totalRows, a.totalCols, a.cut], [7, 4, true]);
  assert.equal(cutTable(data, { maxRows: 7, maxCols: 4 }).cut, false, 'genau auf der Grenze: nichts fehlt');
  assert.equal(cutTable(data, { maxRows: 6, maxCols: 4 }).cut, true);
  assert.deepEqual(cutTable([[1], [1, 2, 3]], { maxCols: 2 }).rows, [[1], [1, 2]]);
  assert.deepEqual(cutTable(null), { rows: [], totalRows: 0, totalCols: 0, cut: false });
  assert.deepEqual([LIMITS.rows, LIMITS.cols], [5000, 50]);
});

test('Zellen: Zahlen ohne Rauschen, Datum, Wahrheit, Kappen; Spaltenbuchstaben', () => {
  assert.equal(fmtCell(0.1 + 0.2), '0.3');
  assert.equal(fmtCell(0.1 + 0.2, 'de'), '0,3');
  assert.equal(fmtCell(1200.5, 'de'), '1200,5');
  assert.equal(fmtCell(12), '12');
  assert.equal(fmtCell(-0.000001), '-0.000001');
  assert.equal(fmtCell(new Date('2026-03-15T00:00:00Z')), '2026-03-15');
  assert.equal(fmtCell(new Date('2026-03-15T13:45:10Z')), '2026-03-15 13:45');
  assert.equal(fmtCell(new Date('x')), '');
  assert.equal(fmtCell(true), 'TRUE');
  assert.equal(fmtCell(null), ''); assert.equal(fmtCell(undefined), '');
  assert.equal(fmtCell('<img src=x onerror=alert(1)>'), '<img src=x onerror=alert(1)>', 'Text bleibt Text (die Oberflaeche nimmt textContent)');
  assert.equal(fmtCell('x'.repeat(5000)).length, LIMITS.cellChars + 1);
  assert.deepEqual([0, 1, 25, 26, 27, 51, 52, 701, 702].map(colName), ['A', 'B', 'Z', 'AA', 'AB', 'AZ', 'BA', 'ZZ', 'AAA']);
});

test('Word-Ausgabe: gefaehrliche Elemente fallen, Attribute nur harmlos, Adressen nur als eingebettetes Bild', () => {
  for (const t of ['script', 'SCRIPT', 'iframe', 'object', 'embed', 'link', 'meta', 'base', 'form', 'input', 'video', 'audio', 'math', 'foreignObject', 'use', 'template', 'noscript']) assert.equal(dropTag(t), true, t);
  for (const t of ['p', 'span', 'table', 'td', 'img', 'a', 'svg', 'path', 'div', 'ul', 'li', 'br', 'sup', 'colgroup', 'style']) assert.equal(dropTag(t), false, t);
  for (const [n, v] of [['onclick', 'x'], ['onerror', 'x'], ['ONLOAD', 'x'], ['href', 'https://evil'], ['href', 'javascript:alert(1)'], ['src', 'https://evil/x.png'], ['src', 'blob:https://h/1'], ['src', 'data:text/html,<script>'], ['xlink:href', 'http://x'],
    ['srcdoc', '<p>'], ['style', 'background:url(https://evil/x.png)'], ['style', 'background:url("//evil/x")'], ['style', '@import url(x)'], ['style', 'width:expression(alert(1))'], ['style', 'x:y\\75rl(z)'], ['target', '_blank'], ['formaction', 'x']])
    assert.equal(keepAttr(n, v), false, `${n}=${v}`);
  for (const [n, v] of [['class', 'docx'], ['colspan', '2'], ['title', 'x'], ['src', 'data:image/png;base64,AAAA'], ['src', ' DATA:image/jpeg;base64,/9j'], ['style', 'width:595pt;font-family:"Calibri"'], ['style', 'background:url(data:image/png;base64,AAA)'], ['alt', 'x']])
    assert.equal(keepAttr(n, v), true, `${n}=${v}`);
  assert.equal(isDataImage('data:text/html;base64,AA'), false);
  assert.equal(safeStyle('color:red'), true);
});

test('Word-Ausgabe: CSS und Rahmendokument ohne Ausbruch, Nachladen oder Skript', () => {
  const css = cleanCss('p{color:red}@import url(https://evil/x.css);a{background:url(https://evil/x.png)}b{background:url(data:image/png;base64,AA)}</style><script>alert(1)</script>i{width:expression(alert(1))}');
  assert.ok(!/@import|evil|<|expression\(/i.test(css), css);
  assert.ok(css.includes('url(data:image/png;base64,AA)') && css.includes('p{color:red}'));
  const doc = frameDoc({ css: 'p{margin:0}', body: '<div class="docx-wrapper"><p>Hallo</p></div>', zoom: 0.5 });
  assert.match(doc, /^<!doctype html>/);
  assert.ok(doc.indexOf('Content-Security-Policy') < doc.indexOf('<style>') && doc.indexOf('Content-Security-Policy') < doc.indexOf('<body>'), 'die CSP steht vor allem anderen');
  assert.match(doc, /default-src 'none'; img-src data:; style-src 'unsafe-inline'/);
  assert.ok(!/script-src|connect-src/.test(doc.split('</head>')[0].replace(/default-src 'none'/, '')));
  assert.match(doc, /\.docx-wrapper\{zoom:0\.5;/);
  assert.match(frameDoc({ css: '', body: '', zoom: 'abc' }), /zoom:1;/);
  assert.match(frameDoc({ css: '', body: '', zoom: 1000 }), /zoom:8;/);
  assert.equal(Math.round(cssLengthPx('595.3pt')), 794);
  assert.equal(Math.round(cssLengthPx('21cm')), 794);
  assert.equal(cssLengthPx('700px'), 700);
  for (const bad of ['', null, 'auto', '5px', '999999px', 'calc(1px)']) assert.equal(cssLengthPx(bad), 794, String(bad));
});

test('Typen: docx als Word, xlsx und csv als Tabelle, alte .doc/.xls als "alt" (nur Meldung), die Grenzen wie am Server', () => {
  assert.equal(viewKind('a.DOCX'), 'word');
  assert.equal(viewKind('a.xlsx'), 'sheet'); assert.equal(viewKind('a.csv'), 'sheet');
  assert.equal(viewKind('a.doc'), 'legacy'); assert.equal(viewKind('a.XLS'), 'legacy');
  for (const n of ['a.docm', 'a.xlsm', 'a.dotx', 'a.ods', 'a.odt', 'a.rtf', 'a.txt', 'a.tsv']) assert.equal(viewKind(n), null, n);
  assert.equal(refusal('a.doc', 1, {}), 'preview_legacy');
  assert.equal(refusal('a.xls', 1, { root: true }), 'preview_legacy');
  assert.equal(refusal('a.xlsx', 10 * MiB, { root: true, previewBytes: 50 * MiB, maxBytes: 200 * MiB }), null, 'Root: erlaubt (der Abruf ist kein Strom)');
  assert.equal(refusal('a.docx', 51 * MiB, { previewBytes: 50 * MiB, maxBytes: 200 * MiB }), 'preview_too_large');
});
