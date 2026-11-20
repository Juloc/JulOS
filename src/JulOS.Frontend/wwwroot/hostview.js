/* =====================================================================
 * Host-Oberflaeche (docs/HOSTS.md, docs/HOST-AGENT.md): die Host-Karte der Hostliste und die Abschnitte des Dialogs "Host-Details". Reine Funktionen ohne DOM (bis auf das
 * Menue der Karte am Ende), gemeinsam fuer Handy und Desktop; das Aussehen steht in hostui.css, die Bausteine in hostui.js, die Rechenlogik in hostinfo.js.
 *  - cardModel: was die Karte zeigt (Art, Kennzahlen, Hinweise); hostCardHtml: das Markup
 *  - PANELS: die Abschnitte des Dialogs (Ueberblick, Auslastung, Datentraeger, Netzwerk, Updates, Dienste, Docker, Logs, Aktionen, Verlauf), je ein Baustein aus den Antworten des Servers
 * Jeder Wert vom Agenten (Namen, Pakete, Adressen, Ausgaben) geht durch esc; Markup entsteht nur hier.
 * ===================================================================== */
import { t, tp, tk, fmtNumber } from './i18n.js';
import { zfsState, zfsLines, hostSupport, fmtBytes, fmtUptime, fmtAge, fmtPercent, loadText, when, ramPercent, mainDisk, healthIssues, MD_STATE, SMART_STATE, SMART_WHY, rateText, wantsInfo, stateNote, agentModeText } from './hostinfo.js';
import { stateText, dotClass, hostDetail, serviceState } from './hosts.js';
import { agentPanel } from './hostagent.js';
import { loginPanel } from './hostlogin.js';
import { icon, dl, noteP, callout, subH, chip, bar, meter, card, listBlock, empty, button } from './hostui.js';
import { hasActionAgent, controlHtml, auditHtml, errText, blockReason, whyText } from './hostactions.js';

/* ---------- Kennzahlen ---------- */
/** Last je Kern in Prozent (1 = jeder Kern ausgelastet); null, wenn die Kernzahl fehlt */
const loadPct = (load, cores) => (Number.isFinite(load) && cores > 0 ? (load / cores) * 100 : null);

/** Die Kennzahlen eines Hosts aus /status (info): Last, RAM, Platte; leer ohne Angaben. warnAt/badAt je Art: Last ab 100 % (ein Kern je Aufgabe) gelb, ab 150 % rot */
export function meters(info) {
  if (!info) return [];
  const out = [];
  const l1 = info.load && info.load[0];
  if (Number.isFinite(l1)) out.push({ id: 'cpu', icon: 'speed', label: t('hostview.cpu'), short: t('hostview.cpuShort'), value: fmtNumber(l1, 2), pct: loadPct(l1, info.cpuCount), warnAt: 100, badAt: 150, warn: !!(info.cpuCount && l1 > info.cpuCount * 1.5), sub: info.cpuCount ? t('hostinfo.cores', { n: info.cpuCount }) : '' });
  const mem = info.memory, ram = ramPercent(mem);
  if (ram !== null) out.push({ id: 'ram', icon: 'memory', label: t('hostview.ram'), short: t('hostview.ramShort'), value: fmtPercent(ram), pct: ram, warn: ram >= 90, sub: t('hostinfo.ofTotal', { used: fmtBytes(mem.totalBytes - mem.availableBytes), total: fmtBytes(mem.totalBytes) }) });
  const d = mainDisk(info.disks);
  if (d) out.push({ id: 'disk', icon: 'disk', label: t('hostview.disk'), short: t('hostview.disk'), value: fmtPercent(d.percent), pct: d.percent, warn: d.percent >= 90, sub: `${d.mount} · ${t('hostinfo.ofTotal', { used: fmtBytes(d.usedBytes), total: fmtBytes(d.totalBytes) })}` });
  return out;
}

/** Was auf der Karte steht: Art des Agenten, Kennzahlen, Hinweise (nur wenn etwas vorliegt). entry kommt aus dem Speicher (hostinfo.js, get). */
export function cardModel(h, entry) {
  const a = h.agent;
  const kind = !a ? 'none' : a.kind === 'host' ? 'host' : 'desktop';
  const info = entry && entry.status && entry.status.info;
  const flags = [];
  const u = entry && entry.updates && entry.updates.updates;
  if (u && u.count > 0) flags.push({ id: 'updates', panel: 'updates', icon: 'update', tone: u.security > 0 ? 'bad' : '', text: tp('hostview.flagUpdates', u.count), detail: u.securityKnown && u.security > 0 ? t('hostview.flagSecurity', { n: u.security }) : '' });
  if (info && info.rebootRequired) flags.push({ id: 'reboot', panel: 'actions', icon: 'power', tone: 'warn', text: t('hostinfo.rebootNeeded') });
  const svc = entry && entry.services && entry.services.services;
  if (svc && svc.count > 0) flags.push({ id: 'services', panel: 'services', icon: 'gear', tone: 'bad', text: tp('hostinfo.failedUnits', svc.count) });
  const issues = healthIssues(info);
  if (issues.length) flags.push({ id: 'health', panel: 'disks', icon: 'disk', tone: 'bad', text: t('hostview.flagDisk'), detail: issues[0] });
  if (a && a.outdated) flags.push({ id: 'agent', icon: 'warn', tone: 'warn', text: t('hosts.outdated') });
  else if (a && a.updateAvailable) flags.push({ id: 'agent', icon: 'update', tone: '', text: t('hosts.updateAvailable') });
  const m = meters(info);
  const up = info ? fmtUptime(info.uptimeSeconds) : '';
  return { kind, online: !!h.online, agentOnline: !!(a && a.online), meters: m, uptime: up, flags, loaded: !!info };
}

/* ---------- die Karte ---------- */
/** Der Satz zum Agenten unter dem Namen: Art, Version, bei getrenntem Agenten wann zuletzt */
function agentLine(h) {
  const a = h.agent;
  if (!a) return t('hosts.noAgent');
  const mode = agentModeText(h);
  const parts = [mode, a.version && t('hosts.agentVersion', { v: a.version })].filter(Boolean);
  if (!a.online) parts.push(t('hosts.agentOffline') + (a.lastSeen && when(a.lastSeen) ? ' ' + t('hosts.lastSeen', { when: when(a.lastSeen) }) : ''));
  return parts.join(' · ');
}

/** Die Aktionen im Menue der Karte: Aktion (oder Seite), noetiges Recht, Symbol, Text. Jede laeuft ueber den bestaetigten Ablauf (hostactions-dialog.js); gesperrte stehen ausgegraut mit Grund. */
const CARD_ACTIONS = [
  { id: 'refresh', action: 'updates.refresh', right: 'updates', icon: 'refresh', label: tk('hostview.mRefresh') },
  { id: 'install', action: 'updates.install', target: 'all', right: 'updates', icon: 'download', label: tk('hostview.mInstall') },
  { id: 'reboot', action: 'power.reboot', right: 'reboot', icon: 'power', label: tk('hostview.mReboot') },
  { id: 'poweroff', action: 'power.poweroff', right: 'reboot', icon: 'power', label: tk('hostview.mPoweroff') },
  { id: 'logs', page: 'logs', right: 'logs', icon: 'logs', label: tk('hostview.mLogs') },
];

/**
 * Das Markup einer Host-Karte. o: { esc, connection(id) -> Verbindung | undefined, paused? }. Die Tasten tragen Attribute fuer beide Oberflaechen (data-act fuers Handy, data-hostinfo usw. fuer den Desktop):
 * data-hpage oeffnet die Seite des Hosts in diesem Abschnitt, data-hcact fuehrt eine Aktion ueber die Bestaetigung aus.
 */
export function hostCardHtml(h, entry, o) {
  const { esc } = o;
  const m = cardModel(h, entry);
  const id = esc(h.id);
  const hasInfo = wantsInfo(h);
  const page = (panel) => `data-act="hostinfo" data-host="${id}" data-hostinfo="${id}"${panel ? ` data-hpage="${esc(panel)}"` : ''}`;
  const btn = (attr, text, ico, tone) => `<button type="button" class="hu-btn ${tone}" data-host="${id}" ${attr}>${icon(ico)}<span>${esc(text)}</span></button>`;
  const agentBtn = (text, tone = 'tonal') => btn(page('agent'), text, 'download', tone);
  const nothing = m.kind === 'none'
    ? empty(t('hostview.noAgentTitle'), t('hostview.noAgentText'), esc, agentBtn(t('shell.agentSetup'), 'filled'), 'server')
    : !m.agentOnline ? noteP(t('hostview.agentOfflineText'), esc)
      : !hasInfo ? (hostSupport(h.agent) === 'none' ? noteP(t('hostview.noSupportText'), esc) : `<div class="hu-nodata">${noteP(t('hostview.noValuesText'), esc)}${agentBtn(t('hostview.agentUpdateSetup'))}</div>`) : '';
  const stat = m.meters.length ? `<div class="hu-meters">${m.meters.map((x) => meter({ ...x, label: x.short }, esc)).join('')}</div>` : (hasInfo && !m.loaded ? noteP(t('hostinfo.stLoading'), esc) : '');
  const flag = (f) => {
    const inner = `${icon(f.icon)}<span>${esc(f.text)}${f.detail ? ' · ' + esc(f.detail) : ''}</span>`;
    return f.panel ? `<button type="button" class="hu-chip btn ${f.tone}" ${page(f.panel)} title="${esc(f.id === 'updates' ? t('hostview.updatesOpen') : t('hostview.look'))}">${inner}</button>` : `<span class="hu-chip ${f.tone}">${inner}</span>`;
  };
  const flags = [...(m.uptime ? [`<span class="hu-chip">${icon('history')}<span>${esc(t('hostinfo.uptime', { v: m.uptime }))}</span></span>`] : []), ...m.flags.map(flag)];
  const svcs = h.services.map((s) => { const c = o.connection(s.connection); const st = serviceState(h, s.connection); return c ? `<button type="button" class="hu-chip btn" data-act="start" data-conn="${esc(c.id)}" title="${esc(c.host)}:${esc(c.port)} – ${esc(stateText(st))}"><i class="hu-dot ${dotClass(st)}"></i><span>${esc(c.name)}</span></button>` : ''; }).join('');
  const conns = h.services.length ? `<div class="hu-svcs"><span class="hu-sl">${esc(t('hostview.connections'))}</span>${svcs}</div>` : '';
  // Menue: erst die einfachen Aktionen (ausgegraut mit Grund und Weg zum Freischalten, wenn gesperrt), dann Agent und Bearbeiten
  const noUpdates = entry && entry.updates && entry.updates.updates && entry.updates.updates.count === 0;
  const whys = CARD_ACTIONS.map((a) => blockReason(h, a.right, !!o.paused) || (a.id === 'install' && noUpdates ? { why: 'none' } : null));
  // Sperrt dasselbe alles (kein Agent-Wert, Stufe aus, Pause), steht der Grund einmal oben statt unter jedem Eintrag
  const texts = whys.map((w) => (w ? (w.why === 'none' ? t('hostview.noUpdates') : whyText(w)) : ''));
  const shared = whys.filter((w) => w && w.why !== 'none').length >= 2 && new Set(whys.filter((w) => w && w.why !== 'none').map((w) => w.why)).size === 1 ? texts.find((x, i) => whys[i] && whys[i].why !== 'none') : '';
  const link = (w) => (w && w.link ? ` ${page(w.link)}` : '');
  const act = (a, i) => {
    const why = whys[i], label = esc(t(a.label));
    if (!why) return `<button type="button" role="menuitem" class="hu-mi" data-host="${id}" ${a.page ? page(a.page) : `data-act="hostaction" data-hcact="${esc(a.action)}" data-htarget="${esc(a.target || '')}"`}>${icon(a.icon)}<span class="hu-mt">${label}</span></button>`;
    const reason = why.why !== 'none' && shared ? '' : texts[i];
    return `<button type="button" role="menuitem" class="hu-mi dis" aria-disabled="true" data-host="${id}"${link(why)}>${icon(a.icon)}<span class="hu-mt">${label}${reason ? `<small>${esc(reason)}</small>` : ''}</span></button>`;
  };
  const item = (attr, text, ico) => `<button type="button" role="menuitem" class="hu-mi" data-host="${id}" ${attr}>${icon(ico)}<span class="hu-mt">${esc(text)}</span></button>`;
  const menu = `<span class="hu-menuw"><button type="button" class="hu-btn icon" data-hmenu aria-haspopup="menu" aria-expanded="false" aria-label="${esc(t('hostview.more'))}" title="${esc(t('hostview.more'))}">${icon('more')}</button>`
    + `<span class="hu-menu" role="menu" hidden>${m.kind === 'none' ? '' : (shared ? `<span class="hu-mnote">${esc(shared)}</span>` : '') + CARD_ACTIONS.map(act).join('') + '<span class="hu-msep" role="separator"></span>'}`
    + `${m.kind === 'none' ? '' : item(page('agent'), t('mobile.agentManage'), 'download')}${item(`data-act="hostedit" data-hostedit="${id}"`, t('hostview.rename'), 'edit')}</span></span>`;
  return `<article class="hu-card hu-host${h.online ? '' : ' off'}" data-host-card="${id}">
    <header class="hu-hh"><span class="hu-hi">${icon('server')}</span><div class="hu-ht"><div class="hu-hn" role="heading" aria-level="3">${esc(h.name)}</div><p>${esc(detail(h))}</p></div>
      <span class="hu-state ${h.online ? 'ok' : 'off'}"><i class="hu-dot ${dotClass(h.online)}"></i>${esc(stateText(h.online))}</span></header>
    <div class="hu-hk">${chip(agentLine(h), esc, '', m.kind === 'none' ? 'info' : 'terminal')}</div>
    ${nothing}${stat}${flags.length ? `<div class="hu-flags">${flags.join('')}</div>` : ''}${conns}
    <footer class="hu-hf">${btn(page(''), t('hostview.details'), 'info', 'tonal')}${menu}</footer></article>`;
}
const detail = (h) => hostDetail(h) || agentLine(h);

/* ---------- Menue der Karte (Nebenaktionen) ---------- */
const closeMenus = (except) => { for (const m of document.querySelectorAll('.hu-menu:not([hidden])')) if (m !== except) { m.hidden = true; const b = m.previousElementSibling; if (b) b.setAttribute('aria-expanded', 'false'); } };
/** Ist gerade ein Menue offen? Dann zeichnen die Ansichten nicht neu (das Menue wuerde zuklappen) */
export const menuOpen = () => typeof document !== 'undefined' && !!document.querySelector('.hu-menu:not([hidden])');
if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
  document.addEventListener('click', (e) => {
    const b = e.target.closest && e.target.closest('[data-hmenu]');
    const menu = b && b.nextElementSibling;
    closeMenus(menu);
    if (!menu) return;
    menu.hidden = !menu.hidden;
    b.setAttribute('aria-expanded', String(!menu.hidden));
    if (!menu.hidden) {
      // Richtung und Hoehe nach dem Platz im Fenster: ueber dem Knopf, sonst darunter; was nicht passt, scrollt im Menue
      const r = b.getBoundingClientRect(), pr = (b.closest('.dk-main, .m3-page') || document.documentElement).getBoundingClientRect();
      const above = r.top - Math.max(pr.top, 0) - 12, below = Math.min(pr.bottom, innerHeight - 100) - r.bottom - 12;
      const up = above >= 340 || above >= below;
      menu.classList.toggle('down', !up);
      menu.style.maxHeight = Math.max(160, Math.min(460, up ? above : below)) + 'px';
      const first = menu.querySelector('[role=menuitem]'); if (first) first.focus();
    }
  });
  document.addEventListener('keydown', (e) => {
    const menu = document.querySelector('.hu-menu:not([hidden])'); if (!menu) return;
    const items = [...menu.querySelectorAll('[role=menuitem]')], i = items.indexOf(document.activeElement);
    if (e.key === 'Escape') { e.stopPropagation(); const b = menu.previousElementSibling; closeMenus(); if (b) b.focus(); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); items[(i + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length].focus(); }
  }, true);
}

/* ---------- Abschnitte des Dialogs ---------- */
const note = (resp, esc) => { const n = stateNote(resp); return n ? noteP(n, esc) : ''; };
const PROXMOX = { ve: tk('hostinfo.pxVe'), pbs: tk('hostinfo.pxPbs'), pmg: tk('hostinfo.pxPmg') };
const DOCKER_REASON = { no_socket: tk('hostinfo.dockerNoSocket'), no_access: tk('hostinfo.dockerNoAccess'), error: tk('hostinfo.dockerError') };

/** Was Aufmerksamkeit braucht, quer ueber alle Antworten: [{ id, panel, tone, text, detail?, action? }] (panel: wohin "Ansehen" fuehrt) */
export function attention(data, host) {
  const out = [];
  const i = data.status && data.status.info;
  const u = data.updates && data.updates.updates;
  if (u && (u.broken || []).length) out.push({ id: 'broken', panel: 'updates', tone: 'bad', text: tp('hostview.brokenPackages', u.broken.length), detail: u.broken.join(', ') });
  if (u && u.count > 0) out.push({ id: 'updates', panel: 'updates', tone: u.security > 0 ? 'bad' : 'warn', text: tp('hostview.flagUpdates', u.count), detail: u.securityKnown && u.security > 0 ? t('hostview.flagSecurity', { n: u.security }) : '' });
  if (i && i.rebootRequired) out.push({ id: 'reboot', panel: 'actions', tone: 'warn', text: t('hostinfo.rebootNeeded'), detail: i.newKernel ? t('hostinfo.rebootKernel', { v: i.newKernel }) : i.rebootPackages && i.rebootPackages.length ? i.rebootPackages.join(', ') : '' });
  const s = data.services && data.services.services;
  if (s && s.count > 0) out.push({ id: 'services', panel: 'services', tone: 'bad', text: tp('hostinfo.failedUnits', s.count) });
  const d = data.docker && data.docker.docker;
  if (d && d.available && d.restarting > 0) out.push({ id: 'docker', panel: 'docker', tone: 'warn', text: tp('hostinfo.dockerRestarting', d.restarting) });
  for (const x of healthIssues(i)) out.push({ id: 'health', panel: 'disks', tone: 'bad', text: x });
  if (i) {
    const full = (i.disks || []).filter((x) => x.percent >= 90);
    if (full.length) out.push({ id: 'full', panel: 'disks', tone: 'warn', text: t('hostview.diskFull', { list: full.map((x) => `${x.mount} (${fmtPercent(x.percent)})`).join(', ') }) });
    const ram = ramPercent(i.memory);
    if (ram !== null && ram >= 90) out.push({ id: 'ram', panel: 'load', tone: 'warn', text: t('hostview.ramHigh', { v: fmtPercent(ram) }) });
    if (i.cpuCount && i.load && i.load[0] > i.cpuCount * 1.5) out.push({ id: 'cpu', panel: 'load', tone: 'warn', text: t('hostview.cpuHigh', { v: fmtNumber(i.load[0], 2) }) });
  }
  return out;
}

/** Ueberblick: was Aufmerksamkeit braucht, die drei Kennzahlen, der Rechner */
function overviewPanel(c) {
  const { esc, data, host } = c;
  const resp = data.status, i = resp && resp.info;
  if (!i) return note(resp, esc);
  const virt = i.virt && i.virt.kind !== 'none' ? t(i.virt.kind === 'container' ? tk('hostinfo.virtContainer') : tk('hostinfo.virtVm'), { name: i.virt.name || '' }) : '';
  const prox = i.proxmox ? t('hostinfo.proxmox', { product: t(PROXMOX[i.proxmox.product] || 'hostinfo.pxVe'), version: i.proxmox.version || '' }).trim() : '';
  const att = attention(data, host);
  const attHtml = att.length
    ? card(t('hostview.attention'), `<ul class="hu-att">${att.map((a) => `<li class="${a.tone}">${icon(a.tone === 'bad' ? 'error' : 'warn')}<div><b>${esc(a.text)}</b>${a.detail ? `<span>${esc(a.detail)}</span>` : ''}</div>${button(t('hostview.look'), esc, { tone: 'text', sm: true, data: { goto: a.panel } })}</li>`).join('')}</ul>`, esc, { icon: 'warn', cls: 'wide' })
    : callout(t('hostview.allGood'), esc, 'ok', data.updates || data.services ? '' : t('hostview.allGoodLimited'));
  const glance = card(t('hostview.glance'), `<div class="hu-meters flat">${meters(i).map((x) => meter(x, esc)).join('')}</div>`, esc, { icon: 'dashboard' });
  const machine = card(t('hostview.machine'), dl([
    [t('hostinfo.os'), [i.os, i.arch].filter(Boolean).join(' · ')],
    [t('hostinfo.kernel'), i.kernel || ''],
    [t('hostinfo.hostname'), i.hostname || ''],
    [t('hostinfo.virt'), [virt, prox].filter(Boolean).join(' · ')],
    [t('hostinfo.uptimeLabel'), fmtUptime(i.uptimeSeconds)],
    [t('hostinfo.reboot'), i.rebootRequired ? t('hostinfo.rebootYes') + (i.newKernel ? ' · ' + t('hostinfo.rebootKernel', { v: i.newKernel }) : '') : t('hostinfo.rebootNo'), i.rebootRequired ? 'warn' : ''],
  ], esc), esc, { icon: 'server' });
  return note(resp, esc) + `<div class="hu-cols">${attHtml}${glance}${machine}</div>`;
}

/** Auslastung: Last (1, 5, 15 min), Arbeitsspeicher, Auslagerung, je mit Balken */
function loadPanel(c) {
  const { esc, data } = c;
  const resp = data.status, i = resp && resp.info;
  if (!i) return note(resp, esc);
  const cores = i.cpuCount, load = i.load || [];
  const cpu = [t('hostview.load1'), t('hostview.load5'), t('hostview.load15')].map((label, k) => (Number.isFinite(load[k]) ? { label, value: fmtNumber(load[k], 2), pct: loadPct(load[k], cores), warnAt: 100, badAt: 150, warn: !!(cores && load[k] > cores * 1.5) } : null)).filter(Boolean);
  const mem = i.memory || {}, ram = ramPercent(mem);
  const swapUsed = mem.swapTotalBytes > 0 ? mem.swapTotalBytes - mem.swapFreeBytes : 0;
  const swapPct = mem.swapTotalBytes > 0 ? Math.round((swapUsed / mem.swapTotalBytes) * 100) : null;
  const cpuCard = card(t('hostview.cpu'), (cores ? noteP(t('hostview.cpuNote', { n: cores }), esc) : '') + `<div class="hu-meters flat">${cpu.map((x) => meter(x, esc)).join('')}</div>`, esc, { icon: 'speed' });
  const memCard = card(t('hostinfo.memory'), (mem.totalBytes ? meter({ label: t('hostview.ram'), value: t('hostinfo.ofTotal', { used: fmtBytes(mem.totalBytes - mem.availableBytes), total: fmtBytes(mem.totalBytes) }), pct: ram, warn: ram >= 90, sub: ram !== null ? fmtPercent(ram) : '' }, esc) : '')
    + (swapPct !== null ? meter({ label: t('hostinfo.swap'), value: t('hostinfo.ofTotal', { used: fmtBytes(swapUsed), total: fmtBytes(mem.swapTotalBytes) }), pct: swapPct, warnAt: 50, badAt: 80, sub: fmtPercent(swapPct) }, esc) : noteP(t('hostview.noSwap'), esc))
    + noteP(t('hostview.ramNote'), esc), esc, { icon: 'memory' });
  return note(resp, esc) + `<div class="hu-cols">${cpuCard}${memCard}</div>`;
}

/** Datentraeger: je Einhaengepunkt ein Balken, dann die Gesundheit der Platten mit klarer Aussage */
function disksPanel(c) {
  const { esc, data } = c;
  const resp = data.status, i = resp && resp.info;
  if (!i) return note(resp, esc);
  const rows = (i.disks || []).map((d) => `<div class="hu-disk">${meter({ label: d.mount, value: fmtPercent(d.percent), pct: d.percent, warn: d.percent >= 90, sub: `${t('hostinfo.ofTotal', { used: fmtBytes(d.usedBytes), total: fmtBytes(d.totalBytes) })}${d.fs ? ' · ' + d.fs : ''}` }, esc)}</div>`).join('');
  return note(resp, esc) + `<div class="hu-cols">${card(t('hostinfo.disks'), rows || noteP(t('hostview.noDisks'), esc), esc, { icon: 'disk' })}${healthCard(i.health, i, esc)}</div>`;
}

/** Gesundheit der Platten: erst die Aussage (in Ordnung, Problem, nicht lesbar), dann SMART je Platte, RAID, ZFS */
export function healthCard(h, info, esc) {
  const title = t('hostinfo.health');
  if (!h) return card(title, noteP(t('hostview.healthNone'), esc), esc, { icon: 'shield' });
  if (h.pending) return card(title, noteP(t('hostinfo.stLoading'), esc), esc, { icon: 'shield' });
  const rows = [];
  const sm = h.smart;
  if (sm && sm.available) rows.push([t('hostinfo.smart'), (sm.devices || []).map((d) => `${d.name}: ${t(SMART_STATE[d.health] || SMART_STATE.unknown)}${Number.isFinite(d.temp) ? ` · ${d.temp} °C` : ''}`).join('\n'), (sm.devices || []).some((d) => d.health === 'failing') ? 'warn' : '']);
  else if (sm) rows.push([t('hostinfo.smart'), t(SMART_WHY[sm.reason] || SMART_WHY.no_access)]);
  if ((h.md || []).length) rows.push([t('hostinfo.raid'), h.md.map((a) => `${a.name}${a.level ? ' (' + a.level + ')' : ''}: ${t(MD_STATE[a.state] || MD_STATE.inactive)} · ${a.active}/${a.total}${a.syncPercent != null ? ' · ' + fmtNumber(a.syncPercent, 1) + ' %' : ''}`).join('\n'), h.md.some((a) => a.state !== 'ok') ? 'warn' : '']);
  const zs = zfsState(h.zfs);
  if (zs === 'healthy') rows.push([t('hostinfo.zfs'), t('hostinfo.zfsOk')]);
  else if (zs === 'problem') rows.push([t('hostinfo.zfs'), zfsLines(h.zfs).join('\n'), 'warn']);
  else if (zs === 'unknown') rows.push([t('hostinfo.zfs'), t('hostinfo.zfsUnreadable', { why: h.zfs.message || t('hostinfo.zfsNoMessage') })]);       // nicht lesbar: ehrlich sagen, aber kein Problem melden
  // zs === 'none': keine Pools, es gibt nichts zu zeigen
  const issues = healthIssues(info);
  const readable = (sm && sm.available) || (h.md && h.md.length) || zs === 'healthy' || zs === 'problem';
  const head = issues.length ? callout(t('hostview.healthProblem'), esc, 'bad', issues.join(' · ')) : readable ? callout(t('hostview.healthOk'), esc, 'ok') : callout(t('hostview.healthUnread'), esc, 'info');
  return card(title, head + dl(rows, esc), esc, { icon: 'shield' });
}

/* ---------- Netzwerk ---------- */
const VIRTUAL_IFACE = /^(docker\d*|br-|veth|virbr|cni|flannel|cali|tun\d|tap\d|vnet|lxcbr|lxdbr|podman|kube|cilium|vmbr\d+v)/;
/** Schnittstellen aus Adressen und Zaehlern: [{ iface, v4: [], v6: [], rx, tx, virtual }], echte zuerst; Docker- und Bridge-Schnittstellen sind "virtual" */
export function interfaces(info) {
  const map = new Map();
  const get = (name) => { if (!map.has(name)) map.set(name, { iface: name, v4: [], v6: [], rx: null, tx: null, virtual: VIRTUAL_IFACE.test(name) }); return map.get(name); };
  for (const a of (info && info.addresses) || []) {
    const e = get(a.iface || '?');
    (a.family === 4 ? e.v4 : e.v6).push(a.family === 4 ? `${a.address}/${a.prefix}` : a.address);
  }
  for (const n of (info && info.network) || []) { const e = get(n.iface); e.rx = n.rxBytes; e.tx = n.txBytes; }
  return [...map.values()].sort((a, b) => Number(a.virtual) - Number(b.virtual) || (b.v4.length > 0) - (a.v4.length > 0) || a.iface.localeCompare(b.iface));
}
const ifaceRow = (e, esc) => {
  const addrs = [...e.v4, ...e.v6.filter((x) => !/^fe80:/i.test(x))];
  const ll = e.v6.length - e.v6.filter((x) => !/^fe80:/i.test(x)).length;
  const traffic = e.rx !== null ? t('hostview.traffic', { rx: fmtBytes(e.rx), tx: fmtBytes(e.tx) }) : '';
  return `<li><div class="hu-lt"><span class="hu-ln">${esc(e.iface)}</span></div>${addrs.length ? `<span class="hu-ld mono">${addrs.map(esc).join('<br>')}</span>` : `<span class="hu-ld">${esc(t('hostview.noAddress'))}</span>`}${ll ? `<span class="hu-ld">${esc(t('hostview.linkLocal', { n: ll }))}</span>` : ''}${traffic ? `<span class="hu-ld">${esc(traffic)}</span>` : ''}</li>`;
};
function networkPanel(c) {
  const { esc, data, rate } = c;
  const resp = data.status, i = resp && resp.info;
  if (!i) return note(resp, esc);
  const all = interfaces(i), real = all.filter((e) => !e.virtual), virt = all.filter((e) => e.virtual);
  const head = rate ? `<p class="hu-note">${esc(rateText(rate))}</p>` : '';
  const main = card(t('hostview.interfaces'), head + (real.length ? `<ul class="hu-list">${real.map((e) => ifaceRow(e, esc)).join('')}</ul>` : noteP(t('hostview.noInterfaces'), esc)), esc, { icon: 'network' });
  const fold = virt.length ? `<details class="hu-fold"><summary>${icon('chevron', 'chev')}<span>${esc(tp('hostview.virtualIfaces', virt.length))}</span></summary><ul class="hu-list">${virt.map((e) => ifaceRow(e, esc)).join('')}</ul></details>` : '';
  return note(resp, esc) + main + fold + noteP(t('hostview.trafficNote'), esc);
}

/* ---------- Updates, Dienste, Docker ---------- */
function updatesPanel(c) {
  const { esc, data, act } = c;
  const resp = data.updates, u = resp && resp.updates;
  if (!u) return note(resp, esc);
  const canRefresh = !!(act && act.can('updates'));
  const security = (u.packages || []).filter((p) => p.security), rest = (u.packages || []).filter((p) => !p.security);
  const row = (p) => ({ name: p.name, detail: p.from ? `${p.from} → ${p.to}` : t('hostinfo.newPackage', { v: p.to }), badge: p.security ? t('hostinfo.security') : '', actions: act && !u.proxmox ? act.bar(act.btn('updates.install', p.name, t('hostact.bInstall'))) : '' });          // Proxmox nur als Ganzes: kein Knopf je Paket
  const broken = (u.broken || []).length
    ? callout(tp('hostview.brokenPackages', u.broken.length), esc, 'bad', u.broken.join(', ')) + (u.unreadable ? noteP(t('hostview.brokenUnreadable'), esc) : '')
    : '';
  const head = u.unreadable ? '' : u.count > 0
    ? callout(tp('hostview.updatesCount', u.count), esc, u.security > 0 ? 'bad' : 'warn', u.securityKnown && u.security > 0 ? t('hostview.flagSecurity', { n: u.security }) : '')
    : callout(t('hostinfo.updatesNone'), esc, 'ok');
  const bar0 = canRefresh ? act.bar(act.btn('updates.refresh', '', t('hostact.a.updatesRefresh')) + (u.count > 0 ? act.btn('updates.install', 'all', t('hostact.bInstallAll')) : '') + act.btn('updates.repair', '', t('hostact.bRepair'))) : '';
  const extra = [];
  if (u.proxmox) extra.push(callout(t('hostview.proxmoxWhole'), esc, 'info'));
  if (u.keptBack && u.keptBack.length) extra.push(noteP(t('hostinfo.keptBack', { list: u.keptBack.join(', ') }), esc));
  if (u.held && u.held.length) extra.push(noteP(t('hostinfo.held', { list: u.held.join(', ') }), esc));
  if (u.removals > 0) extra.push(noteP(tp('hostinfo.removals', u.removals), esc));
  if (u.truncated) extra.push(noteP(t('hostinfo.truncated', { n: (u.packages || []).length }), esc));
  // Der Hinweis auf "sudo apt update" nur, wenn der Knopf fehlt (Stufe aus, Wort fehlt, Pause); sonst reicht das Alter der Listen
  const age = u.listsAgeSeconds != null ? noteP(`${t('hostinfo.listsAge', { age: fmtAge(u.listsAgeSeconds) })}${canRefresh ? '' : ' ' + t('hostinfo.listsHint')}`, esc, u.listsAgeSeconds > 7 * 86400) : (canRefresh ? '' : noteP(t('hostinfo.listsHint'), esc));
  const lists = (security.length ? subH(t('hostview.securityUpdates'), esc) + listBlock(security.map(row), esc) : '') + (rest.length ? (security.length ? subH(t('hostview.otherUpdates'), esc) : '') + listBlock(rest.map(row), esc) : '');
  return note(resp, esc) + broken + head + bar0 + (lists ? card('', lists, esc) : '') + extra.join('') + age;
}

function servicesPanel(c) {
  const { esc, data, act } = c;
  const resp = data.services, s = resp && resp.services;
  if (!s) return note(resp, esc);
  const items = (s.failed || []).map((f) => ({ name: f.unit, detail: f.description || '', actions: act && act.can('services') ? act.bar(act.btn('service.restart', f.unit, t('hostact.bRestart')) + act.btn('logs.unit', f.unit, t('hostact.bLogs'))) : '' }));
  const all = act && act.can('services') ? act.bar(button(t('hostact.manageUnits'), esc, { tone: 'tonal', sm: true, data: { 'hact-open': 'units' } })) + '<div class="hi-units"></div>' : '';
  return note(resp, esc) + (s.count > 0 ? callout(tp('hostinfo.failedUnits', s.count), esc, 'bad') + card('', listBlock(items, esc), esc) : callout(t('hostinfo.noFailedUnits'), esc, 'ok')) + all;
}

/** Aktionstasten eines Containers: laufend (Neustart, Stopp), sonst Start; dazu Logs und das Image neu holen */
function containerActions(ct, act) {
  const run = ct.state === 'running' || ct.state === 'restarting';
  return act.bar((run ? act.btn('docker.restart', ct.id, t('hostact.bRestart')) + act.btn('docker.stop', ct.id, t('hostact.bStop')) : act.btn('docker.start', ct.id, t('hostact.bStart')))
    + act.btn('logs.container', ct.id, t('hostact.bLogs')) + act.btn('docker.pull', ct.id, t('hostact.bPull')));
}
function dockerPanel(c) {
  const { esc, data, act } = c;
  const resp = data.docker, d = resp && resp.docker;
  if (!d) return note(resp, esc);
  if (!d.available) return note(resp, esc) + callout(t(DOCKER_REASON[d.reason] || DOCKER_REASON.error), esc, 'info');
  const items = (d.containers || []).map((ct) => ({ name: ct.name, detail: `${ct.image} · ${ct.status || ct.state}`, off: ct.state !== 'running', badge: ct.state === 'restarting' ? { text: ct.state, tone: 'warn' } : '', actions: act && act.can('docker') ? containerActions(ct, act) : '' }));
  return note(resp, esc) + callout(t('hostinfo.dockerSum', { running: d.running, total: d.total }), esc, d.restarting > 0 ? 'warn' : 'info', d.restarting > 0 ? tp('hostinfo.dockerRestarting', d.restarting) : '')
    + (items.length ? card('', listBlock(items, esc), esc) : '') + (d.truncated ? noteP(t('hostinfo.truncated', { n: d.containers.length }), esc) : '');
}

/* ---------- Logs, Aktionen, Verlauf ---------- */
export const LOG_LINES = { min: 10, max: 200, def: 50 };
/** Zeilenzahl aus dem Eingabefeld: ganze Zahl zwischen 10 und 200 (alles andere wird der Standard bzw. begrenzt) */
export const clampLines = (v) => { const n = Math.round(Number(v)); return Number.isFinite(n) ? Math.max(LOG_LINES.min, Math.min(LOG_LINES.max, n)) : LOG_LINES.def; };

/**
 * Logs eines Dienstes: die Auswahl kommt aus der Einheitenliste des Agenten (GET /units), nie aus freier Eingabe; danach laeuft der gewohnte bestaetigte Ablauf.
 * Die Liste liest der Agent nur mit dem Wort services (und Stufe "alles"): ohne das gibt es keine Auswahl, die Logs der fehlgeschlagenen Dienste und der Container bleiben erreichbar.
 */
function logsPanel(c) {
  const { esc, act, units } = c;
  const canLogs = act && act.can('logs'), canList = act && act.can('services');
  if (!canLogs) return callout(t('hostview.logsOff'), esc, 'info', t('hostview.logsOffText'));
  if (!canList) return callout(t('hostview.logsNoList'), esc, 'info', t('hostview.logsNoListText'));
  const u = units && units.units, list = u && u.units;
  const opts = list ? list.map((u) => `<option value="${esc(u.unit)}">${esc(u.unit)} · ${esc(u.active)}</option>`).join('') : `<option value="">${esc(units && units.error ? errText(units.error) : t('hostinfo.stLoading'))}</option>`;
  return card(t('hostview.logsTitle'), noteP(t('hostview.logsIntro'), esc)
    + `<label class="hu-field"><span>${esc(t('hostview.logsUnit'))}</span><select id="hostLogUnit"${list && list.length ? '' : ' disabled'}>${opts}</select></label>`
    + `<label class="hu-field"><span>${esc(t('hostview.logsLines'))}</span><input id="hostLogLines" type="number" inputmode="numeric" min="${LOG_LINES.min}" max="${LOG_LINES.max}" step="10" value="${LOG_LINES.def}"></label>`
    + (list && list.length ? act.bar(button(t('hostview.logsRead'), esc, { tone: 'filled', icon: 'logs', data: { hlogs: '1' } })) : '')
    + (u && u.truncated ? noteP(t('hostinfo.truncated', { n: list.length }), esc) : ''), esc, { icon: 'logs' });
}

/** Aktionen: Stufe, Pause, Erlaubnis auf dem Rechner, dazu Neustart und Ausschalten */
function actionsPanel(c) {
  const { esc, host, act, paused } = c;
  const power = act && act.can('reboot')
    ? card(t('hostview.power'), noteP(t('hostview.powerText'), esc) + act.bar(act.btn('power.reboot', '', t('hostact.a.reboot')) + act.btn('power.poweroff', '', t('hostact.a.poweroff'))), esc, { icon: 'power' })
    : '';
  return `<div class="hu-cols">${controlHtml(host, paused, esc)}${power}</div>`;
}

function historyPanel(c) {
  const { esc, history } = c;
  const body = history ? auditHtml(history.resp, esc) : noteP(t('hostinfo.stLoading'), esc);
  return card(t('hostact.secHistory'), body, esc, { icon: 'history', aside: button(t('hostinfo.refresh'), esc, { tone: 'text', sm: true, data: { 'hact-open': 'history' } }) });
}

/**
 * Die Abschnitte in der Reihenfolge der Navigation. kind: woher die Daten kommen (Antwort des Servers); cap: was der Agent melden muss; needs: 'actions' = nur fuer Host-Agenten
 * ab Stufe 3 (hasActionAgent). badge(data) -> { text, tone } | null fuer den Zaehler an der Navigation.
 */
export const PANELS = [
  { id: 'overview', icon: 'dashboard', title: tk('hostview.pOverview'), kind: 'status', cap: 'sysinfo', html: overviewPanel, badge: (d, h) => { const n = attention(d, h).length; return n ? { text: String(n), tone: 'warn' } : null; } },
  { id: 'load', icon: 'speed', title: tk('hostview.pLoad'), kind: 'status', cap: 'sysinfo', html: loadPanel },
  { id: 'disks', icon: 'disk', title: tk('hostview.pDisks'), kind: 'status', cap: 'sysinfo', html: disksPanel, badge: (d) => { const i = d.status && d.status.info; return i && (healthIssues(i).length || (i.disks || []).some((x) => x.percent >= 90)) ? { text: '!', tone: 'bad' } : null; } },
  { id: 'network', icon: 'network', title: tk('hostview.pNetwork'), kind: 'status', cap: 'sysinfo', html: networkPanel },
  { id: 'updates', icon: 'update', title: tk('hostview.pUpdates'), kind: 'updates', cap: 'updates', html: updatesPanel, badge: (d) => { const u = d.updates && d.updates.updates; return u && u.count > 0 ? { text: String(u.count), tone: u.security > 0 ? 'bad' : 'warn' } : null; } },
  { id: 'services', icon: 'gear', title: tk('hostview.pServices'), kind: 'services', cap: 'services', html: servicesPanel, badge: (d) => { const s = d.services && d.services.services; return s && s.count > 0 ? { text: String(s.count), tone: 'bad' } : null; } },
  { id: 'docker', icon: 'box', title: tk('hostview.pDocker'), kind: 'docker', cap: 'docker', html: dockerPanel, badge: (d) => { const x = d.docker && d.docker.docker; return x && x.available && x.restarting > 0 ? { text: String(x.restarting), tone: 'warn' } : null; } },
  { id: 'logs', icon: 'logs', title: tk('hostview.pLogs'), needs: 'actions', html: logsPanel },
  { id: 'actions', icon: 'tune', title: tk('hostview.pActions'), needs: 'actions', html: actionsPanel },
  { id: 'history', icon: 'history', title: tk('hostview.pHistory'), needs: 'actions', html: historyPanel },
  { id: 'login', icon: 'lock', title: tk('hostview.pLogin'), html: (c) => loginPanel(c) },
  { id: 'agent', icon: 'terminal', title: tk('hostview.pAgent'), html: (c) => agentPanel(c) },
];

/** Jeder Host zeigt dieselben Abschnitte; was der Agent nicht liefert, erklaert der Abschnitt selbst (hostpage.js, unavailable) */
export const panelsFor = () => PANELS;

/**
 * Warum ein Abschnitt fuer diesen Host leer bleibt, als fertiges HTML mit dem naechsten Schritt; '' wenn er Daten haben kann. Massgeblich sind die Faehigkeiten (caps), die der Agent im hello nennt,
 * nicht der Name der Plattform. Die Taste "Agent aktualisieren / einrichten" traegt data-hgoto="agent" (hostpage.js wechselt zum Abschnitt Agent).
 */
export function unavailable(host, p, esc) {
  if (p.id === 'agent' || p.id === 'login') return '';   // der Abschnitt Agent ist der Weg aus jedem leeren Zustand; der Login braucht keinen Agenten
  const a = host.agent;
  const setup = button(t('hostview.agentUpdateSetup'), esc, { tone: 'filled', icon: 'download', data: { hgoto: 'agent' } });
  if (!a) return empty(t('hostview.noAgentTitle'), t('hostview.noAgentText'), esc, button(t('shell.agentSetup'), esc, { tone: 'filled', icon: 'download', data: { hgoto: 'agent' } }), 'server');
  if (!a.online) return empty(t('hostview.agentOfflineTitle'), t('hostview.agentOfflineText'), esc, button(t('hostview.manageAgent'), esc, { tone: 'tonal', icon: 'download', data: { hgoto: 'agent' } }), 'server');
  const support = hostSupport(a);
  const has = support === 'yes' && (p.cap ? (a.caps || []).includes(p.cap) : p.needs === 'actions' ? hasActionAgent(host) : true);
  if (has) return '';
  // none: der Agent ist neu genug, meldet aber nichts davon (kein Linux): kein Weg ueber eine Aktualisierung
  if (support === 'none') return empty(t('hostview.noValuesTitle'), t('hostview.noSupportText'), esc, '', 'server');
  return empty(t('hostview.noValuesTitle'), t('hostview.noValuesText'), esc, setup, 'server');
}
