// Der Login eines Hosts (docs/HOSTS.md): die Karte, die Anfrage zum Speichern, was eine Verbindung benutzt und die Frage der Dateien-App. Reine Logik, kein Browser noetig.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import { loginPanel, loginRequest, loginUser, loginSaved, usesHostLogin, hostHasLogin, loginState, connectionBadge, savedText } from '../../src/JulOS.Frontend/wwwroot/hostlogin.js';
import { PANELS, unavailable } from '../../src/JulOS.Frontend/wwwroot/hostview.js';
import { auditLine } from '../../src/JulOS.Frontend/wwwroot/hostactions.js';
import * as C from '../../src/JulOS.Frontend/wwwroot/files-core.js';

setPref('de', null);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const host = { id: 'h1', name: 'Server' };
const row = (o = {}) => ({ id: 'c1', name: 'Terminal', kind: 'terminal', address: 'srv-a:22', username: 'jul', source: 'host', ownLogin: false, addressNew: false, ...o });

test('Verbindung: Benutzer und Passwort kommen vom Host-Login, sonst von der Verbindung (aeltere Antworten ohne die Felder auch)', () => {
  assert.equal(loginUser({ username: 'alt', loginUser: 'neu' }), 'neu');
  assert.equal(loginUser({ username: 'alt' }), 'alt');
  assert.equal(loginUser({ username: '', loginUser: '' }), '');
  assert.equal(loginSaved({ hasPassword: true, loginPassword: false }), false);       // das eigene Passwort zaehlt nicht, wenn der Host-Login gilt und keins hat
  assert.equal(loginSaved({ hasPassword: false, loginPassword: true }), true);
  assert.equal(loginSaved({ hasPassword: true }), true);
  assert.equal(usesHostLogin({ loginSource: 'host' }), true);
  assert.equal(usesHostLogin({ loginSource: 'own' }), false);
  assert.equal(hostHasLogin({ loginUser: 'jul' }), true);
  assert.equal(hostHasLogin({}), false);
  assert.equal(hostHasLogin(undefined), false);
});

test('Anfrage: leeres Passwort laesst das gespeicherte stehen, der Haken entfernt es, ein Passwort ersetzt es', () => {
  assert.deepEqual(loginRequest({ user: ' jul ', password: '', noPassword: false }), { user: 'jul' });
  assert.deepEqual(loginRequest({ user: 'jul', password: '', noPassword: true }), { user: 'jul', forgetPassword: true });
  assert.deepEqual(loginRequest({ user: 'jul', password: 'geheim', noPassword: true }), { user: 'jul', password: 'geheim' });
});

test('Karte: das Passwortfeld ist nie vorbelegt, der Benutzer steht drin, alles vom Server ist maskiert', () => {
  const html = loginPanel({ host, esc, login: { resp: { user: 'ju"><img src=x onerror=alert(1)>', hasPassword: true, canStore: true, connections: [row({ name: '<b>x</b>' })] } } });
  assert.match(html, /id="hostLoginPw"[^>]*type="password"|type="password"[^>]*id="hostLoginPw"/);
  assert.doesNotMatch(html.match(/<input[^>]*id="hostLoginPw"[^>]*>/)[0], /value=/);                 // nie vorbelegt
  assert.match(html, /autocomplete="new-password"/);
  assert.doesNotMatch(html, /<img src=x/);
  assert.doesNotMatch(html, /<b>x<\/b>/);
  assert.match(html, /Leer lassen, um das gespeicherte zu behalten/);
  assert.match(html, /data-hl-save/); assert.match(html, /data-hl-remove/);
  assert.match(html, /Passwort nicht speichern/);
  assert.doesNotMatch(html, /onsubmit|onclick/);                                                       // die CSP erlaubt keine Inline-Behandler
});

test('Karte: der Hinweis zu Root steht da, und die Karte verspricht kein Root-Passwort', () => {
  const html = loginPanel({ host, esc, login: { resp: { user: 'jul', hasPassword: false, canStore: true, connections: [] } } });
  assert.match(html, /Für Root-Aktionen brauchst du hier kein Passwort: Der Agent erledigt sie\. Ein Root-Terminal öffnest du mit sudo -i\./);
  assert.doesNotMatch(html, /root-passwort|zweiter Zugang/i);
  setPref('en', null);
  try { assert.match(loginPanel({ host, esc, login: { resp: { user: 'jul', hasPassword: false, canStore: true, connections: [] } } }), /For root actions you do not need a password here: the agent handles them\. Open a root terminal with sudo -i\./); }
  finally { setPref('de', null); }
});

test('Karte: ohne Login ein Vorschlag aus den Verbindungen (nichts wird von allein gesetzt), ohne Schluessel ein Hinweis und kein Passwortfeld zum Tippen', () => {
  const fresh = loginPanel({ host, esc, login: { resp: { user: null, hasPassword: false, canStore: true, suggestion: 'jul', connections: [row({ source: 'own' })] } } });
  assert.match(fresh, /data-hl-suggest="jul"/);
  assert.match(fresh, /Gesetzt wird nichts von allein/);
  assert.doesNotMatch(fresh, /data-hl-remove/);                                                        // nichts zu entfernen
  assert.match(loginState({ user: null }), /noch keinen Login/);
  const nokey = loginPanel({ host, esc, login: { resp: { user: 'jul', hasPassword: false, canStore: false, connections: [] } } });
  assert.match(nokey, /Schlüssel fehlt/);
  assert.match(nokey.match(/<input[^>]*id="hostLoginPw"[^>]*>/)[0], /disabled/);
  assert.match(loginState({ user: 'jul', hasPassword: true }), /Passwort gespeichert/);
  assert.match(loginState({ user: 'jul', hasPassword: false }), /kein Passwort gespeichert/);
});

test('Karte: jede Verbindung sagt, was sie benutzt; eine neue Adresse bekommt das Passwort erst nach dem erneuten Speichern', () => {
  assert.equal(connectionBadge(row()).text, 'nutzt den Host-Login');
  assert.equal(connectionBadge(row({ source: 'own', username: 'root', ownLogin: true })).text, 'eigener Benutzer „root“');
  assert.equal(connectionBadge(row({ source: 'own', username: '' })).text, 'eigener Login');
  assert.equal(connectionBadge(row({ source: 'own', addressNew: true })).tone, 'warn');
  const html = loginPanel({ host, esc, login: { resp: { user: 'jul', hasPassword: true, canStore: true, connections: [row(), row({ id: 'c2', name: 'Fremd', source: 'own', addressNew: true, address: 'neu.example:22' })] } } });
  assert.match(html, /nutzt den Host-Login/);
  assert.match(html, /neue Adresse: Passwort hier neu speichern/);
  assert.match(html, /speicherst du es hier erneut/);
  assert.equal(savedText({ pinned: 0 }), 'Login gespeichert.');
  assert.match(savedText({ pinned: 2 }), /Login gespeichert\. Verbindungen mit einem anderen Benutzer behalten ihren eigenen Login: 2\./);
});

test('Abschnitt Login: jeder Host hat ihn, auch ohne Agent; vor der Antwort steht ein Ladehinweis, ein Fehler wird gezeigt', () => {
  const p = PANELS.find((x) => x.id === 'login');
  assert.ok(p);
  assert.equal(unavailable({ id: 'h', name: 'x' }, p, esc), '');                                      // kein Agent noetig
  assert.match(p.html({ host, esc, login: { resp: null } }), /Wird gelesen/);
  assert.match(p.html({ host, esc, login: { resp: null, error: 'Kein Zugriff' } }), /Kein Zugriff/);
});

test('Verlauf: "Host-Login geaendert" steht ohne Wert da, nur mit dem, was geaendert wurde', () => {
  const line = auditLine({ at: new Date().toISOString(), user: 'jul', action: 'login.set', target: null, event: 'ok', detail: 'user+password' });
  assert.match(line.text, /Host-Login geändert/);
  assert.equal(line.detail, 'Benutzer und Passwort');
  assert.match(auditLine({ at: new Date().toISOString(), user: 'jul', action: 'login.remove', event: 'ok' }).text, /Host-Login entfernt/);
});

const apiErr = (code, data = {}) => Object.assign(new Error(code), { code, data });
const place = { id: 'c1', label: 'Server', address: 'jul@srv-a' };

test('Dateien: hat der Host einen Benutzer ohne Passwort, bekommt der Dialog den Benutzer und darf "fuer diesen Host speichern" anbieten', async () => {
  const calls = [];
  const f = { open: async (id, o) => { calls.push(o); if (!o.password) throw apiErr('password_required', { user: 'hostuser', canSave: true }); return { home: '/h', hostKey: 'known' }; } };
  const asked = [];
  const r = await C.openPlace(f, place, { password: async (p, wrong, hostInfo) => { asked.push(hostInfo); return { password: 'geheim', save: true }; }, hostKey: async () => true });
  assert.equal(r.home, '/h');
  assert.deepEqual(asked, [{ user: 'hostuser', canSave: true }]);
  assert.deepEqual(calls[1], { password: 'geheim', acceptHostKey: null, saveForHost: true });
});

test('Dateien: ohne Haken oder ohne Angebot wird nie gespeichert, und die alte Antwort (nur Text) geht weiter', async () => {
  const calls = [];
  const f = { open: async (id, o) => { calls.push(o); if (!o.password) throw apiErr('password_required', { user: 'hostuser', canSave: true }); return { home: '/h' }; } };
  await C.openPlace(f, place, { password: async () => ({ password: 'x', save: false }), hostKey: async () => true });
  assert.equal('saveForHost' in calls[1], false);
  calls.length = 0;
  const g = { open: async (id, o) => { calls.push(o); if (!o.password) throw apiErr('password_required'); return { home: '/h' }; } };
  await C.openPlace(g, place, { password: async (p, w, hostInfo) => { assert.equal(hostInfo, null); return { password: 'y', save: true }; }, hostKey: async () => true });
  assert.equal('saveForHost' in calls[1], false);                                                      // ohne Angebot des Servers kein Speichern, auch wenn die Seite es behauptet
  calls.length = 0;
  await C.openPlace(g, place, { password: async () => 'z', hostKey: async () => true });
  assert.deepEqual(calls[1], { password: 'z', acceptHostKey: null });
});

test('Dateien: ein Ort zeigt, ob ein Passwort bereitliegt (auch das des Hosts), und den Benutzer, der gilt', () => {
  const places = C.buildPlaces({ info: { local: false }, hosts: [{ id: 'h1', name: 'Server' }], connections: [{ id: 'c1', kind: 'terminal', name: 'T', hostId: 'h1', host: 'srv-a', port: 22, username: 'alt', hasPassword: false, loginUser: 'hostuser', loginPassword: true }] });
  const p = places.flat[0];
  assert.equal(p.hasPassword, true);
  assert.equal(p.address, 'hostuser@srv-a');
});

test('Neue Dateien sind im Cache der App und im Quelltext nirgends ein Inline-Behandler', () => {
  const www = new URL('../../src/JulOS.Frontend/wwwroot/', import.meta.url);
  assert.match(fs.readFileSync(new URL('sw.js', www), 'utf8'), /'\/hostlogin\.js'/);
  assert.doesNotMatch(fs.readFileSync(new URL('hostlogin.js', www), 'utf8'), /onsubmit=|onclick=|javascript:/);
});
