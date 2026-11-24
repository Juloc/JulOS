import { test } from 'node:test';
import assert from 'node:assert/strict';
import { b64uToBytes, bytesToB64u, creationOptions, requestOptions, attestationJson, assertionJson, errorKind, supported } from '../../src/JulOS.Frontend/wwwroot/passkeys.js';

test('Base64url: Rundlauf, ohne Auffuellung, mit - und _ statt + und /', () => {
  const all = Uint8Array.from({ length: 256 }, (_, i) => i);
  for (const n of [0, 1, 2, 3, 4, 31, 32, 255, 256]) {
    const b = all.slice(0, n), s = bytesToB64u(b);
    assert.match(s, /^[A-Za-z0-9_-]*$/);
    assert.deepEqual([...b64uToBytes(s)], [...b]);
  }
  assert.equal(bytesToB64u(Uint8Array.from([0xfb, 0xff, 0xfe])), '-__-');
  assert.deepEqual([...b64uToBytes('-__-')], [0xfb, 0xff, 0xfe]);
  assert.deepEqual([...b64uToBytes('AQID=')], [1, 2, 3]);                       // mit Auffuellung geht auch
  assert.equal(bytesToB64u(new Uint8Array([1, 2, 3]).buffer), 'AQID');         // ArrayBuffer wie von navigator.credentials
  assert.equal(bytesToB64u(new DataView(new Uint8Array([9, 1, 2, 3]).buffer, 1)), 'AQID');   // Ansicht mit Versatz
});

const registration = {
  rp: { name: 'JulOS', id: 'julos.example' },
  user: { id: 'AQID', name: 'jul', displayName: 'jul' },
  challenge: 'BAUG', pubKeyCredParams: [{ type: 'public-key', alg: -7 }], timeout: null, extensions: null,
  excludeCredentials: [{ type: 'public-key', id: 'BwgJ' }], authenticatorSelection: { residentKey: 'required', userVerification: 'required', authenticatorAttachment: null }, attestation: 'none',
};

test('Optionen zum Registrieren: Herausforderung, Benutzerkennung und ausgeschlossene Ids werden Bytes, null-Felder fallen weg', () => {
  const o = creationOptions(JSON.stringify(registration));
  assert.ok(o.challenge instanceof Uint8Array && o.user.id instanceof Uint8Array && o.excludeCredentials[0].id instanceof Uint8Array);
  assert.deepEqual([...o.challenge], [4, 5, 6]);
  assert.deepEqual([...o.user.id], [1, 2, 3]);
  assert.deepEqual([...o.excludeCredentials[0].id], [7, 8, 9]);
  assert.ok(!('timeout' in o) && !('extensions' in o) && !('authenticatorAttachment' in o.authenticatorSelection));
  assert.deepEqual(o.authenticatorSelection, { residentKey: 'required', userVerification: 'required' });   // nichts Eigenes dazugedichtet, nichts weggenommen
  assert.equal(o.rp.id, 'julos.example');
  assert.equal(o.attestation, 'none');
});

test('Optionen zum Anmelden: eine leere Erlaubnisliste fehlt ganz, eine gefuellte wird zu Bytes', () => {
  const base = { challenge: 'BAUG', rpId: 'julos.example', userVerification: 'required', timeout: null };
  const open = requestOptions({ ...base, allowCredentials: [] });
  assert.ok(!('allowCredentials' in open) && !('timeout' in open));
  assert.deepEqual([...open.challenge], [4, 5, 6]);
  assert.ok(!('allowCredentials' in requestOptions({ ...base, allowCredentials: null })));
  const some = requestOptions(JSON.stringify({ ...base, allowCredentials: [{ type: 'public-key', id: 'BwgJ' }] }));
  assert.deepEqual([...some.allowCredentials[0].id], [7, 8, 9]);
  assert.equal(some.userVerification, 'required');
});

const buf = (...n) => Uint8Array.from(n).buffer;

test('Antwort auf das Registrieren: nur die benoetigten Felder, als Text', () => {
  const j = attestationJson({ id: 'abc', rawId: buf(1, 2, 3), type: 'public-key', response: { clientDataJSON: buf(4), attestationObject: buf(5, 6) }, getClientExtensionResults: () => ({ evil: 1 }) });
  assert.deepEqual(j, { id: 'abc', rawId: 'AQID', type: 'public-key', response: { clientDataJSON: 'BA', attestationObject: 'BQY' }, clientExtensionResults: {} });
  JSON.stringify(j);                                                             // ist reiner Text
});

test('Antwort auf das Anmelden: der User-Handle fehlt, wenn der Passkey keinen mitschickt', () => {
  const cred = (userHandle) => ({ id: 'abc', rawId: buf(1), type: 'public-key', response: { authenticatorData: buf(1, 2), clientDataJSON: buf(3), signature: buf(4), userHandle } });
  const withHandle = assertionJson(cred(buf(9, 9)));
  assert.equal(withHandle.response.userHandle, 'CQk');
  assert.deepEqual(Object.keys(withHandle.response).sort(), ['authenticatorData', 'clientDataJSON', 'signature', 'userHandle']);
  for (const none of [null, undefined, new ArrayBuffer(0)]) assert.ok(!('userHandle' in assertionJson(cred(none)).response));
});

test('Fehler des Browsers werden eingeordnet', () => {
  assert.equal(errorKind({ name: 'NotAllowedError' }), 'cancelled');
  assert.equal(errorKind({ name: 'AbortError' }), 'cancelled');
  assert.equal(errorKind({ name: 'InvalidStateError' }), 'duplicate');
  assert.equal(errorKind({ name: 'SecurityError' }), 'insecure');
  assert.equal(errorKind(new Error('x')), 'failed');
  assert.equal(errorKind(null), 'failed');
});

test('ohne Browser-Umgebung ist nichts unterstuetzt (kein Absturz)', () => { assert.equal(supported(), false); });
