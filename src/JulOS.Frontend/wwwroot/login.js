/* =====================================================================
 * Anmeldung in der Sitzung, Seite des Browsers: wann der Server die Anmeldung am Ziel ablehnt (Fehlercode 769) oder die Anmeldedaten
 * verlangt (770), fragt die Sitzung neu nach Benutzer, Passwort und Domaene und verbindet mit einem neuen Ticket, ohne dass das Fenster
 * der Sitzung verloren geht. Hier nur die Entscheidungen und das Lesen der Eingabe, reine Logik ohne DOM (getestet in
 * tests/frontend/login.test.mjs); der Dialog selbst steht in app.js (RemoteSession.loginDialog).
 *
 * Das Passwort bleibt im Speicher der Sitzung (wie das beim ersten Verbinden getippte), es wird nirgends gespeichert und nie geloggt.
 * ===================================================================== */

export const LOGIN_REJECTED = 769, LOGIN_MISSING = 770;

/** Wie oft nach dem ersten Fehlschlag neu eingegeben werden darf. */
export const MAX_LOGIN_TRIES = 3;

/** Ist der Fehlercode des Servers eine Sache der Anmeldedaten (abgelehnt oder fehlend)? */
export const isLoginError = (code) => code === LOGIN_REJECTED || code === LOGIN_MISSING;

/**
 * Was nach einem Anmeldefehler geschieht. tries: wie viele neue Versuche schon gemacht wurden (0 beim ersten Fehlschlag).
 * Gibt {ask: true, attempt, of, text} (Dialog zeigen) oder {ask: false, text} (aufgeben) zurueck.
 */
export function loginStep(code, tries) {
  const used = Math.max(0, Number(tries) || 0);
  if (used >= MAX_LOGIN_TRIES) return { ask: false, text: `Anmeldung am Ziel abgelehnt (${MAX_LOGIN_TRIES} neue Versuche ohne Erfolg).` };
  const attempt = used + 1;
  const text = code === LOGIN_MISSING
    ? 'Das Ziel verlangt Benutzer und Passwort, bevor es etwas zeigt.'
    : used === 0 ? 'Das Ziel hat die Anmeldung abgelehnt. Bitte Benutzer und Passwort prüfen.' : 'Auch das wurde abgelehnt.';
  return { ask: true, attempt, of: MAX_LOGIN_TRIES, text };
}

/** Der Benutzername aus der Zielbeschreibung der Sitzung ("host:port/benutzer"), leer wenn keiner da ist. */
export function userOfTarget(target) {
  const t = String(target || ''), at = t.indexOf('/');
  return at < 0 ? '' : t.slice(at + 1);
}

/**
 * Liest die Eingabe des Dialogs. Benutzer und Domaene werden beschnitten (Leerzeichen am Rand), Steuerzeichen und mehr als 256 Zeichen
 * gelten als Fehler (der Server prueft dasselbe). Das Passwort bleibt unveraendert (Leerzeichen koennen dazugehoeren), darf nicht leer sein.
 * Gibt {user, password, domain} oder {error} zurueck.
 */
export function readLogin({ user = '', password = '', domain = '' } = {}) {
  const u = String(user).trim(), d = String(domain).trim(), p = String(password);
  if (!u) return { error: 'Bitte einen Benutzer eingeben.' };
  if (!p) return { error: 'Bitte ein Passwort eingeben.' };
  if (p.length > 256) return { error: 'Das Passwort ist zu lang.' };
  // eslint-disable-next-line no-control-regex
  if (u.length > 256 || d.length > 256 || /[\u0000-\u001f\u007f]/.test(u + d)) return { error: 'Benutzer oder Domäne ist ungültig.' };
  return { user: u, password: p, domain: d };
}
