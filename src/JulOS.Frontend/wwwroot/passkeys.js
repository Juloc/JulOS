/* =====================================================================
 * Passkeys im Browser (docs/PASSKEYS.md): Registrieren in Konto, Anmelden auf der Anmeldeseite (Knopf und Autofill) und die Freigabe fuer Aktionen.
 * Der Server prueft alles (Bibliothek Fido2); hier steht nur, was der Browser tun muss: Optionen des Servers (Base64url-Text) in die Form bringen, die
 * navigator.credentials verlangt, und die Antwort zurueck in Text. Die reine Umwandlung ist ohne DOM getestet (tests/frontend/passkeys.test.mjs).
 * ===================================================================== */

import { t } from './i18n.js';

// ---- reine Umwandlung ----

/** Base64url-Text -> Bytes (mit oder ohne Auffuellung). */
export function b64uToBytes(s) {
  const b = String(s).replace(/=+$/, '').replace(/-/g, '+').replace(/_/g, '/');
  const bin = atob(b + '='.repeat((4 - (b.length % 4)) % 4));
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** Bytes (ArrayBuffer oder Ansicht) -> Base64url-Text ohne Auffuellung. */
export function bytesToB64u(buf) {
  const bytes = buf instanceof ArrayBuffer ? new Uint8Array(buf) : new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
  let bin = '';
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** Entfernt null-Felder (der Server schreibt sie manchmal; der Browser nimmt sie bei Zahlen und Listen als Fehler). */
function dropNulls(v) {
  if (Array.isArray(v)) return v.map(dropNulls);
  if (v && typeof v === 'object') return Object.fromEntries(Object.entries(v).filter(([, x]) => x !== null && x !== undefined).map(([k, x]) => [k, dropNulls(x)]));
  return v;
}

/** Optionen zum Registrieren: Herausforderung, Benutzerkennung und ausgeschlossene Passkeys als Bytes. */
export function creationOptions(json) {
  const o = dropNulls(typeof json === 'string' ? JSON.parse(json) : json);
  return { ...o, challenge: b64uToBytes(o.challenge), user: { ...o.user, id: b64uToBytes(o.user.id) }, excludeCredentials: (o.excludeCredentials || []).map((c) => ({ ...c, id: b64uToBytes(c.id) })) };
}

/** Optionen zum Anmelden: Herausforderung als Bytes; eine leere Erlaubnisliste fehlt ganz (jeder Passkey dieser Seite darf antworten). */
export function requestOptions(json) {
  const o = dropNulls(typeof json === 'string' ? JSON.parse(json) : json);
  const allow = (o.allowCredentials || []).map((c) => ({ ...c, id: b64uToBytes(c.id) }));
  const out = { ...o, challenge: b64uToBytes(o.challenge) };
  if (allow.length) out.allowCredentials = allow; else delete out.allowCredentials;
  return out;
}

/** Die Antwort auf das Registrieren als Text fuer den Server. Erweiterungen benutzen wir nicht: clientExtensionResults ist leer. */
export function attestationJson(cred) {
  return { id: cred.id, rawId: bytesToB64u(cred.rawId), type: cred.type, response: { clientDataJSON: bytesToB64u(cred.response.clientDataJSON), attestationObject: bytesToB64u(cred.response.attestationObject) }, clientExtensionResults: {} };
}

/** Die Antwort auf das Anmelden als Text fuer den Server (der User-Handle fehlt, wenn der Passkey keinen mitschickt). */
export function assertionJson(cred) {
  const r = cred.response;
  const response = { authenticatorData: bytesToB64u(r.authenticatorData), clientDataJSON: bytesToB64u(r.clientDataJSON), signature: bytesToB64u(r.signature) };
  if (r.userHandle && r.userHandle.byteLength) response.userHandle = bytesToB64u(r.userHandle);
  return { id: cred.id, rawId: bytesToB64u(cred.rawId), type: cred.type, response, clientExtensionResults: {} };
}

/** Was ein Fehler von navigator.credentials fuer die Person heisst: 'cancelled' (abgebrochen, Zeit um), 'duplicate' (dieses Geraet hat schon einen), 'insecure' (keine sichere Seite oder falscher Name), sonst 'failed'. */
export function errorKind(e) {
  switch (e && e.name) {
    case 'NotAllowedError': case 'AbortError': return 'cancelled';
    case 'InvalidStateError': return 'duplicate';
    case 'SecurityError': return 'insecure';
    default: return 'failed';
  }
}

/** Die Meldung zu einem Fehler des Browsers (Fehler des Servers haben ihren eigenen Text). */
export function errorText(e) {
  switch (errorKind(e)) {
    case 'cancelled': return t('pk.cancelled');
    case 'duplicate': return t('pk.duplicate');
    case 'insecure': return t('pk.insecure');
    default: return t('pk.failed');
  }
}

/** Kann dieser Browser Passkeys? (Sichere Seite und WebAuthn.) */
export const supported = () => typeof window !== 'undefined' && !!window.PublicKeyCredential && !!navigator.credentials;

/** Kann er auch das Autofill ("conditional UI"), bei dem der Passkey im Feld fuer den Benutzernamen angeboten wird? */
export async function conditionalSupported() {
  try { return supported() && typeof PublicKeyCredential.isConditionalMediationAvailable === 'function' && await PublicKeyCredential.isConditionalMediationAvailable(); }
  catch { return false; }
}

// ---- Abläufe (api: Funktion wie rawApi in shell.js) ----

const post = (api, path, body = {}) => api(path, { method: 'POST', body });

/** Neuen Passkey anlegen. Der Server verlangt dafuer eine frische Freigabe; wird sie gebraucht, fragt die Seite danach (api wiederholt den Aufruf). */
export async function registerPasskey(api, name) {
  const options = await post(api, '/api/auth/passkeys/register/begin');
  const cred = await navigator.credentials.create({ publicKey: creationOptions(options) });
  return post(api, '/api/auth/passkeys/register/finish', { name, credential: attestationJson(cred) });
}

/** Mit Passkey anmelden, ohne Benutzername. conditional: Autofill (wartet, bis die Person im Feld einen Passkey waehlt; signal bricht ab). */
export async function signInWithPasskey(api, { conditional = false, signal } = {}) {
  const options = await post(api, '/api/auth/passkey/login/begin');
  const cred = await navigator.credentials.get({ publicKey: requestOptions(options), ...(conditional ? { mediation: 'conditional' } : {}), signal });
  return post(api, '/api/auth/passkey/login/finish', { credential: assertionJson(cred) });
}

/** Aktionen freigeben mit Passkey statt Code (api OHNE Wiederholung: die Freigabe selbst darf nicht wieder nach einer Freigabe fragen). */
export async function confirmWithPasskey(api) {
  const options = await post(api, '/api/auth/step-up/passkey/begin');
  const cred = await navigator.credentials.get({ publicKey: requestOptions(options) });
  return post(api, '/api/auth/step-up/passkey/finish', { credential: assertionJson(cred) });
}

// ---- Anmeldeseite ----

/**
 * Knopf "Mit Passkey anmelden" und Autofill. start() beim Zeigen der Anmeldeseite, stop() beim Verlassen. Der Server prueft; onSignedIn bekommt seine Antwort.
 * Die offene Anfrage des Autofills wird vor Ablauf der Herausforderung (5 Minuten) erneuert und beim Tippen auf den Knopf oder beim Absenden des Formulars abgebrochen.
 */
export function initLoginPasskeys({ api, setMsg, onSignedIn }) {
  const button = document.querySelector('#loginPasskey'), or = document.querySelector('#loginPasskeyOr'), msg = document.querySelector('#loginMsg');
  let ctrl = null, timer = 0, running = false;

  const stop = () => { running = false; clearTimeout(timer); if (ctrl) { ctrl.abort(); ctrl = null; } };

  async function autofill() {
    if (!running || !(await conditionalSupported()) || !running) return;
    ctrl = new AbortController();
    const mine = ctrl;
    timer = setTimeout(() => { if (ctrl === mine) { mine.abort(); autofill(); } }, 4 * 60 * 1000);
    try { const r = await signInWithPasskey(api, { conditional: true, signal: mine.signal }); stop(); onSignedIn(r); }
    catch (e) {
      if (ctrl !== mine) return;                                   // abgebrochen oder erneuert: kein Fehler
      if (e && e.status) setMsg(msg, e.message);                  // der Server hat abgelehnt
      // sonst: die Person hat nichts gewaehlt; das Formular bleibt wie es ist
    }
  }

  if (button) {
    const ok = supported();
    button.hidden = !ok; if (or) or.hidden = !ok;
    button.addEventListener('click', async () => {
      if (ctrl) { ctrl.abort(); ctrl = null; }
      clearTimeout(timer);
      button.disabled = true; setMsg(msg, '');
      try { const r = await signInWithPasskey(api); stop(); onSignedIn(r); }
      catch (e) { setMsg(msg, e && e.status ? e.message : errorText(e)); if (running) autofill(); }
      finally { button.disabled = false; }
    });
  }
  return { start() { stop(); running = true; if (supported()) autofill(); }, stop };
}

// ---- Konto: Liste ----

const fmt = (iso) => new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: '2-digit', day: '2-digit' });
const el = (tag, attrs = {}, ...kids) => { const n = Object.assign(document.createElement(tag), attrs); n.append(...kids); return n; };

/**
 * Die Liste der Passkeys im Konto-Dialog (#pkSection): anlegen, umbenennen, entfernen. api wiederholt bei Bedarf nach einer Freigabe (stepup.js).
 * Gibt refresh() zurueck, das beim Oeffnen des Dialogs aufgerufen wird.
 */
export function initPasskeyList({ api, setMsg }) {
  const section = document.querySelector('#pkSection');
  if (!section) return () => {};
  const list = document.querySelector('#pkList'), msg = document.querySelector('#pkMsg'), form = document.querySelector('#pkForm'), name = document.querySelector('#pkName'), note = document.querySelector('#pkNote');
  const busy = (on) => { for (const b of form.querySelectorAll('button')) b.disabled = on; };

  function row(p) {
    const label = el('span', { className: 'pk-name' }, p.name);
    const meta = el('span', { className: 'pk-meta' },
      [t('pk.created', { date: fmt(p.createdAt) }), p.lastUsedAt ? t('pk.lastUsed', { date: fmt(p.lastUsedAt) }) : t('pk.neverUsed'), p.synced ? t('pk.synced') : t('pk.deviceBound')].join(' · '));
    const rename = el('button', { type: 'button', className: 'ghost pk-act', textContent: t('pk.rename') });
    const remove = el('button', { type: 'button', className: 'ghost pk-act', textContent: t('pk.remove') });
    const li = el('li', { className: 'pk-row' }, el('div', { className: 'pk-text' }, label, meta), el('div', { className: 'pk-acts' }, rename, remove));

    rename.addEventListener('click', () => {
      const input = el('input', { type: 'text', value: p.name, maxLength: 60, className: 'pk-edit' });
      const save = el('button', { type: 'button', className: 'primary pk-act', textContent: t('common.save') });
      const cancel = el('button', { type: 'button', className: 'ghost pk-act', textContent: t('common.cancel') });
      li.replaceChildren(el('div', { className: 'pk-text' }, input), el('div', { className: 'pk-acts' }, save, cancel));
      input.focus(); input.select();
      cancel.addEventListener('click', refresh);
      const go = async () => {
        setMsg(msg, '');
        try { await api('/api/auth/passkeys/' + p.id, { method: 'PUT', body: { name: input.value } }); await refresh(); setMsg(msg, t('pk.renamed'), true); }
        catch (e) { setMsg(msg, e.message); }
      };
      save.addEventListener('click', go);
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); go(); } else if (e.key === 'Escape') { e.stopPropagation(); refresh(); } });
    });

    let armed = 0;
    remove.addEventListener('click', async () => {
      if (!armed) {                                                  // zweiter Tipp noetig: ein Passkey ist schnell weg und nicht zurueckzuholen
        remove.textContent = t('pk.removeSure'); remove.classList.add('danger');
        armed = setTimeout(() => { armed = 0; remove.textContent = t('pk.remove'); remove.classList.remove('danger'); }, 4000);
        return;
      }
      clearTimeout(armed); armed = 0; setMsg(msg, '');
      try { await api('/api/auth/passkeys/' + p.id, { method: 'DELETE' }); await refresh(); setMsg(msg, t('pk.removed'), true); }
      catch (e) { setMsg(msg, e.message); refresh(); }
    });
    return li;
  }

  async function refresh() {
    setMsg(msg, '');
    let r;
    try { r = await api('/api/auth/passkeys'); } catch (e) { setMsg(msg, e.message); return; }
    list.replaceChildren(...r.passkeys.map(row));
    const can = supported() && r.available && r.passkeys.length < r.max;
    form.hidden = !can;
    note.textContent = !r.available ? t('pk.unavailable') : !supported() ? t('pk.unsupported') : r.passkeys.length >= r.max ? t('pk.limit', { n: r.max }) : r.passkeys.length ? '' : t('pk.none');
  }

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    busy(true); setMsg(msg, '');
    try { await registerPasskey(api, name.value.trim() || t('pk.defaultName')); name.value = ''; await refresh(); setMsg(msg, t('pk.added'), true); }
    catch (err) { setMsg(msg, err && err.status ? err.message : errorText(err)); }
    finally { busy(false); }
  });
  return refresh;
}
