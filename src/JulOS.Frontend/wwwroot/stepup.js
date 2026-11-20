/* =====================================================================
 * Zweiter Faktor fuer Aktionen (docs/SECURITY-REVIEW.md, F-04): verlangt der Server fuer eine Aktion eine frische Freigabe (403, Code step_up_required),
 * fragt ein kleines Sheet nach dem Code aus der Authenticator-App und wiederholt die Aktion danach genau einmal. Der Server entscheidet, welche Wege es betrifft
 * (StepUpPolicy); hier steht nur die Antwort darauf. withStepUp ist reines JavaScript (tests/frontend/stepup.test.mjs), initStepUp bindet das Sheet #stepUpDlg.
 * ===================================================================== */

/** Ist der Fehler die Aufforderung des Servers, sich mit dem zweiten Faktor zu bestaetigen? */
export const isStepUp = (e) => !!e && e.status === 403 && e.code === 'step_up_required';

/** Fuehrt die Aktion aus; verlangt der Server eine Freigabe, wird einmal gefragt (ask() gibt true, wenn sie erteilt ist) und die Aktion wiederholt. Alles andere geht unveraendert durch. */
export async function withStepUp(run, ask) {
  try { return await run(); }
  catch (e) {
    if (isStepUp(e) && await ask()) return run();
    throw e;
  }
}

/** Der Code ohne Leerzeichen (die App zeigt ihn als "123 456"). */
export const cleanCode = (s) => String(s || '').replace(/\s+/g, '');

/**
 * Das Sheet. api ist die Funktion OHNE Wiederholung (rawApi aus shell.js: die Freigabe selbst darf nicht wieder nach einer Freigabe fragen), setMsg schreibt eine Meldung.
 * Gibt ask() zurueck: eine Funktion, die das Sheet zeigt und true liefert, wenn der Code gestimmt hat, false bei Abbruch. Mehrere gleichzeitige Aufrufe teilen sich ein Sheet.
 */
export function initStepUp({ api, setMsg }) {
  const $ = (s) => document.querySelector(s);
  const dlg = $('#stepUpDlg'), form = $('#suForm'), input = $('#suCode'), msg = $('#suMsg');
  let pending = null, settle = null;

  const finish = (ok) => { const s = settle; settle = null; pending = null; input.value = ''; setMsg(msg, ''); if (dlg.open) dlg.close(); if (s) s(ok); };
  dlg.addEventListener('close', () => { if (settle) finish(false); });      // Escape oder Tippen daneben: abgebrochen
  $('#suCancel').addEventListener('click', () => finish(false));

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const code = cleanCode(input.value);
    if (!code) return;
    for (const b of form.querySelectorAll('button[type=submit]')) b.disabled = true;
    setMsg(msg, '');
    try { await api('/api/auth/step-up', { method: 'POST', body: { code } }); finish(true); }
    catch (err) { setMsg(msg, err.message); input.select(); }
    finally { for (const b of form.querySelectorAll('button[type=submit]')) b.disabled = false; }
  });

  return () => {
    if (pending) return pending;
    pending = new Promise((resolve) => { settle = resolve; });
    input.value = ''; setMsg(msg, '');
    dlg.showModal();
    setTimeout(() => input.focus(), 50);
    return pending;
  };
}
