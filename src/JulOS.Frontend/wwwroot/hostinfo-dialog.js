import { t } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import { fetchKind, systemHtml, updatesHtml, servicesHtml, dockerHtml, wantsInfo, agentDetail, stateNote, sectionHead, noteP, isKit } from './hostinfo.js';
import { makeAct, hasActionAgent, controlHtml, unitsHtml, auditHtml, fetchUnits, fetchAudit, setControl, setPause, errText } from './hostactions.js';

/* =====================================================================
 * Dialog "Host-Details" (docs/HOST-AGENT.md): Systemwerte, Updates, fehlgeschlagene Dienste und Docker-Container eines Hosts, vom Host-Agenten gelesen, und (Stufe 3) die Aktionen darauf:
 * Schalter fuer die Stufe und die Pause, Tasten an den Eintraegen, Verlauf. Jede Taste oeffnet den Bestaetigungsdialog (hostactions-dialog.js); nichts geschieht ohne ihn.
 * Gemeinsam fuer Handy (Sheet) und Desktop; das Markup steht in index.html (#hostInfoDlg).
 * ===================================================================== */
const $ = (s) => document.querySelector(s);

/** Die Abschnitte: Art der Antwort (der Pfad unter /api/hosts/{id}/), Faehigkeit, die der Agent dafuer melden muss, Baustein, Ueberschrift */
const SECTIONS = [
  { kind: 'status', cap: 'sysinfo', html: systemHtml, title: () => t('hostinfo.secSystem') },
  { kind: 'updates', cap: 'updates', html: updatesHtml, title: () => t('hostinfo.secUpdates') },
  { kind: 'services', cap: 'services', html: servicesHtml, title: () => t('hostinfo.secServices') },
  { kind: 'docker', cap: 'docker', html: dockerHtml, title: () => t('hostinfo.secDocker') },
];

/**
 * @param {{state: {hosts: object[], actionsPaused?: boolean}, api: Function, esc: Function, store: object, openAction: Function, reloadHosts: Function}} o
 *   store: createHostInfoStore (hostinfo.js), damit die Karten den frischen Stand mitbekommen; openAction: initHostActionDialog (hostactions-dialog.js); reloadHosts: laedt die Hostliste neu (Stufe, Pause)
 * @returns {(host: object) => void} oeffnet den Dialog fuer einen Host
 */
export function initHostInfoDialog({ state, api, esc, store, openAction, reloadHosts }) {
  let hostId = null, token = 0;
  const hostNow = () => state.hosts.find((h) => h.id === hostId) || null;
  const msg = (text) => { const m = $('#hostInfoMsg'); m.textContent = text || ''; };
  const act = (host) => makeAct(host, !!state.actionsPaused, esc);
  const actionsOn = (host) => !!state.hosts && hasActionAgent(host);

  function skeleton(host) {
    $('#hostInfoTitle').textContent = host.name;
    $('#hostInfoSub').textContent = agentDetail(host) || t('hostinfo.noInfoAgent');
    const caps = (host.agent && host.agent.caps) || [];
    const sec = (kind, title, inner) => `<section${isKit() ? '' : ' class="hi-sec"'} data-kind="${kind}">${sectionHead(title, esc)}<div class="hi-body">${inner}</div></section>`;
    const control = actionsOn(host) ? `<section${isKit() ? '' : ' class="hi-sec"'} data-kind="control"><div class="hi-body">${controlHtml(host, !!state.actionsPaused, esc)}</div></section>` : '';
    const history = actionsOn(host) ? sec('history', t('hostact.secHistory'), `<button type="button" class="${isKit() ? 'm3-btn sm tonal' : 'ghost hi-act'}" data-hact-open="history">${esc(t('hostact.showHistory'))}</button>`) : '';
    $('#hostInfoBody').innerHTML = SECTIONS.filter((s) => caps.includes(s.cap)).map((s) => sec(s.kind, s.title(), noteP(t('hostinfo.stLoading'), esc))).join('') + control + history;
  }

  /** Liest alle Abschnitte des Hosts (fresh: ausdruecklich neu) und zeichnet jeden, sobald seine Antwort da ist */
  async function load(fresh) {
    const host = hostNow(); if (!host) return;
    const my = ++token;
    msg(''); $('#hostInfoRefresh').disabled = true;
    const caps = (host.agent && host.agent.caps) || [];
    const a = act(host);
    try {
      await Promise.all(SECTIONS.filter((s) => caps.includes(s.cap)).map(async (s) => {
        let resp;
        try { resp = await fetchKind(api, host.id, s.kind, fresh); }
        catch (err) { if (my === token) msg(serverMsg(err.message)); return; }
        const box = $(`#hostInfoBody [data-kind="${s.kind}"] .hi-body`);
        if (my === token && box) box.innerHTML = s.html(resp, esc, a);
      }));
      if (my === token && fresh) store.load(host, { fresh: true });                // die Karten bekommen denselben Stand
    } finally { if (my === token) $('#hostInfoRefresh').disabled = false; }
  }

  /** Stufe oder Pause haben sich geaendert: die Hostliste neu laden und den Dialog neu zeichnen */
  async function refreshAll() {
    await reloadHosts();
    const host = hostNow(); if (!host) return;
    skeleton(host);
    load(false);
  }

  async function openUnits(btn) {
    const host = hostNow(); if (!host) return;
    const box = btn.closest('section').querySelector('.hi-units'); if (!box) return;
    box.innerHTML = noteP(t('hostinfo.stLoading'), esc);
    try { box.innerHTML = unitsHtml(await fetchUnits(api, host.id, true), esc, act(host)); }
    catch (e) { box.innerHTML = noteP(e.code ? errText(e.code) : e.message, esc, true); }
  }

  async function openHistory(btn) {
    const host = hostNow(); if (!host) return;
    const box = btn.closest('section').querySelector('.hi-body');
    box.innerHTML = noteP(t('hostinfo.stLoading'), esc);
    try { box.innerHTML = auditHtml(await fetchAudit(api, host.id, 30), esc) + `<button type="button" class="${isKit() ? 'm3-btn sm tonal' : 'ghost hi-act'}" data-hact-open="history">${esc(t('hostinfo.refresh'))}</button>`; }
    catch (e) { box.innerHTML = noteP(e.code ? errText(e.code) : e.message, esc, true); }
  }

  $('#hostInfoRefresh').addEventListener('click', () => load(true));
  $('#hostInfoDlg').addEventListener('close', () => { token++; });

  // Alle Tasten im Dialog gehen ueber diese eine Stelle: Aktionen (Bestaetigung), Dienste laden, Verlauf, Stufe, Pause
  $('#hostInfoBody').addEventListener('click', (e) => {
    const host = hostNow(); if (!host) return;
    const a = e.target.closest('[data-hact]');
    if (a) { openAction(host, a.dataset.hact, a.dataset.htarget || null, { onDone: () => { load(true); } }); return; }
    const o = e.target.closest('[data-hact-open]');
    if (o && o.dataset.hactOpen === 'units') openUnits(o);
    else if (o && o.dataset.hactOpen === 'history') openHistory(o);
  });
  $('#hostInfoBody').addEventListener('change', async (e) => {
    const host = hostNow(); if (!host) return;
    try {
      if (e.target.id === 'hostActLevel') { await setControl(api, host.id, e.target.value); await refreshAll(); }
      else if (e.target.id === 'hostActPause') { await setPause(api, e.target.checked); await refreshAll(); }
    } catch (err) { msg(err.code ? errText(err.code) : err.message); }
  });

  return (host) => {
    hostId = host.id;
    msg('');
    skeleton(host);
    // Ein Host ohne verbundenen Host-Agenten hat nichts zu lesen: der Dialog sagt es, statt leer zu bleiben
    const nothing = !wantsInfo(host);
    $('#hostInfoRefresh').hidden = nothing;
    if (nothing) $('#hostInfoBody').innerHTML = noteP(stateNote({ state: host.agent ? 'offline' : 'no_agent' }), esc);
    $('#hostInfoDlg').showModal();
    if (!nothing) load(false);
  };
}
