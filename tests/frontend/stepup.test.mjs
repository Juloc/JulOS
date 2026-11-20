import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isStepUp, withStepUp, cleanCode, initStepUp } from '../../src/JulOS.Frontend/wwwroot/stepup.js';

const need = () => Object.assign(new Error('x'), { status: 403, code: 'step_up_required' });

test('nur 403 mit dem Code step_up_required ist die Aufforderung zur Freigabe', () => {
  assert.ok(isStepUp(need()));
  assert.ok(!isStepUp(Object.assign(new Error('x'), { status: 403, code: 'control_off' })));
  assert.ok(!isStepUp(Object.assign(new Error('x'), { status: 401, code: 'step_up_required' })));
  assert.ok(!isStepUp(null) && !isStepUp(new Error('x')));
});

test('withStepUp: fragt einmal, wiederholt einmal, und lässt alles andere unveraendert durch', async () => {
  let runs = 0, asks = 0;
  assert.equal(await withStepUp(async () => 'ok', async () => { asks++; return true; }), 'ok');
  assert.equal(asks, 0);

  const flaky = async () => { runs++; if (runs === 1) throw need(); return 'danach'; };
  assert.equal(await withStepUp(flaky, async () => { asks++; return true; }), 'danach');
  assert.deepEqual([runs, asks], [2, 1]);

  runs = 0; asks = 0;                                                     // abgebrochen: der Fehler bleibt, nichts wird wiederholt
  await assert.rejects(withStepUp(async () => { runs++; throw need(); }, async () => { asks++; return false; }), (e) => isStepUp(e));
  assert.deepEqual([runs, asks], [1, 1]);

  runs = 0; asks = 0;                                                     // verlangt der Server nach der Freigabe noch einmal eine, gibt es keine Endlosschleife
  await assert.rejects(withStepUp(async () => { runs++; throw need(); }, async () => { asks++; return true; }), (e) => isStepUp(e));
  assert.deepEqual([runs, asks], [2, 1]);

  const other = Object.assign(new Error('weg'), { status: 404 });          // andere Fehler fragen nie nach einem Code
  await assert.rejects(withStepUp(async () => { throw other; }, async () => { asks++; return true; }), (e) => e === other);
  assert.equal(asks, 1);
});

test('cleanCode entfernt Leerzeichen, wie die App den Code zeigt', () => {
  assert.equal(cleanCode(' 123 456 '), '123456');
  assert.equal(cleanCode(null), '');
});

function fakeEl() {
  const l = {};
  return { value: '', open: false, listeners: l, addEventListener(t, f) { (l[t] ||= []).push(f); }, fire(t, e = {}) { for (const f of l[t] || []) f(e); },
    showModal() { this.open = true; }, close() { this.open = false; this.fire('close'); }, focus() {}, select() {}, querySelectorAll: () => [] };
}

test('das Sheet: falscher Code bleibt offen mit Meldung, richtiger Code (ohne Leerzeichen gesendet) gibt frei, Abbrechen und Escape melden false, gleichzeitige Fragen teilen sich ein Sheet', async () => {
  const els = { '#stepUpDlg': fakeEl(), '#suForm': fakeEl(), '#suCode': fakeEl(), '#suMsg': fakeEl(), '#suCancel': fakeEl() };
  globalThis.document = { querySelector: (s) => els[s] };
  const sent = [];
  const msgs = [];
  const ask = initStepUp({
    api: async (path, o) => { sent.push([path, o.body.code]); if (o.body.code !== '123456') throw new Error('Der Code stimmt nicht.'); return {}; },
    setMsg: (_el, text) => msgs.push(text),
  });
  const submit = async (code) => { els['#suCode'].value = code; els['#suForm'].fire('submit', { preventDefault() {} }); await new Promise((r) => setImmediate(r)); };

  const first = ask(), second = ask();
  assert.equal(first, second);
  assert.ok(els['#stepUpDlg'].open);
  await submit('000 000');
  assert.ok(els['#stepUpDlg'].open, 'bei falschem Code bleibt das Sheet offen');
  assert.ok(msgs.includes('Der Code stimmt nicht.'));
  await submit('123 456');
  assert.equal(await first, true);
  assert.ok(!els['#stepUpDlg'].open);
  assert.deepEqual(sent, [['/api/auth/step-up', '000000'], ['/api/auth/step-up', '123456']]);

  const cancelled = ask();
  els['#suCancel'].fire('click');
  assert.equal(await cancelled, false);
  const escaped = ask();
  els['#stepUpDlg'].close();                                               // Escape oder daneben tippen
  assert.equal(await escaped, false);
  assert.notEqual(ask(), first);                                           // danach gibt es wieder ein neues Sheet
  delete globalThis.document;
});
