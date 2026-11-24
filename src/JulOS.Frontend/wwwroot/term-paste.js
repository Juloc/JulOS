/* =====================================================================
 * Terminal: Bild oder Datei einfuegen oder ablegen (Tests: tests/frontend/term-paste.test.mjs). Reine Logik ohne Oberflaeche:
 *  - parseOsc7: der Ordner, den die Shell mit OSC 7 meldet (Ordner fuer den Upload);
 *  - shellQuote / quotePaths: der Pfad, der nach dem Upload auf die Kommandozeile geschrieben wird;
 *  - pasteName, cleanName, planNames: Dateinamen;
 *  - chooseClipboard: Text oder Dateien, wenn beides in der Zwischenablage liegt.
 * Der Upload selbst geht ueber den bestehenden SFTP-Weg der Dateien-App (term-upload.js); nichts davon ist eine Grenze, der Server prueft alles noch einmal.
 * ===================================================================== */

const hasControl = (s) => /[\u0000-\u001f\u007f-\u009f]/.test(s);

/**
 * OSC 7 ("file://host/pfad", von der Shell nach jedem Befehl gesendet). Gibt { host, path } oder null, wenn es kein absoluter Pfad ist (relativ, "..", Steuerzeichen,
 * zu lang, falsches Schema, kaputte Prozent-Kodierung). Der Host-Teil wird nicht bewertet, nur zurueckgegeben (sameHost entscheidet).
 */
export function parseOsc7(data) {
  if (typeof data !== 'string' || data.length > 8192) return null;
  const m = /^file:\/\/([^/]*)(\/.*)$/i.exec(data);
  if (!m) return null;
  let path;
  try { path = decodeURIComponent(m[2]); } catch { return null; }
  if (hasControl(path) || path.length > 4096) return null;
  const parts = path.split('/');
  if (parts.includes('..')) return null;
  const clean = '/' + parts.filter((p) => p && p !== '.').join('/');
  let host = m[1];
  try { host = decodeURIComponent(host); } catch { return null; }
  return { host: host.toLowerCase(), path: clean };
}

/**
 * Meldet die Shell diesen Rechner? Leer und "localhost" immer; sonst der Name, den die erste Meldung dieser Sitzung genannt hat (known) oder die Adresse der Verbindung.
 * Ein weiteres "ssh" im Terminal meldet einen anderen Namen: dessen Ordner gibt es auf diesem Rechner nicht.
 */
export function sameHost(host, known, address = '') {
  if (!host || host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host === '::1') return true;
  return host === (known || '').toLowerCase() || host === String(address || '').toLowerCase();
}

/** Ein Wort fuer die POSIX-Shell: einfache Anfuehrungszeichen (jedes ' wird '\'' ), nur sichere Zeichen bleiben blank; ein fuehrendes - bekommt ./ davor, damit es kein Schalter wird. */
export function shellQuote(arg) {
  const s = String(arg);
  const word = s.startsWith('-') ? './' + s : s;
  if (word && /^[A-Za-z0-9_./,:@%+=~-]+$/.test(word) && !word.startsWith('~')) return word;
  return "'" + word.replace(/'/g, "'\\''") + "'";
}

/** Pfade fuer die Kommandozeile: einzeln gequotet, mit Leerzeichen getrennt, ein Leerzeichen am Ende. Mit Steuerzeichen (Zeilenumbruch, Tab) null: die wuerden beim Schreiben Enter oder Vervollstaendigen ausloesen. */
export function quotePaths(paths) {
  if (!paths.length || paths.some((p) => typeof p !== 'string' || !p.startsWith('/') || hasControl(p))) return null;
  return paths.map(shellQuote).join(' ') + ' ';
}

const two = (n) => String(n).padStart(2, '0');
/** Name eines Bildes aus der Zwischenablage: paste-YYYYMMDD-HHMMSS.png (Ortszeit). */
export function pasteName(d = new Date()) {
  return `paste-${d.getFullYear()}${two(d.getMonth() + 1)}${two(d.getDate())}-${two(d.getHours())}${two(d.getMinutes())}${two(d.getSeconds())}.png`;
}

/** Ein Dateiname, wie ihn der Server annimmt: keine Steuerzeichen und kein "/" (werden "_"), nicht leer, "." und ".." nicht, hoechstens 255 Byte. */
export function cleanName(name) {
  let n = String(name || '').replace(/[\u0000-\u001f\u007f-\u009f/\\]/g, '_').trim();
  if (!n || n === '.' || n === '..') n = 'datei';
  const enc = new TextEncoder();
  while (enc.encode(n).length > 255) n = [...n].slice(0, -1).join('');
  return n;
}

/**
 * Die Namen fuer einen Stapel: Bilder der Zwischenablage ("image.png", leer oder ohne Namen) heissen paste-<Zeit>.png, alle Namen werden bereinigt und
 * eindeutig (ein zweites gleiches "a.png" wird "a-2.png"). Gibt [{ file, name }].
 */
export function planNames(files, now = new Date()) {
  const used = new Set(), out = [];
  for (const f of files) {
    let name = f.name && !(f.fromClipboard && /^image\.\w+$/i.test(f.name)) ? cleanName(f.name) : pasteName(now);
    if (used.has(name)) {
      const dot = name.lastIndexOf('.'), stem = dot > 0 ? name.slice(0, dot) : name, ext = dot > 0 ? name.slice(dot) : '';
      let i = 2; while (used.has(`${stem}-${i}${ext}`)) i++;
      name = `${stem}-${i}${ext}`;
    }
    used.add(name); out.push({ file: f, name });
  }
  return out;
}

/** Liegt beides in der Zwischenablage, gewinnt der Text, ausser es ist eine Datei dabei, die kein Bild ist (Datei kopiert im Dateimanager: Text ist dann nur ihr Name). */
export function chooseClipboard({ files = [], text = '' }) {
  if (!files.length) return 'text';
  if (!text) return 'files';
  return files.some((f) => !/^image\//.test(f.type || '')) ? 'files' : 'text';
}
