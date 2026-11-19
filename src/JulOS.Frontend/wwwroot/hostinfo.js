/* =====================================================================
 * Host-Agent: Systemwerte, Updates, Dienste, Docker (docs/HOST-AGENT.md). Reine Hilfen ohne DOM, damit Desktop-Oberflaeche, Handy-Huelle und der
 * Dialog "Host-Details" dasselbe zeigen und Node sie testen kann.
 *  - Formatierer und Zusammenfassungszeilen (Text, noch nicht fuer HTML geschuetzt)
 *  - HTML-Bausteine (Strings; jeder Wert vom Agenten geht durch esc, denn der Agent ist nicht der Server)
 *  - der Speicher im Browser (ensure/load): fragt nur, wenn eine Ansicht es verlangt, und hoechstens alle 55 s je Host
 *  - der Einzeiler fuer die SSH-Installation und die Wahl der Terminal-Verbindung
 * Server-Antworten: GET /api/hosts/{id}/status|updates|services|docker = { state, at, stale, error?, info|updates|services|docker? }
 * ===================================================================== */
import { t, tp, tk, fmtNumber, fmtDate } from './i18n.js';
import { programOf } from './terminal-programs.js';

/* ---------- Formatierer ---------- */
export const fmtBytes = (b) => {
  b = Number(b) || 0;
  return b >= 1e12 ? fmtNumber(b / 1e12, 1) + ' TB' : b >= 1e9 ? fmtNumber(b / 1e9, 1) + ' GB' : b >= 1e6 ? fmtNumber(b / 1e6) + ' MB' : fmtNumber(b / 1e3) + ' kB';
};

/** Laufzeit: "3 d 4 h", "5 h 12 min", "7 min" (die Einheiten sind in beiden Sprachen gleich, daher je ein Schluessel mit Zahlen). */
export function fmtUptime(sec) {
  if (!Number.isFinite(sec) || sec < 0) return '';
  const d = Math.floor(sec / 86400), h = Math.floor((sec % 86400) / 3600), m = Math.floor((sec % 3600) / 60);
  return d > 0 ? t('hostinfo.uptimeDH', { d, h }) : h > 0 ? t('hostinfo.uptimeHM', { h, m }) : t('hostinfo.uptimeM', { m });
}

/** Alter in Minuten, Stunden oder Tagen ("12 min", "5 h", "3 d") */
export function fmtAge(sec) {
  if (!Number.isFinite(sec) || sec < 0) return '';
  return sec < 7200 ? t('hostinfo.ageMin', { n: Math.max(1, Math.round(sec / 60)) }) : sec < 172800 ? t('hostinfo.ageHour', { n: Math.round(sec / 3600) }) : t('hostinfo.ageDay', { n: Math.round(sec / 86400) });
}

export const fmtPercent = (p) => fmtNumber(Math.round(p)) + ' %';
export const loadText = (load) => (Array.isArray(load) && load.length ? load.map((x) => fmtNumber(x, 2)).join(' · ') : '');
export const when = (iso) => fmtDate(iso, { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });

/** Belegter Arbeitsspeicher in Prozent (ohne Zwischenspeicher: gesamt minus verfuegbar); null ohne Angabe */
export const ramPercent = (m) => (m && m.totalBytes > 0 ? Math.round(((m.totalBytes - m.availableBytes) / m.totalBytes) * 100) : null);

/** Die Platte, die auf der Karte steht: "/" , sonst die voelligste */
export function mainDisk(disks) {
  if (!Array.isArray(disks) || !disks.length) return null;
  return disks.find((d) => d.mount === '/') || [...disks].sort((a, b) => b.percent - a.percent)[0];
}

/* ---------- Gesundheit der Platten und Netzwerk ---------- */
const MD_STATE = { ok: tk('hostinfo.mdOk'), degraded: tk('hostinfo.mdDegraded'), recovering: tk('hostinfo.mdRecovering'), inactive: tk('hostinfo.mdInactive') };
const SMART_STATE = { ok: tk('hostinfo.smartOk'), failing: tk('hostinfo.smartFailing'), unknown: tk('hostinfo.smartUnknown') };
const SMART_WHY = { no_smartctl: tk('hostinfo.smartNoTool'), no_access: tk('hostinfo.smartNoAccess'), no_devices: tk('hostinfo.smartNoDevices') };

/** Probleme der Platten als Texte: SMART "failing", RAID nicht "ok", ZFS nicht gesund. Leer = nichts Auffaelliges (oder nichts lesbar: das steht dann im Dialog). */
export function healthIssues(info) {
  const h = info && info.health;
  if (!h || h.pending) return [];
  const out = [];
  for (const d of (h.smart && h.smart.devices) || []) if (d.health === 'failing') out.push(t('hostinfo.issueSmart', { name: d.name }));
  for (const a of h.md || []) if (a.state !== 'ok') out.push(t('hostinfo.issueMd', { name: a.name, state: t(MD_STATE[a.state] || MD_STATE.inactive) }));
  if (h.zfs && !h.zfs.healthy) out.push(...(h.zfs.pools.length ? h.zfs.pools.map((p) => t('hostinfo.issueZfs', { name: p.name, state: p.state })) : [t('hostinfo.issueZfs', { name: '', state: '?' }).trim()]));
  return out;
}
/** Eine Zeile fuer die Karte: Problem (warn) oder "Platten: in Ordnung", sobald irgendetwas lesbar war; sonst '' (nichts behaupten). */
export function healthPart(info) {
  const h = info && info.health;
  if (!h || h.pending) return null;
  const issues = healthIssues(info);
  if (issues.length) return { id: 'health', text: t('hostinfo.healthProblem', { list: issues.join(', ') }), warn: true };
  const readable = (h.smart && h.smart.available) || (h.md && h.md.length) || h.zfs;
  return readable ? { id: 'health', text: t('hostinfo.healthOk'), warn: false } : null;
}

/** Summe der Zaehler aller Schnittstellen (Bytes seit dem Start) */
const netSum = (info) => (info && Array.isArray(info.network) ? info.network.reduce((a, n) => ({ rx: a.rx + n.rxBytes, tx: a.tx + n.txBytes }), { rx: 0, tx: 0 }) : null);
/** Rate in Bytes/s zwischen zwei Ablesungen (Antworten von /status); null ohne zwei Werte, bei Zaehlerrueckgang (Neustart) oder zu kurzem Abstand */
export function netRate(prev, cur) {
  const a = netSum(prev && prev.info), b = netSum(cur && cur.info);
  const dt = prev && cur && prev.at && cur.at ? (Date.parse(cur.at) - Date.parse(prev.at)) / 1000 : 0;
  if (!a || !b || !(dt >= 5) || b.rx < a.rx || b.tx < a.tx) return null;
  return { rx: (b.rx - a.rx) / dt, tx: (b.tx - a.tx) / dt };
}
const rateText = (r) => t('hostinfo.net', { rx: fmtBytes(r.rx) + '/s', tx: fmtBytes(r.tx) + '/s' });

/* ---------- Zusammenfassungen ---------- */
/** Systemwerte als Teile { id, text, warn }: Last, RAM, Platte, Laufzeit, "Neustart noetig". warn bei fast vollem Speicher/Platte und bei Neustart. */
export function systemParts(info) {
  if (!info) return [];
  const out = [];
  const load = loadText(info.load && info.load.slice(0, 1));
  if (load) out.push({ id: 'load', text: t('hostinfo.load', { v: load }), warn: !!(info.cpuCount && info.load[0] > info.cpuCount * 1.5) });
  const ram = ramPercent(info.memory);
  if (ram !== null) out.push({ id: 'ram', text: t('hostinfo.ram', { v: fmtPercent(ram) }), warn: ram >= 90 });
  const disk = mainDisk(info.disks);
  if (disk) out.push({ id: 'disk', text: t('hostinfo.disk', { v: fmtPercent(disk.percent) }), warn: disk.percent >= 90 });
  const up = fmtUptime(info.uptimeSeconds);
  if (up) out.push({ id: 'uptime', text: t('hostinfo.uptime', { v: up }), warn: false });
  if (info.rebootRequired) out.push({ id: 'reboot', text: t('hostinfo.rebootNeeded'), warn: true });
  return out;
}
export const systemLine = (info) => systemParts(info).map((p) => p.text).join(' · ');

/** "Updates: 3 verfuegbar (1 Sicherheit)", "Updates: keine", sonst '' (keine Daten: nichts behaupten). resp ist die Antwort von /updates. */
export function updatesLine(resp) {
  const u = resp && resp.updates;
  if (!u) return '';
  if (!u.count) return t('hostinfo.updatesNone');
  return u.securityKnown && u.security > 0 ? t('hostinfo.updatesCountSec', { n: u.count, s: u.security }) : t('hostinfo.updatesCount', { n: u.count });
}

const CAP_LABEL = { sysinfo: tk('hostinfo.capSysinfo'), updates: tk('hostinfo.capUpdates'), services: tk('hostinfo.capServices'), docker: tk('hostinfo.capDocker') };
/** "Host-Agent" oder "Desktop-Agent"; '' ohne Agent */
export const agentModeText = (h) => (!h || !h.agent ? '' : h.agent.kind === 'host' ? t('hostinfo.modeHost') : t('hostinfo.modeDesktop'));
/** Faehigkeiten, soweit gemeldet ("Systemwerte, Updates, Dienste, Docker"); der Server nennt sie nur fuer verbundene Agenten */
export const capsText = (h) => ((h && h.agent && Array.isArray(h.agent.caps) ? h.agent.caps : []).filter((c) => CAP_LABEL[c]).map((c) => t(CAP_LABEL[c])).join(', '));
/** Eine Zeile zum Agenten: Modus und Faehigkeiten (der Satz zu Verbindung und Version steht in hosts.js agentSummary) */
export function agentDetail(h) {
  const mode = agentModeText(h), caps = capsText(h);
  return [mode, caps && t('hostinfo.caps', { list: caps })].filter(Boolean).join(' · ');
}

/** Hat der Server fuer diesen Host etwas zu lesen? Nur ein verbundener Agent mit gemeldeten Faehigkeiten. */
export const wantsInfo = (h) => !!(h && h.agent && h.agent.online && Array.isArray(h.agent.caps) && h.agent.caps.length);

/** Alle Zeilen der Host-Karte (Text): Agent-Detail, Systemwerte, Updates, fehlgeschlagene Dienste. entry kommt aus dem Speicher (get). */
export function cardLines(h, entry) {
  const lines = [];
  const detail = agentDetail(h);
  if (detail) lines.push({ id: 'agent', text: detail, warn: false });
  const sys = entry && entry.status && entry.status.info;
  const parts = systemParts(sys);
  if (parts.length) lines.push({ id: 'system', text: parts.map((p) => p.text).join(' · '), warn: parts.some((p) => p.warn) });
  const health = healthPart(sys);
  if (health) lines.push(health);
  const rate = netRate(entry && entry.prevStatus, entry && entry.status);
  if (rate) lines.push({ id: 'net', text: rateText(rate), warn: false });
  const upd = updatesLine(entry && entry.updates);
  if (upd) lines.push({ id: 'updates', text: upd, warn: !!(entry.updates.updates.security > 0) });
  const svc = entry && entry.services && entry.services.services;
  if (svc && svc.count > 0) lines.push({ id: 'services', text: tp('hostinfo.failedUnits', svc.count), warn: true });
  return lines;
}

/* ---------- Fehler und Zustaende ---------- */
const ERR = {
  timeout: tk('hostinfo.errTimeout'), invalid_answer: tk('hostinfo.errInvalid'), agent_error: tk('hostinfo.errAgent'), failed: tk('hostinfo.errFailed'),
  apt_failed: tk('hostinfo.errApt'), update_check_failed: tk('hostinfo.errUpdateCheck'), systemctl_failed: tk('hostinfo.errSystemctl'), not_permitted: tk('hostinfo.errNotPermitted'),
};
export const errorText = (code) => t(ERR[code] || tk('hostinfo.errUnknown'));

/** Ein Satz, wenn die Antwort nicht (ganz) in Ordnung ist: offline, kein Agent, kann der Agent nicht, Fehler; bei altem Stand der Zeitpunkt. '' bei ok. */
export function stateNote(resp) {
  if (!resp) return t('hostinfo.stLoading');
  let note = '';
  if (resp.state === 'offline') note = t('hostinfo.stOffline');
  else if (resp.state === 'no_agent') note = t('hostinfo.stNoAgent');
  else if (resp.state === 'unsupported') note = t('hostinfo.stUnsupported');
  else if (resp.state === 'error') note = errorText(resp.error);
  if (resp.stale && resp.at && when(resp.at)) note += (note ? ' ' : '') + t('hostinfo.staleFrom', { when: when(resp.at) });
  return note;
}

/* ---------- HTML-Bausteine (esc: Funktion, die Text fuer HTML schuetzt) ---------- */
/**
 * Auf dem Handy (html.m3) sind es die Bausteine aus docs/ui-kit.html (m3-rows, m3-list, m3-msg, m3-sec); am Desktop, der m3.css nicht laedt, gelten die schlichten hi-*-Stile.
 * Gleiche Inhalte, zwei Huellen: kit darf zum Testen als letztes Argument von aussen kommen.
 */
export const isKit = () => typeof document !== 'undefined' && document.documentElement.classList.contains('m3');
const multi = (v, esc) => String(v).split('\n').map(esc).join('<br>');
const dl = (rows, esc, kit = isKit()) => {
  const shown = rows.filter((r) => r[1] !== '' && r[1] != null);
  if (kit) return `<div class="m3-card m3-rows">${shown.map(([k, v, cls]) => `<div class="m3-row${/\bwarn\b/.test(cls || '') ? ' bad' : ''}"><div class="rt">${esc(k)}</div><div class="rc"><span class="rv">${multi(v, esc)}</span></div></div>`).join('')}</div>`;
  return `<dl class="hi-dl">${shown.map(([k, v, cls]) => `<dt>${esc(k)}</dt><dd${cls ? ` class="${cls}"` : ''}>${esc(v)}</dd>`).join('')}</dl>`;
};
/** Ein Hinweis: warn hebt ihn hervor */
export const noteP = (text, esc, warn = false, kit = isKit()) => (kit
  ? (warn ? `<div class="m3-msg warn"><div class="msg-t"><span>${esc(text)}</span></div></div>` : `<p class="hint">${esc(text)}</p>`)
  : `<p class="hi-note${warn ? ' warn' : ''}">${esc(text)}</p>`);
/** Die Kernaussage eines Abschnitts ("Updates: 3 verfuegbar") */
const sumP = (text, esc, warn = false, kit = isKit()) => (kit
  ? `<div class="m3-msg${warn ? ' warn' : ' ok'}"><div class="msg-t"><b>${esc(text)}</b></div></div>`
  : `<p class="hi-sum${warn ? ' warn' : ''}">${esc(text)}</p>`);
const subH = (text, esc, kit = isKit()) => (kit ? `<div class="m3-sec"><span>${esc(text)}</span></div>` : `<h4 class="hi-sub">${esc(text)}</h4>`);
/** Eine Liste aus { name, detail, badge? (Text), off? } */
const listBlock = (items, esc, kit = isKit()) => (kit
  ? `<div class="m3-card m3-list">${items.map((i) => `<div class="m3-li static"><span class="txt"><span class="hl">${esc(i.name)}</span><span class="sl">${esc(i.detail)}</span></span>${i.badge ? `<span class="tr"><span class="m3-chip status"><span class="m3-dot bad"></span>${esc(i.badge)}</span></span>` : ''}</div>`).join('')}</div>`
  : `<ul class="hi-list">${items.map((i) => `<li${i.badge ? ' class="sec"' : i.off ? ' class="off"' : ''}><b>${esc(i.name)}</b> <span>${esc(i.detail)}</span>${i.badge ? `<em class="hi-badge">${esc(i.badge)}</em>` : ''}</li>`).join('')}</ul>`);
/** Ueberschrift eines Abschnitts im Dialog (siehe hostinfo-dialog.js) */
export const sectionHead = (text, esc, kit = isKit()) => (kit ? `<div class="m3-sec"><span>${esc(text)}</span></div>` : `<h3>${esc(text)}</h3>`);
const note = (resp, esc) => { const n = stateNote(resp); return n ? noteP(n, esc) : ''; };

export function systemHtml(resp, esc) {
  const i = resp && resp.info;
  if (!i) return note(resp, esc);
  const mem = i.memory || {};
  const ram = ramPercent(mem);
  const swap = mem.swapTotalBytes > 0 ? t('hostinfo.ofTotal', { used: fmtBytes(mem.swapTotalBytes - mem.swapFreeBytes), total: fmtBytes(mem.swapTotalBytes) }) : '';
  const virt = i.virt && i.virt.kind !== 'none' ? t(i.virt.kind === 'container' ? tk('hostinfo.virtContainer') : tk('hostinfo.virtVm'), { name: i.virt.name || '' }) : '';
  const prox = i.proxmox ? t('hostinfo.proxmox', { product: t(PROXMOX[i.proxmox.product] || 'hostinfo.pxVe'), version: i.proxmox.version || '' }).trim() : '';
  const disks = (i.disks || []).map((d) => `${d.mount}: ${t('hostinfo.ofTotal', { used: fmtBytes(d.usedBytes), total: fmtBytes(d.totalBytes) })} (${fmtPercent(d.percent)})`).join('\n');
  const addr = (i.addresses || []).map((a) => `${a.address}${a.family === 4 ? '/' + a.prefix : ''}${a.iface ? ' (' + a.iface + ')' : ''}`).join('\n');
  const net = (i.network || []).map((n) => `${n.iface}: ↓ ${fmtBytes(n.rxBytes)} ↑ ${fmtBytes(n.txBytes)}`).join('\n');
  return note(resp, esc) + dl([
    [t('hostinfo.os'), [i.os, i.arch].filter(Boolean).join(' · ')],
    [t('hostinfo.kernel'), i.kernel || ''],
    [t('hostinfo.hostname'), i.hostname || ''],
    [t('hostinfo.virt'), [virt, prox].filter(Boolean).join(' · ')],
    [t('hostinfo.cpuLoad'), [loadText(i.load), i.cpuCount ? t('hostinfo.cores', { n: i.cpuCount }) : ''].filter(Boolean).join(' · ')],
    [t('hostinfo.memory'), mem.totalBytes ? t('hostinfo.ofTotal', { used: fmtBytes(mem.totalBytes - mem.availableBytes), total: fmtBytes(mem.totalBytes) }) + (ram !== null ? ` (${fmtPercent(ram)})` : '') : '', ram !== null && ram >= 90 ? 'warn' : ''],
    [t('hostinfo.swap'), swap],
    [t('hostinfo.disks'), disks, (i.disks || []).some((d) => d.percent >= 90) ? 'warn hi-lines' : 'hi-lines'],
    [t('hostinfo.addresses'), addr, 'hi-lines'],
    [t('hostinfo.netTotals'), net, 'hi-lines'],
    [t('hostinfo.uptimeLabel'), fmtUptime(i.uptimeSeconds)],
    [t('hostinfo.reboot'), i.rebootRequired ? t('hostinfo.rebootYes') + (i.rebootPackages && i.rebootPackages.length ? ' (' + i.rebootPackages.join(', ') + ')' : '') : t('hostinfo.rebootNo'), i.rebootRequired ? 'warn' : ''],
  ], esc) + healthHtml(i.health, esc);
}

/** Platten-Gesundheit: SMART je Platte (oder warum nicht lesbar), RAID aus /proc/mdstat, ZFS; alles nur lesend */
export function healthHtml(h, esc) {
  if (!h) return '';
  const head = subH(t('hostinfo.health'), esc);
  if (h.pending) return head + noteP(t('hostinfo.stLoading'), esc);
  const rows = [];
  const sm = h.smart;
  if (sm && sm.available) rows.push([t('hostinfo.smart'), (sm.devices || []).map((d) => `${d.name}: ${t(SMART_STATE[d.health] || SMART_STATE.unknown)}`).join('\n'), (sm.devices || []).some((d) => d.health === 'failing') ? 'warn hi-lines' : 'hi-lines']);
  else if (sm) rows.push([t('hostinfo.smart'), t(SMART_WHY[sm.reason] || SMART_WHY.no_access)]);
  if ((h.md || []).length) rows.push([t('hostinfo.raid'), h.md.map((a) => `${a.name}${a.level ? ' (' + a.level + ')' : ''}: ${t(MD_STATE[a.state] || MD_STATE.inactive)} · ${a.active}/${a.total}${a.syncPercent != null ? ' · ' + fmtNumber(a.syncPercent, 1) + ' %' : ''}`).join('\n'), h.md.some((a) => a.state !== 'ok') ? 'warn hi-lines' : 'hi-lines']);
  if (h.zfs) rows.push([t('hostinfo.zfs'), h.zfs.healthy ? t('hostinfo.zfsOk') : (h.zfs.pools.length ? h.zfs.pools.map((p) => `${p.name}: ${p.state}`).join('\n') : t('hostinfo.zfsBad')), h.zfs.healthy ? '' : 'warn hi-lines']);
  return head + dl(rows, esc);
}
const PROXMOX = { ve: tk('hostinfo.pxVe'), pbs: tk('hostinfo.pxPbs'), pmg: tk('hostinfo.pxPmg') };

export function updatesHtml(resp, esc) {
  const u = resp && resp.updates;
  if (!u) return note(resp, esc);
  const head = sumP(updatesLine(resp), esc, u.security > 0);
  const items = (u.packages || []).map((p) => ({ name: p.name, detail: p.from ? `${p.from} → ${p.to}` : t('hostinfo.newPackage', { v: p.to }), badge: p.security ? t('hostinfo.security') : '' }));
  const extra = [];
  if (u.keptBack && u.keptBack.length) extra.push(noteP(t('hostinfo.keptBack', { list: u.keptBack.join(', ') }), esc));
  if (u.held && u.held.length) extra.push(noteP(t('hostinfo.held', { list: u.held.join(', ') }), esc));
  if (u.removals > 0) extra.push(noteP(tp('hostinfo.removals', u.removals), esc));
  if (u.truncated) extra.push(noteP(t('hostinfo.truncated', { n: (u.packages || []).length }), esc));
  // Die Paketlisten aktualisiert nur root; der Agent zeigt, wie alt sie sind, damit "keine Updates" nicht truegt
  const age = u.listsAgeSeconds != null ? noteP(`${t('hostinfo.listsAge', { age: fmtAge(u.listsAgeSeconds) })} ${t('hostinfo.listsHint')}`, esc, u.listsAgeSeconds > 7 * 86400) : '';
  return note(resp, esc) + head + (items.length ? listBlock(items, esc) : '') + extra.join('') + age;
}

export function servicesHtml(resp, esc) {
  const s = resp && resp.services;
  if (!s) return note(resp, esc);
  const items = (s.failed || []).map((f) => ({ name: f.unit, detail: f.description || '' }));
  return note(resp, esc) + (s.count > 0 ? sumP(tp('hostinfo.failedUnits', s.count), esc, true) + listBlock(items, esc) : sumP(t('hostinfo.noFailedUnits'), esc));
}

const DOCKER_REASON = { no_socket: tk('hostinfo.dockerNoSocket'), no_access: tk('hostinfo.dockerNoAccess'), error: tk('hostinfo.dockerError') };
export function dockerHtml(resp, esc) {
  const d = resp && resp.docker;
  if (!d) return note(resp, esc);
  if (!d.available) return note(resp, esc) + noteP(t(DOCKER_REASON[d.reason] || DOCKER_REASON.error), esc);
  const items = (d.containers || []).map((c) => ({ name: c.name, detail: `${c.image} · ${c.status || c.state}`, off: c.state !== 'running' }));
  return note(resp, esc) + sumP(t('hostinfo.dockerSum', { running: d.running, total: d.total }), esc)
    + (d.restarting > 0 ? sumP(tp('hostinfo.dockerRestarting', d.restarting), esc, true) : '') + (items.length ? listBlock(items, esc) : '')
    + (d.truncated ? noteP(t('hostinfo.truncated', { n: d.containers.length }), esc) : '');
}

/* ---------- Speicher im Browser ---------- */
const KINDS = [['status', 'sysinfo'], ['updates', 'updates'], ['services', 'services'], ['docker', 'docker']];
export const FRESH_MS = 55000;      // der Server haelt 60 s: seltener nachzufragen bringt nichts, haeufiger auch nicht

/** GET /api/hosts/{id}/<kind> (fresh: ausdruecklich neu lesen, der Server begrenzt es auf alle 15 s) */
export const fetchKind = (api, hostId, kind, fresh = false) => api(`/api/hosts/${hostId}/${kind}${fresh ? '?fresh=1' : ''}`);

/**
 * Was die Ansichten ueber die Hosts wissen: je Host { status, updates, services, at, loading }. Es wird nur gefragt, wenn eine Ansicht ensure() ruft
 * (also gerade gezeichnet wird, jemand sieht hin), nur fuer verbundene Agenten mit Faehigkeiten und hoechstens alle FRESH_MS je Host.
 * Docker wird nur im Dialog gelesen. Fehler werden verschluckt (die Karte zeigt dann einfach nichts); onChange meldet neue Daten zum Neuzeichnen.
 */
export function createHostInfoStore({ api, onChange = () => {}, now = Date.now, ttl = FRESH_MS }) {
  const entries = new Map();
  const get = (id) => entries.get(id) || null;

  /** Liest status/updates/services eines Hosts (je nach Faehigkeit). Gibt den Eintrag zurueck, wirft nie. */
  async function load(host, { fresh = false } = {}) {
    const old = entries.get(host.id) || {};
    if (old.loading) return old.loading;
    const caps = (host.agent && host.agent.caps) || [];
    const want = KINDS.filter(([kind, cap]) => kind !== 'docker' && caps.includes(cap));
    const run = (async () => {
      const got = await Promise.allSettled(want.map(([kind]) => fetchKind(api, host.id, kind, fresh)));
      const next = { ...(entries.get(host.id) || {}), at: now(), loading: null };
      want.forEach(([kind], i) => {
        if (got[i].status !== 'fulfilled' || !got[i].value) return;
        // Die Netzwerkrate braucht zwei Ablesungen: die vorige bleibt als prevStatus, solange die neue wirklich neuer ist
        if (kind === 'status' && next.status && got[i].value.at && got[i].value.at !== next.status.at) next.prevStatus = next.status;
        next[kind] = got[i].value;
      });
      entries.set(host.id, next);
      try { onChange(host.id); } catch { /* die Ansicht darf das Lesen nicht stoeren */ }
      return next;
    })();
    entries.set(host.id, { ...old, at: now(), loading: run });       // at schon jetzt: ein fehlgeschlagener Versuch wird nicht sofort wiederholt
    return run;
  }

  /** Fuer die gezeichneten Hosts: veraltete Eintraege neu lesen; Hosts, die es nicht mehr gibt, vergessen. Gibt die Hosts zurueck, die gelesen werden. */
  function ensure(hosts) {
    const ids = new Set(hosts.map((h) => h.id));
    for (const id of [...entries.keys()]) if (!ids.has(id)) entries.delete(id);
    const started = [];
    for (const h of hosts) {
      if (!wantsInfo(h)) { if (entries.has(h.id) && !entries.get(h.id).loading) entries.delete(h.id); continue; }
      const e = entries.get(h.id);
      if (e && (e.loading || now() - e.at < ttl)) continue;
      started.push(h); load(h);
    }
    return started;
  }
  const clear = () => entries.clear();
  return { get, load, ensure, clear };
}

/* ---------- Installation per SSH ---------- */
/**
 * Der Einzeiler fuer den Host (docs/HOST-AGENT.md): laedt das Skript vom eigenen Server in eine Datei (so bleibt es zum Ansehen liegen, nichts wird blind in eine
 * Shell geleitet) und startet es mit sudo. Das sudo-Passwort tippt der Benutzer im Terminal selbst; JulOS sieht es nie.
 */
export function installCommand(origin, code) {
  const http = origin.startsWith('http:') && !/^http:\/\/(localhost|127\.0\.0\.1|\[::1\])(:|$)/.test(origin);
  return `curl -fsSL ${origin}/agent-files/install-host-agent.sh -o julos-install-host-agent.sh && sudo sh julos-install-host-agent.sh --server ${origin} --code ${code}${http ? ' --allow-http' : ''}`;
}

/**
 * Die Terminal-Verbindung, in der der Befehl landen soll: eine SSH-Verbindung dieses Hosts mit der blanken Shell (nie eine Claude- oder Codex-Kachel: dort ginge
 * der Befehl an das Programm statt an die Shell), die zuletzt benutzte zuerst. null, wenn es keine gibt (dann zeigt der Dialog den Befehl zum Kopieren).
 */
export function pickTerminal(connections, hostId) {
  const shells = (connections || []).filter((c) => c.kind === 'terminal' && c.hostId === hostId && programOf(c) === 'shell');
  shells.sort((a, b) => String(b.lastUsedAt || '').localeCompare(String(a.lastUsedAt || '')) || String(a.name).localeCompare(String(b.name)));
  return shells[0] || null;
}
