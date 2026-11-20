/* =====================================================================
 * Host-Agent: Systemwerte, Updates, Dienste, Docker (docs/HOST-AGENT.md). Reine Hilfen ohne DOM, damit Desktop-Oberflaeche, Handy-Huelle und der
 * Dialog "Host-Details" dasselbe zeigen und Node sie testen kann.
 *  - Formatierer und Zusammenfassungszeilen (Text, noch nicht fuer HTML geschuetzt)
 *  - HTML-Bausteine (Strings; jeder Wert vom Agenten geht durch esc, denn der Agent ist nicht der Server)
 *  - der Speicher im Browser (ensure/load): fragt nur, wenn eine Ansicht es verlangt, und hoechstens alle 55 s je Host
 *  - der Einzeiler fuer die SSH-Installation und die Wahl der Terminal-Verbindung
 * Server-Antworten: GET /api/hosts/{id}/status|updates|services|docker = { state, at, stale, error?, info|updates|services|docker? }
 * ===================================================================== */
import { t, tk, fmtNumber, fmtDate } from './i18n.js';
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
export const MD_STATE = { ok: tk('hostinfo.mdOk'), degraded: tk('hostinfo.mdDegraded'), recovering: tk('hostinfo.mdRecovering'), inactive: tk('hostinfo.mdInactive') };
export const SMART_STATE = { ok: tk('hostinfo.smartOk'), failing: tk('hostinfo.smartFailing'), unknown: tk('hostinfo.smartUnknown') };
export const SMART_WHY = { no_smartctl: tk('hostinfo.smartNoTool'), no_access: tk('hostinfo.smartNoAccess'), no_devices: tk('hostinfo.smartNoDevices') };

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
/** Summe der Zaehler aller Schnittstellen (Bytes seit dem Start) */
const netSum = (info) => (info && Array.isArray(info.network) ? info.network.reduce((a, n) => ({ rx: a.rx + n.rxBytes, tx: a.tx + n.txBytes }), { rx: 0, tx: 0 }) : null);
/** Rate in Bytes/s zwischen zwei Ablesungen (Antworten von /status); null ohne zwei Werte, bei Zaehlerrueckgang (Neustart) oder zu kurzem Abstand */
export function netRate(prev, cur) {
  const a = netSum(prev && prev.info), b = netSum(cur && cur.info);
  const dt = prev && cur && prev.at && cur.at ? (Date.parse(cur.at) - Date.parse(prev.at)) / 1000 : 0;
  if (!a || !b || !(dt >= 5) || b.rx < a.rx || b.tx < a.tx) return null;
  return { rx: (b.rx - a.rx) / dt, tx: (b.tx - a.tx) / dt };
}
export const rateText = (r) => t('hostinfo.net', { rx: fmtBytes(r.rx) + '/s', tx: fmtBytes(r.tx) + '/s' });

/* ---------- Zusammenfassungen ---------- */
/** "Updates: 3 verfuegbar (1 Sicherheit)", "Updates: keine", sonst '' (keine Daten: nichts behaupten). resp ist die Antwort von /updates. */
export function updatesLine(resp) {
  const u = resp && resp.updates;
  if (!u) return '';
  if (!u.count) return t('hostinfo.updatesNone');
  return u.securityKnown && u.security > 0 ? t('hostinfo.updatesCountSec', { n: u.count, s: u.security }) : t('hostinfo.updatesCount', { n: u.count });
}

const CAP_LABEL = { sysinfo: tk('hostinfo.capSysinfo'), updates: tk('hostinfo.capUpdates'), services: tk('hostinfo.capServices'), docker: tk('hostinfo.capDocker') };
/** "Host-Agent" oder "Desktop-Agent"; '' ohne Agent */
/**
 * Kann der Agent die Host-Funktionen (Systemwerte, Updates, Aktionen)? 'yes' | 'update' (alter Desktop-Agent vor 0.7.0: liefert noch keine Systemwerte, das Selbst-Update bringt sie) | 'none' (verbunden, aber nichts davon, zum Beispiel
 * kein Linux) | 'unknown' (nicht verbunden). Massgeblich ist das Feld hostSupport des Servers, sonst das Wort "host" in caps; der Name der Plattform zaehlt nicht (nur ein Host-Agent ohne Woerter gilt als 'yes').
 */
export function hostSupport(a) {
  if (!a) return 'unknown';
  if (typeof a.hostSupport === 'string') return a.hostSupport;
  if (Array.isArray(a.caps) && (a.caps.includes('host') || a.caps.some((c) => CAP_LABEL[c]))) return 'yes';       // der Server ergaenzt "host", sobald eine Lese-Faehigkeit da ist
  return a.kind === 'host' ? 'yes' : (a.online ? 'update' : 'unknown');
}
/** Art des Agenten nach seinen Woertern: Desktop und Host zugleich (Agent 0.7.0 und neuer), nur Host oder nur Desktop */
export function agentModeText(h) {
  const a = h && h.agent; if (!a) return '';
  const caps = Array.isArray(a.caps) ? a.caps : [];
  const host = hostSupport(a) === 'yes', desktop = caps.includes('desktop') || (a.kind !== 'host' && !host);
  return host && desktop ? t('hostinfo.modeBoth') : host ? t('hostinfo.modeHost') : t('hostinfo.modeDesktop');
}
/** Faehigkeiten, soweit gemeldet ("Systemwerte, Updates, Dienste, Docker"); der Server nennt sie nur fuer verbundene Agenten */
export const capsText = (h) => ((h && h.agent && Array.isArray(h.agent.caps) ? h.agent.caps : []).filter((c) => CAP_LABEL[c]).map((c) => t(CAP_LABEL[c])).join(', '));
/** Eine Zeile zum Agenten: Modus und Faehigkeiten (der Satz zu Verbindung und Version steht in hosts.js agentSummary) */
export function agentDetail(h) {
  const mode = agentModeText(h), caps = capsText(h);
  return [mode, caps && t('hostinfo.caps', { list: caps })].filter(Boolean).join(' · ');
}

/** Hat der Server fuer diesen Host etwas zu lesen? Nur ein verbundener Agent mit gemeldeten Faehigkeiten. */
export const wantsInfo = (h) => !!(h && h.agent && h.agent.online && hostSupport(h.agent) === 'yes' && Array.isArray(h.agent.caps) && h.agent.caps.some((c) => CAP_LABEL[c]));

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
/** Das Installationsskript, das der Server ausliefert (/agent-files/): ein Ablauf fuer jede Maschine, das Skript erkennt selbst, ob sie einen Desktop hat */
export const AGENT_INSTALLER = 'install-agent.sh';
const INSTALLER_FILE = 'julos-install-agent.sh';
/**
 * Der Einzeiler fuer den Host (docs/HOST-AGENT.md): laedt das Skript vom eigenen Server in eine Datei (so bleibt es zum Ansehen liegen, nichts wird blind in eine Shell geleitet) und startet es mit sudo.
 * opts.allow: die Woerter, die der Agent lokal erlauben soll (nur aus den bekannten, in fester Reihenfolge, nie ein Standard); opts.dockerGroup: --docker-group (nur mit dem Wort docker).
 * Das sudo-Passwort tippt der Benutzer im Terminal selbst; JulOS sieht es nie.
 */
export function installCommand(origin, code, opts = {}) {
  const http = origin.startsWith('http:') && !/^http:\/\/(localhost|127\.0\.0\.1|\[::1\])(:|$)/.test(origin);
  const allow = ALLOW_WORDS.filter((w) => (opts.allow || []).includes(w));
  return `curl -fsSL ${origin}/agent-files/${AGENT_INSTALLER} -o ${INSTALLER_FILE} && sudo sh ${INSTALLER_FILE} --server ${origin} --code ${code}${allow.length ? ` --allow ${allow.join(',')}` : ''}${opts.dockerGroup && allow.includes('docker') ? ' --docker-group' : ''}${http ? ' --allow-http' : ''}`;
}
const ALLOW_WORDS = ['updates', 'logs', 'services', 'docker', 'reboot'];

/**
 * Die Terminal-Verbindung, in der der Befehl landen soll: eine SSH-Verbindung dieses Hosts mit der blanken Shell (nie eine Claude- oder Codex-Kachel: dort ginge
 * der Befehl an das Programm statt an die Shell), die zuletzt benutzte zuerst. null, wenn es keine gibt (dann zeigt der Dialog den Befehl zum Kopieren).
 */
export function pickTerminal(connections, hostId) {
  const shells = (connections || []).filter((c) => c.kind === 'terminal' && c.hostId === hostId && programOf(c) === 'shell');
  shells.sort((a, b) => String(b.lastUsedAt || '').localeCompare(String(a.lastUsedAt || '')) || String(a.name).localeCompare(String(b.name)));
  return shells[0] || null;
}
