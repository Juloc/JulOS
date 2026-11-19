import { t } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import { fetchKind, wantsInfo, agentDetail, stateNote, netRate } from './hostinfo.js';
import { makeAct, unitsHtml, auditHtml, fetchUnits, fetchAudit, setControl, setPause, errText } from './hostactions.js';
import { PANELS, panelsFor, clampLines } from './hostview.js';
import { noteP, icon, empty } from './hostui.js';
import { stateText, hostDetail } from './hosts.js';

/* =====================================================================
 * Dialog "Host-Details" (docs/HOST-AGENT.md): Systemwerte, Updates, Dienste, Docker, Logs und die Aktionen eines Hosts, vom Host-Agenten gelesen. Die Abschnitte stehen als Navigation
 * (Desktop: Seitenleiste, Handy: Reiter oben) neben dem Inhalt; jeder Abschnitt ist eine Folge von Karten (hostview.js). Jede Aktionstaste oeffnet den Bestaetigungsdialog
 * (hostactions-dialog.js); nichts geschieht ohne ihn. Gemeinsam fuer Handy (Sheet) und Desktop; das Markup steht in index.html (#hostInfoDlg), das Aussehen in hostui.css.
 * ===================================================================== */
const $ = (s) => document.querySelector(s);
const KINDS = ['status', 'updates', 'services', 'docker'];

/**
 * @param {{state: {hosts: object[], actionsPaused?: boolean}, api: Function, esc: Function, store: object, openAction: Function, reloadHosts: Function}} o
 *   store: createHostInfoStore (hostinfo.js), damit die Karten den frischen Stand mitbekommen; openAction: initHostActionDialog (hostactions-dialog.js); reloadHosts: laedt die Hostliste neu (Stufe, Pause)
 * @returns {(host: object) => void} oeffnet den Dialog fuer einen Host
 */
export function initHostInfoDialog({ state, api, esc, store, openAction, reloadHosts }) {
  let hostId = null, token = 0, current = null;
  let data = {}, prev = null, units = null, history = null;   // Antworten des Servers je Art, die vorige Ablesung (Netzwerkrate), Einheiten und Verlauf (nur bei Bedarf)
  const hostNow = () => state.hosts.find((h) => h.id === hostId) || null;
  const msg = (text) => { const m = $('#hostInfoMsg'); m.textContent = text || ''; };
  const act = (host) => makeAct(host, !!state.actionsPaused, esc);
  const panels = (host) => panelsFor(host);
  const ctx = (host) => ({ host, esc, data, act: act(host), paused: !!state.actionsPaused, units, history, rate: netRate(prev, data.status) });
  const box = (id) => $(`#hostInfoBody [data-panel="${id}"]`);

  /** Navigation und leere Abschnitte: eine Taste je Abschnitt (Symbol, Name, Zaehler) */
  function navHtml(host) {
    return panels(host).map((p) => `<button type="button" role="tab" class="hu-tab" id="hit-${p.id}" data-tab="${p.id}" aria-controls="hip-${p.id}" aria-selected="false" tabindex="-1">${icon(p.icon)}<span class="hu-tl">${esc(t(p.title))}</span><i class="hu-bd" hidden></i></button>`).join('');
  }
  function skeleton(host) {
    $('#hostInfoTitle').textContent = host.name;
    $('#hostInfoSub').textContent = [hostDetail(host), agentDetail(host) || t('hostinfo.noInfoAgent')].filter(Boolean).join(' · ');
    const st = $('#hostInfoState'); if (st) { st.className = 'hu-state ' + (host.online ? 'ok' : 'off'); st.innerHTML = `<i class="hu-dot ${host.online ? 'ok' : 'off'}"></i>${esc(stateText(host.online))}`; }
    $('#hostInfoNav').innerHTML = navHtml(host);
    $('#hostInfoBody').innerHTML = panels(host).map((p) => `<section class="hu-panel" id="hip-${p.id}" role="tabpanel" aria-labelledby="hit-${p.id}" data-panel="${p.id}" hidden tabindex="0">${p.kind ? noteP(t('hostinfo.stLoading'), esc) : ''}</section>`).join('');
  }

  /** Zeichnet einen Abschnitt neu und aktualisiert den Zaehler an der Navigation */
  function draw(id) {
    const host = hostNow(), p = PANELS.find((x) => x.id === id), el = box(id);
    if (!host || !p || !el) return;
    el.innerHTML = p.html(ctx(host));
  }
  function badges(host) {
    for (const p of panels(host)) {
      const b = $(`#hit-${p.id} .hu-bd`); if (!b) continue;
      const v = p.badge ? p.badge(data, host) : null;
      b.hidden = !v; b.textContent = v ? v.text : ''; b.className = 'hu-bd' + (v ? ' ' + v.tone : '');
    }
  }
  const redraw = (host, kind) => { for (const p of panels(host)) if (p.kind === kind || p.id === 'overview') draw(p.id); badges(host); };

  /** Waehlt einen Abschnitt: zeigt ihn, merkt ihn, laedt bei Bedarf Einheiten (Logs) oder Verlauf */
  function select(id, focus = false) {
    const host = hostNow(); if (!host) return;
    const list = panels(host);
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
    const body = $('#hostInfoBody'); if (body) body.scrollTop = 0;
    if (id === 'logs') loadUnits(host);
    if (id === 'history') loadHistory(host);
    if (id === 'actions' || id === 'logs' || id === 'history') draw(id);
  }

  /** Liest alle Abschnitte des Hosts (fresh: ausdruecklich neu) und zeichnet jeden, sobald seine Antwort da ist */
  async function load(fresh) {
    const host = hostNow(); if (!host) return;
    const my = ++token;
    msg(''); $('#hostInfoRefresh').disabled = true;
    const caps = (host.agent && host.agent.caps) || [];
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
      if (my === token && fresh) store.load(host, { fresh: true });                // die Karten bekommen denselben Stand
    } finally { if (my === token) $('#hostInfoRefresh').disabled = false; }
  }

  /** Stufe oder Pause haben sich geaendert: die Hostliste neu laden und den Dialog neu zeichnen */
  async function refreshAll() {
    await reloadHosts();
    const host = hostNow(); if (!host) return;
    const keep = current;
    skeleton(host);
    select(keep);
    for (const k of KINDS) if (data[k]) redraw(host, k);
    load(false);
  }

  async function loadUnits(host) {
    const a = act(host);
    if (!a.can('services') || !a.can('logs')) return;
    try { units = await fetchUnits(api, host.id); }
    catch (e) { units = { error: e.code || 'agent_error' }; }
    if (hostNow()) draw('logs');
  }
  async function loadHistory(host) {
    try { history = { resp: await fetchAudit(api, host.id, 30) }; }
    catch (e) { history = { resp: null }; msg(e.code ? errText(e.code) : e.message); }
    draw('history');
  }
  async function openUnits(btn) {
    const host = hostNow(); if (!host) return;
    const target = btn.closest('section').querySelector('.hi-units'); if (!target) return;
    target.innerHTML = noteP(t('hostinfo.stLoading'), esc);
    try { target.innerHTML = unitsHtml(await fetchUnits(api, host.id, true), esc, act(host)); }
    catch (e) { target.innerHTML = noteP(e.code ? errText(e.code) : e.message, esc, true); }
  }

  $('#hostInfoRefresh').addEventListener('click', () => { units = null; history = null; load(true); const h = hostNow(); if (h && (current === 'logs' || current === 'history')) select(current); });
  $('#hostInfoDlg').addEventListener('close', () => { token++; });

  // Navigation mit der Tastatur: Pfeile bewegen die Auswahl, Pos1/Ende springen (Reiter-Muster)
  $('#hostInfoNav').addEventListener('keydown', (e) => {
    const tabs = [...document.querySelectorAll('#hostInfoNav [data-tab]')], i = tabs.findIndex((x) => x === e.target.closest('[data-tab]'));
    if (i < 0) return;
    const next = e.key === 'ArrowDown' || e.key === 'ArrowRight' ? i + 1 : e.key === 'ArrowUp' || e.key === 'ArrowLeft' ? i - 1 : e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : null;
    if (next === null) return;
    e.preventDefault();
    select(tabs[(next + tabs.length) % tabs.length].dataset.tab, true);
  });
  $('#hostInfoNav').addEventListener('click', (e) => { const b = e.target.closest('[data-tab]'); if (b) select(b.dataset.tab); });

  // Alle Tasten im Inhalt gehen ueber diese eine Stelle: Aktionen (Bestaetigung), Logs, Dienste laden, Verlauf, "Ansehen", Stufe, Pause
  $('#hostInfoBody').addEventListener('click', (e) => {
    const host = hostNow(); if (!host) return;
    const a = e.target.closest('[data-hact]');
    if (a) { openAction(host, a.dataset.hact, a.dataset.htarget || null, { onDone: () => { load(true); } }); return; }
    const logs = e.target.closest('[data-hlogs]');
    if (logs) {
      const unit = $('#hostLogUnit') && $('#hostLogUnit').value;
      if (unit) openAction(host, 'logs.unit', unit, { lines: clampLines($('#hostLogLines') && $('#hostLogLines').value) });
      return;
    }
    const g = e.target.closest('[data-goto]');
    if (g) { select(g.dataset.goto, true); return; }
    const o = e.target.closest('[data-hact-open]');
    if (o && o.dataset.hactOpen === 'units') openUnits(o);
    else if (o && o.dataset.hactOpen === 'history') loadHistory(host);
  });
  $('#hostInfoBody').addEventListener('change', async (e) => {
    const host = hostNow(); if (!host) return;
    try {
      if (e.target.id === 'hostActLevel') { await setControl(api, host.id, e.target.value); await refreshAll(); }
      else if (e.target.id === 'hostActPause') { await setPause(api, e.target.checked); await refreshAll(); }
    } catch (err) { msg(err.code ? errText(err.code) : err.message); }
  });

  return (host, panel = null) => {
    hostId = host.id; data = {}; prev = null; units = null; history = null; current = null;
    msg('');
    skeleton(host);
    // Ein Host ohne verbundenen Host-Agenten hat nichts zu lesen: der Dialog sagt es, statt leer zu bleiben
    const nothing = !wantsInfo(host);
    $('#hostInfoRefresh').hidden = nothing;
    $('#hostInfoNav').hidden = nothing;
    if (nothing) $('#hostInfoBody').innerHTML = empty(t('hostview.noAgentTitle'), stateNote({ state: host.agent ? 'offline' : 'no_agent' }), esc, '', 'server');
    $('#hostInfoDlg').showModal();
    if (!nothing) { select(panel); load(false); }              // den Fokus setzt der Dialog selbst (erste Taste); die Pfeiltasten wechseln den Abschnitt
  };
}
