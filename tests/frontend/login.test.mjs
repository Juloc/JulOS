// Anmeldung in der Sitzung (login.js): wann neu gefragt wird, wie oft, und was aus der Eingabe wird.
import test from 'node:test';
import assert from 'node:assert/strict';
import { isLoginError, loginStep, userOfTarget, readLogin, MAX_LOGIN_TRIES, LOGIN_MISSING, LOGIN_REJECTED } from '../../src/JulOS.Frontend/wwwroot/login.js';

test('only the codes 769 and 770 are login errors', () => {
  assert.equal(isLoginError(769), true);
  assert.equal(isLoginError(770), true);
  for (const code of [515, 520, 777, 800, 0, undefined]) assert.equal(isLoginError(code), false);
});

test('after the first failure there are three new tries, then the session closes', () => {
  assert.equal(MAX_LOGIN_TRIES, 3);
  for (let tries = 0; tries < 3; tries++) {
    const s = loginStep(LOGIN_REJECTED, tries);
    assert.equal(s.ask, true);
    assert.equal(s.attempt, tries + 1);
    assert.equal(s.of, 3);
  }
  const last = loginStep(LOGIN_REJECTED, 3);
  assert.equal(last.ask, false);
  assert.match(last.text, /abgelehnt/);
  assert.equal(loginStep(LOGIN_MISSING, 7).ask, false);
});

test('missing credentials and a rejected login say different things', () => {
  assert.notEqual(loginStep(LOGIN_MISSING, 0).text, loginStep(LOGIN_REJECTED, 0).text);
  assert.match(loginStep(LOGIN_MISSING, 0).text, /verlangt/);
  assert.notEqual(loginStep(LOGIN_REJECTED, 0).text, loginStep(LOGIN_REJECTED, 1).text);
});

test('the user name comes out of the target text of the session', () => {
  assert.equal(userOfTarget('pc.example:3389/anna'), 'anna');
  assert.equal(userOfTarget('pc.example:3389/'), '');
  assert.equal(userOfTarget('pc.example:3389/DOM\\anna'), 'DOM\\anna');
  assert.equal(userOfTarget(undefined), '');
});

test('the input needs a user and a password; user and domain are trimmed, the password is not', () => {
  assert.deepEqual(readLogin({ user: ' anna ', password: ' geheim ', domain: ' FIRMA ' }), { user: 'anna', password: ' geheim ', domain: 'FIRMA' });
  assert.deepEqual(readLogin({ user: 'anna', password: 'x' }), { user: 'anna', password: 'x', domain: '' });
  assert.ok(readLogin({ user: '', password: 'x' }).error);
  assert.ok(readLogin({ user: 'anna', password: '' }).error);
  assert.ok(readLogin({}).error);
});

test('control characters and over-long values are refused', () => {
  assert.ok(readLogin({ user: 'an\nna', password: 'x' }).error);
  assert.ok(readLogin({ user: 'anna', password: 'x', domain: 'a\u0000b' }).error);
  assert.ok(readLogin({ user: 'a'.repeat(257), password: 'x' }).error);
  assert.ok(readLogin({ user: 'anna', password: 'p'.repeat(257) }).error);
  assert.equal(readLogin({ user: 'a'.repeat(256), password: 'p'.repeat(256) }).error, undefined);
});

test('VNC: ein VNC-Server fragt nur nach dem Passwort: eigene Texte, und ohne Benutzer geht die Eingabe durch', () => {
  assert.notEqual(loginStep(LOGIN_MISSING, 0, 'vnc').text, loginStep(LOGIN_MISSING, 0, 'desktop').text);
  assert.notEqual(loginStep(LOGIN_REJECTED, 0, 'vnc').text, loginStep(LOGIN_REJECTED, 0, 'desktop').text);
  assert.equal(loginStep(LOGIN_REJECTED, 1, 'vnc').text, loginStep(LOGIN_REJECTED, 1, 'desktop').text);       // "auch das wurde abgelehnt" gilt fuer beide
  assert.ok(readLogin({ user: '', password: 'geheim' }).error, 'RDP braucht einen Benutzer');
  assert.deepEqual(readLogin({ user: '', password: 'geheim' }, { user: false }), { user: '', password: 'geheim', domain: '' });
  assert.ok(readLogin({ user: '', password: '' }, { user: false }).error, 'ein Passwort bleibt Pflicht');
});
