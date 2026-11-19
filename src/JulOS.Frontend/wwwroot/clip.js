/* =====================================================================
 * Zwischenablage: Bilder und Dateien (Hilfen ohne Zustand, app.js haelt den Zustand und die Oberflaeche).
 * Das Zusammenspiel der beiden Engines steht in docs/CLIPBOARD.md.
 * ===================================================================== */

import { t, fmtNumber } from './i18n.js';

/* Bilder: Grenzen im Browser. Dateien: die Grenzen nennt der Server beim Verbinden (StreamClient.limits, JULOS_CLIP_MAX_MB), hier nur der Rueckfall. */
export const LIMITS = { fileBytes: 2 * 1024 ** 3, files: 200, entries: 255, imageBytes: 40 * 1024 * 1024, imageSide: 3200 };

export function fmtSize(n) {
  if (!(n >= 0)) return '';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return Math.round(n / 1024) + ' kB';
  if (n < 1024 ** 3) return fmtNumber(n / 1024 / 1024, 1) + ' MB';
  return fmtNumber(n / 1024 ** 3, 2) + ' GB';
}

/* Ein Bild als 24-Bit-BMP (so nimmt es die RDP-Zwischenablage als CF_DIB): von unten nach oben, BGR, Zeilen auf vier Bytes aufgefuellt,
   durchsichtige Stellen auf Weiss (die meisten Programme koennen mit einem Alpha-Kanal in CF_DIB nichts anfangen). Zu grosse Bilder werden verkleinert. */
export async function bmpFromImage(blob) {
  const bmp = await createImageBitmap(blob);
  const scale = Math.min(1, LIMITS.imageSide / Math.max(bmp.width, bmp.height));
  const w = Math.max(1, Math.round(bmp.width * scale)), h = Math.max(1, Math.round(bmp.height * scale));
  const canvas = document.createElement('canvas'); canvas.width = w; canvas.height = h;
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, w, h); ctx.drawImage(bmp, 0, 0, w, h);
  if (bmp.close) bmp.close();
  const px = ctx.getImageData(0, 0, w, h).data;
  const stride = (w * 3 + 3) & ~3, size = 54 + stride * h;
  const out = new Uint8Array(size), v = new DataView(out.buffer);
  out[0] = 0x42; out[1] = 0x4d; v.setUint32(2, size, true); v.setUint32(10, 54, true);
  v.setUint32(14, 40, true); v.setInt32(18, w, true); v.setInt32(22, h, true); v.setUint16(26, 1, true); v.setUint16(28, 24, true);
  v.setUint32(34, stride * h, true); v.setInt32(38, 2835, true); v.setInt32(42, 2835, true);
  for (let y = 0; y < h; y++) {
    let o = 54 + (h - 1 - y) * stride, i = y * w * 4;
    for (let x = 0; x < w; x++, i += 4) { out[o++] = px[i + 2]; out[o++] = px[i + 1]; out[o++] = px[i]; }
  }
  return { blob: new Blob([out], { type: 'image/bmp' }), width: w, height: h };
}

/* PNG aus Bytes, die der Server als PNG oder BMP gemeldet hat (navigator.clipboard.write nimmt nur PNG) */
export async function pngFromBytes(bytes, format) {
  if (format === 1) return new Blob([bytes], { type: 'image/png' });
  const bmp = await createImageBitmap(new Blob([bytes], { type: 'image/bmp' }));
  const canvas = document.createElement('canvas'); canvas.width = bmp.width; canvas.height = bmp.height;
  canvas.getContext('2d').drawImage(bmp, 0, 0);
  if (bmp.close) bmp.close();
  const png = await new Promise((r) => canvas.toBlob(r, 'image/png'));
  if (!png) throw new Error(t('clip.imageConvertFailed'));
  return png;
}

/* Dateien aus einem Ablegen oder Einfuegen, mit Ordnern: ein Ordner wird bis in die Tiefe gelesen. Ergebnis: { items, skipped }, items in der Reihenfolge des Lesens, je
   { path, file } fuer eine Datei (path relativ, mit /) oder { path, dir: true } fuer einen leeren Ordner (Ordner mit Inhalt entstehen aus den Pfaden der Dateien).
   skipped: Namen, die sich nicht lesen liessen. Wichtig: die Eintraege des DataTransfer sind nur im Ereignis selbst gueltig, darum werden sie zuerst alle geholt. */
export async function filesFromTransfer(dt) {
  const items = [], skipped = [];
  if (!dt) return { items, skipped };
  const roots = [];
  const list = dt.items ? [...dt.items].filter((i) => i.kind === 'file') : [];
  if (list.length) {
    for (const item of list) {
      const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;
      const f = item.getAsFile ? item.getAsFile() : null;
      if (entry && entry.isDirectory) roots.push({ entry });
      else if (f) roots.push({ file: f });
      else if (entry) roots.push({ entry });
    }
  } else for (const f of dt.files || []) roots.push({ file: f });
  for (const r of roots) {
    if (r.file) { items.push({ path: r.file.name, file: r.file }); continue; }
    try { await walkEntry(r.entry, '', items); } catch { skipped.push(r.entry.name || '?'); }
  }
  return { items, skipped };
}

const MAX_WALK = 5000;       // so viele Eintraege liest der Browser hoechstens, bevor er aufgibt (die Grenzen sind weit darunter)

async function walkEntry(entry, prefix, items) {
  const path = prefix + entry.name;
  if (items.length > MAX_WALK) return;
  if (entry.isFile) { items.push({ path, file: await new Promise((ok, no) => entry.file(ok, no)) }); return; }
  if (!entry.isDirectory) return;
  const reader = entry.createReader(), children = [];
  for (;;) {                                           // readEntries liefert hoechstens 100 auf einmal
    const batch = await new Promise((ok, no) => reader.readEntries(ok, no));
    if (!batch.length) break;
    children.push(...batch);
  }
  if (!children.length) { items.push({ path, dir: true }); return; }
  for (const child of children) await walkEntry(child, path + '/', items);
}

/* Dateien aus einem Auswahlfeld (auch mit webkitdirectory: dann steht der Pfad in webkitRelativePath) */
export function itemsFromFiles(files) {
  return [...files].filter((f) => f && f.size !== undefined).map((f) => ({ path: f.webkitRelativePath || f.name, file: f }));
}

/* Prueft eine Auswahl gegen die Grenzen des Servers und macht daraus die Liste zum Senden: { ok, list } oder { ok: false, error }.
   list: je { kind: 2 (Datei) oder 3 (leerer Ordner), path, file }, Dateien vor leeren Ordnern. */
export function planSend(items, limits = LIMITS) {
  const files = items.filter((i) => i.file), dirs = items.filter((i) => i.dir);
  if (!files.length && !dirs.length) return { ok: false, error: '' };
  if (files.length > limits.files) return { ok: false, error: t('clip.tooManyFiles', { max: limits.files }) };
  if (files.length + dirs.length > limits.entries) return { ok: false, error: t('clip.tooManyEntries', { max: limits.entries }) };
  const big = files.find((i) => i.file.size > limits.fileBytes);
  const total = files.reduce((n, i) => n + i.file.size, 0);
  if (big || total > limits.fileBytes) return { ok: false, error: t('clip.fileTooBig', { max: fmtSize(limits.fileBytes) }) };
  if (files.length === 1 && !dirs.length && files[0].file.size === 0) return { ok: false, error: t('clip.fileEmpty') };
  const names = new Set();
  for (const i of items) {
    const key = i.path.toLowerCase();
    if (names.has(key)) return { ok: false, error: t('clip.duplicateName', { path: i.path }) };
    names.add(key);
    if (i.path.length > 259) return { ok: false, error: t('clip.pathTooLong', { path: i.path.slice(0, 40) }) };
  }
  return { ok: true, list: [...files.map((i) => ({ kind: 2, path: i.path, file: i.file })), ...dirs.map((i) => ({ kind: 3, path: i.path, file: null }))], total };
}

/* Die Auswahl der VM (Liste aus Nachricht 8: { name: Pfad mit /, size, dir }) als Zeilen der obersten Ebene: eine Datei, oder ein Ordner mit allem darin (auch dann, wenn
   die VM nur die Dateien nannte und der Ordner sich aus den Pfaden ergibt). Je Zeile: { name, path, dir, size (Summe, -1 wenn eine Groesse unbekannt ist), count (Dateien) }. */
export function topLevel(list) {
  const rows = new Map();
  for (const f of list) {
    const cut = f.name.indexOf('/'), top = cut < 0 ? f.name : f.name.slice(0, cut);
    let row = rows.get(top);
    if (!row) { row = { name: top, path: top, dir: cut >= 0 || !!f.dir, size: 0, count: 0 }; rows.set(top, row); }
    if (cut >= 0 || f.dir) row.dir = true;
    if (!f.dir) { row.count++; row.size = row.size < 0 || f.size < 0 ? -1 : row.size + f.size; }
  }
  return [...rows.values()];
}

/* Fortschritt in Worten: "12,3 MB von 40,0 MB (31 %)" oder nur "12,3 MB" */
export function progressText(done, total) {
  if (!(total > 0)) return fmtSize(done);
  return t('clip.progressOf', { done: fmtSize(done), total: fmtSize(total), pct: Math.min(100, Math.round(100 * done / total)) });
}

/* Datei speichern: ein Link mit download, so geht es auch am Handy */
export function saveBlob(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a'); a.href = url; a.download = name; a.style.display = 'none';
  document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

/* Einen Download der VM abholen: der Browser holt das Ticket als normale Anfrage (Download-Verwaltung, kein Speicher der Seite). */
export function startDownload(token, name = '') {
  const a = document.createElement('a'); a.href = '/api/remote/clip/' + encodeURIComponent(token); a.download = name; a.style.display = 'none';
  document.body.append(a); a.click(); a.remove();
}
