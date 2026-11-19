import { ico } from './app.js';
import { Sessions } from './sessions.js';

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
  constructor(status, message, retryAfter) { super(message); this.status = status; this.retryAfter = retryAfter; }
}

/** Fragt den Server. Aenderungen tragen die Sonderkopfzeile, mit der der Server sie als von der eigenen Seite erkennt. */
async function api(path, { method = 'GET', body } = {}) {
  const headers = {};
  if (method !== 'GET') headers['X-JulOS'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  let r;
  try { r = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) }); }
  catch { throw new ApiError(0, 'Der Server ist nicht erreichbar.'); }
  let data = null;
  if (r.status !== 204) { try { data = await r.json(); } catch { /* kein JSON */ } }
  if (!r.ok) {
    const wait = data && data.retryAfter ? ` Bitte ${data.retryAfter} s warten.` : '';
    throw new ApiError(r.status, ((data && data.error) || 'Fehler ' + r.status) + (r.status === 429 ? wait : ''), data && data.retryAfter);
  }
  return data;
}

const setMsg = (el, text, ok = false) => { el.textContent = text || ''; el.classList.toggle('ok', ok && !!text); };
const busy = (form, on) => { for (const b of form.querySelectorAll('button[type=submit]')) b.disabled = on; };

const state = { user: null, connections: [], canStore: false, editing: null, starting: null };

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
  if (password !== $('#setupPw2').value) return setMsg(msg, 'Die beiden Passwörter sind nicht gleich.');
  busy(form, true); setMsg(msg, '');
  try {
    const r = await api('/api/setup/begin', { method: 'POST', body: { code, user, password } });
    setupInfo = { code, user };
    $('#setupSecret').textContent = r.secret;
    $('#setupOtpLink').href = r.uri;
    $('#setupRecovery').textContent = r.recovery.join('\n');
    $('#setupPw').value = ''; $('#setupPw2').value = '';
    form.hidden = true; $('#setupSecretForm').hidden = false; setTimeout(() => $('#setupTotp').focus(), 50);
  } catch (err) { setMsg(msg, err.message); }
  finally { busy(form, false); }
});

$('#setupCopy').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText($('#setupRecovery').textContent); setMsg($('#setupSecretMsg'), 'Codes kopiert.', true); }
  catch { setMsg($('#setupSecretMsg'), 'Kopieren geht hier nicht. Markiere die Codes und kopiere sie von Hand.'); }
});

$('#setupSecretForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.currentTarget, msg = $('#setupSecretMsg');
  busy(form, true); setMsg(msg, '');
  try {
    const r = await api('/api/setup/finish', { method: 'POST', body: { code: setupInfo.code, user: setupInfo.user, totp: $('#setupTotp').value.replace(/\s+/g, '') } });
    $('#setupSecret').textContent = ''; $('#setupRecovery').textContent = ''; $('#setupTotp').value = ''; setupInfo = null;
    showHome(r.user);
  } catch (err) { setMsg(msg, err.message); }
  finally { busy(form, false); }
});

/* ---------- Anmeldung ---------- */
function showLogin(message = '') {
  closeAllSessions();
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
    showHome(r.user, r.recoveryLeft <= 3 ? `Es sind nur noch ${r.recoveryLeft} Wiederherstellungscodes übrig.` : '');
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
  await loadConnections();
  if (D) D.userChanged(user, message);
}

async function loadConnections() {
  try { const r = await api('/api/connections'); state.connections = r.connections; state.canStore = !!r.canStorePasswords; }
  catch (e) { if (e.status === 401) return showLogin('Die Sitzung ist abgelaufen. Bitte neu anmelden.'); setMsg($('#homeMsg'), e.message); if (M) M.snack(e.message); return; }
  renderConnections();
  if (D) D.connectionsChanged();
  if (M) M.connectionsChanged();
}

const esc = (t) => String(t).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const KIND_LABEL = { desktop: 'Desktop', terminal: 'Terminal' };

function renderConnections() {
  const list = $('#connList');
  list.replaceChildren();
  $('#connEmpty').hidden = state.connections.length > 0;
  for (const c of state.connections) {
    const row = document.createElement('div');
    row.className = 'conn';
    row.innerHTML = `<button type="button" class="connmain"><span class="connic">${ico(c.kind === 'terminal' ? 'terminal' : 'desktop')}</span>
        <span class="conntext"><span class="connname">${esc(c.name)}</span><span class="connsub">${esc(KIND_LABEL[c.kind] || c.kind)} · ${esc(c.username ? c.username + '@' : '')}${esc(c.host)}:${c.port}${c.hasPassword ? `<span class="connkey" title="Passwort gespeichert">${ico('key')}</span>` : ''}</span></span></button>
      <button type="button" class="connedit ghost" aria-label="Bearbeiten">${ico('edit')}</button>`;
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
  else if (act === 'signout') signOut();
});

async function signOut() {
  closeAllSessions();
  try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* Sitzung war schon weg */ }
  state.user = null; state.connections = [];
  showLogin();
}

/* ---------- Verbindung anlegen und aendern ---------- */
const DEFAULT_PORT = { desktop: '3389', terminal: '22' };
const showEngineChoice = () => { const desktop = $('#connKind').value === 'desktop'; $('#connEngineRow').hidden = !desktop; $('#connEngineHint').hidden = !desktop || $('#connEngine').value !== 'stream'; };
$('#connKind').addEventListener('change', () => { $('#connPort').placeholder = DEFAULT_PORT[$('#connKind').value]; showEngineChoice(); });
$('#connEngine').addEventListener('change', showEngineChoice);
$('#addConn').addEventListener('click', () => editDialog(null));

function editDialog(c, kind) {
  state.editing = c;
  $('#connDlgTitle').textContent = c ? 'Verbindung ändern' : 'Neue Verbindung';
  $('#connName').value = c ? c.name : '';
  $('#connKind').value = c ? c.kind : kind || 'desktop';
  $('#connEngine').value = c && c.engine ? c.engine : M ? M.defaultEngine() : 'guac'; showEngineChoice();
  $('#connHost').value = c ? c.host : '';
  $('#connPort').value = c ? String(c.port) : '';
  $('#connPort').placeholder = DEFAULT_PORT[$('#connKind').value];
  $('#connUser').value = c ? c.username : '';
  $('#connDelete').hidden = !c; $('#connDelete').textContent = 'Löschen';
  $('#connPw').value = ''; $('#connPw').disabled = !state.canStore;
  $('#connForget').checked = false; $('#connForgetRow').hidden = !(c && c.hasPassword);
  $('#connPwHint').textContent = !state.canStore
    ? 'Passwörter speichern ist am Server nicht eingerichtet (JULOS_SECRET_KEY). Du tippst das Passwort bei jeder Verbindung.'
    : c && c.hasPassword
      ? 'Ein Passwort ist verschlüsselt gespeichert und wird nie angezeigt. Ein neues hier überschreibt es. Ändert sich Adresse, Port, Art oder Benutzer, wird es gelöscht.'
      : 'Auf Wunsch wird es verschlüsselt auf dem Server gespeichert, dann genügt ein Tipp zum Verbinden. Leer lassen: du tippst es jedes Mal.';
  setMsg($('#connMsg'), '');
  $('#connDlg').showModal();
}

$('#connForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.currentTarget, msg = $('#connMsg');
  const portText = $('#connPort').value.trim();
  if (portText && !/^\d{1,5}$/.test(portText)) return setMsg(msg, 'Der Port muss eine Zahl sein.');
  const body = { name: $('#connName').value, kind: $('#connKind').value, host: $('#connHost').value, port: portText ? Number(portText) : null, username: $('#connUser').value, engine: $('#connKind').value === 'desktop' ? $('#connEngine').value : 'guac' };
  const pw = $('#connPw').value;
  if (pw) body.password = pw;
  if ($('#connForget').checked && !pw) body.forgetPassword = true;
  busy(form, true); setMsg(msg, '');
  try {
    const c = state.editing;
    await api(c ? `/api/connections/${c.id}` : '/api/connections', { method: c ? 'PUT' : 'POST', body });
    $('#connPw').value = '';
    $('#connDlg').close();
    await loadConnections();
    if (M) M.snack(c ? 'Änderungen gespeichert' : 'Verbindung hinzugefügt');
  } catch (err) { setMsg(msg, err.message); }
  finally { busy(form, false); }
});

// Loeschen braucht ein zweites Tippen (kein Browser-Dialog: die App laeuft oft im Vollbild)
$('#connDelete').addEventListener('click', async (e) => {
  const b = e.currentTarget;
  if (b.dataset.sure !== '1') { b.dataset.sure = '1'; b.textContent = 'Wirklich löschen?'; setTimeout(() => { b.dataset.sure = ''; b.textContent = 'Löschen'; }, 3000); return; }
  b.dataset.sure = '';
  try { await api(`/api/connections/${state.editing.id}`, { method: 'DELETE' }); $('#connDlg').close(); await loadConnections(); if (M) M.snack('Verbindung gelöscht'); }
  catch (err) { setMsg($('#connMsg'), err.message); }
});

/* ---------- Verbinden ---------- */
const asConn = (c) => ({ id: c.id, name: c.name, kind: c.kind, engine: c.engine, stored: c.hasPassword ? true : undefined, target: `${c.host}:${c.port}/${c.username}` });

/** opts.extra: bewusst eine weitere Sitzung zum selben Ziel; sonst wird eine schon offene Sitzung nur nach vorn geholt. */
function startDialog(c, opts = {}) {
  if (!opts.extra && Sessions.find(c.id).length) { Sessions.open(asConn(c)); return; }   // laeuft schon: nach vorn, kein Passwort noetig
  state.starting = { c, extra: !!opts.extra };
  // Mit gespeichertem Passwort genuegt ein Tipp: der Server nimmt es aus dem verschluesselten Speicher
  if (c.hasPassword) { launch(c, '', !!opts.extra); return; }
  $('#startTitle').textContent = c.name;
  $('#startUser').value = c.username;
  $('#startPw').value = '';
  $('#startDlg').showModal();
  setTimeout(() => $('#startPw').focus(), 150);
}

/** Verbindung starten: der Verwalter legt die Sitzung an, die Huelle (Fenster am Desktop, Umschalter am Handy) zeigt sie. */
function launch(c, password, extra = false) {
  const before = Sessions.count;
  Sessions.open(asConn(c), { password, extra });
  if (M && Sessions.count > before) M.started(c);
}

$('#startForm').addEventListener('submit', (e) => {
  e.preventDefault();
  const password = $('#startPw').value;
  if (!password) return;
  $('#startPw').value = '';
  $('#startDlg').close();
  launch(state.starting.c, password, state.starting.extra);
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

if (M) await M.init({ state, api, esc, describeAgent, ago, connect: startDialog, edit: editDialog, add: (kind) => editDialog(null, kind), signOut, home: goHome });
if (D) await D.init({ state, esc, api, connect: startDialog, edit: editDialog, add: () => editDialog(null), devices: openDevices, signOut });

/* ---------- Geraete und Sitzungen ---------- */
function describeAgent(ua) {
  if (!ua) return 'Unbekanntes Gerät';
  const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows' : /Mac OS/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '';
  return os ? `${browser} auf ${os}` : browser;
}
const rtf = new Intl.RelativeTimeFormat('de', { numeric: 'auto' });
function ago(iso) {
  const s = Math.round((new Date(iso).getTime() - Date.now()) / 1000);
  const abs = Math.abs(s);
  if (abs < 90) return 'gerade eben';
  if (abs < 5400) return rtf.format(Math.round(s / 60), 'minute');
  if (abs < 172800) return rtf.format(Math.round(s / 3600), 'hour');
  return rtf.format(Math.round(s / 86400), 'day');
}

async function openDevices() {
  setMsg($('#devicesMsg'), '');
  $('#devicesDlg').showModal();
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
      row.innerHTML = `<strong>${esc(describeAgent(s.userAgent))}${s.current ? ' · dieses Gerät' : ''}</strong><span>${esc(s.ip || '')} · zuletzt ${esc(ago(s.lastSeen))}</span>`;
      list.appendChild(row);
    }
    $('#logoutOthers').hidden = sessions.length < 2;
  } catch (e) { setMsg($('#devicesMsg'), e.message); }
}

$('#logoutOthers').addEventListener('click', async () => {
  try { const r = await api('/api/auth/logout-others', { method: 'POST' }); setMsg($('#devicesMsg'), `${r.ended} Sitzung(en) abgemeldet.`, true); await renderDevices(); }
  catch (e) { setMsg($('#devicesMsg'), e.message); }
});

boot();
