/* =====================================================================
 * Aussehen (docs/APPEARANCE.md): Akzentfarbe, Hintergrund, Dock, Symbole. Die Helligkeit (Hell/Dunkel/System) bleibt in scheme.js.
 * Gespeichert wird die Einstellung "appearance" je Benutzer auf dem Server (prefs.js, SettingsRules.cs) und lokal als Rueckfall; gespeichert wird nur,
 * was vom Standard abweicht ("Zuruecksetzen" ist also: das Feld fehlt).
 * Hier steht die reine Logik (normalisieren, Akzentfarbe mit gepruefter Kontrastlage, Hintergruende, Variablen) und ein kleines Anwenden auf das Dokument,
 * das nur beim Aufruf etwas anfasst. Kein DOM beim Laden, damit es sich mit node pruefen laesst (tests/frontend/appearance.test.mjs).
 * ===================================================================== */
import { tk } from './i18n.js';
import { cleanOverride } from './icons.js';

export const MAX_OVERRIDES = 200;           // wie auf dem Server (SettingsRules.cs)
export const AREAS = ['accent', 'bg', 'dock', 'icons'];

export const DOCK_MODES = ['auto', 'always', 'toggle'];
export const SIZES = ['s', 'm', 'l'];
export const ICON_STYLES = ['outline', 'filled'];
export const SIZE_LABEL = { s: tk('set.sizeS'), m: tk('set.sizeM'), l: tk('set.sizeL') };
export const STYLE_LABEL = { outline: tk('set.styleOutline'), filled: tk('set.styleFilled') };
const ICON_SCALE = { s: 0.85, m: 1, l: 1.2 }, DOCK_SCALE = { s: 0.8, m: 1, l: 1.2 };
/** Platz, den ein sichtbarer Dock unten braucht (Fenster halten ihn frei): 96 px in der mittleren Groesse */
export const dockHeight = (size) => Math.round(96 * (DOCK_SCALE[size] || 1));

/** Feste Akzentfarben (Name -> Grundton). Dazu kommt eine eigene Farbe. Die Farben der Oberflaeche werden daraus berechnet (accentVars). */
export const ACCENTS = [['violet', '#6a58e6'], ['blue', '#2f6fe0'], ['teal', '#0f8f8a'], ['green', '#2e8b57'], ['amber', '#b8700c'], ['rose', '#d6457f'], ['red', '#d13438'], ['slate', '#5b6a8a']];
export const ACCENT_LABEL = { violet: tk('col.violet'), blue: tk('col.blue'), teal: tk('col.teal'), green: tk('col.green'), amber: tk('col.amber'), rose: tk('col.rose'), red: tk('col.red'), slate: tk('col.slate') };

/**
 * Hintergruende. 'scene' ist das gemalte Bild (desktop.js, Farben in desktop.css); die anderen sind Verlaeufe und Muster aus je drei Farben
 * (oben, Mitte, unten) fuer hell und dunkel. Die Farben sind gegen die Textstufen geprueft (tests/frontend/theme-css.test.mjs).
 * pattern: 'dots' | 'grid' legt ein feines Muster darueber (im Hellen weiss, im Dunklen schwarz, es macht den Grund also nie ungueenstiger).
 */
export const WALLPAPERS = [
  { id: 'scene', label: tk('bg.scene') },
  { id: 'dusk', label: tk('bg.dusk'), light: ['#b9b6ec', '#ecc6e0', '#d9dcf6'], dark: ['#0f0e24', '#2f2250', '#3b2d66'] },
  { id: 'ocean', label: tk('bg.ocean'), light: ['#a9c8ee', '#bfe0ee', '#d6eef0'], dark: ['#0a1830', '#12304f', '#1b4a63'] },
  { id: 'forest', label: tk('bg.forest'), light: ['#b4d3b8', '#cde5c8', '#e1efd6'], dark: ['#0b1d14', '#143526', '#1d4a36'] },
  { id: 'sand', label: tk('bg.sand'), light: ['#e6c9a8', '#f0dcc0', '#f6ead6'], dark: ['#231710', '#3b2818', '#4f3822'] },
  { id: 'graphite', label: tk('bg.graphite'), light: ['#b8bccb', '#cfd2de', '#e2e4ec'], dark: ['#0d0e14', '#1a1c27', '#272a38'] },
  { id: 'dots', label: tk('bg.dots'), light: ['#c3c0ee', '#d6d2f2', '#e4e2f8'], dark: ['#12102a', '#1d1a40', '#2a2556'], pattern: 'dots' },
  { id: 'grid', label: tk('bg.grid'), light: ['#b8cdea', '#cadaf0', '#dce7f6'], dark: ['#0c1424', '#14223a', '#1d3050'], pattern: 'grid' },
];
const WALL_IDS = WALLPAPERS.map((w) => w.id);

export const DEFAULTS = Object.freeze({ accent: null, bg: 'scene', dock: { mode: 'auto', size: 'm' }, icons: { size: 'm', style: 'outline', over: {} } });
const pick = (v, list, d) => (list.includes(v) ? v : d);
const HEX = /^#[0-9a-f]{6}$/i;
const CONN_ID = /^[0-9A-Za-z_-]{1,64}$/;

/** Macht aus irgendeinem Wert (Server, alte lokale Kopie) eine vollstaendige, gueltige Einstellung. Unbekanntes faellt weg. */
export function normalizeAppearance(raw) {
  const r = raw && typeof raw === 'object' ? raw : {};
  const dock = r.dock && typeof r.dock === 'object' ? r.dock : {}, icons = r.icons && typeof r.icons === 'object' ? r.icons : {};
  const over = {};
  if (icons.over && typeof icons.over === 'object') {
    for (const [id, v] of Object.entries(icons.over)) {
      if (Object.keys(over).length >= MAX_OVERRIDES) break;
      const o = CONN_ID.test(id) ? cleanOverride(v) : null;
      if (o) over[id] = o;
    }
  }
  return {
    accent: typeof r.accent === 'string' && HEX.test(r.accent) ? r.accent.toLowerCase() : null,
    bg: pick(r.bg, WALL_IDS, DEFAULTS.bg),
    dock: { mode: pick(dock.mode, DOCK_MODES, 'auto'), size: pick(dock.size, SIZES, 'm') },
    icons: { size: pick(icons.size, SIZES, 'm'), style: pick(icons.style, ICON_STYLES, 'outline'), over },
  };
}

/** Nur das, was vom Standard abweicht (so wird es gespeichert); null, wenn nichts abweicht */
export function compactAppearance(a) {
  const n = normalizeAppearance(a), out = {};
  if (n.accent) out.accent = n.accent;
  if (n.bg !== DEFAULTS.bg) out.bg = n.bg;
  const dock = {}; if (n.dock.mode !== 'auto') dock.mode = n.dock.mode; if (n.dock.size !== 'm') dock.size = n.dock.size;
  if (Object.keys(dock).length) out.dock = dock;
  const icons = {}; if (n.icons.size !== 'm') icons.size = n.icons.size; if (n.icons.style !== 'outline') icons.style = n.icons.style; if (Object.keys(n.icons.over).length) icons.over = n.icons.over;
  if (Object.keys(icons).length) out.icons = icons;
  return Object.keys(out).length ? out : null;
}

/** Ein Bereich zurueck auf den Standard ('all' = alles). Die Symbole je Verbindung (icons.over) gehoeren zu "Symbole": sie werden nur mit 'iconsOver' oder 'all' geleert. */
export function resetArea(a, area) {
  const n = normalizeAppearance(a);
  if (area === 'all') return normalizeAppearance(null);
  if (area === 'accent') n.accent = null;
  else if (area === 'bg') n.bg = DEFAULTS.bg;
  else if (area === 'dock') n.dock = { ...DEFAULTS.dock };
  else if (area === 'icons') n.icons = { ...DEFAULTS.icons, over: n.icons.over };
  else if (area === 'iconsOver') n.icons = { ...n.icons, over: {} };
  return n;
}
export const isDefaultArea = (a, area) => JSON.stringify(resetArea(a, area)) === JSON.stringify(normalizeAppearance(a));

/** Eine Ueberschreibung fuer eine Verbindung setzen (override: { k?, c? }) oder mit null entfernen ("automatisch") */
export function withOverride(a, connId, override) {
  const n = normalizeAppearance(a), o = cleanOverride(override), over = { ...n.icons.over };
  if (o && CONN_ID.test(connId)) over[connId] = o; else delete over[connId];
  return normalizeAppearance({ ...n, icons: { ...n.icons, over } });
}

/* ---------- Farben ---------- */
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
export function hexToHsl(hex) {
  const m = /^#([0-9a-f]{6})$/i.exec(hex); if (!m) return null;
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(m[1].slice(i, i + 2), 16) / 255);
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2, d = max - min;
  if (!d) return { h: 0, s: 0, l };
  const s = d / (1 - Math.abs(2 * l - 1));
  const h = max === r ? ((g - b) / d + (g < b ? 6 : 0)) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return { h: h * 60, s, l };
}
export function hslToHex(h, s, l) {
  const k = (n) => (n + h / 30) % 12, a = s * Math.min(l, 1 - l), f = (n) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return '#' + [f(0), f(8), f(4)].map((x) => Math.round(clamp(x, 0, 1) * 255).toString(16).padStart(2, '0')).join('');
}
const rgb = (hex) => [0, 2, 4].map((i) => parseInt(hex.slice(1 + i, 3 + i), 16));
const lum = (hex) => { const v = rgb(hex).map((x) => { x /= 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; }); return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2]; };
/** Kontrastverhaeltnis zweier #rrggbb-Farben (WCAG) */
export function contrast(a, b) { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); }
/** Dieselbe Farbe so lange heller (dir 1) oder dunkler (dir -1) machen, bis sie gegen alle Gruende in bgs min erreicht (oder nichts mehr geht) */
function fit(h, s, l, bgs, min, dir) {
  let x = clamp(l, 0.04, 0.96);
  for (let i = 0; i < 100; i++) { const hex = hslToHex(h, s, x); if (bgs.every((b) => contrast(hex, b) >= min)) return hex; x = clamp(x + dir * 0.01, 0.02, 0.98); }
  return hslToHex(h, s, x);
}

// Die ungunstigsten Flaechen, auf denen Akzenttext steht: hell Glas ueber dem dunkelsten Hintergrund, dunkel Glas ueber dem hellsten (aus theme-css.test.mjs, mit Luft)
const SURF = { light: ['#ffffff', '#dcdcf0'], dark: ['#15141f', '#2c2a44'] };
const WHITE = '#ffffff', INK_DARK = '#14123a';
/** Alle Variablen, die der Akzent setzt (zum Entfernen beim Zuruecksetzen) */
export const ACCENT_VARS = ['--dk-accent', '--dk-accent2', '--dk-accent-text', '--dk-accent-strong', '--dk-accent-ring', '--dk-pri-a', '--dk-pri-b', '--dk-pri-fg', '--dk-pri-shw',
  '--dk-hover', '--dk-hover-lo', '--dk-sel', '--dk-soft', '--dk-primary', '--dk-on-primary', '--dk-primary-container', '--dk-on-primary-container', '--dk-pill-act', '--dk-snap-fill', '--dk-snap-edge'];

/**
 * Die Farbvariablen der Oberflaeche fuer eine Akzentfarbe, mit gesicherter Kontrastlage: Text 4,5:1 auf den ungunstigsten Flaechen (Text, Hauptknopf,
 * Material-Paare), Symbole und Linien 3:1. Der Farbton bleibt, Helligkeit und Saettigung werden angepasst.
 * @returns {Record<string,string>}
 */
export function accentVars(hex, dark) {
  const c = hexToHsl(hex); if (!c) return {};
  const h = c.h, s = clamp(c.s, 0.25, 0.85), bgs = SURF[dark ? 'dark' : 'light'], dir = dark ? 1 : -1;
  const a = fit(h, s, dark ? 0.72 : 0.5, bgs, 3, dir);                         // Linien, Ringe, Symbole: 3:1
  const text = fit(h, s, dark ? 0.8 : 0.4, bgs, 4.5, dir);                      // Text auf Glas: 4,5:1
  const strong = fit(h, s, dark ? 0.88 : 0.3, [...bgs, ...(dark ? [] : ['#d4d4ee'])], 6.5, dir);   // auf getontem Grund (Auswahl): mehr Luft
  const fg = dark ? INK_DARK : WHITE;
  // Hauptknopf: Schrift 4,5:1 auf beiden Enden des Verlaufs
  const pa = fit(h, s, dark ? 0.72 : 0.5, [fg], 4.5, dark ? 1 : -1), pb = fit((h + 12) % 360, s, dark ? 0.66 : 0.44, [fg], 4.5, dark ? 1 : -1);
  const [r, g, b] = rgb(a);
  const tint = (al) => `rgba(${r}, ${g}, ${b}, ${al})`;
  const prim = pa, onPrim = fg;
  const cont = hslToHex(h, clamp(s, 0.3, 0.7), dark ? 0.22 : 0.91), onCont = fit(h, clamp(s, 0.3, 0.7), dark ? 0.9 : 0.2, [cont], 7, dark ? 1 : -1);
  const [pr, pg, pbb] = rgb(pa);
  return {
    '--dk-accent': a, '--dk-accent2': pb, '--dk-accent-text': text, '--dk-accent-strong': strong, '--dk-accent-ring': tint(dark ? 0.65 : 0.6),
    '--dk-pri-a': pa, '--dk-pri-b': pb, '--dk-pri-fg': fg, '--dk-pri-shw': `${pr}, ${pg}, ${pbb}`,
    '--dk-hover': tint(dark ? 0.18 : 0.14), '--dk-hover-lo': tint(dark ? 0.12 : 0.1), '--dk-sel': tint(dark ? 0.26 : 0.17), '--dk-soft': tint(dark ? 0.14 : 0.1),
    '--dk-primary': prim, '--dk-on-primary': onPrim, '--dk-primary-container': cont, '--dk-on-primary-container': onCont,
    '--dk-pill-act': text, '--dk-snap-fill': tint(dark ? 0.2 : 0.16), '--dk-snap-edge': dark ? tint(0.8) : text,
  };
}

/** Der CSS-Wert des Hintergrunds ('' fuer das gemalte Bild) */
export function wallpaperCss(id, dark) {
  const w = WALLPAPERS.find((x) => x.id === id);
  if (!w || !w.light) return '';
  const [c1, c2, c3] = dark ? w.dark : w.light, ov = dark ? '0, 0, 0' : '255, 255, 255';
  const base = `linear-gradient(180deg, ${c1}, ${c2} 55%, ${c3})`;
  if (w.pattern === 'dots') return `radial-gradient(circle, rgba(${ov}, .3) 1.6px, transparent 1.8px) 0 0 / 24px 24px, ${base}`;
  if (w.pattern === 'grid') return `linear-gradient(rgba(${ov}, .22) 1px, transparent 1px) 0 0 / 32px 32px, linear-gradient(90deg, rgba(${ov}, .22) 1px, transparent 1px) 0 0 / 32px 32px, ${base}`;
  return base;
}

/** Alles, was das Dokument bekommt: Variablen und Datenattribute */
export function appearanceVars(a, dark) {
  const n = normalizeAppearance(a);
  const vars = { '--ic-s': String(ICON_SCALE[n.icons.size]), '--dk-dock-s': String(DOCK_SCALE[n.dock.size]) };
  if (n.accent) Object.assign(vars, accentVars(n.accent, dark));
  const bg = wallpaperCss(n.bg, dark); if (bg) vars['--dk-bg-image'] = bg;
  return { vars, attrs: { 'data-bg': n.bg, 'data-icon-style': n.icons.style, 'data-dock-size': n.dock.size } };
}
const MANAGED = ['--ic-s', '--dk-dock-s', '--dk-bg-image', ...ACCENT_VARS];
/** Setzt das Aussehen sofort auf das Dokument (CSS-Variablen, kein Neuladen); was nicht mehr gilt, wird entfernt */
export function applyAppearance(a, dark, root = document.documentElement) {
  const { vars, attrs } = appearanceVars(a, dark);
  for (const k of MANAGED) { if (k in vars) root.style.setProperty(k, vars[k]); else root.style.removeProperty(k); }
  for (const [k, v] of Object.entries(attrs)) root.setAttribute(k, v);
}
