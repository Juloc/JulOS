import { t } from './i18n.js';
import { confirmReady } from './confirm.js';
import { confirmHtml, confirmModel, prepareAction, runAction, fetchJob, jobHtml, jobFinal, errText, actionLabel } from './hostactions.js';
import { showDialog, focusField, dialogWindowsAvailable } from './desktop-window.js';

/* =====================================================================
 * Der Bestaetigungsdialog fuer Aktionen auf Hosts (docs/HOST-AGENT.md, Stufe 3). Jede Aktion geht durch ihn: erst nennt der Server, was genau geschehen wuerde (Host, Aktion, Ziel;
 * je nach Stufe nur "Ausfuehren", eine Warnung mit Haekchen oder, wie eingestellt, der Rechnername; der Server verlangt es), dann fuehrt ein zweiter Aufruf aus. Danach zeigt der Dialog Fortschritt und Ergebnis.
 * Der Dialog ist nur die Bestaetigung (ein kleines Blatt). Nach "Ausfuehren" schliesst er sich und die Aktion laeuft als Hintergrund-Aufgabe (hosttasks.js): nichts bleibt gesperrt,
 * Fortschritt und Ergebnis stehen im Panel der Aufgabe. Nur Aktionen, die lesen (Logs), zeigen ihr Ergebnis hier im Dialog (kurz, und das Ergebnis ist der Zweck).
 * Das Schliessen bricht eine laufende Aktion nie ab; der Server haelt das Ergebnis. Markup: index.html (#hostActDlg). Die Logik des Textes steht in hostactions.js.
 * Auf dem Desktop ist jede Bestaetigung ein eigenes Fenster (Klon des Markups, mehrere gleichzeitig moeglich; der Titel nennt Aktion und Host); Schliessen, Escape, Abmelden und der
 * Schliessen-Knopf des Fensters schliessen das <dialog> ohne Bestaetigung (nichts wird gesendet), "Ausfuehren" gilt nur durch einen Klick. Auf dem Handy bleibt es das eine Blatt.
 * ===================================================================== */
const $ = (s) => document.querySelector(s);
const POLL_MS = 1500;

/**
 * @param {{api: Function, esc: Function, tasks?: object, onStarted?: Function}} o   tasks: der Speicher der Hintergrund-Aufgaben (hosttasks.js); ohne ihn laeuft alles im Dialog wie frueher; onStarted(task): eine Aufgabe wurde gestartet
 * @returns {(host: object, action: string, target?: string|null, opts?: {lines?: number, onDone?: Function}) => Promise<void>} oeffnet den Dialog; das Versprechen endet, wenn der Dialog geschlossen wird
 */
export function initHostActionDialog({ api, esc, tasks = null, onStarted = null }) {
  const tpl = $('#hostActDlg');
  let session = 0;                 // jede Oeffnung hat eine Nummer: ein alter Abruf zeichnet nichts mehr in einen neuen Dialog (das Handy hat nur ein Blatt)

  return function open(host, action, target = null, opts = {}) {
    const my = ++session;
    const asWindow = dialogWindowsAvailable();
    const dlg = asWindow ? tpl.cloneNode(true) : tpl;             // Desktop: jede Bestaetigung ihr eigenes Fenster, auch mehrere gleichzeitig
    const q = asWindow ? (sel) => dlg.querySelector(sel) : $;
    let timers = [], closed = false;
    const clear = () => { for (const x of timers) clearTimeout(x); timers = []; };
    const body = (html) => { q('#hostActBody').innerHTML = html; };
    const msg = (text) => { q('#hostActMsg').textContent = text || ''; };
    const confirmBtn = () => q('#hostActConfirm');
    const alive = () => !closed && my === session;
    dlg.addEventListener('close', () => { closed = true; clear(); }, { once: true });
    if (asWindow) q('#hostActClose').addEventListener('click', () => dlg.close());     // der Klon hat nicht den Zuhoerer, den app.js beim Start an die Originale haengt
    q('#hostActTitle').textContent = host.name;
    body(`<p class="hu-note">${esc(t('hostinfo.stLoading'))}</p>`);
    msg('');
    confirmBtn().hidden = true;
    q('#hostActClose').textContent = t('common.cancel');
    // Der Titel des Fensters nennt Aktion und Host, damit ein spaeter wiedergefundenes Fenster eindeutig ist
    const win = showDialog(dlg, { width: 560, disposable: asWindow, titleFrom: q('#hostActTitle'), mirror: false, title: `${actionLabel(action)} · ${host.name}` });

    return new Promise((resolve) => {
      dlg.addEventListener('close', () => resolve(), { once: true });
      (async () => {
        let conf;
        try { conf = await prepareAction(api, host.id, action, target, opts.lines || null); }
        catch (e) { if (alive()) { body(''); msg(e.code ? errText(e.code) : e.message); q('#hostActClose').textContent = t('common.close'); } return; }
        if (!alive()) return;
        const model = confirmModel(conf);
        q('#hostActTitle').textContent = model.title;
        if (win) { win.setTitle(`${model.title} · ${conf.host}`); if (model.danger) win.window.el.classList.add('dw-danger'); }     // gefaehrlich und sehr gefaehrlich: rote Titelleiste
        body(confirmHtml(conf, esc));
        const btn = confirmBtn();
        btn.hidden = false;
        btn.className = model.danger ? 'danger' : 'primary';
        btn.textContent = t('hostact.confirm');
        const input = q('#hostActTyped'), ack = q('#hostActAck');
        const answer = () => ({ checked: !!(ack && ack.checked), typed: input ? input.value : '' });
        const refresh = () => { btn.disabled = !confirmReady(model, answer()); };
        if (input) { input.addEventListener('input', refresh); focusField(dlg, input); }     // nur wenn der Fokus noch hier liegt: wer inzwischen woanders tippt, wird nicht weggeholt
        if (ack) ack.addEventListener('change', refresh);
        refresh();
        // Die Bestaetigung gilt nur kurz; danach muss der Dialog neu geoeffnet werden (der Server lehnt sie ohnehin ab)
        timers.push(setTimeout(() => { if (alive() && !btn.hidden) { btn.disabled = true; msg(errText('confirmation_expired')); } }, Math.max(5, conf.expiresIn) * 1000));

        // "full-upgrade" ist eine ausdrueckliche Wahl (Standard aus); sie zeigt, was ein vollstaendiges Upgrade entfernen wuerde
        const fullBox = q('#hostActFull');
        if (fullBox) fullBox.addEventListener('change', () => { const r = q('#hostActRemovals'); if (r) r.hidden = !fullBox.checked; });

        btn.onclick = async () => {
          if (!confirmReady(model, answer())) return;
          btn.disabled = true; msg('');
          let started;
          const full = !!(fullBox && fullBox.checked);
          try { started = await runAction(api, host.id, conf, model.typed ? input.value : null, full, model.needsCheck ? !!ack.checked : null); }
          catch (e) { if (alive()) { msg(e.code ? errText(e.code) : e.message); if (e.code === 'typed_mismatch' || e.code === 'confirm_required') btn.disabled = false; else q('#hostActClose').textContent = t('common.close'); } return; }
          if (!alive()) return;
          clear();
          if (tasks && !conf.readOnly) {           // Hintergrund-Aufgabe: der Dialog schliesst sich, nichts bleibt gesperrt
            const task = tasks.add(host, started, conf, { onDone: opts.onDone });
            if (onStarted) onStarted(task);
            if (opts.onStarted) opts.onStarted(task);
            dlg.close();
            return;
          }
          btn.hidden = true;
          q('#hostActClose').textContent = t('common.close');
          msg(t('hostact.closeHint'));
          body(jobHtml({ action: conf.action, state: 'running' }, esc));
          const poll = async () => {
            if (!alive()) return;
            let job = null;
            try { job = await fetchJob(api, host.id, started.job); } catch (e) { if (alive() && e.status === 404) { msg(errText('job_not_found')); return; } }
            if (!alive()) return;
            if (job) body(jobHtml(job, esc));
            if (jobFinal(job)) { msg(''); if (opts.onDone) opts.onDone(job); return; }
            timers.push(setTimeout(poll, POLL_MS));
          };
          poll();
        };
      })();
    });
  };
}
