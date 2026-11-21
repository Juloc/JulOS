// Download in der Dateien-App: der Vorab-Check nennt den Grund, statt dass der Browser still scheitert (files-core.js, docs/FILES.md). Reine Logik mit einem Ersatz fuer fetch.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import * as C from '../../src/JulOS.Frontend/wwwroot/files-core.js';

setPref('de', null);
const answer = (status, body) => async (_url, opts) => { answer.opts = opts; return { ok: status < 400, status, json: async () => { if (body === undefined) throw new Error('kein JSON'); return body; } }; };

test('Ein Download, den der Server annimmt, gibt kein Problem und wird nach den Kopfzeilen abgebrochen', async () => {
  assert.equal(await C.downloadProblem('/x', answer(200)), null);
  assert.equal(answer.opts.signal.aborted, true);
});

test('Ein Fehler des Servers wird zum Text zum Fehlercode, ein unbekannter oder fehlender Code zum allgemeinen Text', async () => {
  assert.equal(await C.downloadProblem('/x', answer(403, { code: 'denied' })), C.errText('denied'));
  assert.equal(await C.downloadProblem('/x', answer(413, { code: 'too_large' })), C.errText('too_large'));
  assert.equal(await C.downloadProblem('/x', answer(400, { code: 'not_a_file' })), C.errText('not_a_file'));
  assert.equal(await C.downloadProblem('/x', answer(502, { code: 'neu_und_unbekannt' })), C.errText('failed'));
  assert.equal(await C.downloadProblem('/x', answer(500)), C.errText('failed'));
});

test('Ein Netzfehler beim Vorab-Check haelt den Download nicht auf', async () => {
  assert.equal(await C.downloadProblem('/x', async () => { throw new TypeError('offline'); }), null);
});

test('Bei "Zugriff verweigert" haengt die Seite ihren Hinweis an, sonst bleibt der Text wie er ist', () => {
  const plain = C.errText('denied');
  C.setErrorHint((code) => (code === 'denied' ? 'Hinweis.' : ''));
  assert.equal(C.errText('denied'), plain + ' Hinweis.');
  assert.equal(C.errText('not_found'), 'Den Pfad gibt es nicht.');
  C.setErrorHint(null);
  assert.equal(C.errText('denied'), plain);
});
