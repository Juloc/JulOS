/* =====================================================================
 * Hosts (docs/HOSTS.md): reine Hilfen ohne DOM, damit Desktop-Oberflaeche und Handy-Huelle dasselbe zeigen und Node sie testen kann.
 * Ein Host ist, was GET /api/hosts liefert: { id, name, online, hostname?, os?, agent?: { online, lastSeen?, version?, platform?, outdated }, services: [{ connection, kind, online? }] }
 * ===================================================================== */

/** 'up' (erreichbar), 'down' oder 'unknown' (nicht geprueft: Erlaubnisliste oder noch keine Antwort) */
export function serviceState(host, connectionId) {
  const s = host && host.services && host.services.find((x) => x.connection === connectionId);
  return !s || typeof s.online !== 'boolean' ? 'unknown' : s.online ? 'up' : 'down';
}

export const hostById = (hosts, id) => hosts.find((h) => h.id === id) || null;

/** Zustand einer Verbindung anhand ihres Hosts */
export const connectionState = (hosts, c) => serviceState(hostById(hosts, c.hostId), c.id);

/** Der Statuspunkt: gruen (ok) oder grau (off); unbekannt zaehlt als grau */
export const dotClass = (state) => (state === 'up' || state === true ? 'ok' : 'off');
export const stateText = (state) => (state === 'up' || state === true ? 'online' : state === 'unknown' ? 'unbekannt' : 'offline');

/** Die Verbindungen je Host in der Reihenfolge der Hosts; Hosts ohne Verbindung bleiben als leere Gruppe, Verbindungen ohne bekannten Host kommen in eine Gruppe ohne Host ans Ende */
export function groupConnections(hosts, connections) {
  const groups = hosts.map((host) => ({ host, connections: connections.filter((c) => c.hostId === host.id) }));
  const orphans = connections.filter((c) => !hosts.some((h) => h.id === c.hostId));
  if (orphans.length) groups.push({ host: null, connections: orphans });
  return groups;
}

/** Rechnername und Betriebssystem, soweit der Agent sie gemeldet hat */
export const hostDetail = (h) => [h.hostname, h.os].filter(Boolean).join(' · ');

const when = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleString('de-DE', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }); };

/** Ein Satz zum Agenten eines Hosts */
export function agentSummary(h) {
  const a = h.agent;
  if (!a) return 'Kein Agent';
  const what = [a.version && 'Agent ' + a.version, a.platform].filter(Boolean).join(', ');
  const base = a.online ? 'Agent verbunden' : 'Agent getrennt' + (a.lastSeen && when(a.lastSeen) ? ' (zuletzt ' + when(a.lastSeen) + ')' : '');
  return base + (what ? ' – ' + what : '') + (a.outdated ? ' – zu alt, bitte neu einrichten' : '');
}

/** Wie viele Dienste eines Hosts antworten, als Text ("2 von 3 Diensten erreichbar") */
export function servicesSummary(h) {
  const n = h.services.length;
  if (!n) return 'Keine Verbindungen';
  const up = h.services.filter((s) => s.online === true).length;
  return `${up} von ${n} ${n === 1 ? 'Dienst' : 'Diensten'} erreichbar`;
}
