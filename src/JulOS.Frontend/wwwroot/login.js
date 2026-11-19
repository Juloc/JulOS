/* =====================================================================
 * Anmeldung in der Sitzung, Seite des Browsers: wann der Server die Anmeldung am Ziel ablehnt (Fehlercode 769) oder die Anmeldedaten
 * verlangt (770), fragt die Sitzung neu nach Benutzer, Passwort und Domaene und verbindet mit einem neuen Ticket, ohne dass das Fenster
 * der Sitzung verloren geht. Hier nur die Entscheidungen und das Lesen der Eingabe, reine Logik ohne DOM (getestet in
 * tests/frontend/login.test.mjs); der Dialog selbst steht in app.js (RemoteSession.loginDialog).
 *
 * Das Passwort bleibt im Speicher der Sitzung (wie das beim ersten Verbinden getippte), es wird nirgends gespeichert und nie geloggt.
 * ===================================================================== */

import { t } from './i18n.js';

export const LOGIN_REJECTED = 769, LOGIN_MISSING = 770;

/** Wie oft nach dem ersten Fehlschlag neu eingegeben werden darf. */
export const MAX_LOGIN_TRIES = 3;

/** Ist der Fehlercode des Servers eine Sache der Anmeldedaten (abgelehnt oder fehlend)? */
export const isLoginError = (code) => code === LOGIN_REJECTED || code === LOGIN_MISSING;

/**
 * Was nach einem Anmeldefehler geschieht. tries: wie viele neue Versuche schon gemacht wurden (0 beim ersten Fehlschlag).
 * Gibt {ask: true, attempt, of, text} (Dialog zeigen) oder {ask: false, text} (aufgeben) zurueck.
 * kind 'vnc': ein VNC-Server kennt nur ein Passwort (keinen Benutzer), die Texte sagen das.
 */
export function loginStep(code, tries, kind) {
  const used = Math.max(0, Number(tries) || 0);
  if (used >= MAX_LOGIN_TRIES) return { ask: false, text: t('login.giveUp', { n: MAX_LOGIN_TRIES }) };
  const attempt = used + 1;
  const vnc = kind === 'vnc';
  const text = code === LOGIN_MISSING
    ? (vnc ? t('login.missingVnc') : t('login.missing'))
    : used === 0 ? (vnc ? t('login.rejectedVnc') : t('login.rejected')) : t('login.rejectedAgain');
  return { ask: true, attempt, of: MAX_LOGIN_TRIES, text };
}

/** Der Benutzername aus der Zielbeschreibung der Sitzung ("host:port/benutzer"), leer wenn keiner da ist. */
export function userOfTarget(target) {
  const s = String(target || ''), at = s.indexOf('/');
  return at < 0 ? '' : s.slice(at + 1);
}

/**
 * Liest die Eingabe des Dialogs. Benutzer und Domaene werden beschnitten (Leerzeichen am Rand), Steuerzeichen und mehr als 256 Zeichen
 * gelten als Fehler (der Server prueft dasselbe). Das Passwort bleibt unveraendert (Leerzeichen koennen dazugehoeren), darf nicht leer sein.
 * Gibt {user, password, domain} oder {error} zurueck. Mit { user: false } (VNC) ist kein Benutzer noetig.
 */
export function readLogin({ user = '', password = '', domain = '' } = {}, { user: needUser = true } = {}) {
  const u = String(user).trim(), d = String(domain).trim(), p = String(password);
  if (needUser && !u) return { error: t('login.needUser') };
  if (!p) return { error: t('login.needPassword') };
  if (p.length > 256) return { error: t('login.passwordTooLong') };
  // eslint-disable-next-line no-control-regex
  if (u.length > 256 || d.length > 256 || /[\u0000-\u001f\u007f]/.test(u + d)) return { error: t('login.userInvalid') };
  return { user: u, password: p, domain: d };
}
