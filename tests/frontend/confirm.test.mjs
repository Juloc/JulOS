import { test } from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { confirmView, confirmReady, confirmAnswer, auditConfirmText } from '../../src/JulOS.Frontend/wwwroot/confirm.js';

setPref('de', null);

test('Stufe: Routine verlangt nichts, gefaehrlich je nach Modus Haekchen oder Namen, sehr gefaehrlich zeigt eine zweite Zeile', () => {
  const routine = confirmView({ confirmLevel: 'routine', confirmMode: 'click' }, 'srv');
  assert.deepEqual([routine.danger, routine.needsCheck, routine.typed, routine.warn], [false, false, null, null]);
  assert.ok(confirmReady(routine));

  const check = confirmView({ confirmLevel: 'dangerous', confirmMode: 'check', confirmWarn: 'reboot' }, 'srv');
  assert.ok(check.danger && check.needsCheck && !check.critical && /srv wird neu gestartet/.test(check.warn));
  assert.ok(!confirmReady(check) && confirmReady(check, { checked: true }));
  assert.deepEqual(confirmAnswer(check, { checked: true }), { acknowledged: true, typed: undefined });

  const type = confirmView({ confirmLevel: 'dangerous', confirmMode: 'type', typeToConfirm: 'srv-01' });
  assert.ok(!type.needsCheck && type.typed === 'srv-01');
  assert.ok(!confirmReady(type, { checked: true }) && !confirmReady(type, { typed: 'srv' }) && confirmReady(type, { typed: ' SRV-01 ' }));

  const click = confirmView({ confirmLevel: 'dangerous', confirmMode: 'click', confirmWarn: 'poweroff' });
  assert.ok(click.danger && !click.needsCheck && confirmReady(click));                      // Warnung bleibt, aber nur ein Klick

  const crit = confirmView({ confirmLevel: 'critical', confirmMode: 'check', confirmWarn: 'poweroff', confirmCritical: 'julos_host' });
  assert.ok(crit.critical && crit.needsCheck && /JulOS selbst/.test(crit.criticalWarn));
});

test('Stufe: aeltere Antwort ohne Felder gilt als Routine, nur ein Name ohne Stufe heisst wie frueher tippen; unbekannte Schluessel zeigen einen allgemeinen Text', () => {
  assert.equal(confirmView({}).level, 'routine');
  assert.equal(confirmView(null).level, 'routine');
  const old = confirmView({ typeToConfirm: 'srv-01' });
  assert.deepEqual([old.level, old.mode, old.typed], ['dangerous', 'type', 'srv-01']);
  assert.match(confirmView({ confirmLevel: 'dangerous', confirmMode: 'check', confirmWarn: 'gibt-es-nicht' }).warn, /Bitte genau lesen/);
  assert.equal(confirmView({ confirmLevel: 'routine', confirmMode: 'check' }).needsCheck, false);   // Routine bleibt Routine, was der Modus auch sagt
});

test('Verlauf: "Stufe:Modus" als Text, alles andere bleibt unberuehrt', () => {
  assert.match(auditConfirmText('dangerous:check'), /gefährlich.*Häkchen/);
  assert.equal(auditConfirmText('overwrite'), null);
  assert.equal(auditConfirmText(null), null);
});
