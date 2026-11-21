/* =====================================================================
 * Dateien-App (docs/FILES.md): die Logik ohne DOM, damit Desktop, Handy und die Tests sie teilen.
 *  - Orte: der gemeinsame Ordner des Servers ("local", nur lesen), jede SSH-Verbindung (SFTP mit den gespeicherten Zugangsdaten; Hosts mit mehreren SSH-Verbindungen bieten eine Auswahl)
 *    und "<Host> (Root)" fuer Hosts, deren Agent Dateien als root erlaubt (Wort files-root, Stufe "full", Agent ab 0.8.0, online); dort braucht auch Lesen eine Bestaetigung
 *  - Pfade (auf Hosts absolut "/a/b", im gemeinsamen Ordner relativ "a/b"), Namen, Eintraege, Anzeigetexte
 *  - Fehlertexte nach Code (der Server schickt {error, code}), Oeffnen eines Orts mit Passwort- und Host-Schluessel-Abfrage (nie still)
 *  - die Aufrufe: lesen, hochladen, Ordner, umbenennen, loeschen, kopieren, verschieben, Auftraege abfragen und abbrechen
 *  - die Saetze der Bestaetigungen: Host, Pfad und Anzahl stehen immer drin
 * Alles, was vom Server oder Ziel kommt (Namen, Pfade, Text), wird nie als HTML eingesetzt: die Oberflaeche setzt es mit textContent.
 * ===================================================================== */
import { t, tp, tk, fmtNumber, fmtDate } from './i18n.js';
import { loginUser, loginSaved } from './hostlogin.js';
import { confirmView, auditConfirmText } from './confirm.js';

export const LOCAL_ID = 'local';
export const MAX_NAME_BYTES = 255;

const fmtNum = (n, d = 1) => fmtNumber(n, d);
export const fileSize = (b) => (b < 1024 ? fmtNum(b, 0) + ' B' : b < 1024 ** 2 ? fmtNum(b / 1024, b < 10240 ? 1 : 0) + ' KiB' : b < 1024 ** 3 ? fmtNum(b / 1024 ** 2) + ' MiB' : fmtNum(b / 1024 ** 3) + ' GiB');
export const fmtModified = (iso) => fmtDate(iso, { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });

/* ---------- Pfade ---------- */
export const isLocal = (place) => !!place && place.kind === 'local';
export const isRoot = (place) => !!place && place.kind === 'root';
/** Der Wurzelpfad eines Orts: auf Hosts "/", im gemeinsamen Ordner leer */
export const rootOf = (place) => (isLocal(place) ? '' : '/');

export function parentOf(path, place) {
  const parts = String(path || '').split('/').filter(Boolean);
  parts.pop();
  return isLocal(place) ? parts.join('/') : '/' + parts.join('/');
}
export const nameOf = (path) => String(path || '').split('/').filter(Boolean).pop() || '';
export function joinPath(dir, name, place) {
  if (isLocal(place)) return dir ? dir + '/' + name : name;
  return dir === '/' || !dir ? '/' + name : dir + '/' + name;
}
/** Die Teile des Pfads als Brotkruemel: die Wurzel zuerst, jedes mit seinem vollen Pfad */
export function crumbsOf(path, place) {
  const parts = String(path || '').split('/').filter(Boolean);
  const out = [{ label: isLocal(place) ? t('desktopFiles.root') : '/', path: rootOf(place), root: true }];
  parts.forEach((p, i) => out.push({ label: p, path: (isLocal(place) ? '' : '/') + parts.slice(0, i + 1).join('/') }));
  return out;
}
/** Ein neuer Name wie am Server (ValidName): einfach, nicht leer, nicht "." oder "..", ohne "/" und Steuerzeichen, hoechstens 255 Byte */
export function validName(name) {
  if (typeof name !== 'string' || !name.trim() || name === '.' || name === '..' || name.includes('/') || /[\u0000-\u001f\u007f]/.test(name)) return false;
  return new TextEncoder().encode(name).length <= MAX_NAME_BYTES;
}

/* ---------- Orte ---------- */
const ROOT_MIN = [0, 8, 0];
/** Bietet der Agent dieses Hosts Dateien als root an? Online, Stufe "full", das Wort files-root erlaubt, Agent ab 0.8.0. Pause und Rest prueft der Server bei jeder Anfrage. */
export function rootAvailable(agent) {
  if (!agent || !agent.online || agent.control !== 'full' || !Array.isArray(agent.allow) || !agent.allow.includes('files-root')) return false;
  const m = /^(\d+)\.(\d+)\.(\d+)/.exec(String(agent.version || ''));
  if (!m) return false;
  for (let i = 0; i < 3; i++) { const a = Number(m[i + 1]); if (a !== ROOT_MIN[i]) return a > ROOT_MIN[i]; }
  return true;
}

/**
 * Aus den Verbindungen und Hosts die Orte: SSH-Verbindungen (Art "terminal") sind Dateiorte, RDP und VNC nicht. Gruppiert nach Host; hat ein Host mehrere SSH-Verbindungen, bekommt
 * jede die Beschriftung "Host · Verbindung", sonst heisst der Ort wie der Host. Dazu je Host mit freigegebenem Agenten der Ort "Host (Root)" (seine Kennung ist die des Hosts).
 * Hosts ohne SSH-Verbindung und ohne Root-Zugang kommen nicht vor.
 */
export function buildPlaces({ connections = [], hosts = [], info = {} } = {}) {
  const ssh = connections.filter((c) => c && c.kind === 'terminal');
  const byHost = new Map();
  for (const h of hosts) byHost.set(h.id, { id: h.id, name: h.name, places: [], root: rootAvailable(h.agent) });
  for (const c of ssh) {
    if (!byHost.has(c.hostId)) byHost.set(c.hostId, { id: c.hostId, name: c.name, places: [] });
    byHost.get(c.hostId).places.push(c);
  }
  const groups = [];
  for (const g of byHost.values()) {
    if (!g.places.length && !g.root) continue;
    const many = g.places.length > 1;
    const places = g.places.map((c) => ({
      id: c.id, kind: 'ssh', name: c.name, host: g.name, user: loginUser(c) || '', hasPassword: loginSaved(c), address: `${loginUser(c) ? loginUser(c) + '@' : ''}${c.host}`,
      label: many ? `${g.name} · ${c.name}` : g.name,
    }));
    if (g.root) places.push({ id: g.id, kind: 'root', name: g.name, host: `${g.name} (Root)`, user: 'root', hasPassword: true, address: t('files.rootAddress'), label: `${g.name} (Root)` });
    groups.push({ id: g.id, name: g.name, places });
  }
  const flat = groups.flatMap((g) => g.places);
  return { local: info.local ? { id: LOCAL_ID, kind: 'local', name: t('files.local'), label: t('files.local'), host: t('files.local') } : null, groups, flat };
}
export const findPlace = (places, id) => (id === LOCAL_ID ? places.local : places.flat.find((p) => p.id === id)) || null;

/* ---------- Eintraege ---------- */
/** Alles in eine Form: { name, path, kind: file|directory|link|other, size, modified, openable, resolved?, mode?, items? } */
export function normalizeListing(place, data) {
  const entries = (data.entries || []).map((e) => (isLocal(place)
    ? { name: e.name, path: e.path, kind: e.isDirectory ? 'directory' : 'file', size: e.size ?? 0, modified: e.modified, openable: !!e.isDirectory, items: e.items }
    : { name: e.name, path: e.path, kind: e.kind, size: e.size ?? 0, modified: e.modified, openable: !!e.openable, resolved: e.resolved, mode: e.mode }));
  return { path: data.path || '', entries, truncated: !!data.truncated, hidden: data.hidden || 0 };
}
/** Die zweite Zeile eines Eintrags: Groesse (Datei), Anzahl (Ordner im gemeinsamen Ordner), Link und Ziel */
export function entryMeta(e) {
  const parts = [];
  if (e.kind === 'file') parts.push(fileSize(e.size));
  else if (e.kind === 'directory' && e.items != null) parts.push(tp('desktopFiles.entries', e.items, { count: e.items >= 999 ? '999+' : e.items }));
  else if (e.kind === 'link') parts.push(e.resolved === 'directory' ? t('files.linkToFolder') : e.resolved === 'file' ? t('files.linkToFile') : t('files.linkBroken'));
  else if (e.kind === 'other') parts.push(t('files.special'));
  const d = e.modified ? fmtModified(e.modified) : '';
  if (d) parts.push(d);
  return parts.join(' · ');
}
/** Was sich ansehen, herunterladen, kopieren laesst: Dateien und Links auf Dateien (Sonderdateien und kaputte Links nicht) */
export const isFileLike = (e) => e.kind === 'file' || (e.kind === 'link' && e.resolved === 'file');

/** Auswahl: eine Menge von Pfaden */
export class Selection {
  constructor() { this.set = new Set(); }
  has(p) { return this.set.has(p); }
  toggle(p) { if (this.set.has(p)) this.set.delete(p); else this.set.add(p); return this.set.has(p); }
  only(p) { this.set = new Set([p]); }
  clear() { this.set = new Set(); }
  selectAll(entries) { this.set = new Set(entries.map((e) => e.path)); }
  get size() { return this.set.size; }
  paths() { return [...this.set]; }
  /** Die ausgewaehlten Eintraege in der Reihenfolge der Liste; was nicht mehr in der Liste steht, faellt aus der Auswahl */
  entries(entries) { const e = entries.filter((x) => this.set.has(x.path)); this.set = new Set(e.map((x) => x.path)); return e; }
}

/* ---------- Fehler ---------- */
export const ERR_KEYS = {
  not_found: tk('files.err.not_found'), denied: tk('files.err.denied'), exists: tk('files.err.exists'), not_empty: tk('files.err.not_empty'),
  not_a_directory: tk('files.err.not_a_directory'), not_a_file: tk('files.err.not_a_file'), connection_lost: tk('files.err.connection_lost'), failed: tk('files.err.failed'),
  too_many_items: tk('files.err.too_many_items'), too_deep: tk('files.err.too_deep'), too_large: tk('files.err.too_large'), into_itself: tk('files.err.into_itself'),
  same_place: tk('files.err.same_place'), type_mismatch: tk('files.err.type_mismatch'), target_is_link: tk('files.err.target_is_link'), root_forbidden: tk('files.err.root_forbidden'),
  invalid_name: tk('files.err.invalid_name'), invalid_path: tk('files.err.invalid_path'), invalid_request: tk('files.err.invalid_request'),
  not_text: tk('files.err.not_text'), write_off: tk('files.err.write_off'), confirmation_invalid: tk('files.err.confirmation_invalid'), confirmation_expired: tk('files.err.confirmation_expired'),
  confirmation_mismatch: tk('files.err.confirmation_mismatch'), confirm_required: tk('files.err.confirm_required'), typed_mismatch: tk('files.err.typed_mismatch'), too_many_pending: tk('files.err.too_many_pending'), audit_failed: tk('files.err.audit_failed'),
  connection_not_found: tk('files.err.connection_not_found'), not_ssh: tk('files.err.not_ssh'), target_not_allowed: tk('files.err.target_not_allowed'),
  password_required: tk('files.err.password_required'), password_unreadable: tk('files.err.password_unreadable'), logon_failed: tk('files.err.logon_failed'),
  unreachable: tk('files.err.unreachable'), timeout: tk('files.err.timeout'), sftp_unavailable: tk('files.err.sftp_unavailable'),
  host_key_new: tk('files.err.host_key_new'), host_key_changed: tk('files.err.host_key_changed'), too_many_sessions: tk('files.err.too_many_sessions'),
  rate_limited: tk('files.err.rate_limited'), busy: tk('files.err.busy'), job_not_found: tk('files.err.job_not_found'), cancelled: tk('files.err.cancelled'),
  incomplete: tk('files.err.incomplete'),
  root_off: tk('files.err.root_off'), root_not_permitted: tk('files.err.root_not_permitted'), root_offline: tk('files.err.root_offline'), root_outdated: tk('files.err.root_outdated'),
  root_paused: tk('files.err.root_paused'), root_helper: tk('files.err.root_helper'), protected_path: tk('files.err.protected_path'),
};
/** Fehlercode (vom Server) in der Sprache der Oberflaeche; unbekannte Codes werden allgemein gemeldet, nie roh gezeigt */
let errorHint = () => '';
/** Die Seite sagt, welcher Zusatz zu einem Fehlercode passt (heute: bei "denied" der Hinweis auf den Root-Ort); fn(code) gibt Text oder '' */
export const setErrorHint = (fn) => { errorHint = fn || (() => ''); };
export const errText = (code) => { const hint = errorHint(code); return t(ERR_KEYS[code] || tk('files.err.failed')) + (hint ? ' ' + hint : ''); };
/**
 * Vor einem Download: fragt die Adresse ab und bricht nach den Kopfzeilen ab. Ein Download ueber einen Link, den der Server mit einem Fehler beantwortet, schlaegt im Browser still fehl
 * (oder speichert den Fehlertext als Datei); so bekommt der Nutzer den Grund. Gibt den Text des Fehlers oder null (los). Nur fuer Adressen ohne einmaliges Token. Ein Netzfehler hier zaehlt nicht: der Download selbst darf es versuchen.
 */
export async function downloadProblem(url, fetchFn = (...a) => fetch(...a)) {
  const ctl = new AbortController();
  try {
    const r = await fetchFn(url, { credentials: 'same-origin', signal: ctl.signal });
    if (r.ok) return null;
    let code = '';
    try { code = (await r.json()).code || ''; } catch { /* kein JSON: allgemeiner Text */ }
    return errText(code);
  } catch { return null; } finally { ctl.abort(); }
}
/** Der Text zu einer Ausnahme der Aufrufe: der Code wenn bekannt, sonst die Meldung des Servers oder des Netzes */
export const errorMessage = (e) => (e && e.code && ERR_KEYS[e.code] ? errText(e.code) : (e && e.message) || t('files.err.failed'));

/* ---------- Aufrufe ---------- */
const q = encodeURIComponent;
const body = (o) => ({ method: 'POST', body: o });

/**
 * Die Aufrufe der Dateien-App. api(path, {method, body}) ist die Hilfe der Seite (Sonderkopfzeile, Fehler mit code und data). Der Upload braucht einen Fortschritt und geht deshalb ueber
 * xhr (createXhr: () => XMLHttpRequest; in Tests ein Ersatz).
 */
export function makeFilesApi(api, { createXhr = () => new XMLHttpRequest() } = {}) {
  const local = (p) => isLocal(p);
  return {
    info: () => api('/api/files/info'),
    open: (conn, opts = {}) => api('/api/files/remote/open', body({ connection: conn, password: opts.password || undefined, acceptHostKey: opts.acceptHostKey || undefined, saveForHost: opts.saveForHost || undefined })),
    close: (conn) => api('/api/files/remote/close', body({ connection: conn })).catch(() => {}),
    list: (place, path) => (local(place) ? api('/api/files/list?path=' + q(path || '')) : api(`/api/files/remote/list?c=${place.id}&path=${q(path || '')}`)).then((d) => normalizeListing(place, d)),
    search: (path, text) => api('/api/files/search?path=' + q(path || '') + '&q=' + q(text)),
    /** Auf SSH-Orten ohne Token; ein Root-Ort liest nur mit der Bestaetigung (prepare "text" oder "download", dann das Token hier) */
    text: (place, path, token) => api(`/api/files/remote/text?c=${place.id}&path=${q(path)}` + (token ? '&token=' + q(token) : '')),
    downloadUrl: (place, path, token) => (local(place) ? '/api/files/download?path=' + q(path) : `/api/files/remote/download?c=${place.id}&path=${q(path)}` + (token ? '&token=' + q(token) : '')),
    /**
     * Schritt 1 jeder schreibenden Aktion: der Server prueft und beschreibt, was geschaehe (Host, Pfade, Anzahl, Namen, die ersetzt wuerden, ein Ordner mit Inhalt) und stellt eine Bestaetigung (token) aus, die 60 s gilt,
     * einmal, nur fuer diese Sitzung und genau diese Anfrage. action: upload, mkdir, rename, delete, copy, move (auf Root-Orten auch download und text). Die Antwort ist die Grundlage des Dialogs.
     */
    prepare: (action, place, { paths, dir, name, names, target } = {}) => api('/api/files/remote/prepare', body({ action, connection: place.id, paths, dir, name, names, target: target ? { connection: target.place.id, dir: target.dir } : undefined })),
    /** answer: was der Benutzer bestaetigt hat ({ acknowledged, typed }, confirm.js); der Server verlangt es je nach Stufe und lehnt sonst mit 400 confirm_required ab */
    mkdir: (place, dir, name, token, answer) => api('/api/files/remote/mkdir', body({ connection: place.id, dir, name, token, ...answer })),
    rename: (place, path, name, overwrite, token, answer) => api('/api/files/remote/rename', body({ connection: place.id, path, name, overwrite: !!overwrite, token, ...answer })),
    remove: (place, paths, recursive, token, answer) => api('/api/files/remote/delete', body({ connection: place.id, paths, recursive: !!recursive, token, ...answer })),
    copy: (place, paths, target, { move = false, overwrite = false, token, answer } = {}) => api('/api/files/remote/copy', body({ connection: place.id, paths, target: { connection: target.place.id, dir: target.dir }, move, overwrite, token, ...answer })),
    job: (id) => api('/api/files/remote/jobs/' + id),
    cancel: (id) => api(`/api/files/remote/jobs/${id}/cancel`, { method: 'POST' }).catch(() => {}),
    audit: (limit = 50) => api('/api/files/audit?limit=' + limit),
    /** Eine Datei hochladen (roher Koerper). Gibt { promise, abort } zurueck; onProgress(geladen, gesamt). Ein Fehler hat code und data wie bei api(). */
    upload(place, dir, file, { overwrite = false, token, answer, onProgress } = {}) {
      const xhr = createXhr();
      const promise = new Promise((resolve, reject) => {
        const url = `/api/files/remote/upload?c=${place.id}&dir=${q(dir)}&name=${q(file.name)}&token=${q(token || '')}${overwrite ? '&overwrite=1' : ''}${answer && answer.acknowledged ? '&acknowledged=1' : ''}${answer && answer.typed ? '&typed=' + q(answer.typed) : ''}`;
        xhr.open('POST', url);
        xhr.setRequestHeader('X-JulOS', '1');
        xhr.setRequestHeader('Content-Type', 'application/octet-stream');
        if (xhr.upload) xhr.upload.onprogress = (e) => { if (onProgress && e.lengthComputable) onProgress(e.loaded, e.total); };
        xhr.onload = () => {
          let data = null; try { data = JSON.parse(xhr.responseText); } catch { /* kein JSON */ }
          if (xhr.status >= 200 && xhr.status < 300) return resolve(data);
          const e = new Error((data && data.error) || t('files.err.failed')); e.status = xhr.status; e.code = data && data.code; e.data = data; reject(e);
        };
        xhr.onerror = () => { const e = new Error(t('files.err.connection_lost')); e.code = 'connection_lost'; reject(e); };
        xhr.onabort = () => { const e = new Error(t('files.err.cancelled')); e.code = 'cancelled'; reject(e); };
        xhr.send(file);
      });
      return { promise, abort: () => xhr.abort() };
    },
  };
}

/* ---------- Ort oeffnen: Passwort und Host-Schluessel nur mit ausdruecklicher Bestaetigung ---------- */
/** So heisst der Schluessel in der Bestaetigung (Algorithmus und Fingerabdruck, wie beim Terminal) */
export const keyId = (data) => `${data.algorithm} ${data.fingerprint}`;

/**
 * Oeffnet einen SSH-Ort. ask.password(place, retry, host) -> Text | { password, save } | null (null: abgebrochen), ask.hostKey(place, data, changed) -> true|false. Ein neuer oder geaenderter Schluessel
 * wird nie still angenommen; ein getipptes Passwort geht nur an diesen einen Aufruf und wird nicht gemerkt, ausser der Benutzer hakt "Fuer diesen Host speichern" an (nur wenn der Host schon einen
 * Benutzer hat, aber kein Passwort: host = { user, canSave }; der Server speichert es erst nach gelungener Anmeldung). Hat der Host einen Login mit Passwort, kommt die Frage gar nicht erst.
 * Gibt { home, hostKey } zurueck oder wirft den Fehler (code 'cancelled' bei Abbruch).
 */
export async function openPlace(filesApi, place, ask) {
  let password = null, accept = null, wrong = false, save = false, host = null;
  for (let round = 0; round < 5; round++) {
    try {
      return await filesApi.open(place.id, save ? { password, acceptHostKey: accept, saveForHost: true } : { password, acceptHostKey: accept });
    } catch (e) {
      if (e.code === 'password_required' || e.code === 'password_unreadable' || e.code === 'logon_failed') {
        wrong = e.code === 'logon_failed';
        if (e.code === 'password_required' && e.data && e.data.user) host = { user: e.data.user, canSave: !!e.data.canSave };
        const answer = await ask.password(place, wrong, host);
        const typed = answer && typeof answer === 'object' ? answer.password : answer;
        if (typed === null || typed === undefined) throw cancelled();
        password = typed; save = !!(host && host.canSave && answer && typeof answer === 'object' && answer.save);
        continue;
      }
      if ((e.code === 'host_key_new' || e.code === 'host_key_changed') && e.data && e.data.fingerprint) {
        const ok = await ask.hostKey(place, e.data, e.code === 'host_key_changed');
        if (!ok) throw cancelled();
        accept = keyId(e.data);
        continue;
      }
      throw e;
    }
  }
  throw Object.assign(new Error(errText('failed')), { code: 'failed' });
}
const cancelled = () => Object.assign(new Error(errText('cancelled')), { code: 'cancelled' });
export const isCancelled = (e) => !!e && e.code === 'cancelled';

/* ---------- Auftraege (kopieren, verschieben, loeschen) ---------- */
export const jobFinal = (job) => !!job && job.state !== 'running';
export const jobPercent = (job) => {
  if (!job) return null;
  if (job.bytesTotal > 0) return Math.min(100, Math.round((job.bytes / job.bytesTotal) * 100));
  return job.total > 0 ? Math.min(100, Math.round((job.done / job.total) * 100)) : null;
};

/**
 * Fragt einen Auftrag ab, bis er fertig ist. onProgress(job) bei jedem Stand. Gibt { promise, cancel } zurueck; cancel() bittet den Server abzubrechen (der Auftrag endet dann als cancelled).
 * sleep ist austauschbar (Tests). Verliert die Seite die Verbindung, wird weiter gefragt (der Auftrag laeuft auf dem Server weiter).
 */
export function watchJob(filesApi, id, { onProgress = () => {}, interval = 700, sleep = (ms) => new Promise((r) => setTimeout(r, ms)), maxErrors = 5 } = {}) {
  let stopped = false, errors = 0;
  const promise = (async () => {
    for (;;) {
      let job;
      try { job = await filesApi.job(id); errors = 0; }
      catch (e) {
        if (e.status === 404 || ++errors >= maxErrors) throw e;
        await sleep(interval); continue;
      }
      onProgress(job);
      if (jobFinal(job) || stopped) return job;
      await sleep(interval);
    }
  })();
  return { promise, cancel: () => filesApi.cancel(id), stop: () => { stopped = true; } };
}

/** Der Satz zum Ergebnis eines fertigen Auftrags: was geschah, was uebersprungen wurde */
export function jobSummary(job, action) {
  if (job.state === 'cancelled') return t('files.job.cancelled');
  if (job.state === 'failed') return t('files.job.failed', { error: errText(job.error) });
  const r = job.result || {};
  const n = (r.files || 0) + (r.directories || 0);
  const base = t(action === 'delete' ? tk('files.job.doneDelete') : action === 'move' ? tk('files.job.doneMove') : tk('files.job.doneCopy'), { items: tp('files.items', n) });
  const extra = [];
  if (job.skipped) extra.push(tp('files.job.skippedLinks', job.skipped));
  if (job.skippedExisting) extra.push(tp('files.job.skippedExisting', job.skippedExisting));
  return extra.length ? base + ' ' + extra.join(' ') : base;
}

/* ---------- Die Saetze der Bestaetigungen: alles kommt vom Server (prepare), die Seite zeigt es nur ---------- */
const shortPath = (p, max = 70) => (p.length <= max ? p : '…' + p.slice(-(max - 1)));
export const pathsText = (paths) => (paths.length === 1 ? shortPath(paths[0]) : `${shortPath(paths[0], 50)} ${t('files.andMore', { n: paths.length - 1 })}`);
export const countText = (plan) => {
  const parts = [];
  if (plan.files) parts.push(tp('files.nFiles', plan.files));
  if (plan.directories) parts.push(tp('files.nFolders', plan.directories));
  if (plan.links) parts.push(tp('files.nLinks', plan.links));
  const s = parts.join(', ') || t('files.nothing');
  return plan.bytes ? `${s} · ${fileSize(plan.bytes)}` : s;
};
const namesText = (names) => names.slice(0, 5).join(', ') + (names.length > 5 ? ' …' : '');

/**
 * Die Stufe aus der Antwort des Servers (confirm.js): Routine zeigt nur Titel, Zeilen und Ausfuehren; gefaehrlich eine gelbe Warnung und, je nach Einstellung, Haekchen oder Namen; sehr gefaehrlich
 * dazu eine rote Zeile. Das Haekchen kommt nur vom Server (model.ack ist dann sein Text); nur der Editor fragt von sich aus (model.localAck: "seit dem Oeffnen geaendert").
 * Betrifft eine Aktion einen Root-Ort (prepare meldet root), steht das in jedem Dialog auffaellig dabei und die Taste ist rot: root darf fast alles, auch Dinge, die das System kaputt machen. Das Ziel eines Kopierens zaehlt mit.
 */
const finish = (model, prep) => {
  const v = confirmView(prep, prep && prep.host);
  const m = { ...model, level: v.level, mode: v.mode, typed: v.typed, levelWarn: v.warn, criticalWarn: v.criticalWarn, ack: v.needsCheck ? t('confirm.ack') : (model.localAck ? model.ack : null), danger: !!model.danger || v.danger };
  return prep && prep.root ? { ...m, root: true, rootNote: t('files.root.dialogNote'), danger: true } : m;
};

/** Lesen als root (Download und Textansicht): bestaetigt wird die Datei, sie wird danach vom Server als root gelesen und im Verlauf vermerkt */
export function readModel(action, prep) {
  const path = pathsText(prep.paths);
  return finish({
    title: t(action === 'download' ? tk('files.read.titleDownload') : tk('files.read.titleText')),
    sentence: t(action === 'download' ? tk('files.read.sentenceDownload') : tk('files.read.sentenceText'), { host: prep.host, path }),
    rows: [[t('files.rowHost'), prep.host], [t('files.rowPath'), path], [t('files.rowCount'), countText(prep)]],
    confirm: t(action === 'download' ? tk('files.read.confirmDownload') : tk('files.read.confirmText')),
    danger: false,
  }, prep);
}

/** Loeschen: nennt Host, Pfad und Anzahl (vom Server); ein Ordner mit Inhalt braucht den ausdruecklichen zweiten Schritt (recursive), einen Papierkorb gibt es nicht */
export function deleteModel(prep) {
  const needsAck = !!prep.nonEmpty;
  return finish({
    title: t('files.delete.title'),
    sentence: t('files.delete.sentence', { host: prep.host, path: pathsText(prep.paths) }),
    rows: [[t('files.rowHost'), prep.host], [t('files.rowPath'), pathsText(prep.paths)], [t('files.rowCount'), countText(prep)]],
    note: t('files.delete.noTrash'),
    recursive: needsAck,
    confirm: t('files.delete.confirm'),
    danger: true,
  }, prep);
}
/** Kopieren und Verschieben (auch auf einen anderen Host); ersetzt etwas, steht das ausdruecklich da und braucht ein Haekchen */
export function copyModel(action, prep) {
  const move = action === 'move';
  const conflicts = prep.conflicts || [];
  const to = prep.targetHost || prep.host;
  return finish({
    title: t(move ? tk('files.move.title') : tk('files.copy.title')),
    sentence: t(move ? tk('files.move.sentence') : tk('files.copy.sentence'), { from: prep.host, path: pathsText(prep.paths), to, dir: shortPath(prep.dir) }),
    rows: [[t('files.rowFrom'), prep.host], [t('files.rowPath'), pathsText(prep.paths)], [t('files.rowTo'), `${to}: ${shortPath(prep.dir)}`], [t('files.rowCount'), countText(prep)]],
    note: prep.targetHost ? t('files.copy.viaServer') : null,
    conflicts,
    warning: conflicts.length ? t('files.copy.overwriteWarning', { names: namesText(conflicts), n: conflicts.length }) : null,
    overwrite: conflicts.length > 0,
    confirm: t(move ? tk('files.move.confirm') : tk('files.copy.confirm')),
    danger: conflicts.length > 0 || move,
  }, prep);
}
/** Hochladen: ersetzt etwas, steht Host, Pfad und Anzahl da und es braucht ein Haekchen */
export function uploadModel(prep, files) {
  const total = files.reduce((n, f) => n + (f.size || 0), 0);
  const conflicts = prep.conflicts || [];
  return finish({
    title: t('files.upload.title'),
    sentence: t('files.upload.sentence', { host: prep.host, dir: shortPath(prep.dir), n: tp('files.nFiles', files.length) }),
    rows: [[t('files.rowHost'), prep.host], [t('files.rowPath'), shortPath(prep.dir)], [t('files.rowCount'), `${tp('files.nFiles', files.length)} · ${fileSize(total)}`]],
    warning: conflicts.length ? t('files.upload.overwriteWarning', { names: namesText(conflicts), n: conflicts.length }) : null,
    overwrite: conflicts.length > 0,
    confirm: t('files.upload.confirm'),
    danger: conflicts.length > 0,
  }, prep);
}
/** Umbenennen: ersetzt der neue Name eine Datei (der Server meldet es als Konflikt), braucht es ein Haekchen */
export function renameModel(prep) {
  const exists = (prep.conflicts || []).length > 0;
  return finish({
    title: t('files.rename.title'),
    sentence: t('files.rename.sentence', { host: prep.host, path: shortPath(prep.paths[0]), name: prep.name }),
    rows: [[t('files.rowHost'), prep.host], [t('files.rowPath'), shortPath(prep.paths[0])], [t('files.rowNewName'), prep.name]],
    warning: exists ? t('files.rename.overwriteWarning', { name: prep.name }) : null,
    overwrite: exists,
    confirm: t('files.rename.confirm'),
    danger: exists,
  }, prep);
}
export function mkdirModel(prep) {
  return finish({
    title: t('files.mkdir.title'),
    sentence: t('files.mkdir.sentence', { host: prep.host, dir: shortPath(prep.dir), name: prep.name }),
    rows: [[t('files.rowHost'), prep.host], [t('files.rowPath'), shortPath(prep.dir)], [t('files.rowNewName'), prep.name]],
    confirm: t('files.mkdir.confirm'), danger: false,
  }, prep);
}

/** Eine Datei speichern (Editor): ersetzt die Datei mit dem bearbeiteten Text. Hat sie sich auf dem Host seit dem Oeffnen geaendert (oder gibt es sie nicht mehr), steht das als Warnung da und es braucht ein Haekchen. */
export function saveModel(prep, name, bytes, changed) {
  const path = (prep.dir === '/' || !prep.dir ? '' : prep.dir) + '/' + name;
  return finish({
    title: t('files.save.title'),
    sentence: t('files.save.sentence', { host: prep.host, path: shortPath(path) }),
    rows: [[t('files.rowHost'), prep.host], [t('files.rowPath'), shortPath(path)], [t('files.rowCount'), fileSize(bytes)]],
    warning: changed ? t('files.save.changedWarning') : null,
    ack: changed ? t('files.save.changedAck') : null, localAck: !!changed,
    overwrite: (prep.conflicts || []).includes(name),
    confirm: t('files.save.confirm'),
    danger: !!changed,
  }, prep);
}
/** Eine leere Textdatei anlegen (ein Upload ohne Inhalt); gibt es den Namen schon, wuerde sie ersetzt und es braucht ein Haekchen */
export function newFileModel(prep, name) {
  const exists = (prep.conflicts || []).includes(name);
  return finish({
    title: t('files.newFile.title'),
    sentence: t('files.newFile.sentence', { host: prep.host, dir: shortPath(prep.dir), name }),
    rows: [[t('files.rowHost'), prep.host], [t('files.rowPath'), shortPath(prep.dir)], [t('files.rowNewName'), name]],
    warning: exists ? t('files.rename.overwriteWarning', { name }) : null,
    overwrite: exists,
    confirm: t('files.mkdir.confirm'),
    danger: exists,
  }, prep);
}

/* ---------- Verlauf ---------- */
const ACTION = { upload: tk('files.a.upload'), mkdir: tk('files.a.mkdir'), rename: tk('files.a.rename'), delete: tk('files.a.delete'), copy: tk('files.a.copy'), move: tk('files.a.move'), open: tk('files.a.open'), list: tk('files.a.list'), text: tk('files.a.text'), download: tk('files.a.download') };
const RESULT = { started: tk('files.r.started'), ok: tk('files.r.ok'), failed: tk('files.r.failed'), denied: tk('files.r.denied'), cancelled: tk('files.r.cancelled') };
export const auditLine = (e) => ({
  when: fmtDate(e.at, { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }),
  host: e.host, action: t(ACTION[e.action] || tk('files.a.unknown')), path: e.path || '', result: t(RESULT[e.result] || tk('files.r.failed')), failed: e.result !== 'ok',
  detail: e.detail ? (ERR_KEYS[e.detail] ? errText(e.detail) : auditConfirmText(e.detail) || (e.detail === 'overwrite' ? t('files.d.overwrite') : e.detail === 'recursive' ? t('files.d.recursive') : '')) : '',
  items: e.items != null ? tp('files.items', e.items) : '',
});
