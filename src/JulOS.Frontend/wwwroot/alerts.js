/* Meldungen (Push ohne KI, docs/PUSH.md): ein Dialog #alertsDlg (index.html), gemeinsam fuer Handy und Desktop. Hier steht das Einschalten auf diesem Geraet
 * (Berechtigung, Abo beim Push-Dienst des Browsers, Anmeldung am Server), die Liste der Geraete, die Regeln mit Schwellen und Ruhezeit, die Testmeldung und die
 * letzten Meldungen. Die Regeln selbst wertet der Server aus; der Service Worker (sw-push.js) zeigt die Nachricht. */
import { t, tk, fmtDate } from './i18n.js';
import { serverMsg } from './i18n-errors.js';

const $ = (s) => document.querySelector(s);
const STORE_KEY = 'pd.push.id';        // die Kennung dieses Geraets beim Server (der Server nennt dem Browser keine Endpunkte)

export const RULES = ['security', 'disk', 'service', 'offline', 'reboot'];
const RULE_TEXT = { security: tk('alerts.r.security'), disk: tk('alerts.r.disk'), service: tk('alerts.r.service'), offline: tk('alerts.r.offline'), reboot: tk('alerts.r.reboot'), test: tk('alerts.r.test') };
const OK_TEXT = { offline: tk('alerts.ok.offline'), disk: tk('alerts.ok.disk'), service: tk('alerts.ok.service') };
const STATE_TEXT = { pending: tk('alerts.state.pending'), sent: tk('alerts.state.sent'), none: tk('alerts.state.none'), dropped: tk('alerts.state.dropped') };

/** Der Text einer Meldung in der Liste ("Platte fast voll", "Agent wieder online"). */
export const eventText = (row) => t((row.kind === 'ok' && OK_TEXT[row.rule]) || RULE_TEXT[row.rule] || RULE_TEXT.test);

/** /#host=<Kennung> (aus einer Push-Nachricht): die Kennung oder null. */
export const parseHostLink = (hash) => { const m = /^#host=([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.exec(hash || ''); return m ? m[1].toLowerCase() : null; };

/** ok | nohttps | nopush: kann dieser Browser Push empfangen? */
export function pushSupport(w = globalThis.window) {
  if (!w || !w.isSecureContext) return 'nohttps';
  return 'serviceWorker' in w.navigator && 'PushManager' in w && 'Notification' in w ? 'ok' : 'nopush';
}

const keyBytes = (b64) => { const s = b64.replace(/-/g, '+').replace(/_/g, '/'); return Uint8Array.from(atob(s + '='.repeat((4 - s.length % 4) % 4)), (c) => c.charCodeAt(0)); };
const toB64 = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

/** Gibt die Funktion zurueck, die den Dialog oeffnet. openHost(id) springt zur Seite eines Hosts, deviceLabel() nennt dieses Geraet (zum Beispiel "Chrome auf Android"). */
export function initAlerts({ api, setMsg, esc, deviceLabel, openHost }) {
  const dlg = $('#alertsDlg'), body = $('#alertsBody'), msg = $('#alertsMsg');
  let data = null, here = false, brave = false, hint = '';
  let timer = 0;

  async function localSubscription() {
    try { const reg = await navigator.serviceWorker.getRegistration(); return reg ? await reg.pushManager.getSubscription() : null; } catch { return null; }
  }

  async function load() {
    try {
      data = await api('/api/push');
      const id = localStorage.getItem(STORE_KEY);
      here = pushSupport() === 'ok' && !!id && data.devices.some((d) => d.id === id) && !!(await localSubscription());
      if (navigator.brave && navigator.brave.isBrave) brave = await navigator.brave.isBrave().catch(() => false);
      render();
    } catch (e) { setMsg(msg, serverMsg(e.message)); }
  }

  const hourOptions = (sel) => Array.from({ length: 24 }, (_, h) => `<option value="${h}"${h === sel ? ' selected' : ''}>${esc(t('alerts.hour', { h }))}</option>`).join('');

  function deviceSection() {
    const support = pushSupport();
    let state = '';
    if (!data.available) state = t('alerts.noServerKey');
    else if (support === 'nohttps') state = t('alerts.noHttps');
    else if (support === 'nopush') state = t('alerts.noPush') + (/iPhone|iPad/.test(navigator.userAgent) ? ' ' + t('alerts.iosHint') : '');
    else if ('Notification' in window && Notification.permission === 'denied' && !here) state = t('alerts.denied');
    else state = here ? t('alerts.on') : t('alerts.off');
    const can = data.available && support === 'ok';
    const button = can ? `<button type="button" class="${here ? 'ghost' : 'primary'}" data-a="${here ? 'disable' : 'enable'}">${esc(here ? t('alerts.disable') : t('alerts.enable'))}</button>` : '';
    const note = can && ((brave && !here) || hint === 'brave') ? `<p class="hint">${esc(t('alerts.braveHint'))}</p>` : '';
    return `<h3>${esc(t('alerts.deviceTitle'))}</h3><p class="al-state" role="status">${esc(state)}</p>${note}<div class="dlg-btns">${button}<button type="button" class="ghost" data-a="test"${data.devices.length ? '' : ' disabled'}>${esc(t('alerts.test'))}</button></div>`;
  }

  function devicesSection() {
    const mine = localStorage.getItem(STORE_KEY);
    const list = data.devices.map((d) => `<div class="dev${d.id === mine && here ? ' current' : ''}"><strong>${esc(d.label)}${d.id === mine && here ? ' · ' + esc(t('alerts.thisDevice')) : ''}</strong><span>${esc(d.lastOk ? t('alerts.lastOk', { when: fmtDate(d.lastOk) }) : t('alerts.neverOk'))}</span><button type="button" class="ghost al-x" data-a="remove" data-id="${esc(d.id)}">${esc(t('alerts.remove'))}</button></div>`).join('');
    return `<h3>${esc(t('alerts.devices'))}</h3><div class="devlist">${list || `<p class="hint">${esc(t('alerts.noDevices'))}</p>`}</div>`;
  }

  function rulesSection() {
    const s = data.settings;
    const rule = (id, extra = '') => `<label class="check al-row"><input type="checkbox" data-k="${id}"${s[id] ? ' checked' : ''}><span>${esc(t(RULE_TEXT[id]))}</span></label>${extra}`;
    const recover = (id) => `<label class="check al-row al-sub"><input type="checkbox" data-k="${id}"${s[id] ? ' checked' : ''}><span>${esc(t('alerts.recover'))}</span></label>`;
    const number = (k, min, max, label) => `<label class="al-num">${esc(t(label))}<input type="number" inputmode="numeric" data-k="${k}" min="${min}" max="${max}" value="${s[k]}"></label>`;
    return `<h3>${esc(t('alerts.rules'))}</h3>
      ${rule('security')}
      ${rule('disk')}${number('diskPercent', 50, 99, tk('alerts.diskFrom'))}${recover('recoverDisk')}
      ${rule('service')}${recover('recoverService')}
      ${rule('offline')}${number('offlineMinutes', 5, 1440, tk('alerts.offlineAfter'))}${recover('recoverOffline')}
      ${rule('reboot')}
      <h3>${esc(t('alerts.quiet'))}</h3><p class="hint">${esc(t('alerts.quietHelp'))}</p>
      <label class="check al-row"><input type="checkbox" data-k="quiet"${s.quiet ? ' checked' : ''}><span>${esc(t('alerts.quietOn'))}</span></label>
      <div class="al-hours"><label>${esc(t('alerts.from'))} <select data-k="quietFrom">${hourOptions(s.quietFrom)}</select></label><label>${esc(t('alerts.to'))} <select data-k="quietTo">${hourOptions(s.quietTo)}</select></label></div>`;
  }

  function logSection() {
    const rows = data.log.map((r) => `<li><button type="button" class="al-log" data-a="host"${r.hostId ? ` data-host="${esc(r.hostId)}"` : ' disabled'}><b>${esc(r.host)}</b><span>${esc(eventText(r))}${r.detail && r.rule !== 'security' ? ' · ' + esc(r.detail) : ''}</span><i>${esc(fmtDate(r.at))} · ${esc(t(STATE_TEXT[r.push] || STATE_TEXT.sent))}</i></button></li>`).join('');
    return `<h3>${esc(t('alerts.log'))}</h3>${rows ? `<ul class="al-logs">${rows}</ul>` : `<p class="hint">${esc(t('alerts.logEmpty'))}</p>`}`;
  }

  function render() {
    const top = dlg.scrollTop;
    body.innerHTML = `<p class="hint">${esc(t('alerts.intro'))}</p>${deviceSection()}${devicesSection()}${rulesSection()}${logSection()}`;
    dlg.scrollTop = top;
  }

  const collect = () => {
    const s = { ...data.settings };
    body.querySelectorAll('[data-k]').forEach((el) => { s[el.dataset.k] = el.type === 'checkbox' ? el.checked : Number(el.value); });
    try { s.zone = Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { /* ohne Zone gilt der Versatz */ }
    s.zoneOffset = -new Date().getTimezoneOffset();
    return s;
  };

  async function save() {
    try { data.settings = await api('/api/push/settings', { method: 'PUT', body: collect() }); setMsg(msg, t('alerts.saved'), true); }
    catch (e) { setMsg(msg, serverMsg(e.message)); }
  }

  async function enable() {
    setMsg(msg, ''); hint = '';
    try {
      const permission = await Notification.requestPermission();      // gleich im Klick, sonst blockiert der Browser die Frage
      if (permission !== 'granted') { render(); setMsg(msg, t('alerts.denied')); return; }
      const { publicKey } = await api('/api/push/key');
      const reg = await navigator.serviceWorker.ready;
      let sub = await reg.pushManager.getSubscription();
      if (sub && sub.options.applicationServerKey && toB64(sub.options.applicationServerKey) !== publicKey) { await sub.unsubscribe(); sub = null; }   // Abo zu einem anderen Schluessel des Servers
      sub = sub || await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
      const j = sub.toJSON();
      const r = await api('/api/push/subscriptions', { method: 'POST', body: { endpoint: j.endpoint, keys: { p256dh: j.keys.p256dh, auth: j.keys.auth }, label: deviceLabel() } });
      localStorage.setItem(STORE_KEY, r.id);
      await save();                                                   // die Zone dieses Geraets gilt fuer die Ruhezeit
      await load();
    } catch (e) {
      if (e && e.status) setMsg(msg, serverMsg(e.message));
      else { hint = brave ? 'brave' : ''; setMsg(msg, t('alerts.pushFailed')); }
      render();
    }
  }

  async function dropLocal() {
    const sub = await localSubscription();
    if (sub) await sub.unsubscribe().catch(() => {});
    localStorage.removeItem(STORE_KEY);
  }

  async function remove(id) {
    try {
      await api(`/api/push/subscriptions/${id}`, { method: 'DELETE' });
      if (id === localStorage.getItem(STORE_KEY)) await dropLocal();
      await load();
    } catch (e) { setMsg(msg, serverMsg(e.message)); }
  }

  async function test(btn) {
    btn.disabled = true; setMsg(msg, '');
    try {
      const r = await api('/api/push/test', { method: 'POST' });
      setMsg(msg, r.limited ? t('alerts.testLimited') : !r.devices ? t('alerts.testNone') : r.sent ? t('alerts.testSent') : t('alerts.testFailed'), !!r.sent);
      await load();
    } catch (e) { setMsg(msg, serverMsg(e.message)); }
    finally { btn.disabled = false; }
  }

  dlg.addEventListener('close', () => { clearTimeout(timer); setMsg(msg, ''); });
  dlg.addEventListener('click', (e) => {
    if (e.target.closest('[data-act=close]')) { dlg.close(); return; }
    const a = e.target.closest('[data-a]'); if (!a) return;
    if (a.dataset.a === 'enable') enable();
    else if (a.dataset.a === 'disable') { const id = localStorage.getItem(STORE_KEY); if (id) remove(id); }
    else if (a.dataset.a === 'remove') remove(a.dataset.id);
    else if (a.dataset.a === 'test') test(a);
    else if (a.dataset.a === 'host' && a.dataset.host) { dlg.close(); openHost(a.dataset.host); }
  });
  dlg.addEventListener('change', (e) => {
    if (!e.target.closest('[data-k]') || !data) return;
    clearTimeout(timer); timer = setTimeout(save, 400);
  });

  return async () => { setMsg(msg, ''); data = null; body.replaceChildren(); dlg.showModal(); await load(); };
}
