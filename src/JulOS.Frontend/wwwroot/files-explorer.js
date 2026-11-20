/* =====================================================================
 * Explorer der Dateien-App (docs/FILES-EXPLORER.md): die reine Logik ohne DOM, damit Desktop, Handy und die Tests sie teilen.
 *  - Auswahl wie im Windows-Explorer (Einfachklick, Strg, Umschalt, Strg+A, Pfeiltasten, Rahmen)
 *  - Buchstabensprung (Eintippen springt zum ersten passenden Eintrag, Puffer etwa eine Sekunde)
 *  - Sortierung (Ordner zuerst, natuerliche Reihenfolge), Typ aus der Endung, Anzeigename (Steuer- und Richtungszeichen unschaedlich)
 *  - Ansicht, angeheftete Ordner und Auf-/Zuklappzustand (pro Benutzer auf dem Server, Einstellung "files"; die Regeln des Servers stehen in SettingsRules.cs)
 *  - Zwischenablage und Ziehen (Ausschneiden, Kopieren, Einfuegen, auch zwischen Hosts)
 * Namen vom Zielhost sind fremde Daten: nichts hier erzeugt HTML.
 * ===================================================================== */

/* ---------- Anzeigenamen ---------- */
// Steuerzeichen, Richtungsumkehr (U+202A-202E, U+2066-2069), unsichtbare Markierungen: sie werden sichtbar als <U+XXXX> gezeigt, nie wirksam
const unsafeCp = (c) => c <= 0x1f || (c >= 0x7f && c <= 0x9f) || (c >= 0x200b && c <= 0x200f) || (c >= 0x2028 && c <= 0x202e) || (c >= 0x2060 && c <= 0x2064) || (c >= 0x2066 && c <= 0x2069) || c === 0xfeff;
export const safeName = (name) => [...String(name ?? '')].map((ch) => (unsafeCp(ch.codePointAt(0)) ? `<U+${ch.codePointAt(0).toString(16).toUpperCase().padStart(4, '0')}>` : ch)).join('');
export const hasUnsafe = (name) => [...String(name ?? '')].some((ch) => unsafeCp(ch.codePointAt(0)));

/** Name und Endung; ".bashrc" hat keine Endung, "a.tar.gz" die Endung "gz" */
export function splitName(name) {
  const s = String(name ?? '');
  const i = s.lastIndexOf('.');
  return i <= 0 || i === s.length - 1 ? { base: s, ext: '' } : { base: s.slice(0, i), ext: s.slice(i + 1).toLowerCase() };
}
export const isHiddenName = (name) => String(name ?? '').startsWith('.');

/* ---------- Typ ---------- */
const TYPES = {
  text: ['txt', 'md', 'log', 'conf', 'cfg', 'ini', 'yml', 'yaml', 'toml', 'json', 'xml', 'csv', 'tsv', 'env', 'service', 'timer', 'rules', 'list', 'sql'],
  code: ['js', 'mjs', 'ts', 'py', 'sh', 'bash', 'c', 'h', 'cpp', 'cs', 'java', 'go', 'rs', 'rb', 'php', 'html', 'htm', 'css', 'lua', 'pl'],
  image: ['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'svg', 'ico', 'tif', 'tiff', 'heic', 'avif'],
  audio: ['mp3', 'wav', 'flac', 'ogg', 'm4a', 'opus', 'aac'],
  video: ['mp4', 'mkv', 'avi', 'mov', 'webm', 'wmv'],
  archive: ['zip', 'tar', 'gz', 'tgz', 'bz2', 'xz', '7z', 'rar', 'zst', 'deb', 'rpm', 'iso', 'img'],
  doc: ['pdf', 'doc', 'docx', 'odt', 'xls', 'xlsx', 'ods', 'ppt', 'pptx', 'odp', 'epub'],
};
const BINARY_EXT = new Set([...TYPES.image.filter((x) => x !== 'svg'), ...TYPES.audio, ...TYPES.video, ...TYPES.archive, ...TYPES.doc, 'exe', 'dll', 'so', 'bin', 'o', 'class', 'jar', 'db', 'sqlite', 'woff', 'woff2', 'ttf', 'otf', 'dat']);
/** Art eines Eintrags: dir, link, other, oder nach Endung text|code|image|audio|video|archive|doc|file */
export function kindOf(e) {
  if (e.kind === 'directory') return 'dir';
  if (e.kind === 'link') return e.resolved === 'directory' ? 'dir' : 'link';
  if (e.kind === 'other') return 'other';
  const { ext } = splitName(e.name);
  for (const [k, list] of Object.entries(TYPES)) if (list.includes(ext)) return k;
  return 'file';
}
/** Schluessel des Typ-Texts (i18n) und die Endung; der Aufrufer macht daraus "Textdatei" oder "PNG-Datei" */
export function typeKey(e) {
  const k = kindOf(e);
  if (k === 'dir') return { key: 'files.type.dir', ext: '' };
  if (k === 'link') return { key: 'files.type.link', ext: '' };
  if (k === 'other') return { key: 'files.type.other', ext: '' };
  const { ext } = splitName(e.name);
  return ext ? { key: 'files.type.ext', ext: ext.toUpperCase() } : { key: 'files.type.file', ext: '' };
}
/** Sortierschluessel fuer die Spalte Typ: die Endung (ohne Endung zuerst) */
const typeSort = (e) => (kindOf(e) === 'dir' ? '' : splitName(e.name).ext);

/** Soll diese Datei im Editor aufgehen? Nur Dateien (oder Links auf Dateien) bis 1 MiB, nicht mit einer bekannten Binaer-Endung; der Server prueft den Inhalt noch einmal (not_text) */
export const EDIT_MAX_BYTES = 1024 * 1024;
export function editable(e) {
  if (!(e.kind === 'file' || (e.kind === 'link' && e.resolved === 'file'))) return false;
  if ((e.size || 0) > EDIT_MAX_BYTES) return false;
  return !BINARY_EXT.has(splitName(e.name).ext);
}

/* ---------- Sortierung ---------- */
export const SORT_COLS = ['name', 'date', 'type', 'size'];
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });
const isDir = (e) => e.openable || e.kind === 'directory';
/** Neue Liste: Ordner (und Links auf Ordner) immer zuerst, dann nach der Spalte; bei Gleichstand nach dem Namen */
export function sortEntries(entries, { col = 'name', dir = 'asc' } = {}) {
  const sign = dir === 'desc' ? -1 : 1;
  const cmp = (a, b) => {
    const da = isDir(a), db = isDir(b);
    if (da !== db) return da ? -1 : 1;
    let r = 0;
    if (col === 'date') r = (Date.parse(a.modified) || 0) - (Date.parse(b.modified) || 0);
    else if (col === 'size') r = da && db ? 0 : (a.size || 0) - (b.size || 0);
    else if (col === 'type') r = collator.compare(typeSort(a), typeSort(b));
    else r = collator.compare(a.name, b.name);
    if (r !== 0) return r * sign;
    return collator.compare(a.name, b.name) || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
  };
  return [...entries].sort(cmp);
}

/** Die Liste, wie sie gezeigt wird: auf Wunsch ohne versteckte Dateien (Name beginnt mit einem Punkt), sortiert */
export function visibleEntries(entries, { showHidden = true, sort } = {}) {
  return sortEntries(showHidden ? entries : entries.filter((e) => !isHiddenName(e.name)), sort);
}

/* ---------- Auswahl ---------- */
/**
 * Auswahl und Fokus einer Liste. paths: die Pfade in Anzeigereihenfolge (wird bei jeder Aenderung der Liste neu gesetzt). Verhalten wie im Explorer:
 *  Klick: nur dieser; Strg+Klick: umschalten; Umschalt+Klick: Bereich vom Anker; Strg+Umschalt+Klick: Bereich dazu.
 *  Pfeile: nur der Nachbar; Umschalt+Pfeil: Bereich vom Anker; Strg+Pfeil: Fokus ohne Auswahl (Leertaste schaltet um).
 */
export class ListSelection {
  constructor() { this.paths = []; this.set = new Set(); this.anchor = null; this.focus = null; }
  /** Neue Liste; was nicht mehr da ist, faellt aus Auswahl, Anker und Fokus */
  setPaths(paths) {
    this.paths = paths;
    const have = new Set(paths);
    this.set = new Set([...this.set].filter((p) => have.has(p)));
    if (!have.has(this.anchor)) this.anchor = null;
    if (!have.has(this.focus)) this.focus = null;
  }
  has(p) { return this.set.has(p); }
  get size() { return this.set.size; }
  list() { return this.paths.filter((p) => this.set.has(p)); }
  clear() { this.set = new Set(); }
  index(p) { return this.paths.indexOf(p); }
  _range(a, b) { const i = this.index(a), j = this.index(b); if (i < 0 || j < 0) return []; return this.paths.slice(Math.min(i, j), Math.max(i, j) + 1); }
  /** Ein Klick (auch Antippen im Mehrfachmodus: toggle) */
  click(p, { shift = false, ctrl = false } = {}) {
    if (this.index(p) < 0) return;
    if (shift && this.anchor !== null && this.index(this.anchor) >= 0) {
      const range = this._range(this.anchor, p);
      this.set = ctrl ? new Set([...this.set, ...range]) : new Set(range);
    } else if (ctrl) {
      if (this.set.has(p)) this.set.delete(p); else this.set.add(p);
      this.anchor = p;
    } else { this.set = new Set([p]); this.anchor = p; }
    this.focus = p;
  }
  /** Rechtsklick: liegt der Eintrag nicht in der Auswahl, wird er allein ausgewaehlt; sonst bleibt die Auswahl */
  context(p) { if (this.index(p) < 0) return; if (!this.set.has(p)) { this.set = new Set([p]); this.anchor = p; } this.focus = p; }
  selectAll() { this.set = new Set(this.paths); if (this.paths.length) { this.anchor = this.anchor ?? this.paths[0]; this.focus = this.focus ?? this.paths[0]; } }
  /** Pfeiltasten und Pos1/Ende/Seite: by = Schritt (+1, -1, Seitengroesse, -Seitengroesse) oder 'first'/'last'. Gibt den neuen Fokus zurueck (oder null). */
  move(by, { shift = false, ctrl = false } = {}) {
    const n = this.paths.length; if (!n) return null;
    let i = this.focus === null ? -1 : this.index(this.focus);
    if (by === 'first') i = 0; else if (by === 'last') i = n - 1;
    else if (i < 0) i = by > 0 ? 0 : n - 1;
    else i = Math.max(0, Math.min(n - 1, i + by));
    const p = this.paths[i];
    if (ctrl) { this.focus = p; return p; }
    if (shift) { if (this.anchor === null) this.anchor = this.focus ?? p; this.set = new Set(this._range(this.anchor, p)); }
    else { this.set = new Set([p]); this.anchor = p; }
    this.focus = p;
    return p;
  }
  /** Fokus direkt setzen und allein auswaehlen (Buchstabensprung) */
  jump(p) { if (this.index(p) < 0) return; this.set = new Set([p]); this.anchor = p; this.focus = p; }
  /** Strg+Leertaste: den fokussierten Eintrag umschalten */
  toggleFocus() { if (this.focus !== null) { this.click(this.focus, { ctrl: true }); } }
  /** Rahmen: ersetzt die Auswahl durch die beruehrten Pfade (mit additive: dazu) */
  setFromBand(hit, base = null) { this.set = new Set([...(base ? base : []), ...hit]); if (hit.length) { this.focus = hit[hit.length - 1]; if (this.anchor === null) this.anchor = hit[0]; } }
}

/** Welche Rechtecke schneidet der Rahmen? rects: [{ path, top, bottom, left, right }], band: { top, bottom, left, right } */
export function rectsInBand(rects, band) {
  const t = Math.min(band.top, band.bottom), b = Math.max(band.top, band.bottom), l = Math.min(band.left, band.right), r = Math.max(band.left, band.right);
  return rects.filter((x) => x.bottom >= t && x.top <= b && x.right >= l && x.left <= r).map((x) => x.path);
}

/* ---------- Buchstabensprung ---------- */
export const TYPE_AHEAD_MS = 1000;
/**
 * Ein Zustand fuer den Buchstabensprung. press(key, now, names, from) gibt den Index des Treffers oder -1.
 *  - Ein Buchstabe springt zum ersten Eintrag, der so beginnt (ab dem naechsten nach dem aktuellen, bei Wiederholung desselben Buchstabens reihum).
 *  - Mehrere Buchstaben innerhalb einer Sekunde verfeinern: "do" sucht "do...", beginnend beim aktuellen Eintrag.
 * Gross- und Kleinschreibung ist egal. names: die Namen in Anzeigereihenfolge.
 */
export function createTypeAhead(ms = TYPE_AHEAD_MS) {
  let buf = '', last = -Infinity;
  const find = (names, text, start) => {
    const n = names.length, needle = text.toLowerCase();
    for (let k = 0; k < n; k++) { const i = (start + k) % n; if (String(names[i]).toLowerCase().startsWith(needle)) return i; }
    return -1;
  };
  return {
    press(key, now, names, from = -1) {
      if (typeof key !== 'string' || [...key].length !== 1 || key === ' ' && !buf) return -1;
      if (now - last > ms) buf = '';
      last = now;
      const prev = buf;
      buf += key;
      const same = buf.length > 1 && [...buf].every((c) => c.toLowerCase() === buf[0].toLowerCase());
      if (same) {                                                   // "aaa" heisst: weiter reihum durch alle mit "a"
        buf = buf[0];
        return find(names, buf, from + 1);
      }
      // Verfeinern beginnt beim aktuellen Eintrag, ein neuer Anfang beim naechsten
      const hit = find(names, buf, prev ? Math.max(from, 0) : from + 1);
      if (hit < 0 && prev) { buf = key; return find(names, buf, from + 1); }
      return hit;
    },
    reset() { buf = ''; last = -Infinity; },
    get buffer() { return buf; },
  };
}

/* ---------- Kurzbefehle der Tastatur ---------- */
/** Aus einem Tastaturereignis (Felder key, ctrlKey, metaKey, shiftKey, altKey) die Aktion oder null. Der Aufrufer prueft vorher, dass kein Eingabefeld den Fokus hat. */
export function actionForKey(e) {
  const ctrl = e.ctrlKey || e.metaKey, k = e.key;
  if (e.altKey && !ctrl) return k === 'ArrowLeft' ? 'back' : k === 'ArrowRight' ? 'forward' : k === 'ArrowUp' ? 'up' : null;
  if (ctrl && !e.altKey) {
    const l = k.length === 1 ? k.toLowerCase() : k;
    if (l === 'a') return 'selectAll';
    if (l === 'c') return 'copy';
    if (l === 'x') return 'cut';
    if (l === 'v') return 'paste';
    if (l === 's') return 'save';
    if (l === 'n' && e.shiftKey) return 'newFolder';
    if (l === 'l') return 'address';
    if (l === 'f') return 'search';
    if (l === ' ') return 'toggle';
    if (k === 'ArrowDown') return 'open';
    if (k === 'ArrowUp' || k === 'ArrowLeft' || k === 'ArrowRight' || k === 'Home' || k === 'End') return 'ctrlMove';
    return null;
  }
  switch (k) {
    case 'Enter': return 'open';
    case 'Backspace': return 'up';
    case 'F2': return 'rename';
    case 'Delete': return 'delete';
    case 'F5': return 'refresh';
    case 'Escape': return 'clear';
    case 'ArrowUp': case 'ArrowDown': case 'ArrowLeft': case 'ArrowRight': case 'Home': case 'End': case 'PageUp': case 'PageDown': return 'move';
    case 'ContextMenu': return 'menu';
    case 'F10': return e.shiftKey ? 'menu' : null;
    default: return null;
  }
}

/* ---------- Ansicht und Pins (Einstellung "files" auf dem Server) ---------- */
export const DEFAULT_COLS = { name: 240, date: 140, type: 110, size: 80 };
export const COL_MIN = 60, COL_MAX = 800;
export const MAX_PINS = 100;
export const DEFAULT_VIEW = { mode: 'list', density: 'compact', sortCol: 'name', sortDir: 'asc', showHidden: true, cols: { ...DEFAULT_COLS } };
const MODES = ['list', 'tiles'], DENSITIES = ['compact', 'comfy'], DIRS = ['asc', 'desc'];

/** Die Ansicht aus einem unbekannten Wert: nur Bekanntes, alles andere der Standard */
export function normalizeView(raw) {
  const v = raw && typeof raw === 'object' ? raw : {};
  const cols = { ...DEFAULT_COLS };
  if (v.cols && typeof v.cols === 'object') for (const c of Object.keys(DEFAULT_COLS)) { const w = Number(v.cols[c]); if (Number.isFinite(w)) cols[c] = Math.max(COL_MIN, Math.min(COL_MAX, Math.round(w))); }
  return {
    mode: MODES.includes(v.mode) ? v.mode : DEFAULT_VIEW.mode,
    density: DENSITIES.includes(v.density) ? v.density : DEFAULT_VIEW.density,
    sortCol: SORT_COLS.includes(v.sortCol) ? v.sortCol : DEFAULT_VIEW.sortCol,
    sortDir: DIRS.includes(v.sortDir) ? v.sortDir : DEFAULT_VIEW.sortDir,
    showHidden: typeof v.showHidden === 'boolean' ? v.showHidden : DEFAULT_VIEW.showHidden,
    cols,
  };
}
/** Klick auf eine Spaltenkopfzeile: dieselbe Spalte kehrt um, eine andere beginnt aufsteigend (Groesse und Datum: absteigend ist meist gewollt, aber der Explorer beginnt aufsteigend) */
export const nextSort = (view, col) => (view.sortCol === col ? { ...view, sortDir: view.sortDir === 'asc' ? 'desc' : 'asc' } : { ...view, sortCol: col, sortDir: 'asc' });

export const pinKey = (p) => `${p.place}\u0000${p.path}`;
/** Pins: [{ place, path, name }], hoechstens MAX_PINS, ohne Doppelte, nur Text */
export function normalizePins(raw) {
  const out = [], seen = new Set();
  for (const p of Array.isArray(raw) ? raw : []) {
    if (!p || typeof p.place !== 'string' || typeof p.path !== 'string' || !p.place || p.path.length > 4096) continue;
    const pin = { place: p.place.slice(0, 64), path: p.path, name: typeof p.name === 'string' ? p.name.slice(0, 255) : '' };
    if (seen.has(pinKey(pin))) continue;
    seen.add(pinKey(pin)); out.push(pin);
    if (out.length >= MAX_PINS) break;
  }
  return out;
}
export const isPinned = (pins, place, path) => pins.some((p) => p.place === place && p.path === path);
export const addPin = (pins, pin) => (isPinned(pins, pin.place, pin.path) || pins.length >= MAX_PINS ? pins : [...pins, pin]);
export const removePin = (pins, place, path) => pins.filter((p) => !(p.place === place && p.path === path));
/** Einen Pin um `by` Stellen verschieben, aber nur unter den Pins desselben Orts (ihre Reihenfolge untereinander zaehlt) */
export function movePin(pins, place, path, by) {
  const idx = pins.map((p, i) => (p.place === place ? i : -1)).filter((i) => i >= 0);
  const at = idx.findIndex((i) => pins[i].path === path);
  const to = at + by;
  if (at < 0 || to < 0 || to >= idx.length) return pins;
  const out = [...pins];
  [out[idx[at]], out[idx[to]]] = [out[idx[to]], out[idx[at]]];
  return out;
}
/** Ein Pin auf die Stelle vor einem anderen (Ziehen in der Seitenleiste); before null: ans Ende des Orts */
export function reorderPin(pins, from, beforePin) {
  const src = pins.find((p) => pinKey(p) === pinKey(from));
  if (!src) return pins;
  const rest = pins.filter((p) => p !== src);
  if (!beforePin) { const last = rest.reduce((a, p, i) => (p.place === src.place ? i : a), -1); rest.splice(last + 1, 0, src); return rest; }
  const at = rest.findIndex((p) => pinKey(p) === pinKey(beforePin));
  if (at < 0) return pins;
  rest.splice(at, 0, src);
  return rest;
}
/** Zugeklappte Knoten: eine Liste von Kennungen (alles andere ist aufgeklappt) */
export const normalizeClosed = (raw) => [...new Set((Array.isArray(raw) ? raw : []).filter((x) => typeof x === 'string' && x && x.length <= 80))].slice(0, 200);
export const toggleClosed = (closed, id) => (closed.includes(id) ? closed.filter((x) => x !== id) : [...closed, id]);

/** Der Zustand, wie ihn der Server speichert */
export function normalizePrefs(raw) {
  const r = raw && typeof raw === 'object' ? raw : {};
  return { view: normalizeView(r.view), pins: normalizePins(r.pins), closed: normalizeClosed(r.closed) };
}

/* ---------- Zwischenablage ---------- */
/** Die Zwischenablage der App: { mode: 'copy'|'cut', place, paths } (nur Pfade, nie Inhalt) */
export function makeClip(mode, placeId, entries) { return entries.length ? { mode, place: placeId, paths: entries.map((e) => e.path) } : null; }
/** Einfuegen in dasselbe Verzeichnis ausschneiden ergibt nichts zu tun: gleicher Ort, gleiche Elternordner */
export function pasteIsNoop(clip, placeId, dir, parentOfFn) {
  return !!clip && clip.mode === 'cut' && clip.place === placeId && clip.paths.every((p) => parentOfFn(p) === dir);
}
/** Darf `paths` nach `dir` (gleicher Ort)? Ein Ordner nie in sich selbst oder einen seiner Unterordner */
export function dropIntoSelf(paths, dir) { return paths.some((p) => dir === p || dir.startsWith(p.endsWith('/') ? p : p + '/')); }

/* ---------- Pfadleiste ---------- */
/** Aus einem getippten Pfad den normalisierten: Hosts absolut ("/a/b"), der gemeinsame Ordner relativ ("a/b"); "~" bleibt (der Server loest es auf) */
export function parseTypedPath(text, local) {
  let s = String(text ?? '').trim().replace(/\\/g, '/');
  if (!s) return local ? '' : '/';
  const parts = [];
  for (const seg of s.split('/')) { if (!seg || seg === '.') continue; if (seg === '..') parts.pop(); else parts.push(seg); }
  if (local) return parts.join('/');
  return s.startsWith('~') ? s : '/' + parts.join('/');
}

/* ---------- Statusleiste ---------- */
/** Anzahl und Gesamtgroesse der Auswahl: { count, files, dirs, bytes } (Ordner zaehlen ihren Inhalt nicht) */
export function selectionStats(entries) {
  let files = 0, dirs = 0, bytes = 0;
  for (const e of entries) { if (isDir(e)) dirs++; else { files++; bytes += e.size || 0; } }
  return { count: entries.length, files, dirs, bytes };
}
