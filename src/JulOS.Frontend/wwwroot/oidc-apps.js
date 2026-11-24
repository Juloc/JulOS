import { t, tk, fmtDate } from './i18n.js';
import { showDialog } from './desktop-window.js';

/* =====================================================================
 * Einstellungen, Sicherheit: Anmelde-Zentrale (docs/SIGN-IN.md). Apps anlegen, Geheimnis neu erzeugen, sperren, loeschen; je Person und App eine Rolle; die oeffentliche Adresse;
 * die Signaturschluessel. Alles unter /api/security/oidc: nur der Eigentuemer, jede Aenderung mit dem zweiten Faktor (der Server verlangt ihn, stepup.js fragt) und im Verlauf.
 * Gefaehrliches (Geheimnis neu, sperren, loeschen, Schluessel drehen) fragt zusaetzlich nach, so wie es "Bestaetigung bei gefaehrlichen Aktionen" einstellt (Haekchen, Namen tippen, Klick).
 * Das Geheimnis einer App wird einmal gezeigt und nirgends gespeichert. Der Dialog wird hier gebaut (kein Eintrag in index.html); die Teile oben sind reines JavaScript.
 * ===================================================================== */

/** Eingabe als Liste: bei Adressen nur Leerraum trennt (eine Adresse darf Kommas enthalten), bei Rollen auch Komma und Semikolon. */
export const parseUris = (text) => String(text || '').split(/\s+/).filter(Boolean);
export const parseRoles = (text) => String(text || '').split(/[\s,;]+/).filter(Boolean);
export const DEFAULT_ROLES = 'admin, member, viewer';

const ERRORS = {
  owner_only: tk('oidc.err.ownerOnly'), name_taken: tk('oidc.err.nameTaken'), not_found: tk('oidc.err.notFound'), rate_limited: tk('oidc.err.rateLimited'),
  no_key_store: tk('oidc.err.noKeyStore'), owner: tk('oidc.err.owner'), invalid_role: tk('oidc.err.invalidRole'),
};
/** Fehlertext: feste Faelle uebersetzt, bei Eingabefehlern der Text des Servers (er nennt die Stelle). */
export const errorText = (e) => (e && ERRORS[e.code] ? t(ERRORS[e.code]) : (e && e.message) || '');

/** Was eine Bestaetigung verlangt: { kind: 'check' | 'type' | 'click', word }. Beim Tippen: der Name der App (fuer den Schluessel ein festes Wort). */
export function confirmNeed(mode, word) {
  if (mode === 'click') return { kind: 'click', word: '' };
  if (mode === 'type' && word) return { kind: 'type', word };
  return { kind: 'check', word: '' };
}
export const confirmReady = (need, { checked = false, typed = '' } = {}) =>
  need.kind === 'click' || (need.kind === 'check' && checked) || (need.kind === 'type' && String(typed).trim().toLowerCase() === need.word.toLowerCase());

const ACTIONS = { secret: tk('oidc.confirmSecret'), disable: tk('oidc.confirmDisable'), delete: tk('oidc.confirmDelete'), rotate: tk('oidc.confirmRotate') };

/** Das Markup des Dialogs aus dem Stand des Servers (data) und der Oberflaeche (ui). Alles Fremde geht durch esc. */
export function oidcMarkup(data, ui, esc) {
  if (!data) return `<p class="hint">${esc(ui.error || t('common.loading'))}</p>`;
  const btn = (act, label, extra = '', cls = 'ghost') => `<button type="button" class="${cls}" data-oidc="${act}"${extra}>${esc(label)}</button>`;
  const draft = (id, field, fallback) => (ui.drafts[id] && ui.drafts[id][field] !== undefined ? ui.drafts[id][field] : fallback);
  const field = (id, name, label, value, kind = 'input', help = '') => {
    const common = `data-draft="${esc(id)}" data-field="${name}" spellcheck="false" autocapitalize="none" autocomplete="off" autocorrect="off"`;
    return `<label>${esc(label)}${kind === 'area' ? `<textarea ${common} rows="3">${esc(value)}</textarea>` : `<input type="text" ${common} value="${esc(value)}">`}</label>${help ? `<p class="hint">${esc(help)}</p>` : ''}`;
  };
  const msg = (k) => `<p class="msg${ui.msg[k] && ui.msg[k].ok ? ' ok' : ''}" role="alert">${esc((ui.msg[k] && ui.msg[k].text) || '')}</p>`;
  const form = (id, app) => `${field(id, 'name', t('oidc.name'), draft(id, 'name', app ? app.name : ''))}
    ${field(id, 'redirectUris', t('oidc.redirects'), draft(id, 'redirectUris', app ? app.redirectUris.join('\n') : ''), 'area', t('oidc.redirectsHelp'))}
    ${field(id, 'postLogoutUris', t('oidc.postLogout'), draft(id, 'postLogoutUris', app ? app.postLogoutUris.join('\n') : ''), 'area', t('oidc.postLogoutHelp'))}
    ${field(id, 'backchannelUri', t('oidc.backchannel'), draft(id, 'backchannelUri', app && app.backchannelUri ? app.backchannelUri : ''), 'input', t('oidc.backchannelHelp'))}
    <label class="sec-check"><span><input type="checkbox" data-draft="${esc(id)}" data-field="backchannelPrivate"${draft(id, 'backchannelPrivate', app ? app.backchannelPrivate : false) ? ' checked' : ''}> ${esc(t('oidc.backchannelPrivate'))}</span></label>
    <p class="hint">${esc(t('oidc.backchannelPrivateHelp'))}</p>
    ${field(id, 'roles', t('oidc.roles'), draft(id, 'roles', app ? app.roles.join(', ') : DEFAULT_ROLES), 'input', t('oidc.rolesHelp'))}`;

  const secretBox = (name) => ui.secret && ui.secret.app === name
    ? `<div class="sec-key oidc-secret"><p class="hint">${esc(t('oidc.secretOnce'))}</p><code class="mono" id="oidcSecret">${esc(ui.secret.value)}</code>
       <div class="dlg-btns">${btn('copy-secret', t('oidc.copy'))}${btn('hide-secret', t('oidc.secretSaved'), '', 'primary')}</div></div>` : '';

  /** Die Rueckfrage vor einer gefaehrlichen Aktion; ui.confirm sagt, fuer welche Zeile und Aktion sie offen ist. go: der Knopf, der sie ausfuehrt. */
  const confirmRow = (id, kind, word, go, extra = '') => {
    if (!ui.confirm || ui.confirm.id !== id || ui.confirm.kind !== kind) return '';
    const need = confirmNeed(data.confirmMode, word);
    const control = need.kind === 'check' ? `<label class="sec-check"><span><input type="checkbox" data-oidc-check> ${esc(t('oidc.confirmCheck'))}</span></label>`
      : need.kind === 'type' ? `<label>${esc(t('oidc.confirmType', { word: need.word }))}<input type="text" data-oidc-type autocomplete="off" autocapitalize="none" spellcheck="false"></label>` : '';
    return `<div class="sec-warn oidc-confirm" role="alert"><p>${esc(t(ACTIONS[kind]))}</p>${control}
      <div class="dlg-btns">${btn(go, t('oidc.confirmGo'), extra + (confirmReady(need, ui.confirm) ? '' : ' disabled'), 'primary')}${btn('cancel', t('common.cancel'))}</div></div>`;
  };

  const appCard = (a) => {
    const open = ui.open.has(a.id);
    const people = data.people.map((p) => {
      const cur = (data.assignments.find((x) => x.appId === a.id && x.userId === p.id) || {}).role || '';
      const sel = p.owner ? `<span class="oidc-owner">${esc(t('oidc.ownerRole'))}</span>`
        : `<select data-oidc-role data-app="${esc(a.id)}" data-user="${esc(p.id)}" aria-label="${esc(t('oidc.roleOf', { name: p.name }))}"><option value="">${esc(t('oidc.noAccess'))}</option>${a.roles.map((r) => `<option value="${esc(r)}"${r === cur ? ' selected' : ''}>${esc(r)}</option>`).join('')}</select>`;
      return `<li><span class="oidc-who">${esc(p.name)}</span>${sel}</li>`;
    }).join('');
    return `<section class="sec-sec oidc-app" data-app-id="${esc(a.id)}">
      <h3>${esc(a.name)} <span class="oidc-chip ${a.enabled ? 'on' : 'off'}">${esc(t(a.enabled ? tk('oidc.active') : tk('oidc.blocked')))}</span></h3>
      <p class="hint">${esc(t('oidc.clientId'))}: <code class="mono">${esc(a.clientId)}</code> ${btn('copy-id', t('oidc.copy'), ` data-value="${esc(a.clientId)}"`)}</p>
      <p class="hint">${esc(t('oidc.appFacts', { n: a.sessions, date: fmtDate(a.secretChangedAt) }))}</p>
      ${secretBox(a.name)}
      <details class="oidc-edit" data-id="${esc(a.id)}"${open ? ' open' : ''}><summary>${esc(t('oidc.edit'))}</summary>
        ${form(a.id, a)}
        <div class="dlg-btns">${btn('save-app', t('common.save'), ` data-id="${esc(a.id)}"`, 'primary')}</div>
      </details>
      <h4>${esc(t('oidc.peopleRoles'))}</h4>
      <ul class="oidc-people">${people}</ul>
      <div class="dlg-btns oidc-danger">${btn('ask-secret', t('oidc.newSecret'), ` data-id="${esc(a.id)}"`)}${a.enabled ? btn('ask-disable', t('oidc.block'), ` data-id="${esc(a.id)}"`) : btn('enable', t('oidc.unblock'), ` data-id="${esc(a.id)}"`)}${btn('ask-delete', t('common.delete'), ` data-id="${esc(a.id)}"`)}</div>
      ${confirmRow(a.id, 'secret', a.name, 'do-secret', ` data-id="${esc(a.id)}"`)}${confirmRow(a.id, 'disable', a.name, 'do-disable', ` data-id="${esc(a.id)}"`)}${confirmRow(a.id, 'delete', a.name, 'do-delete', ` data-id="${esc(a.id)}"`)}
      ${msg(a.id)}</section>`;
  };

  const issuer = `<section class="sec-sec"><h3>${esc(t('oidc.address'))}</h3>
    <p class="hint">${esc(t('oidc.addressHelp'))}</p>
    ${data.ready ? '' : `<p class="sec-warn" role="status">${esc(t(data.keyProblem ? tk('oidc.notReadyKeyUnusable') : data.keyStore ? tk('oidc.notReadyAddress') : tk('oidc.notReadyKey')))}</p>`}
    <label>${esc(t('oidc.addressLabel'))}<input type="text" id="oidcIssuer" data-draft="issuer" data-field="issuer" spellcheck="false" autocapitalize="none" autocomplete="off" value="${esc(draft('issuer', 'issuer', data.issuer || ''))}" placeholder="${esc(data.suggestedIssuer)}"></label>
    <p class="hint">${esc(t('oidc.addressSource', { source: t(data.issuerSource === 'app' ? tk('oidc.srcApp') : data.issuerSource === 'env' ? tk('oidc.srcEnv') : tk('oidc.srcNone')) }))}${data.ready ? ' ' + esc(t('oidc.discovery', { url: data.issuer + '/.well-known/openid-configuration' })) : ''}</p>
    <div class="dlg-btns">${btn('save-issuer', t('common.save'), '', 'primary')}${btn('use-suggested', t('oidc.useSuggested', { url: data.suggestedIssuer }))}${data.issuerSource === 'app' ? btn('reset-issuer', t('sec.reset')) : ''}</div>${msg('issuer')}</section>`;

  const newApp = ui.newApp
    ? `<section class="sec-sec"><h3>${esc(t('oidc.newApp'))}</h3>${form('new', null)}
        <div class="dlg-btns">${btn('create-app', t('oidc.create'), '', 'primary')}${btn('cancel-new', t('common.cancel'))}</div>${msg('new')}</section>`
    : `<div class="dlg-btns">${btn('open-new', t('oidc.newApp'), '', 'primary')}</div>${msg('new')}`;

  const keys = `<section class="sec-sec"><h3>${esc(t('oidc.keys'))}</h3><p class="hint">${esc(t('oidc.keysHelp'))}</p>
    <ul class="oidc-keys">${data.keys.map((k) => `<li><code class="mono">${esc(k.kid)}</code> ${esc(t(k.retiredAt ? tk('oidc.keyRetired') : tk('oidc.keyActive')))} · ${esc(fmtDate(k.createdAt))}</li>`).join('') || `<li class="hint">${esc(t('oidc.keysNone'))}</li>`}</ul>
    ${data.keyStore ? btn('ask-rotate', t('oidc.rotate')) : ''}
    ${confirmRow('keys', 'rotate', t('oidc.rotateWord'), 'do-rotate')}${msg('keys')}</section>`;

  return issuer + `<section class="sec-sec"><h3>${esc(t('oidc.apps'))}</h3><p class="hint">${esc(t('oidc.appsHelp'))}</p>${data.apps.length ? '' : `<p class="hint">${esc(t('oidc.appsNone'))}</p>`}</section>`
    + data.apps.map(appCard).join('') + newApp + keys;
}

/** Oeffnet den Dialog. ctx kommt aus der Oberflaeche: api (mit Wiederholung nach der Freigabe durch den zweiten Faktor) und esc. */
export function openOidcApps({ api, esc }) {
  if (!document.getElementById('oidcCss')) { const l = document.createElement('link'); l.id = 'oidcCss'; l.rel = 'stylesheet'; l.href = '/oidc.css'; document.head.append(l); }
  const dialog = document.createElement('dialog');                         // je Oeffnen ein neuer Dialog: nichts bleibt zwischen zwei Besuchen
  dialog.id = 'oidcDlg'; dialog.className = 'sec-dlg';
  dialog.innerHTML = `<h2></h2><div id="oidcBody"></div><div class="row"><button class="ghost close" data-act="close" type="button"></button></div>`;
  document.body.append(dialog);
  dialog.querySelector('h2').textContent = t('oidc.title');
  dialog.querySelector('.close').textContent = t('common.close');
  const body = dialog.querySelector('#oidcBody');
  let data = null;
  let ui = fresh();
  function fresh() { return { drafts: {}, open: new Set(), msg: {}, secret: null, confirm: null, newApp: false, error: '' }; }
  const render = () => { body.innerHTML = oidcMarkup(data, ui, esc); };
  const say = (k, text, ok = false) => { ui.msg[k] = { text, ok }; render(); };
  const drafts = (id) => (ui.drafts[id] ||= {});

  async function load() {
    try { data = await api('/api/security/oidc'); ui.error = ''; } catch (e) { ui.error = errorText(e); data = null; }
    render();
  }

  const read = (id) => {
    const d = drafts(id), app = data.apps.find((a) => a.id === id);
    const pick = (f, fallback) => (d[f] !== undefined ? d[f] : fallback);
    return {
      name: pick('name', app ? app.name : '').trim(),
      redirectUris: parseUris(pick('redirectUris', app ? app.redirectUris.join('\n') : '')),
      postLogoutUris: parseUris(pick('postLogoutUris', app ? app.postLogoutUris.join('\n') : '')),
      backchannelUri: pick('backchannelUri', app && app.backchannelUri ? app.backchannelUri : '').trim() || undefined,
      roles: parseRoles(pick('roles', app ? app.roles.join(', ') : DEFAULT_ROLES)),
      backchannelPrivate: !!pick('backchannelPrivate', app ? app.backchannelPrivate : false),
    };
  };

  body.addEventListener('input', (e) => {
    const el = e.target;
    if (el.dataset.draft) { drafts(el.dataset.draft)[el.dataset.field] = el.type === 'checkbox' ? el.checked : el.value; return; }
    if (el.hasAttribute('data-oidc-check')) { ui.confirm.checked = el.checked; syncConfirm(); }
    else if (el.hasAttribute('data-oidc-type')) { ui.confirm.typed = el.value; syncConfirm(); }
  });
  // nur den Knopf "Bestaetigen" nachfuehren, ohne alles neu zu zeichnen (sonst verliert das Feld den Fokus)
  function syncConfirm() {
    const box = body.querySelector('.oidc-confirm'); if (!box || !ui.confirm) return;
    const word = ui.confirm.id === 'keys' ? t('oidc.rotateWord') : (data.apps.find((a) => a.id === ui.confirm.id) || {}).name;
    box.querySelector('button.primary').disabled = !confirmReady(confirmNeed(data.confirmMode, word), ui.confirm);
  }
  body.addEventListener('change', async (e) => {
    const el = e.target;
    if (el.hasAttribute('data-oidc-role')) {
      const url = `/api/security/oidc/apps/${el.dataset.app}/roles/${el.dataset.user}`;
      try { await (el.value ? api(url, { method: 'PUT', body: { role: el.value } }) : api(url, { method: 'DELETE' })); await load(); say(el.dataset.app, t('oidc.saved'), true); }
      catch (err) { await load(); say(el.dataset.app, errorText(err)); }
    }
  });
  body.addEventListener('toggle', (e) => { const d = e.target; if (d.classList && d.classList.contains('oidc-edit')) { d.open ? ui.open.add(d.dataset.id) : ui.open.delete(d.dataset.id); } }, true);

  const ask = (id, kind) => { ui.confirm = { id, kind, checked: false, typed: '' }; render(); };
  const showSecret = (appName, value) => { ui.secret = { app: appName, value }; ui.confirm = null; };
  const wrap = (key, run) => async () => { try { await run(); } catch (err) { say(key, errorText(err)); } };

  const ACT = {
    'open-new': () => { ui.newApp = true; render(); },
    'cancel-new': () => { ui.newApp = false; delete ui.drafts.new; render(); },
    'cancel': () => { ui.confirm = null; render(); },
    'create-app': wrap('new', async () => {
      const b = read('new');
      const r = await api('/api/security/oidc/apps', { method: 'POST', body: b });
      ui.newApp = false; delete ui.drafts.new; showSecret(r.app.name, r.clientSecret); await load();
    }),
    'save-issuer': wrap('issuer', async () => {
      await api('/api/security/oidc/issuer', { method: 'PUT', body: { issuer: (ui.drafts.issuer || {}).issuer ?? data.issuer ?? '' } });
      delete ui.drafts.issuer; await load(); say('issuer', t('oidc.saved'), true);
    }),
    'use-suggested': () => { drafts('issuer').issuer = data.suggestedIssuer; render(); },
    'reset-issuer': wrap('issuer', async () => { await api('/api/security/oidc/issuer', { method: 'DELETE' }); delete ui.drafts.issuer; await load(); say('issuer', t('sec.resetDone'), true); }),
    'ask-rotate': () => ask('keys', 'rotate'),
    'do-rotate': wrap('keys', async () => { await api('/api/security/oidc/keys/rotate', { method: 'POST', body: {} }); ui.confirm = null; await load(); say('keys', t('oidc.rotated'), true); }),
    'hide-secret': () => { ui.secret = null; render(); },
    'copy-secret': async () => { try { await navigator.clipboard.writeText(ui.secret.value); say('new', t('sec.copied'), true); } catch { say('new', t('account.copyFailed')); } },
    'copy-id': async (b) => { try { await navigator.clipboard.writeText(b.dataset.value); say('new', t('sec.copied'), true); } catch { say('new', t('account.copyFailed')); } },
    'save-app': (b) => wrap(b.dataset.id, async () => {
      await api(`/api/security/oidc/apps/${b.dataset.id}`, { method: 'PUT', body: read(b.dataset.id) });
      delete ui.drafts[b.dataset.id]; await load(); say(b.dataset.id, t('oidc.saved'), true);
    })(),
    'ask-secret': (b) => ask(b.dataset.id, 'secret'),
    'ask-disable': (b) => ask(b.dataset.id, 'disable'),
    'ask-delete': (b) => ask(b.dataset.id, 'delete'),
    'do-secret': (b) => wrap(b.dataset.id, async () => {
      const app = data.apps.find((a) => a.id === b.dataset.id);
      const r = await api(`/api/security/oidc/apps/${b.dataset.id}/secret`, { method: 'POST', body: {} });
      showSecret(app.name, r.clientSecret); await load();
    })(),
    'do-disable': (b) => wrap(b.dataset.id, async () => { await api(`/api/security/oidc/apps/${b.dataset.id}/enabled`, { method: 'PUT', body: { enabled: false } }); ui.confirm = null; await load(); say(b.dataset.id, t('oidc.blockedDone'), true); })(),
    'enable': (b) => wrap(b.dataset.id, async () => { await api(`/api/security/oidc/apps/${b.dataset.id}/enabled`, { method: 'PUT', body: { enabled: true } }); await load(); say(b.dataset.id, t('oidc.saved'), true); })(),
    'do-delete': (b) => wrap(b.dataset.id, async () => { await api(`/api/security/oidc/apps/${b.dataset.id}`, { method: 'DELETE' }); ui.confirm = null; await load(); })(),
  };
  body.addEventListener('click', (e) => { const b = e.target.closest('[data-oidc]'); if (b && ACT[b.dataset.oidc]) ACT[b.dataset.oidc](b); });
  dialog.addEventListener('click', (e) => { if (e.target.closest('[data-act=close]')) dialog.close(); });
  // nichts Geheimes bleibt nach dem Schliessen
  dialog.addEventListener('close', () => { ui = fresh(); data = null; dialog.remove(); }, { once: true });

  render();
  showDialog(dialog, { system: true, width: 640, resizable: true, disposable: true });
  load();
}
