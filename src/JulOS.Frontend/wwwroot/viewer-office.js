/* =====================================================================
 * Reine Logik fuer Word, Excel und csv in der Vorschau (docs/FILES-VIEWER.md, Teil C; Darstellung in files-viewer.js). Kein DOM, kein Netz, nichts wird ausgefuehrt:
 *  - Grenzen und die Pruefung eines ZIP-Archivs VOR dem Entpacken (ZIP-Bombe): nur das Inhaltsverzeichnis wird gelesen, nichts entpackt,
 *  - ein kleiner csv-Leser, das Kuerzen grosser Tabellen, die Darstellung einzelner Zellen als Text,
 *  - die Regeln, nach denen die Ausgabe von docx-preview bereinigt wird, bevor sie in das Rahmenfenster ohne Skripte kommt, und dessen Grundgeruest.
 * ===================================================================== */

export const MiB = 1024 * 1024;
/** Was die Vorschau von einer Tabelle zeigt (Zeilen und Spalten je Blatt, Zeichen je Zelle) und was ein Office-Archiv entpackt haben darf */
export const LIMITS = { rows: 5000, cols: 50, cellChars: 2000, zipEntries: 5000, unpackedDocx: 200 * MiB, unpackedXlsx: 64 * MiB };

/* ---------- ZIP: das Inhaltsverzeichnis lesen, ohne zu entpacken ---------- */
const EOCD = 0x06054b50, CENTRAL = 0x02014b50, MAX_COMMENT = 0xffff;

/**
 * Liest aus einem ZIP (Uint8Array) das Verzeichnis am Ende: { ok: true, entries, unpacked (Summe der angegebenen entpackten Groessen), zip64 } oder { ok: false } (kein ZIP, abgeschnitten, Verzeichnis ausserhalb der Datei).
 * Die Angaben im Verzeichnis koennen luegen; die beiden Entpacker (JSZip, fflate) pruefen die Groesse beim Entpacken gegen sie und brechen bei Abweichung ab. Zip64 (Zahlen ab 4 GiB oder 65535 Eintraegen) gibt es in Office-Dateien dieser Groesse nicht: gilt als zu gross.
 */
export function zipStats(bytes) {
  const n = bytes ? bytes.length : 0;
  if (n < 22) return { ok: false };
  const dv = new DataView(bytes.buffer, bytes.byteOffset, n);
  let end = -1;
  for (let i = n - 22; i >= Math.max(0, n - 22 - MAX_COMMENT); i--) if (dv.getUint32(i, true) === EOCD) { end = i; break; }
  if (end < 0) return { ok: false };
  const entries = dv.getUint16(end + 10, true), cdSize = dv.getUint32(end + 12, true), cdOffset = dv.getUint32(end + 16, true);
  if (entries === 0xffff || cdSize === 0xffffffff || cdOffset === 0xffffffff) return { ok: true, entries, unpacked: Infinity, zip64: true };
  if (cdOffset + cdSize > end) return { ok: false };
  let at = cdOffset, unpacked = 0, zip64 = false;
  for (let i = 0; i < entries; i++) {
    if (at + 46 > end || dv.getUint32(at, true) !== CENTRAL) return { ok: false };
    const size = dv.getUint32(at + 24, true);
    if (size === 0xffffffff || dv.getUint32(at + 20, true) === 0xffffffff) zip64 = true;
    unpacked += size;
    at += 46 + dv.getUint16(at + 28, true) + dv.getUint16(at + 30, true) + dv.getUint16(at + 32, true);
  }
  if (at > end) return { ok: false };
  return { ok: true, entries, unpacked: zip64 ? Infinity : unpacked, zip64 };
}

/** 'invalid' (kein lesbares ZIP), 'unpacked' (zu viele Eintraege oder entpackt zu gross) oder null; kind: 'word' | 'sheet' */
export function zipRefusal(stats, kind) {
  if (!stats || !stats.ok) return 'invalid';
  const limit = kind === 'word' ? LIMITS.unpackedDocx : LIMITS.unpackedXlsx;
  return stats.entries > LIMITS.zipEntries || stats.unpacked > limit ? 'unpacked' : null;
}

/* ---------- Text und csv ---------- */
/** Bytes als Text: UTF-16 mit Kennung, sonst UTF-8, und ist das keins, Windows-1252 (so speichert Excel in Deutschland "CSV" ohne UTF-8) */
export function decodeText(bytes) {
  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) return new TextDecoder('utf-16le').decode(bytes);
  if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) return new TextDecoder('utf-16be').decode(bytes);
  try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); } catch { return new TextDecoder('windows-1252').decode(bytes); }
}

/** Das Trennzeichen aus der ersten Zeile (ausserhalb von Anfuehrungszeichen): Komma, Semikolon oder Tab, das haeufigste; sonst Komma */
export function detectDelimiter(text) {
  const count = { ',': 0, ';': 0, '\t': 0 };
  let quoted = false;
  for (let i = 0; i < text.length && i < 100000; i++) {
    const ch = text[i];
    if (ch === '"') quoted = !quoted;
    else if (!quoted && (ch === '\n' || ch === '\r')) break;
    else if (!quoted && ch in count) count[ch]++;
  }
  const best = Object.keys(count).reduce((a, b) => (count[b] > count[a] ? b : a), ',');
  return count[best] ? best : ',';
}

/**
 * Liest csv (RFC 4180: Anfuehrungszeichen mit "" darin, Zeilenumbrueche in Feldern, \r\n, \n oder \r) in { rows, totalRows, totalCols }. rows hat hoechstens maxRows Zeilen mit hoechstens maxCols Feldern;
 * der Rest wird nur gezaehlt, nicht gespeichert. Eine leere letzte Zeile zaehlt nicht.
 */
export function parseCsv(text, { maxRows = LIMITS.rows, maxCols = LIMITS.cols, delimiter } = {}) {
  const sep = delimiter || detectDelimiter(text);
  const rows = [];
  let totalRows = 0, totalCols = 0, row = [], field = '', quoted = false, started = false, atStart = true, cols = 0;   // atStart: am Anfang eines Feldes
  const keep = () => totalRows < maxRows && cols < maxCols;       // wird dieses Feld gespeichert?
  const endField = () => { if (keep()) row.push(field); field = ''; cols++; atStart = true; };
  const endRow = () => {
    endField();
    if (cols > totalCols) totalCols = cols;
    if (totalRows < maxRows) rows.push(row);
    totalRows++; row = []; cols = 0; started = false; atStart = true;
  };
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quoted) {
      if (ch === '"') { if (text[i + 1] === '"') { if (keep()) field += '"'; i++; } else quoted = false; }
      else if (keep()) field += ch;
    } else if (ch === '"' && atStart) { quoted = true; started = true; atStart = false; }
    else if (ch === sep) { started = true; endField(); }
    else if (ch === '\n' || ch === '\r') { if (ch === '\r' && text[i + 1] === '\n') i++; endRow(); }
    else { started = true; atStart = false; if (keep()) field += ch; }
  }
  if (started || field !== '' || cols > 0) endRow();                 // letzte Zeile ohne Zeilenumbruch
  return { rows, totalRows, totalCols };
}

/* ---------- Tabellen kuerzen und darstellen ---------- */
/** Kuerzt eine Tabelle (Zeilen von Zellen) auf die Grenzen: { rows, totalRows, totalCols, cut } (cut: es fehlt etwas) */
export function cutTable(data, { maxRows = LIMITS.rows, maxCols = LIMITS.cols } = {}) {
  const all = Array.isArray(data) ? data : [];
  let totalCols = 0;
  for (const r of all) if (Array.isArray(r) && r.length > totalCols) totalCols = r.length;
  const rows = all.slice(0, maxRows).map((r) => (Array.isArray(r) ? r.slice(0, maxCols) : []));
  return { rows, totalRows: all.length, totalCols, cut: all.length > maxRows || totalCols > maxCols };
}

/** Der Buchstabe einer Spalte wie in Excel: 0 = A, 25 = Z, 26 = AA */
export function colName(i) {
  let n = i + 1, s = '';
  while (n > 0) { const r = (n - 1) % 26; s = String.fromCharCode(65 + r) + s; n = Math.floor((n - 1) / 26); }
  return s;
}

/**
 * Eine Zelle als Text (nur Text: die Oberflaeche setzt ihn mit textContent). Zahlen ohne Gleitkomma-Rauschen (0.1+0.2 = 0.3) und in der Zahlschreibweise der Sprache, Datum als JJJJ-MM-TT (mit Uhrzeit nur,
 * wenn sie nicht null ist), Wahrheitswerte als TRUE/FALSE, alles Lange gekappt. Formeln kommen schon als ihr Ergebnis (der Leser rechnet nie).
 */
export function fmtCell(v, lang = 'en', maxChars = LIMITS.cellChars) {
  let s;
  if (v === null || v === undefined) return '';
  if (v instanceof Date) {
    if (Number.isNaN(v.getTime())) return '';
    const iso = v.toISOString();
    s = iso.slice(11, 19) === '00:00:00' ? iso.slice(0, 10) : iso.slice(0, 10) + ' ' + iso.slice(11, 16);
  } else if (typeof v === 'number') {
    if (!Number.isFinite(v)) return String(v);
    s = Number(v.toPrecision(15)).toLocaleString(lang, { useGrouping: false, maximumFractionDigits: 15 });
  } else if (typeof v === 'boolean') s = v ? 'TRUE' : 'FALSE';
  else s = String(v);
  return s.length > maxChars ? s.slice(0, maxChars) + '…' : s;
}

/* ---------- Word: Regeln fuer die Bereinigung und das Geruest des Rahmenfensters ---------- */
/** Diese Elemente kommen nie in das Rahmenfenster (sie laden, fuehren aus oder nehmen Eingaben): sie werden samt Inhalt entfernt */
const DROP = new Set(['script', 'iframe', 'frame', 'frameset', 'object', 'embed', 'applet', 'link', 'meta', 'base', 'form', 'input', 'button', 'select', 'textarea', 'option', 'audio', 'video', 'source', 'track',
  'canvas', 'noscript', 'template', 'slot', 'portal', 'dialog', 'math', 'foreignobject', 'animate', 'animatetransform', 'animatemotion', 'set', 'use', 'title', 'head', 'html', 'body']);
export const dropTag = (name) => DROP.has(String(name).toLowerCase());

/** Eine Adresse, die ein Bild AUS DER DATEI ist (data:image/...; kein Netz, kein blob:, kein javascript:) */
export const isDataImage = (v) => /^\s*data:image\/[a-z0-9.+-]+[;,]/i.test(String(v));
/** Ein style-Wert ohne Nachladen oder Ausfuehren (url() nur mit data:image, kein @import, expression, javascript:) */
export function safeStyle(v) {
  const s = String(v);
  if (/@import|expression\s*\(|javascript:|behavior\s*:|-moz-binding|<|\\/i.test(s)) return false;
  for (const m of s.matchAll(/url\s*\(\s*(['"]?)([^)]*)/gi)) if (!isDataImage(m[2])) return false;
  return true;
}
/** Ein Attribut bleibt oder faellt: Ereignisse (on...) nie, Adressen nur als eingebettetes Bild, style nur wenn sauber */
export function keepAttr(name, value) {
  const n = String(name).toLowerCase();
  if (n.startsWith('on')) return false;
  if (['href', 'src', 'xlink:href', 'srcset', 'action', 'formaction', 'background', 'poster', 'data', 'ping', 'dynsrc', 'lowsrc', 'cite', 'longdesc'].includes(n)) return isDataImage(value);
  if (n === 'style') return safeStyle(value);
  if (['srcdoc', 'sandbox', 'target', 'rel', 'is', 'contenteditable', 'autofocus', 'tabindex', 'id', 'name', 'for', 'form'].includes(n)) return false;
  return true;
}
/** CSS von docx-preview fuer das Rahmenfenster: kein @import, url() nur mit data:image, kein "<" (damit nichts aus dem style-Element ausbricht) */
export function cleanCss(css) {
  return String(css).replace(/<|\\3c/gi, ' ').replace(/@import[^;]*;?/gi, '').replace(/expression\s*\(/gi, '(').replace(/url\s*\(\s*(['"]?)([^)]*)\)/gi, (m, _q, u) => (isDataImage(u) ? m : 'none'));
}
/** Die Anzeigebreite einer Seite aus der CSS-Laenge ihres Abschnitts ("595.3pt", "21cm", "794px") in px; sonst 794 (A4) */
export function cssLengthPx(v) {
  const m = /^\s*(\d+(?:\.\d+)?)\s*(px|pt|cm|mm|in|pc)?\s*$/i.exec(String(v || ''));
  if (!m) return 794;
  const k = { px: 1, pt: 96 / 72, cm: 96 / 2.54, mm: 96 / 25.4, in: 96, pc: 16 }[(m[2] || 'px').toLowerCase()];
  const px = Number(m[1]) * k;
  return px >= 100 && px <= 20000 ? px : 794;
}
/**
 * Das Dokument fuer das Rahmenfenster (srcdoc): erst die eigene CSP (nichts laden ausser eingebetteten Bildern, keine Skripte), dann die Stile, dann der bereinigte Inhalt. Das iframe selbst hat
 * sandbox ohne allow-scripts und ohne allow-same-origin; die Seite ausserhalb kann den Inhalt nicht lesen und er sie nicht.
 */
export function frameDoc({ css, body, zoom = 1 }) {
  const z = Math.min(8, Math.max(0.1, Number(zoom) || 1));
  return '<!doctype html><html><head><meta charset="utf-8">'
    + `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'">`
    + '<style>' + cleanCss(css) + '</style>'
    // zuletzt, damit es die Stile des Dokuments uebertrifft; Seiten mit auto-Rand: mittig, und bei Zoom ueber die Breite erreichbar (zentrierendes align-items schneidet die linke Seite ab)
    + `<style>html{background:#7d7f86}body{margin:0}html .docx-wrapper{zoom:${z};align-items:flex-start}html .docx-wrapper>section.docx{margin-left:auto;margin-right:auto;flex:none}</style>`
    + '</head><body>' + body + '</body></html>';
}
