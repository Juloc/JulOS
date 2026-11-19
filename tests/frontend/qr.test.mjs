// QR-Code fuer das Authenticator-Geheimnis (qr.js): eigene Umsetzung, deshalb gegen bekannte Werte und die Struktur geprueft.
// Dass echte Leser die Codes entziffern (Versionen 1 bis 10), wurde mit OpenCV nachgewiesen (siehe docs/DEVELOPMENT.md).
import test from 'node:test';
import assert from 'node:assert/strict';
import { qrMatrix, qrSvg, reedSolomon } from '../../src/JulOS.Frontend/wwwroot/qr.js';

test('Fehlerkorrektur stimmt mit dem bekannten Beispiel "HELLO WORLD" (Version 1, Stufe M) ueberein', () => {
  const data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
  assert.deepEqual(reedSolomon(data, 10), [196, 35, 39, 119, 235, 215, 231, 226, 93, 23]);
});

test('die Version waechst mit der Laenge: 21, 25, 29 ... Module', () => {
  assert.equal(qrMatrix('a').length, 21);                    // Version 1
  assert.equal(qrMatrix('x'.repeat(15)).length, 25);         // Version 2 (1-M fasst 14 Byte)
  assert.equal(qrMatrix('x'.repeat(213)).length, 57);        // Version 10
  assert.throws(() => qrMatrix('x'.repeat(214)), /zu lang/);
});

test('der Authenticator-Link passt (Version 5 oder kleiner) und hat die drei Suchmuster, Zeitmuster und das dunkle Modul', () => {
  const m = qrMatrix('otpauth://totp/JulOS:jul?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=JulOS');
  const n = m.length;
  assert.ok(n <= 37, 'Version ' + (n - 17) / 4);
  for (const [ox, oy] of [[0, 0], [n - 7, 0], [0, n - 7]]) {
    for (let i = 0; i < 7; i++) for (let j = 0; j < 7; j++) {
      const ring = Math.max(Math.abs(i - 3), Math.abs(j - 3));
      assert.equal(m[oy + j][ox + i], ring === 2 ? 0 : 1, `Suchmuster ${ox},${oy} an ${i},${j}`);
    }
  }
  for (let i = 8; i < n - 8; i++) { assert.equal(m[6][i], i % 2 === 0 ? 1 : 0); assert.equal(m[i][6], i % 2 === 0 ? 1 : 0); }
  assert.equal(m[n - 8][8], 1);
});

test('die Formatinformation ist ein gueltiges BCH-Wort der Stufe M (beide Kopien gleich)', () => {
  const m = qrMatrix('hallo');
  const n = m.length;
  const a = []; for (let i = 0; i <= 5; i++) a.push(m[i][8]); a.push(m[7][8], m[8][8], m[8][7]); for (let i = 9; i < 15; i++) a.push(m[8][14 - i]);
  const b = []; for (let i = 0; i < 8; i++) b.push(m[8][n - 1 - i]); for (let i = 8; i < 15; i++) b.push(m[n - 15 + i][8]);
  assert.deepEqual(a, b);
  const bits = a.reduce((acc, bit, i) => acc | (bit << i), 0) ^ 0x5412;
  let rem = bits >>> 10;
  for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
  assert.equal(rem & 0x3ff, bits & 0x3ff, 'BCH-Pruefbits');
  assert.equal((bits >>> 13) & 3, 0, 'Stufe M');
});

test('qrSvg liefert ein SVG mit hellem Rand und ohne Fremdinhalt', () => {
  const svg = qrSvg('hallo', { label: 'Test <&>' });
  assert.match(svg, /^<svg [^>]*viewBox="0 0 29 29"/);        // 21 + 2 * 4
  assert.match(svg, /<rect width="29" height="29" fill="#fff"\/>/);
  assert.doesNotMatch(svg, /script|href|<&>/);
  assert.match(svg, /aria-label="Test &#60;&#38;&#62;"/);
});
