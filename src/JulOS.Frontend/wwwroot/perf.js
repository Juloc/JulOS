/* =====================================================================
 * Leistungsanzeige einer Stream-Sitzung: Bilder je Sekunde, Reaktionszeit (Eingabe bis neues Bild), Datenrate, Aufloesung, Kodierer.
 * Dazu die Stufen der Bildrate (Nachricht 31, siehe StreamQuality.cs). Reine Logik ohne DOM (getestet in tests/frontend/perf.test.mjs);
 * stream.js zaehlt, statusbar.js zeigt an. Beschreibung: docs/STATUSBAR.md.
 *
 * Kosten im Normalfall: zwei Zaehler und ein Zeitstempel je Bild bzw. Nachricht. Gerechnet wird nur, wenn die Anzeige sample() ruft
 * (einmal je Sekunde, nur solange sie sichtbar ist).
 * ===================================================================== */

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
  return (kbps / 1000).toFixed(kbps < 10000 ? 1 : 0).replace('.', ',') + ' Mbit/s';
}

/**
 * Die Anzeige als Liste { id, label, value, title }: fps (Zahl), reaction (ms), rate, size, encoder.
 * info: { sample, width, height, encoder (Text), quality (Name der Stufe) }
 */
export function perfItems(info) {
  const s = info.sample || { fps: 0, kbps: 0, reaction: null };
  return [
    { id: 'fps', label: 'Bilder/s', value: String(Math.round(s.fps)), title: 'Bilder, die in der letzten Sekunde gezeichnet wurden' },
    { id: 'reaction', label: 'Reaktion', value: s.reaction === null ? '–' : s.reaction + ' ms', title: 'Zeit von Klick oder Taste bis zum nächsten Bild (Mittel der letzten Eingaben)' },
    { id: 'rate', label: 'Daten', value: formatRate(s.kbps), title: 'Empfangene Datenmenge je Sekunde' },
    { id: 'size', label: 'Auflösung', value: info.width && info.height ? `${info.width}×${info.height}` : '–', title: 'Größe des Desktops am Ziel' },
    { id: 'encoder', label: 'Kodierer', value: info.encoder || '–', title: 'Womit der Server das Bild kodiert' },
    { id: 'quality', label: 'Bildrate', value: info.quality || 'Automatisch', title: 'Gewählte Stufe der Bildrate' },
  ];
}

/* ---------- Stufen der Bildrate (Nachricht 31) ---------- */
export const QUALITIES = [
  { id: 'auto', label: 'Automatisch', byte: 0, hint: 'wie bisher: das Netz bestimmt das Tempo' },
  { id: '60', label: '60 Bilder/s', byte: 1, hint: 'höchstens 60 Bilder/s, volle Bitrate' },
  { id: '30', label: '30 Bilder/s', byte: 2, hint: 'höchstens 30 Bilder/s, volle Bitrate' },
  { id: 'eco', label: 'Sparsam', byte: 3, hint: '15 Bilder/s und weniger Daten (Mobilfunk, Akku)' },
];

export const normalizeQuality = (id) => (QUALITIES.some((q) => q.id === id) ? id : 'auto');
export const qualityOf = (id) => QUALITIES.find((q) => q.id === id) || QUALITIES[0];
export const nextQuality = (id) => QUALITIES[(QUALITIES.indexOf(qualityOf(id)) + 1) % QUALITIES.length].id;
