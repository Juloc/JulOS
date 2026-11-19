// Diagnose von Ton und Mikrofon (audiodiag.js): Zeilen und Hinweise aus dem Zustand von StreamAudio, ohne Oberflaeche.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { audioLines, micPermission, micDeniedHint, micBeforeHint, SILENCE_WAIT_MS } from '../../src/JulOS.Frontend/wwwroot/audiodiag.js';

setPref('de', null);

/** Ein StreamAudio-Ersatz mit den Feldern, die die Diagnose liest. */
const audio = (over = {}) => ({
  supported: true, micSupported: true, wanted: true, active: true, state: 'running', micOn: false, micLevel: 0, micChunks: 0, createdAt: 0,
  remote: { output: true, microphone: false, recording: false, micRate: 16000 },
  stats: { blocks: 40, ignored: 0, received: 76800, peak: 0.37, bufferedMs: 62, targetMs: 60, underruns: 0, dropped: 0 },
  ...over,
});
const find = (lines, re) => lines.find((l) => re.test(l.text));

test('VNC und eine Sitzung ohne Ton: eine klare Zeile, sonst nichts', () => {
  const vnc = audioLines({ audio: audio(), vnc: true });
  assert.equal(vnc.length, 1);
  assert.equal(vnc[0].ok, false);
  assert.match(vnc[0].text, /VNC.*kein Mikrofon/);
  assert.match(audioLines({ audio: null })[0].text, /keinen Ton/);
});

test('der Ton laeuft: Kanal da, Wiedergabe laeuft, Pegel in Prozent mit Puffer und Zaehlern', () => {
  const lines = audioLines({ audio: audio(), now: 5000 });
  assert.deepEqual(find(lines, /Ton-Kanal/), { ok: true, text: 'Ton-Kanal des Desktops: vorhanden' });
  assert.equal(find(lines, /Wiedergabe/).text, 'Wiedergabe: läuft');
  assert.equal(find(lines, /Wiedergabe/).ok, true);
  assert.match(find(lines, /Vom Desktop angekommen/).text, /40 Stücke \(76800 Samples\)/);
  assert.match(find(lines, /Pegel/).text, /Pegel 37 %, Puffer 62 ms \(Ziel 60 ms\), Aussetzer 0, verworfen 0/);
});

test('ohne Eingabe des Benutzers: Wiedergabe wartet, und der Text sagt, was zu tun ist', () => {
  const l = find(audioLines({ audio: audio({ state: 'suspended' }) }), /Wiedergabe/);
  assert.equal(l.ok, false);
  assert.match(l.text, /Seite antippst oder eine Taste/);
});

test('ausgeschaltet und im Hintergrund sind Hinweise, keine Fehler', () => {
  assert.equal(find(audioLines({ audio: audio({ wanted: false }) }), /Wiedergabe/).ok, null);
  const bg = find(audioLines({ audio: audio({ active: false }) }), /Wiedergabe/);
  assert.equal(bg.ok, null);
  assert.match(bg.text, /pausiert/);
});

test('der Desktop meldet keinen Ton-Kanal: Fehlerzeile mit Verweis auf die Doku', () => {
  const l = find(audioLines({ audio: audio({ remote: { output: false, microphone: false, recording: false } }) }), /Ton-Kanal/);
  assert.equal(l.ok, false);
  assert.match(l.text, /docs\/AUDIO\.md/);
});

test('kommt nach der Wartezeit nichts an, sagt die Diagnose es; vorher nicht', () => {
  const quiet = audio({ stats: { blocks: 3, ignored: 3, received: 0, peak: 0, bufferedMs: 0, targetMs: 60, underruns: 0, dropped: 0 } });
  assert.equal(find(audioLines({ audio: quiet, now: SILENCE_WAIT_MS - 1 }), /xrdp-sink/), undefined);
  const late = find(audioLines({ audio: quiet, now: SILENCE_WAIT_MS + 1 }), /xrdp-sink/);
  assert.equal(late.ok, null, 'stilles Desktop ist kein Fehler, nur ein Hinweis');
  assert.match(late.text, /normal/);
  assert.equal(find(audioLines({ audio: audio({ active: false, ...{ stats: quiet.stats } }), now: SILENCE_WAIT_MS + 1 }), /xrdp-sink/), undefined, 'ein Hintergrund-Tab bekommt absichtlich nichts');
});

test('Mikrofon: Kanal, an/aus, nimmt auf; ohne sicheren Kontext nicht moeglich', () => {
  assert.match(find(audioLines({ audio: audio() }), /Mikrofon des Desktops/).text, /noch nicht offen \(xrdp öffnet ihn erst/);
  assert.equal(find(audioLines({ audio: audio() }), /^Mikrofon: /).text, 'Mikrofon: aus');
  const waiting = audio({ micOn: true, remote: { output: true, microphone: true, recording: false } });
  assert.match(find(audioLines({ audio: waiting }), /^Mikrofon: /).text, /wartet auf ein Programm/);
  assert.equal(find(audioLines({ audio: waiting }), /Mikrofon des Desktops/).ok, true);
  const rec = audio({ micOn: true, micLevel: 0.5, micChunks: 120, remote: { output: true, microphone: true, recording: true } });
  const r = find(audioLines({ audio: rec }), /^Mikrofon: /);
  assert.equal(r.ok, true);
  assert.match(r.text, /nimmt auf\. Pegel 50 %, 120 Stücke gesendet/);
  assert.equal(find(audioLines({ audio: audio(), secure: false }), /^Mikrofon: /).ok, false);
  assert.match(find(audioLines({ audio: audio(), secure: false }), /^Mikrofon: /).text, /https oder localhost/);
});

test('Freigabe verweigert: der Hinweis haengt vom Browser ab', () => {
  const denied = (kind) => find(audioLines({ audio: audio(), micPerm: 'denied', kind }), /Mikrofon-Freigabe/);
  assert.equal(denied('chromium').ok, false);
  assert.match(denied('chromium').text, /Schloss oder Regler/);
  assert.match(denied('firefox').text, /durchgestrichene Mikrofon/);
  assert.match(denied('safari').text, /aA/);
  assert.match(denied('ios').text, /Einstellungen, Safari, Mikrofon/);
  assert.match(denied('android').text, /Android-Einstellungen/);
  assert.match(denied('other').text, /Einstellungen der Seite/);
  assert.equal(find(audioLines({ audio: audio(), micPerm: 'granted' }), /Mikrofon-Freigabe/).ok, true);
  assert.match(find(audioLines({ audio: audio(), micPerm: 'prompt' }), /Mikrofon-Freigabe/).text, /fragt beim Einschalten/);
  assert.match(find(audioLines({ audio: audio(), micPerm: 'unknown' }), /Mikrofon-Freigabe/).text, /nennt sie nicht/);
});

test('micPermission: Zustand der Abfrage, unbekannt wenn der Browser sie nicht kennt oder wirft', async () => {
  assert.equal(await micPermission({ permissions: { query: async ({ name }) => { assert.equal(name, 'microphone'); return { state: 'denied' }; } } }), 'denied');
  assert.equal(await micPermission({ permissions: { query: async () => ({ state: 'weird' }) } }), 'unknown');
  assert.equal(await micPermission({ permissions: { query: async () => { throw new TypeError('kein microphone'); } } }), 'unknown');
  assert.equal(await micPermission({}), 'unknown');
});

test('Hinweis vor dem Einschalten nur, wenn der Browser gleich fragt', () => {
  assert.match(micBeforeHint('prompt'), /Zulassen/);
  assert.equal(micBeforeHint('granted'), '');
  assert.equal(micBeforeHint('denied'), '');
  assert.ok(micDeniedHint('chromium').length > 20);
});
