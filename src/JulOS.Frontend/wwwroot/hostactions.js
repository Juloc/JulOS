/* =====================================================================
 * Host-Agent, Stufe 3: Aktionen (docs/HOST-AGENT.md). Reine Hilfen ohne DOM, damit Handy-Huelle, Desktop und der Dialog "Host-Details" dasselbe zeigen und Node sie testen kann.
 *  - wann welche Aktion angeboten wird (Stufe am Server, lokale Erlaubnis des Agenten, Pause, Version)
 *  - der Text der Bestaetigung (Host, Aktion, Ziel, Warnungen), das Tippen des Rechnernamens
 *  - Fortschritt, Ergebnis, Fehlertexte, Verlauf als HTML-Bausteine (jeder Wert vom Server oder Agenten geht durch esc)
 *  - die Aufrufe: Bestaetigung holen (prepare), ausfuehren (run), Auftrag abfragen
 * Server: POST /api/hosts/{id}/actions/prepare -> { token, expiresIn, action, target, targetName, host, typeToConfirm?, warnings, readOnly, confirmLevel, confirmMode, confirmWarn?, confirmCritical? }
 *         POST /api/hosts/{id}/actions/run {token, action, target, typed?, acknowledged?} -> 202 { job } ; GET /api/hosts/{id}/actions/{job} -> { state, progress, done, total, result?, error? }
 * ===================================================================== */
import { t, tk, tp } from './i18n.js';
import { when, hostSupport } from './hostinfo.js';
import { dl, noteP, callout, listBlock, card, icon, button } from './hostui.js';
import { confirmView, auditConfirmText } from './confirm.js';

export const RIGHTS = ['updates', 'reboot', 'services', 'docker', 'logs'];
export const LEVELS = ['read', 'updates', 'full'];
/** Dieselbe Zuordnung wie AgentControl.Allows am Server: welche Aktionsarten eine Stufe erlaubt */
const LEVEL_RIGHTS = { read: [], updates: ['updates', 'logs'], full: RIGHTS };
export const MIN_ACTION_VERSION = [0, 6, 0];

export const ACTION_LABEL = {
  'updates.refresh': tk('hostact.a.updatesRefresh'), 'updates.install': tk('hostact.a.updatesInstall'), 'updates.repair': tk('hostact.a.updatesRepair'),
  'power.reboot': tk('hostact.a.reboot'), 'power.poweroff': tk('hostact.a.poweroff'),
  'service.start': tk('hostact.a.serviceStart'), 'service.stop': tk('hostact.a.serviceStop'), 'service.restart': tk('hostact.a.serviceRestart'),
  'docker.start': tk('hostact.a.dockerStart'), 'docker.stop': tk('hostact.a.dockerStop'), 'docker.restart': tk('hostact.a.dockerRestart'), 'docker.pull': tk('hostact.a.dockerPull'),
  'logs.unit': tk('hostact.a.logsUnit'), 'logs.container': tk('hostact.a.logsContainer'),
  'login.set': tk('hostact.a.loginSet'), 'login.remove': tk('hostact.a.loginRemove'),          // Host-Login geaendert oder entfernt (im Verlauf, nie mit einem Wert)
};
/** Was am Login geaendert wurde (der Server schreibt nur diese Codes, nie einen Wert) */
const LOGIN_DETAIL = { 'user+password': tk('hostact.login.userPassword'), password: tk('hostact.login.password'), user: tk('hostact.login.user'), 'password removed': tk('hostact.login.passwordRemoved'), unchanged: tk('hostact.login.unchanged'), 'password (files)': tk('hostact.login.passwordFiles') };
export const actionLabel = (a) => t(ACTION_LABEL[a] || tk('hostact.a.unknown'));
/** Aktionen, die etwas anhalten oder abschalten: die Taste wird rot */
export const isDanger = (action, target) => action === 'power.reboot' || action === 'power.poweroff' || action === 'service.stop' || action === 'docker.stop' || (action === 'updates.install' && target === 'all');

const versionOk = (v) => {
  const m = /^(\d+)\.(\d+)\.(\d+)/.exec(String(v || ''));
  if (!m) return false;
  for (let i = 0; i < 3; i++) { const a = Number(m[i + 1]); if (a !== MIN_ACTION_VERSION[i]) return a > MIN_ACTION_VERSION[i]; }
  return true;
};

/** Kann der Agent die Host-Anfragen beantworten? Danach richtet sich die Anzeige (hostSupport des Servers, sonst das Wort "host" in caps), nicht nach dem Namen der Plattform: ein Agent, zwei Faehigkeitsgruppen (Desktop und Host). */
const capable = (a) => hostSupport(a) === 'yes';

/**
 * Warum wird diese Aktionsart gerade nicht angeboten? null = sie ist erlaubt. Sonst { why, link? }: why benennt den Grund (Text: whyText), link 'actions' heisst, dass der Abschnitt
 * "Aktionen" der Weg zum Freischalten ist (Stufe am Server, Pause), sonst gibt es hier nichts zu tun (kein Agent, nicht verbunden, zu alt, meldet nichts).
 */
export function blockReason(host, right, paused = false) {
  const a = host && host.agent;
  if (!a) return { why: 'noAgent' };
  if (!a.online) return { why: 'offline' };
  if (!capable(a)) return { why: hostSupport(a) === 'none' ? 'noSupport' : 'noCaps' };
  if (!versionOk(a.version)) return { why: 'outdated' };
  if (paused) return { why: 'paused', link: 'actions' };
  if (!(LEVEL_RIGHTS[a.control] || []).includes(right)) return { why: !a.control || a.control === 'read' ? 'levelOff' : 'level', link: 'actions' };
  if (!Array.isArray(a.allow) || !a.allow.includes(right)) return { why: 'word', word: right, link: 'actions' };
  return null;
}
const WHY = {
  noAgent: tk('hostact.why.noAgent'), offline: tk('hostact.why.offline'), noCaps: tk('hostact.why.noCaps'), outdated: tk('hostact.why.outdated'), paused: tk('hostact.why.paused'),
  noSupport: tk('hostact.why.noSupport'), levelOff: tk('hostact.why.levelOff'), level: tk('hostact.why.level'), word: tk('hostact.why.word'),
};
/** Der Satz zum Grund einer gesperrten Aktion, mit dem Weg zum Freischalten */
export const whyText = (r) => t(WHY[r.why] || WHY.noCaps, { word: r.word || '' });

/** Zeigt die Oberflaeche diese Aktionsart fuer den Host an? Nur ein verbundener Agent, der Faehigkeiten meldet und ab 0.6.0 ist, dessen Stufe am Server UND dessen lokale Erlaubnis sie nennen, und nicht pausiert. */
export const canAct = (host, right, paused = false) => blockReason(host, right, paused) === null;

/** Gibt es fuer diesen Host etwas zu schalten (Stufe, lokale Erlaubnis)? Fuer den Abschnitt "Aktionen" */
export const hasActionAgent = (host) => !!(host && host.agent && capable(host.agent) && versionOk(host.agent.version));

/* ---------- die Bestaetigung ---------- */
/** Ziel zum Anzeigen: "alle Updates", der Name des Containers, der Dienst, das Paket; '' ohne Ziel */
export function targetText(conf) {
  if (conf.action === 'updates.install' && conf.target === 'all') return t('hostact.allPackages');
  return conf.targetName || conf.target || '';
}

/** Muss der Rechnername getippt werden? Dann ist das Ergebnis der Name, sonst null. */
export const typedName = (conf) => (conf && typeof conf.typeToConfirm === 'string' && conf.typeToConfirm ? conf.typeToConfirm : null);
/** Stimmt der getippte Text (Gross/Klein und Leerraum am Rand zaehlen nicht; der Server prueft es noch einmal)? */
export const typedOk = (conf, text) => { const need = typedName(conf); return !need || String(text || '').trim().toLowerCase() === need.toLowerCase(); };

const WARN = { julos_group: tk('hostact.warn.julosGroup'), kept_back: tk('hostact.warn.keptBack'), proxmox_full: tk('hostact.warn.proxmoxFull') };
/** Alles, was die Bestaetigung zeigt: Satz, Zeilen, Warnungen, Stufe (Routine, gefaehrlich, sehr gefaehrlich), Haekchen oder Tippaufforderung */
export function confirmModel(conf) {
  const target = targetText(conf);
  const v = confirmView(conf, conf.host);
  return {
    title: actionLabel(conf.action),
    sentence: target ? t('hostact.sentenceTarget', { host: conf.host, action: actionLabel(conf.action), target }) : t('hostact.sentence', { host: conf.host, action: actionLabel(conf.action) }),
    rows: [[t('hostact.rowHost'), conf.host], [t('hostact.rowAction'), actionLabel(conf.action)], [t('hostact.rowTarget'), target]],
    warnings: (conf.warnings || []).filter((w) => !(v.critical && w === 'julos_group')).map((w) => t(WARN[w] || tk('hostact.warn.unknown'))),   // bei "sehr gefaehrlich" sagt es schon die rote Zeile
    level: v.level, mode: v.mode, needsCheck: v.needsCheck, warn: v.warn, criticalWarn: v.criticalWarn,
    typed: v.typed,
    danger: v.danger,
    expiresIn: conf.expiresIn,
    options: confirmOptions(conf),
  };
}

/** Die Wahl "alle Updates": Proxmox wird immer vollstaendig aktualisiert (kein Haken), sonst darf der Benutzer "full-upgrade" ausdruecklich waehlen (Standard aus). null ohne diese Wahl. */
export function confirmOptions(conf) {
  const o = conf && conf.options;
  if (!o || typeof o !== 'object') return null;
  return { proxmox: !!o.proxmox, fullChoice: !!o.fullChoice, full: !!o.full, keptBack: Array.isArray(o.keptBack) ? o.keptBack : [], removals: Number(o.removals) > 0 ? Number(o.removals) : 0 };
}

export function confirmHtml(conf, esc) {
  const m = confirmModel(conf);
  const warn = m.warnings.map((w) => callout(w, esc, 'warn')).join('');
  const o = m.options;
  const full = o && o.fullChoice
    ? `<label class="hu-check"><input type="checkbox" id="hostActFull"><span class="hu-cb" aria-hidden="true"></span><span>${esc(t('hostact.fullLabel'))}</span></label>${noteP(t('hostact.fullHint', { list: o.keptBack.join(', ') }), esc)}`
    : '';
  const removals = o && o.removals > 0 && (o.proxmox || o.fullChoice) ? `<div id="hostActRemovals"${o.proxmox ? '' : ' hidden'}>${callout(tp('hostact.removals', o.removals), esc, 'warn')}</div>` : '';
  const levelBox = (m.warn ? callout(t('confirm.warnTitle'), esc, 'warn', m.warn) : '') + (m.criticalWarn ? callout(t('confirm.criticalTitle'), esc, 'bad', m.criticalWarn) : '');
  const ack = m.needsCheck ? `<label class="hu-check hact-ack"><input type="checkbox" id="hostActAck"><span class="hu-cb" aria-hidden="true"></span><span>${esc(t('confirm.ack'))}</span></label>` : '';
  const typed = m.typed ? `<label class="hu-field hact-typed"><span>${esc(t('hostact.typePrompt', { name: m.typed }))}</span><input id="hostActTyped" type="text" autocomplete="off" autocapitalize="off" spellcheck="false" aria-label="${esc(t('hostact.typeLabel'))}"></label>` : '';
  return callout(m.sentence, esc, 'info') + dl(m.rows, esc) + levelBox + warn + full + removals + ack + typed + noteP(t('hostact.expires', { n: m.expiresIn }), esc);
}

/* ---------- Fehler ---------- */
const ERR = {
  host_not_found: tk('hostact.err.host_not_found'), no_agent: tk('hostact.err.no_agent'), unknown_action: tk('hostact.err.unknown_action'), invalid_target: tk('hostact.err.invalid_target'),
  paused: tk('hostact.err.paused'), control_off: tk('hostact.err.control_off'), offline: tk('hostact.err.offline'), agent_outdated: tk('hostact.err.agent_outdated'), unsupported: tk('hostact.err.unsupported'),
  agent_not_permitted: tk('hostact.err.agent_not_permitted'), not_permitted: tk('hostact.err.agent_not_permitted'), busy: tk('hostact.err.busy'), unknown_target: tk('hostact.err.unknown_target'),
  list_unavailable: tk('hostact.err.list_unavailable'), protected_unit: tk('hostact.err.protected_unit'), too_many: tk('hostact.err.too_many'), confirmation_invalid: tk('hostact.err.confirmation_invalid'),
  confirmation_expired: tk('hostact.err.confirmation_expired'), confirmation_mismatch: tk('hostact.err.confirmation_mismatch'), typed_mismatch: tk('hostact.err.typed_mismatch'), confirm_required: tk('hostact.err.confirm_required'),
  audit_failed: tk('hostact.err.audit_failed'), invalid_level: tk('hostact.err.invalid_level'), rate_limited: tk('hostact.err.rate_limited'), job_not_found: tk('hostact.err.job_not_found'),
  apt_locked: tk('hostact.err.apt_locked'), apt_failed: tk('hostact.err.apt_failed'), dpkg_failed: tk('hostact.err.dpkg_failed'), dpkg_broken: tk('hostact.err.dpkg_broken'), proxmox_whole: tk('hostact.err.proxmox_whole'), systemctl_failed: tk('hostact.err.systemctl_failed'), journal_failed: tk('hostact.err.journal_failed'),
  docker_denied: tk('hostact.err.docker_denied'), docker_unavailable: tk('hostact.err.docker_unavailable'), docker_failed: tk('hostact.err.docker_failed'), pull_failed: tk('hostact.err.pull_failed'),
  no_registry_image: tk('hostact.err.no_registry_image'), helper_unavailable: tk('hostact.err.helper_unavailable'), helper_closed: tk('hostact.err.helper_closed'), not_implemented: tk('hostact.err.not_implemented'),
  interrupted: tk('hostact.err.interrupted'), duplicate_job: tk('hostact.err.duplicate_job'), not_installed: tk('hostact.err.not_installed'), update_check_failed: tk('hostact.err.update_check_failed'),
  timeout: tk('hostact.err.timeout'), invalid_answer: tk('hostact.err.invalid_answer'), agent_error: tk('hostact.err.agent_error'),
};
/** Fehlercode (vom Server oder Agenten) in der Sprache der Oberflaeche; Unbekanntes wird allgemein gemeldet, nie roh angezeigt */
export const errText = (code) => t(ERR[code] || tk('hostact.err.unknown'));

/* ---------- Auftrag: Fortschritt und Ergebnis ---------- */
export const jobFinal = (job) => !!job && (job.state === 'ok' || job.state === 'failed' || job.state === 'accepted');

/** Prozent, wenn der Agent Zaehler meldet (erledigt von gesamt), sonst null */
export const jobPercent = (job) => (job && job.total > 0 && Number.isFinite(job.done) ? Math.max(0, Math.min(100, Math.round((job.done / job.total) * 100))) : null);

/** Ausgabe und Logs: als Text, jede Zeile fuer sich (CSS: pre-wrap), nie als HTML */
const pre = (text, esc) => `<pre class="hu-pre">${esc(text)}</pre>`;

/** Das Protokoll eines Laufs aufklappbar (Text, nie HTML); ohne Protokoll die letzten Zeilen der Ausgabe */
function logHtml(r, esc) {
  if (Array.isArray(r.log) && r.log.length) {
    return `<details class="hu-fold hu-log"><summary>${icon('chevron', 'chev')}<span>${esc(tp('hostact.logLines', r.log.length))}</span></summary><pre class="hu-pre" tabindex="0">${esc(r.log.join('\n'))}</pre></details>`;
  }
  return r.output ? pre(r.output, esc) : '';
}

/** Die Fehlerzusammenfassung eines apt-Laufs: betroffene Pakete, zurueckgehaltene Pakete, E:-Zeilen, offene Fragen, Sperre. '' ohne etwas Auffaelliges. */
export function summaryHtml(r, esc) {
  const s = r && r.summary;
  if (!s) return '';
  const rows = [];
  if ((s.failedPackages || []).length) rows.push([t('hostact.sum.failed'), s.failedPackages.join(', '), 'warn']);
  if ((s.keptBack || []).length) rows.push([t('hostact.sum.kept'), s.keptBack.join(', ')]);
  if ((s.errors || []).length) rows.push([t('hostact.sum.errors'), s.errors.join('\n'), 'warn']);
  const flags = [];
  if (s.locked) flags.push(noteP(t('hostact.sum.locked'), esc, true));
  if (s.configQuestion) flags.push(noteP(t('hostact.sum.question'), esc, true));
  return dl(rows, esc) + flags.join('');
}

/**
 * Der Stand eines Auftrags als HTML: laufend (Text, Balken, Live-Ausgabe), verloren, angenommen, fertig, fehlgeschlagen (Zusammenfassung oben, Protokoll aufklappbar, Angebot zu reparieren);
 * Ausgabe und Logs nur als Text. opts.repair: die Taste "Reparieren" darf gezeigt werden (Stufe und Wort erlauben es).
 */
export function jobHtml(job, esc, opts = {}) {
  if (!job) return '';
  const label = actionLabel(job.action);
  if (job.state === 'running' || job.state === 'lost') {
    const pct = jobPercent(job);
    const bar = pct === null ? '<progress></progress>' : `<progress max="100" value="${pct}"></progress>`;
    const live = Array.isArray(job.tail) && job.tail.length ? `<pre class="hu-pre hu-live" data-live tabindex="0">${esc(job.tail.slice(-60).join('\n'))}</pre>` : '';
    return callout(job.state === 'lost' ? t('hostact.stLost', { action: label }) : t('hostact.stRunning', { action: label }), esc, job.state === 'lost' ? 'warn' : 'info') + `<div class="hu-prog">${bar}</div>`
      + (job.progress ? noteP(job.progress + (pct !== null ? ` (${job.done}/${job.total})` : ''), esc) : '') + live;
  }
  const r = job.result || {};
  if (job.state === 'failed') {
    const repair = r.needsRepair ? callout(t('hostact.repairOffer'), esc, 'warn') + (opts.repair ? `<div class="hu-acts">${button(t('hostact.a.updatesRepair'), esc, { tone: 'filled', icon: 'gear', data: { 'task-act': 'repair' } })}</div>` : '') : '';
    return callout(t('hostact.stFailed', { action: label }), esc, 'bad', errText(job.error)) + summaryHtml(r, esc) + repair + logHtml(r, esc);
  }
  const parts = [];
  if (job.state === 'accepted') parts.push(callout(job.action === 'power.poweroff' ? t('hostact.stPoweroff') : t('hostact.stReboot'), esc, 'ok'));
  else parts.push(callout(t('hostact.stDone', { action: label }), esc, 'ok'));
  if (r.already) parts.push(noteP(t('hostact.already'), esc));
  if (job.action === 'docker.pull') parts.push(noteP(r.updated ? t('hostact.pullUpdated') : t('hostact.pullCurrent'), esc, !!r.updated));
  if (r.rebootRequired) parts.push(callout(t('hostact.rebootHint'), esc, 'warn'));
  if (r.summary && (r.summary.keptBack || []).length && !r.full) parts.push(noteP(t('hostact.sum.keptAfter', { list: r.summary.keptBack.join(', ') }), esc));
  if (Array.isArray(r.lines)) parts.push(r.lines.length ? pre(r.lines.join('\n'), esc) : noteP(t('hostact.noLogs'), esc));
  else parts.push(logHtml(r, esc));
  return parts.join('');
}

/* ---------- Verlauf ---------- */
const EVENT = { prepared: tk('hostact.ev.prepared'), started: tk('hostact.ev.started'), ok: tk('hostact.ev.ok'), failed: tk('hostact.ev.failed'), accepted: tk('hostact.ev.accepted'), lost: tk('hostact.ev.lost'), denied: tk('hostact.ev.denied') };
export const eventText = (e) => t(EVENT[e] || tk('hostact.ev.unknown'));
const CONTROL_TEXT = { read: tk('hostact.level.read'), updates: tk('hostact.level.updates'), full: tk('hostact.level.full') };
export const levelText = (l) => t(CONTROL_TEXT[l] || CONTROL_TEXT.read);

/** Eine Zeile des Verlaufs als Text: "Neustart · srv · abgelehnt (Aktionen sind nicht eingeschaltet)" */
export function auditLine(e) {
  const action = e.action === 'control.set' ? t('hostact.a.controlSet') : actionLabel(e.action);
  const detail = e.detail ? (e.event === 'denied' || e.event === 'failed' ? errText(e.detail) : e.detail === 'reboot_required' ? t('hostact.rebootHintShort') : e.detail === 'already' ? t('hostact.already') : levelOrDetail(e)) : '';
  return { when: when(e.at), who: e.user, text: [action, e.action === 'control.set' ? levelText(e.target) : e.target].filter(Boolean).join(' · '), event: e.action === 'control.set' ? eventText('ok') : eventText(e.event), detail };
}
const levelOrDetail = (e) => (e.detail === 'no_answer' ? t('hostact.noAnswer') : auditConfirmText(e.detail) || e.action.startsWith('login.') && LOGIN_DETAIL[e.detail] ? t(LOGIN_DETAIL[e.detail]) : e.detail);

export function auditHtml(resp, esc) {
  const list = (resp && resp.entries) || [];
  if (!list.length) return noteP(t('hostact.historyEmpty'), esc);
  const rows = list.map((e) => { const l = auditLine(e); return [`${l.when} · ${l.who}`, `${l.text} — ${l.event}${l.detail ? ' (' + l.detail + ')' : ''}`, e.event === 'denied' || e.event === 'failed' ? 'warn' : '']; });
  return dl(rows, esc);
}

/* ---------- Schalter "Aktionen" im Dialog ---------- */
/** Die Stufe am Server, die lokale Erlaubnis und der Pausenschalter eines Host-Agenten als Karte */
export function controlHtml(host, paused, esc) {
  const a = host.agent || {};
  const words = (a.allow || []).length ? (a.allow || []).map((w) => `<span class="hu-chip ok">${esc(w)}</span>`).join('') : `<span class="hu-chip">${esc(t('hostact.allowNone'))}</span>`;
  const opts = LEVELS.map((l) => `<option value="${l}"${a.control === l ? ' selected' : ''}>${esc(levelText(l))}</option>`).join('');
  const body = noteP(t('hostact.intro'), esc)
    + `<label class="hu-field"><span>${esc(t('hostact.level'))}</span><select id="hostActLevel" aria-label="${esc(t('hostact.level'))}">${opts}</select></label>`
    + `<div class="hu-field"><span>${esc(t('hostact.localAllow'))}</span><div class="hu-flags">${words}</div></div>`
    + `<label class="hu-switch hact-pause"><input id="hostActPause" type="checkbox"${paused ? ' checked' : ''}><span class="hu-sw" aria-hidden="true"></span><span>${esc(t('hostact.pause'))}</span></label>`
    + ((a.allow || []).length ? '' : callout(t('hostact.allowHint'), esc, 'info'));
  return card(t('hostact.secActions'), body, esc, { icon: 'tune' });
}

/* ---------- Tasten in den Abschnitten ---------- */
/**
 * Das, was die Abschnitte brauchen, um Aktionstasten zu zeichnen: can(recht) und btn(aktion, ziel, text). Ohne Erlaubnis zeichnet btn nichts. Die Tasten tragen nur
 * die Aktion und das Ziel als data-Werte; geklickt wird ueber den Dialog (hostactions-dialog.js), der die Bestaetigung holt.
 */
export function makeAct(host, paused, esc) {
  const rightOf = (a) => (a.startsWith('updates') ? 'updates' : a.startsWith('power') ? 'reboot' : a.startsWith('service') ? 'services' : a.startsWith('docker') ? 'docker' : 'logs');
  return {
    can: (right) => canAct(host, right, paused),
    btn(action, target, text, extraClass = '') {
      if (!canAct(host, rightOf(action), paused)) return '';
      return `<button type="button" class="hu-btn sm ${isDanger(action, target) ? 'danger' : 'tonal'}${extraClass ? ' ' + extraClass : ''}" data-hact="${esc(action)}" data-htarget="${esc(target || '')}">${esc(text || actionLabel(action))}</button>`;
    },
    bar: (html) => (html ? `<div class="hu-acts">${html}</div>` : ''),
  };
}

/** Die Dienste (GET /units) mit Tasten: laufende (Neustart, Stopp, Logs), nicht laufende (Start, Logs) */
export function unitsHtml(resp, esc, act) {
  const u = resp && resp.units;
  if (!u) return noteP(resp && resp.state === 'error' ? errText(resp.error) : t('hostinfo.stLoading'), esc);
  const items = (u.units || []).map((x) => {
    const on = x.active === 'active';
    const acts = on ? act.btn('service.restart', x.unit, t('hostact.bRestart')) + act.btn('service.stop', x.unit, t('hostact.bStop')) : act.btn('service.start', x.unit, t('hostact.bStart'));
    return { name: x.unit, detail: `${x.active}/${x.sub}${x.description ? ' · ' + x.description : ''}`, off: !on, actions: act.bar(acts + act.btn('logs.unit', x.unit, t('hostact.bLogs'))) };
  });
  return callout(t('hostact.unitsSum', { n: u.count }), esc, 'info') + (u.truncated ? noteP(t('hostinfo.truncated', { n: (u.units || []).length }), esc) : '') + card('', listBlock(items, esc), esc);
}

/* ---------- Aufrufe ---------- */
export const prepareAction = (api, hostId, action, target = null, lines = null) => api(`/api/hosts/${hostId}/actions/prepare`, { method: 'POST', body: { action, target, lines } });
export const runAction = (api, hostId, conf, typed = null, full = null, acknowledged = null) => api(`/api/hosts/${hostId}/actions/run`, { method: 'POST', body: { token: conf.token, action: conf.action, target: conf.target || null, typed, ...(acknowledged === true ? { acknowledged: true } : {}), ...(full === true ? { full: true } : {}) } });
export const fetchJob = (api, hostId, job) => api(`/api/hosts/${hostId}/actions/${job}`);
export const fetchAudit = (api, hostId, limit = 30) => api(`/api/hosts/${hostId}/audit?limit=${limit}`);
export const fetchUnits = (api, hostId, fresh = false) => api(`/api/hosts/${hostId}/units${fresh ? '?fresh=1' : ''}`);
export const setControl = (api, hostId, level) => api(`/api/hosts/${hostId}/agent/control`, { method: 'PUT', body: { level } });
export const setPause = (api, paused) => api('/api/host-actions/pause', { method: 'PUT', body: { paused } });
export const fetchJobs = (api) => api('/api/host-jobs');
