import { t } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import { fetchKind, systemHtml, updatesHtml, servicesHtml, dockerHtml, wantsInfo, agentDetail, stateNote, sectionHead, noteP, isKit } from './hostinfo.js';

/* =====================================================================
 * Dialog "Host-Details" (docs/HOST-AGENT.md): Systemwerte, Updates, fehlgeschlagene Dienste und Docker-Container eines Hosts, vom Host-Agenten gelesen.
 * Nur lesen. Gemeinsam fuer Handy (Sheet) und Desktop; das Markup steht in index.html (#hostInfoDlg).
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
 * @param {{state: {hosts: object[]}, api: Function, esc: Function, store: object}} o  store: createHostInfoStore (hostinfo.js), damit die Karten den frischen Stand mitbekommen
 * @returns {(host: object) => void} oeffnet den Dialog fuer einen Host
 */
export function initHostInfoDialog({ state, api, esc, store }) {
  let hostId = null, token = 0;
  const hostNow = () => state.hosts.find((h) => h.id === hostId) || null;
  const msg = (text) => { const m = $('#hostInfoMsg'); m.textContent = text || ''; };

  function skeleton(host) {
    $('#hostInfoTitle').textContent = host.name;
    $('#hostInfoSub').textContent = agentDetail(host) || t('hostinfo.noInfoAgent');
    const caps = (host.agent && host.agent.caps) || [];
    $('#hostInfoBody').innerHTML = SECTIONS.filter((s) => caps.includes(s.cap)).map((s) => `<section${isKit() ? '' : ' class="hi-sec"'} data-kind="${s.kind}">${sectionHead(s.title(), esc)}<div class="hi-body">${noteP(t('hostinfo.stLoading'), esc)}</div></section>`).join('');
  }

  /** Liest alle Abschnitte des Hosts (fresh: ausdruecklich neu) und zeichnet jeden, sobald seine Antwort da ist */
  async function load(fresh) {
    const host = hostNow(); if (!host) return;
    const my = ++token;
    msg(''); $('#hostInfoRefresh').disabled = true;
    const caps = (host.agent && host.agent.caps) || [];
    try {
      await Promise.all(SECTIONS.filter((s) => caps.includes(s.cap)).map(async (s) => {
        let resp;
        try { resp = await fetchKind(api, host.id, s.kind, fresh); }
        catch (err) { if (my === token) msg(serverMsg(err.message)); return; }
        const box = $(`#hostInfoBody [data-kind="${s.kind}"] .hi-body`);
        if (my === token && box) box.innerHTML = s.html(resp, esc);
      }));
      if (my === token && fresh) store.load(host, { fresh: true });                // die Karten bekommen denselben Stand
    } finally { if (my === token) $('#hostInfoRefresh').disabled = false; }
  }

  $('#hostInfoRefresh').addEventListener('click', () => load(true));
  $('#hostInfoDlg').addEventListener('close', () => { token++; });

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
