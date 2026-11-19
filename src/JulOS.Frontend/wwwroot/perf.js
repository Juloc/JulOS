/* =====================================================================
 * Leistungsanzeige einer Stream-Sitzung: Bilder je Sekunde, Reaktionszeit (Eingabe bis neues Bild), Datenrate, Aufloesung, Kodierer.
 * Dazu die Stufen der Bildrate (Nachricht 31, siehe StreamQuality.cs). Reine Logik ohne DOM (getestet in tests/frontend/perf.test.mjs);
 * stream.js zaehlt, statusbar.js zeigt an. Beschreibung: docs/STATUSBAR.md.
 *
 * Kosten im Normalfall: zwei Zaehler und ein Zeitstempel je Bild bzw. Nachricht. Gerechnet wird nur, wenn die Anzeige sample() ruft
 * (einmal je Sekunde, nur solange sie sichtbar ist).
 * ===================================================================== */

import { t, fmtNumber } from './i18n.js';

/** Wie lange nach einer Eingabe ein neues Bild noch als deren Antwort gilt; spaeter kommende Bilder haben mit ihr nichts zu tun. */
export const REACTION_WINDOW_MS = 1500;
const KEEP = 9;                 // so viele Reaktionszeiten gehen in den Mittelwert (Median)
const STALE_MS = 20_000;        // eine Reaktionszeit, die so lange her ist, wird nicht mehr gezeigt (der Wert waere irrefuehrend)

export class StreamPerf {
  constructor() {
    this.frames = 0; this.bytes = 0;          // Zaehler seit Beginn
    this.pending = null;                       // { at, seq }: die erste Eingabe, auf die noch kein Bild gefolgt ist
    this.reactions = [];                       // die letzten Reaktionszeiten (ms)
    this.last = { at: -1, frames: 0, bytes: 0 };
    this.fps = 0; this.kbps = 0;
  }

  /** Daten sind angekommen (Laenge der Nachricht). */
  received(length) { this.bytes += length; }

  /**
   * Der Benutzer hat geklickt, getippt oder gerollt. receivedSeq: Folgenummer des zuletzt empfangenen Bildes; ein Bild mit hoeherer Nummer
   * ist nach dieser Eingabe beim Browser angekommen (naeherungsweise: es kann schon vorher kodiert worden sein).
   */
  input(now, receivedSeq) { if (!this.pending || now - this.pending.at > REACTION_WINDOW_MS) this.pending = { at: now, seq: receivedSeq }; }

  /** Ein Bild ist gezeichnet. */
  drawn(now, sequence) {
    this.frames++;
    const p = this.pending;
    if (!p || sequence <= p.seq) return;
    this.pending = null;
    const ms = now - p.at;
    if (ms > REACTION_WINDOW_MS) return;       // zu spaet, das war eine andere Aenderung
    this.reactions.push({ ms, at: now });
    if (this.reactions.length > KEEP) this.reactions.shift();
  }

  /** Reaktionszeit (Median der letzten Eingaben) in ms, oder null (keine Eingabe in den letzten 20 s). now: Zeit des Aufrufs. */
  reaction(now) {
    const fresh = this.reactions.filter((r) => now - r.at <= STALE_MS).map((r) => r.ms).sort((a, b) => a - b);
    return fresh.length ? Math.round(fresh[fresh.length >> 1]) : null;
  }

  /** Werte seit dem letzten Aufruf; zu kurze Abstaende (unter 400 ms) liefern die vorigen Werte noch einmal. */
  sample(now) {
    const l = this.last;
    if (l.at < 0) { this.last = { at: now, frames: this.frames, bytes: this.bytes }; }
    else if (now - l.at >= 400) {
      const s = (now - l.at) / 1000;
      this.fps = (this.frames - l.frames) / s;
      this.kbps = ((this.bytes - l.bytes) * 8) / 1000 / s;
      this.last = { at: now, frames: this.frames, bytes: this.bytes };
    }
    return { fps: this.fps, kbps: this.kbps, reaction: this.reaction(now) };
  }
}

/** Datenrate lesbar: "640 kbit/s", "2,4 Mbit/s". */
export function formatRate(kbps) {
  if (!(kbps >= 0.5)) return '0 kbit/s';
  if (kbps < 1000) return Math.round(kbps) + ' kbit/s';
  return fmtNumber(kbps / 1000, kbps < 10000 ? 1 : 0) + ' Mbit/s';
}

/**
 * Die Anzeige als Liste { id, label, value, title }: fps (Zahl), reaction (ms), rate, size, encoder.
 * info: { sample, width, height, encoder (Text), quality (Name der Stufe) }
 */
export function perfItems(info) {
  const s = info.sample || { fps: 0, kbps: 0, reaction: null };
  return [
    { id: 'fps', label: t('statusbar.perfFps'), value: String(Math.round(s.fps)), title: t('statusbar.perfFpsTip') },
    { id: 'reaction', label: t('statusbar.perfReaction'), value: s.reaction === null ? '–' : s.reaction + ' ms', title: t('statusbar.perfReactionTip') },
    { id: 'rate', label: t('statusbar.perfRate'), value: formatRate(s.kbps), title: t('statusbar.perfRateTip') },
    { id: 'size', label: t('statusbar.perfSize'), value: info.width && info.height ? `${info.width}×${info.height}` : '–', title: t('statusbar.perfSizeTip') },
    { id: 'encoder', label: t('statusbar.perfEncoder'), value: info.encoder || '–', title: t('statusbar.perfEncoderTip') },
    { id: 'quality', label: t('statusbar.perfQuality'), value: info.quality || t('statusbar.qAuto'), title: t('statusbar.perfQualityTip') },
  ];
}

/* ---------- Stufen der Bildrate (Nachricht 31) ---------- */
export const QUALITIES = [
  { id: 'auto', label: t('statusbar.qAuto'), byte: 0, hint: t('statusbar.qAutoHint') },
  { id: '60', label: t('statusbar.q60'), byte: 1, hint: t('statusbar.q60Hint') },
  { id: '30', label: t('statusbar.q30'), byte: 2, hint: t('statusbar.q30Hint') },
  { id: 'eco', label: t('statusbar.qEco'), byte: 3, hint: t('statusbar.qEcoHint') },
];

export const normalizeQuality = (id) => (QUALITIES.some((q) => q.id === id) ? id : 'auto');
export const qualityOf = (id) => QUALITIES.find((q) => q.id === id) || QUALITIES[0];
export const nextQuality = (id) => QUALITIES[(QUALITIES.indexOf(qualityOf(id)) + 1) % QUALITIES.length].id;
