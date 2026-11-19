import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

/* Pruefung der Farben der Desktop-Oberflaeche (docs/THEME.md): keine festen Farben in den Regeln, beide Farbsaetze vollstaendig, Kontrast nach WCAG AA. */
const WWW = new URL('../../src/JulOS.Frontend/wwwroot/', import.meta.url);
const css = readFileSync(new URL('desktop.css', WWW), 'utf8');
const js = readFileSync(new URL('desktop.js', WWW), 'utf8');

/** Der Inhalt des ersten Blocks mit diesem Selektor (die Tokenbloecke enthalten keine geschachtelten Klammern). */
const block = (selector) => { const i = css.indexOf(selector + ' {'); assert.ok(i >= 0, selector); return css.slice(i + selector.length + 2, css.indexOf('\n}', i)); };
const tokens = (text) => Object.fromEntries([...text.matchAll(/(--[a-z0-9-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
const LIGHT = tokens(block('html.desktop'));
const DARK = tokens(block('html.desktop[data-scheme="dark"]'));
const set = (scheme) => ({ ...LIGHT, ...(scheme === 'dark' ? DARK : {}) });

test('der dunkle Satz ueberschreibt nur Werte, die es im hellen gibt', () => {
  assert.ok(Object.keys(LIGHT).length > 80);
  for (const k of Object.keys(DARK)) assert.ok(k in LIGHT, `${k} fehlt im hellen Satz`);
  assert.ok(Object.keys(DARK).length > 60, 'der dunkle Satz ist zu klein');
});

test('jede benutzte Variable --dk-*, --wp-* und --md-* ist definiert', () => {
  const defined = new Set([...Object.keys(LIGHT), ...[...css.matchAll(/(--md-[a-z-]+|--bg|--panel2?|--line|--text|--muted):/g)].map((m) => m[1])]);
  const used = new Set([...css.matchAll(/var\((--[a-z0-9-]+)/g), ...js.matchAll(/var\((--(?:dk|wp)-[a-z0-9-]+)/g)].map((m) => m[1]));
  const skip = new Set(['--g1', '--g2', '--dk-reserve']);       // je Kachel bzw. von desktop.js gesetzt
  for (const v of used) if (!skip.has(v)) assert.ok(defined.has(v), `${v} ist nicht definiert`);
});

test('keine festen Farben in den Regeln (nur Variablen), ausser den bewusst unveraenderten Stellen', () => {
  const rules = css.slice(css.indexOf('/* Die Dialoge liegen'));
  const bad = [];
  rules.split('\n').forEach((line, i) => {
    if (!/#[0-9a-fA-F]{3,8}\b|rgba?\((?!var\()/.test(line)) return;
    if (/dk-session .*sv-perf|dk-session \.dk-wb \{ background: #000/.test(line)) return;     // Sitzungsfenster: Bild und Statusleiste bleiben unveraendert
    if (/inset 0 1px 0 rgba\(255, 255, 255/.test(line)) return;                                // Glanzkante der farbigen Kacheln
    if (/^\.dk-tile \.dk-tt|var\(--g1/.test(line) && !/#[0-9a-fA-F]{3,8}\b(?!.*var\(--g)/.test(line.replace(/var\(--g[12], #[0-9a-fA-F]{6}\)/g, ''))) return;   // Rueckfallwerte der Kachelverlaeufe
    bad.push(`${i + 1}: ${line.trim().slice(0, 100)}`);
  });
  assert.deepEqual(bad, []);
});

test('Hintergrundbild, Logo und Symbole in desktop.js haben keine festen Farben', () => {
  const scene = js.slice(js.indexOf('const DEFS'), js.indexOf('const LOGO') + 400) + js.slice(js.indexOf('function scene('), js.indexOf('Menues (Menueleiste'));
  assert.doesNotMatch(scene.replace(/stop-opacity|fill-opacity/g, ''), /(?:fill|stroke|stop-color)="#[0-9a-fA-F]{3,8}"/, 'feste Farbe als Attribut');
  assert.doesNotMatch(js.slice(js.indexOf('const P = {'), js.indexOf('const ic =')), /#[0-9a-fA-F]{3,8}|rgb/, 'Symbole zeichnen mit currentColor');
});

// ---- Kontrast ----
const hex = (s) => { const m = /^#([0-9a-f]{6})$/i.exec(s) || /^#([0-9a-f])([0-9a-f])([0-9a-f])$/i.exec(s); if (!m) return null; const h = m.length === 2 ? m[1] : m.slice(1).map((c) => c + c).join(''); return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)).concat(1); };
const rgba = (s) => { const m = /^rgba?\(([^)]+)\)$/.exec(s); if (!m) return hex(s); const p = m[1].split(',').map(Number); return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1]; };
const over = (f, b) => [0, 1, 2].map((i) => f[i] * f[3] + b[i] * (1 - f[3])).concat(1);
const lum = (c) => { const v = c.slice(0, 3).map((x) => { x /= 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; }); return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2]; };
const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };

// Hinter dem Glas liegt das Hintergrundbild; ein Fenster kann ueberall stehen. Geprueft wird gegen die unguenstigsten grossen Flaechen:
// hell der dunkelste Himmel, die Berge und der See; dunkel die hellsten grossen Flaechen (Himmel, Berg, Bluetenzweige).
const WALLS = { light: ['--wp-s1', '--wp-m1', '--wp-w3'], dark: ['--wp-s3', '--wp-m2', '--wp-b3'] };

for (const scheme of ['light', 'dark']) {
  const T = set(scheme);
  const c = (name) => { const v = rgba(T[name]); assert.ok(v, `${name} = ${T[name]}`); return v; };
  /** Flaechen als Funktion des Hintergrunds */
  const surfaceDefs = {
    'Fenster (Glas)': (w) => over(c('--dk-glass'), w),
    'Karte im Fenster': (w) => over(c('--dk-card'), over(c('--dk-glass'), w)),
    'Karte, Maus darueber': (w) => over(c('--dk-card-hi'), over(c('--dk-glass'), w)),
    'Menue': (w) => over(c('--dk-glass-strong'), w),
    'Menueleiste': (w) => over(c('--dk-bar'), w),
    'Dock': (w) => over(c('--dk-dock'), w),
    'Dialog': (w) => over(c('--dk-dlg'), w),
    'Marke (getont)': (w) => over(c('--dk-hover-lo'), over(c('--dk-glass'), w)),
  };
  const worst = (fg, surface) => Math.min(...WALLS[scheme].map((wn) => ratio(fg, surfaceDefs[surface](c(wn)))));
  test(`${scheme}: Text erreicht 4,5:1 auf allen Flaechen`, () => {
    const texts = ['--dk-ink', '--dk-t1', '--dk-t2', '--dk-t3', '--dk-sub', '--dk-hint', '--dk-accent-text', '--dk-accent-strong', '--dk-ok-text', '--dk-bad-text', '--dk-on-surface', '--dk-on-variant'];
    const bad = [];
    for (const t of texts) for (const name of Object.keys(surfaceDefs)) { const r = worst(c(t), name); if (r < 4.5) bad.push(`${t} auf ${name}: ${r.toFixed(2)}`); }
    assert.deepEqual(bad, []);
  });
  test(`${scheme}: Tooltip, Hauptknopf, Material-Paare und Zustandstexte`, () => {
    const pairs = [
      ['--dk-tip-fg', '--dk-tip-bg', 4.5], ['--dk-pri-fg', '--dk-pri-a', 4.5], ['--dk-pri-fg', '--dk-pri-b', 4.5], ['--dk-av-fg', '--dk-av1', 4.5], ['--dk-av-fg', '--dk-av2', 4.5],
      ['--dk-on-primary', '--dk-primary', 4.5], ['--dk-on-primary-container', '--dk-primary-container', 4.5], ['--dk-on-secondary-container', '--dk-secondary-container', 4.5],
      ['--dk-error', '--dk-error-container', 4.5], ['--dk-on-surface', '--dk-surface', 4.5], ['--dk-on-variant', '--dk-surface-container', 4.5], ['--dk-on-variant', '--dk-surface-high', 4.5],
      ['--dk-close-fg', '--dk-close', 3],
    ];
    const bad = [];
    for (const [a, b, min] of pairs) { const r = ratio(c(a), over(c(b), [128, 128, 128, 1])); if (r < min) bad.push(`${a} auf ${b}: ${r.toFixed(2)}`); }
    // Zustandstexte auf ihrem getonten Grund
    const tint = (fgName, bgName) => Math.min(...WALLS[scheme].map((wn) => ratio(c(fgName), over(c(bgName), surfaceDefs['Fenster (Glas)'](c(wn))))));
    for (const [f, g] of [['--dk-ok-text', '--dk-ok-bg'], ['--dk-bad-text', '--dk-bad-bg']]) { const r = tint(f, g); if (r < 4.5) bad.push(`${f} auf ${g}: ${r.toFixed(2)}`); }
    assert.deepEqual(bad, []);
  });
  test(`${scheme}: Raender von Feldern und Zustandspunkte erreichen 3:1`, () => {
    const bad = [];
    for (const name of ['Dialog', 'Fenster (Glas)']) { const r = worst(c('--dk-ctl-edge'), name); if (r < 3) bad.push(`ctl-edge auf ${name}: ${r.toFixed(2)}`); }
    for (const d of ['--dk-st-off', '--dk-st-ok', '--dk-st-busy', '--dk-st-bad']) { const r = worst(c(d), 'Fenster (Glas)'); if (r < 3) bad.push(`${d}: ${r.toFixed(2)}`); }
    assert.deepEqual(bad, []);
  });
}

test('Kachelfarben: das weisse Symbol erreicht auf beiden Enden des Verlaufs 3:1', () => {
  const apps = [...js.matchAll(/g: \['(#[0-9a-f]{6})', '(#[0-9a-f]{6})'\]/gi)];
  assert.ok(apps.length >= 10);
  const bad = [];
  for (const m of apps) for (const col of [m[1], m[2]]) { const r = ratio([255, 255, 255, 1], hex(col)); if (r < 3) bad.push(`${col}: ${r.toFixed(2)}`); }
  assert.deepEqual(bad, []);
});
