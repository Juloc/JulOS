/* =====================================================================
 * Vorschau der Dateien-App (docs/FILES-VIEWER.md, Teil C): Bild, Video, Audio, PDF, Word, Excel und csv in einem Fenster (Desktop) oder Vollbild-Blatt (Handy).
 * Die Daten kommen NUR von den Vorschau-Adressen des Servers (GET /api/files/preview, /api/files/remote/preview; Sitzung noetig). Der Server liefert nur eine feste Liste von Typen, mit
 * festem Content-Type, nosniff und strenger CSP; hier wird nie Inhalt als Seite behandelt: Bild und Medien als <img>/<video>/<audio>, ein PDF mit pdf.js (vendored, eigene Seite,
 * Leinwand, kein Skript, kein eval), ein Word-Dokument als bereinigter, von docx-preview gebauter Baum NUR in einem iframe mit sandbox (kein Skript, keine Herkunft) und eigener CSP, eine Tabelle
 * nur ueber textContent. Sonst landet nichts aus einer Datei in innerHTML. Blob-Adressen und Strome werden beim Wechseln und Schliessen freigegeben.
 * Root-Orte: alles ausser Video und Audio, nach der Bestaetigung (prepare "preview", Token gilt einmal): die Datei wird einmal geholt und aus einem Blob gezeigt.
 * Oben die reine Logik (Typ nach Endung, Grenzen, Nachbarn, Zoom), darunter die Oberflaeche.
 * ===================================================================== */
import { t, getLang } from './i18n.js';
import * as C from './files-core.js';
import { h, icon, modal, noteEl } from './files-ui.js';
import { safeName } from './files-explorer.js';
import { LIMITS, zipStats, zipRefusal, parseCsv, decodeText, cutTable, colName, fmtCell, dropTag, keepAttr, cleanCss, cssLengthPx, frameDoc } from './viewer-office.js';

/* ---------- reine Logik ---------- */
/** Dieselbe feste Liste wie am Server (Files/FilePreview.cs); der Server prueft sie noch einmal und zusaetzlich den Anfang der Datei */
const KINDS = {
  png: 'image', jpg: 'image', jpeg: 'image', gif: 'image', webp: 'image', avif: 'image',
  mp4: 'video', webm: 'video',
  mp3: 'audio', ogg: 'audio', wav: 'audio', flac: 'audio', m4a: 'audio',
  pdf: 'pdf',
  docx: 'word', xlsx: 'sheet', csv: 'sheet',
  doc: 'legacy', xls: 'legacy',       // alte Word- und Excel-Dateien (OLE): nicht auf der Liste des Servers, die Vorschau nennt nur den Grund
};
/** 'image' | 'video' | 'audio' | 'pdf' | 'word' | 'sheet' | 'legacy' nach der Endung des Namens, sonst null (SVG, HTML, unbekannt: nie in der Vorschau) */
export function viewKind(name) {
  const n = String(name || ''), dot = n.lastIndexOf('.');
  return dot < 0 || dot === n.length - 1 ? null : KINDS[n.slice(dot + 1).toLowerCase()] || null;
}
const streamed = (kind) => kind === 'video' || kind === 'audio';

/**
 * Fehlercode, wenn die Vorschau diese Datei nicht zeigt (sonst null); dieselben Regeln wie am Server (FilePreview.Refuse), damit niemand fuer eine Datei bestaetigen muss, die doch abgelehnt wird.
 * limits: { previewBytes, maxBytes } aus /api/files/info (0 oder fehlend: keine Grenze bekannt, der Server entscheidet).
 */
export function refusal(name, size, { root = false, previewBytes = 0, maxBytes = 0 } = {}) {
  const kind = viewKind(name);
  if (!kind) return 'preview_type';
  if (kind === 'legacy') return 'preview_legacy';
  if (root && streamed(kind)) return 'preview_root_type';
  const hard = maxBytes > 0 ? maxBytes : Infinity;
  const limit = streamed(kind) ? hard : Math.min(previewBytes > 0 ? previewBytes : Infinity, hard);
  return size > limit ? 'preview_too_large' : null;
}

/** Die Vorschau-Adresse einer Datei; token nur auf Root-Orten */
export function previewUrl(place, path, token) {
  const q = encodeURIComponent;
  return C.isLocal(place) ? `/api/files/preview?path=${q(path)}` : `/api/files/remote/preview?c=${place.id}&path=${q(path)}` + (token ? '&token=' + q(token) : '');
}

/** Die Bilder der Liste in ihrer Reihenfolge (nur Dateien) und die Stelle der aktuellen: { list, index, prev, next } (prev/next null am Rand, kein Umlauf) */
export function neighbours(entries, path) {
  const list = (entries || []).filter((e) => C.isFileLike(e) && viewKind(e.name) === 'image');
  const index = list.findIndex((e) => e.path === path);
  return { list, index, prev: index > 0 ? list[index - 1] : null, next: index >= 0 && index < list.length - 1 ? list[index + 1] : null };
}

export const ZOOM_MIN = 0.05, ZOOM_MAX = 16, ZOOM_STEP = 1.25;
export const clampZoom = (z) => Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, z));
/** Der naechste Zoom: dir +1 / -1 (um ZOOM_STEP), gerundet auf 1 Promille, in den Grenzen */
export const stepZoom = (z, dir) => clampZoom(Math.round(z * (dir > 0 ? ZOOM_STEP : 1 / ZOOM_STEP) * 1000) / 1000);
/** Der Massstab, der ein Bild in die Flaeche einpasst; ein kleines Bild wird nicht ueber 100 % vergroessert */
export function fitZoom(w, h2, boxW, boxH) {
  if (!(w > 0 && h2 > 0 && boxW > 0 && boxH > 0)) return 1;
  return Math.min(1, boxW / w, boxH / h2);
}

/** Die Seite, die oben im sichtbaren Bereich liegt (tops: obere Kanten der Seiten, aufsteigend; Abstand: wie weit das Blatt schon in den Blick ragen darf) */
export function pageAt(tops, scrollTop, slack = 0) {
  let page = 0;
  for (let i = 0; i < tops.length; i++) if (tops[i] <= scrollTop + slack) page = i;
  return page;
}

/* ---------- Laden ---------- */
const fail = (code, message) => Object.assign(new Error(message || code), { code });

/** Eine Antwort des Servers als Fehler lesen ({error, code}); der Code ist der Schluessel fuer den Text */
async function problem(r) {
  if (r.status === 401) return fail('', t('shell.sessionExpired'));
  let code = '';
  try { code = (await r.json()).code || ''; } catch { /* kein JSON */ }
  return fail(code || 'failed');
}
/** Holt die Datei (nie Video oder Audio) als Blob; onProgress(Prozent|null). Bricht mit signal ab. */
async function fetchBlob(url, signal, onProgress) {
  let r;
  try { r = await fetch(url, { credentials: 'same-origin', signal }); }
  catch (e) { if (e && e.name === 'AbortError') throw e; throw fail('connection_lost'); }
  if (!r.ok) throw await problem(r);
  const total = Number(r.headers.get('content-length')) || 0;
  if (!r.body || !total) return r.blob();
  const reader = r.body.getReader(), parts = [];
  let got = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    parts.push(value); got += value.length; onProgress(Math.floor((got * 100) / total));
  }
  return new Blob(parts, { type: r.headers.get('content-type') || '' });
}
/** Ein winziger Abruf (erstes Byte) vor Video und Audio: zeigt den Grund eines Fehlers (der Player kennt ihn nicht) und laesst bei geschlossener Sitzung den Ort neu oeffnen */
async function probe(url, signal) {
  let r;
  try { r = await fetch(url, { credentials: 'same-origin', signal, headers: { Range: 'bytes=0-0' } }); }
  catch (e) { if (e && e.name === 'AbortError') throw e; throw fail('connection_lost'); }
  const ok = r.ok;
  try { await r.body?.cancel(); } catch { /* egal */ }
  if (!ok) throw await problem(r);
}

/* ---------- kleine Bausteine der Oberflaeche ---------- */
const SVGNS = 'http://www.w3.org/2000/svg';
const PATHS = {
  zoomIn: 'M15.5 14h-.79l-.28-.27A6.471 6.471 0 0016 9.5 6.5 6.5 0 109.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14zm2.5-4h-2v2H9v-2H7V9h2V7h1v2h2v1z',
  zoomOut: 'M15.5 14h-.79l-.28-.27A6.471 6.471 0 0016 9.5 6.5 6.5 0 109.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14zM7 9h5v1H7z',
  fit: 'M17 4h3c1.1 0 2 .9 2 2v2h-2V6h-3V4zM4 8V6h3V4H4c-1.1 0-2 .9-2 2v2h2zm16 8v2h-3v2h3c1.1 0 2-.9 2-2v-2h-2zM7 18H4v-2H2v2c0 1.1.9 2 2 2h3v-2zM18 8H6v8h12V8z',
};
function glyph(name) {
  if (!PATHS[name]) return icon(name);
  const s = document.createElementNS(SVGNS, 'svg'); s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('aria-hidden', 'true'); s.setAttribute('class', 'f-ic');
  const p = document.createElementNS(SVGNS, 'path'); p.setAttribute('d', PATHS[name]); s.appendChild(p);
  return s;
}
const vbtn = (name, label, onclick, text = '') => h('button', { type: 'button', class: 'fv-btn' + (text ? ' txt' : ''), title: label, 'aria-label': label, onclick }, text || glyph(name));

let cssReady = null;
/** viewer.css wird erst beim ersten Oeffnen geladen (Desktop und Handy; nichts davon steht im Start) */
function loadCss() {
  cssReady ||= new Promise((done) => {
    const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = 'viewer.css'; l.onload = l.onerror = done; document.head.appendChild(l);
  });
  return cssReady;
}

let pdfjs = null;
/** pdf.js (vendored, Version und Pruefsumme in vendor/README.md) beim ersten PDF laden; der Worker liegt daneben, nichts kommt von aussen */
async function loadPdfjs() {
  if (!pdfjs) {
    pdfjs = import('./vendor/pdfjs/pdf.min.mjs').then((m) => { m.GlobalWorkerOptions.workerSrc = new URL('./vendor/pdfjs/pdf.worker.min.mjs', import.meta.url).href; return m; });
    pdfjs.catch(() => { pdfjs = null; });
  }
  return pdfjs;
}


const scripts = new Map();
/** Ein vendored Skript (Pfad relativ zu dieser Datei) einmal als normales Skript laden: docx-preview und jszip sind UMD-Dateien, die eine globale Variable setzen; die CSP erlaubt Skripte der eigenen Herkunft */
function loadScript(path) {
  if (!scripts.has(path)) {
    const p = new Promise((ok, bad) => {
      const s = document.createElement('script');
      s.src = new URL(path, import.meta.url).href; s.onload = ok; s.onerror = () => bad(fail('failed'));
      document.head.appendChild(s);
    });
    p.catch(() => scripts.delete(path));
    scripts.set(path, p);
  }
  return scripts.get(path);
}
/** docx-preview (mit jszip, normale Skripte) und read-excel-file (ein ES-Modul, mit tools/vendor-read-excel-file.sh gebaut) erst beim ersten Word- oder Excel-Dokument (Versionen und Pruefsummen: vendor/README.md) */
async function loadDocx() { await loadScript('./vendor/jszip/jszip.min.js'); await loadScript('./vendor/docx-preview/docx-preview.min.js'); return globalThis.docx; }
let xlsxLib = null;
async function loadXlsx() { xlsxLib ||= import('./vendor/read-excel-file/read-excel-file.mjs').then((m) => m.readXlsx); xlsxLib.catch(() => { xlsxLib = null; }); return xlsxLib; }

/** Entfernt aus dem von docx-preview gebauten Baum alles, was laden, ausfuehren oder Eingaben nehmen koennte (Regeln: viewer-office.js); ein Link bleibt Text, sein Ziel steht als Hinweis (title) */
function sanitize(root) {
  for (const el of [...root.querySelectorAll('*')]) {
    if (!root.contains(el)) continue;                                       // war in einem schon entfernten Element
    const tag = el.localName.toLowerCase();
    if (dropTag(tag)) { el.remove(); continue; }
    if (tag === 'style') el.textContent = cleanCss(el.textContent);
    if (tag === 'a') { const href = el.getAttribute('href') || ''; if (/^(https?:|mailto:)/i.test(href)) el.setAttribute('title', href.slice(0, 300)); }
    for (const a of [...el.attributes]) if (!keepAttr(a.name, a.value)) el.removeAttribute(a.name);
  }
}

const registry = new Map();      // je Dateien-App ein Vorschau-Fenster: ein weiterer Aufruf zeigt die neue Datei im offenen Fenster

/**
 * Zeigt eine Datei in der Vorschau.
 * ctx: { id (Kennung der Dateien-App), kit, place, entry, entries (die Liste, aus der das Bild kommt), limits () -> { previewBytes, maxBytes },
 *   guard(fn) (ruft fn auf und oeffnet bei geschlossener Sitzung den Ort neu), confirm(entry) -> { token } | null (Root: Bestaetigung der Datei, sonst { token: undefined }),
 *   download(entry), edit(entry) (nur wenn sich die Datei bearbeiten laesst: eine csv auf einem Host), say(text) }
 * Gibt das Versprechen des Fensters zurueck (endet beim Schliessen).
 */
export async function openViewer(ctx) {
  const open = registry.get(ctx.id);
  if (open) { open.focus(); return open.show(ctx); }
  await loadCss();
  if (registry.has(ctx.id)) { const o = registry.get(ctx.id); o.focus(); return o.show(ctx); }
  const v = createViewer(ctx);
  registry.set(ctx.id, v);
  v.promise.then(() => registry.delete(ctx.id));
  v.show(ctx);
  return v.promise;
}

function createViewer(first) {
  let cur = first, seq = 0, cleanup = () => {}, ctl = null, shown = false, closed = false;
  const kit = first.kit;
  const meta = h('p', { class: 'f-sub fv-meta' });
  const bar = h('div', { class: 'fv-bar' });
  const note = h('p', { class: 'f-sub fv-note', role: 'status' });
  const stage = h('div', { class: 'fv-stage', tabindex: '0' });
  const dlg = modal({
    title: '', kit, id: 'view', root: C.isRoot(first.place), win: { width: Math.min(960, innerWidth - 40), height: Math.min(700, innerHeight - 110), resizable: true },
    content: [meta, bar, note, stage],
    actions: [
      { id: 'download', label: t('desktopFiles.download'), kind: 'ghost', keepOpen: true, onclick: () => cur.download(cur.entry) },
      { id: 'close', label: t('common.close'), kind: 'primary' },
    ],
  });
  dlg.el.classList.add('f-viewer');
  dlg.promise.then(() => { closed = true; release(); });

  function release() { seq++; if (ctl) ctl.abort(); ctl = null; cleanup(); cleanup = () => {}; }

  /** Eine Meldung statt des Inhalts (Fehler, Warten); die Taste "Herunterladen" bleibt */
  const message = (text, kind = '') => { bar.replaceChildren(); note.textContent = ''; stage.className = 'fv-stage fv-msg'; stage.replaceChildren(noteEl(text, kind)); };

  async function show(ctx) {
    cur = ctx;
    release();
    note.textContent = '';
    const my = seq, entry = ctx.entry, place = ctx.place, kind = viewKind(entry.name), root = C.isRoot(place);
    ctl = new AbortController();
    const signal = ctl.signal;
    dlg.setTitle(safeName(entry.name));
    const where = [place.label || place.host || '', safeName(C.parentOf(entry.path, place) || '/')].filter(Boolean).join(' · ');
    const metaText = (extra = '') => { meta.textContent = [C.fileSize(entry.size), where, extra].filter(Boolean).join(' · '); };
    metaText();
    stage.setAttribute('aria-label', t('files.view.label', { name: safeName(entry.name) }));
    const why = refusal(entry.name, entry.size, { root, ...(ctx.limits ? ctx.limits() : {}) });
    if (why) return message(C.errText(why), why === 'preview_legacy' ? '' : 'warn');
    message(t('files.view.loading'));
    const stale = () => closed || my !== seq;
    let token;
    if (root) {
      const ok = await ctx.confirm(entry);
      if (stale()) return;
      if (!ok) { if (!shown) dlg.close(); else message(C.errText('cancelled')); return; }
      token = ok.token;
    }
    const url = previewUrl(place, entry.path, token);
    const progress = (pct) => { if (!stale()) stage.firstChild.textContent = pct === null ? t('files.view.loading') : t('files.view.loadingPct', { percent: pct }); };
    try {
      let shownKind;
      if (kind === 'image') shownKind = await showImage(ctx, url, signal, progress, stale, metaText);
      else if (kind === 'pdf') shownKind = await showPdf(ctx, url, signal, progress, stale);
      else if (kind === 'word') shownKind = await showWord(ctx, url, signal, progress, stale);
      else if (kind === 'sheet') shownKind = await showSheet(ctx, url, signal, progress, stale);
      else shownKind = await showMedia(ctx, kind, url, signal, stale);
      if (!stale() && shownKind) { shown = true; stage.focus({ preventScroll: true }); }
    } catch (e) {
      if (stale() || (e && e.name === 'AbortError')) return;
      message(e && e.code ? C.errText(e.code) : (e && e.message) || t('files.err.failed'), 'warn');
    }
  }

  /* ----- Bild: holen, in einen Blob, einpassen oder zoomen; Pfeiltasten zum Nachbarn ----- */
  async function showImage(ctx, url, signal, progress, stale, metaText) {
    const blob = await ctx.guard(() => fetchBlob(url, signal, progress));
    if (stale()) return false;
    const src = URL.createObjectURL(blob);
    cleanup = () => URL.revokeObjectURL(src);
    const img = h('img', { class: 'fv-img', alt: '', draggable: 'false' });
    try { await new Promise((ok, bad) => { img.onload = ok; img.onerror = bad; img.src = src; }); } catch { throw fail('', t('files.view.imageFailed')); }          // load statt decode(): ein verdecktes Fenster der Seite meldet decode() erst spaet
    if (stale()) return false;
    const nb = C.isRoot(ctx.place) ? { list: [], index: -1, prev: null, next: null } : neighbours(ctx.entries, ctx.entry.path);
    let fit = true, z = 1;
    const nat = { w: img.naturalWidth, h: img.naturalHeight };
    metaText(`${nat.w} × ${nat.h}`);
    const label = h('span', { class: 'fv-zoom', role: 'status' });
    const apply = () => {
      const k = fit ? fitZoom(nat.w, nat.h, stage.clientWidth - 2, stage.clientHeight - 2) : z;
      img.style.width = Math.max(1, Math.round(nat.w * k)) + 'px'; img.style.height = Math.max(1, Math.round(nat.h * k)) + 'px';
      label.textContent = Math.round(k * 100) + ' %';
      stage.classList.toggle('fv-pan', !fit && (img.offsetWidth > stage.clientWidth || img.offsetHeight > stage.clientHeight));
      return k;
    };
    const setFit = () => { fit = true; apply(); };
    const setZoom = (k) => { fit = false; z = clampZoom(k); apply(); };
    const step = (dir) => { const k = apply(); setZoom(stepZoom(k, dir)); };
    const go = (to) => { if (to) ctx.entry !== to && show({ ...ctx, entry: to }); };
    bar.replaceChildren(
      ...(nb.list.length > 1 ? [vbtn('chevL', t('files.view.prev'), () => go(nb.prev)), vbtn('chevR', t('files.view.next'), () => go(nb.next)), h('span', { class: 'fv-count', text: t('files.view.counter', { n: nb.index + 1, total: nb.list.length }) }), h('span', { class: 'fv-gap' })] : []),
      vbtn('zoomOut', t('files.view.zoomOut'), () => step(-1)), vbtn('fit', t('files.view.fit'), setFit), vbtn('', t('files.view.actual'), () => setZoom(1), '1:1'), vbtn('zoomIn', t('files.view.zoomIn'), () => step(1)), label);
    stage.className = 'fv-stage fv-image';
    stage.replaceChildren(img);
    apply();
    const ro = new ResizeObserver(() => { if (fit) apply(); });
    ro.observe(stage);
    const keys = (e) => {
      if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
      const ok = { ArrowLeft: () => go(nb.prev), ArrowRight: () => go(nb.next), '+': () => step(1), '=': () => step(1), '-': () => step(-1), 0: setFit, 1: () => setZoom(1) }[e.key];
      if (ok) { e.preventDefault(); ok(); }
    };
    stage.addEventListener('keydown', keys);
    stage.addEventListener('dblclick', () => (fit ? setZoom(1) : setFit()));
    stage.addEventListener('wheel', (e) => { if (e.ctrlKey) { e.preventDefault(); step(e.deltaY < 0 ? 1 : -1); } }, { passive: false });
    let drag = null;           // Ziehen verschiebt den Ausschnitt, wenn das Bild groesser ist als die Flaeche
    stage.addEventListener('pointerdown', (e) => { if (e.button === 0 && stage.classList.contains('fv-pan')) { drag = { x: e.clientX, y: e.clientY, l: stage.scrollLeft, t: stage.scrollTop }; stage.setPointerCapture(e.pointerId); } });
    stage.addEventListener('pointermove', (e) => { if (drag) { stage.scrollLeft = drag.l - (e.clientX - drag.x); stage.scrollTop = drag.t - (e.clientY - drag.y); } });
    const end = () => { drag = null; };
    stage.addEventListener('pointerup', end); stage.addEventListener('pointercancel', end);
    const prevClean = cleanup;
    cleanup = () => { ro.disconnect(); stage.removeEventListener('keydown', keys); prevClean(); };
    return true;
  }

  /* ----- Video und Audio: der Player des Browsers auf der Vorschau-Adresse (Range zum Spulen); beim Schliessen wird der Strom beendet ----- */
  async function showMedia(ctx, kind, url, signal, stale) {
    await ctx.guard(() => probe(url, signal));
    if (stale()) return false;
    const el = h(kind === 'video' ? 'video' : 'audio', { class: 'fv-' + kind, controls: true, preload: 'metadata', playsinline: true, 'aria-label': safeName(ctx.entry.name) });
    el.addEventListener('error', () => { if (!stale()) message(t('files.view.mediaFailed'), 'warn'); });
    stage.className = 'fv-stage fv-media';
    bar.replaceChildren();
    stage.replaceChildren(el);
    el.src = url;
    cleanup = () => { try { el.pause(); } catch { /* egal */ } el.removeAttribute('src'); el.load(); };
    return true;
  }

  /* ----- PDF: pdf.js zeichnet jede Seite auf eine Leinwand unserer Seite (kein Browser-Viewer, kein Skript, kein eval); Seiten werden erst beim Sichtbarwerden gezeichnet ----- */
  async function showPdf(ctx, url, signal, progress, stale) {
    const [lib, blob] = await Promise.all([loadPdfjs(), ctx.guard(() => fetchBlob(url, signal, progress))]);
    if (stale()) return false;
    const data = new Uint8Array(await blob.arrayBuffer());
    // isEvalSupported aus (kein Code aus der Datei), kein Wasm (die Seiten-CSP erlaubt keines; pdf.js nimmt dann den kleinen JS-Ersatz), nur Systemschriften statt nachgeladener Schriftdateien
    const task = lib.getDocument({ data, isEvalSupported: false, useWasm: false, wasmUrl: new URL('./vendor/pdfjs/wasm/', import.meta.url).href, useSystemFonts: true, verbosity: 0 });
    cleanup = () => { task.destroy().catch(() => {}); };
    let pdf;
    try { pdf = await task.promise; }
    catch (e) { throw fail('', e && e.name === 'PasswordException' ? t('files.view.pdfPassword') : t('files.view.pdfFailed')); }
    if (stale()) return false;
    const first = await pdf.getPage(1);
    const base = first.getViewport({ scale: 1 });
    const sizes = [{ w: base.width, h: base.height }];
    let zoom = 1, pageNo = 1, timer = 0;
    const holder = h('div', { class: 'fv-pages' });
    const pages = [];
    const cssWidth = () => Math.max(120, (stage.clientWidth - 24) * zoom);
    const label = h('span', { class: 'fv-count', role: 'status' });
    const sizeOf = (i) => sizes[i] || sizes[0];
    const layout = () => {
      const w = cssWidth();
      pages.forEach((p, i) => { const s = sizeOf(i); p.el.style.width = Math.round(w) + 'px'; p.el.style.height = Math.round((w * s.h) / s.w) + 'px'; });
    };
    async function draw(p, i) {
      if (p.task || p.done === cssWidth()) return;
      const w = cssWidth(), gen = p.gen;
      try {
        const page = await pdf.getPage(i + 1);
        const v1 = page.getViewport({ scale: 1 });
        sizes[i] = { w: v1.width, h: v1.height };
        layout();
        let ratio = Math.max(1, Math.min(devicePixelRatio || 1, 3));
        const area = w * w * (v1.height / v1.width);
        if (area * ratio * ratio > 16e6) ratio = Math.max(1, Math.sqrt(16e6 / area));            // eine Leinwand hoechstens 16 Megapixel
        const vp = page.getViewport({ scale: (w / v1.width) * ratio });
        const canvas = document.createElement('canvas');
        canvas.width = Math.floor(vp.width); canvas.height = Math.floor(vp.height);
        canvas.style.width = '100%'; canvas.style.height = '100%';
        p.task = page.render({ canvas, viewport: vp });
        await p.task.promise;
        if (stale() || gen !== p.gen) return;
        p.el.replaceChildren(canvas); p.done = w;
      } catch (e) { if (!(e && e.name === 'RenderingCancelledException')) p.el.classList.add('fv-bad'); }
      finally { if (gen === p.gen) p.task = null; }
    }
    // Eine Seite loslassen (ausser Sicht oder neue Breite): laufendes Zeichnen abbrechen, die Leinwand freigeben
    const drop = (p) => { p.gen++; if (p.task) p.task.cancel(); p.task = null; p.el.replaceChildren(); p.done = 0; };
    const io = new IntersectionObserver((list) => { for (const e of list) { const p = pages[e.target.dataset.i]; if (e.isIntersecting) draw(p, +e.target.dataset.i); else if (p.done) drop(p); } }, { root: stage, rootMargin: '150% 0px' });
    for (let i = 0; i < pdf.numPages; i++) {
      const el = h('div', { class: 'fv-page', 'data-i': String(i), role: 'img', 'aria-label': t('files.view.page', { n: i + 1, total: pdf.numPages }) });
      pages.push({ el, task: null, done: 0, gen: 0 }); holder.append(el);
    }
    const setLabel = () => { label.textContent = t('files.view.page', { n: pageNo, total: pdf.numPages }); };
    const scrollTo = (n) => { const p = pages[Math.min(pdf.numPages, Math.max(1, n)) - 1]; stage.scrollTop = p.el.offsetTop - holder.offsetTop - 8; };
    // Neue Breite: alle Seiten neu bemessen und gezeichnete loslassen; die sichtbaren zeichnet der Beobachter gleich neu (observe meldet jede Seite sofort wieder)
    const relayout = () => { layout(); for (const p of pages) if (p.done && p.done !== cssWidth()) drop(p); io.disconnect(); for (const p of pages) io.observe(p.el); };
    const setZoom = (k) => { zoom = clampZoom(k); relayout(); };
    bar.replaceChildren(
      vbtn('chevU', t('files.view.pagePrev'), () => scrollTo(pageNo - 1)), vbtn('chevD', t('files.view.pageNext'), () => scrollTo(pageNo + 1)), label, h('span', { class: 'fv-gap' }),
      vbtn('zoomOut', t('files.view.zoomOut'), () => setZoom(stepZoom(zoom, -1))), vbtn('fit', t('files.view.fitWidth'), () => setZoom(1)), vbtn('zoomIn', t('files.view.zoomIn'), () => setZoom(stepZoom(zoom, 1))));
    stage.className = 'fv-stage fv-pdf';
    stage.replaceChildren(holder);
    layout(); setLabel();
    pages.forEach((p) => io.observe(p.el));
    const onScroll = () => { const tops = pages.map((p) => p.el.offsetTop - holder.offsetTop); const n = pageAt(tops, stage.scrollTop, stage.clientHeight / 3) + 1; if (n !== pageNo) { pageNo = n; setLabel(); } };
    stage.addEventListener('scroll', onScroll, { passive: true });
    const ro = new ResizeObserver(() => { clearTimeout(timer); timer = setTimeout(relayout, 150); });
    ro.observe(stage);
    const keys = (e) => {
      if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
      const ok = { '+': () => setZoom(stepZoom(zoom, 1)), '=': () => setZoom(stepZoom(zoom, 1)), '-': () => setZoom(stepZoom(zoom, -1)), 0: () => setZoom(1) }[e.key];
      if (ok) { e.preventDefault(); ok(); }
    };
    stage.addEventListener('keydown', keys);
    stage.addEventListener('wheel', (e) => { if (e.ctrlKey) { e.preventDefault(); setZoom(stepZoom(zoom, e.deltaY < 0 ? 1 : -1)); } }, { passive: false });
    const prevClean = cleanup;
    cleanup = () => { clearTimeout(timer); ro.disconnect(); io.disconnect(); stage.removeEventListener('scroll', onScroll); stage.removeEventListener('keydown', keys); for (const p of pages) if (p.task) p.task.cancel(); holder.replaceChildren(); prevClean(); };
    return true;
  }

  /* ----- Word: docx-preview baut die Seiten mit DOM-Aufrufen in ein abgetrenntes Element; der bereinigte Baum wird als Text in ein iframe mit sandbox (ohne Skripte, ohne Herkunft) und eigener CSP
         gesetzt. Links sind kein Link (Ziel als Hinweis), Bilder kommen nur aus der Datei (data:). Vorher prueft die Seite das ZIP-Verzeichnis (Grenze fuer das Entpackte). ----- */
  async function showWord(ctx, url, signal, progress, stale) {
    const [lib, blob] = await Promise.all([loadDocx().catch(() => { throw fail('', t('files.view.docxFailed')); }), ctx.guard(() => fetchBlob(url, signal, progress))]);
    if (stale()) return false;
    const bytes = new Uint8Array(await blob.arrayBuffer());
    const bad = zipRefusal(zipStats(bytes), 'word');
    if (bad) throw fail('', bad === 'unpacked' ? t('files.view.unpacked') : t('files.view.zipInvalid'));
    const body = document.createElement('div'), styles = document.createElement('div');          // nie im Dokument
    try { await lib.renderAsync(bytes, body, styles, { className: 'docx', inWrapper: true, ignoreFonts: true, useBase64URL: true, renderAltChunks: false, renderComments: false, renderChanges: false, breakPages: true, ignoreLastRenderedPageBreak: true }); }
    catch { throw fail('', t('files.view.docxFailed')); }
    if (stale()) return false;
    sanitize(body); sanitize(styles);
    const html = body.innerHTML, css = [...styles.querySelectorAll('style')].map((x) => x.textContent).join('\n');
    const section = body.querySelector('section');
    const pageW = cssLengthPx(section && section.style.width);
    const frame = h('iframe', { class: 'fv-frame', sandbox: '', referrerpolicy: 'no-referrer', title: safeName(ctx.entry.name) });
    let fit = true, z = 1, timer = 0;
    const label = h('span', { class: 'fv-zoom', role: 'status' });
    const apply = () => {
      const k = fit ? Math.min(1, Math.max(0.1, (stage.clientWidth - 16) / (pageW + 60))) : z;
      frame.srcdoc = frameDoc({ css, body: html, zoom: k });
      label.textContent = Math.round(k * 100) + ' %';
      return k;
    };
    const setFit = () => { fit = true; apply(); };
    const step = (dir) => { const k = fit ? Math.min(1, (stage.clientWidth - 16) / (pageW + 60)) : z; fit = false; z = clampZoom(stepZoom(k, dir)); apply(); };
    bar.replaceChildren(vbtn('zoomOut', t('files.view.zoomOut'), () => step(-1)), vbtn('fit', t('files.view.fitWidth'), setFit), vbtn('zoomIn', t('files.view.zoomIn'), () => step(1)), label);
    stage.className = 'fv-stage fv-word';
    stage.replaceChildren(frame);
    apply();
    const ro = new ResizeObserver(() => { if (fit) { clearTimeout(timer); timer = setTimeout(apply, 150); } });
    ro.observe(stage);
    cleanup = () => { clearTimeout(timer); ro.disconnect(); frame.removeAttribute('srcdoc'); };
    return true;
  }

  /* ----- Excel und csv: nur Werte (bei Formeln ihr Ergebnis), jedes Blatt ein Reiter, hoechstens LIMITS.rows Zeilen und LIMITS.cols Spalten; alle Zellen ueber textContent ----- */
  async function showSheet(ctx, url, signal, progress, stale) {
    const csv = /\.csv$/i.test(ctx.entry.name);
    const [lib, blob] = await Promise.all([csv ? null : loadXlsx().catch(() => { throw fail('', t('files.view.xlsxFailed')); }), ctx.guard(() => fetchBlob(url, signal, progress))]);
    if (stale()) return false;
    const bytes = new Uint8Array(await blob.arrayBuffer());
    let sheets;
    if (csv) {
      const r = parseCsv(decodeText(bytes));
      sheets = [{ name: '', rows: r.rows, totalRows: r.totalRows, totalCols: r.totalCols, cut: r.totalRows > r.rows.length || r.totalCols > LIMITS.cols }];
    } else {
      const bad = zipRefusal(zipStats(bytes), 'sheet');
      if (bad) throw fail('', bad === 'unpacked' ? t('files.view.unpacked') : t('files.view.zipInvalid'));
      let out;
      try { out = await lib(bytes); }
      catch (e) { throw fail('', e && e.code === 'XLS_FILE_NOT_SUPPORTED' ? C.errText('preview_legacy') : t('files.view.xlsxFailed')); }
      sheets = (Array.isArray(out) ? out : []).map((s) => ({ name: String(s.sheet || ''), ...cutTable(s.data) }));
    }
    if (!sheets.length) throw fail('', t('files.view.xlsxFailed'));
    if (stale()) return false;
    const lang = getLang();
    let drawn = 0, current = -1;
    const tabs = h('div', { class: 'fv-tabs', role: 'tablist', 'aria-label': t('files.view.sheets') });
    const tabEls = sheets.map((s, i) => h('button', { type: 'button', class: 'fv-tab', role: 'tab', 'aria-selected': 'false', title: s.name, onclick: () => select(i) }, s.name || '1'));
    tabs.append(...tabEls);
    bar.replaceChildren(...(sheets.length > 1 ? [tabs] : []), ...(csv && ctx.edit ? [h('span', { class: 'fv-gap' }), vbtn('', t('common.edit'), () => ctx.edit(ctx.entry), t('common.edit'))] : []));
    stage.className = 'fv-stage fv-sheet';
    function select(i) {
      if (i === current) return;
      current = i; const my = ++drawn, sh = sheets[i];
      tabEls.forEach((el, j) => { el.setAttribute('aria-selected', String(j === i)); el.classList.toggle('on', j === i); });
      const cols = Math.min(sh.totalCols, LIMITS.cols);
      note.textContent = sh.cut ? t('files.view.sheetCut', { rows: sh.rows.length, totalRows: sh.totalRows, cols, totalCols: sh.totalCols })
        : t('files.view.sheetInfo', { rows: sh.totalRows, cols: sh.totalCols });
      stage.scrollTo(0, 0);
      if (!sh.totalRows || !cols) { stage.replaceChildren(noteEl(t('files.view.sheetEmpty'))); return; }
      const head = h('tr', {}, h('th', { class: 'fv-corner', scope: 'col' }));
      for (let c = 0; c < cols; c++) head.append(h('th', { scope: 'col', text: colName(c) }));
      const tbody = h('tbody');
      stage.replaceChildren(h('table', { class: 'fv-table', 'aria-label': sh.name || safeName(ctx.entry.name) }, h('thead', {}, head), tbody));
      let at = 0;
      const chunk = () => {                                                   // 300 Zeilen je Bild: eine volle Tabelle (5000 x 50) blockiert die Seite nicht
        if (my !== drawn || stale()) return;
        for (const end = Math.min(sh.rows.length, at + 300); at < end; at++) {
          const tr = h('tr', {}, h('th', { scope: 'row', class: 'fv-rowno', text: String(at + 1) }));
          const row = sh.rows[at];
          for (let c = 0; c < cols; c++) { const v = row[c]; tr.append(h('td', { class: typeof v === 'number' ? 'num' : '', text: fmtCell(v, lang) })); }
          tbody.append(tr);
        }
        if (at < sh.rows.length) requestAnimationFrame(chunk);
      };
      chunk();
    }
    select(0);
    cleanup = () => { drawn++; };
    return true;
  }

  return { promise: dlg.promise, show, focus: () => dlg.focus && dlg.focus() };
}
