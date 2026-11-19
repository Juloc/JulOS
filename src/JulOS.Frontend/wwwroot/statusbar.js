/* =====================================================================
 * Statusleiste einer Stream-Sitzung (ein-/ausblendbar): zeigt die Werte aus perf.js. Im Desktop als schmale Fusszeile am unteren Rand des
 * Sitzungsfensters, auf dem Handy als kleines Overlay oben im Bild (CSS: style.css, desktop.css). Beschreibung: docs/STATUSBAR.md.
 *
 * Ausgeblendet kostet sie nichts: kein Zeitgeber, keine Rechnung. Eingeblendet liest sie einmal je Sekunde die Zaehler des Clients.
 * ===================================================================== */
import { perfItems, qualityOf } from './perf.js';

export class StatusBar {
  /** @param {HTMLElement} el das Element, das die Leiste zeigt (wird gefuellt) */
  constructor(el) {
    this.el = el; this.client = null; this.on = false; this.active = true; this.timer = 0;
    this.quality = 'auto';
    this.cells = new Map();
    el.hidden = true;
    el.setAttribute('role', 'status');
  }

  /** Der Client der Sitzung (oder null, solange keiner da ist). */
  attach(client) { this.client = client; this.render(); }

  /** Anzeigen oder verbergen. */
  show(on) { this.on = !!on; this.el.hidden = !this.on; this.sync(); }

  /** Die Sitzung ist im Hintergrund (nicht sichtbar): dann wird nichts gerechnet. */
  setActive(on) { this.active = !!on; this.sync(); }

  /** Die gewaehlte Stufe der Bildrate fuer die Anzeige. */
  setQuality(id) { this.quality = id; this.render(); }

  sync() {
    const run = this.on && this.active;
    if (run && !this.timer) { this.render(); this.timer = setInterval(() => this.render(), 1000); }
    else if (!run && this.timer) { clearInterval(this.timer); this.timer = 0; }
  }

  render() {
    if (!this.on) return;
    const c = this.client, display = c && c.display;
    const items = perfItems({
      sample: c && c.perf ? c.perf.sample(performance.now()) : null,
      width: display ? display.getWidth() : 0, height: display ? display.getHeight() : 0,
      encoder: c && c.stats ? c.stats.encoder : '', quality: qualityOf(this.quality).label,
    });
    if (!this.cells.size) {
      for (const it of items) {
        const cell = document.createElement('span'); cell.className = 'sv-perfc sv-perf-' + it.id; cell.title = it.title;
        const k = document.createElement('i'); k.textContent = it.label; const v = document.createElement('b');
        cell.append(k, v); this.el.appendChild(cell); this.cells.set(it.id, v);
      }
    }
    for (const it of items) { const v = this.cells.get(it.id); if (v && v.textContent !== it.value) v.textContent = it.value; }
  }

  dispose() { clearInterval(this.timer); this.timer = 0; this.client = null; }
}
