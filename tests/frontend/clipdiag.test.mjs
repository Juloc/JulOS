// Zwischenablage-Diagnose (clipdiag.js): Zustand und Zeilen, ohne Oberflaeche. Die Zahlen der Ereignisse muessen mit ClipNote im Server uebereinstimmen.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { ClipDiag, NOTE, CHANNEL_WAIT_MS, ago } from '../../src/JulOS.Frontend/wwwroot/clipdiag.js';

setPref('de', null);

function clock(start = 1_000_000) { const c = { t: start, now: () => c.t, tick(ms) { c.t += ms; } }; return c; }
const text = (lines, id) => lines.find((l) => l.id === id);

test('die Zahlen der Ereignisse sind die von ClipNote im Server', () => {
  const src = readFileSync(new URL('../../src/JulOS.Integrations/Display/ClipboardBridge.cs', import.meta.url), 'utf8');
  const body = src.slice(src.indexOf('public enum ClipNote'), src.indexOf('}', src.indexOf('public enum ClipNote')));
  const server = Object.fromEntries([...body.matchAll(/^\s+(\w+) = (\d+),/gm)].map((m) => [m[1][0].toLowerCase() + m[1].slice(1), Number(m[2])]));
  assert.deepEqual(NOTE, server);
});

test('ago: Sekunden, Minuten, Stunden', () => {
  assert.equal(ago(0), 'vor 0 s');
  assert.equal(ago(12_400), 'vor 12 s');
  assert.equal(ago(5 * 60_000), 'vor 5 min');
  assert.equal(ago(3 * 3600_000), 'vor 3 h');
});

test('der Kanal: erst "wird aufgebaut", dann bereit; ohne Meldung nach einigen Sekunden "fehlt" mit Hinweis auf xrdp-chansrv', () => {
  const c = clock(), d = new ClipDiag(c.now);
  assert.match(text(d.lines(), 'channel').text, /wird aufgebaut/);
  assert.equal(text(d.lines(), 'channel').ok, null);
  c.tick(CHANNEL_WAIT_MS + 1);
  assert.equal(text(d.lines(), 'channel').ok, false);
  assert.match(text(d.lines(), 'channel').text, /xrdp-chansrv/);
  d.serverNote({ ready: true, files: false, note: NOTE.status, count: 0 });
  assert.equal(text(d.lines(), 'channel').ok, true);
  assert.match(text(d.lines(), 'channel').text, /bereit \(Text und Bilder\)/);
  d.serverNote({ ready: true, files: true, note: NOTE.status, count: 0 });
  assert.match(text(d.lines(), 'channel').text, /Dateien/);
  d.connected();                                  // neue Verbindung: der Kanal muss sich neu melden
  assert.equal(text(d.lines(), 'channel').ok, null);
});

test('aus der VM: angekommen und abgelegt, verweigert (mit Grund), ohne Fokus, ohne HTTPS', () => {
  const c = clock(), d = new ClipDiag(c.now);
  assert.match(text(d.lines(), 'fromVm').text, /noch nichts kopiert/);
  d.vmText(340, 'stored'); c.tick(12_000);
  assert.deepEqual(text(d.lines(), 'fromVm'), { id: 'fromVm', ok: true, text: 'Aus der VM: vor 12 s, 340 Zeichen, im Browser abgelegt' });
  assert.match(text(d.lines(), 'error').text, /keiner/);

  d.vmText(5, 'denied', 'NotAllowedError');
  assert.equal(text(d.lines(), 'fromVm').ok, false);
  assert.match(text(d.lines(), 'fromVm').text, /verweigert \(NotAllowedError\)/);
  assert.match(text(d.lines(), 'error').text, /Letzter Fehler \(vor 0 s\): Der Browser hat das Ablegen verweigert \(NotAllowedError\)/);

  d.vmText(5, 'nofocus');
  assert.match(text(d.lines(), 'fromVm').text, /keinen Fokus/);
  d.vmText(5, 'insecure');
  assert.match(text(d.lines(), 'error').text, /HTTPS/);
  assert.equal(text(d.lines({ secure: false }), 'browser').ok, false);
  assert.equal(text(d.lines({ secure: true }), 'browser').ok, true);
});

test('in die VM: gesendet, abgeholt, noch nicht abgeholt, Kanal nicht bereit, nicht gesendet', () => {
  const c = clock(), d = new ClipDiag(c.now);
  assert.match(text(d.lines(), 'toVm').text, /noch nichts gesendet/);
  d.sentText(52, 'sent'); c.tick(3000);
  assert.equal(text(d.lines(), 'toVm').ok, null);
  assert.match(text(d.lines(), 'toVm').text, /vor 3 s, 52 Zeichen gesendet; die VM hat ihn noch nicht abgeholt \(dort Strg\+V/);
  d.serverNote({ ready: true, files: false, note: NOTE.textFetchedByVm, count: 52 });
  assert.equal(text(d.lines(), 'toVm').ok, true);
  assert.match(text(d.lines(), 'toVm').text, /abgeholt$/);

  d.sentText(7, 'sent');
  d.serverNote({ ready: false, files: false, note: NOTE.textWaiting, count: 7 });
  assert.match(text(d.lines(), 'toVm').text, /noch nicht bereit/);

  d.sentText(7, 'offline');
  assert.equal(text(d.lines(), 'toVm').ok, false);
  assert.match(text(d.lines(), 'error').text, /Keine Verbindung zur VM/);
  d.sentText(2_000_000, 'tooLong');
  assert.match(text(d.lines(), 'error').text, /zu lang .*höchstens 1 MB/);
});

test('Fehler des Servers stehen im Klartext in der Diagnose', () => {
  const d = new ClipDiag(clock().now);
  d.serverNote({ ready: true, files: false, note: NOTE.textFromVmLost, count: 0 });
  assert.match(text(d.lines(), 'error').text, /hat den kopierten Text nicht geliefert/);
  d.serverNote({ ready: true, files: false, note: NOTE.textFromVmTooLong, count: 3_000_000 });
  assert.match(text(d.lines(), 'error').text, /aus der VM ist zu lang/);
  d.serverNote({ ready: true, files: false, note: NOTE.listRefused, count: 0 });
  assert.match(text(d.lines(), 'error').text, /abgelehnt/);
});

test('sendProblem: leer, wenn alles gut ist', () => {
  assert.equal(ClipDiag.sendProblem('sent'), '');
  assert.equal(ClipDiag.sendProblem('empty'), '');
  assert.match(ClipDiag.sendProblem('offline'), /Keine Verbindung/);
  assert.match(ClipDiag.sendProblem('tooLong'), /zu lang/);
});

test('die Diagnose enthaelt nie einen Inhalt: nur Zahlen und feste Texte', () => {
  const c = clock(), d = new ClipDiag(c.now);
  d.vmText(9, 'stored'); d.sentText(9, 'sent');
  const all = JSON.stringify(d);
  assert.ok(!/geheim/i.test(all));
  assert.deepEqual(Object.keys(d.fromVm).sort(), ['at', 'chars', 'detail', 'outcome']);
  assert.deepEqual(Object.keys(d.toVm).sort(), ['at', 'chars', 'fetchedAt', 'result', 'waiting']);
});
