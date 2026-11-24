/* =====================================================================
 * Zwischenablage am Handy: eine kleine Anzeige in der Sitzung, was in der Zwischenablage DIESES GERAETS liegt ("Text · 42 Zeichen", "Bild · PNG 1280x720"), und ein Blatt mit
 * Vorschau und Aktionen (Einfuegen, Als Text tippen, Vorher bearbeiten, Als Datei senden, bei Bildern PNG oder JPG) sowie dem, was zuletzt aus der Sitzung kam
 * ("Aufs Geraet kopieren"). Das automatische Verhalten (Text und Bilder in beide Richtungen) bleibt wie es ist, siehe docs/CLIPBOARD.md.
 *
 * Regeln (docs/CLIPBOARD.md, clipperm.js): gelesen wird nur, wenn der Browser es erlaubt. Ohne Geste nur, wenn die Berechtigung "granted" ist (Chrome, Edge), die Seite den Fokus hat
 * und die Sitzung zu sehen ist; sonst erst nach einem Tippen (Anzeige oder Knopf) und mit dem Hinweis vorher, dass der Browser gleich fragt. Der Inhalt steht nur im Speicher dieser Seite und
 * im Blatt, nie in einem Log (Diagnose: nur Art und Laenge) und nie in einer Adresse.
 * Der obere Teil dieser Datei ist reine Logik (mit node pruefbar: tests/frontend/clip-panel.test.mjs), der untere die Oberflaeche.
 * ===================================================================== */
import { t, tk, applyDom, fmtNumber } from './i18n.js';
import { queryState, browserKind, beforeReadHint, deniedHint, copyToDevice } from './clipperm.js';
import { KS, charKeysym, textOps, LIMITS as KEY_LIMITS } from './keys-shortcuts.js';
import { showDialog } from './desktop-window.js';

/* ---------- Reine Logik ---------- */
/** Kurzname des Bildformats ('image/png' -> 'PNG', 'image/jpeg' -> 'JPG'). */
export const imageLabel = (type) => ({ 'image/png': 'PNG', 'image/jpeg': 'JPG', 'image/jpg': 'JPG', 'image/webp': 'WEBP', 'image/gif': 'GIF', 'image/bmp': 'BMP' }[String(type).toLowerCase()] || String(type).replace(/^image\//i, '').toUpperCase().slice(0, 5) || '?');

/** Was die Anzeige sagt: info ist { kind: 'text', length } | { kind: 'image', type, width, height } | { kind: 'empty' } | null (unbekannt: noch nicht gelesen). */
export function chipText(info) {
  if (!info) return t('clipp.chipUnknown');
  if (info.kind === 'text') return t('clipp.chipText', { n: fmtNumber(info.length) });
  if (info.kind === 'image') return t('clipp.chipImage', { type: imageLabel(info.type), w: info.width || '?', h: info.height || '?' });
  return t('clipp.chipEmpty');
}

/**
 * Darf die Anzeige die Zwischenablage ohne Tippen lesen? Nur mit erteilter Berechtigung (sonst fragt der Browser oder verweigert es), mit Fokus der Seite, bei sichtbarer Sitzung
 * und eingeschalteter Anzeige. Firefox und Safari kennen "clipboard-read" nicht (Zustand 'unknown'): dort wird nie von allein gelesen.
 */
export function mayAutoRead({ state, focused, visible, enabled }) {
  return !!(enabled && visible && focused && state && state.secure && state.canRead && state.read === 'granted');
}

/** Dateiname fuer "Als Datei senden", mit Zeitstempel (nur Zeichen, die jedes Dateisystem mag). */
export function fileStamp(date = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}-${p(date.getHours())}${p(date.getMinutes())}${p(date.getSeconds())}`;
}
export const textFileName = (date) => `${t('clipp.fileBase')}-${fileStamp(date)}.txt`;
export const imageFileName = (type, date) => `${t('clipp.fileBase')}-${fileStamp(date)}.${imageLabel(type).toLowerCase()}`;

/** Wie viel von einem Text das "Als Text tippen" tippen wuerde: { count, total, truncated, max }. */
export function typePlan(text) {
  const r = textOps(text);
  return { count: r.count, total: r.total, truncated: r.truncated, max: KEY_LIMITS.typeChars };
}

/** Kurzform des Inhalts fuer die Vorschau: ab `max` Zeichen abgeschnitten (der ganze Text bleibt im Speicher). */
export function previewText(text, max = 20000) {
  const chars = [...String(text)];
  return chars.length > max ? { text: chars.slice(0, max).join(''), truncated: true } : { text: String(text), truncated: false };
}

/* ---------- Lesen der Zwischenablage des Geraets ---------- */
/** Bilder: Breite und Hoehe lesen (nur zum Anzeigen). */
async function imageSize(blob) {
  try { const b = await createImageBitmap(blob); const r = { width: b.width, height: b.height }; if (b.close) b.close(); return r; } catch { return { width: 0, height: 0 }; }
}

/**
 * Liest die Zwischenablage: { kind: 'text', text } | { kind: 'image', blob, type, width, height } | { kind: 'empty' }. Wirft { problem: Schluessel, fallback } wie readDeviceText.
 * Nur aus einer Geste aufrufen oder wenn mayAutoRead gilt. Mit clipboard.read() (Bilder und Text), sonst readText().
 */
export async function readDevice({ nav = globalThis.navigator, win = globalThis.window, onHint = () => {} } = {}) {
  const st = await queryState(nav, win), kind = browserKind((nav && nav.userAgent) || '');
  const fail = (problem, extra = {}) => Object.assign(new Error(problem), { problem, fallback: true, state: st, ...extra });
  if (!st.secure) throw fail(tk('clipperm.hintInsecure'));
  if (!st.hasApi || !st.canRead) throw fail(tk('clipperm.hintNoRead'));
  if (st.read === 'denied') throw fail(deniedHint(kind));
  const hint = beforeReadHint(st, kind);
  if (hint) onHint(hint);
  try {
    if (nav.clipboard.read) {
      const items = await nav.clipboard.read();
      let image = null;
      for (const item of items) {
        if (item.types.includes('text/plain')) { const text = await (await item.getType('text/plain')).text(); if (text) return { kind: 'text', text, state: st }; }
        const it = item.types.find((x) => /^image\//.test(x));
        if (it && !image) image = { blob: await item.getType(it), type: it };
      }
      if (image) return { kind: 'image', ...image, ...(await imageSize(image.blob)), state: st };
      return { kind: 'empty', state: st };
    }
    const text = await nav.clipboard.readText();
    return text ? { kind: 'text', text, state: st } : { kind: 'empty', state: st };
  } catch (e) {
    const name = (e && e.name) || '';
    throw fail(name === 'NotAllowedError' || name === 'SecurityError' ? deniedHint(kind) : tk('clipperm.readFailed'), { detail: name });
  }
}

/** Ein Bild in ein anderes Format wandeln (JPG: durchsichtige Stellen werden weiss). */
export async function convertImage(blob, type, quality = 0.92) {
  const bmp = await createImageBitmap(blob);
  try {
    const canvas = document.createElement('canvas'); canvas.width = bmp.width; canvas.height = bmp.height;
    const ctx = canvas.getContext('2d');
    if (type === 'image/jpeg') { ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, canvas.width, canvas.height); }
    ctx.drawImage(bmp, 0, 0);
    const out = await new Promise((res) => canvas.toBlob(res, type, quality));
    if (!out) throw new Error(t('clip.imageConvertFailed'));
    return out;
  } finally { if (bmp.close) bmp.close(); }
}

const ICON = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 2h-4.18C14.4.84 13.3 0 12 0c-1.3 0-2.4.84-2.82 2H5c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm7 18H5V4h2v3h10V4h2v16z"/></svg>';

/* ---------- Oberflaeche ---------- */
let dlg = null, owner = null;      // das Blatt gibt es einmal; es gehoert der Sitzung, die es zuletzt geoeffnet hat

export class ClipPanel {
  /**
   * @param session die RemoteSession (toast, client, pasteToRemote, typeTextSafely, sendToRemote, clipText, remoteItem, visible, connected)
   * @param {{host: HTMLElement, enabled: () => boolean, onMore: () => void}} o  host: das Bild der Sitzung (hier sitzt die Anzeige)
   */
  constructor(session, { host, enabled, onMore }) {
    this.s = session; this.enabled = enabled; this.onMore = onMore;
    this.info = null; this.state = null; this.timer = 0; this.busy = false;
    this.chip = document.createElement('button'); this.chip.type = 'button'; this.chip.className = 'cp-chip idle'; this.chip.hidden = true;
    this.chip.setAttribute('aria-label', t('clipp.chipLabel'));
    this.chip.innerHTML = ICON + '<span></span>';
    this.chip.addEventListener('click', (e) => { e.stopPropagation(); this.openSheet(); });
    // das Antippen der Anzeige darf nicht als Tippen ins Bild zaehlen (die Touch-Ebene liegt darunter, die Anzeige darueber)
    host.appendChild(this.chip);
    this.onFocus = () => this.refresh();
    window.addEventListener('focus', this.onFocus); document.addEventListener('visibilitychange', this.onFocus);
    this.timer = setInterval(() => this.refresh(), 4000);
    this.render();
  }

  /** Anzeige zeigen oder verbergen und beschriften. */
  render() {
    const on = this.enabled() && this.s.visible && this.s.connected;
    this.chip.hidden = !on;
    this.chip.querySelector('span').textContent = chipText(this.info);
    this.chip.classList.toggle('idle', !this.info);
  }

  /** Von allein auffrischen, wenn es erlaubt ist (mayAutoRead); sonst bleibt der letzte Stand. Kein Fehler wird gezeigt. */
  async refresh() {
    this.render();
    if (this.busy || document.hidden) return;
    const state = await queryState().catch(() => null);
    this.state = state;
    if (!mayAutoRead({ state, focused: document.hasFocus(), visible: this.s.visible && this.s.connected, enabled: this.enabled() })) return;
    try { this.set(await readDevice()); } catch { /* erlaubt oder nicht: die Anzeige bleibt */ }
  }

  /** Nach einem Schreiben in die Zwischenablage dieses Geraets (aus der Sitzung kopiert): bald neu lesen. */
  refreshSoon() { setTimeout(() => this.refresh(), 300); }

  set(data) {
    this.data = data;
    this.info = data.kind === 'text' ? { kind: 'text', length: [...data.text].length } : data.kind === 'image' ? { kind: 'image', type: data.type, width: data.width, height: data.height } : { kind: 'empty' };
    this.render();
  }

  dispose() {
    clearInterval(this.timer); window.removeEventListener('focus', this.onFocus); document.removeEventListener('visibilitychange', this.onFocus);
    this.chip.remove();
    if (owner === this && dlg && dlg.open) dlg.close();
  }

  /* ---------- das Blatt ---------- */
  /** Aus einer Geste (Tippen auf die Anzeige, Taste der Leiste, Menue): Blatt oeffnen und die Zwischenablage lesen. */
  openSheet() {
    owner = this;
    const reading = this.readNow();                 // der Aufruf des Browsers steht in der Geste, noch vor dem Oeffnen
    if (!dlg) { dlg = document.createElement('dialog'); dlg.id = 'clipPanelDlg'; dlg.className = 'tt-sheet'; document.body.appendChild(dlg); }
    this.draw();
    showDialog(dlg, { title: t('clipp.title') });
    reading.then(() => { if (owner === this && dlg.open) this.draw(); });
  }

  async readNow() {
    this.problem = '';
    this.busy = true;
    try { this.set(await readDevice({ onHint: (key) => this.s.toast(t(key), 5000) })); }
    catch (e) { this.problem = e.problem ? t(e.problem) : t('clipperm.readFailed'); this.fallback = !!e.fallback; this.data = null; }
    finally { this.busy = false; }
  }

  draw() {
    const s = this.s, d = this.data;
    const remoteText = s.clipText || '', remoteImg = s.remoteItem && s.remoteItem.image;
    const textPlan = d && d.kind === 'text' ? typePlan(d.text) : null;
    dlg.innerHTML = `
      <h2>${t('clipp.title')}</h2>
      <h3>${t('clipp.device')}</h3>
      <p class="cp-meta" id="cpMeta"></p>
      <div id="cpPreview"></div>
      <p class="tt-hint" id="cpNote"></p>
      <div class="tt-actions" id="cpActions"></div>
      <hr>
      <h3>${t('clipp.fromSession')}</h3>
      <p class="cp-meta" id="cpRemoteMeta"></p>
      <div id="cpRemotePreview"></div>
      <div class="tt-actions" id="cpRemoteActions"></div>
      <hr>
      <div class="row">
        <button type="button" class="ghost" data-act="more">${t('clipp.more')}</button>
        <button type="button" class="ghost close" data-act="close">${t('common.close')}</button>
      </div>`;
    const $ = (id) => dlg.querySelector('#' + id);
    const button = (parent, label, fn, cls = '') => { const b = document.createElement('button'); b.type = 'button'; if (cls) b.className = cls; b.textContent = label; b.addEventListener('click', fn); parent.appendChild(b); return b; };
    dlg.querySelector('[data-act="close"]').addEventListener('click', () => dlg.close());
    dlg.querySelector('[data-act="more"]').addEventListener('click', () => { dlg.close(); this.onMore(); });

    // --- dieses Geraet ---
    const meta = $('cpMeta'), preview = $('cpPreview'), note = $('cpNote'), actions = $('cpActions');
    meta.textContent = this.busy ? t('clipp.reading') : d ? chipText(this.info) : t('clipp.notRead');
    if (d && d.kind === 'image') {
      const img = document.createElement('img'); img.className = 'cp-preview'; img.alt = t('clipp.imageAlt');
      img.src = this.urlFor('previewUrl', d.blob); preview.appendChild(img);
      button(actions, t('clipp.paste'), () => this.act(() => s.sendToRemote([new File([d.blob], imageFileName(d.type), { type: d.blob.type })]), 'paste-image'));
      button(actions, t('clipp.sendPng'), () => this.sendImageFile(d, 'image/png'), 'ghost');
      button(actions, t('clipp.sendJpg'), () => this.sendImageFile(d, 'image/jpeg'), 'ghost');
      note.textContent = t('clipp.imageNote');
    } else {
      const ta = document.createElement('textarea'); ta.className = 'cp-preview'; ta.rows = 4; ta.spellcheck = false; ta.autocapitalize = 'off'; ta.setAttribute('autocorrect', 'off');
      ta.readOnly = !!(d && d.kind === 'text') && !this.editing;
      ta.value = d && d.kind === 'text' ? previewText(d.text).text : '';
      ta.placeholder = t('clipp.pasteHere');
      preview.appendChild(ta);
      this.ta = ta;
      // Ohne Leserecht: Einfuegen in das Feld (das paste-Ereignis liefert Text und Bilder ohne Berechtigung)
      ta.addEventListener('paste', (e) => {
        const files = [...((e.clipboardData && e.clipboardData.files) || [])].filter((f) => /^image\//.test(f.type));
        if (files.length) { e.preventDefault(); this.set({ kind: 'image', blob: files[0], type: files[0].type, width: 0, height: 0 }); imageSize(files[0]).then((z) => { this.data = { ...this.data, ...z }; this.set(this.data); this.draw(); }); }
      });
      ta.addEventListener('input', () => { this.data = { kind: 'text', text: ta.value, state: this.state }; this.info = { kind: 'text', length: [...ta.value].length }; this.render(); this.updateTextButtons(); });
      const cur = () => (d && d.kind === 'text' && !this.editing ? d.text : ta.value);
      this.textGetter = cur;
      this.pasteBtn = button(actions, t('clipp.paste'), () => this.act(() => this.pasteText(cur()), 'paste'));
      this.typeBtn = button(actions, t('clipp.typeAs'), () => this.act(() => this.typeAsText(cur()), 'type'));
      this.editBtn = button(actions, this.editing ? t('clipp.editDone') : t('clipp.edit'), () => {
        this.editing = !this.editing;
        if (this.editing) { ta.readOnly = false; ta.value = d && d.kind === 'text' ? d.text : ta.value; ta.focus(); } else { this.data = { kind: 'text', text: ta.value, state: this.state }; this.draw(); }
        this.editBtn.textContent = this.editing ? t('clipp.editDone') : t('clipp.edit');
      }, 'ghost');
      this.fileBtn = button(actions, t('clipp.sendFile'), () => this.act(() => s.sendToRemote([new File([cur()], textFileName(), { type: '' })]), 'send-file'), 'ghost');
      this.updateTextButtons();
      if (this.problem) note.textContent = this.problem;
      else if (textPlan && textPlan.truncated) note.textContent = t('clipp.typeLimit', { max: fmtNumber(textPlan.max), total: fmtNumber(textPlan.total) });
      else note.textContent = t('clipp.typeNote');
    }
    button(actions, t('clipp.reload'), async () => { await this.readNow(); this.editing = false; this.draw(); }, 'ghost');

    // --- zuletzt aus der Sitzung ---
    const rMeta = $('cpRemoteMeta'), rPrev = $('cpRemotePreview'), rActions = $('cpRemoteActions');
    if (!remoteText && !remoteImg) rMeta.textContent = t('clipp.nothingFromSession');
    else {
      if (remoteImg) {
        rMeta.textContent = t('clipp.remoteImage');
        const img = document.createElement('img'); img.className = 'cp-preview'; img.alt = t('clipp.imageAlt');
        img.src = this.urlFor('remoteUrl', remoteImg); rPrev.appendChild(img);
        button(rActions, t('clipp.copyToDevice'), async () => {
          try { await navigator.clipboard.write([new ClipboardItem({ 'image/png': remoteImg })]); s.toast(t('app.imageCopied')); this.refreshSoon(); } catch (e) { s.toast(e.message || t('app.copyNotAllowed'), 3600); }
        });
      } else {
        rMeta.textContent = t('clipp.remoteText', { n: fmtNumber([...remoteText].length) });
        const ta = document.createElement('textarea'); ta.className = 'cp-preview'; ta.rows = 3; ta.readOnly = true; ta.value = previewText(remoteText).text; rPrev.appendChild(ta);
        button(rActions, t('clipp.copyToDevice'), async () => {
          const r = await copyToDevice(remoteText);
          if (r.ok) { s.toast(t('app.copied')); this.refreshSoon(); } else { ta.focus(); ta.select(); s.toast(t('app.selectAndCopy'), 3200); }
        });
      }
    }
    applyDom(dlg);
  }

  /** Adresse fuer die Vorschau eines Bildes; die vorige wird freigegeben. */
  urlFor(key, blob) { if (this[key]) URL.revokeObjectURL(this[key]); return (this[key] = URL.createObjectURL(blob)); }

  /** "Als Text tippen" zeigt, wie viele Zeichen es tippt; ohne Text sind die Textknoepfe aus. */
  updateTextButtons() {
    const text = this.textGetter ? this.textGetter() : '';
    const plan = typePlan(text), has = plan.count > 0;
    if (this.pasteBtn) this.pasteBtn.disabled = !has;
    if (this.typeBtn) { this.typeBtn.disabled = !has; this.typeBtn.textContent = plan.truncated ? t('clipp.typeAsN', { n: fmtNumber(plan.count) }) : t('clipp.typeAs'); }
    if (this.fileBtn) this.fileBtn.disabled = !has;
  }

  /** Eine Aktion des Blattes: das Blatt schliesst sich, dann geht sie hinaus; Fehler werden gemeldet. Nur die Art geht ins Log, nie der Inhalt. */
  async act(fn, what) {
    if (!this.s.client) { this.s.toast(t('app.notConnected')); return; }
    dlg.close();
    await new Promise((r) => setTimeout(r, 150));        // der Fokus kehrt in die Sitzung zurueck, bevor etwas hinausgeht
    this.s.dbg('clip-panel', { what, kind: this.info ? this.info.kind : '', n: this.info && this.info.length ? this.info.length : 0 });
    try { await fn(); } catch (e) { this.s.toast(e.message || t('clipp.failed'), 3600); }
  }
  pasteText(text) {
    if (this.s.sendClipboard(text) !== 'sent') return;
    this.s.toast(t('app.putInVmClipboard'), 2200);
    this.s.enqueue(async () => { await new Promise((r) => setTimeout(r, 180)); });
    this.s.sendChord([KS.ctrl, charKeysym('v')], 8);
  }
  async typeAsText(text) {
    const plan = typePlan(text);
    const r = await this.s.typeTextSafely(text);
    this.s.toast(r.aborted ? t('clipp.typeAborted', { n: r.typed }) : plan.truncated ? t('clipp.typedPart', { n: r.typed, total: plan.total }) : t('app.typed'), 2600);
  }
  async sendImageFile(d, type) {
    await this.act(async () => {
      const blob = d.blob.type === type ? d.blob : await convertImage(d.blob, type);
      await this.s.sendToRemote([new File([blob], imageFileName(type), { type: '' })]);       // ohne Typ: als Datei, nicht als Bild der Zwischenablage
    }, 'send-image-file');
  }
}
