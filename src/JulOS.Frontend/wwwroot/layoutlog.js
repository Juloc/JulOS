/*
 * Groessen-Logik des Handy-Clients: wann darf die Remote-Aufloesung geaendert werden (SizeGate), wie oft hat sich in den letzten 10 s
 * etwas geaendert (SizeCounter, Schleifen-Erkennung) und die Diagnose fuer ?debug (LayoutLog: Messwerte ins Server-Log, Anzeige auf dem Bild).
 * SizeGate und SizeCounter kennen keinen Browser (deploy/dev/test.sh startet ihre Tests mit node); LayoutLog liest das Fenster.
 * Es werden nur Zahlen und kurze Woerter protokolliert, nie Eingaben.
 */
import { t } from './i18n.js';

/** Zaehlt Groessenwechsel je Art in einem gleitenden Fenster ('send' gesendet, 'remote' von der VM gemeldet, 'stage' Bildflaeche, 'conn' Verbindungsaufbau). */
export class SizeCounter {
  constructor(windowMs = 10000, max = 12) { this.windowMs = windowMs; this.max = max; this.items = []; this.alertedAt = -Infinity; }
  note(kind, now) { this.items.push({ kind, t: now }); this.#prune(now); }
  #prune(now) { while (this.items.length && now - this.items[0].t >= this.windowMs) this.items.shift(); }
  counts(now) {
    this.#prune(now);
    const c = { n: this.items.length, send: 0, remote: 0, stage: 0, conn: 0 };
    for (const i of this.items) if (i.kind in c) c[i.kind] += 1;
    return c;
  }
  /** Mehr als max Wechsel im Fenster = vermutlich eine Schleife. */
  looping(now) { return this.counts(now).n > this.max; }
  /** true hoechstens einmal je Fenster, wenn gerade eine Schleife erkannt ist (fuer den Eintrag im Server-Log). */
  shouldReport(now) {
    if (!this.looping(now) || now - this.alertedAt < this.windowMs) return false;
    this.alertedAt = now; return true;
  }
}

/**
 * Entscheidet, ob eine neue Remote-Groesse jetzt gesendet wird. Jede Aenderung baut das Bild der VM neu auf, deshalb:
 * nur bei echtem Unterschied (Toleranz), erst wenn die gewuenschte Groesse settleMs lang gleich geblieben ist (Entprellung: Tastatur-Animation,
 * Adressleiste, Drehen), nicht waehrend die Tastatur ein- oder ausfaehrt (hold), mit Mindestabstand
 * und mit einer Notbremse gegen Rueckkopplung (hoechstens stormMax in stormWindow, danach stormPause Ruhe).
 */
export class SizeGate {
  constructor({ tolW = 8, tolH = 16, minGap = 1000, stormMax = 6, stormWindow = 20000, stormPause = 4000, settleMs = 0 } = {}) {
    Object.assign(this, { tolW, tolH, minGap, stormMax, stormWindow, stormPause, settleMs });
    this.times = []; this.lastSendAt = -Infinity; this.pend = null;
  }
  #near(a, b) { return Math.abs(a.w - b.w) <= this.tolW && Math.abs(a.h - b.h) <= this.tolH; }
  /** Meldet die gerade gewuenschte Groesse (bei jedem Wechsel der Flaeche); aendert sie sich, laeuft die Entprellung von vorn. */
  observe(now, want) { if (!this.pend || !this.#near(want, this.pend)) this.pend = { w: want.w, h: want.h, since: now }; }
  /** want/last = {w,h} in Geraetepixeln. Antwort: {action:'send'} | {action:'skip',why} | {action:'wait',why,delay}. */
  decide(now, want, last, { force = false, holdUntil = 0 } = {}) {
    if (force) return { action: 'send' };
    if (this.#near(want, last)) { this.pend = null; return { action: 'skip', why: 'tol' }; }
    this.observe(now, want);
    const stable = now - this.pend.since;
    if (stable < this.settleMs) return { action: 'wait', why: 'settle', delay: this.settleMs - stable + 20 };
    if (holdUntil > now) return { action: 'wait', why: 'hold', delay: holdUntil - now + 30 };
    const since = now - this.lastSendAt;
    if (since < this.minGap) return { action: 'wait', why: 'gap', delay: this.minGap - since + 20 };
    this.times = this.times.filter((t) => now - t < this.stormWindow);
    if (this.times.length >= this.stormMax) return { action: 'wait', why: 'storm', delay: this.stormPause, n: this.times.length };
    return { action: 'send' };
  }
  sent(now) { this.times.push(now); this.lastSendAt = now; this.pend = null; }
}

/** Beobachtet das Layout und schreibt Messwerte; Anzeige auf dem Bild und dichte Protokollierung nur mit ?debug. */
export class LayoutLog {
  /**
   * emit(evt, data): Weg ins Server-Log (dbg in app.js). parts: { app, stage, topbar, dock } Elemente. state(): { kbOpen, k }.
   */
  constructor({ emit, parts, state, visible }) {
    this.emit = emit; this.parts = parts; this.state = state; this.visible = visible;
    this.counter = new SizeCounter();
    this.lastKey = ''; this.lastSend = null; this.lastRemote = null; this.laySent = 0; this.timer = null; this.hudTimer = null;
    this.probe = document.createElement('div');       // misst die Sicherheitsabstaende (Kameraloch, Navigationsleiste)
    this.probe.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;visibility:hidden;pointer-events:none;padding:env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left)';
    document.body.appendChild(this.probe);
    if (visible) {
      this.hud = document.createElement('div'); this.hud.id = 'layHud';
      this.hud.style.cssText = 'position:absolute;left:4px;top:4px;z-index:25;pointer-events:none;font:10px/1.25 ui-monospace,monospace;color:#fff;background:rgba(0,0,0,.62);padding:3px 6px;border-radius:6px;white-space:pre;max-width:96%;overflow:hidden';
      parts.stage.appendChild(this.hud);
      this.warn = document.createElement('div'); this.warn.id = 'layWarn';
      this.warn.style.cssText = 'position:absolute;left:8px;right:8px;top:64px;z-index:26;pointer-events:none;font:600 13px/1.3 system-ui,sans-serif;color:#fff;background:rgba(190,30,30,.92);padding:8px 10px;border-radius:10px;text-align:center';
      this.warn.hidden = true; parts.stage.appendChild(this.warn);
    }
  }
  dispose() { clearTimeout(this.timer); clearInterval(this.hudTimer); this.probe.remove(); if (this.hud) { this.hud.remove(); this.warn.remove(); } }
  /** Messwerte des Fensters; alle Groessen in CSS-Pixeln, ausser dpr. */
  snapshot() {
    const vv = window.visualViewport, cs = getComputedStyle(this.probe), p = this.parts, r = p.stage.getBoundingClientRect();
    const px = (v) => Math.round(parseFloat(v) || 0);
    return {
      iw: window.innerWidth, ih: window.innerHeight,
      vw: vv ? Math.round(vv.width) : 0, vh: vv ? Math.round(vv.height) : 0, vt: vv ? Math.round(vv.offsetTop) : 0, vl: vv ? Math.round(vv.offsetLeft) : 0, vs: vv ? +vv.scale.toFixed(2) : 1,
      sat: px(cs.paddingTop), sar: px(cs.paddingRight), sab: px(cs.paddingBottom), sal: px(cs.paddingLeft),
      sw: Math.round(r.width), sh: Math.round(r.height), tb: p.topbar.hidden ? 0 : Math.round(p.topbar.offsetHeight), dk: Math.round(p.dock.offsetHeight),
      kb: this.state().kbOpen ? 1 : 0, dpr: +(window.devicePixelRatio || 1).toFixed(2),
    };
  }
  /** Fenster oder Tastatur haben sich geaendert: hoechstens alle 200 ms ein Eintrag, nur wenn sich etwas bewegt hat. */
  layout(why) {
    if (!this.visible || this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      const s = this.snapshot();
      const key = [s.iw, s.ih, s.vw, s.vh, s.vt, s.sat, s.sab, s.sw, s.sh, s.tb, s.dk, s.kb].join(',');
      if (key === this.lastKey) return;
      this.lastKey = key;
      if (this.laySent++ < 400) this.emit('lay', { why, ...s, n10: this.counter.counts(performance.now()).n });
      this.refresh();
    }, 200);
  }
  /** Ein Groessenwechsel: kind = 'send' | 'remote' | 'stage' | 'conn'. */
  note(kind, data = {}) {
    const now = performance.now();
    this.counter.note(kind, now);
    if (kind === 'send') this.lastSend = `${data.w}x${data.h}`;
    if (kind === 'remote') this.lastRemote = `${data.w}x${data.h}`;
    if (this.counter.shouldReport(now)) this.emit('loop', { ...this.counter.counts(now), ...this.snapshot() });
    if (this.visible) this.refresh();
  }
  refresh() {
    if (!this.visible || this.hudTimer) return;
    this.hudTimer = setTimeout(() => { this.hudTimer = null; this.draw(); }, 250);
  }
  draw() {
    if (!this.hud) return;
    const now = performance.now(), s = this.snapshot(), c = this.counter.counts(now);
    this.hud.textContent =
      `in ${s.iw}x${s.ih}  vv ${s.vw}x${s.vh}+${s.vt}  x${s.dpr}\n` +
      `stage ${s.sw}x${s.sh}  bar ${s.tb}  dock ${s.dk}  kb ${s.kb}\n` +
      `safe ${s.sat}/${s.sar}/${s.sab}/${s.sal}\n` +
      `10 s: ${c.n} (send ${c.send}, vm ${c.remote}, stage ${c.stage}, conn ${c.conn})\n` +
      `gesendet ${this.lastSend || '-'}  VM ${this.lastRemote || '-'}`;
    const loop = this.counter.looping(now);
    this.warn.hidden = !loop;
    if (loop) {
      this.warn.textContent = t('term.layoutLoop', { n: c.n });
      clearTimeout(this.clearTimer); this.clearTimer = setTimeout(() => this.draw(), 10500);   // Hinweis geht von selbst weg, wenn Ruhe ist
    }
  }
}
