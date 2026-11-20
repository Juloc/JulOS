// Dateien-App (files-core.js, docs/FILES.md): Pfade, Orte, Eintraege, Fehlertexte, die Aufrufe, Ort oeffnen mit Passwort und Host-Schluessel, Auftraege, die Saetze der Bestaetigungen. Reine Logik, kein DOM.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { setPref } from '../../src/JulOS.Frontend/wwwroot/i18n.js';
import * as C from '../../src/JulOS.Frontend/wwwroot/files-core.js';
import de from '../../src/JulOS.Frontend/wwwroot/i18n/de.js';
import en from '../../src/JulOS.Frontend/wwwroot/i18n/en.js';

setPref('de', null);
const remote = { id: 'c1', kind: 'ssh', label: 'Server A', name: 'Server A', address: 'jul@srv-a', hasPassword: true };
const other = { id: 'c2', kind: 'ssh', label: 'Server B', name: 'Server B', address: 'jul@srv-b', hasPassword: false };
const local = { id: 'local', kind: 'local', label: 'Gemeinsamer Ordner' };

test('Pfade: Eltern, Verbinden und Brotkruemel fuer Hosts (absolut) und den gemeinsamen Ordner (relativ)', () => {
  assert.equal(C.parentOf('/a/b', remote), '/a');
  assert.equal(C.parentOf('/a', remote), '/');
  assert.equal(C.parentOf('/', remote), '/');
  assert.equal(C.parentOf('a/b', local), 'a');
  assert.equal(C.parentOf('a', local), '');
  assert.equal(C.joinPath('/', 'x', remote), '/x');
  assert.equal(C.joinPath('/a', 'x', remote), '/a/x');
  assert.equal(C.joinPath('', 'x', local), 'x');
  assert.equal(C.joinPath('a', 'x', local), 'a/x');
  assert.equal(C.nameOf('/a/b.txt'), 'b.txt');
  assert.deepEqual(C.crumbsOf('/a/b', remote).map((c) => [c.label, c.path]), [['/', '/'], ['a', '/a'], ['b', '/a/b']]);
  assert.deepEqual(C.crumbsOf('x/y', local).map((c) => c.path), ['', 'x', 'x/y']);
  assert.deepEqual(C.crumbsOf('/', remote).map((c) => c.path), ['/']);
});

test('neue Namen wie am Server: einfach, ohne Schraegstrich und Steuerzeichen, hoechstens 255 Byte', () => {
  for (const ok of ['a.txt', 'Ünï ✓', 'a b']) assert.ok(C.validName(ok), ok);
  for (const bad of ['', '  ', '.', '..', 'a/b', 'a\nb', 'a\u0000', 'ä'.repeat(128), 5, null]) assert.ok(!C.validName(bad), String(bad));
  assert.ok(C.validName('a'.repeat(255)));
  assert.ok(!C.validName('a'.repeat(256)));
});

test('Orte: nur SSH-Verbindungen, nach Host gruppiert, mehrere SSH-Verbindungen eines Hosts mit Auswahl', () => {
  const hosts = [{ id: 'h1', name: 'Server A' }, { id: 'h2', name: 'Nur Desktop' }, { id: 'h3', name: 'Server C' }];
  const connections = [
    { id: 'c1', kind: 'terminal', name: 'Shell', hostId: 'h1', host: 'srv-a', username: 'jul', hasPassword: true },
    { id: 'c2', kind: 'desktop', name: 'RDP', hostId: 'h2', host: 'srv-b', username: 'jul' },
    { id: 'c3', kind: 'vnc', name: 'VNC', hostId: 'h2', host: 'srv-b', username: 'jul' },
    { id: 'c4', kind: 'terminal', name: 'Claude', hostId: 'h1', host: 'srv-a', username: 'jul', hasPassword: false },
    { id: 'c5', kind: 'terminal', name: 'C', hostId: 'h3', host: 'srv-c', username: '', hasPassword: false },
    { id: 'c6', kind: 'terminal', name: 'Lose', hostId: 'gibtsnicht', host: 'x' },
  ];
  const p = C.buildPlaces({ connections, hosts, info: { local: true } });
  assert.equal(p.local.label, 'Gemeinsamer Ordner');
  assert.deepEqual(p.groups.map((g) => g.name), ['Server A', 'Server C', 'Lose']);                 // Hosts ohne SSH (nur RDP/VNC) fehlen
  assert.deepEqual(p.flat.map((x) => x.id), ['c1', 'c4', 'c5', 'c6']);
  assert.ok(!p.flat.some((x) => ['c2', 'c3'].includes(x.id)));
  assert.deepEqual(p.groups[0].places.map((x) => x.label), ['Server A · Shell', 'Server A · Claude']);   // Auswahl bei mehreren
  assert.equal(p.groups[1].places[0].label, 'Server C');                                              // ein Eintrag: so heisst der Ort wie der Host
  assert.equal(p.flat[0].address, 'jul@srv-a');
  assert.equal(p.groups[1].places[0].address, 'srv-c');
  assert.equal(C.findPlace(p, 'local').id, 'local');
  assert.equal(C.findPlace(p, 'c4').name, 'Claude');
  assert.equal(C.findPlace(p, 'nope'), null);
  assert.equal(C.buildPlaces({ connections, hosts, info: { local: false } }).local, null);
  assert.deepEqual(C.buildPlaces().flat, []);
});

test('Eintraege: beide Quellen in eine Form, Meldungen zu Dateien, Ordnern, Links', () => {
  const l = C.normalizeListing(local, { path: 'a', truncated: true, entries: [{ name: 'd', path: 'a/d', isDirectory: true, size: null, modified: '2026-11-19T10:00:00Z', items: 3 }, { name: 'f', path: 'a/f', isDirectory: false, size: 2048, modified: '2026-11-19T10:00:00Z' }] });
  assert.deepEqual([l.entries[0].kind, l.entries[0].openable, l.entries[1].kind, l.truncated], ['directory', true, 'file', true]);
  assert.match(C.entryMeta(l.entries[0]), /^3 Einträge · /);
  assert.match(C.entryMeta(l.entries[1]), /^2,0 KiB · /);
  const r = C.normalizeListing(remote, { path: '/x', hidden: 2, entries: [{ name: 'l', path: '/x/l', kind: 'link', size: 0, modified: '2026-11-19T10:00:00Z', mode: 'rwxrwxrwx', resolved: 'directory', openable: true }, { name: 'k', path: '/x/k', kind: 'link', size: 0, modified: '2026-11-19T10:00:00Z', mode: 'rwxrwxrwx', openable: false }, { name: 's', path: '/x/s', kind: 'other', size: 0, modified: '2026-11-19T10:00:00Z', mode: '---------', openable: false }] });
  assert.equal(r.hidden, 2);
  assert.match(C.entryMeta(r.entries[0]), /^Link auf einen Ordner/);
  assert.match(C.entryMeta(r.entries[1]), /^Link ohne Ziel/);
  assert.match(C.entryMeta(r.entries[2]), /^Sonderdatei/);
  assert.ok(C.isFileLike({ kind: 'file' }) && C.isFileLike({ kind: 'link', resolved: 'file' }));
  assert.ok(!C.isFileLike({ kind: 'link', resolved: 'directory' }) && !C.isFileLike({ kind: 'link' }) && !C.isFileLike({ kind: 'directory' }));
  assert.equal(C.fileSize(500), '500 B');
  assert.equal(C.fileSize(5 * 1024 ** 3), '5,0 GiB');
});

test('Auswahl: umschalten, alles, und nur was noch in der Liste steht bleibt ausgewaehlt', () => {
  const s = new C.Selection();
  const entries = [{ path: '/a' }, { path: '/b' }, { path: '/c' }];
  assert.equal(s.toggle('/a'), true);
  assert.equal(s.toggle('/a'), false);
  s.selectAll(entries);
  assert.equal(s.size, 3);
  assert.deepEqual(s.entries([{ path: '/c' }, { path: '/a' }]).map((e) => e.path), ['/c', '/a']);   // Reihenfolge der Liste
  assert.equal(s.size, 2);
  s.only('/z'); assert.deepEqual(s.paths(), ['/z']);
  s.clear(); assert.equal(s.size, 0);
});

test('Fehlertexte: jeder Code des Servers hat einen Schluessel in Deutsch und Englisch, Unbekanntes wird allgemein gemeldet', () => {
  const src = fs.readFileSync(new URL('../../src/JulOS.Frontend/Files/FileErrors.cs', import.meta.url), 'utf8');
  const serverCodes = [...src.matchAll(/\["([a-z_]+)"\] = \(\d+,/g)].map((m) => m[1]);
  assert.ok(serverCodes.length > 30);
  for (const code of serverCodes) {
    assert.ok(C.ERR_KEYS[code], 'kein Schluessel fuer ' + code);
    assert.ok(de[C.ERR_KEYS[code]] && en[C.ERR_KEYS[code]], 'Text fehlt: ' + code);
  }
  assert.equal(C.errText('exists'), 'Es gibt diesen Namen dort schon.');
  assert.equal(C.errText('gibt_es_nicht'), de['files.err.failed']);
  assert.equal(C.errorMessage({ code: 'denied', message: 'x' }), de['files.err.denied']);
  assert.equal(C.errorMessage({ message: 'Netz weg' }), 'Netz weg');
  assert.equal(C.errorMessage({ code: 'unbekannt', message: 'Text vom Server' }), 'Text vom Server');
  setPref('en', null);
  assert.equal(C.errText('exists'), en['files.err.exists']);
  setPref('de', null);
});

/** Ein api() das alle Aufrufe aufzeichnet und antwortet */
function stubApi(answer = () => ({})) {
  const calls = [];
  const api = async (path, o = {}) => { calls.push([o.method || 'GET', path, o.body === undefined ? undefined : JSON.parse(JSON.stringify(o.body))]); const r = await answer(path, o, calls); return r; };
  return { api, calls };
}

test('Aufrufe: lesen mit Ort und Pfad (gequotet), Schreiben in zwei Schritten (prepare, dann die Aktion mit dem Token), Host-zu-Host mit Ziel', async () => {
  const { api, calls } = stubApi((path) => (path.includes('/list') ? { path: '/a b', entries: [] } : {}));
  const f = C.makeFilesApi(api);
  await f.list(remote, '/a b/ä&x');
  assert.deepEqual(calls[0], ['GET', '/api/files/remote/list?c=c1&path=%2Fa%20b%2F%C3%A4%26x', undefined]);
  await f.list(local, 'x/y');
  assert.equal(calls[1][1], '/api/files/list?path=x%2Fy');
  assert.equal(f.downloadUrl(remote, '/a/b c'), '/api/files/remote/download?c=c1&path=%2Fa%2Fb%20c');
  assert.equal(f.downloadUrl(local, 'a/b'), '/api/files/download?path=a%2Fb');
  await f.text(remote, '/t.txt');
  assert.equal(calls[2][1], '/api/files/remote/text?c=c1&path=%2Ft.txt');
  await f.prepare('mkdir', remote, { dir: '/a', name: 'n' });
  await f.prepare('delete', remote, { paths: ['/a/x'] });
  await f.prepare('copy', remote, { paths: ['/a/x'], target: { place: other, dir: '/dst' } });
  await f.prepare('upload', remote, { dir: '/a', names: ['a.txt', 'b.txt'] });
  await f.mkdir(remote, '/a', 'n', 'T1');
  await f.rename(remote, '/a/x', 'y', true, 'T2');
  await f.remove(remote, ['/a/x'], true, 'T3');
  await f.copy(remote, ['/a/x'], { place: other, dir: '/dst' }, { move: true, overwrite: true, token: 'T4' });
  const writes = calls.slice(3);
  assert.deepEqual(writes.map((c) => [c[0], c[1]]), [['POST', '/api/files/remote/prepare'], ['POST', '/api/files/remote/prepare'], ['POST', '/api/files/remote/prepare'], ['POST', '/api/files/remote/prepare'],
    ['POST', '/api/files/remote/mkdir'], ['POST', '/api/files/remote/rename'], ['POST', '/api/files/remote/delete'], ['POST', '/api/files/remote/copy']]);
  // Schritt 1: Aktion und Felder, nie ein Token; Schritt 2: das Token statt eines Flags "confirm"
  assert.deepEqual(writes[0][2], { action: 'mkdir', connection: 'c1', dir: '/a', name: 'n' });
  assert.deepEqual(writes[2][2], { action: 'copy', connection: 'c1', paths: ['/a/x'], target: { connection: 'c2', dir: '/dst' } });
  assert.deepEqual(writes[3][2], { action: 'upload', connection: 'c1', dir: '/a', names: ['a.txt', 'b.txt'] });
  assert.deepEqual(writes[4][2], { connection: 'c1', dir: '/a', name: 'n', token: 'T1' });
  assert.deepEqual(writes[5][2], { connection: 'c1', path: '/a/x', name: 'y', overwrite: true, token: 'T2' });
  assert.deepEqual(writes[6][2], { connection: 'c1', paths: ['/a/x'], recursive: true, token: 'T3' });
  assert.deepEqual(writes[7][2], { connection: 'c1', paths: ['/a/x'], target: { connection: 'c2', dir: '/dst' }, move: true, overwrite: true, token: 'T4' });
  for (const c of writes) assert.equal('confirm' in c[2], false, c[1]);
  await f.job('j1'); await f.cancel('j1'); await f.audit(5);
  assert.deepEqual(calls.slice(11).map((c) => [c[0], c[1]]), [['GET', '/api/files/remote/jobs/j1'], ['POST', '/api/files/remote/jobs/j1/cancel'], ['GET', '/api/files/audit?limit=5']]);
});

/** Ein Ersatz fuer XMLHttpRequest */
function fakeXhr(respond) {
  const x = { headers: {}, upload: {}, open(m, u) { x.method = m; x.url = u; }, setRequestHeader(k, v) { x.headers[k] = v; }, abort() { x.onabort(); },
    send(file) { x.sent = file; setTimeout(() => { x.upload.onprogress({ lengthComputable: true, loaded: 5, total: 10 }); respond(x); }, 0); } };
  return x;
}

test('Hochladen: roher Koerper mit Sonderkopfzeile, Fortschritt, Fehler mit Code, Abbruch', async () => {
  let x;
  const f = C.makeFilesApi(async () => ({}), { createXhr: () => (x = fakeXhr((r) => { r.status = 200; r.responseText = '{"path":"/d/a.txt","bytes":10,"replaced":false}'; r.onload(); })) });
  const seen = [];
  const file = { name: 'ä b.txt', size: 10 };
  const up = f.upload(remote, '/d', file, { overwrite: true, token: 'T9', onProgress: (a, b) => seen.push([a, b]) });
  assert.deepEqual(await up.promise, { path: '/d/a.txt', bytes: 10, replaced: false });
  assert.equal(x.method, 'POST');
  assert.equal(x.url, '/api/files/remote/upload?c=c1&dir=%2Fd&name=%C3%A4%20b.txt&token=T9&overwrite=1');
  assert.equal(x.headers['X-JulOS'], '1');
  assert.equal(x.sent, file);
  assert.deepEqual(seen, [[5, 10]]);

  const g = C.makeFilesApi(async () => ({}), { createXhr: () => fakeXhr((r) => { r.status = 409; r.responseText = '{"error":"x","code":"exists"}'; r.onload(); }) });
  const err = await g.upload(remote, '/d', file).promise.catch((e) => e);
  assert.deepEqual([err.code, err.status], ['exists', 409]);

  const h = C.makeFilesApi(async () => ({}), { createXhr: () => fakeXhr(() => {}) });
  const u = h.upload(remote, '/d', file);
  u.abort();
  assert.equal((await u.promise.catch((e) => e)).code, 'cancelled');
  const net = C.makeFilesApi(async () => ({}), { createXhr: () => fakeXhr((r) => r.onerror()) });
  assert.equal((await net.upload(remote, '/d', file).promise.catch((e) => e)).code, 'connection_lost');
});

const apiErr = (code, data = {}) => Object.assign(new Error(code), { code, data });

test('Ort oeffnen: ein neuer oder geaenderter Host-Schluessel gilt nie still, ein Passwort geht nur an den einen Aufruf', async () => {
  const key = { algorithm: 'ssh-ed25519', fingerprint: 'SHA256:abc', known: 'ssh-ed25519 SHA256:old' };
  const seq = [apiErr('host_key_new', key), apiErr('password_required'), { home: '/home/jul', hostKey: 'remembered' }];
  const calls = [];
  const f = { open: async (id, o) => { calls.push({ id, ...o }); const r = seq.shift(); if (r instanceof Error) throw r; return r; } };
  const asked = [];
  const r = await C.openPlace(f, other, { hostKey: async (p, d, changed) => { asked.push(['key', p.id, d.fingerprint, changed]); return true; }, password: async (p, wrong) => { asked.push(['pw', p.id, wrong]); return 'geheim'; } });
  assert.equal(r.home, '/home/jul');
  assert.deepEqual(asked, [['key', 'c2', 'SHA256:abc', false], ['pw', 'c2', false]]);
  assert.deepEqual(calls[0], { id: 'c2', password: null, acceptHostKey: null });                      // erst ohne alles
  assert.deepEqual(calls[1], { id: 'c2', password: null, acceptHostKey: 'ssh-ed25519 SHA256:abc' });   // der bestaetigte Schluessel, genau dieser
  assert.deepEqual(calls[2], { id: 'c2', password: 'geheim', acceptHostKey: 'ssh-ed25519 SHA256:abc' });

  // Abgelehnt: nichts wird weitergeschickt
  const n = [];
  const refuse = { open: async (id, o) => { n.push(o); throw apiErr('host_key_changed', key); } };
  const e1 = await C.openPlace(refuse, remote, { hostKey: async (p, d, changed) => { assert.equal(changed, true); return false; }, password: async () => 'x' }).catch((e) => e);
  assert.ok(C.isCancelled(e1));
  assert.equal(n.length, 1);
  // Passwort abgebrochen
  const e2 = await C.openPlace({ open: async () => { throw apiErr('password_required'); } }, other, { password: async () => null, hostKey: async () => true }).catch((e) => e);
  assert.ok(C.isCancelled(e2));
  // Falsches Passwort: es wird erneut gefragt, mit dem Hinweis
  let tries = 0; const wrongs = [];
  const retry = { open: async (id, o) => { if (o.password !== 'richtig') throw apiErr(tries++ ? 'logon_failed' : 'password_required'); return { home: '/', hostKey: 'known' }; } };
  const pws = ['falsch', 'richtig'];
  const ok = await C.openPlace(retry, other, { password: async (p, wrong) => { wrongs.push(wrong); return pws.shift(); }, hostKey: async () => true });
  assert.equal(ok.home, '/');
  assert.deepEqual(wrongs, [false, true]);
  // Andere Fehler gehen unveraendert durch (Host nicht erreichbar)
  const e3 = await C.openPlace({ open: async () => { throw apiErr('unreachable'); } }, remote, { password: async () => 'x', hostKey: async () => true }).catch((e) => e);
  assert.equal(e3.code, 'unreachable');
  // Ein Schluessel-Fehler ohne Fingerabdruck wird nicht bestaetigt
  const e4 = await C.openPlace({ open: async () => { throw apiErr('host_key_new', {}); } }, remote, { password: async () => 'x', hostKey: async () => { assert.fail('darf nicht gefragt werden'); } }).catch((e) => e);
  assert.equal(e4.code, 'host_key_new');
  // Kein Endlos-Kreisel
  const loop = await C.openPlace({ open: async () => { throw apiErr('password_required'); } }, remote, { password: async () => 'x', hostKey: async () => true }).catch((e) => e);
  assert.equal(loop.code, 'failed');
});

test('Auftraege: abfragen bis fertig, Fortschritt melden, nach Fehlern weiter fragen, 404 beendet, Abbruch geht an den Server', async () => {
  const states = [{ state: 'running', done: 1, total: 4, bytes: 10, bytesTotal: 100 }, { state: 'running', done: 2, total: 4 }, { state: 'done', done: 4, total: 4, result: { files: 3, directories: 1 }, skipped: 0 }];
  let n = 0; const cancelled = [];
  const f = { job: async () => { if (n === 1) { n++; throw Object.assign(new Error('x'), { status: 0 }); } return states[n++ === 0 ? 0 : n - 2 > 1 ? 2 : 1]; }, cancel: async (id) => { cancelled.push(id); } };
  const seen = []; const sleeps = [];
  const w = C.watchJob(f, 'j9', { onProgress: (j) => seen.push(j.state), sleep: async (ms) => { sleeps.push(ms); } });
  const job = await w.promise;
  assert.equal(job.state, 'done');
  assert.ok(seen.length >= 2 && seen.at(-1) === 'done');
  assert.ok(sleeps.every((ms) => ms === 700));
  await w.cancel();
  assert.deepEqual(cancelled, ['j9']);

  const gone = C.watchJob({ job: async () => { throw Object.assign(new Error('weg'), { status: 404 }); } }, 'x', { sleep: async () => {} });
  assert.equal((await gone.promise.catch((e) => e)).status, 404);
  const dead = C.watchJob({ job: async () => { throw Object.assign(new Error('netz'), { status: 0 }); } }, 'x', { sleep: async () => {}, maxErrors: 3 });
  assert.equal((await dead.promise.catch((e) => e)).message, 'netz');
  assert.ok(C.jobFinal({ state: 'failed' }) && !C.jobFinal({ state: 'running' }) && !C.jobFinal(null));
  assert.equal(C.jobPercent({ bytes: 50, bytesTotal: 200 }), 25);
  assert.equal(C.jobPercent({ done: 1, total: 4, bytesTotal: 0 }), 25);
  assert.equal(C.jobPercent({ done: 0, total: 0 }), null);
  assert.equal(C.jobPercent({ bytes: 500, bytesTotal: 200 }), 100);
});

test('Ergebnis eines Auftrags: was geschah, uebersprungene Links, Fehler als Text', () => {
  assert.equal(C.jobSummary({ state: 'done', result: { files: 3, directories: 1 }, skipped: 0 }, 'copy'), 'Kopiert: 4 Elemente.');
  assert.equal(C.jobSummary({ state: 'done', result: { files: 1, directories: 0 }, skipped: 2, skippedExisting: 1 }, 'move'), 'Verschoben: 1 Element. 2 Links oder Sonderdateien wurden nicht angefasst. 1 vorhandene Datei blieb unverändert.');
  assert.equal(C.jobSummary({ state: 'done', result: { files: 2, directories: 0 } }, 'delete'), 'Gelöscht: 2 Elemente.');
  assert.match(C.jobSummary({ state: 'failed', error: 'not_empty' }, 'delete'), /Fehlgeschlagen: Der Ordner ist nicht leer/);
  assert.match(C.jobSummary({ state: 'cancelled' }, 'copy'), /Abgebrochen/);
});

const prep = (over = {}) => ({ token: 'TOK', expiresIn: 60, action: 'delete', host: 'Server A', targetHost: null, paths: ['/home/jul/projekt', '/home/jul/x.txt'], dir: null, name: null, names: null, files: 3, directories: 2, links: 1, bytes: 2048, conflicts: [], nonEmpty: true, ...over });

test('Bestaetigung Loeschen: alles vom Server; nennt Host, Pfad und Anzahl; das Haekchen kommt nur, wenn der Server es verlangt; kein Papierkorb', () => {
  const m = C.deleteModel(prep({ confirmLevel: 'dangerous', confirmMode: 'check', confirmWarn: 'delete_recursive' }));
  assert.match(m.sentence, /Server A/);
  assert.match(m.sentence, /\/home\/jul\/projekt/);
  assert.deepEqual(m.rows.map((r) => r[0]), ['Host', 'Pfad', 'Umfang']);
  assert.match(m.rows[2][1], /3 Dateien, 2 Ordner, 1 Link · 2,0 KiB/);
  assert.match(m.rows[1][1], /und 1 weitere/);
  assert.ok(m.ack && m.recursive && m.danger);
  assert.match(m.levelWarn, /samt allem darin/);
  assert.equal(C.deleteModel(prep({ confirmLevel: 'dangerous', confirmMode: 'click' })).ack, null);           // "nur Klick": Warnung, aber kein Haekchen
  const simple = C.deleteModel(prep({ paths: ['/a/x.txt'], files: 1, directories: 0, links: 0, bytes: 3, nonEmpty: false }));
  assert.deepEqual([simple.ack, simple.recursive, simple.levelWarn], [null, false, null]);
  assert.match(simple.note, /keinen Papierkorb/);
  assert.match(simple.rows[2][1], /1 Datei · 3 B/);
});

test('Bestaetigung Kopieren und Verschieben: Quelle, Ziel (auch ein anderer Host), Anzahl; Ersetzen nur mit Haekchen', () => {
  const base = prep({ action: 'copy', paths: ['/home/jul/a', '/home/jul/b'], dir: '/srv/ziel', targetHost: 'Server B', files: 2, directories: 0, links: 0, bytes: 10, nonEmpty: false });
  const copy = C.copyModel('copy', base);
  assert.match(copy.sentence, /Server A/); assert.match(copy.sentence, /Server B/); assert.match(copy.sentence, /\/srv\/ziel/);
  assert.match(copy.note, /JulOS-Server/);                                                  // laeuft ueber den Server
  assert.ok(!copy.ack && !copy.overwrite);
  assert.equal(C.copyModel('copy', { ...base, targetHost: null, dir: '/x' }).note, null);
  const clash = C.copyModel('move', { ...base, conflicts: ['a', 'b', 'c', 'd', 'e', 'f'] });
  assert.ok(!clash.ack && clash.overwrite && clash.danger);                                // ohne Stufe vom Server kein Haekchen: das Ersetzen ist nur angezeigt
  assert.ok(C.copyModel('move', { ...base, conflicts: ['a'], root: true, confirmLevel: 'dangerous', confirmMode: 'check', confirmWarn: 'overwrite_root' }).ack);
  assert.match(clash.warning, /\(6\)/);
  assert.match(clash.warning, /a, b, c, d, e …/);
  assert.equal(clash.title, 'Verschieben');
});

test('Bestaetigung Hochladen, Umbenennen, Ordner: Host und Pfad stehen drin, Ersetzen braucht das Haekchen', () => {
  const files = [{ name: 'a.txt', size: 1024 }, { name: 'b.txt', size: 2048 }];
  const fresh = C.uploadModel(prep({ action: 'upload', dir: '/home/jul', names: ['a.txt', 'b.txt'], paths: [] }), files);
  assert.deepEqual([fresh.ack, fresh.danger], [null, false]);
  assert.match(fresh.sentence, /2 Dateien nach Server A/);
  assert.match(fresh.rows[2][1], /2 Dateien · 3,0 KiB/);
  const clash = C.uploadModel(prep({ action: 'upload', dir: '/home/jul', names: ['a.txt', 'b.txt'], paths: [], conflicts: ['a.txt'] }), files);
  assert.ok(!clash.ack && clash.danger && clash.overwrite);
  assert.match(clash.warning, /a\.txt/);
  const ren = C.renameModel(prep({ action: 'rename', paths: ['/home/jul/a.txt'], name: 'b.txt', conflicts: ['b.txt'] }));
  assert.ok(!ren.ack && ren.danger);
  assert.match(ren.sentence, /Server A/); assert.match(ren.sentence, /b\.txt/);
  assert.equal(C.renameModel(prep({ action: 'rename', paths: ['/home/jul/a.txt'], name: 'z.txt' })).ack, null);
  const mk = C.mkdirModel(prep({ action: 'mkdir', dir: '/home/jul', name: 'neu', paths: [] }));
  assert.equal(mk.confirm, 'Anlegen');
  assert.match(mk.sentence, /neu/);
  assert.equal(C.pathsText(['/x'.padEnd(120, 'y')]).length, 70);                          // lange Pfade werden gekuerzt, das Ende bleibt
  assert.ok(C.pathsText(['/x'.padEnd(120, 'y')]).startsWith('…'));
});

test('Verlauf: eine Zeile mit Aktion, Pfad, Ergebnis und Zusatz; Unbekanntes wird allgemein gemeldet, nie roh', () => {
  const l = C.auditLine({ at: '2026-11-19T10:05:00Z', host: 'Server A', action: 'delete', path: '/x', result: 'failed', detail: 'not_empty', items: 0 });
  assert.deepEqual([l.action, l.result, l.failed, l.detail, l.items], ['Gelöscht', 'fehlgeschlagen', true, 'Der Ordner ist nicht leer.', '0 Elemente']);
  assert.deepEqual(['gestartet', false], [C.auditLine({ at: '2026-11-19T10:05:00Z', host: 'h', action: 'delete', result: 'started' }).result, C.auditLine({ at: '2026-11-19T10:05:00Z', host: 'h', action: 'delete', result: 'ok' }).failed === true]);
  assert.equal(C.auditLine({ at: '2026-11-19T10:05:00Z', host: 'h', action: 'upload', result: 'ok', detail: 'overwrite' }).detail, 'mit Ersetzen');
  const odd = C.auditLine({ at: '2026-11-19T10:05:00Z', host: 'h', action: '<img onerror=x>', result: '??', detail: '<b>' });
  assert.equal(odd.action, 'Aktion'); assert.equal(odd.result, 'fehlgeschlagen'); assert.equal(odd.detail, '');
});

test('die Dateien-App ist im Service Worker und die Texte stehen in Deutsch und Englisch', () => {
  const sw = fs.readFileSync(new URL('../../src/JulOS.Frontend/wwwroot/sw.js', import.meta.url), 'utf8');
  for (const f of ['/files-app.js', '/files-core.js', '/files.css', '/desktop-files.js']) assert.ok(sw.includes(`'${f}'`), f);
  const html = fs.readFileSync(new URL('../../src/JulOS.Frontend/wwwroot/index.html', import.meta.url), 'utf8');
  assert.ok(html.includes('href="files.css"'));
  assert.equal(de['mobile.files'], 'Dateien'); assert.equal(en['mobile.files'], 'Files');
});

test('Root: der Ort kommt nur, wenn Agent online, Stufe full, Wort files-root erlaubt und Agent ab 0.8.0', () => {
  const agent = (o = {}) => ({ online: true, control: 'full', allow: ['files-root'], version: '0.8.0', ...o });
  assert.equal(C.rootAvailable(agent()), true);
  assert.equal(C.rootAvailable(agent({ version: '0.10.1' })), true);
  for (const bad of [undefined, null, agent({ online: false }), agent({ control: 'updates' }), agent({ control: 'read' }), agent({ allow: ['updates'] }), agent({ allow: undefined }), agent({ version: '0.7.9' }), agent({ version: '' }), agent({ version: 'x' })]) {
    assert.equal(C.rootAvailable(bad), false, JSON.stringify(bad));
  }
  const hosts = [{ id: 'h1', name: 'Server A', agent: agent() }, { id: 'h2', name: 'Server B', agent: agent({ allow: [] }) }, { id: 'h3', name: 'Server C', agent: agent({ online: false }) }];
  const connections = [{ id: 'c1', kind: 'terminal', name: 'Shell', hostId: 'h1', host: 'srv-a', username: 'jul', hasPassword: true }];
  const p = C.buildPlaces({ connections, hosts });
  assert.deepEqual(p.flat.map((x) => [x.id, x.kind, x.label]), [['c1', 'ssh', 'Server A'], ['h1', 'root', 'Server A (Root)']]);   // nur Server A hat den Root-Ort; Host ohne SSH-Verbindung mit Root bekaeme nur diesen
  assert.equal(C.findPlace(p, 'h1').kind, 'root');
  assert.ok(C.isRoot(p.flat[1]) && !C.isRoot(p.flat[0]) && !C.isLocal(p.flat[1]));
  const only = C.buildPlaces({ hosts: [hosts[0]] });
  assert.deepEqual(only.flat.map((x) => x.label), ['Server A (Root)']);                                                         // auch ohne SSH-Verbindung
  assert.equal(C.rootOf(p.flat[1]), '/');
});

test('Root: Lesen ist eine bestaetigte Aktion (prepare, dann Token an text und Download), jeder Dialog nennt Root und ist rot', async () => {
  const root = { id: 'h1', kind: 'root', label: 'Server A (Root)', name: 'Server A' };
  const { api, calls } = stubApi();
  const f = C.makeFilesApi(api);
  await f.prepare('text', root, { paths: ['/etc/hosts'] });
  assert.deepEqual(calls[0], ['POST', '/api/files/remote/prepare', { action: 'text', connection: 'h1', paths: ['/etc/hosts'] }]);
  await f.text(root, '/etc/hosts', 'tok');
  assert.equal(calls[1][1], '/api/files/remote/text?c=h1&path=%2Fetc%2Fhosts&token=tok');
  assert.equal(f.downloadUrl(root, '/etc/a b', 'tok'), '/api/files/remote/download?c=h1&path=%2Fetc%2Fa%20b&token=tok');
  assert.equal(f.downloadUrl(remote, '/x'), '/api/files/remote/download?c=c1&path=%2Fx');                                      // ein SSH-Ort: kein Token
  const rp = { host: 'Server A (Root)', paths: ['/etc/shadow'], files: 1, directories: 0, links: 0, bytes: 900, root: true, conflicts: [] };
  const m = C.readModel('download', rp);
  assert.ok(m.root && m.rootNote && m.danger);
  assert.match(m.sentence, /Server A \(Root\)/); assert.match(m.sentence, /\/etc\/shadow/); assert.match(m.sentence, /als root/);
  assert.match(C.readModel('text', rp).title, /ansehen/);
  for (const model of [C.deleteModel({ ...rp, nonEmpty: false }), C.copyModel('copy', { ...rp, dir: '/x', targetHost: 'Server B' }), C.uploadModel({ ...rp, dir: '/x' }, [{ name: 'a', size: 1 }]), C.renameModel({ ...rp, name: 'n' }), C.mkdirModel({ ...rp, dir: '/', name: 'n' })]) {
    assert.ok(model.root && model.rootNote && model.danger, model.title);                                                       // jede Root-Aktion steht auffaellig da
  }
  const plain = C.deleteModel({ ...rp, root: false, nonEmpty: false });
  assert.ok(!plain.root);
  assert.ok(C.copyModel('copy', { ...rp, root: true, dir: '/x', host: 'Server B', targetHost: 'Server A (Root)' }).root);       // auch wenn nur das Ziel root ist (der Server meldet root)
});

test('Root: die neuen Texte stehen in Deutsch und Englisch', () => {
  for (const k of ['files.root.banner', 'files.root.dialogNote', 'files.rootAddress', 'app.allowFilesRoot', 'files.read.titleText', 'files.read.titleDownload']) assert.ok(de[k] && en[k], k);
  assert.match(de['files.root.banner'], /\{host\}/); assert.match(en['files.root.banner'], /\{host\}/);
});
