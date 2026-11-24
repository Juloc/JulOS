/* =====================================================================
 * Zweiter Faktor fuer Aktionen (docs/SECURITY-REVIEW.md, F-04): verlangt der Server fuer eine Aktion eine frische Freigabe (403, Code step_up_required),
 * fragt ein kleines Sheet nach dem Code aus der Authenticator-App und wiederholt die Aktion danach genau einmal. Der Server entscheidet, welche Wege es betrifft
 * (StepUpPolicy); hier steht nur die Antwort darauf. withStepUp ist reines JavaScript (tests/frontend/stepup.test.mjs), initStepUp bindet das Sheet #stepUpDlg.
 * ===================================================================== */

import { showDialog } from './desktop-window.js';
import { t } from './i18n.js';

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
export function initStepUp({ api, setMsg, passkey = null }) {
  const $ = (s) => document.querySelector(s);
  const dlg = $('#stepUpDlg'), form = $('#suForm'), input = $('#suCode'), msg = $('#suMsg');
  let pending = null, settle = null;

  const finish = (ok) => { const s = settle; settle = null; pending = null; input.value = ''; setMsg(msg, ''); if (dlg.open) dlg.close(); if (s) s(ok); };
  dlg.addEventListener('close', () => { if (settle) finish(false); });      // Escape oder Tippen daneben: abgebrochen
  $('#suCancel').addEventListener('click', () => finish(false));

  // Statt des Codes ein Passkey mit Nutzerpruefung (gleiches Zeitfenster). Der Knopf ist nur da, wo der Browser Passkeys kann; ob die Person einen hat, sagt der Server.
  const pk = $('#suPasskey');
  if (pk && passkey && typeof window !== 'undefined' && window.PublicKeyCredential) {
    pk.hidden = false;
    pk.addEventListener('click', async () => {
      pk.disabled = true; setMsg(msg, '');
      try { await passkey(); finish(true); }
      catch (err) { setMsg(msg, err && err.status ? err.message : t('pk.cancelled')); }
      finally { pk.disabled = false; }
    });
  }

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

  const show = () => showDialog(dlg, { system: true, top: true, width: 420 });
  return () => {
    if (pending) { show(); return pending; }               // ein zweiter Aufruf holt das offene Fenster nach vorn
    pending = new Promise((resolve) => { settle = resolve; });
    input.value = ''; setMsg(msg, '');
    // Ein Fenster, das vor allen anderen liegt und den Fokus nimmt: die Aktion wartet auf diese Eingabe. Schliessen, Escape und Abmelden heissen "nicht freigegeben" (close-Ereignis oben).
    show();
    setTimeout(() => input.focus(), 50);
    return pending;
  };
}
