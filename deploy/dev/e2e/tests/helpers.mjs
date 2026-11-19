import { createHmac, randomBytes } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';

export const OUT = '/out';
export const STATE_FILE = OUT + '/account.json';       // Wegwerf-Konto dieses Laufs (liegt ausserhalb von git, e2e.sh raeumt es beim naechsten Lauf weg)
export const AUTH_FILE = OUT + '/auth.json';           // Cookie der angemeldeten Sitzung (storageState)

// Ziele im Compose-Netz des Stacks (deploy/dev/compose.yaml): RDP-Desktop mit festem Wegwerf-Zugang, SSH-Ziel mit Zufallspasswort aus dem Stack
export const RDP = { host: 'rdp-target', user: 'ubuntu', password: 'ubuntu' };
export const SSH = { host: 'test-ssh', port: '2222', user: 'tester', password: process.env.E2E_SSH_PASSWORD || '' };

export const saveState = (s) => writeFileSync(STATE_FILE, JSON.stringify(s));
export const loadState = () => JSON.parse(readFileSync(STATE_FILE, 'utf8'));

export const newPassword = () => 'pw-' + randomBytes(9).toString('hex');

const B32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
function base32(s) {
  let bits = 0, value = 0; const out = [];
  for (const ch of s.replace(/[\s=]/g, '').toUpperCase()) {
    value = (value << 5) | B32.indexOf(ch); bits += 5;
    if (bits >= 8) { out.push((value >>> (bits - 8)) & 255); bits -= 8; }
  }
  return Buffer.from(out);
}

/** Authenticator-Code (RFC 6238, SHA-1, 6 Stellen, 30 s) zum angezeigten Schluessel; `shift` = Zeitschritte vor (der Server nimmt +-1). */
export function totp(secret, shift = 0, now = Date.now()) {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(now / 30000) + shift));
  const h = createHmac('sha1', base32(secret)).update(counter).digest();
  const o = h[h.length - 1] & 15;
  return String((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000).padStart(6, '0');
}
