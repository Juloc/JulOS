// Tests der Gesten-Zustandsmaschine mit Zeitreihen (reine Logik, keine Optik): node --test tests/frontend
import test from 'node:test';
import assert from 'node:assert/strict';
import { TouchGestures, normalizeTouch, compactTouch, TOUCH_DEFAULTS, HOLD_MIN, HOLD_MAX, WheelAccum, scrollStep, padDelta } from '../../src/JulOS.Frontend/wwwroot/touch-gestures.js';

/** Spielt eine Folge ab: [t, 'down'|'move'|'up'|'tick'|'cancel', id, x, y]; gibt alle Aktionen mit Zeit zurueck. Zwischen den Ereignissen laeuft die Zeit wie im Browser: der Zeitgeber weckt tick() zur Frist. */
function run(g, steps) {
  const out = [];
  let now = 0;
  const advance = (to) => {
    for (let d = g.deadline(); d !== null && d <= to; d = g.deadline()) { now = Math.max(now, d); for (const a of g.tick(d)) out.push({ t: d, ...a }); if (g.deadline() === d) break; }
    now = to;
  };
  for (const [t, type, id = 1, x = 100, y = 100] of steps) {
    advance(t);
    if (type === 'tick') { for (const a of g.tick(t)) out.push({ t, ...a }); continue; }
    for (const a of g.feed({ type, id, x, y, t })) out.push({ t, ...a });
  }
  return out;
}
const types = (acts) => acts.map((a) => a.type + (a.button ? ':' + a.button : '') + (a.phase ? ':' + a.phase : ''));
const clicks = (acts) => acts.filter((a) => a.type === 'click');

test('Einstellungen: Standard, Grenzen der Haltedauer, Unbekanntes faellt weg, nur Abweichungen gehen zum Server', () => {
  assert.deepEqual(normalizeTouch(undefined), TOUCH_DEFAULTS);
  assert.equal(TOUCH_DEFAULTS.mode, 'tablet', 'am Handy ist der Tablet-Modus die Vorgabe');
  assert.equal(normalizeTouch({ holdMs: 10 }).holdMs, HOLD_MIN);
  assert.equal(normalizeTouch({ holdMs: 99999 }).holdMs, HOLD_MAX);
  assert.equal(normalizeTouch({ holdMs: 612 }).holdMs, 600);
  assert.equal(normalizeTouch({ holdMs: 'x' }).holdMs, 500);
  assert.equal(normalizeTouch({ mode: 'quatsch', rightClick: 'two' }).mode, 'tablet');
  assert.equal(normalizeTouch({ rightClick: 'two' }).rightClick, 'two');
  assert.equal(normalizeTouch({ padSpeed: 99 }).padSpeed, 3);
  assert.equal(normalizeTouch({ evil: 1 }).evil, undefined);
  assert.deepEqual(compactTouch({}), {});
  assert.deepEqual(compactTouch({ mode: 'pad', holdMs: 500 }), { mode: 'pad' });
});

test('Tippen: Linksklick an der Stelle (direkt), erst beim Heben; vorher der Zeiger an die Stelle', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const acts = run(g, [[0, 'down', 1, 50, 60], [90, 'up', 1, 51, 61]]);
  assert.deepEqual(types(acts), ['hover', 'click:left']);
  assert.deepEqual([acts[1].x, acts[1].y, acts[1].where, acts[1].how], [50, 60, 'finger', 'tap']);
  assert.ok(!acts.some((a) => a.type === 'ring'), 'ein kurzes Tippen zeigt keinen Ring');
});

test('Tippen im Touchpad-Modus: Klick am Zeiger', () => {
  const g = new TouchGestures({ mode: 'pad' });
  const acts = run(g, [[0, 'down'], [80, 'up']]);
  assert.deepEqual(types(acts), ['click:left']);
  assert.equal(acts[0].where, 'pointer');
});

test('Halten: Ring erscheint nach kurzer Zeit, fuellt sich ueber die Haltedauer, dann Rechtsklick und Rueckmeldung; Heben danach klickt nicht mehr', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const acts = run(g, [[0, 'down', 1, 40, 40], [700, 'up', 1, 40, 40]]);
  assert.deepEqual(types(acts), ['hover', 'ring:start', 'ring:full', 'click:right']);
  const ring = acts.find((a) => a.type === 'ring' && a.phase === 'start');
  assert.equal(ring.t, 140);
  assert.equal(ring.ms, 360, 'die Fuellung dauert bis zur vollen Haltedauer (500 - 140)');
  assert.equal(acts.find((a) => a.type === 'click').t, 500);
  assert.equal(acts.find((a) => a.type === 'click').how, 'hold');
});

test('Halten: einstellbare Dauer 300 bis 1000 ms', () => {
  for (const holdMs of [300, 750, 1000]) {
    const g = new TouchGestures({ mode: 'direct', holdMs });
    const acts = run(g, [[0, 'down'], [holdMs + 100, 'up']]);
    assert.equal(acts.find((a) => a.type === 'click').t, holdMs, 'bei ' + holdMs);
  }
  const g = new TouchGestures({ mode: 'direct', holdMs: 800 });
  const acts = run(g, [[0, 'down'], [600, 'up']]);                      // kuerzer als die Haltedauer: Tippen
  assert.deepEqual(clicks(acts).map((a) => a.button + a.how), ['lefttap']);
});

test('Halten mit kleiner Bewegung (unter der Schwelle) gilt noch; ueber der Schwelle bricht der Ring ab und es wird gezogen (direkt)', () => {
  let g = new TouchGestures({ mode: 'direct' });
  let acts = run(g, [[0, 'down', 1, 100, 100], [200, 'move', 1, 106, 104], [700, 'up', 1, 106, 104]]);
  assert.equal(clicks(acts)[0].button, 'right');
  assert.equal(clicks(acts)[0].x, 100, 'der Rechtsklick sitzt an der Stelle, an der der Finger aufsetzte');

  g = new TouchGestures({ mode: 'direct' });
  acts = run(g, [[0, 'down', 1, 100, 100], [300, 'move', 1, 130, 100], [320, 'move', 1, 160, 100], [400, 'up', 1, 160, 100]]);
  assert.deepEqual(types(acts), ['hover', 'ring:start', 'ring:cancel', 'press:left', 'mark', 'move', 'mark', 'move', 'mark', 'release:left', 'mark']);
  assert.equal(clicks(acts).length, 0, 'kein Rechtsklick, kein Linksklick');
  assert.deepEqual([acts.find((a) => a.type === 'press').x, acts.find((a) => a.type === 'release').x], [100, 160]);
});

test('Bewegung vor dem Ring: kein Ring, keine Klicks, direkt wird gezogen; mit ausgeschaltetem Fingerziehen passiert nichts', () => {
  let g = new TouchGestures({ mode: 'direct' });
  let acts = run(g, [[0, 'down', 1, 10, 10], [50, 'move', 1, 60, 10], [120, 'up', 1, 60, 10]]);
  assert.ok(!acts.some((a) => a.type === 'ring') && !clicks(acts).length);
  assert.ok(acts.some((a) => a.type === 'press'));
  g = new TouchGestures({ mode: 'direct', fingerDrag: false });
  acts = run(g, [[0, 'down', 1, 10, 10], [50, 'move', 1, 60, 10], [120, 'up', 1, 60, 10]]);
  assert.deepEqual(types(acts), ['hover']);
});

test('Rechtsklick-Methode: nur Halten, nur Zwei-Finger-Tipp, beides', () => {
  // nur Zwei-Finger-Tipp: Halten gibt keinen Ring und keinen Rechtsklick, das Heben ist ein Linksklick
  let g = new TouchGestures({ mode: 'direct', rightClick: 'two' });
  let acts = run(g, [[0, 'down'], [900, 'up']]);
  assert.deepEqual(types(acts), ['hover', 'click:left']);
  acts = run(g, [[1000, 'down', 1, 50, 50], [1000, 'down', 2, 90, 50], [1100, 'up', 1, 50, 50], [1110, 'up', 2, 90, 50]]);
  assert.deepEqual(clicks(acts).map((a) => a.button), ['right']);
  // nur Halten: der Zwei-Finger-Tipp macht nichts
  g = new TouchGestures({ mode: 'direct', rightClick: 'hold' });
  acts = run(g, [[0, 'down', 1, 50, 50], [0, 'down', 2, 90, 50], [100, 'up', 1, 50, 50], [110, 'up', 2, 90, 50]]);
  assert.equal(clicks(acts).length, 0);
  acts = run(g, [[1000, 'down'], [1700, 'up']]);
  assert.equal(clicks(acts)[0].button, 'right');
});

test('Zwei-Finger-Tipp: Rechtsklick in der Mitte der Finger (direkt) bzw. am Zeiger (pad)', () => {
  let g = new TouchGestures({ mode: 'direct' });
  let acts = run(g, [[0, 'down', 1, 40, 100], [10, 'down', 2, 80, 100], [120, 'up', 1, 40, 100], [130, 'up', 2, 80, 100]]);
  assert.deepEqual(clicks(acts).map((a) => [a.button, a.x, a.y, a.where]), [['right', 60, 100, 'finger']]);
  assert.equal(clicks(acts).length, 1, 'ein Klick, nicht zwei');
  g = new TouchGestures({ mode: 'pad' });
  acts = run(g, [[0, 'down', 1, 40, 100], [10, 'down', 2, 80, 100], [120, 'up', 1], [130, 'up', 2]]);
  assert.equal(clicks(acts)[0].where, 'pointer');
});

test('Zwei Finger zu lange aufgelegt ohne Bewegung: kein Rechtsklick', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const acts = run(g, [[0, 'down', 1, 40, 100], [10, 'down', 2, 80, 100], [900, 'up', 1], [910, 'up', 2]]);
  assert.equal(clicks(acts).length, 0);
});

test('Zwei Finger mit Hand-Zittern (kleine Bewegung): weder Scrollen noch Zoom, aber der Tipp zaehlt', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const acts = run(g, [[0, 'down', 1, 40, 100], [5, 'down', 2, 80, 100], [50, 'move', 1, 43, 103], [60, 'move', 2, 84, 104], [150, 'up', 1], [160, 'up', 2]]);
  assert.ok(!acts.some((a) => a.type === 'scroll' || a.type === 'pinch'));
  assert.equal(clicks(acts).length, 1);
});

test('Scrollen: beide Finger gleichzeitig nach oben (natuerlich: Inhalt folgt dem Finger, das Rad dreht nach unten), kein Zoom', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const steps = [[0, 'down', 1, 100, 400], [5, 'down', 2, 160, 400]];
  for (let i = 1; i <= 10; i++) steps.push([5 + i * 16, 'move', 1, 100 + i * 0.5, 400 - i * 12], [5 + i * 16, 'move', 2, 160 + i * 0.5, 400 - i * 12]);
  steps.push([300, 'up', 1], [310, 'up', 2]);
  const acts = run(g, steps);
  const scrolls = acts.filter((a) => a.type === 'scroll');
  assert.ok(scrolls.length > 3);
  assert.ok(!acts.some((a) => a.type === 'pinch'), 'Scrollen ist kein Zoom');
  assert.ok(!clicks(acts).length, 'Scrollen ist kein Zwei-Finger-Tipp');
  const total = scrolls.reduce((n, a) => n + a.dy, 0);
  assert.ok(total > 100 && total <= 120 + 1, 'Rad nach unten, der ganze Weg kommt an: ' + total);
  assert.ok(scrolls.every((a) => a.dx === 0), 'leichtes Abdriften scrollt nicht waagerecht');
});

test('Scrollrichtung "klassisch" dreht das Vorzeichen um; waagerechtes Scrollen kommt an', () => {
  const mk = (cfg) => {
    const g = new TouchGestures(cfg), steps = [[0, 'down', 1, 100, 300], [5, 'down', 2, 160, 300]];
    for (let i = 1; i <= 8; i++) steps.push([5 + i * 16, 'move', 1, 100 - i * 14, 300], [5 + i * 16, 'move', 2, 160 - i * 14, 300]);
    return run(g, steps).filter((a) => a.type === 'scroll');
  };
  const nat = mk({}), cls = mk({ scrollDir: 'classic' });
  assert.ok(nat.length && cls.length);
  assert.ok(nat.every((a) => a.dx > 0 && a.dy === 0), 'Finger nach links: natuerlich scrollt nach rechts');
  assert.ok(cls.every((a) => a.dx < 0));
  assert.ok(nat.every((a) => a.fdx < 0), 'das Rohmass des Fingers bleibt dabei (fuer das Verschieben des Bildes)');
});

test('Pinch erst bei klarer Abstandsaenderung; aus dem Pinch wird kein Scrollen, und am Ende kommt das Verhaeltnis', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const steps = [[0, 'down', 1, 100, 300], [5, 'down', 2, 200, 300]];        // Abstand 100
  for (let i = 1; i <= 8; i++) steps.push([5 + i * 16, 'move', 1, 100 - i * 8, 300], [5 + i * 16, 'move', 2, 200 + i * 8, 300]);   // Abstand waechst auf 228
  steps.push([200, 'up', 1, 36, 300], [210, 'up', 2, 264, 300]);
  const acts = run(g, steps);
  const pinches = acts.filter((a) => a.type === 'pinch');
  assert.ok(pinches.length >= 4);
  assert.ok(pinches[0].ratio > 1.3 && pinches[0].ratio < 1.6, 'der erste Pinch kommt erst ab 32 px mehr Abstand: ' + pinches[0].ratio);
  assert.ok(!acts.some((a) => a.type === 'scroll'));
  const end = acts.find((a) => a.type === 'pinchEnd');
  assert.ok(end && Math.abs(end.ratio - 2.28) < 0.01, 'Abschluss mit dem letzten Verhaeltnis');
  assert.equal(clicks(acts).length, 0);
});

test('Pinch mit nur einem bewegten Finger zaehlt als Pinch, Scrollen mit leicht wechselndem Abstand als Scrollen', () => {
  let g = new TouchGestures({ mode: 'direct' });
  let acts = run(g, [[0, 'down', 1, 100, 300], [5, 'down', 2, 200, 300], [30, 'move', 1, 90, 300], [50, 'move', 1, 70, 300], [70, 'move', 1, 50, 300]]);
  assert.ok(acts.some((a) => a.type === 'pinch') && !acts.some((a) => a.type === 'scroll'));
  g = new TouchGestures({ mode: 'direct' });
  const steps = [[0, 'down', 1, 100, 300], [5, 'down', 2, 200, 300]];
  for (let i = 1; i <= 8; i++) steps.push([5 + i * 16, 'move', 1, 100, 300 - i * 14], [5 + i * 16, 'move', 2, 200 + i * 2.5, 300 - i * 14]);   // Abstand waechst nur um 20
  acts = run(g, steps);
  assert.ok(acts.some((a) => a.type === 'scroll') && !acts.some((a) => a.type === 'pinch'));
});

test('einmal als Scrollen entschieden, bleibt es dabei (kein Umschlagen in Zoom)', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const steps = [[0, 'down', 1, 100, 300], [5, 'down', 2, 160, 300]];
  for (let i = 1; i <= 4; i++) steps.push([5 + i * 16, 'move', 1, 100, 300 - i * 20], [5 + i * 16, 'move', 2, 160, 300 - i * 20]);
  for (let i = 1; i <= 8; i++) steps.push([100 + i * 16, 'move', 1, 100 - i * 20, 220], [100 + i * 16, 'move', 2, 160 + i * 20, 220]);   // jetzt spreizen
  const acts = run(g, steps);
  assert.ok(acts.some((a) => a.type === 'scroll'));
  assert.ok(!acts.some((a) => a.type === 'pinch'));
});

test('Doppeltipp mit zwei Fingern setzt den Zoom zurueck, nur wenn gezoomt ist; der erste Rechtsklick wartet dann und entfaellt', () => {
  const tap = (t0) => [[t0, 'down', 1, 40, 100], [t0 + 5, 'down', 2, 80, 100], [t0 + 100, 'up', 1], [t0 + 110, 'up', 2]];
  let g = new TouchGestures({ mode: 'direct' });
  g.setOptions({ zoomed: true });
  let acts = run(g, [...tap(0), ...tap(250)]);
  assert.deepEqual(types(acts).filter((x) => x !== 'hover'), ['zoomReset']);
  assert.equal(g.deadline(), null);
  // nicht gezoomt: zwei Rechtsklicks, nichts wird aufgeschoben
  g = new TouchGestures({ mode: 'direct' });
  acts = run(g, [...tap(0), ...tap(250)]);
  assert.deepEqual(clicks(acts).map((a) => a.t), [100, 350]);
  // gezoomt, aber nur ein Tipp: der Rechtsklick kommt nach dem Warten
  g = new TouchGestures({ mode: 'direct' }); g.setOptions({ zoomed: true });
  acts = run(g, [...tap(0), [1000, 'tick']]);
  assert.deepEqual(clicks(acts).map((a) => a.t), [480]);
  // zwei Tipps, zu weit auseinander: beide ergeben Rechtsklicks
  g = new TouchGestures({ mode: 'direct' }); g.setOptions({ zoomed: true });
  acts = run(g, [...tap(0), ...tap(900), [2000, 'tick']]);
  assert.equal(clicks(acts).length, 2);
});

test('Doppeltipp und halten: die linke Taste bleibt gedrueckt, bis der Finger hebt; sichtbare Markierung', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const acts = run(g, [[0, 'down', 1, 100, 100], [60, 'up', 1, 100, 100], [200, 'down', 1, 104, 98], [300, 'move', 1, 150, 120], [350, 'move', 1, 220, 160], [900, 'move', 1, 230, 170], [1000, 'up', 1, 230, 170]]);
  assert.deepEqual(types(acts).filter((x) => !x.startsWith('mark')), ['hover', 'click:left', 'hover', 'press:left', 'move', 'move', 'move', 'release:left']);
  const press = acts.find((a) => a.type === 'press'), release = acts.find((a) => a.type === 'release');
  assert.deepEqual([press.t, press.how, release.t, release.x, release.y], [200, 'double', 1000, 230, 170]);
  assert.deepEqual(acts.filter((a) => a.type === 'mark').map((a) => a.on), [true, true, true, true, false]);
  assert.ok(!acts.some((a) => a.type === 'ring'), 'beim Ziehen gibt es keinen Halte-Ring');
  assert.equal(clicks(acts).length, 1, 'nur das erste Tippen ist ein Klick');
});

test('Doppeltipp zu langsam oder zu weit weg: zwei einzelne Klicks', () => {
  let g = new TouchGestures({ mode: 'direct' });
  let acts = run(g, [[0, 'down'], [50, 'up'], [600, 'down'], [650, 'up']]);
  assert.equal(clicks(acts).length, 2);
  assert.ok(!acts.some((a) => a.type === 'press'));
  g = new TouchGestures({ mode: 'direct' });
  acts = run(g, [[0, 'down', 1, 100, 100], [50, 'up', 1, 100, 100], [200, 'down', 1, 300, 100], [250, 'up', 1, 300, 100]]);
  assert.equal(clicks(acts).length, 2);
  assert.ok(!acts.some((a) => a.type === 'press'));
});

test('Doppeltipp ohne Bewegung: Taste gedrueckt und losgelassen am selben Ort (der zweite Klick eines Doppelklicks)', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const acts = run(g, [[0, 'down', 1, 100, 100], [50, 'up', 1, 100, 100], [150, 'down', 1, 100, 100], [200, 'up', 1, 100, 100]]);
  const press = acts.find((a) => a.type === 'press'), release = acts.find((a) => a.type === 'release');
  assert.ok(press && release && press.x === release.x && press.y === release.y);
});

test('Touchpad: ein Finger bewegt den Zeiger relativ (mit Beschleunigung), Tippen danach klickt nicht doppelt', () => {
  const g = new TouchGestures({ mode: 'pad', padSpeed: 1 });
  const acts = run(g, [[0, 'down', 1, 100, 100], [16, 'move', 1, 110, 100], [32, 'move', 1, 130, 100], [48, 'move', 1, 160, 100], [200, 'up', 1, 160, 100]]);
  const moves = acts.filter((a) => a.type === 'move');
  assert.ok(moves.length === 2 && moves.every((m) => m.where === 'pointer'));
  assert.ok(moves[0].dx >= 30, 'der erste Schritt enthaelt den Weg ueber die Schwelle (30 px)');
  assert.ok(moves[1].dx > 30, 'schnellere Bewegung ist beschleunigt (30 px in 16 ms)');
  assert.equal(clicks(acts).length, 0);
  assert.ok(!acts.some((a) => a.type === 'press'));
});

test('Touchpad: Tippen und ziehen wie am Laptop; ausgeschaltet wird daraus Tippen und Zeiger bewegen', () => {
  let g = new TouchGestures({ mode: 'pad' });
  let acts = run(g, [[0, 'down', 1, 100, 100], [50, 'up', 1, 100, 100], [200, 'down', 1, 300, 300], [250, 'move', 1, 330, 310], [300, 'move', 1, 360, 320], [600, 'up', 1, 360, 320]]);
  assert.deepEqual(types(acts).filter((x) => !x.startsWith('mark')), ['click:left', 'press:left', 'move', 'move', 'release:left']);
  assert.equal(acts.find((a) => a.type === 'press').where, 'pointer');
  g = new TouchGestures({ mode: 'pad', tapDrag: false });
  acts = run(g, [[0, 'down'], [50, 'up'], [200, 'down', 1, 300, 300], [250, 'move', 1, 330, 310], [600, 'up', 1, 330, 310]]);
  assert.ok(!acts.some((a) => a.type === 'press'));
});

test('Touchpad: Halten gibt Rechtsklick am Zeiger', () => {
  const g = new TouchGestures({ mode: 'pad' });
  const acts = run(g, [[0, 'down'], [800, 'up']]);
  assert.deepEqual(clicks(acts).map((a) => [a.button, a.where]), [['right', 'pointer']]);
});

test('zweiter Finger waehrend des Ziehens: Taste wird losgelassen, danach Scrollen; dritter Finger bricht alles ab', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const acts = run(g, [[0, 'down', 1, 100, 100], [30, 'move', 1, 140, 100], [60, 'down', 2, 200, 100]]);
  assert.deepEqual(types(acts).slice(-4), ['move', 'mark', 'release:left', 'mark']);
  assert.equal(g.pressed, false);
  const more = run(g, [[100, 'down', 3, 250, 100]]);
  assert.deepEqual(clicks(more), []);
  run(g, [[200, 'up', 1], [210, 'up', 2], [220, 'up', 3]]);
  assert.equal(g.busy, false);
});

test('touchcancel: gehaltene Taste und Ring werden aufgeraeumt', () => {
  const g = new TouchGestures({ mode: 'direct' });
  let acts = run(g, [[0, 'down'], [200, 'tick'], [300, 'cancel']]);
  assert.deepEqual(types(acts).slice(-1), ['ring:cancel']);
  acts = run(g, [[1000, 'down', 1, 10, 10], [1020, 'move', 1, 80, 10], [1040, 'cancel']]);
  assert.deepEqual(types(acts).slice(-2), ['release:left', 'mark']);
  assert.equal(g.busy, false);
});

test('nach dem Rechtsklick durch Halten: weitere Bewegung tut nichts, danach geht alles wieder', () => {
  const g = new TouchGestures({ mode: 'direct' });
  const acts = run(g, [[0, 'down', 1, 100, 100], [600, 'move', 1, 180, 100], [650, 'up', 1, 180, 100], [1000, 'down', 1, 50, 50], [1050, 'up', 1, 50, 50]]);
  assert.deepEqual(clicks(acts).map((a) => a.button + a.how), ['righthold', 'lefttap']);
  assert.ok(!acts.some((a) => a.type === 'press'));
});

test('Mausrad-Sammler: ganze Rasten, der Rest bleibt; Schrittweite je Geschwindigkeit', () => {
  const w = new WheelAccum(34);
  assert.deepEqual(w.add(0, 20), { v: 0, h: 0 });
  assert.deepEqual(w.add(0, 20), { v: 1, h: 0 });
  assert.deepEqual(w.add(0, -80), { v: -2, h: 0 });
  assert.deepEqual(w.add(70, 0), { v: 0, h: 2 });
  assert.ok(scrollStep('slow') > scrollStep('normal') && scrollStep('normal') > scrollStep('fast'));
  assert.equal(scrollStep('quatsch'), 34);
});

test('Zeigerbeschleunigung: maessig, langsam = Grundfaktor, schnell hoechstens 1,45 fach', () => {
  const slow = padDelta(10, 0, 1000, 1.4);
  assert.ok(Math.abs(slow.dx - 14) < 0.1 && slow.dy === 0);
  const fast = padDelta(100, 0, 10, 1);
  assert.ok(fast.dx > 100 && fast.dx <= 145.0001);
  assert.deepEqual(padDelta(0, 0, 0, 1), { dx: 0, dy: 0 });
});
