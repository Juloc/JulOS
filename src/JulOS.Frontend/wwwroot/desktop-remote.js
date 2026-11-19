import { remoteApi as R } from './app.js';

/* =====================================================================
 * Menues der oberen Leiste fuer das aktive Fernfenster (Desktop oder Terminal). Sie ersetzen die Tastenleiste unten, die auf dem PC nicht mehr steht.
 * Alle Aktionen kommen aus app.js (remoteApi): dieselben Tasten, Kombinationen (gleiche Speicherung wie die Leiste), Zoom und Fensterliste der VM.
 * Eintraege wie in desktop.js: { label, icon, hint, disabled, run } | { sep: true } | { cap } | { label, sub: [...] }.
 * deps: { win: () => Fenster des Fernfensters (maximized, toggleMax, el), toggleMax, notify }
 * ===================================================================== */

const MODS = [['ctrl', 'Strg'], ['alt', 'Alt'], ['shift', 'Umschalt'], ['super', 'Windows']];
const MOD_STATE = { off: 'aus', once: 'nächste Taste', lock: 'gesperrt' };
const ZOOMS = [75, 100, 125, 150, 200];

export function remoteMenuNames(kind) { return kind === 'desktop' ? ['Tasten', 'Kombinationen', 'Ansicht', 'Fenster'] : ['Tasten', 'Kombinationen', 'Ansicht']; }

/** Menue "<Name der Verbindung>": die Sitzung als Ganzes (wie das Programm-Menue am Mac) */
export function sessionMenu(deps) {
  const funcs = R.group('funcs');
  return [
    { label: 'Neu verbinden', icon: 'play', run: () => R.reconnect() },
    ...(R.canPopOut() ? [{ label: R.popped() ? 'Wieder anheften' : 'In eigenem Fenster öffnen', icon: 'blank', run: () => R.popOut() }] : []),
    { sep: true },
    ...(funcs ? funcs.items.filter((d) => d.type === 'action').map((d) => ({ label: d.label.startsWith(':') ? d.value : d.label, icon: 'blank', run: () => R.press(d) })) : []),
    { sep: true },
    { label: 'Verbindung beenden', icon: 'power', run: () => deps.end() },
  ];
}

function keysMenu() {
  const mods = MODS.map(([n, label]) => {
    const st = R.mod(n) || 'off';
    return { label: label + ' halten', icon: st === 'off' ? 'blank' : 'check', hint: MOD_STATE[st], run: () => R.cycleMod(n) };
  });
  const fkeys = Array.from({ length: 12 }, (_, i) => ({ label: 'F' + (i + 1), icon: 'blank', run: () => R.keys('f' + (i + 1)) }));
  return [
    { label: 'Esc', icon: 'blank', run: () => R.keys('esc') },
    { label: 'Tab', icon: 'blank', run: () => R.keys('tab') },
    { label: 'Windows-Taste', icon: 'blank', run: () => R.keys('win') },
    { label: 'Strg+Alt+Entf', icon: 'blank', run: () => R.keys('ctrl+alt+delete') },
    { sep: true },
    { cap: 'Für die nächste Taste' },
    ...mods,
    { sep: true },
    { label: 'F-Tasten', icon: 'blank', sub: fkeys },
  ];
}

function combosMenu() {
  const g = R.group('combos');
  if (!g) return [{ label: 'Keine Kombinationen', disabled: true }];
  const label = (d) => (String(d.label).startsWith(':') ? d.value : d.label);
  return [
    ...g.items.map((d) => ({ label: label(d), icon: 'blank', run: () => R.press(d) })),
    { sep: true },
    { label: 'Neue Kombination …', icon: 'plus', run: () => R.editKey(g.index, -1) },
    ...(g.items.length ? [{ label: 'Kombination bearbeiten', icon: 'edit', sub: g.items.map((d, i) => ({ label: label(d), icon: 'blank', run: () => R.editKey(g.index, i) })) }] : []),
    { label: 'Auf Standard zurücksetzen …', icon: 'blank', run: () => R.resetKeys() },
  ];
}

function viewMenu(deps) {
  const pct = R.zoomPercent();
  const w = deps.win();
  return [
    { cap: `Zoom ${pct} %` },
    { label: 'Vergrößern', icon: 'blank', run: () => R.zoomIn() },
    { label: 'Verkleinern', icon: 'blank', run: () => R.zoomOut() },
    ...ZOOMS.map((p) => ({ label: p + ' %', icon: Math.abs(pct - p) <= 3 ? 'check' : 'blank', run: () => R.setZoomPercent(p) })),
    { sep: true },
    { label: 'An Fenstergröße anpassen', icon: 'blank', run: () => R.fit() },
    { sep: true },
    { label: 'Statusleiste', icon: R.perfShown() ? 'check' : 'blank', hint: R.perfShown() ? 'an' : 'aus', run: () => R.setPerf(!R.perfShown()) },
    { label: 'Bildrate', icon: 'blank', hint: (R.qualities.find((q) => q.id === R.quality()) || R.qualities[0]).label,
      sub: R.qualities.map((q) => ({ label: q.label, icon: R.quality() === q.id ? 'check' : 'blank', hint: q.id === 'auto' ? 'Standard' : '', run: () => R.setQuality(q.id) })) },
    { sep: true },
    { label: w && w.maximized ? 'Fenster wiederherstellen' : 'Fenster maximieren', icon: 'blank', run: () => deps.toggleMax() },
    { label: document.fullscreenElement ? 'Vollbild beenden' : 'Vollbild', icon: 'blank', run: () => deps.fullscreen() },
  ];
}

/** Fenster der Ziel-Maschine (Agent): die Liste wird beim Oeffnen frisch geholt */
async function windowsMenu() {
  try { await Promise.race([R.refreshWindows(), new Promise((r) => setTimeout(r, 900))]); } catch { /* alte Liste */ }
  const s = R.windows();
  if (!s.available) return [{ label: 'Kein Agent auf diesem Ziel', disabled: true }, { label: 'Fensterliste und Programm-Modus brauchen den Agenten', disabled: true }];
  const items = [
    { label: s.mode === 'apps' ? 'Zum Desktop-Modus wechseln' : 'Zum Programm-Modus wechseln', icon: s.mode === 'apps' ? 'monitor' : 'grid', run: () => R.toggleMode() },
    { label: 'Programm starten …', icon: 'plus', run: () => R.openApps() },
    { sep: true },
    { cap: s.list.length ? 'Offene Fenster der Ziel-Maschine' : 'Keine offenen Fenster' },
  ];
  for (const w of s.list) items.push({ label: (w.title || w.app || '?').slice(0, 48), icon: w.active ? 'check' : 'blank', hint: w.minimized ? 'minimiert' : '', run: () => R.activateWindow(w.id) });
  return items;
}

/** Eintraege eines Menues der Leiste (kann ein Versprechen sein, weil die Fensterliste geholt wird) */
export function remoteMenuItems(name, deps) {
  return { Tasten: () => keysMenu(), Kombinationen: () => combosMenu(), Ansicht: () => viewMenu(deps), Fenster: () => windowsMenu() }[name]();
}
