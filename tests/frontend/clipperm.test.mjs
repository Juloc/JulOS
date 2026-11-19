// Zugriff auf die Zwischenablage des Geraets (clipperm.js) mit gefaelschtem navigator, permissions und document: Zustaende granted, prompt, denied, nicht vorhanden,
// Hinweise vor und nach dem Zugriff, Rueckfall ueber execCommand, und die Zeilen der Diagnose dazu.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setPref, t } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { queryState, stateWord, browserKind, beforeReadHint, readDeviceText, copyToDevice, legacyCopy } from '../../src/JulOS.Frontend/wwwroot/clipperm.js';
import { ClipDiag } from '../../src/JulOS.Frontend/wwwroot/clipdiag.js';

setPref('de', null);

const UA = {
  chrome: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36',
  edge: 'Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/130.0 Safari/537.36 Edg/130.0',
  firefox: 'Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0',
  safari: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15',
  ios: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1',
  android: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/130.0 Mobile Safari/537.36',
};

/** Ein gefaelschter navigator: perms = { 'clipboard-read': 'granted' | ..., 'clipboard-write': ... }, ein Name, der fehlt, wirft wie in Firefox und Safari. */
function fakeNav({ ua = UA.chrome, perms = {}, text = 'aus dem Geraet', readError = null, writeError = null, api = true, noRead = false } = {}) {
  const written = [];
  const nav = {
    userAgent: ua, written,
    permissions: { query: async ({ name }) => { if (!(name in perms)) throw new TypeError('unbekannt'); return { state: perms[name] }; } },
  };
  if (api) {
    nav.clipboard = { writeText: async (v) => { if (writeError) throw writeError; written.push(v); } };
    if (!noRead) nav.clipboard.readText = async () => { if (readError) throw readError; return text; };
  }
  return nav;
}
const err = (name) => Object.assign(new Error(name), { name });
const SECURE = { isSecureContext: true }, INSECURE = { isSecureContext: false };

function fakeDoc({ focus = true, copyWorks = true } = {}) {
  const log = { appended: [], removed: 0, copied: [] };
  const doc = {
    log, body: { appendChild: (n) => log.appended.push(n) },
    hasFocus: () => focus,
    createElement: () => { const n = { style: {}, setAttribute() {}, select() {}, setSelectionRange() {}, remove() { log.removed++; } }; doc.last = n; return n; },
    execCommand: (c) => { log.copied.push([c, doc.last.value]); return copyWorks; },
  };
  return doc;
}

test('der Zustand: erlaubt, fragt nach, verweigert, und Firefox/Safari ohne clipboard-read', async () => {
  assert.deepEqual(await queryState(fakeNav({ perms: { 'clipboard-read': 'granted', 'clipboard-write': 'granted' } }), SECURE),
    { secure: true, hasApi: true, canRead: true, canWrite: true, read: 'granted', write: 'granted' });
  const prompt = await queryState(fakeNav({ perms: { 'clipboard-read': 'prompt', 'clipboard-write': 'granted' } }), SECURE);
  assert.deepEqual([prompt.read, prompt.write], ['prompt', 'granted']);
  const denied = await queryState(fakeNav({ perms: { 'clipboard-read': 'denied', 'clipboard-write': 'denied' } }), SECURE);
  assert.deepEqual([denied.read, denied.write], ['denied', 'denied']);
  const ff = await queryState(fakeNav({ ua: UA.firefox, perms: { 'clipboard-write': 'granted' } }), SECURE);     // clipboard-read wirft
  assert.deepEqual([ff.read, ff.write], ['unknown', 'granted']);
  const none = await queryState({ userAgent: UA.chrome }, SECURE);                                           // gar kein navigator.clipboard
  assert.deepEqual(none, { secure: true, hasApi: false, canRead: false, canWrite: false, read: 'unsupported', write: 'unsupported' });
  assert.equal((await queryState(fakeNav({ api: false }), INSECURE)).secure, false);
  const noperm = await queryState({ userAgent: UA.safari, clipboard: { readText: async () => '', writeText: async () => {} } }, SECURE);   // auch navigator.permissions fehlt
  assert.deepEqual([noperm.read, noperm.write], ['unknown', 'unknown']);
  const noread = await queryState(fakeNav({ noRead: true, perms: { 'clipboard-read': 'granted' } }), SECURE);
  assert.deepEqual([noread.canRead, noread.read], [false, 'unsupported']);
});

test('der Browser wird erkannt', () => {
  assert.equal(browserKind(UA.chrome), 'chromium'); assert.equal(browserKind(UA.edge), 'chromium'); assert.equal(browserKind(UA.firefox), 'firefox');
  assert.equal(browserKind(UA.safari), 'safari'); assert.equal(browserKind(UA.ios), 'ios'); assert.equal(browserKind(UA.android), 'android'); assert.equal(browserKind(''), 'other');
});

test('Lesen mit erlaubter Berechtigung: Text, kein Hinweis', async () => {
  const hints = [];
  const r = await readDeviceText({ nav: fakeNav({ perms: { 'clipboard-read': 'granted' }, text: 'Hallo' }), win: SECURE, onHint: (k) => hints.push(k) });
  assert.equal(r.text, 'Hallo'); assert.equal(r.problem, undefined); assert.deepEqual(hints, []);
});

test('Lesen bei "prompt": der Hinweis kommt VOR dem Aufruf des Browsers', async () => {
  const order = [], nav = fakeNav({ perms: { 'clipboard-read': 'prompt' }, text: 'x' });
  const read = nav.clipboard.readText; nav.clipboard.readText = async () => { order.push('readText'); return read(); };
  const r = await readDeviceText({ nav, win: SECURE, onHint: (k) => order.push(k) });
  assert.deepEqual(order, ['clipperm.hintPrompt', 'readText']);
  assert.equal(r.text, 'x');
  assert.match(t('clipperm.hintPrompt'), /fragt gleich nach Zugriff auf die Zwischenablage, bitte erlauben/);
});

test('Lesen bei "denied": kein Aufruf, Hinweis mit dem Weg zur Freigabe je Browser, Rueckfall Strg+V', async () => {
  let called = false;
  for (const [ua, key, word] of [[UA.chrome, 'deniedChromium', /Schloss-Symbol/], [UA.firefox, 'deniedFirefox', /Firefox/], [UA.safari, 'deniedSafari', /Safari/], [UA.ios, 'deniedSafari', /iPhone/], [UA.android, 'deniedAndroid', /Chrome am Handy/]]) {
    const nav = fakeNav({ ua, perms: { 'clipboard-read': 'denied' } }); nav.clipboard.readText = async () => { called = true; return 'nie'; };
    const r = await readDeviceText({ nav, win: SECURE });
    assert.equal(r.problem, 'clipperm.' + key); assert.equal(r.fallback, true); assert.equal(r.text, undefined);
    assert.match(t(r.problem), word); assert.match(t(r.problem), /Strg\+V|langer Druck/);
  }
  assert.equal(called, false);
});

test('Lesen scheitert trotz "granted" oder unbekanntem Zustand (NotAllowedError): derselbe Hinweis, sonst ein allgemeiner', async () => {
  const r = await readDeviceText({ nav: fakeNav({ ua: UA.firefox, readError: err('NotAllowedError') }), win: SECURE });
  assert.equal(r.problem, 'clipperm.deniedFirefox'); assert.equal(r.detail, 'NotAllowedError');
  const o = await readDeviceText({ nav: fakeNav({ readError: err('DataError') }), win: SECURE });
  assert.equal(o.problem, 'clipperm.readFailed'); assert.equal(o.fallback, true);
});

test('Safari und Firefox ohne Abfragemoeglichkeit: Hinweis auf die Blase "Einfuegen" vor dem Lesen', async () => {
  for (const [ua, key] of [[UA.safari, 'clipperm.hintSafariBubble'], [UA.ios, 'clipperm.hintSafariBubble'], [UA.firefox, 'clipperm.hintFirefoxBubble']]) {
    const hints = [];
    const r = await readDeviceText({ nav: fakeNav({ ua, perms: {} }), win: SECURE, onHint: (k) => hints.push(k) });
    assert.deepEqual(hints, [key]); assert.equal(r.text, 'aus dem Geraet');
  }
  assert.match(t('clipperm.hintSafariBubble'), /nur direkt nach deinem Tippen/);
});

test('leere Zwischenablage: kein Text, der Hinweis sagt es', async () => {
  const r = await readDeviceText({ nav: fakeNav({ perms: { 'clipboard-read': 'granted' }, text: '' }), win: SECURE });
  assert.equal(r.text, ''); assert.equal(r.problem, 'clipdiag.noText');
});

test('unsicherer Kontext oder keine API: Lesen sagt warum, ohne den Browser zu fragen', async () => {
  const a = await readDeviceText({ nav: fakeNav({ api: false }), win: INSECURE });
  assert.equal(a.problem, 'clipperm.hintInsecure'); assert.equal(a.fallback, true);
  assert.match(t(a.problem), /ohne HTTPS/);
  const b = await readDeviceText({ nav: fakeNav({ noRead: true }), win: SECURE });
  assert.equal(b.problem, 'clipperm.hintNoRead');
  assert.equal(beforeReadHint({ secure: true, hasApi: true, canRead: true, read: 'granted' }, 'chromium'), '');
});

test('Schreiben: API, bei Ablehnung der Rueckfall ueber execCommand, ohne Fokus gar nicht', async () => {
  const nav = fakeNav(), doc = fakeDoc();
  assert.deepEqual(await copyToDevice('Text', { nav, win: SECURE, doc }), { ok: true, how: 'api' });
  assert.deepEqual(nav.written, ['Text']); assert.equal(doc.log.copied.length, 0);

  const denied = fakeNav({ writeError: err('NotAllowedError') }), doc2 = fakeDoc();
  assert.deepEqual(await copyToDevice('B', { nav: denied, win: SECURE, doc: doc2 }), { ok: true, how: 'legacy' });
  assert.deepEqual(doc2.log.copied, [['copy', 'B']]); assert.equal(doc2.log.removed, 1);                          // das Textfeld wird wieder entfernt

  const both = await copyToDevice('C', { nav: denied, win: SECURE, doc: fakeDoc({ copyWorks: false }) });
  assert.deepEqual(both, { ok: false, reason: 'denied', detail: 'NotAllowedError' });

  const blur = await copyToDevice('D', { nav, win: SECURE, doc: fakeDoc({ focus: false }) });
  assert.deepEqual(blur, { ok: false, reason: 'nofocus' });
});

test('Schreiben im unsicheren Kontext (kein navigator.clipboard): nur der Rueckfall ueber das versteckte Textfeld', async () => {
  const doc = fakeDoc();
  assert.deepEqual(await copyToDevice('Aus der VM', { nav: fakeNav({ api: false }), win: INSECURE, doc }), { ok: true, how: 'legacy' });
  assert.deepEqual(doc.log.copied, [['copy', 'Aus der VM']]);
  const fail = await copyToDevice('x', { nav: fakeNav({ api: false }), win: INSECURE, doc: fakeDoc({ copyWorks: false }) });
  assert.deepEqual(fail, { ok: false, reason: 'insecure', detail: '' });
  assert.equal(legacyCopy('y', { createElement: () => ({}) }), false);                       // ein Dokument ohne execCommand
  const unsupported = await copyToDevice('x', { nav: { userAgent: UA.chrome }, win: SECURE, doc: fakeDoc({ copyWorks: false }) });
  assert.equal(unsupported.reason, 'unsupported');
});

test('die Diagnose nennt Kontext und Berechtigungen und den Weg zur Freigabe', async () => {
  const d = new ClipDiag(() => 1000);
  const line = (lines, id) => lines.find((l) => l.id === id);
  const ok = d.lines({ perm: await queryState(fakeNav({ perms: { 'clipboard-read': 'granted', 'clipboard-write': 'granted' } }), SECURE), kind: 'chromium' });
  assert.equal(line(ok, 'browser').ok, true);
  assert.equal(line(ok, 'browser').text, 'Browser: sicherer Kontext (HTTPS oder localhost); Lesen: erlaubt, Schreiben: erlaubt');
  assert.equal(line(ok, 'browserHint'), undefined);

  const prompt = d.lines({ perm: await queryState(fakeNav({ perms: { 'clipboard-read': 'prompt', 'clipboard-write': 'granted' } }), SECURE), kind: 'chromium' });
  assert.match(line(prompt, 'browser').text, /Lesen: fragt beim ersten Mal nach/); assert.match(line(prompt, 'browserHint').text, /bitte erlauben/);

  const denied = d.lines({ perm: await queryState(fakeNav({ perms: { 'clipboard-read': 'denied', 'clipboard-write': 'granted' } }), SECURE), kind: 'firefox' });
  assert.equal(line(denied, 'browser').ok, false); assert.match(line(denied, 'browserHint').text, /Firefox/);

  const writeDenied = d.lines({ perm: await queryState(fakeNav({ perms: { 'clipboard-read': 'granted', 'clipboard-write': 'denied' } }), SECURE), kind: 'chromium' });
  assert.match(line(writeDenied, 'browserHint').text, /Schloss-Symbol/);

  const insecure = d.lines({ perm: await queryState(fakeNav({ api: false }), INSECURE), kind: 'chromium' });
  assert.equal(line(insecure, 'browser').ok, false);
  assert.match(line(insecure, 'browser').text, /unsicherer Kontext \(kein HTTPS\); Lesen: nicht vorhanden, Schreiben: nicht vorhanden/);
  assert.match(line(insecure, 'browserHint').text, /ohne HTTPS/);

  const unknown = d.lines({ perm: await queryState(fakeNav({ ua: UA.safari, perms: {} }), SECURE), kind: 'safari' });
  assert.match(line(unknown, 'browser').text, /Lesen: unbekannt/);
  assert.equal(stateWord('granted'), 'erlaubt');
});
