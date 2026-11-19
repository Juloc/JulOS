import { qrSvg } from './qr.js';

/* Konto: Passwort aendern und Authenticator (TOTP) neu einrichten. Der Dialog #accountDlg steht in index.html und gilt fuer die
 * Handy-Shell (als Sheet) und die Desktop-Oberflaeche. Jede Aenderung verlangt das aktuelle Passwort; der Server beendet danach
 * alle anderen Sitzungen. Der QR-Code entsteht im Browser (qr.js), das Geheimnis verlaesst die Seite nicht. */

const $ = (s) => document.querySelector(s);

const spaced = (secret) => secret.replace(/(.{4})/g, '$1 ').trim();

/** Gibt die Funktion zurueck, die den Dialog oeffnet. api und setMsg kommen aus shell.js. */
export function initAccount({ api, setMsg }) {
  const dlg = $('#accountDlg');
  const steps = { main: $('#acctMain'), code: $('#totpFinishForm'), done: $('#totpDone') };
  const show = (name) => { for (const [k, el] of Object.entries(steps)) el.hidden = k !== name; };
  const busy = (form, on) => { for (const b of form.querySelectorAll('button[type=submit]')) b.disabled = on; };

  /** Alles Geheime und alle Eingaben weg, sobald der Dialog zugeht. */
  function reset() {
    for (const f of dlg.querySelectorAll('form')) f.reset();
    $('#acctQr').replaceChildren();
    $('#acctSecret').textContent = '';
    $('#acctOtpLink').removeAttribute('href');
    $('#acctRecovery').textContent = '';
    for (const m of dlg.querySelectorAll('.msg')) setMsg(m, '');
    show('main');
  }
  dlg.addEventListener('close', reset);
  dlg.addEventListener('click', (e) => { if (e.target.closest('[data-act=close]')) dlg.close(); });

  $('#pwForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const form = e.currentTarget, msg = $('#pwMsg');
    const current = $('#pwCur').value, next = $('#pwNew').value;
    if (next !== $('#pwNew2').value) return setMsg(msg, 'Die beiden neuen Passwörter sind nicht gleich.');
    busy(form, true); setMsg(msg, '');
    try {
      await api('/api/auth/password', { method: 'POST', body: { current, next } });
      form.reset();
      setMsg(msg, 'Passwort geändert. Alle anderen Geräte sind abgemeldet.', true);
    } catch (err) { setMsg(msg, err.message); }
    finally { busy(form, false); }
  });

  $('#totpBeginForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const form = e.currentTarget, msg = $('#totpMsg');
    busy(form, true); setMsg(msg, '');
    try {
      const r = await api('/api/auth/totp/begin', { method: 'POST', body: { current: $('#totpCur').value } });
      form.reset();
      $('#acctSecret').textContent = spaced(r.secret);
      $('#acctOtpLink').href = r.uri;
      try { $('#acctQr').innerHTML = qrSvg(r.uri, { label: 'QR-Code für den Authenticator' }); }
      catch { $('#acctQr').replaceChildren(); }   // sehr langer Link: dann bleibt der Text
      show('code'); setTimeout(() => $('#totpCode').focus(), 50);
    } catch (err) { setMsg(msg, err.message); }
    finally { busy(form, false); }
  });

  $('#totpFinishForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const form = e.currentTarget, msg = $('#totpFinishMsg');
    busy(form, true); setMsg(msg, '');
    try {
      const r = await api('/api/auth/totp/finish', { method: 'POST', body: { code: $('#totpCode').value } });
      $('#acctSecret').textContent = ''; $('#acctQr').replaceChildren(); form.reset();
      $('#acctRecovery').textContent = r.recovery.join('\n');
      show('done');
    } catch (err) { setMsg(msg, err.message); }
    finally { busy(form, false); }
  });
  $('#totpAbort').addEventListener('click', reset);

  $('#acctCopy').addEventListener('click', async () => {
    const msg = $('#totpDoneMsg');
    try { await navigator.clipboard.writeText($('#acctRecovery').textContent); setMsg(msg, 'Codes kopiert.', true); }
    catch { setMsg(msg, 'Kopieren geht hier nicht. Markiere die Codes und kopiere sie von Hand.'); }
  });

  return () => { reset(); dlg.showModal(); };
}
