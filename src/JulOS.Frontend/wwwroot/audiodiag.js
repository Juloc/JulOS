/* =====================================================================
 * Diagnose fuer Ton und Mikrofon einer Stream-Sitzung (Zustand ohne Oberflaeche, im Dialog "Ton und Mikrofon" zu sehen): ist der Ton-Kanal des Desktops da, spielt der Browser
 * wirklich (Pegel, Puffer, Aussetzer), bietet der Desktop ein Mikrofon an, hat der Browser es freigegeben. Dazu die Hinweise bei verweigerter Freigabe (wie clipperm.js
 * bei der Zwischenablage). Es steht nie ein Ton darin, nur Zaehler und Pegel.
 * Texte stehen in den Woerterbuechern (audio.*). Die Werte kommen aus StreamAudio (stream-audio.js).
 * ===================================================================== */
import { t, tk } from './i18n.js';
import { browserKind } from './clipperm.js';

/** So lange darf der Desktop nach dem Verbinden stumm bleiben, bevor die Diagnose den fehlenden Ton benennt (xrdp meldet den Kanal meist nach ein bis zwei Sekunden). */
export const SILENCE_WAIT_MS = 15000;

/** Zustand der Mikrofon-Freigabe: 'granted' | 'prompt' | 'denied' | 'unknown' (der Browser nennt ihn nicht, zum Beispiel Safari aelter als 16). Wirft nie. */
export async function micPermission(nav = globalThis.navigator) {
  try {
    const r = await nav.permissions.query({ name: 'microphone' });
    return r && ['granted', 'prompt', 'denied'].includes(r.state) ? r.state : 'unknown';
  } catch { return 'unknown'; }
}

/** Hinweis nach einem Nein: wie die Seite das Mikrofon wieder bekommt (je nach Browser). */
export function micDeniedHint(kind = browserKind()) {
  switch (kind) {
    case 'firefox': return t(tk('audio.deniedFirefox'));
    case 'ios': case 'safari': return t(tk('audio.deniedSafari'));
    case 'android': return t(tk('audio.deniedAndroid'));
    case 'chromium': return t(tk('audio.deniedChromium'));
    default: return t(tk('audio.deniedOther'));
  }
}

/** Hinweis VOR dem Einschalten, wenn der Browser gleich fragt (leer: keiner noetig). */
export function micBeforeHint(state) { return state === 'prompt' ? t('audio.promptHint') : ''; }

const pct = (v) => Math.round(Math.max(0, Math.min(1, v || 0)) * 100);

/**
 * Die Zeilen der Diagnose: [{ ok: true | false | null, text }] (null: nur ein Hinweis).
 * @param {{ audio: object, vnc?: boolean, secure?: boolean, micPerm?: string, kind?: string, now?: number }} o  audio ist das StreamAudio der Sitzung
 */
export function audioLines({ audio: a, vnc = false, secure = true, micPerm = 'unknown', kind = browserKind(), now = Date.now() }) {
  if (vnc) return [{ ok: false, text: t('audio.vnc') }];
  if (!a) return [{ ok: false, text: t('audio.none') }];
  const lines = [];
  const add = (ok, text) => lines.push({ ok, text });

  // Ton vom Desktop
  if (!a.supported) { add(false, t('audio.noSupport')); }
  else {
    add(a.remote.output ? true : false, a.remote.output ? t('audio.channelYes') : t('audio.channelNo'));
    if (!a.wanted) add(null, t('audio.playOff'));
    else if (a.state !== 'running') add(false, t('audio.playTap'));
    else if (!a.active) add(null, t('audio.playPaused'));
    else add(true, t('audio.playOn'));
    const s = a.stats;
    if (a.wanted && a.state === 'running') {
      add(s.received > 0 ? true : null, t('audio.received', { blocks: s.blocks - s.ignored, frames: s.received }));
      add(null, t('audio.level', { n: pct(s.peak), buffer: s.bufferedMs, target: s.targetMs, under: s.underruns, dropped: s.dropped }));
      if (a.active && a.remote.output && s.blocks - s.ignored === 0 && now - a.createdAt > SILENCE_WAIT_MS) add(null, t('audio.silent'));
    }
  }

  // Mikrofon
  add(a.remote.microphone ? true : null, a.remote.microphone ? t('audio.micChannelYes') : t('audio.micChannelNo'));
  if (!secure || !a.micSupported) add(false, t('audio.micInsecure'));
  else if (!a.micOn) add(null, t('audio.micOff'));
  else if (a.remote.recording) add(true, t('audio.micRecording', { n: pct(a.micLevel), chunks: a.micChunks }));
  else add(null, t('audio.micWaiting'));
  if (micPerm === 'granted') add(true, t('audio.permGranted'));
  else if (micPerm === 'prompt') add(null, t('audio.permPrompt'));
  else if (micPerm === 'denied') add(false, t('audio.permDenied', { hint: micDeniedHint(kind) }));
  else add(null, t('audio.permUnknown'));
  return lines;
}
