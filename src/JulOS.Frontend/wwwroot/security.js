import { t, tp, tk, fmtDate } from './i18n.js';
import { showDialog } from './desktop-window.js';

/* =====================================================================
 * Einstellungen, Sicherheit (docs/SECURITY-REVIEW.md, F-01 bis F-04): vertraute Proxy-Adressen, erlaubte Zielhosts, zweiter Faktor fuer Aktionen, Schluessel im Backup
 * und Wiederherstellungsschluessel, Verlauf. Gilt fuer Handy (Sheet) und Desktop (Fenster) gleich: der Dialog #securityDlg steht in index.html.
 * Der obere Teil ist reines JavaScript (Aufbereitung und Markup, tests/frontend/security.test.mjs), initSecurity bindet den Dialog. Alles Geschriebene prueft und
 * speichert der Server (er verlangt dafuer ausserdem die Freigabe mit dem zweiten Faktor, stepup.js); die Seite zeigt nur und fragt vorher nach.
 * ===================================================================== */

/** Eine Eingabe (Zeilen, Kommas, Leerzeichen) als Liste. */
export const parseList = (text) => String(text || '').split(/[\s,;]+/).filter(Boolean);
export const listText = (entries) => (entries || []).join('\n');

export const SOURCES = { app: tk('sec.sourceApp'), env: tk('sec.sourceEnv'), default: tk('sec.sourceDefault') };
export const sourceText = (source) => t(SOURCES[source] || SOURCES.default);

/** Die schnelle Auswahl der Dauer: 0 heisst "bei jeder Aktion fragen", sonst Minuten (der Server nimmt 0 bis 60; ein anderer, frueher eingestellter Wert bleibt als eigener Eintrag sichtbar). */
export const MINUTES = [0, 10, 30, 60];
/** Wie gefaehrliche Aktionen bestaetigt werden (docs/HOST-AGENT.md): die Reihenfolge der Auswahl, Haekchen ist der Standard. */
export const CONFIRM_MODES = ['check', 'type', 'click'];
const CONFIRM_TEXT = { check: [tk('sec.confirm.check'), tk('sec.confirm.checkHelp')], type: [tk('sec.confirm.type'), tk('sec.confirm.typeHelp')], click: [tk('sec.confirm.click'), tk('sec.confirm.clickHelp')] };
export const minutesText = (m) => (m === 0 ? t('sec.always') : tp('sec.minutes', m));

/** Der Zugriff gerade: "von Adresse X ueber Proxy Y" oder direkt. */
export function clientText(c) {
  if (!c) return '';
  return c.forwarded ? t('sec.clientProxy', { address: c.address, proxy: c.peer }) : t('sec.clientDirect', { address: c.address });
}

/** Steht die Adresse des Proxys, ueber den der Zugriff gerade kommt, in der eingegebenen Liste (nur exakte Adresse oder /32, /128; genauer prueft es der Server)? */
export function peerListed(entries, peer) {
  return (entries || []).some((e) => e === peer || e === peer + '/32' || e === peer + '/128');
}

/** Der Wiederherstellungsschluessel als Satz mit naechstem Schritt: { tone, chip, line, next }. */
export function recoveryInfo(r, canProtect) {
  if (!canProtect) return { tone: 'warn', chip: t('set.notSetUp'), line: t('sec.recNoKey'), next: '' };
  switch (r && r.state) {
    case 'confirmed': return { tone: 'ok', chip: t('set.ok'), line: t('sec.recConfirmed', { at: fmtDate(r.confirmedAt) }), next: '' };
    case 'pending': return { tone: 'warn', chip: t('set.planned'), line: t('sec.recPending'), next: t('sec.recPendingNext') };
    default: return { tone: 'warn', chip: t('set.notSetUp'), line: t('sec.recNone'), next: t('sec.recNoneNext') };
  }
}

const KEY_ON = tk('sec.keyOn'), KEY_OFF = tk('sec.keyOff');
const KEY_WHERE = { app: tk('sec.keyApp'), env: tk('sec.keyEnv'), legacy: tk('sec.keyLegacy'), default: tk('sec.keyDefault') };

/** Was mit dem Schluessel im Backup gerade gilt (Quelle: app, env, legacy, default). */
export function keyInfo(b) {
  if (!b) return { tone: 'info', line: '', warn: false };
  return { tone: b.copyKey ? 'warn' : 'ok', line: t(b.copyKey ? KEY_ON : KEY_OFF) + ' ' + t(KEY_WHERE[b.source] || KEY_WHERE.default), warn: !!b.copyKey };
}

const EVENTS = {
  'proxies.set': tk('sec.ev.proxies_set'), 'proxies.reset': tk('sec.ev.proxies_reset'), 'hosts.set': tk('sec.ev.hosts_set'), 'hosts.reset': tk('sec.ev.hosts_reset'),
  'confirm_mode.set': tk('sec.ev.confirm_mode_set'), 'files_write.set': tk('sec.ev.files_write_set'), 'step_up.set': tk('sec.ev.step_up_set'), 'step_up.ok': tk('sec.ev.step_up_ok'), 'step_up.failed': tk('sec.ev.step_up_failed'),
  'backup_key.set': tk('sec.ev.backup_key_set'), 'recovery.new': tk('sec.ev.recovery_new'), 'recovery.confirm': tk('sec.ev.recovery_confirm'),
};

/** Die Zeilen des Verlaufs: Zeit, wer, was (ein unbekanntes Ereignis erscheint mit seinem Namen). */
export function auditLine(e) {
  const detail = e.event === 'confirm_mode.set' && CONFIRM_TEXT[e.detail] ? t(CONFIRM_TEXT[e.detail][0]) : (e.detail || '');
  return { at: fmtDate(e.at), who: e.user, what: EVENTS[e.event] ? t(EVENTS[e.event]) : e.event, detail };
}

/**
 * Das Markup des Dialogs. state: Antwort von GET /api/security (oder null); ui: { proxyText, hostText, step: {enabled, minutes}, blocked, proxyAsk, recoveryKey, audit, msg: {proxies, hosts, step, backup} }.
 * Die Bedienung traegt data-sec="..." (initSecurity). Alles vom Server und aus der Eingabe geht durch esc.
 */
export function securityMarkup(state, ui, esc) {
  if (!state) return `<p class="hint">${esc(ui.error || t('hostinfo.stLoading'))}</p>`;
  const m = ui.msg || {};
  const msg = (k) => `<p class="msg${m[k] && m[k].ok ? ' ok' : ''}" role="alert">${esc((m[k] && m[k].text) || '')}</p>`;
  const btn = (act, label, cls = 'ghost', extra = '') => `<button type="button" class="${cls}" data-sec="${act}"${extra}>${esc(label)}</button>`;
  const area = (id, value, rows) => `<textarea id="${id}" rows="${rows}" spellcheck="false" autocapitalize="none" autocomplete="off" autocorrect="off" maxlength="4000">${esc(value)}</textarea>`;
  const entries = parseList(ui.proxyText);
  const client = state.client;

  const warn = state.proxiesUnset ? `<p class="sec-warn" role="status">${esc(t('sec.proxyWarn'))}</p>` : '';
  const peerHint = client && client.forwarded && !peerListed(entries, client.peer)
    ? `<p class="hint">${esc(t('sec.peerMissing', { proxy: client.peer }))} ${btn('addpeer', t('sec.addPeer', { proxy: client.peer }), 'ghost', ' data-peer="' + esc(client.peer) + '"')}</p>` : '';
  const proxyAsk = ui.proxyAsk ? `<p class="sec-warn" role="alert">${esc(t('sec.proxyAsk', { proxy: ui.proxyAsk }))}</p><div class="dlg-btns">${btn('save-proxies-confirm', t('sec.saveAnyway'), 'primary')}</div>` : '';
  const proxies = `<section class="sec-sec"><h3>${esc(t('sec.proxies'))}</h3>
    <p class="hint">${esc(t('sec.proxiesHelp'))}</p>${warn}
    <p class="hint"><b>${esc(clientText(client))}</b></p>${peerHint}
    <label>${esc(t('sec.proxiesLabel'))}${area('secProxies', ui.proxyText, 4)}</label>
    <p class="hint">${esc(t('sec.source', { source: sourceText(state.proxies.source) }))}</p>
    <div class="dlg-btns">${btn('save-proxies', t('common.save'), 'primary')}${btn('reset-proxies', t('sec.reset'))}</div>${proxyAsk}${msg('proxies')}</section>`;

  const blocked = ui.blocked && ui.blocked.length
    ? `<div class="sec-warn" role="alert"><p>${esc(t('sec.blocked', { n: ui.blocked.length }))}</p><ul>${ui.blocked.map((b) => `<li>${esc(b.name)} (${esc(b.host)})</li>`).join('')}</ul>${ui.blockedAsk ? `<div class="dlg-btns">${btn('save-hosts-confirm', t('sec.saveAnyway'), 'primary')}</div>` : ''}</div>`
    : (ui.blocked ? `<p class="msg ok">${esc(t('sec.blockedNone'))}</p>` : '');
  const hosts = `<section class="sec-sec"><h3>${esc(t('sec.hosts'))}</h3>
    <p class="hint">${esc(t('sec.hostsHelp'))}</p>
    <label>${esc(t('sec.hostsLabel'))}${area('secHosts', ui.hostText, 4)}</label>
    <p class="hint">${esc(t('sec.source', { source: sourceText(state.hosts.source) }))}${parseList(ui.hostText).length ? '' : ' ' + esc(t('sec.hostsEmpty'))}</p>
    ${blocked}
    <div class="dlg-btns">${btn('save-hosts', t('common.save'), 'primary')}${btn('check-hosts', t('sec.check'))}${btn('reset-hosts', t('sec.reset'))}</div>${msg('hosts')}</section>`;

  const st = ui.step || state.stepUp;
  const step = `<section class="sec-sec"><h3>${esc(t('sec.stepUp'))}</h3>
    <p class="hint">${esc(t('sec.stepUpHelp'))}</p>
    <label class="sec-check"><span><input id="secStepOn" type="checkbox"${st.enabled ? ' checked' : ''}> ${esc(t('sec.stepUpOn'))}</span></label>
    <p class="hint">${esc(t('sec.stepUpFor'))}</p>
    <div class="sec-chips" id="secStepMin" role="radiogroup" aria-label="${esc(t('sec.stepUpFor'))}">${(MINUTES.includes(st.minutes) ? MINUTES : [...MINUTES, st.minutes].sort((a, b) => a - b)).map((n) => `<button type="button" class="sec-chip${n === st.minutes ? ' on' : ''}" role="radio" aria-checked="${n === st.minutes}" data-sec="step-min" data-min="${n}"${st.enabled ? '' : ' disabled'}>${esc(minutesText(n))}</button>`).join('')}</div>
    <p class="hint">${esc(t('sec.stepUpLonger'))}</p>
    ${st.enabled ? '' : `<p class="sec-warn">${esc(t('sec.stepUpOffWarn'))}</p>`}
    <div class="dlg-btns">${btn('save-step', t('common.save'), 'primary')}</div>${msg('step')}</section>`;

  const cm = ui.confirmMode || state.confirmMode || 'check';
  const confirm = `<section class="sec-sec"><h3>${esc(t('sec.confirm'))}</h3>
    <p class="hint">${esc(t('sec.confirmHelp'))}</p>
    <div class="sec-radios" role="radiogroup" aria-label="${esc(t('sec.confirm'))}">${CONFIRM_MODES.map((m) => `<label class="sec-radio"><input type="radio" name="secConfirm" value="${m}"${m === cm ? ' checked' : ''}><span><b>${esc(t(CONFIRM_TEXT[m][0]))}</b><span class="hint">${esc(t(CONFIRM_TEXT[m][1]))}</span></span></label>`).join('')}</div>
    ${cm === 'click' ? `<p class="sec-warn" role="status">${esc(t('sec.confirmClickWarn'))}</p>` : ''}
    <div class="dlg-btns">${btn('save-confirm', t('common.save'), 'primary')}</div>${msg('confirm')}</section>`;

  const fw = ui.filesWrite ?? (state.filesWrite && state.filesWrite.enabled) ?? false, fwSrc = (state.filesWrite && state.filesWrite.source) || 'default';
  const filesWrite = `<section class="sec-sec" id="secSecFilesWrite"><h3>${esc(t('sec.filesWrite'))}</h3>
    <p class="hint">${esc(t('sec.filesWriteHelp'))}</p>
    <label class="sec-check"><span><input id="secFilesWrite" type="checkbox"${fw ? ' checked' : ''}> ${esc(t('sec.filesWriteOn'))}</span></label>
    <p class="hint">${esc(t('sec.source', { source: sourceText(fwSrc) }))}</p>
    ${fw ? `<p class="hint" role="status">${esc(t('sec.filesWriteWarn'))}</p>` : ''}
    <div class="dlg-btns">${btn('save-files-write', t('common.save'), 'primary')}${fwSrc === 'app' ? btn('reset-files-write', t('sec.reset')) : ''}</div>${msg('filesWrite')}</section>`;

  const bk = state.backupKey, ki = keyInfo(bk), ri = recoveryInfo(bk.recovery, bk.canProtect);
  const recBox = ui.recoveryKey
    ? `<div class="sec-key"><p class="hint">${esc(t('sec.recShown'))}</p><code class="mono" id="secRecKey">${esc(ui.recoveryKey)}</code>
       <div class="dlg-btns">${btn('copy-key', t('sec.copy'))}${btn('download-key', t('sec.download'))}${btn('confirm-key', t('sec.recSaved'), 'primary')}</div></div>` : '';
  const backup = `<section class="sec-sec"><h3>${esc(t('sec.backup'))}</h3>
    <p class="hint">${esc(t('sec.backupHelp'))}</p>
    <p class="${ri.tone === 'ok' ? 'msg ok' : 'hint'}"><b>${esc(t('sec.recTitle'))}:</b> ${esc(ri.line)} ${ri.next ? esc(ri.next) : ''}</p>
    ${recBox}
    ${ui.recoveryKey ? '' : `<div class="dlg-btns">${bk.canProtect ? btn('new-key', bk.recovery.state === 'none' ? t('sec.recCreate') : t('sec.recRenew'), 'primary') : ''}</div>`}
    <label class="sec-check"><span><input id="secCopyKey" type="checkbox"${(ui.copyKey ?? bk.copyKey) ? ' checked' : ''}> ${esc(t('sec.copyKeyLabel'))}</span></label>
    <p class="${ki.warn ? 'sec-warn' : 'hint'}">${esc(ki.line)}</p>
    ${(ui.copyKey ?? bk.copyKey) ? `<p class="sec-warn">${esc(t('sec.copyKeyWarn'))}</p>` : ''}
    <div class="dlg-btns">${btn('save-backup', t('common.save'), 'primary')}</div>${msg('backup')}</section>`;

  const audit = (ui.audit || []).length
    ? `<section class="sec-sec"><h3>${esc(t('sec.audit'))}</h3><ul class="sec-audit">${ui.audit.map((e) => { const l = auditLine(e); return `<li><span>${esc(l.at)}</span> <b>${esc(l.what)}</b> ${esc(l.who)}${l.detail ? ' · ' + esc(l.detail) : ''}</li>`; }).join('')}</ul></section>` : '';

  return proxies + hosts + step + confirm + filesWrite + backup + audit;
}

/** Bindet den Dialog #securityDlg. api ist die Funktion mit Freigabe-Wiederholung (shell.js), setMsg und esc kommen von dort. Gibt zurueck: { open, warn } (warn: den Hinweis fuer den Start-Bildschirm pruefen). */
export function initSecurity({ api, esc }) {
  const $ = (s) => document.querySelector(s);
  const dlg = $('#securityDlg'), body = $('#secBody');
  let state = null, scrollTo = '';
  let ui = fresh();

  function fresh() { return { proxyText: '', hostText: '', step: null, confirmMode: null, filesWrite: undefined, blocked: null, blockedAsk: false, proxyAsk: '', recoveryKey: '', copyKey: undefined, audit: [], msg: {}, error: '' }; }
  const say = (k, text, ok = false) => { ui.msg[k] = { text, ok }; render(); };
  const render = () => { body.innerHTML = securityMarkup(state, ui, esc); };

  async function load(keepText = false) {
    try {
      state = await api('/api/security');
      const a = await api('/api/security/audit?limit=10').catch(() => ({ entries: [] }));
      ui.audit = a.entries || [];
      if (!keepText) { ui.proxyText = listText(state.proxies.entries); ui.hostText = listText(state.hosts.entries); }
      ui.step = { ...state.stepUp }; ui.copyKey = undefined; ui.filesWrite = undefined; ui.confirmMode = state.confirmMode || 'check';
      ui.error = '';
    } catch (e) { ui.error = e.message; }
    render();
    if (state) { unset = !!state.proxiesUnset; syncHint(); }
    if (scrollTo) { const el = body.querySelector('#' + scrollTo); scrollTo = ''; if (el) el.scrollIntoView({ block: 'start' }); }
  }

  // Eingaben merken, damit ein Neuzeichnen sie nicht loescht
  body.addEventListener('input', (e) => {
    if (e.target.id === 'secProxies') { ui.proxyText = e.target.value; ui.proxyAsk = ''; }
    else if (e.target.id === 'secHosts') { ui.hostText = e.target.value; ui.blocked = null; ui.blockedAsk = false; }
  });
  body.addEventListener('change', (e) => {
    if (e.target.id === 'secStepOn') { ui.step = { ...ui.step, enabled: e.target.checked }; render(); }
    else if (e.target.name === 'secConfirm') { ui.confirmMode = e.target.value; render(); }
    else if (e.target.id === 'secFilesWrite') { ui.filesWrite = e.target.checked; render(); }
    else if (e.target.id === 'secCopyKey') { ui.copyKey = e.target.checked; render(); }
  });

  const put = (path, bodyObj) => api(path, { method: 'PUT', body: bodyObj });

  async function saveProxies(confirm) {
    try {
      await put('/api/security/proxies', { entries: parseList(ui.proxyText), confirm: confirm || undefined });
      ui.proxyAsk = ''; await load(); say('proxies', t('sec.saved'), true);
    } catch (e) {
      if (e.code === 'proxy_not_included') { ui.proxyAsk = (e.data && e.data.peer) || ''; render(); }
      else say('proxies', e.message);
    }
  }
  async function saveHosts(confirm) {
    try {
      await put('/api/security/hosts', { entries: parseList(ui.hostText), confirm: confirm || undefined });
      ui.blocked = null; ui.blockedAsk = false; await load(); say('hosts', t('sec.saved'), true);
    } catch (e) {
      if (e.code === 'blocked_connections') { ui.blocked = (e.data && e.data.blocked) || []; ui.blockedAsk = true; render(); }
      else say('hosts', e.message);
    }
  }

  const ACTIONS = {
    'addpeer': (b) => { const peer = b.dataset.peer; ui.proxyText = listText([...parseList(ui.proxyText), peer]); render(); },
    'save-proxies': () => saveProxies(false),
    'save-proxies-confirm': () => saveProxies(true),
    'reset-proxies': async () => { try { await api('/api/security/proxies', { method: 'DELETE' }); ui.proxyAsk = ''; await load(); say('proxies', t('sec.resetDone'), true); } catch (e) { say('proxies', e.message); } },
    'save-hosts': () => saveHosts(false),
    'save-hosts-confirm': () => saveHosts(true),
    'check-hosts': async () => {
      try { const r = await api('/api/security/hosts/preview', { method: 'POST', body: { entries: parseList(ui.hostText) } }); ui.blocked = r.blocked; ui.blockedAsk = false; render(); }
      catch (e) { say('hosts', e.message); }
    },
    'reset-hosts': async () => { try { await api('/api/security/hosts', { method: 'DELETE' }); ui.blocked = null; await load(); say('hosts', t('sec.resetDone'), true); } catch (e) { say('hosts', e.message); } },
    'save-step': async () => {
      try { await put('/api/security/step-up', { enabled: !!ui.step.enabled, minutes: ui.step.minutes }); await load(); say('step', t('sec.saved'), true); }
      catch (e) { say('step', e.message); }
    },
    'step-min': (b) => { ui.step = { ...ui.step, minutes: Number(b.dataset.min) }; render(); },
    'save-confirm': async () => {
      try { await put('/api/security/confirm-mode', { mode: ui.confirmMode }); await load(); say('confirm', t('sec.saved'), true); }
      catch (e) { say('confirm', e.message); }
    },
    'save-files-write': async () => {
      try { await put('/api/security/files-write', { enabled: ui.filesWrite ?? !!(state.filesWrite && state.filesWrite.enabled) }); await load(); say('filesWrite', t('sec.saved'), true); }
      catch (e) { say('filesWrite', e.message); }
    },
    'reset-files-write': async () => {
      try { await put('/api/security/files-write', { enabled: null }); await load(); say('filesWrite', t('sec.resetDone'), true); }
      catch (e) { say('filesWrite', e.message); }
    },
    'save-backup': async () => {
      try { await put('/api/security/backup-key', { copyKey: ui.copyKey ?? state.backupKey.copyKey }); await load(); say('backup', t('sec.saved'), true); }
      catch (e) { say('backup', e.message); }
    },
    'new-key': async () => {
      try { const r = await api('/api/security/recovery', { method: 'POST', body: {} }); ui.recoveryKey = r.key; await load(true); }
      catch (e) { say('backup', e.message); }
    },
    'copy-key': async () => {
      try { await navigator.clipboard.writeText(ui.recoveryKey); say('backup', t('sec.copied'), true); }
      catch { say('backup', t('account.copyFailed')); }
    },
    'download-key': () => {
      const url = URL.createObjectURL(new Blob([t('sec.keyFile', { key: ui.recoveryKey }) + '\n'], { type: 'text/plain' }));
      const a = document.createElement('a'); a.href = url; a.download = 'julos-recovery-key.txt'; document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    'confirm-key': async () => {
      try { await api('/api/security/recovery/confirm', { method: 'POST', body: {} }); ui.recoveryKey = ''; await load(true); say('backup', t('sec.recDone'), true); }
      catch (e) { say('backup', e.message); }
    },
  };
  body.addEventListener('click', (e) => { const b = e.target.closest('[data-sec]'); if (b && ACTIONS[b.dataset.sec]) ACTIONS[b.dataset.sec](b); });

  const reset = () => { ui = fresh(); state = null; body.replaceChildren(); };       // nichts Geheimes bleibt nach dem Schliessen (der Wiederherstellungsschluessel zuerst)
  dlg.addEventListener('close', reset);
  dlg.addEventListener('click', (e) => { if (e.target.closest('[data-act=close]')) dlg.close(); });

  /** Start-Bildschirm: ein dezenter Hinweis, solange die Proxy-Liste weder in der App noch in der Umgebung gesetzt ist; nur dort, nicht in einer Fernsteuer-Sitzung. */
  const hint = $('#secHint');
  let unset = false, homeShown = false;
  const syncHint = () => { if (hint) hint.hidden = !(homeShown && unset); };
  async function refreshHint() {
    try { unset = !!(state || await api('/api/security')).proxiesUnset; } catch { unset = false; }
    syncHint();
  }
  if (hint) {
    $('#secHintOpen').addEventListener('click', () => open());
    $('#secHintClose').addEventListener('click', () => { unset = false; syncHint(); });
  }

  /** section: die Kennung eines Abschnitts, zu dem nach dem Laden gescrollt wird (die Dateien-App fuehrt so zum Schalter "Dateien schreiben") */
  function open(section) { reset(); scrollTo = section === 'filesWrite' ? 'secSecFilesWrite' : ''; render(); showDialog(dlg, { system: true, width: 600, resizable: true }); load(); }
  return { open, check: refreshHint, setHomeShown: (on) => { homeShown = on; syncHint(); } };
}
