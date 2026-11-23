/* =====================================================================
 * Einstellungen, KI (docs/AI-GATEWAY.md): der Schalter fuer den KI-Zugang (aus ist der Anfang: nichts laeuft, nichts wird geladen), die Anbieter einrichten (Codex-Bruecke mit
 * dem ChatGPT-Konto per Geraete-Code, OpenAI- und Anthropic-Schluessel), die Apps der Familie mit ihrem Schluessel, Grenzen und Verbrauch. Der Dialog #aiDlg wird beim ersten Oeffnen
 * angelegt (und ai.css geladen), gilt fuer Handy (Sheet) und Desktop (Fenster) gleich. Der obere Teil ist reines JavaScript (tests/frontend/ai.test.mjs). Alles Geschriebene prueft und
 * speichert der Server und verlangt dafuer die Freigabe mit dem zweiten Faktor (api kommt aus shell.js mit Wiederholung); hier steht nur Anzeige und Eingabe.
 * Ein Schluessel einer App wird nur im Moment des Anlegens (oder des Erneuerns) gezeigt; nirgends sonst gibt es ihn, auch dem Server nicht.
 * ===================================================================== */
import { t, tk, fmtDate } from './i18n.js';
import { showDialog, askWindow } from './desktop-window.js';

// ---------------------------------------------------------------- reine Hilfen

/** "200k", "1,5m", "1500" -> Zahl; leer -> null (keine Grenze); Unsinn -> undefined. */
export function parseTokens(text) {
  const s = String(text ?? '').trim().toLowerCase().replace(/\s+/g, '').replace(',', '.');
  if (s === '') return null;
  const m = /^(\d+(?:\.\d+)?)([km]?)$/.exec(s);
  if (!m) return undefined;
  const n = Math.round(parseFloat(m[1]) * (m[2] === 'k' ? 1e3 : m[2] === 'm' ? 1e6 : 1));
  return n > 0 ? n : undefined;
}

/** 1234 -> "1,2k", 2000000 -> "2M" (kurz, fuer Budgets und Verbrauch). */
export function fmtTokens(n, locale = 'de') {
  const f = (x) => Number(x.toFixed(x >= 100 || Number.isInteger(x) ? 0 : 1)).toLocaleString(locale);
  n = Number(n) || 0;
  return n >= 1e6 ? f(n / 1e6) + 'M' : n >= 1e3 ? f(n / 1e3) + 'k' : String(n);
}

/** Verbrauch von Grenze in Prozent (0 bis 100), null ohne Grenze. */
export const percent = (used, limit) => (limit ? Math.min(100, Math.floor((used / limit) * 100)) : null);

/** ok | warn | full: wie die Anzeige eines Budgets gefaerbt wird (ab 80 % Hinweis, ab 100 % voll). */
export const budgetTone = (used, limit) => { const p = percent(used, limit); return p === null ? 'ok' : p >= 100 ? 'full' : p >= 80 ? 'warn' : 'ok'; };

/** Die Wege einer App als Liste: ganze Anbieter oder einzelne Modelle ("openai", "openai/gpt-4.1"). */
export const splitAllowed = (allowed) => ({ providers: (allowed || []).filter((a) => !a.includes('/')), models: (allowed || []).filter((a) => a.includes('/')) });

/** Aus den Eingaben des Formulars die erlaubten Eintraege: genannte Modelle gehen vor, sonst die angehakten Anbieter. */
export function buildAllowed(providers, modelText) {
  const models = String(modelText || '').split(/[\s,;]+/).filter(Boolean);
  return models.length ? [...new Set(models)] : [...new Set(providers)];
}

/** Der Zustand eines Anbieters als Schluessel des Textes und Ton fuer die Anzeige. */
export function providerChip(p) {
  if (!p.enabled) return { key: STATUS.off, tone: 'info' };
  const tone = p.status === 'ready' ? 'ok' : p.status === 'error' || p.status === 'install_failed' ? 'error' : 'warn';
  return { key: STATUS[p.status] || STATUS.error, tone };
}

const STATUS = {
  off: tk('ai.status.off'), no_key: tk('ai.status.no_key'), not_installed: tk('ai.status.not_installed'), downloading: tk('ai.status.downloading'), install_failed: tk('ai.status.install_failed'),
  not_signed_in: tk('ai.status.not_signed_in'), login_waiting: tk('ai.status.login_waiting'), ready: tk('ai.status.ready'), error: tk('ai.status.error'),
};
export const PROVIDERS = { codex: tk('ai.p.codex'), openai: tk('ai.p.openai'), anthropic: tk('ai.p.anthropic'), fake: tk('ai.p.fake') };
const LOGIN_END = { failed: tk('ai.login.failed'), expired: tk('ai.login.expired') };

/** Summe je Tag und App aus den Zeilen des Servers (die Tabelle des Verbrauchs). */
export function usageByDay(rows) {
  const map = new Map();
  for (const r of rows || []) {
    const k = r.day + '|' + r.appId;
    const e = map.get(k) || { day: r.day, app: r.app, requests: 0, failed: 0, tokensIn: 0, tokensOut: 0 };
    e.requests += r.requests; e.failed += r.failed + r.aborted; e.tokensIn += r.tokensIn; e.tokensOut += r.tokensOut;
    map.set(k, e);
  }
  return [...map.values()];
}

// ---------------------------------------------------------------- Markup

const KNOWN = ['codex', 'openai', 'anthropic'];

function budgetBar(label, part, esc) {
  const p = percent(part.used, part.limit), tone = budgetTone(part.used, part.limit);
  return `<div class="ai-budget ai-${tone}"><span>${esc(label)}</span><div class="ai-bar" role="img" aria-label="${esc(label)}"><i style="width:${p === null ? 0 : p}%"></i></div><b>${esc(fmtTokens(part.used))}${part.limit ? ' / ' + esc(fmtTokens(part.limit)) : ' · ' + esc(t('ai.noLimit'))}</b></div>`;
}

function codexBlock(p, esc) {
  const c = p.codex, btn = (a, label, cls = '') => `<button type="button" class="${cls || 'ghost'}" data-ai="${a}">${esc(label)}</button>`;
  if (!c.supported) return `<p class="hint">${esc(t('ai.codex.unsupported'))}</p>`;
  if (p.status === 'downloading') return `<p class="hint" role="status">${esc(t('ai.codex.downloading', { version: c.version }))}</p>`;
  if (p.status === 'not_installed' || p.status === 'install_failed') return `${p.status === 'install_failed' ? `<p class="sec-warn">${esc(t('ai.codex.failed'))}${p.detail ? ' ' + esc(p.detail) : ''}</p>` : ''}<div class="dlg-btns">${btn('codex-retry', t('ai.codex.download'), 'primary')}</div>`;
  const login = c.login || {};
  if (p.status === 'login_waiting' || login.state === 'waiting' || login.state === 'starting') {
    if (login.state === 'waiting' && login.url && login.code) {
      return `<div class="ai-login" role="status"><p>${esc(t('ai.login.step1'))}</p><p><a href="${esc(login.url)}" target="_blank" rel="noopener noreferrer">${esc(login.url)}</a></p><p>${esc(t('ai.login.step2'))}</p>
        <code class="ai-code">${esc(login.code)}</code><p class="hint">${esc(t('ai.login.expires', { at: login.expiresAt ? fmtDate(login.expiresAt, { timeStyle: 'short' }) : '' }))}</p>
        <div class="dlg-btns">${btn('login-cancel', t('common.cancel'))}</div></div>`;
    }
    return `<p class="hint" role="status">${esc(t('ai.login.starting'))}</p><div class="dlg-btns">${btn('login-cancel', t('common.cancel'))}</div>`;
  }
  const plan = p.plan && p.plan.windows.length ? `<p class="hint">${esc(t('ai.codex.plan', { plan: p.plan.plan || '', usage: p.plan.windows.map((w) => `${Math.round(w.usedPercent)} %${w.minutes ? ' / ' + (w.minutes >= 1440 ? Math.round(w.minutes / 1440) + ' d' : Math.round(w.minutes / 60) + ' h') : ''}`).join(', ') }))}</p>` : '';
  const after = LOGIN_END[login.state] ? `<p class="sec-warn">${esc(t(LOGIN_END[login.state]))}</p>` : '';
  if (p.status === 'ready') return `${plan}<div class="dlg-btns">${btn('logout', t('ai.codex.logout'))}</div>`;
  return `${after}<p class="hint">${esc(t('ai.codex.signInHelp'))}</p><div class="dlg-btns">${btn('login-start', t('ai.codex.signIn'), 'primary')}</div>`;
}

function keyBlock(p, canStore, ui, esc) {
  if (!canStore) return `<p class="sec-warn">${esc(t('ai.key.noBox'))}</p>`;
  const id = 'aiKey-' + p.id;
  return `<label>${esc(t('ai.key.label'))}<input id="${id}" type="password" autocomplete="off" spellcheck="false" placeholder="${esc(p.keySet ? t('ai.key.set') : t('ai.key.placeholder'))}" value="${esc(ui.keys[p.id] || '')}"></label>
    <div class="dlg-btns"><button type="button" class="primary" data-ai="key-save" data-id="${p.id}">${esc(t('common.save'))}</button>${p.keySet ? `<button type="button" class="ghost" data-ai="key-test" data-id="${p.id}">${esc(t('ai.key.test'))}</button><button type="button" class="ghost" data-ai="key-clear" data-id="${p.id}">${esc(t('ai.key.clear'))}</button>` : ''}</div>`;
}

function providerCard(p, state, ui, esc) {
  const chip = providerChip(p), fake = p.id === 'fake';
  const detail = p.status === 'ready' || p.status === 'off' ? '' : '';
  let inner = '';
  if (fake) inner = `<p class="hint">${esc(t('ai.p.fakeHelp'))}</p>`;
  else if (p.enabled && p.id === 'codex' && p.codex) inner = codexBlock(p, esc);
  else if (p.enabled && p.id !== 'codex') inner = keyBlock(p, state.canStoreKeys, ui, esc);
  else if (p.id === 'codex') inner = `<p class="hint">${esc(t('ai.codex.help'))}</p>`;
  else inner = `<p class="hint">${esc(t('ai.key.help'))}</p>`;
  const toggle = fake ? '' : `<button type="button" class="ghost ai-toggle" data-ai="provider-toggle" data-id="${p.id}" data-on="${p.enabled ? '0' : '1'}">${esc(p.enabled ? t('ai.turnOff') : t('ai.turnOn'))}</button>`;
  return `<div class="ai-card" data-provider="${p.id}"><div class="ai-card-head"><b>${esc(t(PROVIDERS[p.id] || 'ai.p.fake'))}</b><span class="ai-chip ai-${chip.tone}">${esc(t(chip.key))}</span>${toggle}</div>${detail}${inner}${msgLine('p-' + p.id, ui, esc)}</div>`;
}

function msgLine(k, ui, esc) { const m = ui.msg[k]; return m ? `<p class="msg${m.ok ? ' ok' : ''}" role="${m.ok ? 'status' : 'alert'}">${esc(m.text)}</p>` : ''; }

function appForm(a, state, ui, esc) {
  const f = ui.form, { providers, models } = splitAllowed(f.allowed);
  const choices = state.providers.map((p) => p.id).filter((id) => KNOWN.includes(id) || id === 'fake');
  return `<div class="ai-form" data-form="${a ? a.id : 'new'}"><label>${esc(t('ai.app.name'))}<input id="aiName" type="text" maxlength="60" autocomplete="off" value="${esc(f.name)}"></label>
    <fieldset class="ai-allowed"><legend>${esc(t('ai.app.providers'))}</legend>${choices.map((id) => `<label class="sec-check"><span><input type="checkbox" data-prov="${id}"${providers.includes(id) ? ' checked' : ''}> ${esc(t(PROVIDERS[id] || 'ai.p.fake'))}</span></label>`).join('')}</fieldset>
    <label>${esc(t('ai.app.models'))}<input id="aiModels" type="text" autocomplete="off" spellcheck="false" placeholder="openai/gpt-4.1, codex/gpt-5" value="${esc(models.join(', '))}"></label><p class="hint">${esc(t('ai.app.modelsHelp'))}</p>
    <div class="ai-two"><label>${esc(t('ai.app.daily'))}<input id="aiDaily" type="text" inputmode="text" autocomplete="off" placeholder="200k" value="${esc(f.daily)}"></label>
    <label>${esc(t('ai.app.monthly'))}<input id="aiMonthly" type="text" inputmode="text" autocomplete="off" placeholder="2M" value="${esc(f.monthly)}"></label></div><p class="hint">${esc(t('ai.app.budgetHelp'))}</p>
    <label>${esc(t('ai.app.rate'))}<input id="aiRate" type="number" min="1" max="600" step="1" value="${esc(f.rate)}"></label>
    <label class="sec-check"><span><input id="aiFiles" type="checkbox"${f.files ? ' checked' : ''}> ${esc(t('ai.app.files'))}</span></label>
    <div class="dlg-btns"><button type="button" class="primary" data-ai="app-save">${esc(a ? t('common.save') : t('ai.app.create'))}</button><button type="button" class="ghost" data-ai="form-cancel">${esc(t('common.cancel'))}</button></div>${msgLine('form', ui, esc)}</div>`;
}

function appCard(a, state, ui, esc) {
  if (ui.editing === a.id) return `<div class="ai-app">${appForm(a, state, ui, esc)}</div>`;
  const allowed = a.allowed.join(', ');
  const key = ui.shown && ui.shown.id === a.id ? `<div class="sec-key"><p class="hint">${esc(t('ai.key.shownOnce'))}</p><code class="mono ai-appkey">${esc(ui.shown.key)}</code><p class="hint">${esc(t('ai.key.address'))} <code>${esc(location.origin)}/api/ai/v1</code></p>
      <div class="dlg-btns"><button type="button" class="ghost" data-ai="copy-key">${esc(t('sec.copy'))}</button><button type="button" class="primary" data-ai="key-done">${esc(t('ai.key.done'))}</button></div></div>` : '';
  return `<div class="ai-app${a.active ? '' : ' off'}" data-app="${a.id}"><div class="ai-app-head"><b>${esc(a.name)}</b>${a.active ? '' : `<span class="ai-chip ai-warn">${esc(t('ai.app.blocked'))}</span>`}<span class="hint mono">…${esc(a.keyHint)}</span></div>
    <p class="hint">${esc(t('ai.app.allows', { allowed }))} · ${esc(t('ai.app.rateLine', { n: a.ratePerMinute }))} · ${esc(a.allowFiles ? t('ai.app.filesOn') : t('ai.app.filesOff'))}${a.lastUsedAt ? ' · ' + esc(t('ai.app.lastUsed', { at: fmtDate(a.lastUsedAt) })) : ''}</p>
    ${budgetBar(t('ai.today'), a.budget.day, esc)}${budgetBar(t('ai.month'), a.budget.month, esc)}${key}
    <div class="dlg-btns ai-actions"><button type="button" class="ghost" data-ai="app-edit" data-id="${a.id}">${esc(t('ai.app.edit'))}</button><button type="button" class="ghost" data-ai="app-active" data-id="${a.id}" data-on="${a.active ? '0' : '1'}">${esc(a.active ? t('ai.app.block') : t('ai.app.unblock'))}</button><button type="button" class="ghost" data-ai="app-rotate" data-id="${a.id}">${esc(t('ai.app.rotate'))}</button><button type="button" class="ghost ai-danger" data-ai="app-delete" data-id="${a.id}">${esc(t('ai.app.delete'))}</button></div>${msgLine('a-' + a.id, ui, esc)}</div>`;
}

/** Der ganze Dialog als Markup aus dem Stand des Servers (state) und der Oberflaeche (ui). */
export function aiMarkup(state, ui, esc) {
  if (!state) return ui.error ? `<p class="msg" role="alert">${esc(ui.error)}</p>` : `<p class="hint">${esc(t('common.loading'))}</p>`;
  const sw = `<section class="sec-sec"><h3>${esc(t('ai.title'))}</h3><p class="hint">${esc(t('ai.help'))}</p>
    <label class="sec-check"><span><input id="aiOn" type="checkbox"${state.enabled ? ' checked' : ''}> ${esc(t('ai.switch'))}</span></label>
    <p class="${state.enabled ? 'hint' : 'sec-warn'}" role="status">${esc(state.enabled ? t('ai.on') : t('ai.off'))}</p>${msgLine('switch', ui, esc)}</section>`;
  const models = ui.models || [];
  const select = `<label>${esc(t('ai.default'))}<select id="aiDefault"><option value="">${esc(t('ai.defaultNone'))}</option>${state.defaultModel && !models.some((m) => m.id === state.defaultModel) ? `<option value="${esc(state.defaultModel)}" selected>${esc(state.defaultModel)}</option>` : ''}${models.map((m) => `<option value="${esc(m.id)}"${m.id === state.defaultModel ? ' selected' : ''}>${esc(m.id)}${m.name && m.name !== m.id.split('/')[1] ? ' · ' + esc(m.name) : ''}</option>`).join('')}</select></label><p class="hint">${esc(t('ai.defaultHelp'))}</p>${msgLine('default', ui, esc)}`;
  const providers = `<section class="sec-sec"><h3>${esc(t('ai.providers'))}</h3><p class="hint">${esc(t('ai.providersHelp'))}</p>${state.providers.map((p) => providerCard(p, state, ui, esc)).join('')}${state.enabled ? select : ''}</section>`;
  const apps = `<section class="sec-sec"><h3>${esc(t('ai.apps'))}</h3><p class="hint">${esc(t('ai.appsHelp'))}</p>
    ${state.apps.length ? state.apps.map((a) => appCard(a, state, ui, esc)).join('') : `<p class="hint">${esc(t('ai.apps.none'))}</p>`}
    ${ui.editing === 'new' ? `<div class="ai-app">${appForm(null, state, ui, esc)}</div>` : `<div class="dlg-btns"><button type="button" class="primary" data-ai="app-new">${esc(t('ai.app.add'))}</button></div>`}
    ${ui.shown && !state.apps.some((a) => a.id === ui.shown.id) ? `<div class="sec-key"><p class="hint">${esc(t('ai.key.shownOnce'))}</p><code class="mono ai-appkey">${esc(ui.shown.key)}</code><div class="dlg-btns"><button type="button" class="ghost" data-ai="copy-key">${esc(t('sec.copy'))}</button><button type="button" class="primary" data-ai="key-done">${esc(t('ai.key.done'))}</button></div></div>` : ''}
    ${msgLine('apps', ui, esc)}</section>`;
  const rows = usageByDay(ui.usage);
  const usage = `<section class="sec-sec"><h3>${esc(t('ai.usage'))}</h3>${rows.length ? `<table class="ai-usage"><thead><tr><th>${esc(t('ai.usage.day'))}</th><th>${esc(t('ai.usage.app'))}</th><th>${esc(t('ai.usage.requests'))}</th><th>${esc(t('ai.usage.tokens'))}</th></tr></thead><tbody>${rows.slice(0, 40).map((r) => `<tr><td>${esc(r.day)}</td><td>${esc(r.app)}</td><td>${r.requests}${r.failed ? ` <span class="hint">(${esc(t('ai.usage.failed', { n: r.failed }))})</span>` : ''}</td><td>${esc(fmtTokens(r.tokensIn))} → ${esc(fmtTokens(r.tokensOut))}</td></tr>`).join('')}</tbody></table>` : `<p class="hint">${esc(t('ai.usage.none'))}</p>`}<p class="hint">${esc(t('ai.usage.privacy'))}</p></section>`;
  return sw + providers + apps + usage;
}

// ---------------------------------------------------------------- Dialog

let opened = null;

/** Oeffnet den Dialog (legt ihn beim ersten Mal an). api: die Funktion mit Freigabe-Wiederholung, esc: Maskierung (beide von shell.js ueber desktop.js und mobile.js). */
export function openAi({ api, esc }) {
  if (!opened) opened = createDialog(api, esc);
  opened.open();
}

function createDialog(api, esc) {
  if (!document.querySelector('link[data-ai-css]')) {
    const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = '/ai.css'; l.dataset.aiCss = '1'; document.head.append(l);
  }
  const dlg = document.createElement('dialog');
  dlg.id = 'aiDlg'; dlg.className = 'sec-dlg ai-dlg' + (document.querySelector('link[href="mobile.css"]') ? ' m3sheet' : '');       // am Handy als Sheet wie die anderen Dialoge
  dlg.innerHTML = `<h2>${esc(t('ai.title'))}</h2><div id="aiBody"></div><div class="row"><button class="ghost close" data-act="close" type="button">${esc(t('common.close'))}</button></div>`;
  document.body.append(dlg);
  const body = dlg.querySelector('#aiBody');
  let state = null, timer = 0;
  const fresh = () => ({ keys: {}, msg: {}, models: [], usage: [], editing: null, form: null, shown: null, error: '' });
  let ui = fresh();

  const errText = (e) => (e && e.message) || '';       // der Text des Servers kommt schon in der Sprache der Oberflaeche (serverMsg)
  const say = (k, text, ok = false) => { ui.msg[k] = text ? { text, ok } : undefined; render(); };
  const render = () => {
    // Eingaben der offenen Felder merken, damit ein Neuzeichnen (Abfrage alle paar Sekunden) sie nicht loescht
    body.querySelectorAll('input[id^="aiKey-"]').forEach((i) => { ui.keys[i.id.slice(6)] = i.value; });
    const keep = body.contains(document.activeElement) ? document.activeElement.id : '';
    body.innerHTML = aiMarkup(state, ui, esc);
    if (keep) { const el = body.querySelector('#' + CSS.escape(keep)); if (el) el.focus({ preventScroll: true }); }
  };

  async function load() {
    try {
      state = await api('/api/ai');
      ui.error = '';
      ui.usage = (await api('/api/ai/usage?days=7').catch(() => ({ rows: [] }))).rows || [];
      ui.models = state.enabled ? (await api('/api/ai/models').catch(() => ({ models: [] }))).models || [] : [];
    } catch (e) { ui.error = errText(e); }
    render();
    poll();
  }

  // Solange etwas wartet (Download, Anmeldung am Telefon), alle 2 Sekunden nachsehen; sonst nicht
  const busy = () => !!state && state.providers.some((p) => p.enabled && (p.status === 'downloading' || p.status === 'login_waiting' || (p.codex && ['starting', 'waiting'].includes(p.codex.login.state))));
  function poll() {
    clearTimeout(timer);
    if (!dlg.open || !busy()) return;
    timer = setTimeout(async () => {
      if (!dlg.open) return;
      try { state = await api('/api/ai'); ui.error = ''; render(); } catch { /* beim naechsten Mal */ }
      poll();
    }, 2000);
  }
  dlg.addEventListener('close', () => { clearTimeout(timer); ui = fresh(); });

  const run = async (k, fn, okText) => {
    try { await fn(); ui.msg = {}; await load(); if (okText) say(k, okText, true); }
    catch (e) { say(k, errText(e)); }
  };
  const call = (path, method, bodyObj) => api(path, { method, body: bodyObj });

  const formFrom = (a) => a
    ? { name: a.name, allowed: [...a.allowed], daily: a.dailyTokens ? fmtTokens(a.dailyTokens, 'en') : '', monthly: a.monthlyTokens ? fmtTokens(a.monthlyTokens, 'en') : '', rate: String(a.ratePerMinute), files: a.allowFiles }
    : { name: '', allowed: state.providers.filter((p) => p.enabled).map((p) => p.id), daily: '200k', monthly: '2M', rate: '30', files: false };

  const readForm = () => {
    const daily = parseTokens(body.querySelector('#aiDaily').value), monthly = parseTokens(body.querySelector('#aiMonthly').value);
    if (daily === undefined || monthly === undefined) return { error: t('ai.err.badTokens') };
    const providers = [...body.querySelectorAll('[data-prov]')].filter((c) => c.checked).map((c) => c.dataset.prov);
    return { name: body.querySelector('#aiName').value.trim(), allowed: buildAllowed(providers, body.querySelector('#aiModels').value), dailyTokens: daily, monthlyTokens: monthly, ratePerMinute: parseInt(body.querySelector('#aiRate').value, 10) || 30, allowFiles: body.querySelector('#aiFiles').checked };
  };

  const ACTIONS = {
    'provider-toggle': (b) => run('p-' + b.dataset.id, () => call('/api/ai/providers/' + b.dataset.id, 'PUT', { enabled: b.dataset.on === '1' })),
    'codex-retry': () => run('p-codex', () => call('/api/ai/providers/codex', 'PUT', { enabled: true })),
    'login-start': () => run('p-codex', () => call('/api/ai/providers/codex/login', 'POST')),
    'login-cancel': () => run('p-codex', () => call('/api/ai/providers/codex/login', 'DELETE')),
    'logout': () => run('p-codex', () => call('/api/ai/providers/codex/logout', 'POST')),
    'key-save': (b) => {
      const input = body.querySelector('#aiKey-' + b.dataset.id), key = input ? input.value.trim() : '';
      if (!key) return say('p-' + b.dataset.id, t('ai.err.keyEmpty'));
      delete ui.keys[b.dataset.id];
      return run('p-' + b.dataset.id, () => call('/api/ai/providers/' + b.dataset.id, 'PUT', { key }), t('ai.key.saved'));
    },
    'key-clear': (b) => run('p-' + b.dataset.id, () => call('/api/ai/providers/' + b.dataset.id + '/key', 'DELETE')),
    'key-test': async (b) => {
      try { const r = await api('/api/ai/providers/' + b.dataset.id + '/test'); say('p-' + b.dataset.id, r.ok ? t('ai.key.testOk', { n: r.models }) : t('ai.key.testFail', { code: r.code || '' }), r.ok); }
      catch (e) { say('p-' + b.dataset.id, errText(e)); }
    },
    'app-new': () => { ui.editing = 'new'; ui.form = formFrom(null); ui.msg = {}; render(); },
    'app-edit': (b) => { ui.editing = b.dataset.id; ui.form = formFrom(state.apps.find((a) => a.id === b.dataset.id)); ui.msg = {}; render(); },
    'form-cancel': () => { ui.editing = null; ui.form = null; render(); },
    'app-save': async () => {
      const f = readForm();
      if (f.error) return say('form', f.error);
      ui.form = { name: f.name, allowed: f.allowed, daily: body.querySelector('#aiDaily').value, monthly: body.querySelector('#aiMonthly').value, rate: String(f.ratePerMinute), files: f.allowFiles };
      try {
        if (ui.editing === 'new') {
          const r = await call('/api/ai/apps', 'POST', f);
          ui.shown = { id: r.app.id, key: r.key };
        } else await call('/api/ai/apps/' + ui.editing, 'PUT', f);
        ui.editing = null; ui.form = null; ui.msg = {}; await load();
      } catch (e) { say('form', errText(e)); }
    },
    'app-active': (b) => run('a-' + b.dataset.id, () => call('/api/ai/apps/' + b.dataset.id + '/active', 'PUT', { active: b.dataset.on === '1' })),
    'app-rotate': async (b) => {
      if (!await askWindow({ title: t('ai.app.rotate'), text: t('ai.app.rotateAsk'), yes: t('ai.app.rotate'), no: t('common.cancel'), danger: true })) return;
      try { const r = await call('/api/ai/apps/' + b.dataset.id + '/key', 'POST'); ui.shown = { id: b.dataset.id, key: r.key }; await load(); } catch (e) { say('a-' + b.dataset.id, errText(e)); }
    },
    'app-delete': async (b) => { if (await askWindow({ title: t('ai.app.delete'), text: t('ai.app.deleteAsk'), yes: t('ai.app.delete'), no: t('common.cancel'), danger: true })) return run('apps', () => call('/api/ai/apps/' + b.dataset.id, 'DELETE')); },
    'copy-key': async (b) => { try { await navigator.clipboard.writeText(ui.shown.key); b.textContent = t('sec.copied'); } catch { const c = body.querySelector('.ai-appkey'); if (c) { const r = document.createRange(); r.selectNodeContents(c); const s = getSelection(); s.removeAllRanges(); s.addRange(r); } } },
    'key-done': () => { ui.shown = null; render(); },
  };

  body.addEventListener('click', (e) => { const b = e.target.closest('[data-ai]'); if (b && ACTIONS[b.dataset.ai]) ACTIONS[b.dataset.ai](b); });
  body.addEventListener('change', (e) => {
    if (e.target.id === 'aiOn') run('switch', () => call('/api/ai/enabled', 'PUT', { enabled: e.target.checked })).then(() => { if (state && state.enabled !== e.target.checked) render(); });
    else if (e.target.id === 'aiDefault') run('default', () => call('/api/ai/default-model', 'PUT', { model: e.target.value || null }));
  });
  dlg.addEventListener('click', (e) => { if (e.target.closest('[data-act=close]')) dlg.close(); });

  return {
    open() {
      ui = fresh(); state = null; render();
      showDialog(dlg, { width: 640, resizable: true });
      load();
    },
  };
}
