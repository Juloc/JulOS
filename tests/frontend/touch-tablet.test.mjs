// Tests des Tablet-Modus mit Zeitreihen (reine Logik, keine Optik): node --test tests/frontend
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  TouchGestures, normalizeTouch, compactTouch, scrollStep, WheelAccum, dragReadyMs,
  clampView, zoomAbout, panView, toVm, VIEW_ONE, ZOOM_MAX, FLING_TAU,
} from '../../src/JulOS.Frontend/wwwroot/touch-gestures.js';

/** Spielt eine Folge ab: [t, 'down'|'move'|'up'|'cancel', id, x, y]; zwischen den Ereignissen laeuft die Zeit wie im Browser (Zeitgeber weckt tick() zur Frist). */
function run(g, steps, until = null) {
  const out = [];
  const advance = (to) => { for (let d = g.deadline(); d !== null && d <= to; d = g.deadline()) { for (const a of g.tick(d)) out.push({ t: d, ...a }); } };
  for (const [t, type, id = 1, x = 100, y = 100] of steps) {
    advance(t);
    for (const a of g.feed({ type, id, x, y, t })) out.push({ t, ...a });
  }
  if (until !== null) advance(until);
  return out;
}
const tab = (cfg = {}) => new TouchGestures({ mode: 'tablet', ...cfg });
const kinds = (acts) => acts.filter((a) => a.type !== 'log').map((a) => a.type + (a.button ? ':' + a.button : '') + (a.phase ? ':' + a.phase : ''));
const clicks = (acts) => acts.filter((a) => a.type === 'click');
const logs = (acts) => acts.filter((a) => a.type === 'log').map((a) => a.kind);
const sum = (acts, k) => acts.filter((a) => a.type === 'scroll').reduce((n, a) => n + a[k], 0);
/** Ein Wisch: ein Finger bewegt sich in gleichen Schritten von (x0,y0) nach (x1,y1) in dur ms. */
function swipe(x0, y0, x1, y1, dur, t0 = 0, id = 1, stepMs = 16) {
  const n = Math.max(1, Math.round(dur / stepMs)), s = [[t0, 'down', id, x0, y0]];
  for (let i = 1; i <= n; i++) s.push([t0 + i * stepMs, 'move', id, x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n]);
  return s;
}

test('Vorgabe: Tablet; "Maus" und Touchpad bleiben waehlbar, Abweichungen gehen als Einstellung zum Server', () => {
  assert.equal(normalizeTouch(undefined).mode, 'tablet');
  assert.equal(normalizeTouch({ mode: 'pad' }).mode, 'pad');
  assert.equal(normalizeTouch({ mode: 'direct' }).mode, 'direct');
  assert.equal(normalizeTouch({ pinch: 'size' }).pinch, 'size');
  assert.equal(normalizeTouch({ pinch: 'x' }).pinch, 'view');
  assert.deepEqual(compactTouch({ mode: 'direct' }), { mode: 'direct' });
  assert.deepEqual(compactTouch({ mode: 'tablet' }), {});
});

test('Tippen: Zeiger vorab an die Stelle, Linksklick erst beim Heben, kein Ring', () => {
  const acts = run(tab(), [[0, 'down', 1, 50, 60], [90, 'up', 1, 51, 61]]);
  assert.deepEqual(kinds(acts), ['hover', 'click:left']);
  assert.deepEqual([clicks(acts)[0].x, clicks(acts)[0].y, clicks(acts)[0].how], [50, 60, 'tap']);
  assert.deepEqual(logs(acts), ['tap']);
});

test('Doppeltipp: der zweite Tipp in kleinem Umkreis wird auf die Stelle des ersten gelegt; ein dritter ist wieder ein einzelner', () => {
  const g = tab();
  const acts = run(g, [[0, 'down', 1, 100, 100], [60, 'up', 1, 100, 100], [200, 'down', 1, 112, 95], [260, 'up', 1, 112, 95], [400, 'down', 1, 112, 95], [450, 'up', 1, 112, 95]]);
  const c = clicks(acts);
  assert.equal(c.length, 3);
  assert.deepEqual([c[0].x, c[0].y, c[0].how], [100, 100, 'tap']);
  assert.deepEqual([c[1].x, c[1].y, c[1].how], [100, 100, 'double'], 'zweiter Klick an DERSELBEN Stelle');
  assert.deepEqual([c[2].x, c[2].y, c[2].how], [112, 95, 'tap'], 'der dritte Tipp zaehlt nicht als weiterer Doppelklick');
});

test('Doppeltipp zu langsam oder zu weit weg: zwei einzelne Klicks an den eigenen Stellen', () => {
  let c = clicks(run(tab(), [[0, 'down', 1, 100, 100], [50, 'up', 1, 100, 100], [600, 'down', 1, 105, 100], [650, 'up', 1, 105, 100]]));
  assert.deepEqual(c.map((a) => [a.x, a.how]), [[100, 'tap'], [105, 'tap']]);
  c = clicks(run(tab(), [[0, 'down', 1, 100, 100], [50, 'up', 1, 100, 100], [200, 'down', 1, 200, 100], [250, 'up', 1, 200, 100]]));
  assert.deepEqual(c.map((a) => [a.x, a.how]), [[100, 'tap'], [200, 'tap']]);
});

test('Wischen senkrecht: beginnt erst nach der Schwelle, kein Klick, Zeiger vorher an die Stelle, natuerlich = Inhalt folgt dem Finger', () => {
  const g = tab();
  const acts = run(g, swipe(100, 400, 100, 200, 200).concat([[260, 'up', 1, 100, 200]]));
  assert.equal(acts[0].type, 'hover');
  assert.deepEqual([acts[0].x, acts[0].y], [100, 400]);
  assert.equal(clicks(acts).length, 0);
  assert.ok(acts.some((a) => a.type === 'scroll'));
  assert.equal(Math.round(sum(acts, 'fdy')), -200, 'der Finger ist 200 px nach oben gegangen, nichts davon geht verloren (auch die ersten 12 px nicht)');
  assert.ok(sum(acts, 'dy') > 200, 'natuerlich: Finger nach oben = Rad nach unten (positiv), bei schnellem Wischen verstaerkt');
  assert.equal(sum(acts, 'dx'), 0);
  assert.equal(acts.filter((a) => a.type === 'scroll')[0].x, 100, 'gescrollt wird dort, wo der Finger aufsetzte');
});

test('Wischen: unter der Schwelle bleibt es ein Tippen; klassisch dreht die Richtung um', () => {
  const acts = run(tab(), [[0, 'down', 1, 100, 100], [40, 'move', 1, 104, 106], [80, 'up', 1, 104, 106]]);
  assert.deepEqual(kinds(acts), ['hover', 'click:left']);
  const cl = run(tab({ scrollDir: 'classic' }), swipe(100, 400, 100, 300, 200).concat([[260, 'up', 1, 100, 300]]));
  assert.ok(sum(cl, 'dy') < 0, 'klassisch: Finger nach oben = Bildlauf nach oben');
});

test('Achsen-Sperre: leichtes Abdriften scrollt nicht quer; waagerecht genauso; wer deutlich quer weiterwischt, entsperrt', () => {
  const v = run(tab(), swipe(100, 500, 118, 200, 300).concat([[360, 'up', 1, 118, 200]]));
  assert.equal(sum(v, 'dx'), 0, 'senkrecht mit 18 px Drift: keine waagerechten Rasten');
  assert.ok(sum(v, 'dy') !== 0);
  const h = run(tab(), swipe(300, 100, 40, 112, 300).concat([[360, 'up', 1, 40, 112]]));
  assert.equal(sum(h, 'dy'), 0);
  assert.ok(sum(h, 'dx') > 0, 'waagerecht: Finger nach links = Inhalt nach rechts schieben = Rad nach rechts (positiv)');
  // erst senkrecht, dann deutlich nach links: die Sperre faellt
  const steps = swipe(300, 500, 300, 380, 150).concat(swipe(300, 380, 120, 380, 200, 166).slice(1), [[400, 'up', 1, 120, 380]]);
  const both = run(tab(), steps);
  assert.ok(sum(both, 'dx') > 0 && sum(both, 'dy') > 0);
  const diag = run(tab(), swipe(300, 500, 150, 350, 200).concat([[260, 'up', 1, 150, 350]]));
  assert.ok(sum(diag, 'dx') > 0 && sum(diag, 'dy') > 0, 'schraeg: beide Achsen');
});

test('Schnelles Wischen scrollt mehr als langsames (gleicher Weg)', () => {
  const slow = run(tab(), swipe(100, 500, 100, 300, 1200).concat([[1300, 'up', 1, 100, 300]]));
  const fast = run(tab(), swipe(100, 500, 100, 300, 120).concat([[200, 'up', 1, 100, 300]]));
  assert.ok(sum(fast, 'dy') > sum(slow, 'dy') * 1.2, `${sum(fast, 'dy')} gegen ${sum(slow, 'dy')}`);
});

test('Nachlauf: nach schnellem Heben laeuft das Scrollen mit abklingender Geschwindigkeit weiter und endet von selbst', () => {
  const g = tab();
  const acts = run(g, swipe(100, 600, 100, 300, 150).concat([[160, 'up', 1, 100, 300]]), 4000);
  const after = acts.filter((a) => a.type === 'scroll' && a.fling);
  assert.ok(after.length > 10, 'viele kleine Schritte');
  assert.ok(after.every((a) => a.fdy < 0 && a.dx === 0), 'in Richtung des Wischens, ohne quer');
  const half = after.slice(0, Math.floor(after.length / 2)).reduce((n, a) => n + Math.abs(a.fdy), 0);
  const rest = after.slice(Math.floor(after.length / 2)).reduce((n, a) => n + Math.abs(a.fdy), 0);
  assert.ok(half > rest, 'klingt ab');
  const total = after.reduce((n, a) => n + Math.abs(a.fdy), 0);
  assert.ok(total > 100 && total < 2.2 * 2 * FLING_TAU * 3.5, `Weg des Nachlaufs ${total}`);
  assert.equal(g.deadline(), null, 'danach ist alles ruhig');
  assert.deepEqual(logs(acts), ['swipe', 'fling']);
});

test('Nachlauf: ein neuer Finger stoppt ihn sofort und klickt beim Heben nicht; danach geht Tippen wieder', () => {
  const g = tab();
  const acts = run(g, swipe(100, 600, 100, 300, 150).concat([[160, 'up', 1, 100, 300], [400, 'down', 1, 100, 300], [450, 'up', 1, 100, 300]]), 3000);
  assert.ok(acts.filter((a) => a.type === 'scroll' && a.fling).every((a) => a.t <= 400), 'nach dem Aufsetzen kein Schritt mehr');
  assert.equal(clicks(acts).length, 0, 'das Tippen zum Anhalten ist kein Klick');
  assert.ok(logs(acts).includes('stop'));
  const more = run(g, [[5000, 'down', 1, 10, 10], [5050, 'up', 1, 10, 10]]);
  assert.equal(clicks(more).length, 1);
});

test('Kein Nachlauf: Finger ruht vor dem Heben, langsamer Wisch, reduzierte Bewegung', () => {
  const rest = run(tab(), swipe(100, 600, 100, 300, 150).concat([[400, 'up', 1, 100, 300]]), 3000);
  assert.equal(rest.filter((a) => a.fling).length, 0, 'Finger lag still: nicht weiterscrollen');
  const slow = run(tab(), swipe(100, 600, 100, 400, 3000).concat([[3016, 'up', 1, 100, 400]]), 6000);
  assert.equal(slow.filter((a) => a.fling).length, 0);
  const g = tab(); g.setOptions({ reducedMotion: true });
  const rm = run(g, swipe(100, 600, 100, 300, 150).concat([[160, 'up', 1, 100, 300]]), 3000);
  assert.equal(rm.filter((a) => a.fling).length, 0);
  assert.ok(rm.some((a) => a.type === 'scroll'), 'das Wischen selbst scrollt');
});

test('Halten: Ring ab 140 ms, "Ziehen bereit" in der Mitte, "Rechtsklick bereit" bei voller Haltedauer; der Rechtsklick kommt erst beim Heben', () => {
  const g = tab();
  const acts = run(g, [[0, 'down', 1, 40, 40], [700, 'up', 1, 40, 40]]);
  assert.deepEqual(kinds(acts), ['hover', 'ring:start', 'ring:ready', 'ring:armed', 'ring:cancel', 'click:right']);
  assert.equal(acts.find((a) => a.phase === 'start').t, 140);
  assert.equal(acts.find((a) => a.phase === 'ready').t, dragReadyMs(500));
  assert.equal(dragReadyMs(500), 320);
  assert.equal(acts.find((a) => a.phase === 'armed').t, 500);
  assert.equal(acts.find((a) => a.phase === 'start').ms, 360, 'der Ring fuellt sich bis zur vollen Haltedauer');
  assert.equal(clicks(acts)[0].t, 700, 'der Rechtsklick geht beim Loslassen hinaus, nicht schon beim vollen Ring');
  assert.deepEqual(logs(acts), ['right']);
});

test('Halten und loslassen zwischen "Ziehen bereit" und "voll": ein gewoehnlicher Linksklick (langes Tippen)', () => {
  const acts = run(tab(), [[0, 'down', 1, 40, 40], [400, 'up', 1, 40, 40]]);
  assert.deepEqual(kinds(acts), ['hover', 'ring:start', 'ring:ready', 'ring:cancel', 'click:left']);
  assert.equal(clicks(acts)[0].how, 'long');
});

test('Rechtsklick nur mit zwei Fingern: Halten kennt nur "Ziehen bereit", der Ring fuellt sich bis dahin', () => {
  const acts = run(tab({ rightClick: 'two' }), [[0, 'down', 1, 40, 40], [900, 'up', 1, 40, 40]]);
  assert.ok(!acts.some((a) => a.phase === 'armed'));
  assert.equal(acts.find((a) => a.phase === 'start').ms, 180);
  assert.equal(clicks(acts)[0].button, 'left');
});

test('Halten und ziehen: ab "Ziehen bereit" haelt die linke Taste an der Startstelle bis zum Heben; Markierung sichtbar', () => {
  const acts = run(tab(), [[0, 'down', 1, 100, 100], [400, 'move', 1, 130, 100], [450, 'move', 1, 200, 140], [500, 'up', 1, 200, 140]]);
  assert.deepEqual(kinds(acts), ['hover', 'ring:start', 'ring:ready', 'ring:cancel', 'press:left', 'mark', 'move', 'mark', 'move', 'mark', 'release:left', 'mark']);
  const press = acts.find((a) => a.type === 'press');
  assert.deepEqual([press.x, press.y], [100, 100], 'gedrueckt wird an der Stelle, wo der Finger lag');
  assert.deepEqual(acts.filter((a) => a.type === 'move').map((a) => [a.x, a.y, a.pressed]), [[130, 100, true], [200, 140, true]]);
  const rel = acts.find((a) => a.type === 'release');
  assert.deepEqual([rel.x, rel.y], [200, 140]);
  assert.ok(acts.filter((a) => a.type === 'mark').at(-1).on === false);
  assert.equal(clicks(acts).length, 0);
  assert.deepEqual(logs(acts), ['drag-ready']);
});

test('Halten bis der Ring voll ist und dann ziehen: auch das zieht (kein Rechtsklick)', () => {
  const acts = run(tab(), [[0, 'down', 1, 100, 100], [600, 'move', 1, 160, 100], [700, 'up', 1, 160, 100]]);
  assert.equal(clicks(acts).length, 0);
  assert.ok(acts.some((a) => a.type === 'press') && acts.some((a) => a.type === 'release'));
  assert.deepEqual(logs(acts), ['drag-hold']);
});

test('Zittern beim Halten (unter der Schwelle) bricht weder Ring noch Rechtsklick ab', () => {
  const acts = run(tab(), [[0, 'down', 1, 100, 100], [200, 'move', 1, 104, 103], [450, 'move', 1, 98, 106], [650, 'up', 1, 98, 106]]);
  assert.ok(clicks(acts).length === 1 && clicks(acts)[0].button === 'right');
});

test('Wisch zu spaet begonnen (der Finger lag schon ueber "Ziehen bereit"): das ist ein Ziehen, kein Scrollen', () => {
  const acts = run(tab(), [[0, 'down', 1, 100, 100], [330, 'move', 1, 100, 160], [360, 'up', 1, 100, 160]]);
  assert.ok(!acts.some((a) => a.type === 'scroll'));
  assert.ok(acts.some((a) => a.type === 'press'));
});

test('Zwei Finger: Pinch zoomt optisch (Verhaeltnis seit Beginn, Mitte), am Ende zoomEnd; mit "Aufloesung" bleibt der alte Pinch', () => {
  const steps = [[0, 'down', 1, 100, 300], [10, 'down', 2, 200, 300], [60, 'move', 2, 240, 300], [120, 'move', 2, 300, 300], [180, 'up', 2, 300, 300]];
  const acts = run(tab(), steps);
  const z = acts.filter((a) => a.type === 'zoom');
  assert.ok(z.length >= 2 && z[0].start && !z[1].start);
  assert.ok(Math.abs(z.at(-1).ratio - 2) < 1e-9, 'Abstand 100 -> 200');
  assert.deepEqual([z[0].from.x, z[0].from.y], [150, 300], 'Mitte zu Beginn');
  assert.ok(acts.some((a) => a.type === 'zoomEnd'));
  assert.ok(!acts.some((a) => a.type === 'scroll' || a.type === 'pinch'));
  assert.ok(logs(acts).includes('pinch'));
  const old = run(tab({ pinch: 'size' }), steps);
  assert.ok(old.some((a) => a.type === 'pinch') && old.some((a) => a.type === 'pinchEnd') && !old.some((a) => a.type === 'zoom'));
});

test('Zwei Finger schieben: ungezoomt scrollen, gezoomt das Bild verschieben (es folgt den Fingern)', () => {
  const steps = [[0, 'down', 1, 100, 400], [5, 'down', 2, 200, 400], [40, 'move', 1, 100, 360], [41, 'move', 2, 200, 360], [80, 'move', 1, 100, 300], [81, 'move', 2, 200, 300], [120, 'up', 1, 100, 300]];
  const plain = run(tab(), steps);
  assert.ok(plain.some((a) => a.type === 'scroll') && !plain.some((a) => a.type === 'pan'));
  const g = tab(); g.setOptions({ zoomed: true });
  const zoomed = run(g, steps);
  const pans = zoomed.filter((a) => a.type === 'pan');
  assert.ok(pans.length && !zoomed.some((a) => a.type === 'scroll'));
  assert.equal(Math.round(pans.reduce((n, a) => n + a.dy, 0)), -100, 'das Bild geht 100 px mit den Fingern nach oben');
  assert.equal(pans.reduce((n, a) => n + a.dx, 0), 0);
});

test('Zwei-Finger-Tipp: Rechtsklick in der Mitte; gezoomt wartet er, ein zweiter Tipp setzt den Zoom zurueck', () => {
  const tap = (t0) => [[t0, 'down', 1, 100, 100], [t0 + 5, 'down', 2, 140, 100], [t0 + 100, 'up', 1, 100, 100], [t0 + 105, 'up', 2, 140, 100]];
  let acts = run(tab(), tap(0));
  assert.deepEqual([clicks(acts)[0].button, clicks(acts)[0].x, clicks(acts)[0].how], ['right', 120, 'two']);
  const g = tab(); g.setOptions({ zoomed: true });
  acts = run(g, tap(0).concat(tap(250)), 2000);
  assert.ok(acts.some((a) => a.type === 'zoomReset'));
  assert.equal(clicks(acts).length, 0, 'der wartende Rechtsklick entfaellt');
  const g2 = tab(); g2.setOptions({ zoomed: true });
  assert.equal(clicks(run(g2, tap(0), 2000)).length, 1, 'ohne zweiten Tipp kommt der Rechtsklick nach der Wartezeit');
});

test('Abbruch (touchcancel) mitten in jeder Geste: keine Taste bleibt haengen, Ring und Markierung weg, Nachlauf stoppt, danach geht alles wieder', () => {
  const dragging = tab(); let acts = run(dragging, [[0, 'down', 1, 100, 100], [400, 'move', 1, 150, 100], [450, 'cancel']]);
  assert.ok(acts.some((a) => a.type === 'release') && acts.filter((a) => a.type === 'mark').at(-1).on === false);
  assert.equal(dragging.pressed, false);
  const ring = tab(); acts = run(ring, [[0, 'down'], [250, 'cancel']]);
  assert.ok(acts.some((a) => a.phase === 'cancel'));
  assert.equal(clicks(acts).length, 0);
  const scrolling = tab(); acts = run(scrolling, swipe(100, 500, 100, 300, 150).concat([[160, 'cancel']]), 3000);
  assert.ok(!acts.some((a) => a.fling), 'nach dem Abbruch kein Nachlauf');
  const flinging = tab(); run(flinging, swipe(100, 500, 100, 300, 150).concat([[160, 'up', 1, 100, 300]]), 200);
  assert.ok(flinging.fling);
  flinging.feed({ type: 'cancel', t: 210 }); assert.equal(flinging.fling, null); assert.equal(flinging.deadline(), null);
  const two = tab(); acts = run(two, [[0, 'down', 1, 100, 100], [5, 'down', 2, 200, 100], [50, 'cancel']]);
  assert.equal(clicks(acts).length, 0);
  assert.equal(two.busy, false);
  assert.equal(clicks(run(two, [[1000, 'down', 1, 5, 5], [1050, 'up', 1, 5, 5]])).length, 1);
  assert.ok(logs(run(tab(), [[0, 'down'], [10, 'cancel']])).includes('cancel'));
});

test('Zweiter Finger waehrend des Wischens oder Ziehens: Wisch endet ohne Nachlauf, die Taste wird losgelassen; dritter Finger tut nichts', () => {
  const g = tab();
  let acts = run(g, swipe(100, 500, 100, 380, 100).concat([[110, 'down', 2, 200, 380]]), 2000);
  assert.ok(!acts.some((a) => a.fling));
  const d = tab(); acts = run(d, [[0, 'down', 1, 100, 100], [400, 'move', 1, 150, 100], [450, 'down', 2, 200, 100]]);
  assert.ok(acts.some((a) => a.type === 'release'));
  const three = tab(); acts = run(three, [[0, 'down', 1, 1, 1], [5, 'down', 2, 50, 50], [10, 'down', 3, 90, 90], [20, 'up', 3], [30, 'up', 2], [40, 'up', 1]]);
  assert.equal(clicks(acts).length, 0);
  assert.equal(three.busy, false);
});

test('Protokoll: jede Geste endet mit einem Eintrag ohne Inhalte (Art, Fingerzahl, Dauer, Weg)', () => {
  const acts = run(tab(), swipe(100, 500, 100, 300, 200).concat([[216, 'up', 1, 100, 300]]), 4000);
  const l = acts.find((a) => a.type === 'log');
  assert.deepEqual(Object.keys(l).filter((k) => k !== 't').sort(), ['fingers', 'kind', 'ms', 'px', 'type']);
  assert.equal(l.kind, 'swipe'); assert.equal(l.fingers, 1); assert.ok(l.ms >= 200 && l.px >= 190);
});

test('Maus- und Touchpad-Modus schreiben nichts ins Protokoll und kennen weder Wisch noch Nachlauf', () => {
  for (const mode of ['direct', 'pad']) {
    const acts = run(new TouchGestures({ mode }), swipe(100, 500, 100, 300, 150).concat([[160, 'up', 1, 100, 300]]), 3000);
    assert.equal(logs(acts).length, 0);
    assert.ok(!acts.some((a) => a.type === 'scroll'));
  }
});

/* ---------- Zoom-Rechnung ---------- */
const B = { stage: { w: 400, h: 800 }, content: { w: 400, h: 800 }, base: { x: 0, y: 0 } };
const rectOf = (v, b = B) => ({ left: b.base.x + v.x, top: b.base.y + v.y });

test('Zoom: nie unter 1 und nie ueber 4; bei 1 ist die Ansicht die Grundstellung', () => {
  assert.deepEqual(clampView({ z: 0.5, x: 30, y: 30 }, B), VIEW_ONE);
  assert.equal(clampView({ z: 9, x: 0, y: 0 }, B).z, ZOOM_MAX);
  assert.deepEqual(clampView({ z: NaN, x: 5, y: 5 }, B), VIEW_ONE);
});

test('Zoom: das Bild fuellt die Flaeche immer aus (keine leeren Raender)', () => {
  const v = clampView({ z: 2, x: 500, y: -5000 }, B);
  assert.equal(v.x, 0); assert.equal(v.y, 800 - 2 * 800);
  assert.deepEqual(panView({ z: 2, x: 0, y: 0 }, 50, 50, B), { z: 2, x: 0, y: 0 }, 'nach rechts unten ueber den Rand: bleibt');
  assert.deepEqual(panView({ z: 2, x: 0, y: 0 }, -100, -30, B), { z: 2, x: -100, y: -30 });
  assert.equal(panView({ z: 2, x: 0, y: 0 }, -9999, 0, B).x, -400);
});

test('Zoom: der Punkt unter den Fingern bleibt unter den Fingern, auch wenn die Mitte wandert', () => {
  const from = { x: 150, y: 300 }, v0 = VIEW_ONE;
  const before = toVm(from.x, from.y, rectOf(v0), 1, v0.z);
  const v1 = zoomAbout(v0, 2, from, from, B);
  assert.equal(v1.z, 2);
  assert.deepEqual(toVm(from.x, from.y, rectOf(v1), 1, v1.z), before);
  const to = { x: 200, y: 250 }, v2 = zoomAbout(v0, 2.5, from, to, B);
  const p = toVm(to.x, to.y, rectOf(v2), 1, v2.z);
  assert.ok(Math.abs(p.x - before.x) <= 1 && Math.abs(p.y - before.y) <= 1, 'der Bildpunkt wandert mit den Fingern');
  const again = zoomAbout(v1, 1.5, { x: 100, y: 100 }, { x: 100, y: 100 }, B);
  assert.equal(again.z, 3);
  assert.equal(zoomAbout(v1, 10, from, from, B).z, ZOOM_MAX);
  assert.deepEqual(zoomAbout(v1, 0.1, from, from, B), VIEW_ONE, 'zurueckzoomen endet in der Grundstellung');
});

test('Zoom: Tippen im vergroesserten Bild trifft die richtige Stelle der VM (mit Massstab und Grundverschiebung)', () => {
  // Bild 800x1600 VM-Pixel, Massstab 0,5 -> 400x800 Bildflaeche; Taskleiste verschiebt das Bild um 30 nach oben
  const b = { stage: { w: 400, h: 800 }, content: { w: 400, h: 800 }, base: { x: 0, y: -30 } };
  const s = 0.5;
  const v = clampView({ z: 3, x: -200, y: -100 }, b);
  // Vorwaerts: VM-Punkt (300, 700) liegt auf dem Schirm bei base + v + z * s * vm
  const scr = { x: b.base.x + v.x + v.z * s * 300, y: b.base.y + v.y + v.z * s * 700 };
  assert.deepEqual(toVm(scr.x, scr.y, rectOf(v, b), s, v.z), { x: 300, y: 700 });
  assert.deepEqual(toVm(10, 10, { left: 0, top: 0 }, 0.5, 1), { x: 20, y: 20 });
});

test('Scrollschritt: eine Raste entspricht etwa dem gleichen Stueck Bild, ob der Massstab klein oder gross ist; schneller = kleinere Schritte', () => {
  assert.ok(scrollStep('slow', 0.5) > scrollStep('normal', 0.5) && scrollStep('normal', 0.5) > scrollStep('fast', 0.5));
  assert.equal(scrollStep('normal', 0.5), 20);
  assert.equal(scrollStep('normal', 1), 40);
  assert.equal(scrollStep('normal', 0.1), 14, 'nie winzig');
  assert.equal(scrollStep('normal', 5), 90);
  assert.equal(scrollStep('normal'), 34, 'ohne Massstab wie bisher');
  const w = new WheelAccum(20);
  assert.deepEqual(w.add(0, 50), { v: 2, h: 0 });
});
