import { test } from 'node:test';
import assert from 'node:assert/strict';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { parseList, listText, peerListed, clientText, recoveryInfo, keyInfo, auditLine, securityMarkup, initSecurity, MINUTES, minutesText, sourceText } from '../../src/JulOS.Frontend/wwwroot/security.js';

setPref('de', null);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const STATE = {
  proxies: { entries: [], source: 'default' }, hosts: { entries: [], source: 'env' }, proxiesUnset: true,
  client: { address: '203.0.113.9', peer: '192.0.2.10', forwarded: true },
  stepUp: { enabled: true, minutes: 10 },
  backupKey: { copyKey: false, source: 'default', wrapped: false, canProtect: true, recovery: { state: 'none', replacing: false } },
};
const UI = (o = {}) => ({ proxyText: '', hostText: '', step: null, blocked: null, blockedAsk: false, proxyAsk: '', recoveryKey: '', audit: [], msg: {}, ...o });

test('Listen: Eingabe in Zeilen, Kommas und Leerzeichen, und zurueck', () => {
  assert.deepEqual(parseList('192.0.2.10, 192.0.2.0/24\n\n pve.example;'), ['192.0.2.10', '192.0.2.0/24', 'pve.example']);
  assert.deepEqual(parseList(''), []);
  assert.equal(listText(['a', 'b']), 'a\nb');
  assert.ok(peerListed(['192.0.2.10/32'], '192.0.2.10') && peerListed(['192.0.2.10'], '192.0.2.10') && !peerListed(['192.0.2.11'], '192.0.2.10'));
});

test('Texte: woher der Wert kommt, wie der Zugriff gerade kommt, wie lange eine Freigabe gilt', () => {
  assert.equal(sourceText('app'), 'aus der App');
  assert.equal(sourceText('env'), 'aus der Umgebung des Servers');
  assert.ok(sourceText('default').includes('nichts eingestellt'));
  assert.equal(clientText({ forwarded: true, address: 'A', peer: 'P' }), 'Dein Zugriff kommt gerade von Adresse A über Proxy P.');
  assert.ok(clientText({ forwarded: false, address: 'A' }).includes('ohne Proxy'));
  assert.deepEqual(MINUTES.filter((m) => m < 1 || m > 60), [0]);          // "immer fragen" und 1 bis 60 Minuten
  assert.equal(minutesText(0), 'Bei jeder Aktion fragen');
  assert.equal(minutesText(10), '10 Minuten');
});

test('Schluessel im Backup und Wiederherstellungsschluessel: ein Satz mit Ton und naechstem Schritt', () => {
  assert.equal(recoveryInfo({ state: 'none' }, true).tone, 'warn');
  assert.ok(recoveryInfo({ state: 'pending' }, true).next.includes('bestätige'));
  assert.equal(recoveryInfo({ state: 'confirmed', confirmedAt: '2026-11-20T10:00:00Z' }, true).tone, 'ok');
  assert.ok(recoveryInfo({ state: 'none' }, false).line.includes('nichts zu sichern'));
  const on = keyInfo({ copyKey: true, source: 'legacy' });
  assert.ok(on.warn && on.line.includes('mit ins Backup') && on.line.includes('so war es bisher'));
  const off = keyInfo({ copyKey: false, source: 'default' });
  assert.ok(!off.warn && off.line.includes('nur auf dem Server'));
  assert.equal(auditLine({ at: '2026-11-20T10:00:00Z', user: 'jul', event: 'hosts.set', detail: 'a' }).what, 'Erlaubte Zielhosts geändert');
  assert.equal(auditLine({ at: '2026-11-20T10:00:00Z', user: 'jul', event: 'neu.ereignis' }).what, 'neu.ereignis');
});

test('Seite: Warnung bei leerer Proxy-Liste, Hinweis auf den Proxy dieses Zugriffs, Gefahrentexte, nichts Geheimes ohne Anlass', () => {
  const html = securityMarkup(STATE, UI(), esc);
  assert.ok(html.includes('Weder in der App noch in der Umgebung'));
  assert.ok(html.includes('über Proxy 192.0.2.10'));
  assert.ok(html.includes('data-sec="addpeer"') && html.includes('data-peer="192.0.2.10"'));
  assert.ok(html.includes('aus der Umgebung des Servers'), 'Quelle der Zielhosts');
  assert.ok(html.includes('Leer heißt: keine Einschränkung'));
  assert.ok(!html.includes('secRecKey') && !html.includes('data-sec="confirm-key"'));
  assert.ok(html.includes('data-sec="new-key"'));
  // gesetzt und der Proxy steht drin: keine Warnung, kein Eintrags-Knopf
  const set = securityMarkup({ ...STATE, proxiesUnset: false, proxies: { entries: ['192.0.2.10/32'], source: 'app' } }, UI({ proxyText: '192.0.2.10/32' }), esc);
  assert.ok(!set.includes('Weder in der App') && !set.includes('data-sec="addpeer"'));
  // zweiter Faktor aus: Warnung, Auswahl gesperrt; Schluessel mit ins Backup: Warnung
  const off = securityMarkup(STATE, UI({ step: { enabled: false, minutes: 10 }, copyKey: true }), esc);
  assert.ok(off.includes('Ausgeschaltet') && off.includes('id="secStepMin" disabled'));
  assert.ok(off.includes('alle gespeicherten Passwörter öffnen'));
  // der Wiederherstellungsschluessel erscheint nur, wenn er gerade erzeugt wurde, mit Kopieren, Laden und Bestaetigen
  const key = securityMarkup(STATE, UI({ recoveryKey: 'ABCD-EFGH' }), esc);
  assert.ok(key.includes('ABCD-EFGH') && key.includes('data-sec="copy-key"') && key.includes('data-sec="download-key"') && key.includes('data-sec="confirm-key"'));
  assert.ok(!key.includes('data-sec="new-key"'));
  assert.ok(securityMarkup(null, { error: 'Nicht erreichbar' }, esc).includes('Nicht erreichbar'));
});

test('Seite: gesperrte Verbindungen werden genannt, Speichern nur nach ausdruecklicher Bestaetigung; Eingaben und Namen sind escaped', () => {
  const ui = UI({ hostText: 'neu.example', blocked: [{ name: '<b>Büro</b>', host: 'alt.example' }], blockedAsk: true });
  const html = securityMarkup(STATE, ui, esc);
  assert.ok(html.includes('1 gespeicherte Verbindung(en) wären danach gesperrt'));
  assert.ok(html.includes('&lt;b&gt;Büro&lt;/b&gt;') && !html.includes('<b>Büro</b>'));
  assert.ok(html.includes('data-sec="save-hosts-confirm"'));
  assert.ok(!securityMarkup(STATE, UI({ blocked: ui.blocked, blockedAsk: false }), esc).includes('save-hosts-confirm'));   // nur eine Pruefung: keine Bestaetigung
  assert.ok(securityMarkup(STATE, UI({ blocked: [] }), esc).includes('Keine gespeicherte Verbindung wäre gesperrt'));
  const evil = securityMarkup(STATE, UI({ proxyText: '"><script>x</script>' }), esc);
  assert.ok(!evil.includes('<script>'));
  const ask = securityMarkup(STATE, UI({ proxyAsk: '192.0.2.10' }), esc);
  assert.ok(ask.includes('data-sec="save-proxies-confirm"') && ask.includes('über 192.0.2.10'));
});

function fakeEl() {
  const l = {};
  return { listeners: l, innerHTML: '', hidden: true, open: false, addEventListener(t, f) { (l[t] ||= []).push(f); }, fire(t, e) { for (const f of l[t] || []) f(e); },
    showModal() { this.open = true; }, close() { this.open = false; this.fire('close'); }, replaceChildren() { this.innerHTML = ''; } };
}
const click = (el, sec, extra = {}) => el.fire('click', { target: { closest: () => ({ dataset: { sec, ...extra } }) } });
const settle = () => new Promise((r) => setTimeout(r, 5));

test('Ablauf: Zielhosts speichern verlangt nach einer Sperre die Bestaetigung und sendet sie erst dann; Wiederherstellungsschluessel wird nach dem Schliessen vergessen', async () => {
  const els = { '#securityDlg': fakeEl(), '#secBody': fakeEl() };
  globalThis.document = { querySelector: (s) => els[s] };
  const calls = [];
  let blockedNow = true;
  const api = async (path, o = {}) => {
    calls.push([o.method || 'GET', path, o.body]);
    if (path === '/api/security') return STATE;
    if (path.startsWith('/api/security/audit')) return { entries: [] };
    if (path === '/api/security/hosts' && o.method === 'PUT') {
      if (blockedNow && !o.body.confirm) throw Object.assign(new Error('gesperrt'), { status: 409, code: 'blocked_connections', data: { blocked: [{ name: 'Büro', host: 'alt.example' }] } });
      return { entries: o.body.entries, source: 'app' };
    }
    if (path === '/api/security/recovery') return { key: 'ABCD-EFGH-IJKL' };
    return {};
  };
  const sec = initSecurity({ api, esc });
  sec.open();
  await settle();
  const body = els['#secBody'];
  assert.ok(body.innerHTML.includes('Vertraute Proxy-Adressen'));

  body.fire('input', { target: { id: 'secHosts', value: 'neu.example' } });
  click(body, 'save-hosts');
  await settle();
  assert.ok(body.innerHTML.includes('wären danach gesperrt') && body.innerHTML.includes('save-hosts-confirm'));
  assert.equal(calls.filter(([m, p]) => m === 'PUT' && p === '/api/security/hosts').length, 1);
  assert.equal(calls.find(([m, p]) => m === 'PUT' && p === '/api/security/hosts')[2].confirm, undefined);

  click(body, 'save-hosts-confirm');
  await settle();
  const puts = calls.filter(([m, p]) => m === 'PUT' && p === '/api/security/hosts');
  assert.equal(puts.length, 2);
  assert.deepEqual(puts[1][2], { entries: ['neu.example'], confirm: true });

  click(body, 'new-key');
  await settle();
  assert.ok(body.innerHTML.includes('ABCD-EFGH-IJKL'));
  els['#securityDlg'].close();                                             // schliessen: der Schluessel ist weg, nichts bleibt im Dokument
  assert.ok(!body.innerHTML.includes('ABCD-EFGH-IJKL'));
  delete globalThis.document;
});
