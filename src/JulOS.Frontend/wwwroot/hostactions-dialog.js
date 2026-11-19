import { t } from './i18n.js';
import { confirmHtml, confirmModel, typedOk, typedName, prepareAction, runAction, fetchJob, jobHtml, jobFinal, errText } from './hostactions.js';

/* =====================================================================
 * Der Bestaetigungsdialog fuer Aktionen auf Hosts (docs/HOST-AGENT.md, Stufe 3). Jede Aktion geht durch ihn: erst nennt der Server, was genau geschehen wuerde (Host, Aktion, Ziel;
 * bei Neustart, Ausschalten und "alle Updates" muss der Rechnername getippt werden), dann fuehrt ein zweiter Aufruf aus. Danach zeigt der Dialog Fortschritt und Ergebnis.
 * Das Schliessen des Dialogs bricht eine laufende Aktion nicht ab; der Server haelt das Ergebnis. Markup: index.html (#hostActDlg). Die Logik des Textes steht in hostactions.js.
 * ===================================================================== */
const $ = (s) => document.querySelector(s);
const POLL_MS = 1500;

/**
 * @param {{api: Function, esc: Function}} o
 * @returns {(host: object, action: string, target?: string|null, opts?: {lines?: number, onDone?: Function}) => Promise<void>} oeffnet den Dialog; das Versprechen endet, wenn der Dialog geschlossen wird
 */
export function initHostActionDialog({ api, esc }) {
  let session = 0;                 // jede Oeffnung hat eine Nummer: ein alter Abruf zeichnet nichts mehr in einen neuen Dialog
  let timers = [];
  const clear = () => { for (const x of timers) clearTimeout(x); timers = []; };
  const body = (html) => { $('#hostActBody').innerHTML = html; };
  const msg = (text) => { $('#hostActMsg').textContent = text || ''; };
  const confirmBtn = () => $('#hostActConfirm');

  $('#hostActDlg').addEventListener('close', () => { session++; clear(); });

  return function open(host, action, target = null, opts = {}) {
    const my = ++session;
    clear();
    const alive = () => my === session;
    const dlg = $('#hostActDlg');
    $('#hostActTitle').textContent = host.name;
    body(`<p class="hint">${esc(t('hostinfo.stLoading'))}</p>`);
    msg('');
    confirmBtn().hidden = true;
    $('#hostActClose').textContent = t('common.cancel');
    dlg.showModal();

    return new Promise((resolve) => {
      dlg.addEventListener('close', () => resolve(), { once: true });
      (async () => {
        let conf;
        try { conf = await prepareAction(api, host.id, action, target, opts.lines || null); }
        catch (e) { if (alive()) { body(''); msg(e.code ? errText(e.code) : e.message); $('#hostActClose').textContent = t('common.close'); } return; }
        if (!alive()) return;
        const model = confirmModel(conf);
        $('#hostActTitle').textContent = model.title;
        body(confirmHtml(conf, esc));
        const btn = confirmBtn();
        btn.hidden = false;
        btn.className = model.danger ? 'danger' : 'primary';
        btn.textContent = t('hostact.confirm');
        const input = $('#hostActTyped');
        const refresh = () => { btn.disabled = !typedOk(conf, input ? input.value : ''); };
        if (input) { input.addEventListener('input', refresh); input.focus(); }
        refresh();
        // Die Bestaetigung gilt nur kurz; danach muss der Dialog neu geoeffnet werden (der Server lehnt sie ohnehin ab)
        timers.push(setTimeout(() => { if (alive() && !btn.hidden) { btn.disabled = true; msg(errText('confirmation_expired')); } }, Math.max(5, conf.expiresIn) * 1000));

        btn.onclick = async () => {
          if (!typedOk(conf, input ? input.value : '')) return;
          btn.disabled = true; msg('');
          let started;
          try { started = await runAction(api, host.id, conf, typedName(conf) ? input.value : null); }
          catch (e) { if (alive()) { msg(e.code ? errText(e.code) : e.message); if (e.code === 'typed_mismatch') btn.disabled = false; else $('#hostActClose').textContent = t('common.close'); } return; }
          if (!alive()) return;
          clear();
          btn.hidden = true;
          $('#hostActClose').textContent = t('common.close');
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
