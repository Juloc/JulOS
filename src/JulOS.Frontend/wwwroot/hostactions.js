/* =====================================================================
 * Host-Agent, Stufe 3: Aktionen (docs/HOST-AGENT.md). Reine Hilfen ohne DOM, damit Handy-Huelle, Desktop und der Dialog "Host-Details" dasselbe zeigen und Node sie testen kann.
 *  - wann welche Aktion angeboten wird (Stufe am Server, lokale Erlaubnis des Agenten, Pause, Version)
 *  - der Text der Bestaetigung (Host, Aktion, Ziel, Warnungen), das Tippen des Rechnernamens
 *  - Fortschritt, Ergebnis, Fehlertexte, Verlauf als HTML-Bausteine (jeder Wert vom Server oder Agenten geht durch esc)
 *  - die Aufrufe: Bestaetigung holen (prepare), ausfuehren (run), Auftrag abfragen
 * Server: POST /api/hosts/{id}/actions/prepare -> { token, expiresIn, action, target, targetName, host, typeToConfirm?, warnings, readOnly }
 *         POST /api/hosts/{id}/actions/run {token, action, target, typed} -> 202 { job } ; GET /api/hosts/{id}/actions/{job} -> { state, progress, done, total, result?, error? }
 * ===================================================================== */
import { t, tk } from './i18n.js';
import { dl, noteP, sumP, subH, isKit, when } from './hostinfo.js';

export const RIGHTS = ['updates', 'reboot', 'services', 'docker', 'logs'];
export const LEVELS = ['read', 'updates', 'full'];
/** Dieselbe Zuordnung wie AgentControl.Allows am Server: welche Aktionsarten eine Stufe erlaubt */
const LEVEL_RIGHTS = { read: [], updates: ['updates', 'logs'], full: RIGHTS };
export const MIN_ACTION_VERSION = [0, 6, 0];

export const ACTION_LABEL = {
  'updates.refresh': tk('hostact.a.updatesRefresh'), 'updates.install': tk('hostact.a.updatesInstall'),
  'power.reboot': tk('hostact.a.reboot'), 'power.poweroff': tk('hostact.a.poweroff'),
  'service.start': tk('hostact.a.serviceStart'), 'service.stop': tk('hostact.a.serviceStop'), 'service.restart': tk('hostact.a.serviceRestart'),
  'docker.start': tk('hostact.a.dockerStart'), 'docker.stop': tk('hostact.a.dockerStop'), 'docker.restart': tk('hostact.a.dockerRestart'), 'docker.pull': tk('hostact.a.dockerPull'),
  'logs.unit': tk('hostact.a.logsUnit'), 'logs.container': tk('hostact.a.logsContainer'),
};
export const actionLabel = (a) => t(ACTION_LABEL[a] || tk('hostact.a.unknown'));
/** Aktionen, die etwas anhalten oder abschalten: die Taste wird rot */
export const isDanger = (action, target) => action === 'power.reboot' || action === 'power.poweroff' || action === 'service.stop' || action === 'docker.stop' || (action === 'updates.install' && target === 'all');

const versionOk = (v) => {
  const m = /^(\d+)\.(\d+)\.(\d+)/.exec(String(v || ''));
  if (!m) return false;
  for (let i = 0; i < 3; i++) { const a = Number(m[i + 1]); if (a !== MIN_ACTION_VERSION[i]) return a > MIN_ACTION_VERSION[i]; }
  return true;
};

/** Kann der Agent die Host-Anfragen beantworten? Ein Host-Agent (kind host) und seit Agent 0.7.0 auch ein Desktop-Agent, der das Wort "host" meldet (ein Agent, zwei Faehigkeitsgruppen). */
const answersHostRequests = (a) => a.kind === 'host' || (Array.isArray(a.caps) && a.caps.includes('host'));

/** Zeigt die Oberflaeche diese Aktionsart fuer den Host an? Nur ein verbundener Host-Agent ab 0.6.0, dessen Stufe am Server UND dessen lokale Erlaubnis sie nennen, und nicht pausiert. */
export function canAct(host, right, paused = false) {
  const a = host && host.agent;
  return !!(a && a.online && answersHostRequests(a) && !paused && versionOk(a.version) && (LEVEL_RIGHTS[a.control] || []).includes(right) && Array.isArray(a.allow) && a.allow.includes(right));
}

/** Gibt es fuer diesen Host etwas zu schalten (Stufe, lokale Erlaubnis)? Fuer den Abschnitt "Aktionen" */
export const hasActionAgent = (host) => !!(host && host.agent && answersHostRequests(host.agent) && versionOk(host.agent.version));

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

const WARN = { julos_group: tk('hostact.warn.julosGroup'), kept_back: tk('hostact.warn.keptBack') };
/** Alles, was die Bestaetigung zeigt: Satz, Zeilen, Warnungen, Tippaufforderung */
export function confirmModel(conf) {
  const target = targetText(conf);
  return {
    title: actionLabel(conf.action),
    sentence: target ? t('hostact.sentenceTarget', { host: conf.host, action: actionLabel(conf.action), target }) : t('hostact.sentence', { host: conf.host, action: actionLabel(conf.action) }),
    rows: [[t('hostact.rowHost'), conf.host], [t('hostact.rowAction'), actionLabel(conf.action)], [t('hostact.rowTarget'), target]],
    warnings: (conf.warnings || []).map((w) => t(WARN[w] || tk('hostact.warn.unknown'))),
    typed: typedName(conf),
    danger: isDanger(conf.action, conf.target),
    expiresIn: conf.expiresIn,
  };
}

export function confirmHtml(conf, esc, kit = isKit()) {
  const m = confirmModel(conf);
  const warn = m.warnings.map((w) => noteP(w, esc, true, kit)).join('');
  const typed = m.typed ? `<label class="hact-typed">${esc(t('hostact.typePrompt', { name: m.typed }))}<input id="hostActTyped" type="text" autocomplete="off" autocapitalize="off" spellcheck="false" aria-label="${esc(t('hostact.typeLabel'))}"></label>` : '';
  return sumP(m.sentence, esc, m.danger, kit) + dl(m.rows, esc, kit) + warn + typed + noteP(t('hostact.expires', { n: m.expiresIn }), esc, false, kit);
}

/* ---------- Fehler ---------- */
const ERR = {
  host_not_found: tk('hostact.err.host_not_found'), no_agent: tk('hostact.err.no_agent'), unknown_action: tk('hostact.err.unknown_action'), invalid_target: tk('hostact.err.invalid_target'),
  paused: tk('hostact.err.paused'), control_off: tk('hostact.err.control_off'), offline: tk('hostact.err.offline'), agent_outdated: tk('hostact.err.agent_outdated'), unsupported: tk('hostact.err.unsupported'),
  agent_not_permitted: tk('hostact.err.agent_not_permitted'), not_permitted: tk('hostact.err.agent_not_permitted'), busy: tk('hostact.err.busy'), unknown_target: tk('hostact.err.unknown_target'),
  list_unavailable: tk('hostact.err.list_unavailable'), protected_unit: tk('hostact.err.protected_unit'), too_many: tk('hostact.err.too_many'), confirmation_invalid: tk('hostact.err.confirmation_invalid'),
  confirmation_expired: tk('hostact.err.confirmation_expired'), confirmation_mismatch: tk('hostact.err.confirmation_mismatch'), typed_mismatch: tk('hostact.err.typed_mismatch'),
  audit_failed: tk('hostact.err.audit_failed'), invalid_level: tk('hostact.err.invalid_level'), rate_limited: tk('hostact.err.rate_limited'), job_not_found: tk('hostact.err.job_not_found'),
  apt_locked: tk('hostact.err.apt_locked'), apt_failed: tk('hostact.err.apt_failed'), systemctl_failed: tk('hostact.err.systemctl_failed'), journal_failed: tk('hostact.err.journal_failed'),
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

const pre = (text, esc, kit) => (kit ? `<pre class="hi-pre m3-pre">${esc(text)}</pre>` : `<pre class="hi-pre">${esc(text)}</pre>`);

/** Der Stand eines Auftrags als HTML: laufend (Text und Balken), verloren, angenommen, fertig, fehlgeschlagen; Ausgabe und Logs nur als Text */
export function jobHtml(job, esc, kit = isKit()) {
  if (!job) return '';
  const label = actionLabel(job.action);
  if (job.state === 'running' || job.state === 'lost') {
    const pct = jobPercent(job);
    const bar = pct === null ? '<progress></progress>' : `<progress max="100" value="${pct}"></progress>`;
    return sumP((job.state === 'lost' ? t('hostact.stLost', { action: label }) : t('hostact.stRunning', { action: label })), esc, job.state === 'lost', kit) + bar
      + (job.progress ? noteP(job.progress + (pct !== null ? ` (${job.done}/${job.total})` : ''), esc, false, kit) : '');
  }
  const r = job.result || {};
  if (job.state === 'failed') return sumP(t('hostact.stFailed', { action: label }), esc, true, kit) + noteP(errText(job.error), esc, true, kit) + (r.output ? pre(r.output, esc, kit) : '');
  const parts = [];
  if (job.state === 'accepted') parts.push(sumP((job.action === 'power.poweroff' ? t('hostact.stPoweroff') : t('hostact.stReboot')), esc, false, kit));
  else parts.push(sumP(t('hostact.stDone', { action: label }), esc, false, kit));
  if (r.already) parts.push(noteP(t('hostact.already'), esc, false, kit));
  if (job.action === 'docker.pull') parts.push(noteP(r.updated ? t('hostact.pullUpdated') : t('hostact.pullCurrent'), esc, !!r.updated, kit));
  if (r.rebootRequired) parts.push(noteP(t('hostact.rebootHint'), esc, true, kit));
  if (Array.isArray(r.lines)) parts.push(r.lines.length ? pre(r.lines.join('\n'), esc, kit) : noteP(t('hostact.noLogs'), esc, false, kit));
  else if (r.output) parts.push(pre(r.output, esc, kit));
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
const levelOrDetail = (e) => (e.detail === 'no_answer' ? t('hostact.noAnswer') : e.detail);

export function auditHtml(resp, esc, kit = isKit()) {
  const list = (resp && resp.entries) || [];
  if (!list.length) return noteP(t('hostact.historyEmpty'), esc, false, kit);
  const rows = list.map((e) => { const l = auditLine(e); return [`${l.when} · ${l.who}`, `${l.text} — ${l.event}${l.detail ? ' (' + l.detail + ')' : ''}`, e.event === 'denied' || e.event === 'failed' ? 'warn' : '']; });
  return dl(rows, esc, kit);
}

/* ---------- Schalter "Aktionen" im Dialog ---------- */
/** Die Stufe am Server, die lokale Erlaubnis und der Pausenschalter eines Host-Agenten */
export function controlHtml(host, paused, esc, kit = isKit()) {
  const a = host.agent || {};
  const allow = (a.allow || []).length ? a.allow.join(', ') : t('hostact.allowNone');
  const opts = LEVELS.map((l) => `<option value="${l}"${a.control === l ? ' selected' : ''}>${esc(levelText(l))}</option>`).join('');
  const rows = kit
    ? `<div class="m3-card m3-rows"><div class="m3-row"><div class="rt">${esc(t('hostact.level'))}</div><div class="rc"><select id="hostActLevel" aria-label="${esc(t('hostact.level'))}">${opts}</select></div></div>`
      + `<div class="m3-row"><div class="rt">${esc(t('hostact.localAllow'))}</div><div class="rc"><span class="rv">${esc(allow)}</span></div></div></div>`
    : `<dl class="hi-dl"><dt>${esc(t('hostact.level'))}</dt><dd><select id="hostActLevel" aria-label="${esc(t('hostact.level'))}">${opts}</select></dd><dt>${esc(t('hostact.localAllow'))}</dt><dd>${esc(allow)}</dd></dl>`;
  const pause = `<label class="check hact-pause"><input id="hostActPause" type="checkbox"${paused ? ' checked' : ''}> ${esc(t('hostact.pause'))}</label>`;
  const hint = (a.allow || []).length ? '' : noteP(t('hostact.allowHint'), esc, false, kit);
  return subH(t('hostact.secActions'), esc, kit) + noteP(t('hostact.intro'), esc, false, kit) + rows + pause + hint;
}

/* ---------- Tasten in den Abschnitten ---------- */
/**
 * Das, was die Abschnitte brauchen, um Aktionstasten zu zeichnen: can(recht) und btn(aktion, ziel, text). Ohne Erlaubnis zeichnet btn nichts. Die Tasten tragen nur
 * die Aktion und das Ziel als data-Werte; geklickt wird ueber den Dialog (hostactions-dialog.js), der die Bestaetigung holt.
 */
export function makeAct(host, paused, esc, kit = isKit()) {
  const rightOf = (a) => (a.startsWith('updates') ? 'updates' : a.startsWith('power') ? 'reboot' : a.startsWith('service') ? 'services' : a.startsWith('docker') ? 'docker' : 'logs');
  return {
    can: (right) => canAct(host, right, paused),
    btn(action, target, text, extraClass = '') {
      if (!canAct(host, rightOf(action), paused)) return '';
      const cls = kit ? `m3-btn sm ${isDanger(action, target) ? 'danger' : 'tonal'}` : (isDanger(action, target) ? 'danger hi-act' : 'ghost hi-act');
      return `<button type="button" class="${cls}${extraClass ? ' ' + extraClass : ''}" data-hact="${esc(action)}" data-htarget="${esc(target || '')}">${esc(text || actionLabel(action))}</button>`;
    },
    bar: (html) => (html ? `<div class="hi-acts">${html}</div>` : ''),
  };
}

/** Die Dienste (GET /units) mit Tasten: laufende (Neustart, Stopp, Logs), nicht laufende (Start, Logs) */
export function unitsHtml(resp, esc, act, kit = isKit()) {
  const u = resp && resp.units;
  if (!u) return noteP(resp && resp.state === 'error' ? errText(resp.error) : t('hostinfo.stLoading'), esc, false, kit);
  const items = (u.units || []).map((x) => {
    const on = x.active === 'active';
    const acts = on ? act.btn('service.restart', x.unit, t('hostact.bRestart')) + act.btn('service.stop', x.unit, t('hostact.bStop')) : act.btn('service.start', x.unit, t('hostact.bStart'));
    return { name: x.unit, detail: `${x.active}/${x.sub}${x.description ? ' · ' + x.description : ''}`, off: !on, actions: act.bar(acts + act.btn('logs.unit', x.unit, t('hostact.bLogs'))) };
  });
  return sumP(t('hostact.unitsSum', { n: u.count }), esc, false, kit) + (u.truncated ? noteP(t('hostinfo.truncated', { n: (u.units || []).length }), esc, false, kit) : '') + itemsList(items, esc, kit);
}
// Die Liste mit Tasten unter jedem Eintrag ("actions" ist fertiges HTML der Tasten, nie Text des Agenten)
export function itemsList(items, esc, kit = isKit()) {
  return kit
    ? `<div class="m3-card m3-list">${items.map((i) => `<div class="m3-li static"><span class="txt"><span class="hl">${esc(i.name)}</span><span class="sl">${esc(i.detail)}</span>${i.actions || ''}</span></div>`).join('')}</div>`
    : `<ul class="hi-list">${items.map((i) => `<li${i.off ? ' class="off"' : ''}><b>${esc(i.name)}</b> <span>${esc(i.detail)}</span>${i.actions || ''}</li>`).join('')}</ul>`;
}

/* ---------- Aufrufe ---------- */
export const prepareAction = (api, hostId, action, target = null, lines = null) => api(`/api/hosts/${hostId}/actions/prepare`, { method: 'POST', body: { action, target, lines } });
export const runAction = (api, hostId, conf, typed = null) => api(`/api/hosts/${hostId}/actions/run`, { method: 'POST', body: { token: conf.token, action: conf.action, target: conf.target || null, typed } });
export const fetchJob = (api, hostId, job) => api(`/api/hosts/${hostId}/actions/${job}`);
export const fetchAudit = (api, hostId, limit = 30) => api(`/api/hosts/${hostId}/audit?limit=${limit}`);
export const fetchUnits = (api, hostId, fresh = false) => api(`/api/hosts/${hostId}/units${fresh ? '?fresh=1' : ''}`);
export const setControl = (api, hostId, level) => api(`/api/hosts/${hostId}/agent/control`, { method: 'PUT', body: { level } });
export const setPause = (api, paused) => api('/api/host-actions/pause', { method: 'PUT', body: { paused } });
