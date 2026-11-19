/* =====================================================================
 * Quick Connect: eine Sitzung ohne Speichern. Hier nur das Lesen der Eingabe (Adresse, Port, Benutzer, Art); der Dialog steckt in shell.js,
 * die Pruefung und die Erlaubnisliste am Server (POST /api/remote/ticket mit "quick"). Reines JavaScript, ohne DOM.
 * ===================================================================== */

export const DEFAULT_PORT = { desktop: 3389, terminal: 22 };
const SCHEME_KIND = { rdp: 'desktop', ssh: 'terminal' };
const HOST = /^[A-Za-z0-9._\-]{1,253}$/;

/**
 * Liest "[rdp://|ssh://][benutzer@]adresse[:port]". Die Art ergibt sich aus dem Schema, sonst aus dem Port (22 = Terminal, 3389 = Desktop), sonst null.
 * Eine IPv6-Adresse (mehrere Doppelpunkte) gilt als Ganzes ohne Port. Gibt null zurueck, wenn es keine Adresse ist.
 */
export function parseQuickTarget(input) {
  let text = String(input ?? '').trim();
  if (!text || /\s/.test(text)) return null;
  let kind = null;
  const scheme = /^([a-z]+):\/\//i.exec(text);
  if (scheme) {
    kind = SCHEME_KIND[scheme[1].toLowerCase()] || null;
    if (!kind) return null;
    text = text.slice(scheme[0].length).replace(/\/+$/, '');
  }
  let username = '';
  const at = text.lastIndexOf('@');
  if (at >= 0) { username = text.slice(0, at); text = text.slice(at + 1); }
  let host = text, port = null;
  const colons = (text.match(/:/g) || []).length;
  if (colons === 1) {
    const m = /^(.+):(\d{1,5})$/.exec(text);
    if (!m) return null;
    host = m[1]; port = Number(m[2]);
    if (port < 1 || port > 65535) return null;
  } else if (colons > 1) {
    if (!/^[0-9a-fA-F:.]+$/.test(text)) return null;       // IPv6-Adresse
  }
  if (!host || (colons <= 1 && !HOST.test(host)) || username.length > 64) return null;
  if (!kind && port === 22) kind = 'terminal';
  else if (!kind && port === 3389) kind = 'desktop';
  return { kind, host, port, username };
}

/** Sieht die Eingabe nach einer Adresse aus (und nicht nach einem Namen aus der Liste)? Fuer den Vorschlag "Quick Connect zu ..." in der Suche. */
export function looksLikeAddress(input) {
  const t = parseQuickTarget(input);
  if (!t) return false;
  return /^[a-z]+:\/\//i.test(String(input).trim()) || String(input).includes('@') || t.port !== null || /\./.test(t.host) || t.host.includes(':');
}

/** Die Sitzungsbeschreibung fuer den Verwalter (sessions.js): eine Kennung je Ziel, damit dasselbe Ziel keine zweite Sitzung aufmacht. Kein Passwort darin. */
export function quickConnection({ kind, host, port, username }) {
  const p = port || DEFAULT_PORT[kind];
  return {
    id: `quick:${kind}:${host.toLowerCase()}:${p}:${username || ''}`,
    name: host, kind, quick: { kind, host, port: p, username: username || '' },
    target: `${host}:${p}/${username || ''}`,
  };
}
