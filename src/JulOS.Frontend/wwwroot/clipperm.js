/* =====================================================================
 * Zugriff des Browsers auf die Zwischenablage des Geraets (ohne Oberflaeche, mit gefaelschtem navigator pruefbar):
 *  - Zustand der Berechtigungen (navigator.permissions.query fuer clipboard-read und clipboard-write; Firefox und Safari kennen clipboard-read nicht: dann "unknown"),
 *  - Hinweise VOR dem Zugriff (der Browser fragt gleich) und NACH einem Nein (wie man es in den Seiteneinstellungen wieder freigibt),
 *  - Lesen (nur aus einer Benutzergeste) und Schreiben mit Rueckfall: ohne navigator.clipboard (kein HTTPS) schreibt document.execCommand('copy') ueber ein verstecktes
 *    Textfeld, und zum Einfuegen bleibt Strg+V im Seitenfokus (das paste-Ereignis liefert den Text ohne jede Berechtigung) oder das Feld im Dialog.
 * Die Regeln der Browser: navigator.clipboard gibt es nur auf HTTPS und localhost. Chrome und Edge fragen beim ersten Lesen (readText) und merken sich die Antwort; Schreiben
 * braucht den Fokus der Seite. Firefox fragt beim Lesen mit einer eigenen Blase "Einfuegen" (erst neuere Versionen), Safari und iOS ebenso und nur direkt in der Geste;
 * Android Chrome liest nur mit Fokus der Seite. Texte stehen in den Woerterbuechern (clipperm.*).
 * ===================================================================== */
import { t, tk } from './i18n.js';

/** Welcher Browser? 'ios', 'android', 'firefox', 'safari', 'chromium' (auch Edge) oder 'other'. Nur fuer die Wahl des Hinweises. */
export function browserKind(ua = (typeof navigator !== 'undefined' && navigator.userAgent) || '') {
  if (/iPhone|iPad|iPod/i.test(ua)) return 'ios';
  if (/Android/i.test(ua)) return 'android';
  if (/Firefox\//i.test(ua)) return 'firefox';
  if (/Chrome\/|Chromium\/|Edg\//i.test(ua)) return 'chromium';
  if (/Safari\//i.test(ua)) return 'safari';
  return 'other';
}

/**
 * Der Zustand: { secure, hasApi, canRead, canWrite, read, write } mit read und write je 'granted' | 'prompt' | 'denied' | 'unknown' (der Browser nennt es nicht) | 'unsupported'
 * (keine navigator.clipboard). Wirft nie.
 */
export async function queryState(nav = globalThis.navigator, win = globalThis.window) {
  const secure = !win || win.isSecureContext !== false;
  const clip = nav && nav.clipboard;
  const hasApi = !!clip;
  const st = { secure, hasApi, canRead: !!(clip && clip.readText), canWrite: !!(clip && clip.writeText), read: 'unsupported', write: 'unsupported' };
  if (!hasApi) return st;
  st.read = st.canRead ? 'unknown' : 'unsupported'; st.write = st.canWrite ? 'unknown' : 'unsupported';
  for (const [key, name] of [['read', 'clipboard-read'], ['write', 'clipboard-write']]) {
    try {
      const r = await nav.permissions.query({ name });
      if (r && ['granted', 'prompt', 'denied'].includes(r.state) && st[key] !== 'unsupported') st[key] = r.state;
    } catch { /* Firefox und Safari kennen clipboard-read nicht: unknown bleibt */ }
  }
  return st;
}

/** Wort fuer einen Zustand (fuer die Diagnose). */
export function stateWord(s) {
  return t({ granted: tk('clipperm.stateGranted'), prompt: tk('clipperm.statePrompt'), denied: tk('clipperm.stateDenied'), unsupported: tk('clipperm.stateUnsupported') }[s] || tk('clipperm.stateUnknown'));
}

/** Schluessel des Hinweises, der VOR dem Lesen gezeigt wird (leer: keiner noetig). */
export function beforeReadHint(st, kind) {
  if (!st.secure || !st.hasApi) return tk('clipperm.hintInsecure');
  if (!st.canRead) return tk('clipperm.hintNoRead');
  if (st.read === 'denied') return deniedHint(kind);
  if (st.read === 'prompt') return tk('clipperm.hintPrompt');
  if (st.read === 'unknown' && (kind === 'ios' || kind === 'safari')) return tk('clipperm.hintSafariBubble');
  if (st.read === 'unknown' && kind === 'firefox') return tk('clipperm.hintFirefoxBubble');
  return '';
}

/** Hinweis nach einem Nein: wie die Seite wieder freigegeben wird. */
export function deniedHint(kind) {
  switch (kind) {
    case 'firefox': return tk('clipperm.deniedFirefox');
    case 'ios': case 'safari': return tk('clipperm.deniedSafari');
    case 'android': return tk('clipperm.deniedAndroid');
    case 'chromium': return tk('clipperm.deniedChromium');
    default: return tk('clipperm.deniedOther');
  }
}

/**
 * Text aus der Zwischenablage des Geraets lesen; nur aus einer Benutzergeste aufrufen (Tippen, Taste, Knopf). Vor dem Aufruf des Browsers meldet onHint(Schluessel), wenn der
 * Browser gleich fragt (oder eine Blase zeigt), damit der Hinweis schon steht, wenn die Abfrage kommt. Ergebnis: { text } oder { problem: Schluessel, fallback: true }; der
 * Rueckfall ist immer: Strg+V im Seitenfokus oder ins Feld einfuegen.
 */
export async function readDeviceText({ nav = globalThis.navigator, win = globalThis.window, onHint = () => {} } = {}) {
  const st = await queryState(nav, win), kind = browserKind((nav && nav.userAgent) || '');
  if (!st.secure) return { problem: tk('clipperm.hintInsecure'), fallback: true, state: st };
  if (!st.hasApi || !st.canRead) return { problem: tk('clipperm.hintNoRead'), fallback: true, state: st };
  if (st.read === 'denied') return { problem: deniedHint(kind), fallback: true, state: st };
  const hint = beforeReadHint(st, kind);
  if (hint) onHint(hint);
  try {
    const text = await nav.clipboard.readText();
    return { text: text || '', state: st, ...(text ? {} : { problem: tk('clipdiag.noText'), fallback: false }) };
  } catch (e) {
    const name = (e && e.name) || '';
    return { problem: name === 'NotAllowedError' || name === 'SecurityError' ? deniedHint(kind) : tk('clipperm.readFailed'), fallback: true, detail: name, state: st };
  }
}

/** Kopieren ueber ein verstecktes Textfeld und document.execCommand('copy') (geht ohne HTTPS, aber nur direkt in einer Benutzergeste). */
export function legacyCopy(text, doc = globalThis.document) {
  if (!doc || !doc.body || !doc.createElement || !doc.execCommand) return false;
  const ta = doc.createElement('textarea');
  ta.value = text; ta.setAttribute('readonly', '');
  ta.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;pointer-events:none';
  doc.body.appendChild(ta);
  let ok = false;
  try { ta.select(); if (ta.setSelectionRange) ta.setSelectionRange(0, text.length); ok = !!doc.execCommand('copy'); } catch { ok = false; }
  ta.remove();
  return ok;
}

/**
 * Text in die Zwischenablage des Geraets legen. Ergebnis: { ok, how: 'api' | 'legacy' } oder { ok: false, reason: 'nofocus' | 'insecure' | 'unsupported' | 'denied' | 'error', detail }.
 * Ohne Fokus der Seite wird nichts versucht (der Browser verweigert es ohnehin). Scheitert die API oder fehlt sie, kommt der Rueckfall ueber execCommand.
 */
export async function copyToDevice(text, { nav = globalThis.navigator, win = globalThis.window, doc = globalThis.document } = {}) {
  const secure = !win || win.isSecureContext !== false;
  const clip = nav && nav.clipboard;
  if (doc && doc.hasFocus && !doc.hasFocus()) return { ok: false, reason: 'nofocus' };
  let reason = !secure ? 'insecure' : !(clip && clip.writeText) ? 'unsupported' : 'error', detail = '';
  if (secure && clip && clip.writeText) {
    try { await clip.writeText(text); return { ok: true, how: 'api' }; }
    catch (e) { detail = (e && e.name) || ''; reason = detail === 'NotAllowedError' || detail === 'SecurityError' ? 'denied' : 'error'; }
  }
  if (legacyCopy(text, doc)) return { ok: true, how: 'legacy' };
  return { ok: false, reason, detail };
}
