import { ico, prefs, applyServerPrefs } from './app.js';
import { Sessions } from './sessions.js';
import { hostDetail } from './hosts.js';
import { createHostInfoStore } from './hostinfo.js';
import { initHostPage } from './hostpage.js';
import { loginUser, loginSaved, hostHasLogin } from './hostlogin.js';
import { initHostActionDialog } from './hostactions-dialog.js';
import { createTaskStore } from './hosttasks.js';
import { loadOrganisation, takeOverLocal, mergePlacements, setFolder, touch, move, folderName } from './folders.js';
import { initFolderDialog } from './folder-dialog.js';
import { parseQuickTarget, quickConnection, DEFAULT_PORT as QUICK_PORT } from './quick.js';
import { launchTarget, validWorkDir, validCustom, programOf } from './terminal-programs.js';
import { initAccount } from './account.js';
import { initSecurity } from './security.js';
import { initStepUp, withStepUp } from './stepup.js';
import { initNetTools } from './nettools.js';
import { initAlerts, parseHostLink } from './alerts.js';
import { qrSvg } from './qr.js';
import { showDialog, focusField } from './desktop-window.js';
import { t, tp, tk, relativeTime, getPref, isPref } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import './terminal-session.js';      // Sitzungsart 'terminal' (natives SSH-Terminal)

/* =====================================================================
 * Startseite und Konto: Ersteinrichtung, Anmeldung, gespeicherte Verbindungen, Geraete.
 * Die Fernsteuerung selbst steckt in app.js (eine RemoteSession je Sitzung, Verwalter: sessions.js); hier wird nur gewaehlt, was verbunden wird.
 * Kein Passwort wird gespeichert: das Passwort fuer ein Ziel wird bei jeder Verbindung getippt.
 * ===================================================================== */

const $ = (s) => document.querySelector(s);
const SCREENS = ['boot', 'setup', 'login', 'home', 'remote'];

// Breiter Bildschirm mit Maus: die Desktop-Oberflaeche (desktop.js) ersetzt Startseite und Fernsteuerungs-Ansicht. Auf dem Handy wird sie nicht geladen.
// ?desktop und ?phone erzwingen eine der beiden Ansichten (zum Testen, zum Beispiel in einem Browser ohne "feinen" Zeiger)
const FORCE = new URLSearchParams(location.search);
const WIDE = FORCE.has('phone') ? false : FORCE.has('desktop') ? innerWidth >= 1000 : matchMedia('(min-width: 1000px) and (pointer: fine)').matches;
const D = WIDE ? await import('./desktop.js').catch(() => null) : null;
// Handy-Oberflaeche (Material 3, mobile.js): auf allem, was nicht die Desktop-Oberflaeche bekommt. ?classic zeigt die schlichte alte Startseite.
const M = !D && !FORCE.has('classic') ? await import('./mobile.js').catch(() => null) : null;

const show = (name) => {
  if (securityUi) securityUi.setHomeShown(name === 'home');     // der Hinweis zu den Proxy-Adressen nur auf dem Start-Bildschirm
  if (D) {   // Desktop: Startseite und Fernsteuerung sind Fenster in #desktop (die Fernsteuerung zeigt und versteckt app.js selbst)
    const desk = name === 'home' || name === 'remote';
    for (const n of ['boot', 'setup', 'login', 'home']) $('#' + n).hidden = n !== name;
    $('#desktop').hidden = !desk;
    return;
  }
  if (M) {   // Handy: die Startseite ist #mobile (blendet sich selbst ein und aus), die alte #home bleibt zu
    M.setShown(name === 'home');
    for (const n of SCREENS) if (n !== 'home') $('#' + n).hidden = n !== name;
    $('#home').hidden = true;
    if (name === 'remote') { const r = $('#remote'); r.classList.remove('m3-in'); void r.offsetWidth; r.classList.add('m3-in'); r.addEventListener('animationend', () => r.classList.remove('m3-in'), { once: true }); }
    return;
  }
  for (const n of SCREENS) $('#' + n).hidden = n !== name;
};

class ApiError extends Error {
  constructor(status, message, retryAfter, code, data) { super(message); this.status = status; this.retryAfter = retryAfter; this.code = code; this.data = data; }   // code: kurzer Fehlercode mancher Wege (Aktionen auf Hosts, Dateien), den die Oberflaeche uebersetzt; data: die ganze Antwort (zum Beispiel der Fingerabdruck eines Host-Schluessels)
}

/** Fragt den Server und wiederholt die Aktion einmal, wenn er dafuer eine frische Freigabe mit dem zweiten Faktor verlangt (stepup.js). */
function api(path, options) { return withStepUp(() => rawApi(path, options), askStepUp); }

/** Fragt den Server. Aenderungen tragen die Sonderkopfzeile, mit der der Server sie als von der eigenen Seite erkennt. */
async function rawApi(path, { method = 'GET', body } = {}) {
  const headers = {};
  if (method !== 'GET') headers['X-JulOS'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  let r;
  try { r = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) }); }
  catch { throw new ApiError(0, t('shell.serverUnreachable')); }
  let data = null;
  if (r.status !== 204) { try { data = await r.json(); } catch { /* kein JSON */ } }
  if (!r.ok) {
    const wait = data && data.retryAfter ? ' ' + t('shell.waitSeconds', { n: data.retryAfter }) : '';
    throw new ApiError(r.status, ((data && data.error ? serverMsg(data.error) : '') || t('shell.errorStatus', { status: r.status })) + (r.status === 429 ? wait : ''), data && data.retryAfter, data && data.code, data);
  }
  return data;
}

const setMsg = (el, text, ok = false) => { el.textContent = text || ''; el.classList.toggle('ok', ok && !!text); };
const askStepUp = initStepUp({ api: rawApi, setMsg });      // das Sheet fuer den zweiten Faktor (stepup.js)
let securityUi = null;                                      // Einstellungen, Sicherheit (security.js), unten angelegt
const busy = (form, on) => { for (const b of form.querySelectorAll('button[type=submit]')) b.disabled = on; };

const state = { user: null, connections: [], folders: [], hosts: [], actionsPaused: false, canStore: false, driveAvailable: false, editing: null, editingHost: null, starting: null };

/* ---------- Start: wo stehen wir? ---------- */
async function boot() {
  show('boot');
  let me;
  try { me = await api('/api/auth/me'); }
  catch (e) { showLogin(e.message); return; }
  if (me.user) return showHome(me.user);
  if (me.setupNeeded) return showSetup();
  showLogin();
}

/* ---------- Ersteinrichtung ---------- */
let setupInfo = null;
function showSetup() {
  show('setup'); $('#setupForm').hidden = false; $('#setupSecretForm').hidden = true;
  setTimeout(() => $('#setupCode').focus(), 50);
}

$('#setupForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.currentTarget, msg = $('#setupMsg');
  const code = $('#setupCode').value.trim(), user = $('#setupUser').value.trim(), password = $('#setupPw').value;
  if (password !== $('#setupPw2').value) return setMsg(msg, t('shell.setupPwMismatch'));
  busy(form, true); setMsg(msg, '');
  try {
    const r = await api('/api/setup/begin', { method: 'POST', body: { code, user, password } });
    setupInfo = { code, user };
    $('#setupSecret').textContent = r.secret;
    $('#setupOtpLink').href = r.uri;
    try { $('#setupQr').innerHTML = qrSvg(r.uri, { label: t('account.qrLabel') }); } catch { $('#setupQr').replaceChildren(); }
    $('#setupRecovery').textContent = r.recovery.join('\n');
    $('#setupPw').value = ''; $('#setupPw2').value = '';
    form.hidden = true; $('#setupSecretForm').hidden = false; setTimeout(() => $('#setupTotp').focus(), 50);
  } catch (err) { setMsg(msg, err.message); }
  finally { busy(form, false); }
});

$('#setupCopy').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText($('#setupRecovery').textContent); setMsg($('#setupSecretMsg'), t('account.codesCopied'), true); }
  catch { setMsg($('#setupSecretMsg'), t('account.copyFailed')); }
});

$('#setupSecretForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.currentTarget, msg = $('#setupSecretMsg');
  busy(form, true); setMsg(msg, '');
  try {
    const r = await api('/api/setup/finish', { method: 'POST', body: { code: setupInfo.code, user: setupInfo.user, totp: $('#setupTotp').value.replace(/\s+/g, '') } });
    $('#setupSecret').textContent = ''; $('#setupRecovery').textContent = ''; $('#setupQr').replaceChildren(); $('#setupTotp').value = ''; setupInfo = null;
    showHome(r.user);
  } catch (err) { setMsg(msg, err.message); }
  finally { busy(form, false); }
});

/* ---------- Anmeldung ---------- */
function showLogin(message = '') {
  closeAllSessions();
  hostInfo.clear();
  tasks.clear();
  if (D) D.reset();
  if (M) M.reset();
  show('login'); setMsg($('#loginMsg'), message);
  $('#loginPw').value = ''; $('#loginCode').value = '';
  setTimeout(() => ($('#loginUser').value ? $('#loginPw') : $('#loginUser')).focus(), 50);
}

$('#loginForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.currentTarget, msg = $('#loginMsg');
  busy(form, true); setMsg(msg, '');
  try {
    const r = await api('/api/auth/login', { method: 'POST', body: { user: $('#loginUser').value.trim(), password: $('#loginPw').value, code: $('#loginCode').value.trim() } });
    $('#loginPw').value = ''; $('#loginCode').value = '';
    showHome(r.user, r.recoveryLeft <= 3 ? tp('shell.recoveryLeft', r.recoveryLeft) : '');
  } catch (err) { setMsg(msg, err.message); $('#loginPw').value = ''; $('#loginCode').value = ''; }
  finally { busy(form, false); }
});

/* ---------- Startseite ---------- */
async function showHome(user, message = '') {
  state.user = user;
  $('#accountName').textContent = user;
  $('#accountMenu').hidden = true;
  setMsg($('#homeMsg'), message);
  show('home');
  if (M) M.userChanged(user, message);
  syncPrefs(true);
  await loadConnections();
  if (D) D.userChanged(user, message);
  if (securityUi) securityUi.check();
  openLinkedHost();
}

/** Tastenleiste und Themenfarbe vom Server holen (er hat Vorrang; hat er nichts, geht das Lokale hinaus). Ohne Server bleibt die lokale Kopie. */
let prefsAt = 0;
async function syncPrefs(afterSignIn = false) {
  prefsAt = Date.now();
  const got = await prefs.sync();
  // Hat der Server eine andere Sprache als diese Seite: nach der Anmeldung (noch keine Sitzung offen) neu laden, sonst gilt sie ab dem naechsten Laden
  if (afterSignIn && got.lang && isPref(got.lang.lang) && got.lang.lang !== getPref()) { applyServerPrefs({ lang: got.lang }); location.reload(); return; }
  if (got.keybar || got.theme || got.lang) { applyServerPrefs(got); if (M) M.applyShellTheme(); }
}
// Ein anderes Geraet hat etwas geaendert: beim Zurueckkehren zur Seite (hoechstens einmal je Minute) neu abgleichen
document.addEventListener('visibilitychange', () => { if (!document.hidden && state.user && Date.now() - prefsAt > 60000) syncPrefs(); });

async function loadConnections() {
  try { const r = await api('/api/connections'); state.connections = r.connections; state.canStore = !!r.canStorePasswords; state.driveAvailable = !!r.driveAvailable; }
  catch (e) { if (e.status === 401) return showLogin(t('shell.sessionExpired')); setMsg($('#homeMsg'), e.message); if (M) M.snack(e.message); return; }
  // Ordner, Favoriten und zuletzt benutzt kommen vom Server (beim ersten Mal nimmt das Geraet seine lokalen Favoriten mit); ein Fehler laesst die Liste ohne sie stehen
  try { await takeOverLocal(localStorage, api); await loadOrganisation(api, state); }
  catch (e) { if (e.status === 401) return showLogin(t('shell.sessionExpired')); mergePlacements(state.connections, []); }
  renderConnections();
  if (D) D.connectionsChanged();
  if (M) M.connectionsChanged();
  await loadHosts();
  tasks.load();                  // laufende und eben beendete Aktionen auf Hosts (auch nach einem Neuladen der Seite)
}

/** Die Hosts mit Zustand (online/offline, Agent, Dienste); fragt der Server nach, prueft er die Ports kurz (docs/HOSTS.md). Ein Fehler laesst die alte Liste stehen. */
async function loadHosts() {
  try { const list = await api('/api/hosts'); state.hosts = list.hosts; state.actionsPaused = !!list.actionsPaused; }
  catch (e) { if (e.status === 401) return showLogin(t('shell.sessionExpired')); return; }
  fillHostSelect();
  if (D) D.hostsChanged();
  if (M) M.hostsChanged();
}
// Der Zustand ist eine Momentaufnahme: bei sichtbarer Seite alle 30 s neu (der Server merkt sich die Antworten ohnehin 15 s)
setInterval(() => { if (state.user && !document.hidden) loadHosts(); }, 30000);

const esc = (t) => String(t).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const KIND_LABEL = { desktop: tk('shell.kindDesktop'), vnc: tk('shell.kindVnc'), terminal: tk('shell.kindTerminal') };

function renderConnections() {
  const list = $('#connList');
  list.replaceChildren();
  $('#connEmpty').hidden = state.connections.length > 0;
  for (const c of state.connections) {
    const row = document.createElement('div');
    row.className = 'conn';
    row.innerHTML = `<button type="button" class="connmain"><span class="connic">${ico(c.kind === 'terminal' ? 'terminal' : 'desktop')}</span>
        <span class="conntext"><span class="connname">${esc(c.name)}</span><span class="connsub">${esc(KIND_LABEL[c.kind] ? t(KIND_LABEL[c.kind]) : c.kind)} · ${esc(loginUser(c) ? loginUser(c) + '@' : '')}${esc(c.host)}:${c.port}${folderName(state.folders, c) ? ' · ' + esc(folderName(state.folders, c)) : ''}${loginSaved(c) ? `<span class="connkey" title="${esc(t('shell.passwordSaved'))}">${ico('key')}</span>` : ''}</span></span></button>
      <button type="button" class="connedit ghost" aria-label="${esc(t('common.edit'))}">${ico('edit')}</button>`;
    row.querySelector('.connmain').addEventListener('click', () => startDialog(c));
    row.querySelector('.connedit').addEventListener('click', () => editDialog(c));
    list.appendChild(row);
  }
}

/* Konto-Menue */
$('#accountBtn').addEventListener('click', (e) => { e.stopPropagation(); $('#accountMenu').hidden = !$('#accountMenu').hidden; });
document.addEventListener('click', (e) => { if (!e.target.closest('#accountMenu')) $('#accountMenu').hidden = true; });
$('#accountMenu').addEventListener('click', async (e) => {
  const act = e.target.closest('button')?.dataset.act;
  $('#accountMenu').hidden = true;
  if (act === 'devices') openDevices();
  else if (act === 'account') openAccount();
  else if (act === 'signout') signOut();
});

async function signOut() {
  closeAllSessions();
  try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* Sitzung war schon weg */ }
  state.user = null; state.connections = []; state.folders = []; state.hosts = [];
  showLogin();
}

/* ---------- Verbindung anlegen und aendern ---------- */
const DEFAULT_PORT = { desktop: '3389', vnc: '5900', terminal: '22' };
// Laufwerk (nur Desktop): Freigabe und Schreiben, beide standardmaessig aus; ohne Ordner am Server (JULOS_DRIVE_ROOT) nur der Hinweis
const showDriveChoice = () => {
  const on = $('#connKind').value === 'desktop';
  const share = $('#connDrive').checked && state.driveAvailable;
  $('#connDriveRow').hidden = !on;
  $('#connDrive').disabled = !state.driveAvailable;
  if (!state.driveAvailable) { $('#connDrive').checked = false; $('#connDriveWrite').checked = false; }
  if (!share) $('#connDriveWrite').checked = false;
  $('#connDriveWriteRow').hidden = !on || !share;
  $('#connDriveWriteHint').hidden = !on || !share || !$('#connDriveWrite').checked;
  $('#connDriveOff').hidden = !on || state.driveAvailable;
};
$('#connKind').addEventListener('change', () => { $('#connPort').placeholder = DEFAULT_PORT[$('#connKind').value]; showDriveChoice(); showDesktopFields(); });
$('#connDrive').addEventListener('change', showDriveChoice);
$('#connDriveWrite').addEventListener('change', showDriveChoice);
$('#addConn').addEventListener('click', () => editDialog(null));

// Domäne und Tastaturlayout gibt es nur beim Desktop (RDP)
function showDesktopFields() { const on = $('#connKind').value === 'desktop'; $('#connDomainRow').hidden = !on; $('#connLayoutRow').hidden = !on; showProgramFields(); }

// Startprogramm, Arbeitsverzeichnis und tmux gibt es nur beim Terminal (docs/CLI-SESSIONS.md)
function showProgramFields() {
  const term = $('#connKind').value === 'terminal', prog = $('#connProgram').value;
  $('#connProgRow').hidden = !term;
  $('#connCustomRow').hidden = prog !== 'custom';
  $('#connProgHint').textContent = prog === 'custom' ? t('shell.progHintCustom') : prog === 'shell' ? '' : t('shell.progHint');
  $('#connTileRow').hidden = !(term && state.editing && state.editing.kind === 'terminal');
}
$('#connProgram').addEventListener('change', showProgramFields);

function editDialog(c, kind) {
  state.editing = c;
  $('#connDlgTitle').textContent = c ? t('shell.editConnection') : t('shell.newConnection');
  $('#connName').value = c ? c.name : '';
  $('#connKind').value = c ? c.kind : kind || 'desktop';
  $('#connDrive').checked = !!(c && c.driveShare); $('#connDriveWrite').checked = !!(c && c.driveWrite); showDriveChoice();
  $('#connHost').value = c ? c.host : '';
  $('#connPort').value = c ? String(c.port) : '';
  $('#connPort').placeholder = DEFAULT_PORT[$('#connKind').value];
  $('#connUser').value = c ? c.username : '';
  $('#connProgram').value = c && c.program ? c.program : 'shell'; $('#connCustom').value = c ? c.customCommand || '' : '';
  $('#connWorkdir').value = c ? c.workDir || '' : ''; $('#connKeep').checked = !!(c && c.keepAlive);
  $('#connDomain').value = c ? c.domain || '' : ''; $('#connLayout').value = c && c.layout ? c.layout : 'de'; $('#connNotes').value = c ? c.notes || '' : '';
  showDesktopFields();
  fillHostSelect(c ? c.hostId : undefined);
  fillFolderSelect(c ? c.folderId : '');
  $('#connUp').hidden = $('#connDown').hidden = !c;
  $('#connDelete').hidden = !c; $('#connDelete').textContent = t('common.delete');
  $('#connOwn').checked = !!(c && c.ownLogin);
  $('#connPw').value = ''; $('#connPw').disabled = !state.canStore;
  $('#connForget').checked = false; $('#connForgetRow').hidden = !(c && c.hasPassword);
  $('#connPwHint').textContent = !state.canStore
    ? t('shell.pwHintNoStore')
    : c && c.hasPassword
      ? t('shell.pwHintStored')
      : t('shell.pwHintNew');
  syncOwnLogin();                                   // nach den Zeilen darueber: sie versteckt Benutzer, Passwort und Hinweis, wenn der Host-Login gilt
  setMsg($('#connMsg'), '');
  showDialog($('#connDlg'), { system: true, width: 500 });
}

/* ---------- Hosts: Auswahl in der Verbindung, Anlegen, Umbenennen, Loeschen, Agent (docs/HOSTS.md) ---------- */
const NEW_HOST = '__new';
/** Die Auswahl der Verbindung: alle Hosts, am Ende "Neuer Host …". Behaelt die Wahl; ohne Wahl gilt der erste Host (so legt der Server neue Verbindungen ab). */
/** Hat der gewaehlte Host einen Login, gilt er fuer diese Verbindung, ausser sie verwendet ausdruecklich ihren eigenen Benutzer (Haken, Standard aus); sonst bleiben Benutzer und Passwort verborgen */
function syncOwnLogin() {
  const host = state.hosts.find((h) => h.id === $('#connHostSel').value);
  const has = hostHasLogin(host), own = !has || $('#connOwn').checked;
  $('#connOwnRow').hidden = !has;
  $('#connOwnHint').hidden = own;
  if (!own) $('#connOwnHint').textContent = t(host.loginPassword ? tk('shell.ownLoginHintPw') : tk('shell.ownLoginHint'), { user: host.loginUser });
  $('#connUserRow').hidden = $('#connPwRow').hidden = $('#connPwHint').hidden = !own;
  $('#connForgetRow').hidden = !own || !(state.editing && state.editing.hasPassword);
}
$('#connOwn').addEventListener('change', syncOwnLogin);

function fillHostSelect(selected) {
  const sel = $('#connHostSel');
  const keep = selected ?? (sel.value && sel.value !== NEW_HOST ? sel.value : '');
  sel.replaceChildren(...state.hosts.map((h) => new Option(h.name, h.id)), new Option(t('shell.newHostOption'), NEW_HOST));
  if (keep && state.hosts.some((h) => h.id === keep)) sel.value = keep; else if (state.hosts.length) sel.value = state.hosts[0].id;
  sel.dataset.prev = sel.value;
  $('#connHostRow').hidden = state.hosts.length === 0;
}
$('#connHostSel').addEventListener('change', (e) => {
  const sel = e.currentTarget;
  if (sel.value !== NEW_HOST) { sel.dataset.prev = sel.value; syncOwnLogin(); return; }
  sel.value = sel.dataset.prev;                      // bis der neue Host da ist, bleibt die bisherige Wahl
  hostDialog(null, (h) => { fillHostSelect(h.id); });
});

/* ---------- Ordner: Auswahl in der Verbindung, Verwaltung im Dialog (docs/FOLDERS.md) ---------- */
function fillFolderSelect(selected) {
  const sel = $('#connFolderSel');
  sel.replaceChildren(new Option(t('shell.noFolder'), ''), ...state.folders.map((f) => new Option(f.name, f.id)));
  sel.value = selected && state.folders.some((f) => f.id === selected) ? selected : '';
  $('#connFolderRow').hidden = state.folders.length === 0;
}
const openFolders = initFolderDialog({ state, api, esc: (t) => esc(t), changed: loadConnections });
// Reihenfolge der Verbindung innerhalb ihres Ordners (der Dialog bleibt offen)
for (const [id, delta] of [['#connUp', -1], ['#connDown', 1]]) {
  $(id).addEventListener('click', async () => {
    try { await move(api, state.editing, delta); await loadConnections(); setMsg($('#connMsg'), t('shell.moved'), true); }
    catch (err) { setMsg($('#connMsg'), err.message); }
  });
}

/** Host anlegen (h = null) oder bearbeiten. onCreated bekommt den neuen Host (zum Beispiel fuer die Auswahl in der Verbindung). */
function hostDialog(h, onCreated) {
  state.editingHost = h; state.hostCreated = onCreated || null;
  $('#hostDlgTitle').textContent = h ? t('shell.editHost') : t('shell.newHost');
  $('#hostName').value = h ? h.name : '';
  const nOf = h ? state.connections.filter((c) => c.hostId === h.id).length : 0;
  $('#hostDelete').hidden = !h; $('#hostDelete').textContent = t('common.delete');
  const info = $('#hostInfo');
  info.hidden = !h;
  if (h) info.textContent = [hostDetail(h), tp('shell.hostConns', nOf)].filter(Boolean).join(' · ');
  setMsg($('#hostMsg'), '');
  showDialog($('#hostDlg'), { system: true, width: 420 });
  setTimeout(() => focusField($('#hostDlg'), $('#hostName')), 100);
}

$('#hostForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.currentTarget, msg = $('#hostMsg'), h = state.editingHost;
  busy(form, true); setMsg(msg, '');
  try {
    const saved = await api(h ? `/api/hosts/${h.id}` : '/api/hosts', { method: h ? 'PUT' : 'POST', body: { name: $('#hostName').value } });
    $('#hostDlg').close();
    await loadHosts();
    if (!h && state.hostCreated) state.hostCreated(saved);
    if (M) M.snack(h ? t('shell.changesSaved') : t('shell.hostAdded'));
  } catch (err) { setMsg(msg, err.message); }
  finally { busy(form, false); }
});

// Loeschen braucht ein zweites Tippen; der Server lehnt einen Host mit Verbindungen ab
$('#hostDelete').addEventListener('click', async (e) => {
  const b = e.currentTarget;
  if (b.dataset.sure !== '1') { b.dataset.sure = '1'; b.textContent = t('shell.reallyDelete'); setTimeout(() => { b.dataset.sure = ''; b.textContent = t('common.delete'); }, 3000); return; }
  b.dataset.sure = '';
  try { await api(`/api/hosts/${state.editingHost.id}`, { method: 'DELETE' }); $('#hostDlg').close(); await loadHosts(); if (M) M.snack(t('shell.hostDeleted')); }
  catch (err) { setMsg($('#hostMsg'), err.message); }
});

$('#connForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.currentTarget, msg = $('#connMsg');
  const portText = $('#connPort').value.trim();
  if (portText && !/^\d{1,5}$/.test(portText)) return setMsg(msg, t('shell.portNumber'));
  const body = { name: $('#connName').value, kind: $('#connKind').value, host: $('#connHost').value, port: portText ? Number(portText) : null, username: $('#connUser').value, notes: $('#connNotes').value };
  if (body.kind === 'desktop') { body.domain = $('#connDomain').value; body.layout = $('#connLayout').value; }
  if (body.kind === 'terminal') {
    body.program = $('#connProgram').value; body.workDir = $('#connWorkdir').value; body.keepAlive = $('#connKeep').checked; body.customCommand = body.program === 'custom' ? $('#connCustom').value : '';
    if (!validWorkDir(body.workDir)) return setMsg(msg, t('shell.workdirInvalid'));
    if (body.program === 'custom' && (!body.customCommand.trim() || !validCustom(body.customCommand))) return setMsg(msg, t('shell.customInvalid'));
  }
  const hostId = $('#connHostSel').value; if (hostId && hostId !== NEW_HOST) body.hostId = hostId;
  const drive = body.kind === 'desktop' && state.driveAvailable && $('#connDrive').checked;
  body.driveShare = drive; body.driveWrite = drive && $('#connDriveWrite').checked;
  const hostLogin = hostHasLogin(state.hosts.find((h) => h.id === $('#connHostSel').value));
  if (hostLogin) body.ownLogin = $('#connOwn').checked;                    // ohne Login am Host gibt es den Haken nicht: die Verbindung bleibt, wie sie ist
  const pw = $('#connPw').value;
  if (pw && (!hostLogin || body.ownLogin)) body.password = pw;
  if ($('#connForget').checked && !pw && (!hostLogin || body.ownLogin)) body.forgetPassword = true;
  busy(form, true); setMsg(msg, '');
  try {
    const c = state.editing;
    const saved = await api(c ? `/api/connections/${c.id}` : '/api/connections', { method: c ? 'PUT' : 'POST', body });
    const folder = $('#connFolderSel').value || null;
    if (state.folders.length && folder !== ((c && c.folderId) || null)) await setFolder(api, { id: saved.id }, folder);
    $('#connPw').value = '';
    $('#connDlg').close();
    await loadConnections();
    if (M) M.snack(c ? t('shell.changesSaved') : t('shell.connectionAdded'));
  } catch (err) { setMsg(msg, err.message); }
  finally { busy(form, false); }
});

// Loeschen braucht ein zweites Tippen (kein Browser-Dialog: die App laeuft oft im Vollbild)
$('#connDelete').addEventListener('click', async (e) => {
  const b = e.currentTarget;
  if (b.dataset.sure !== '1') { b.dataset.sure = '1'; b.textContent = t('shell.reallyDelete'); setTimeout(() => { b.dataset.sure = ''; b.textContent = t('common.delete'); }, 3000); return; }
  b.dataset.sure = '';
  try { await api(`/api/connections/${state.editing.id}`, { method: 'DELETE' }); $('#connDlg').close(); await loadConnections(); if (M) M.snack(t('shell.connectionDeleted')); }
  catch (err) { setMsg($('#connMsg'), err.message); }
});

/* ---------- Programm-Kacheln: "Claude auf <Host>", "Codex auf <Host>" (docs/CLI-SESSIONS.md) ---------- */
const notice = (text) => { if (M) M.snack(text); else if (D && D.toast) D.toast(text); else setMsg($('#homeMsg'), text); };

/** Legt aus einer Terminal-Verbindung eine Kachel fuer Claude oder Codex an (gleiches Ziel, gleiches gespeichertes Passwort; der Server uebernimmt beides). */
async function createProgramTile(c, program) {
  const host = state.hosts.find((h) => h.id === c.hostId);
  const name = t('prog.tileName', { name: program === 'codex' ? 'Codex' : 'Claude', host: host ? host.name : c.name }).slice(0, 60);
  const saved = await api(`/api/connections/${c.id}/program`, { method: 'POST', body: { program, name } });
  await loadConnections();
  return state.connections.find((x) => x.id === saved.id) || saved;
}
for (const [id, program] of [['#connTileClaude', 'claude'], ['#connTileCodex', 'codex']]) {
  $(id).addEventListener('click', async () => {
    const c = state.editing; if (!c) return;
    try { const made = await createProgramTile(c, program); $('#connDlg').close(); notice(t('prog.tileCreated', { name: made.name })); }
    catch (err) { setMsg($('#connMsg'), err.message); }
  });
}

/**
 * Ein Tipp auf "Claude" oder "Codex": eine passende Kachel oeffnet sie, mehrere lassen waehlen (choose bekommt die Liste), keine legt eine aus einer
 * Terminal-Verbindung an und startet sie, ohne Terminal-Verbindung fuehrt es in den Dialog fuer eine neue.
 */
async function launchProgram(program, choose) {
  const target = launchTarget(state.connections, program);
  if (target.action === 'open') return startDialog(target.conn);
  if (target.action === 'choose') return choose ? choose(target.list) : startDialog(target.list[0]);
  if (target.action === 'create') {
    try { const made = await createProgramTile(target.from, program); notice(t('prog.tileCreated', { name: made.name })); startDialog(made); }
    catch (err) { notice(err.message); }
    return;
  }
  notice(t('prog.needTerminal'));
  editDialog(null, 'terminal');
  $('#connProgram').value = program; showProgramFields();
}

/* ---------- Verbinden ---------- */
const asConn = (c) => ({ id: c.id, name: c.name, kind: c.kind, stored: loginSaved(c) ? true : undefined, target: `${c.host}:${c.port}/${loginUser(c)}`, domain: c.domain || undefined, program: programOf(c), workDir: c.workDir || undefined });

/** Text in die Eingabeleiste einer Terminal-Sitzung legen, ohne ihn zu senden (der Benutzer sieht ihn und startet ihn selbst mit Enter). Andere Sitzungsarten ignorieren es. */
function prefillSession(session, text) {
  if (text && session && typeof session.prefill === 'function') session.prefill(text);
}

/** opts.extra: bewusst eine weitere Sitzung zum selben Ziel; sonst wird eine schon offene Sitzung nur nach vorn geholt. */
function startDialog(c, opts = {}) {
  if (!opts.extra && Sessions.find(c.id).length) { prefillSession(Sessions.open(asConn(c)), opts.prefill); return; }   // laeuft schon: nach vorn, kein Passwort noetig
  state.starting = { c, extra: !!opts.extra, prefill: opts.prefill };
  // Mit gespeichertem Passwort genuegt ein Tipp: der Server nimmt es aus dem verschluesselten Speicher
  // Ein VNC-Server kann ohne Anmeldung laufen: dann gleich verbinden; verlangt er ein Passwort, fragt die Sitzung danach
  if (loginSaved(c) || c.kind === 'vnc') { launch(c, '', !!opts.extra, opts.prefill); return; }
  $('#startTitle').textContent = c.name;
  $('#startUser').value = loginUser(c);
  $('#startPw').value = '';
  showDialog($('#startDlg'), { system: true });
  setTimeout(() => focusField($('#startDlg'), $('#startPw')), 150);
}

/** Verbindung starten: der Verwalter legt die Sitzung an, die Huelle (Fenster am Desktop, Umschalter am Handy) zeigt sie. */
function launch(c, password, extra = false, prefill = '') {
  const before = Sessions.count;
  const session = Sessions.open(asConn(c), { password, extra });
  prefillSession(session, prefill);
  if (Sessions.count > before) {
    c.useCount = (c.useCount || 0) + 1; c.lastUsedAt = new Date().toISOString();       // sofort sichtbar; der Server zaehlt es fuer alle Geraete
    touch(api, c).catch(() => {});
    if (M) M.started(c);
  }
}

$('#startForm').addEventListener('submit', (e) => {
  e.preventDefault();
  const password = $('#startPw').value;
  if (!password) return;
  $('#startPw').value = '';
  $('#startDlg').close();
  launch(state.starting.c, password, state.starting.extra, state.starting.prefill);
});

/* ---------- Quick Connect: Sitzung ohne Speichern (nichts in der Datenbank, das Passwort gilt nur fuer diese Sitzung) ---------- */
/** prefill: Eingabe aus der Suche (Adresse, benutzer@adresse:port oder rdp://, ssh://) */
function quickDialog(prefill = '') {
  const q = parseQuickTarget(prefill);
  $('#quickKind').value = (q && q.kind) || 'desktop'; $('#quickKind').dispatchEvent(new Event('change'));
  $('#quickHost').value = q ? (q.port ? `${q.host}:${q.port}` : q.host) : prefill.trim();
  $('#quickUser').value = q ? q.username : '';
  $('#quickPw').value = '';
  setMsg($('#quickMsg'), '');
  showDialog($('#quickDlg'), { system: true, width: 480 });
  setTimeout(() => focusField($('#quickDlg'), $('#quickHost').value ? $('#quickPw') : $('#quickHost')), 150);
}
$('#quickKind').addEventListener('change', () => { $('#quickPw').required = $('#quickKind').value !== 'vnc'; });   // ein VNC-Ziel ohne Anmeldung braucht kein Passwort
$('#quickForm').addEventListener('submit', (e) => {
  e.preventDefault();
  const kind = $('#quickKind').value, msg = $('#quickMsg');
  const q = parseQuickTarget($('#quickHost').value);
  if (!q) return setMsg(msg, t('shell.quickAddressInvalid'));
  const user = $('#quickUser').value.trim() || q.username;
  const password = $('#quickPw').value;
  if (!password && kind !== 'vnc') return setMsg(msg, t('shell.quickPasswordMissing'));
  $('#quickPw').value = '';
  $('#quickDlg').close();
  Sessions.open(quickConnection({ kind, host: q.host, port: q.port || QUICK_PORT[kind], username: user }), { password });
});

/* ---------- Sitzungen: die Huelle zeigt, was der Verwalter meldet ---------- */
// Handy und einfache Ansicht: alle Sitzungen haengen in #remote, genau eine ist sichtbar; die anderen laufen im Hintergrund (pausiert).
// Desktop: jede Sitzung ist ein Fenster (desktop.js).
let remoteShown = false, closingAll = false;
/** Alle Sitzungen schliessen (Abmelden, Sitzung der App abgelaufen), ohne dass die Huelle dabei zwischendurch die Startseite zeigt. */
function closeAllSessions() { closingAll = true; try { Sessions.closeAll(); } finally { closingAll = false; remoteShown = false; } }
function phoneVisibility() { for (const s of Sessions.list()) s.setVisible(remoteShown && s === Sessions.active); }
function showRemote() {
  if (!remoteShown) { remoteShown = true; if (M && history.state?.m3 !== 'remote') history.pushState({ ...(history.state || {}), m3: 'remote' }, ''); }
  show('remote'); phoneVisibility(); if (M) M.sessionsChanged();
}
function showHomeView() {
  remoteShown = false;
  if (document.fullscreenElement && !Sessions.count) document.exitFullscreen().catch(() => {});
  show('home'); phoneVisibility(); if (M) M.sessionsChanged();
}
function goHome() { if (M && history.state?.m3 === 'remote') history.back(); else showHomeView(); }
window.addEventListener('popstate', (e) => { if (!D && remoteShown && !(e.state && e.state.m3 === 'remote')) showHomeView(); });

Sessions.addEventListener('opened', (e) => {
  const session = e.detail.session;
  if (D) D.sessionOpened(session, Sessions.count); else { session.mount($('#remote')); session.setVisible(false); }
});
Sessions.addEventListener('activated', (e) => {
  const { session, reveal } = e.detail;
  if (D) D.sessionActivated(session, reveal);
  else if (reveal) showRemote();
  else { phoneVisibility(); if (M) M.sessionsChanged(); }
});
Sessions.addEventListener('statechange', () => { if (D) D.sessionsChanged(); else if (M) M.sessionsChanged(); });
Sessions.addEventListener('closed', (e) => {
  const { session, message, reason } = e.detail;
  if (D) D.sessionClosed(session, closingAll ? '' : message);
  if (closingAll) return;
  if (reason === 'auth') return showLogin(message);
  if (D) return;
  if (M) M.ended(message, reason);
  if (!Sessions.count) { if (remoteShown) goHome(); else if (M) M.sessionsChanged(); if (message) { M ? M.snack(message) : setMsg($('#homeMsg'), message); } return; }
  // noch andere offen: bei der Ansicht bleiben (der Verwalter hat die naechste aktiviert), die Meldung zeigen
  phoneVisibility();
  if (message) { M ? M.snack(message) : setMsg($('#homeMsg'), message); }
  if (M) M.sessionsChanged();
});
Sessions.addEventListener('home', () => { if (!D) goHome(); });
Sessions.addEventListener('switcher', () => { if (M) M.openSwitcher(); else goHome(); });

const hostInfo = createHostInfoStore({ api, onChange: () => { if (D && D.hostInfoChanged) D.hostInfoChanged(); if (M && M.hostInfoChanged) M.hostInfoChanged(); } });
// Hintergrund-Aufgaben auf Hosts (hosttasks.js): eine bestaetigte Aktion laeuft hier weiter, ohne etwas zu sperren; Leiste und Panel zeichnen Desktop und Handy
const ui = () => (D || M);
const tasks = createTaskStore({ api, onChange: () => ui() && ui().tasksChanged && ui().tasksChanged(), onFinish: (task) => ui() && ui().taskFinished && ui().taskFinished(task) });
const openHostAction = initHostActionDialog({ api, esc, tasks, onStarted: (task) => ui() && ui().taskStarted && ui().taskStarted(task) });
const hostPage = initHostPage({ state, api, esc, store: hostInfo, openAction: openHostAction, reloadHosts: loadHosts, reloadConnections: loadConnections, origin: () => location.origin,
  snack: (text) => { if (M) M.snack(text); },
  installOverSsh: (conn, cmd) => { startDialog(conn, { prefill: cmd }); notice(t('shell.sshPrefilled', { name: conn.name })); } });
// Die Seite eines Hosts (Abschnitt panel): am Desktop eine Ansicht im Start-Fenster, am Handy eine eigene Seite
const openHostInfo = (h, panel) => (D ? D.openHost(h, panel) : M && M.openHost(h, panel));
const openAccount = initAccount({ api, setMsg });
securityUi = initSecurity({ api, esc });
const openSecurity = () => securityUi.open();
window.addEventListener('julos:open-security', (e) => securityUi.open(e.detail && e.detail.section));            // die Dateien-App fuehrt von "Schreiben ist aus" direkt zum Schalter
const openNetTools = initNetTools({ api, setMsg, connections: () => state.connections });
const openAlerts = initAlerts({ api, setMsg, esc, deviceLabel: () => describeAgent(navigator.userAgent), openHost: (id) => { const h = state.hosts.find((x) => x.id === id); if (h) openHostInfo(h); } });
// Ein Tipp auf eine Push-Meldung oeffnet die App auf der Seite des Hosts (/#host=<Kennung>): beim Start, bei offener App ueber hashchange und die Nachricht des Service Workers
function openLinkedHost() {
  const id = parseHostLink(location.hash);
  if (!id || !state.user) return;
  history.replaceState(history.state, '', location.pathname + location.search);
  const h = state.hosts.find((x) => x.id === id);
  if (h) openHostInfo(h);
}
window.addEventListener('hashchange', openLinkedHost);
if ('serviceWorker' in navigator) navigator.serviceWorker.addEventListener('message', (e) => { if (e.data && e.data.julos === 'open' && typeof e.data.hash === 'string' && e.data.hash) location.hash = e.data.hash; });
if (M) await M.init({ state, api, esc, describeAgent, ago, connect: startDialog, edit: editDialog, add: (kind) => editDialog(null, kind), program: launchProgram, quick: quickDialog, folders: openFolders, editHost: (h) => hostDialog(h), addHost: () => hostDialog(null), hostInfo: openHostInfo, hostPage, hostAction: openHostAction, info: hostInfo, tasks, signOut, home: goHome, account: openAccount, security: openSecurity, devices: openDevices, nettools: openNetTools, alerts: openAlerts });
if (D) await D.init({ state, esc, api, connect: startDialog, edit: editDialog, add: () => editDialog(null), program: launchProgram, quick: quickDialog, folders: openFolders, editHost: (h) => hostDialog(h), addHost: () => hostDialog(null), hostInfo: openHostInfo, hostPage, hostAction: openHostAction, info: hostInfo, tasks, devices: openDevices, account: openAccount, security: openSecurity, nettools: openNetTools, alerts: openAlerts, signOut });

/* ---------- Geraete und Sitzungen ---------- */
function describeAgent(ua) {
  if (!ua) return t('shell.unknownDevice');
  const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : t('shell.browser');
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows' : /Mac OS/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '';
  return os ? t('shell.browserOn', { browser, os }) : browser;
}
function ago(iso) {
  const rtf = relativeTime();
  const s = Math.round((new Date(iso).getTime() - Date.now()) / 1000);
  const abs = Math.abs(s);
  if (abs < 90) return t('shell.justNow');
  if (abs < 5400) return rtf.format(Math.round(s / 60), 'minute');
  if (abs < 172800) return rtf.format(Math.round(s / 3600), 'hour');
  return rtf.format(Math.round(s / 86400), 'day');
}

async function openDevices() {
  setMsg($('#devicesMsg'), '');
  showDialog($('#devicesDlg'), { system: true, width: 480 });
  await renderDevices();
}

async function renderDevices() {
  const list = $('#devicesList');
  try {
    const { sessions } = await api('/api/auth/sessions');
    list.replaceChildren();
    for (const s of sessions) {
      const row = document.createElement('div');
      row.className = 'dev' + (s.current ? ' current' : '');
      row.innerHTML = `<strong>${esc(describeAgent(s.userAgent))}${s.current ? ' · ' + esc(t('shell.thisDevice')) : ''}</strong><span>${esc(s.ip || '')} · ${esc(t('shell.lastSeen', { when: ago(s.lastSeen) }))}</span>`;
      list.appendChild(row);
    }
    $('#logoutOthers').hidden = sessions.length < 2;
  } catch (e) { setMsg($('#devicesMsg'), e.message); }
}

$('#logoutOthers').addEventListener('click', async () => {
  try { const r = await api('/api/auth/logout-others', { method: 'POST' }); setMsg($('#devicesMsg'), t('shell.sessionsEnded', { n: r.ended }), true); await renderDevices(); }
  catch (e) { setMsg($('#devicesMsg'), e.message); }
});

boot();
