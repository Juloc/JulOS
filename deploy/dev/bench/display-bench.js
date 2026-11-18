// Messung im Browser: Reaktionszeit (Klick auf das Messquadrat bis zur Farbaenderung auf dem Anzeige-Canvas) und grobe Bildrate.
// Einfuegen ueber die Konsole oder das javascript-Werkzeug der Browser-Seite, dann: JSON.stringify(await window.__bench({}))
// Voraussetzung: angemeldet, verbunden (Guacamole oder Strom), probe.py laeuft im Test-Desktop (siehe README.md).
// Die Bildrate dieser Schleife ist durch die Abtastung (getImageData) auf etwa 11 je Sekunde gedeckelt: nur die Reaktionszeit zaehlt.
window.__bench = async function (o) {
  o = Object.assign({ n: 15, fpsSeconds: 6, px: 620, py: 90, vx: 200, vy: 120, video: true }, o || {});
  const S = window.__pd.S, c = S.client;
  const cv = S.engine === 'stream' ? S.display.flatten() : S.display.getDefaultLayer().getCanvas();
  const ctx = cv.getContext('2d', { willReadFrequently: true });
  const mc = new MessageChannel(); const yieldNow = () => new Promise((r) => { mc.port1.onmessage = () => r(); mc.port2.postMessage(0); });
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const st = (x, y, l) => ({ x, y, left: !!l, middle: false, right: false, up: false, down: false });
  const col = () => { const d = ctx.getImageData(o.px, o.py, 1, 1).data; return (d[0] > 150 && d[1] < 100) ? 'r' : (d[1] > 150 && d[0] < 100) ? 'g' : '?'; };
  const out = { engine: S.engine, canvas: [cv.width, cv.height] };
  if (o.video) {
    const hash = () => { const d = ctx.getImageData(o.vx, o.vy, 64, 32).data; let h = 0; for (let i = 0; i < d.length; i += 28) h = (h * 31 + d[i] + d[i + 1] * 3 + d[i + 2] * 7) | 0; return h; };
    let last = hash(), changes = 0; const t0 = performance.now();
    while (performance.now() - t0 < o.fpsSeconds * 1000) { await yieldNow(); const h = hash(); if (h !== last) { changes++; last = h; } }
    out.fps = +(changes / ((performance.now() - t0) / 1000)).toFixed(1);
  }
  c.sendMouseState(st(o.px, o.py, false)); await sleep(500);
  const res = [];
  for (let i = 0; i < o.n; i++) {
    const before = col(); const t0 = performance.now(); let t = null;
    c.sendMouseState(st(o.px, o.py, true)); c.sendMouseState(st(o.px, o.py, false));
    while (performance.now() - t0 < 3000) { await yieldNow(); const now = col(); if (now !== '?' && now !== before) { t = performance.now() - t0; break; } }
    res.push(t === null ? null : Math.round(t)); await sleep(600);
  }
  const ok = res.filter((x) => x !== null).sort((a, b) => a - b);
  out.latencyMs = { n: ok.length, lost: res.length - ok.length, median: ok[Math.floor(ok.length / 2)], p90: ok[Math.min(ok.length - 1, Math.floor(ok.length * 0.9))], min: ok[0], max: ok[ok.length - 1], all: res };
  return out;
};
