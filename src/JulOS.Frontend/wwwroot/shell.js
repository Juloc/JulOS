import { openRemote, setLeaveHandler, disconnectRemote, ico } from './app.js';

/* =====================================================================
 * Startseite und Konto: Ersteinrichtung, Anmeldung, gespeicherte Verbindungen, Geraete.
 * Die Fernsteuerung selbst steckt in app.js; hier wird nur gewaehlt, was verbunden wird.
 * Kein Passwort wird gespeichert: das Passwort fuer ein Ziel wird bei jeder Verbindung getippt.
 * ===================================================================== */

const $ = (s) => document.querySelector(s);
const SCREENS = ['boot', 'setup', 'login', 'home', 'remote'];
const show = (name) => { for (const n of SCREENS) $('#' + n).hidden = n !== name; };

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

const state = { user: null, connections: [], editing: null, starting: null };

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
  await loadConnections();
}

async function loadConnections() {
  try { state.connections = (await api('/api/connections')).connections; }
  catch (e) { if (e.status === 401) return showLogin('Die Sitzung ist abgelaufen. Bitte neu anmelden.'); setMsg($('#homeMsg'), e.message); return; }
  renderConnections();
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
        <span class="conntext"><span class="connname">${esc(c.name)}</span><span class="connsub">${esc(KIND_LABEL[c.kind] || c.kind)} · ${esc(c.username ? c.username + '@' : '')}${esc(c.host)}:${c.port}</span></span></button>
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
  disconnectRemote();
  try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* Sitzung war schon weg */ }
  state.user = null; state.connections = [];
  showLogin();
}

/* ---------- Verbindung anlegen und aendern ---------- */
const DEFAULT_PORT = { desktop: '3389', terminal: '22' };
$('#connKind').addEventListener('change', () => { $('#connPort').placeholder = DEFAULT_PORT[$('#connKind').value]; });
$('#addConn').addEventListener('click', () => editDialog(null));

function editDialog(c) {
  state.editing = c;
  $('#connDlgTitle').textContent = c ? 'Verbindung ändern' : 'Neue Verbindung';
  $('#connName').value = c ? c.name : '';
  $('#connKind').value = c ? c.kind : 'desktop';
  $('#connHost').value = c ? c.host : '';
  $('#connPort').value = c ? String(c.port) : '';
  $('#connPort').placeholder = DEFAULT_PORT[$('#connKind').value];
  $('#connUser').value = c ? c.username : '';
  $('#connDelete').hidden = !c; $('#connDelete').textContent = 'Löschen';
  setMsg($('#connMsg'), '');
  $('#connDlg').showModal();
}

$('#connForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.currentTarget, msg = $('#connMsg');
  const portText = $('#connPort').value.trim();
  if (portText && !/^\d{1,5}$/.test(portText)) return setMsg(msg, 'Der Port muss eine Zahl sein.');
  const body = { name: $('#connName').value, kind: $('#connKind').value, host: $('#connHost').value, port: portText ? Number(portText) : null, username: $('#connUser').value };
  busy(form, true); setMsg(msg, '');
  try {
    const c = state.editing;
    await api(c ? `/api/connections/${c.id}` : '/api/connections', { method: c ? 'PUT' : 'POST', body });
    $('#connDlg').close();
    await loadConnections();
  } catch (err) { setMsg(msg, err.message); }
  finally { busy(form, false); }
});

// Loeschen braucht ein zweites Tippen (kein Browser-Dialog: die App laeuft oft im Vollbild)
$('#connDelete').addEventListener('click', async (e) => {
  const b = e.currentTarget;
  if (b.dataset.sure !== '1') { b.dataset.sure = '1'; b.textContent = 'Wirklich löschen?'; setTimeout(() => { b.dataset.sure = ''; b.textContent = 'Löschen'; }, 3000); return; }
  b.dataset.sure = '';
  try { await api(`/api/connections/${state.editing.id}`, { method: 'DELETE' }); $('#connDlg').close(); await loadConnections(); }
  catch (err) { setMsg($('#connMsg'), err.message); }
});

/* ---------- Verbinden ---------- */
function startDialog(c) {
  state.starting = c;
  $('#startTitle').textContent = c.name;
  $('#startUser').value = c.username;
  $('#startPw').value = '';
  $('#startDlg').showModal();
  setTimeout(() => $('#startPw').focus(), 150);
}

$('#startForm').addEventListener('submit', (e) => {
  e.preventDefault();
  const password = $('#startPw').value;
  if (!password) return;
  $('#startPw').value = '';
  $('#startDlg').close();
  const c = state.starting;
  show('remote');
  openRemote({ id: c.id, name: c.name, kind: c.kind }, password);
});

// Verbindung zu Ende (Fehler, Abbruch, "Zur Startseite"): zurueck zur Liste, mit Hinweis
setLeaveHandler((message, reason) => {
  if (reason === 'auth') return showLogin(message);
  showHome(state.user, message);
});

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
