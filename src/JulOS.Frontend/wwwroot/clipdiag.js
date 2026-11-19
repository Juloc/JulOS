/* =====================================================================
 * Zwischenablage-Diagnose einer Sitzung (Zustand ohne Oberflaeche, im Dialog "Text senden" unter "Diagnose" zu sehen): ist der Kanal der VM da, was ging zuletzt
 * in welche Richtung und wann, und der letzte Fehler im Klartext. Es steht nie ein Inhalt darin, nur Richtung, Zeit und Laenge (Zeichen).
 * Die Ereignisse des Servers sind die Nachricht 15 (ClipNote in ClipboardBridge.cs); der Browser meldet selbst, ob er den Text ablegen durfte.
 * ===================================================================== */
import { t, tk } from './i18n.js';
import { stateWord, beforeReadHint, deniedHint } from './clipperm.js';

/** Die Arten der Nachricht 15 (Reihenfolge wie ClipNote im Server). */
export const NOTE = { status: 0, textFromVm: 1, textFromVmLost: 2, textFetchedByVm: 3, textTooLong: 4, textFromVmTooLong: 5, textWaiting: 6, listRefused: 7 };

/** So lange darf der Kanal der VM nach dem Verbinden auf sich warten lassen, bevor die Diagnose ihn "fehlt" nennt (xrdp meldet ihn meist nach ein bis zwei Sekunden). */
export const CHANNEL_WAIT_MS = 8000;

/** "vor 12 s", "vor 3 min", "vor 2 h" */
export function ago(ms) {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return t('clipdiag.agoS', { n: s });
  if (s < 3600) return t('clipdiag.agoM', { n: Math.round(s / 60) });
  return t('clipdiag.agoH', { n: Math.round(s / 3600) });
}

export class ClipDiag {
  /** @param {() => number} now Uhr (in Tests ersetzbar) */
  constructor(now = () => Date.now()) {
    this.now = now;
    this.startedAt = now();
    this.perm = null;                   // Zustand der Berechtigungen (clipperm.queryState), vom Dialog nachgetragen
    this.ready = null;                  // null: noch nichts vom Server gehoert, true/false: Stand des Kanals der VM
    this.files = false;
    this.fromVm = null;                 // { at, chars, outcome: 'stored' | 'denied' | 'nofocus' | 'insecure' | 'unsupported' | 'background', detail }
    this.toVm = null;                   // { at, chars, result: 'sent' | 'offline' | 'tooLong', waiting, fetchedAt }
    this.error = null;                  // { at, key, params } des letzten Fehlers
  }

  /** Eine neue Verbindung (auch ein Wiederverbinden): der Kanal der VM muss sich neu melden. */
  connected() { this.startedAt = this.now(); this.ready = null; this.files = false; }

  /** Nachricht 15 des Servers: { ready, files, note, count }. */
  serverNote(n) {
    this.ready = !!n.ready; this.files = !!n.files;
    const at = this.now();
    switch (n.note) {
      case NOTE.textFetchedByVm: if (this.toVm) this.toVm.fetchedAt = at; break;
      case NOTE.textWaiting: if (this.toVm) this.toVm.waiting = true; break;
      case NOTE.textFromVmLost: this.fail(tk('clipdiag.errVmNoText')); break;
      case NOTE.textTooLong: this.fail(tk('clipdiag.errTooLongOut'), { mb: 1 }); break;
      case NOTE.textFromVmTooLong: this.fail(tk('clipdiag.errTooLongIn'), { mb: 1 }); break;
      case NOTE.listRefused: this.fail(tk('clipdiag.errListRefused')); break;
      default: break;
    }
  }

  /** Text aus der VM ist angekommen; outcome sagt, was der Browser damit tun konnte. */
  vmText(chars, outcome, detail = '') {
    this.fromVm = { at: this.now(), chars, outcome, detail };
    if (outcome === 'denied') this.fail(tk('clipdiag.errBrowserDenied'), { why: detail || '?' });
    else if (outcome === 'insecure') this.fail(tk('clipdiag.errInsecure'));
    else if (outcome === 'unsupported') this.fail(tk('clipdiag.errUnsupported'));
  }

  /** Text wurde an die VM gesendet (result: Rueckgabe von sendClipboardText). */
  sentText(chars, result) {
    this.toVm = { at: this.now(), chars, result, waiting: false, fetchedAt: 0 };
    if (result === 'offline') this.fail(tk('clipdiag.errOffline'));
    else if (result === 'tooLong') this.fail(tk('clipdiag.errTooLongOut'), { mb: 1 });
  }

  fail(key, params) { this.error = { at: this.now(), key, params }; }

  /** Kurze Meldung fuer das Senden, die der Anwender sofort sehen soll (leer, wenn alles gut ist). */
  static sendProblem(result) {
    if (result === 'offline') return t('clipdiag.errOffline');
    if (result === 'tooLong') return t('clipdiag.errTooLongOut', { mb: 1 });
    return '';
  }

  /** Die Zeilen der Anzeige: [{ id, ok: true | false | null, text }] (null: Hinweis). */
  lines(env = {}) {
    const now = this.now(), out = [];
    const perm = env.perm || null, secure = perm ? perm.secure : env.secure !== false;
    // Kanal der VM
    if (this.ready === true) out.push({ id: 'channel', ok: true, text: t(this.files ? tk('clipdiag.channelReadyFiles') : tk('clipdiag.channelReady')) });
    else if (this.ready === false || now - this.startedAt > CHANNEL_WAIT_MS) out.push({ id: 'channel', ok: false, text: t('clipdiag.channelMissing') });
    else out.push({ id: 'channel', ok: null, text: t('clipdiag.channelWaiting') });
    // Browser
    const denied = !!perm && (perm.read === 'denied' || perm.write === 'denied');
    out.push({ id: 'browser', ok: secure && !denied, text: t('clipdiag.browserCtx', { ctx: t(secure ? tk('clipdiag.ctxSecure') : tk('clipdiag.ctxInsecure')), read: perm ? stateWord(perm.read) : '?', write: perm ? stateWord(perm.write) : '?' }) });
    if (!secure) out.push({ id: 'browserHint', ok: false, text: t('clipperm.hintInsecure') });
    else if (perm) {
      const key = beforeReadHint(perm, env.kind);
      if (key && perm.read !== 'unknown') out.push({ id: 'browserHint', ok: denied ? false : null, text: t(key) });
      else if (perm.write === 'denied') out.push({ id: 'browserHint', ok: false, text: t(deniedHint(env.kind)) });
    }
    // aus der VM
    const f = this.fromVm;
    if (!f) out.push({ id: 'fromVm', ok: null, text: t('clipdiag.fromVmNone') });
    else {
      const base = { when: ago(now - f.at), n: f.chars };
      const key = { stored: tk('clipdiag.fromVmStored'), denied: tk('clipdiag.fromVmDenied'), nofocus: tk('clipdiag.fromVmNoFocus'), insecure: tk('clipdiag.fromVmInsecure'),
        unsupported: tk('clipdiag.fromVmUnsupported'), background: tk('clipdiag.fromVmBackground') }[f.outcome] || tk('clipdiag.fromVmStored');
      out.push({ id: 'fromVm', ok: f.outcome === 'stored', text: t(key, { ...base, why: f.detail || '?' }) });
    }
    // in die VM
    const o = this.toVm;
    if (!o) out.push({ id: 'toVm', ok: null, text: t('clipdiag.toVmNone') });
    else if (o.result !== 'sent') out.push({ id: 'toVm', ok: false, text: t('clipdiag.toVmFailed', { when: ago(now - o.at), n: o.chars }) });
    else if (o.fetchedAt) out.push({ id: 'toVm', ok: true, text: t('clipdiag.toVmFetched', { when: ago(now - o.at), n: o.chars }) });
    else if (o.waiting) out.push({ id: 'toVm', ok: null, text: t('clipdiag.toVmWaiting', { when: ago(now - o.at), n: o.chars }) });
    else out.push({ id: 'toVm', ok: null, text: t('clipdiag.toVmNotFetched', { when: ago(now - o.at), n: o.chars }) });
    // letzter Fehler
    if (this.error) out.push({ id: 'error', ok: false, text: t('clipdiag.lastError', { when: ago(now - this.error.at), what: t(this.error.key, this.error.params) }) });
    else out.push({ id: 'error', ok: true, text: t('clipdiag.noError') });
    return out;
  }
}
