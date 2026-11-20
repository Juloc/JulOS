import { t } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import { fetchKind, wantsInfo, agentDetail, stateNote, netRate, pickTerminal } from './hostinfo.js';
import { newSetup, setupCommand } from './hostagent.js';
import { makeAct, unitsHtml, fetchUnits, fetchAudit, setControl, setPause, errText } from './hostactions.js';
import { PANELS, panelsFor, clampLines, unavailable } from './hostview.js';
import { noteP, icon } from './hostui.js';
import { loginRequest, savedText } from './hostlogin.js';
import { stateText, hostDetail } from './hosts.js';

/* =====================================================================
 * Die Seite "Host-Details" (docs/HOST-AGENT.md): Systemwerte, Updates, Dienste, Docker, Logs und die Aktionen eines Hosts. Keine Dialoge: am Desktop ist es eine Ansicht im Start-Fenster
 * (Ansicht Hosts, mit Seitenleiste der Abschnitte), am Handy eine eigene Seite mit Zurueck-Pfeil und Reitern. Nur die Bestaetigung einer Aktion bleibt ein kleines Blatt
 * (hostactions-dialog.js); nichts geschieht ohne sie. Jeder Host zeigt dieselben Abschnitte; was sein Agent nicht liefert, erklaert der Abschnitt mit dem naechsten Schritt (hostview.js, unavailable).
 * Dieses Modul schreibt in einen Behaelter, den desktop.js oder mobile.js stellen; das Aussehen steht in hostui.css.
 * ===================================================================== */
const KINDS = ['status', 'updates', 'services', 'docker'];

/**
 * @param {{state: {hosts: object[], connections?: object[], actionsPaused?: boolean}, api: Function, esc: Function, store: object, openAction: Function, reloadHosts: Function, installOverSsh?: Function, snack?: Function, origin?: Function}} o
 *   store: createHostInfoStore (hostinfo.js), damit die Karten den frischen Stand mitbekommen; openAction: initHostActionDialog (hostactions-dialog.js); reloadHosts: laedt die Hostliste neu (Stufe, Pause);
 *   connections: die Verbindungen (fuer das Terminal der Installation per SSH); installOverSsh(conn, command): oeffnet das Terminal und legt den Befehl in die Eingabeleiste, ohne ihn zu senden;
 *   snack(text): kurze Rueckmeldung; origin(): Adresse dieses Servers
 * @returns {{mount: (root: Element, host: object, panel?: string|null, opts?: {onBack?: Function}) => void, unmount: () => void}}
 */
export function initHostPage({ state, api, esc, store, openAction, reloadHosts, installOverSsh = () => {}, snack = () => {}, origin = () => location.origin, reloadConnections = null }) {
  let root = null, hostId = null, token = 0, current = null, onBack = null;
  let setup = newSetup();                                       // die Haken der Einrichtung (Abschnitt Agent)
  let login = { resp: null, error: '' };                          // der Login des Hosts (Abschnitt Login): die Antwort des Servers
  let data = {}, prev = null, units = null, history = null;   // Antworten des Servers je Art, die vorige Ablesung (Netzwerkrate), Einheiten und Verlauf (nur bei Bedarf)
  const $ = (s) => root.querySelector(s);
  const hostNow = () => state.hosts.find((h) => h.id === hostId) || null;
  const msg = (text, ok = false) => { if (root) { const m = $('#hostInfoMsg'); m.textContent = text || ''; m.className = 'msg' + (ok ? ' ok' : ''); } };
  const act = (host) => makeAct(host, !!state.actionsPaused, esc);
  const ctx = (host) => ({ host, esc, data, act: act(host), paused: !!state.actionsPaused, units, history, rate: netRate(prev, data.status), setup, login, terminal: pickTerminal(state.connections || [], host.id) });
  const box = (id) => $(`#hostInfoBody [data-panel="${id}"]`);

  function pageHtml(host) {
    const tabs = panelsFor(host).map((p) => `<button type="button" role="tab" class="hu-tab" id="hit-${p.id}" data-tab="${p.id}" aria-controls="hip-${p.id}" aria-selected="false" tabindex="-1">${icon(p.icon)}<span class="hu-tl">${esc(t(p.title))}</span><i class="hu-bd" hidden></i></button>`).join('');
    const panels = panelsFor(host).map((p) => `<section class="hu-panel" id="hip-${p.id}" role="tabpanel" aria-labelledby="hit-${p.id}" data-panel="${p.id}" hidden tabindex="0"></section>`).join('');
    return `<div class="hu-page">
      <header class="hu-ph">
        <button type="button" class="hu-btn icon" id="hostBack" aria-label="${esc(t('common.back'))}" title="${esc(t('common.back'))}">${icon('back')}</button>
        <div class="hu-dt"><div class="hu-dtt"><h2 id="hostInfoTitle"></h2><span id="hostInfoState" class="hu-state off"></span></div><p id="hostInfoSub" class="hu-dsub"></p></div>
        <button type="button" class="hu-btn icon" id="hostInfoRefresh" aria-label="${esc(t('hostinfo.refresh'))}" title="${esc(t('hostinfo.refresh'))}">${icon('refresh')}</button>
      </header>
      <div class="hu-db"><nav id="hostInfoNav" class="hu-nav" role="tablist" aria-orientation="vertical" aria-label="${esc(t('hostview.sections'))}">${tabs}</nav><div id="hostInfoBody" class="hu-panels">${panels}</div></div>
      <p id="hostInfoMsg" class="msg" role="alert"></p>
    </div>`;
  }

  /** Zeichnet einen Abschnitt neu: Daten, oder die Erklaerung, warum der Agent sie nicht liefert */
  function draw(id) {
    const host = hostNow(), p = PANELS.find((x) => x.id === id), el = root && box(id);
    if (!host || !p || !el) return;
    el.innerHTML = unavailable(host, p, esc) || (p.kind && !data[p.kind] ? noteP(t('hostinfo.stLoading'), esc) : p.html(ctx(host)));
  }
  function badges(host) {
    for (const p of panelsFor(host)) {
      const b = $(`#hit-${p.id} .hu-bd`); if (!b) continue;
      const v = p.badge && !unavailable(host, p, esc) ? p.badge(data, host) : null;
      b.hidden = !v; b.textContent = v ? v.text : ''; b.className = 'hu-bd' + (v ? ' ' + v.tone : '');
    }
  }
  const redraw = (host, kind) => { for (const p of panelsFor(host)) if (p.kind === kind || p.id === 'overview') draw(p.id); badges(host); };

  /** Waehlt einen Abschnitt: zeigt ihn, merkt ihn, laedt bei Bedarf Einheiten (Logs) oder Verlauf */
  function select(id, focus = false) {
    const host = hostNow(); if (!host || !root) return;
    const list = panelsFor(host);
    if (!list.some((p) => p.id === id)) id = list[0] && list[0].id;
    if (!id) return;
    current = id;
    for (const p of list) {
      const on = p.id === id, tab = $(`#hit-${p.id}`), sec = box(p.id);
      if (tab) { tab.setAttribute('aria-selected', String(on)); tab.tabIndex = on ? 0 : -1; }
      if (sec) sec.hidden = !on;
    }
    const cur = $(`#hit-${id}`);
    if (cur && cur.scrollIntoView) cur.scrollIntoView({ block: 'nearest', inline: 'center' });      // am Handy liegt der Reiter sonst ausserhalb der Reihe
    if (focus && cur) cur.focus();
    if (id === 'logs') loadUnits(host);
    if (id === 'history') loadHistory(host);
    if (id === 'login') loadLogin(host);
    draw(id);
  }

  /** Liest alle Abschnitte des Hosts (fresh: ausdruecklich neu) und zeichnet jeden, sobald seine Antwort da ist */
  async function load(fresh) {
    const host = hostNow(); if (!host || !root) return;
    const my = ++token;
    msg(''); $('#hostInfoRefresh').disabled = true;
    const caps = wantsInfo(host) ? (host.agent.caps || []) : [];
    try {
      await Promise.all(PANELS.filter((p) => p.kind && caps.includes(p.cap)).map((p) => p.kind).filter((k, i, a) => a.indexOf(k) === i).map(async (kind) => {
        let resp;
        try { resp = await fetchKind(api, host.id, kind, fresh); }
        catch (err) { if (my === token) msg(serverMsg(err.message)); return; }
        if (my !== token) return;
        if (kind === 'status') { if (data.status && resp.at && data.status.at !== resp.at) prev = data.status; }
        data = { ...data, [kind]: resp };
        redraw(host, kind);
      }));
      if (my === token && fresh && caps.length) store.load(host, { fresh: true });                // die Karten bekommen denselben Stand
    } finally { if (my === token && root) $('#hostInfoRefresh').disabled = false; }
  }

  /** Stufe oder Pause haben sich geaendert: die Hostliste neu laden und die Seite neu zeichnen */
  async function refreshAll() {
    await reloadHosts();
    const host = hostNow(); if (!host || !root) return;
    const keep = current;
    render(host, keep);
  }

  async function loadUnits(host) {
    const a = act(host);
    if (!a.can('services') || !a.can('logs')) return;
    try { units = await fetchUnits(api, host.id); }
    catch (e) { units = { error: e.code || 'agent_error' }; }
    if (hostNow() && root) draw('logs');
  }
  async function loadHistory(host) {
    try { history = { resp: await fetchAudit(api, host.id, 30) }; }
    catch (e) { history = { resp: null }; msg(e.code ? errText(e.code) : e.message); }
    if (root) draw('history');
  }
  async function openUnits(btn) {
    const host = hostNow(); if (!host) return;
    const target = btn.closest('section').querySelector('.hi-units'); if (!target) return;
    target.innerHTML = noteP(t('hostinfo.stLoading'), esc);
    try { target.innerHTML = unitsHtml(await fetchUnits(api, host.id, true), esc, act(host)); }
    catch (e) { target.innerHTML = noteP(e.code ? errText(e.code) : e.message, esc, true); }
  }

  /* ---------- Abschnitt Login: ein Benutzer und ein Passwort fuer alle Verbindungen des Hosts ---------- */
  /** Nach einer Aenderung wissen Verbindungen und Host-Liste, womit sie sich jetzt anmelden (Benutzer, Passwort bereit) */
  const refreshLists = () => (reloadConnections ? reloadConnections() : reloadHosts());
  async function loadLogin(host) {
    try { login = { resp: await api(`/api/hosts/${host.id}/login`), error: '' }; }
    catch (err) { login = { resp: null, error: err.code ? errText(err.code) : err.message }; }
    if (hostNow() && root) draw('login');
  }
  async function loginSave(host, btn) {
    const fields = { user: $('#hostLoginUser').value, password: $('#hostLoginPw').value, noPassword: $('#hostLoginNoPw').checked };
    if (!fields.user.trim()) { msg(t('hostlogin.userMissing')); return; }
    btn.disabled = true; msg('');
    try {
      const resp = await api(`/api/hosts/${host.id}/login`, { method: 'PUT', body: loginRequest(fields) });
      login = { resp, error: '' };
      await refreshLists();
      if (hostNow() && root) { draw('login'); msg(savedText(resp), true); snack(t('hostlogin.saved')); }
    } catch (err) { msg(err.code ? errText(err.code) : serverMsg(err.message)); }
    finally { btn.disabled = false; const pw = root && $('#hostLoginPw'); if (pw) pw.value = ''; }
  }
  // Entfernen braucht ein zweites Tippen; die Passwoerter der Verbindungen bleiben, wie sie waren
  async function loginRemove(host, btn) {
    if (btn.dataset.sure !== '1') { btn.dataset.sure = '1'; btn.textContent = t('hostlogin.reallyRemove'); setTimeout(() => { btn.dataset.sure = ''; btn.textContent = t('hostlogin.remove'); }, 3000); return; }
    btn.dataset.sure = ''; btn.disabled = true; msg('');
    try {
      login = { resp: await api(`/api/hosts/${host.id}/login`, { method: 'DELETE' }), error: '' };
      await refreshLists();
      if (hostNow() && root) { draw('login'); msg(t('hostlogin.removed'), true); snack(t('hostlogin.removed')); }
    } catch (err) { btn.disabled = false; msg(err.code ? errText(err.code) : serverMsg(err.message)); }
  }

  /* ---------- Abschnitt Agent: Einrichtung, aktualisieren, entfernen ---------- */
  /** Frischer Code (10 Minuten, einmal) und der fertige Einzeiler zu den Haken */
  async function newCommand(host) {
    const r = await api(`/api/hosts/${host.id}/agent/code`, { method: 'POST', body: {} });
    return setupCommand(origin(), r.code, setup);
  }
  async function agentCopy(host, btn) {
    btn.disabled = true; msg('');
    try {
      setup.command = await newCommand(host); setup.note = t('shell.sshCommandNote');
      draw('agent');
      try { await navigator.clipboard.writeText(setup.command); msg(t('shell.commandsCopied'), true); }
      catch { const pre = $('#hostAgentCmd'); if (pre && root.ownerDocument) { const r = root.ownerDocument.createRange(); r.selectNodeContents(pre); const sel = root.ownerDocument.defaultView.getSelection(); sel.removeAllRanges(); sel.addRange(r); } msg(t('shell.copySelected')); }
    } catch (err) { msg(err.code ? errText(err.code) : err.message); }
    finally { btn.disabled = false; }
  }
  /** "Agent per SSH installieren": Terminal dieses Hosts oeffnen und den Befehl in die Eingabeleiste legen, NICHT senden. Das sudo-Passwort tippt der Benutzer im Terminal; JulOS sieht es nie. */
  async function agentSsh(host, btn) {
    const conn = pickTerminal(state.connections || [], host.id); if (!conn) return;
    btn.disabled = true; msg('');
    try { installOverSsh(conn, await newCommand(host)); }
    catch (err) { msg(err.code ? errText(err.code) : err.message); }
    finally { btn.disabled = false; }
  }
  // Aktualisieren: der Server schickt dem Agenten die Anweisung, der Agent holt die Datei selbst, prueft sie und startet neu; danach fragen wir kurz die Liste, bis die neue Version da ist
  async function agentUpdate(host, btn) {
    btn.disabled = true; msg('');
    try {
      const r = await api(`/api/hosts/${host.id}/agent/update`, { method: 'POST', body: {} });
      msg(t('shell.agentUpdating', { version: r.version }), true);
      for (let i = 0; i < 20 && root; i++) {
        await new Promise((ok) => setTimeout(ok, 2000));
        await reloadHosts();
        const now = hostNow();
        if (now && now.agent && now.agent.online && !now.agent.updateAvailable) { msg(t('shell.agentNowOn', { version: now.agent.version }), true); snack(t('shell.agentUpdated')); return; }
      }
      msg(t('shell.agentNoReport'));
    } catch (err) { msg(err.code ? errText(err.code) : err.message); }
    finally { if (root && hostNow()) draw('agent'); }
  }
  async function agentAuto(host, box) {
    box.disabled = true; msg('');
    try { await api(`/api/hosts/${host.id}/agent/auto-update`, { method: 'PUT', body: { enabled: box.checked } }); await reloadHosts(); }
    catch (err) { box.checked = !box.checked; msg(err.code ? errText(err.code) : err.message); }
    finally { box.disabled = false; }
  }
  // Entfernen braucht ein zweites Tippen (das Token gilt danach nicht mehr, der Agent trennt sich sofort)
  async function agentRemove(host, btn) {
    if (btn.dataset.sure !== '1') { btn.dataset.sure = '1'; btn.textContent = t('shell.reallyRemove'); setTimeout(() => { btn.dataset.sure = ''; btn.textContent = t('app.agentRemove'); }, 3000); return; }
    btn.dataset.sure = '';
    try { await api(`/api/hosts/${host.id}/agent`, { method: 'DELETE' }); await reloadHosts(); setup = newSetup(); snack(t('shell.agentRemoved')); const h = hostNow(); if (h && root) render(h, 'agent'); }
    catch (err) { msg(err.code ? errText(err.code) : err.message); }
  }

  /** Alle Eingaben der Seite laufen ueber den Behaelter (Delegation): die Elemente darunter werden bei jedem Zeichnen neu gebaut, der Behaelter bleibt */
  const onClick = (e) => {
    const h = hostNow(); if (!h) return;
    const tg = e.target;
    if (tg.closest('#hostBack')) { if (onBack) onBack(); return; }
    if (tg.closest('#hostInfoRefresh')) { units = null; history = null; load(true); if (current === 'logs' || current === 'history') select(current); return; }
    const tab = tg.closest('[data-tab]');
    if (tab) { select(tab.dataset.tab); return; }
    // Tasten im Inhalt: Aktionen (Bestaetigung), Logs, Dienste laden, Verlauf, "Ansehen", Agent einrichten
    const a = tg.closest('[data-hact]');
    if (a) { openAction(h, a.dataset.hact, a.dataset.htarget || null, { onDone: () => { load(true); } }); return; }
    const logs = tg.closest('[data-hlogs]');
    if (logs) {
      const unit = $('#hostLogUnit') && $('#hostLogUnit').value;
      if (unit) openAction(h, 'logs.unit', unit, { lines: clampLines($('#hostLogLines') && $('#hostLogLines').value) });
      return;
    }
    const go = tg.closest('[data-hgoto]');
    if (go) { select(go.dataset.hgoto, true); return; }
    const sug = tg.closest('[data-hl-suggest]');
    if (sug) { const f = $('#hostLoginUser'); if (f) { f.value = sug.dataset.hlSuggest; f.focus(); } return; }
    for (const [attr, fn] of [['[data-hl-save]', loginSave], ['[data-hl-remove]', loginRemove], ['[data-hag-copy]', agentCopy], ['[data-hag-ssh]', agentSsh], ['[data-hag-update]', agentUpdate], ['[data-hag-remove]', agentRemove]]) { const b = tg.closest(attr); if (b) { fn(h, b); return; } }
    const g = tg.closest('[data-goto]');
    if (g) { select(g.dataset.goto, true); return; }
    const o = tg.closest('[data-hact-open]');
    if (o && o.dataset.hactOpen === 'units') openUnits(o);
    else if (o && o.dataset.hactOpen === 'history') loadHistory(h);
  };
  // Navigation mit der Tastatur: Pfeile bewegen die Auswahl, Pos1/Ende springen (Reiter-Muster)
  const onKey = (e) => {
    if (e.key === 'Enter' && e.target.closest && e.target.closest('#hostLoginForm') && e.target.tagName === 'INPUT' && e.target.type !== 'checkbox') {    // Enter in den Feldern des Logins speichert
      e.preventDefault(); const b = $('[data-hl-save]'), h = hostNow(); if (b && h) loginSave(h, b); return;
    }
    const tabs = [...root.querySelectorAll('#hostInfoNav [data-tab]')], i = tabs.findIndex((x) => x === e.target.closest('[data-tab]'));
    if (i < 0) return;
    const next = e.key === 'ArrowDown' || e.key === 'ArrowRight' ? i + 1 : e.key === 'ArrowUp' || e.key === 'ArrowLeft' ? i - 1 : e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : null;
    if (next === null) return;
    e.preventDefault();
    select(tabs[(next + tabs.length) % tabs.length].dataset.tab, true);
  };
  const onChange = async (e) => {
    const h = hostNow(); if (!h) return;
    const d = e.target.dataset || {};
    if (d.allow) {                                              // ein Haken der Einrichtung; die Docker-Gruppe erscheint erst mit dem Haken "Container steuern"
      setup.allow = e.target.checked ? [...new Set([...setup.allow, d.allow])] : setup.allow.filter((w) => w !== d.allow);
      if (d.allow === 'docker') { if (!e.target.checked) setup.dockerGroup = false; const row = $('[data-hag-grouprow]'); if (row) { row.hidden = !e.target.checked; const g = row.querySelector('input'); if (g && !e.target.checked) g.checked = false; } }
      setup.command = ''; const pre = $('.hu-cmd'); if (pre) pre.remove();
      return;
    }
    if (d.hagGroup) { setup.dockerGroup = e.target.checked; setup.command = ''; const pre = $('.hu-cmd'); if (pre) pre.remove(); return; }
    if (d.hagAuto) { agentAuto(h, e.target); return; }
    if (d.hlNopw) { const pw = $('#hostLoginPw'); if (pw) { if (e.target.checked) pw.value = ''; pw.disabled = e.target.checked; } return; }
    try {
      if (e.target.id === 'hostActLevel') { await setControl(api, h.id, e.target.value); await refreshAll(); }
      else if (e.target.id === 'hostActPause') { await setPause(api, e.target.checked); await refreshAll(); }
    } catch (err) { msg(err.code ? errText(err.code) : err.message); }
  };

  /** Baut die Seite im Behaelter auf und haengt die Bedienung an (jeder Aufruf zeichnet neu; die Elemente sind neu, also gibt es keine alten Zuhoerer) */
  function render(host, panel) {
    root.innerHTML = pageHtml(host);
    $('#hostInfoTitle').textContent = host.name;
    $('#hostInfoSub').textContent = [hostDetail(host), agentDetail(host) || t('hostinfo.noInfoAgent')].filter(Boolean).join(' · ');
    const st = $('#hostInfoState'); st.className = 'hu-state ' + (host.online ? 'ok' : 'off'); st.innerHTML = `<i class="hu-dot ${host.online ? 'ok' : 'off'}"></i>${esc(stateText(host.online))}`;
    $('#hostInfoRefresh').hidden = !wantsInfo(host);

    for (const k of KINDS) if (data[k]) redraw(host, k);
    select(panel);
    if (wantsInfo(host)) load(false);
  }

  const detach = () => { if (root && root.removeEventListener) for (const [n, f] of [['click', onClick], ['keydown', onKey], ['change', onChange]]) root.removeEventListener(n, f); };
  return {
    mount(el, host, panel = null, opts = {}) {
      detach(); root = el; hostId = host.id;
      root.addEventListener('click', onClick); root.addEventListener('keydown', onKey); root.addEventListener('change', onChange); onBack = opts.onBack || null;
      data = {}; prev = null; units = null; history = null; login = { resp: null, error: '' }; current = null; setup = newSetup(); token++;
      render(host, panel);
    },
    unmount() { token++; detach(); root = null; },
  };
}
