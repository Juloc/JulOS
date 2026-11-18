/* =====================================================================
 * Zwischenablage: Bilder und Dateien (Hilfen ohne Zustand, app.js haelt den Zustand und die Oberflaeche).
 * Das Zusammenspiel der beiden Engines steht in docs/CLIPBOARD.md.
 * ===================================================================== */

export const LIMITS = { fileBytes: 64 * 1024 * 1024, files: 10, imageBytes: 40 * 1024 * 1024, imageSide: 3200 };

export function fmtSize(n) {
  if (!(n >= 0)) return '';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return Math.round(n / 1024) + ' kB';
  return (n / 1024 / 1024).toFixed(1).replace('.', ',') + ' MB';
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
  if (!png) throw new Error('Bild konnte nicht umgewandelt werden');
  return png;
}

/* Dateien aus einem Ablegen oder Einfuegen: Ordner werden uebersprungen (ihre Eintraege waeren nur ein leeres "File") */
export function filesFromTransfer(dt) {
  const files = [], skipped = [];
  if (!dt) return { files, skipped };
  const items = dt.items ? [...dt.items].filter((i) => i.kind === 'file') : [];
  if (items.length) {
    for (const item of items) {
      const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;
      const f = item.getAsFile();
      if (entry && entry.isDirectory) skipped.push(entry.name);
      else if (f) files.push(f);
    }
  } else for (const f of dt.files || []) files.push(f);
  return { files, skipped };
}

/* Upload mit Fortschritt und Abbruch (fetch kennt beides beim Senden nicht): Antwort { status, body } mit dem JSON des Servers */
export function xhrUpload(url, headers, blob, progress, signal) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    for (const [k, v] of Object.entries(headers)) xhr.setRequestHeader(k, v);
    xhr.upload.onprogress = (e) => { if (progress && e.lengthComputable) progress(e.loaded, e.total); };
    xhr.onload = () => { let body = {}; try { body = JSON.parse(xhr.responseText); } catch { /* keine JSON-Antwort */ } resolve({ status: xhr.status, body }); };
    xhr.onerror = () => reject(new Error('Keine Verbindung zum Server.'));
    xhr.onabort = () => reject(new Error('Abgebrochen.'));
    if (signal) signal.addEventListener('abort', () => xhr.abort(), { once: true });
    xhr.send(blob);
  });
}

/* Datei speichern: ein Link mit download, so geht es auch am Handy */
export function saveBlob(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a'); a.href = url; a.download = name; a.style.display = 'none';
  document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}
