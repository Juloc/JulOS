/* =====================================================================
 * Hintergrund-Aufgaben auf Hosts (docs/HOST-AGENT.md, "Hintergrund-Aufgaben"): eine laufende Aktion (Updates installieren, Paketlisten, Reparieren, Neustart-Anfrage, Docker pull)
 * sperrt nichts. Nach "Ausfuehren" laeuft sie als Aufgabe: ein kleiner Eintrag in der Statusleiste (Desktop) bzw. eine Leiste am Handy, ein Klick oeffnet das Fortschritts-Panel
 * (Desktop: verschiebbares Fenster, Handy: Seite). Das Panel zu schliessen bricht nichts ab, die Aktion laeuft weiter. Mehrere Aufgaben (auch auf verschiedenen Hosts) sind sichtbar;
 * je Host schreibt nur eine zugleich (Server und Agent: busy). Beim Ende meldet die Oberflaeche das Ergebnis. Nach einem Neuladen der Seite findet GET /api/host-jobs die laufenden
 * und die letzte Stunde beendeten Aufgaben wieder (der Server kennt den Zustand, der Agent die letzten 20 Ergebnisse).
 *
 * Dieses Modul hat keine eigene Oberflaeche: der Speicher (createTaskStore) fragt den Server, die Hilfen liefern Texte und Markup (alles durch esc). Desktop (desktop.js) und
 * Handy (mobile.js) zeichnen damit Leiste und Panel.
 * ===================================================================== */
import { t, tp } from './i18n.js';
import { actionLabel, jobHtml, jobFinal, jobPercent, fetchJob, fetchJobs, errText, canAct } from './hostactions.js';

export const POLL_MS = 1500;
export const KEEP_FINISHED_MS = 60 * 60 * 1000;            // wie lange der Server beendete Aufgaben in der Liste haelt (und wir sie zeigen)
const STORE_KEY = 'pd.hosttasks.dismissed';

/** Ein Eintrag der Liste GET /api/host-jobs oder eine eben gestartete Aktion als Aufgabe. */
function fromJob(host, job, extra = {}) {
  return {
    job: job.job, host: { id: host.id, name: host.name }, action: job.action, target: job.target || null, targetName: job.targetName || null, state: job.state || 'running',
    progress: job.progress || '', done: job.done ?? null, total: job.total ?? null, tail: Array.isArray(job.tail) ? job.tail : [], error: job.error || null, result: job.result || null, full: !!job.full,
    startedAt: job.started ? Date.parse(job.started) : Date.now(), finishedAt: job.finished ? Date.parse(job.finished) : null, seen: false, ...extra,
  };
}

/**
 * Der Speicher der Aufgaben. api: wie in shell.js (wirft bei Fehlern, mit status und code). onChange(): etwas hat sich geaendert (zeichnen). onFinish(task): eine Aufgabe ist eben
 * fertig geworden (Benachrichtigung). Zeitgeber und Speicher sind einsetzbar (Tests).
 */
export function createTaskStore({ api, onChange = () => {}, onFinish = () => {}, setTimer = setTimeout, clearTimer = clearTimeout, storage = null, now = Date.now } = {}) {
  const store = storage || (typeof localStorage !== 'undefined' ? localStorage : null);
  const tasks = new Map();                                  // job -> Aufgabe
  const callbacks = new Map();                              // job -> onDone der Stelle, die gestartet hat
  let timer = null;
  let dismissed = new Set();
  try { dismissed = new Set(JSON.parse((store && store.getItem(STORE_KEY)) || '[]')); } catch { dismissed = new Set(); }
  const saveDismissed = () => { try { store && store.setItem(STORE_KEY, JSON.stringify([...dismissed].slice(-100))); } catch { /* egal */ } };

  const list = () => [...tasks.values()].sort((a, b) => b.startedAt - a.startedAt);
  const running = () => list().filter((x) => !jobFinal(x));
  const changed = () => { try { onChange(); } catch { /* die Oberflaeche darf den Speicher nicht stoeren */ } };

  function schedule() {
    if (timer || !running().length) return;
    timer = setTimer(poll, POLL_MS);
  }

  async function pollOne(task) {
    let job = null;
    try { job = await fetchJob(api, task.host.id, task.job); }
    catch (e) {
      if (e && e.status === 404) { mergeJob(task, { state: 'failed', error: 'job_not_found' }); }          // der Server kennt ihn nicht mehr (Neustart des Servers): nicht ewig "laeuft"
      return;
    }
    mergeJob(task, job);
  }

  function mergeJob(task, job) {
    const was = jobFinal(task);
    Object.assign(task, { state: job.state || task.state, progress: job.progress ?? task.progress, done: job.done ?? null, total: job.total ?? null, tail: Array.isArray(job.tail) ? job.tail : task.tail,
      error: job.error || null, result: job.result || task.result, full: !!job.full || task.full, finishedAt: job.finished ? Date.parse(job.finished) : task.finishedAt });
    if (!was && jobFinal(task)) {
      task.finishedAt = task.finishedAt || now();
      const cb = callbacks.get(task.job);
      callbacks.delete(task.job);
      try { cb && cb(task); } catch { /* Rueckruf der Aufrufstelle */ }
      try { onFinish(task); } catch { /* Benachrichtigung */ }
    }
  }

  async function poll() {
    timer = null;
    await Promise.all(running().map(pollOne));
    changed();
    schedule();
  }

  return {
    list, running,
    get: (job) => tasks.get(job) || null,
    /** Eine gerade gestartete Aktion (Antwort von run) wird zur Aufgabe. opts.onDone(task) kommt beim Ende. */
    add(host, started, conf, opts = {}) {
      const task = fromJob(host, { job: started.job, action: conf.action, target: conf.target, targetName: conf.targetName, state: started.state || 'running' }, { seen: false });
      tasks.set(task.job, task);
      if (opts.onDone) callbacks.set(task.job, opts.onDone);
      changed();
      schedule();
      return task;
    },
    /** Nach dem Laden der Seite: die laufenden und eben beendeten Aufgaben beim Server holen (auch die, die vor einem Neuladen begonnen haben). */
    async load() {
      let resp = null;
      try { resp = await fetchJobs(api); } catch { return []; }
      const added = [];
      for (const item of (resp && resp.jobs) || []) {
        const job = item.job;
        if (!job || !item.host || tasks.has(job.job) || dismissed.has(job.job)) continue;
        if (jobFinal(job) && job.finished && now() - Date.parse(job.finished) > KEEP_FINISHED_MS) continue;
        const task = fromJob({ id: item.host, name: item.hostName || '' }, job, { seen: jobFinal(job) });       // Beendetes ist schon gemeldet worden (oder wird es hier nicht mehr)
        tasks.set(task.job, task);
        added.push(task);
      }
      changed();
      schedule();
      return added;
    },
    /** Eine Aufgabe aus der Leiste nehmen (nur beendete); sie kommt nach einem Neuladen nicht wieder. */
    dismiss(job) {
      const task = tasks.get(job);
      if (!task || !jobFinal(task)) return false;
      tasks.delete(job);
      dismissed.add(job);
      saveDismissed();
      changed();
      return true;
    },
    dismissFinished() { for (const x of list()) if (jobFinal(x)) this.dismiss(x.job); },
    /** Das Ergebnis wurde angesehen (Panel offen). */
    markSeen(job) { const x = tasks.get(job); if (x) x.seen = true; },
    stop() { if (timer) clearTimer(timer); timer = null; },
    /** Abgemeldet: nichts behalten */
    clear() { if (timer) clearTimer(timer); timer = null; tasks.clear(); callbacks.clear(); changed(); },
    _poll: poll,
  };
}

/* ---------- Texte ---------- */
/** "Updates installieren auf pve: 12 von 63" (laeuft), "... fertig", "... fehlgeschlagen" */
export function chipText(task) {
  const base = { action: actionLabel(task.action), host: task.host.name };
  if (task.state === 'failed') return t('hosttask.chipFailed', base);
  if (jobFinal(task)) return t('hosttask.chipDone', base);
  if (task.state === 'lost') return t('hosttask.chipLost', base);
  if (task.total > 0 && Number.isFinite(task.done)) return t('hosttask.chipProgress', { ...base, done: task.done, total: task.total });
  return t('hosttask.chipRunning', base);
}

/** Kurzer Text der Leiste: eine Aufgabe als ihr Chip-Text, mehrere als Anzahl */
export function barText(tasks) {
  const run = tasks.filter((x) => !jobFinal(x));
  if (!tasks.length) return '';
  if (tasks.length === 1) return chipText(tasks[0]);
  return run.length ? tp('hosttask.barRunning', run.length) : tp('hosttask.barDone', tasks.length);
}

/** Der Fortschritt der Leiste in Prozent: null, wenn nichts Zaehlbares laeuft (dann eine Balkenanimation) */
export function barPercent(tasks) {
  const run = tasks.filter((x) => !jobFinal(x));
  if (!run.length) return null;
  const parts = run.map(jobPercent).filter((p) => p !== null);
  return parts.length === run.length ? Math.round(parts.reduce((a, b) => a + b, 0) / parts.length) : null;
}

/** Ton fuer die Leiste: bad, wenn etwas fehlschlug, sonst ok (fertig) oder info (laeuft) */
export const barTone = (tasks) => (tasks.some((x) => x.state === 'failed') ? 'bad' : tasks.some((x) => !jobFinal(x)) ? 'info' : 'ok');

/** Die Benachrichtigung beim Ende: Text und Ton */
export function finishNotice(task) {
  return { text: chipText(task), tone: task.state === 'failed' ? 'bad' : 'ok', detail: task.state === 'failed' ? errText(task.error) : '' };
}

/** Inhalt des Panels: Kopf (Host, wann), Zustand, Fortschritt, Live-Ausgabe oder Ergebnis; Reparieren nur, wenn Stufe und Wort es erlauben (host: Eintrag aus state.hosts) */
export function panelHtml(task, esc, host = null) {
  const target = task.targetName || (task.target === 'all' ? t('hostact.allPackages') : task.target || '');
  const head = `<p class="hu-note tp-head">${esc(target ? t('hosttask.panelHeadTarget', { host: task.host.name, target }) : t('hosttask.panelHead', { host: task.host.name }))}</p>`;
  const repair = !!(host && canAct(host, 'updates'));
  const note = task.state === 'running' || task.state === 'lost' ? `<p class="hu-note tp-hint">${esc(t('hosttask.closeHint'))}</p>` : '';
  return head + jobHtml({ ...task, job: task.job, result: task.result }, esc, { repair }) + note;
}
