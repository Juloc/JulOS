/* =====================================================================
 * Ordner, Favoriten und "Zuletzt benutzt" (docs/FOLDERS.md): die Hilfen ohne DOM (Symbole, Gruppieren, Sortieren, Uebernahme der lokalen Favoriten)
 * und die Aufrufe an den Server. Die Dialoge und die Listen zeichnen shell.js, mobile.js und desktop.js.
 * ===================================================================== */

/** Die Symbole, aus denen ein Ordner eines waehlen kann (dieselben Namen kennt der Server, FolderRules.Icons); Material, 24 px, gefuellt. */
export const FOLDER_ICONS = {
  folder: 'M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z',
  star: 'M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z',
  home: 'M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z',
  work: 'M20 6h-4V4c0-1.11-.89-2-2-2h-4c-1.11 0-2 .89-2 2v2H4c-1.11 0-1.99.89-1.99 2L2 19c0 1.11.89 2 2 2h16c1.11 0 2-.89 2-2V8c0-1.11-.89-2-2-2zm-6 0h-4V4h4v2z',
  server: 'M20 13H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1v-6c0-.55-.45-1-1-1zM7 19c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM20 3H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1V4c0-.55-.45-1-1-1zM7 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z',
  cloud: 'M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96z',
  lock: 'M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM9 8V6c0-1.66 1.34-3 3-3s3 1.34 3 3v2H9z',
  bolt: 'M11 21h-1l1-7H7.5c-.58 0-.57-.32-.38-.66.19-.34.05-.08.07-.12C8.48 10.94 10.42 7.54 13 3h1l-1 7h3.5c.49 0 .56.33.47.51l-.07.15C12.96 17.55 11 21 11 21z',
  game: 'M21 6H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h18c1.1 0 1.99-.9 1.99-2L23 8c0-1.1-.9-2-2-2zm-10 7H8v3H6v-3H3v-2h3V8h2v3h3v2zm4.5 2c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm4-3c-.83 0-1.5-.67-1.5-1.5S18.67 9 19.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z',
  school: 'M5 13.18v4L12 21l7-3.82v-4L12 17l-7-3.82zM12 3L1 9l11 6 9-4.91V17h2V9L12 3z',
  build: 'M22.7 19l-9.1-9.1c.9-2.3.4-5-1.5-6.9-2-2-5-2.4-7.4-1.3L9 6 6 9 1.6 4.7C.4 7.1.9 10.1 2.9 12.1c1.9 1.9 4.6 2.4 6.9 1.5l9.1 9.1c.4.4 1 .4 1.4 0l2.3-2.3c.5-.4.5-1.1.1-1.4z',
  globe: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 17.93c-3.95-.49-7-3.85-7-7.93 0-.62.08-1.21.21-1.79L9 15v1c0 1.1.9 2 2 2v1.93zm6.9-2.54c-.26-.81-1-1.39-1.9-1.39h-1v-3c0-.55-.45-1-1-1H8v-2h2c.55 0 1-.45 1-1V7h2c1.1 0 2-.9 2-2v-.41c2.93 1.19 5 4.06 5 7.41 0 2.08-.8 3.97-2.1 5.39z',
};
export const FOLDER_ICON_KEYS = Object.keys(FOLDER_ICONS);
export const FOLDER_ICON_LABEL = { folder: 'Ordner', star: 'Stern', home: 'Haus', work: 'Arbeit', server: 'Server', cloud: 'Wolke', lock: 'Schloss', bolt: 'Blitz', game: 'Spiel', school: 'Schule', build: 'Werkzeug', globe: 'Welt' };

/** Das Symbol eines Ordners als SVG-Text (ein unbekanntes Symbol faellt auf "folder" zurueck). */
export const folderIconSvg = (key) => `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${FOLDER_ICONS[key] || FOLDER_ICONS.folder}"/></svg>`;

/** Legt die Einsortierung des Servers (GET /api/folders, placements) auf die Verbindungen: folderId, favorite, lastUsedAt, useCount. Unbekannte bleiben ohne Ordner. */
export function mergePlacements(connections, placements) {
  const by = new Map((placements || []).map((p) => [p.id, p]));
  for (const c of connections) {
    const p = by.get(c.id);
    c.folderId = (p && p.folderId) || null;
    c.favorite = !!(p && p.favorite);
    c.lastUsedAt = (p && p.lastUsedAt) || null;
    c.useCount = (p && p.useCount) || 0;
  }
  return connections;
}

/**
 * Die Verbindungen je Ordner in der Reihenfolge der Ordner; Verbindungen ohne (oder mit unbekanntem) Ordner kommen als Gruppe ohne Ordner ans Ende.
 * Leere Ordner bleiben mit showEmpty (Verwaltung), sonst fallen sie weg.
 */
export function groupByFolder(folders, connections, { showEmpty = false } = {}) {
  const known = new Set(folders.map((f) => f.id));
  const groups = folders.map((folder) => ({ folder, connections: connections.filter((c) => c.folderId === folder.id) }));
  const loose = connections.filter((c) => !c.folderId || !known.has(c.folderId));
  const out = showEmpty ? groups : groups.filter((g) => g.connections.length);
  if (loose.length) out.push({ folder: null, connections: loose });
  return out;
}

/** Filter der Dienste: 'all', 'fav' (Favoriten), 'recent' (zuletzt benutzt), 'none' (ohne Ordner) oder die Kennung eines Ordners. */
export function filterByFolder(connections, filter, folders = []) {
  if (!filter || filter === 'all') return connections;
  if (filter === 'fav') return connections.filter((c) => c.favorite);
  if (filter === 'recent') return recentList(connections, 12);
  const known = new Set(folders.map((f) => f.id));
  if (filter === 'none') return connections.filter((c) => !c.folderId || !known.has(c.folderId));
  return connections.filter((c) => c.folderId === filter);
}

/** Die Favoriten in der Reihenfolge der Liste, danach die meistgenutzten (die noch keine Favoriten sind), hoechstens limit. */
export function favouriteList(connections, limit = 6) {
  const fav = connections.filter((c) => c.favorite);
  const used = connections.filter((c) => !c.favorite && c.useCount > 0).sort((a, b) => b.useCount - a.useCount);
  return [...fav, ...used].slice(0, limit);
}

/** Zuletzt benutzte Verbindungen, die neueste zuerst. */
export function recentList(connections, limit = 8) {
  return connections.filter((c) => c.lastUsedAt).sort((a, b) => Date.parse(b.lastUsedAt) - Date.parse(a.lastUsedAt)).slice(0, limit);
}

/** Kurzer Name des Ordners einer Verbindung (fuer eine Zeile unter dem Namen), sonst ''. */
export const folderName = (folders, c) => (folders.find((f) => f.id === c.folderId) || {}).name || '';

/* ---------- Uebernahme der lokalen Favoriten (einmal je Geraet) ---------- */
const DONE_KEY = 'pd.m.orgImported';

/**
 * Liest, was die Handy-Oberflaeche bisher nur lokal gemerkt hat (pd.m.fav, pd.m.use, pd.m.log) und baut daraus die Anfrage fuer den Server.
 * Gibt null zurueck, wenn nichts da ist.
 */
export function localOrganisation(storage) {
  const read = (k, d) => { try { const v = storage.getItem('pd.m.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } };
  const fav = read('fav', []), use = read('use', {}), log = read('log', []);
  const favorites = Array.isArray(fav) ? fav.filter((x) => typeof x === 'string').slice(0, 200) : [];
  const uses = {};
  for (const [id, n] of Object.entries(use && typeof use === 'object' ? use : {})) if (Number.isFinite(n) && n > 0) uses[id] = Math.floor(n);
  const last = {};
  for (const l of Array.isArray(log) ? log : []) if (l && l.type === 'start' && l.id && Number.isFinite(l.t) && !(last[l.id] >= l.t)) last[l.id] = l.t;
  if (!favorites.length && !Object.keys(uses).length && !Object.keys(last).length) return null;
  return { favorites, uses, last };
}

/**
 * Uebernimmt beim ersten Aufruf die lokalen Favoriten auf den Server und merkt sich, dass es getan ist. Schlaegt es fehl, bleibt alles lokal und es wird
 * beim naechsten Mal wieder versucht. @returns {Promise<boolean>} true, wenn jetzt etwas uebernommen wurde (die Seite soll neu laden).
 */
export async function takeOverLocal(storage, api) {
  try { if (storage.getItem(DONE_KEY)) return false; } catch { return false; }
  const body = localOrganisation(storage);
  try {
    if (body) await api('/api/folders/import-local', { method: 'POST', body });
    storage.setItem(DONE_KEY, '1');
    if (body) { storage.removeItem('pd.m.fav'); storage.removeItem('pd.m.use'); }   // die lokale Kopie wird nicht mehr gelesen; das Protokoll der Aktivitaeten bleibt
  } catch { return false; }
  return !!body;
}

/* ---------- Aufrufe an den Server ---------- */
export async function loadOrganisation(api, state) {
  const r = await api('/api/folders');
  state.folders = r.folders || [];
  mergePlacements(state.connections, r.placements);
}
export const setFavorite = (api, c, on) => api(`/api/connections/${c.id}/favorite`, { method: 'PUT', body: { favorite: !!on } }).then(() => { c.favorite = !!on; });
export const setFolder = (api, c, folderId) => api(`/api/connections/${c.id}/folder`, { method: 'PUT', body: { folderId: folderId || null } }).then(() => { c.folderId = folderId || null; });
export const touch = (api, c) => api(`/api/connections/${c.id}/used`, { method: 'POST', body: {} });
export const move = (api, c, delta) => api(`/api/connections/${c.id}/move`, { method: 'POST', body: { delta } });
