import { remoteApi as R } from './app.js';
import { t, tk } from './i18n.js';

/* =====================================================================
 * Menues der oberen Leiste fuer das aktive Fernfenster (Desktop oder Terminal). Sie ersetzen die Tastenleiste unten, die auf dem PC nicht mehr steht.
 * Alle Aktionen kommen aus app.js (remoteApi): dieselben Tasten, Kombinationen (gleiche Speicherung wie die Leiste), Zoom und Fensterliste der VM.
 * Eintraege wie in desktop.js: { label, icon, hint, disabled, run } | { sep: true } | { cap } | { label, sub: [...] }.
 * deps: { win: () => Fenster des Fernfensters (maximized, toggleMax, el), toggleMax, notify }
 * ===================================================================== */

const MODS = [['ctrl', tk('desktop.keyCtrl')], ['alt', tk('desktop.keyAlt')], ['shift', tk('desktop.keyShift')], ['super', tk('desktop.keyWindows')]];
const MOD_STATE = { off: tk('desktop.off'), once: tk('desktop.modOnce'), lock: tk('desktop.modLock') };
const ZOOMS = [75, 100, 125, 150, 200];

/** Kennungen der Menues (Reihenfolge der Leiste); die Beschriftung kommt aus remoteMenuLabel */
export function remoteMenuNames(kind) { return kind === 'desktop' ? ['keys', 'combos', 'view', 'windows', 'sound'] : kind === 'vnc' ? ['keys', 'combos', 'view', 'sound'] : ['keys', 'combos', 'view']; }
const MENU_LABELS = { keys: tk('desktop.menuKeys'), combos: tk('desktop.menuCombos'), view: tk('desktop.menuView'), windows: tk('desktop.menuWindows'), sound: tk('desktop.menuAudio') };
export const remoteMenuLabel = (name) => t(MENU_LABELS[name]);

/** Menue "<Name der Verbindung>": die Sitzung als Ganzes (wie das Programm-Menue am Mac) */
export function sessionMenu(deps) {
  const funcs = R.group('funcs');
  return [
    { label: t('desktop.reconnect'), icon: 'play', run: () => R.reconnect() },
    ...(R.canPopOut() ? [{ label: R.popped() ? t('desktop.pinBack') : t('desktop.openOwnWindow'), icon: 'blank', run: () => R.popOut() }] : []),
    { sep: true },
    ...taskbarItems(deps),
    ...(funcs ? funcs.items.filter((d) => d.type === 'action').map((d) => ({ label: d.label.startsWith(':') ? d.value : d.label, icon: 'blank', run: () => R.press(d) })) : []),
    { sep: true },
    { label: t('desktop.endConnection'), icon: 'power', run: () => deps.end() },
  ];
}

/** Taskleiste der VM im Dock (desktop-taskbar.js): nur fuer Bildverbindungen (Desktop, VNC), nicht fuers Terminal */
function taskbarItems(deps) {
  const s = deps.taskbar && deps.taskbar.session();
  if (!s) return [];
  return [
    { label: s.tb ? t('desktop.tbRemark') : t('desktop.tbMark'), icon: 'blank', run: () => deps.taskbar.mark() },
    ...(s.tb ? [{ label: t('desktop.tbOff'), icon: 'blank', run: () => deps.taskbar.off() }] : []),
    { sep: true },
  ];
}

function keysMenu() {
  const mods = MODS.map(([n, label]) => {
    const st = R.mod(n) || 'off';
    return { label: t('desktop.holdKey', { key: t(label) }), icon: st === 'off' ? 'blank' : 'check', hint: t(MOD_STATE[st]), run: () => R.cycleMod(n) };
  });
  const fkeys = Array.from({ length: 12 }, (_, i) => ({ label: 'F' + (i + 1), icon: 'blank', run: () => R.keys('f' + (i + 1)) }));
  return [
    { label: 'Esc', icon: 'blank', run: () => R.keys('esc') },
    { label: 'Tab', icon: 'blank', run: () => R.keys('tab') },
    { label: t('desktop.windowsKey'), icon: 'blank', run: () => R.keys('win') },
    { label: t('desktop.ctrlAltDel'), icon: 'blank', run: () => R.keys('ctrl+alt+delete') },
    { sep: true },
    { cap: t('desktop.forNextKey') },
    ...mods,
    { sep: true },
    { label: t('desktop.fKeys'), icon: 'blank', sub: fkeys },
  ];
}

function combosMenu() {
  const g = R.group('combos');
  if (!g) return [{ label: t('desktop.noCombos'), disabled: true }];
  const label = (d) => (String(d.label).startsWith(':') ? d.value : d.label);
  return [
    ...g.items.map((d) => ({ label: label(d), icon: 'blank', run: () => R.press(d) })),
    { sep: true },
    { label: t('desktop.newCombo'), icon: 'plus', run: () => R.editKey(g.index, -1) },
    ...(g.items.length ? [{ label: t('desktop.editCombo'), icon: 'edit', sub: g.items.map((d, i) => ({ label: label(d), icon: 'blank', run: () => R.editKey(g.index, i) })) }] : []),
    { label: t('desktop.resetCombos'), icon: 'blank', run: () => R.resetKeys() },
  ];
}

function viewMenu(deps) {
  const pct = R.zoomPercent();
  const w = deps.win();
  return [
    { cap: t('desktop.zoomCap', { n: pct }) },
    { label: t('desktop.zoomIn'), icon: 'blank', run: () => R.zoomIn() },
    { label: t('desktop.zoomOut'), icon: 'blank', run: () => R.zoomOut() },
    ...ZOOMS.map((p) => ({ label: t('desktop.percent', { n: p }), icon: Math.abs(pct - p) <= 3 ? 'check' : 'blank', run: () => R.setZoomPercent(p) })),
    { sep: true },
    { label: t('desktop.fitWindow'), icon: 'blank', run: () => R.fit() },
    { sep: true },
    { label: t('desktop.statusBar'), icon: R.perfShown() ? 'check' : 'blank', hint: R.perfShown() ? t('desktop.on') : t('desktop.off'), run: () => R.setPerf(!R.perfShown()) },
    { label: t('desktop.frameRate'), icon: 'blank', hint: (R.qualities.find((q) => q.id === R.quality()) || R.qualities[0]).label,
      sub: R.qualities.map((q) => ({ label: q.label, icon: R.quality() === q.id ? 'check' : 'blank', hint: q.id === 'auto' ? t('desktop.default') : '', run: () => R.setQuality(q.id) })) },
    { sep: true },
    { label: w && w.maximized ? t('desktop.restoreWindow') : t('desktop.maximizeWindow'), icon: 'blank', run: () => deps.toggleMax() },
    { label: document.fullscreenElement ? t('desktop.exitFullscreen') : t('desktop.fullscreen'), icon: 'blank', run: () => deps.fullscreen() },
  ];
}

/** Ton und Mikrofon: Ton vom Rechner an/aus, Lautstaerke, Mikrofon an/aus, Diagnose. VNC kennt weder Ton noch Mikrofon und sagt es. */
const VOLUMES = [100, 75, 50, 25, 0];
function soundMenu() {
  const a = R.audio();
  if (!a) return [{ label: t('audio.none'), disabled: true }];
  if (a.vnc) return [{ label: t('audio.vnc'), disabled: true }];
  const pct = Math.round(a.volume * 100);
  return [
    { label: t('app.menuSound'), icon: a.on ? 'check' : 'blank', hint: a.values.sound, run: () => R.audioAct('sound') },
    { label: t('app.menuVolume'), icon: 'blank', hint: a.values.volume,
      sub: VOLUMES.map((p) => ({ label: t('desktop.percent', { n: p }), icon: Math.abs(pct - p) <= 12 ? 'check' : 'blank', run: () => R.setVolume(p / 100) })) },
    { sep: true },
    { label: t('app.menuMic'), icon: a.micOn ? 'check' : 'blank', hint: a.values.mic, run: () => R.audioAct('mic') },
    { sep: true },
    { label: t('audio.menuDiag'), icon: 'blank', run: () => R.audioAct('audioDiag') },
  ];
}

/** Fenster der Ziel-Maschine (Agent): die Liste wird beim Oeffnen frisch geholt */
async function windowsMenu() {
  try { await Promise.race([R.refreshWindows(), new Promise((r) => setTimeout(r, 900))]); } catch { /* alte Liste */ }
  const s = R.windows();
  if (!s.available) return [{ label: t('desktop.noAgent'), disabled: true }, { label: t('desktop.noAgentHint'), disabled: true }];
  const items = [
    { label: s.mode === 'apps' ? t('desktop.toDesktopMode') : t('desktop.toAppMode'), icon: s.mode === 'apps' ? 'monitor' : 'grid', run: () => R.toggleMode() },
    { label: t('desktop.startProgram'), icon: 'plus', run: () => R.openApps() },
    { sep: true },
    { cap: s.list.length ? t('desktop.openWindows') : t('desktop.noOpenWindows') },
  ];
  for (const w of s.list) items.push({ label: (w.title || w.app || '?').slice(0, 48), icon: w.active ? 'check' : 'blank', hint: w.minimized ? t('desktop.minimized') : '', run: () => R.activateWindow(w.id) });
  return items;
}

/** Eintraege eines Menues der Leiste (kann ein Versprechen sein, weil die Fensterliste geholt wird) */
export function remoteMenuItems(name, deps) {
  return { keys: () => keysMenu(), combos: () => combosMenu(), view: () => viewMenu(deps), windows: () => windowsMenu(), sound: () => soundMenu() }[name]();
}
