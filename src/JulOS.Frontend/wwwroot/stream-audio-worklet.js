/* =====================================================================
 * Ton im Audio-Faden (AudioWorklet): hier laeuft alles, was pro Sample geschieht, damit der Haupt-Faden nichts davon rechnet.
 *
 * jul-player   spielt den Ton des fernen Rechners. Die Haupt-Seite schickt Stuecke (16 Bit, verschachtelt) in einen Ringpuffer;
 *              gespielt wird mit linearer Neuberechnung auf die Rate des Browsers. Der Puffer haelt eine kleine Reserve (Ziel 60 ms,
 *              waechst nach einem Aussetzer bis 200 ms und schrumpft wieder). Liegt mehr darin als das Ziel, wird der Ton bis
 *              3 % schneller gespielt (die Verzoegerung baut sich ab, ohne zu knacken); bei mehr als Ziel + 150 ms wird das Aelteste
 *              verworfen. So driftet nichts, auch wenn die Uhren beider Seiten nicht gleich laufen.
 * jul-capture  nimmt das Mikrofon: rechnet auf die Rate herunter, die der ferne Rechner will, und gibt 20-ms-Stuecke (16 Bit, Mono) zurueck.
 * ===================================================================== */

class JulPlayer extends AudioWorkletProcessor {
  constructor() {
    super();
    this.rate = 0; this.channels = 2; this.size = 0; this.left = null; this.right = null;
    this.write = 0; this.pos = 0;                 // Schreib- und Leseposition, in Samples der Quellrate (wachsen ohne Ende, der Index ist modulo)
    this.targetMs = 60; this.buffering = true;
    this.underrunAt = -1;                         // Bild, an dem der letzte Aussetzer begann (currentFrame)
    this.underruns = 0; this.dropped = 0; this.calm = 0; this.reported = 0; this.peak = 0; this.received = 0;
    this.port.onmessage = (e) => this.onmessage(e.data);
  }

  onmessage(m) {
    if (m.type === 'reset') { this.write = 0; this.pos = 0; this.buffering = true; return; }
    if (m.type !== 'pcm') return;
    // Kommt nach einer langen Pause (ein Klang endete, ein neuer beginnt) wieder Ton, war das kein Ruckeln: wieder mit der kleinen Reserve beginnen
    if (this.underrunAt >= 0 && currentFrame - this.underrunAt > sampleRate) { this.targetMs = 60; this.underrunAt = -1; }
    if (m.rate !== this.rate || m.channels !== this.channels || !this.left) this.format(m.rate, m.channels);
    const data = new Int16Array(m.buffer, m.offset, m.length), ch = m.channels, frames = (data.length / ch) | 0, size = this.size;
    for (let i = 0; i < frames; i++) {
      const at = (this.write + i) % size;
      this.left[at] = data[i * ch] / 32768;
      this.right[at] = (ch > 1 ? data[i * ch + 1] : data[i * ch]) / 32768;
    }
    this.write += frames; this.received += frames;
  }

  format(rate, channels) {
    this.rate = rate; this.channels = channels; this.size = rate * 2;       // zwei Sekunden Platz, benutzt werden hoechstens 300 ms
    this.left = new Float32Array(this.size); this.right = new Float32Array(this.size);
    this.write = 0; this.pos = 0; this.buffering = true;
  }

  process(inputs, outputs) {
    const out = outputs[0], l = out[0], r = out[1] || out[0], n = l.length;
    if (!this.rate) return true;
    const rate = this.rate, bufferedMs = ((this.write - this.pos) / rate) * 1000;

    // Ueberlauf: zu viel angestaut (Tab war verdeckt, Netz stockte): das Aelteste verwerfen und bei der Reserve weitermachen
    if (bufferedMs > this.targetMs + 150) {
      const keep = Math.floor((this.targetMs / 1000) * rate);
      this.dropped += (this.write - this.pos) - keep; this.pos = this.write - keep;
    }
    if (this.buffering) {
      if (((this.write - this.pos) / rate) * 1000 >= this.targetMs) this.buffering = false; else { this.report(); return true; }
    }

    // Etwas schneller spielen, solange mehr als das Ziel im Puffer liegt; etwas langsamer, wenn es knapp wird
    const err = ((this.write - this.pos) / rate) * 1000 - this.targetMs;
    const adj = 1 + Math.max(-0.01, Math.min(0.03, err * 0.0004));
    const step = (rate / sampleRate) * adj, size = this.size;
    let pos = this.pos, peak = this.peak, i = 0;
    for (; i < n; i++) {
      const base = Math.floor(pos);
      if (base + 1 >= this.write) break;                                       // aufgebraucht
      const f = pos - base, a = base % size, b = (base + 1) % size;
      const sl = this.left[a] + (this.left[b] - this.left[a]) * f, sr = this.right[a] + (this.right[b] - this.right[a]) * f;
      l[i] = sl; if (r !== l) r[i] = sr;
      const v = sl < 0 ? -sl : sl; if (v > peak) peak = v;
      pos += step;
    }
    this.pos = pos; this.peak = peak;
    if (i < n) {
      // Aussetzer: Stille fuer den Rest, neu auffuellen, und die Reserve etwas groesser machen
      for (; i < n; i++) { l[i] = 0; if (r !== l) r[i] = 0; }
      this.buffering = true; this.underruns++; this.calm = 0; this.underrunAt = currentFrame;
      this.targetMs = Math.min(200, this.targetMs * 1.4);
    } else if (++this.calm > 3000) { this.calm = 0; this.targetMs = Math.max(40, this.targetMs - 5); }   // etwa alle 8 s ohne Aussetzer 5 ms weniger
    this.report();
    return true;
  }

  report() {
    if (++this.reported < 64) return;                                            // etwa 5 Mal pro Sekunde
    this.reported = 0;
    this.port.postMessage({ type: 'stats', bufferedMs: Math.round(((this.write - this.pos) / (this.rate || 1)) * 1000), targetMs: Math.round(this.targetMs),
      underruns: this.underruns, dropped: this.dropped, peak: this.peak, received: this.received });
    this.peak = 0;
  }
}
registerProcessor('jul-player', JulPlayer);

class JulCapture extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const o = options.processorOptions || {};
    this.targetRate = o.rate || 16000;
    this.ratio = sampleRate / this.targetRate;            // Quellsamples je Ausgabesample
    this.chunk = Math.max(1, Math.round(this.targetRate * 0.02));
    this.out = new Int16Array(this.chunk); this.filled = 0;
    this.acc = 0; this.count = 0; this.phase = 0; this.last = 0; this.peak = 0; this.reported = 0;
    this.port.onmessage = () => {};
  }

  process(inputs) {
    const input = inputs[0] && inputs[0][0];
    if (!input) return true;
    for (let i = 0; i < input.length; i++) {
      const s = input[i];
      this.acc += s; this.count++; this.last = s;
      this.phase += 1;
      while (this.phase >= this.ratio) {                  // Herunterrechnen: Mittel der Samples im Abschnitt; Hochrechnen: letzten Wert halten
        this.phase -= this.ratio;
        const v = this.count ? this.acc / this.count : this.last;
        this.acc = 0; this.count = 0;
        const c = v > 1 ? 1 : v < -1 ? -1 : v;
        const a = c < 0 ? -c : c; if (a > this.peak) this.peak = a;
        this.out[this.filled++] = c < 0 ? c * 32768 : c * 32767;
        if (this.filled === this.chunk) {
          const buffer = this.out.buffer;
          this.port.postMessage({ type: 'pcm', buffer, peak: this.peak }, [buffer]);
          this.out = new Int16Array(this.chunk); this.filled = 0; this.peak = 0;
        }
      }
    }
    return true;
  }
}
registerProcessor('jul-capture', JulCapture);
