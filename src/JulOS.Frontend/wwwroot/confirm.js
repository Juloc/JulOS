import { t, tk } from './i18n.js';

/* =====================================================================
 * Die Stufe einer Bestaetigung (docs/HOST-AGENT.md, "Bestaetigung"), gemeinsam fuer Host-Aktionen und Dateien. Reine Funktionen ohne DOM (tests/frontend/confirm.test.mjs).
 * Der Server legt fest und prueft: prepare nennt confirmLevel (routine, dangerous, critical) und confirmMode (was run verlangt: click = nichts, check = Haekchen, type = Rechnername)
 * samt den Schluesseln der Warntexte; die Seite zeigt es nur. Der Server lehnt run ohne Haekchen oder Namen mit 400 confirm_required ab, egal was diese Seite tut.
 * ===================================================================== */

const LEVELS = ['routine', 'dangerous', 'critical'];
const MODES = ['check', 'type', 'click'];

/** Was geschieht (gelbe Warnung); der Schluessel kommt vom Server (confirmWarn) */
const WARN = {
  reboot: tk('confirm.warn.reboot'), poweroff: tk('confirm.warn.poweroff'), stop_julos: tk('confirm.warn.stopJulos'), restart_julos: tk('confirm.warn.restartJulos'),
  delete_many: tk('confirm.warn.deleteMany'), delete_recursive: tk('confirm.warn.deleteRecursive'), delete_root: tk('confirm.warn.deleteRoot'),
  overwrite_root: tk('confirm.warn.overwriteRoot'), move_root: tk('confirm.warn.moveRoot'),
};
/** Die zweite, rote Zeile bei "sehr gefaehrlich"; der Schluessel kommt vom Server (confirmCritical) */
const CRITICAL = { julos_host: tk('confirm.critical.julosHost'), julos_self: tk('confirm.critical.julosSelf'), system_path: tk('confirm.critical.systemPath') };

const LEVEL_TEXT = { routine: tk('confirm.level.routine'), dangerous: tk('confirm.level.dangerous'), critical: tk('confirm.level.critical') };
const MODE_TEXT = { click: tk('confirm.mode.click'), check: tk('confirm.mode.check'), type: tk('confirm.mode.type') };
const AUDIT_DETAIL = /^(routine|dangerous|critical):(click|check|type)$/;
/** Der Eintrag "Stufe:Modus" im Verlauf (host_audit und files_audit, nie ein Inhalt) als Text; null, wenn der Eintrag etwas anderes ist */
export const auditConfirmText = (detail) => { const m = AUDIT_DETAIL.exec(detail || ''); return m ? t('hostact.auditConfirm', { level: t(LEVEL_TEXT[m[1]]), mode: t(MODE_TEXT[m[2]]) }) : null; };

/**
 * Alles, was ein Bestaetigungsdialog zur Stufe braucht. conf ist die Antwort von prepare (Host-Aktion oder Datei). Fehlen die Felder (aeltere Antwort), gilt Routine; nur ein
 * typeToConfirm ohne Stufe bedeutet wie frueher "Namen tippen".
 * @returns {{level: string, mode: string, danger: boolean, critical: boolean, needsCheck: boolean, typed: string|null, warn: string|null, criticalWarn: string|null}}
 */
export function confirmView(conf, host = '') {
  const c = conf || {};
  const named = typeof c.typeToConfirm === 'string' && c.typeToConfirm ? c.typeToConfirm : null;
  let level = LEVELS.includes(c.confirmLevel) ? c.confirmLevel : (named && c.confirmLevel === undefined ? 'dangerous' : 'routine');
  let mode = MODES.includes(c.confirmMode) ? c.confirmMode : (named && c.confirmMode === undefined ? 'type' : 'click');
  if (level === 'routine') mode = 'click';
  if (mode === 'type' && !named) mode = 'check';                 // ohne Namen kann nichts getippt werden: dann das Haekchen (der Server verlangt ohnehin, was er genannt hat)
  const name = host || c.host || '';
  return {
    level, mode,
    danger: level !== 'routine',
    critical: level === 'critical',
    needsCheck: level !== 'routine' && mode === 'check',
    typed: mode === 'type' ? named : null,
    warn: level !== 'routine' ? t(WARN[c.confirmWarn] || tk('confirm.warn.unknown'), { host: name }) : null,
    criticalWarn: level === 'critical' ? t(CRITICAL[c.confirmCritical] || tk('confirm.critical.unknown'), { host: name }) : null,
  };
}

/** Darf "Ausfuehren" aktiv sein? Haekchen gesetzt, wenn es verlangt wird; Name richtig getippt (Gross/Klein und Leerraum am Rand zaehlen nicht), wenn er verlangt wird. Der Server prueft es noch einmal. */
export function confirmReady(view, { checked = false, typed = '' } = {}) {
  if (view.needsCheck && !checked) return false;
  if (view.typed && String(typed || '').trim().toLowerCase() !== view.typed.toLowerCase()) return false;
  return true;
}

/** Was run mitschicken soll: acknowledged bei Haekchen, typed bei Namen (sonst nichts) */
export function confirmAnswer(view, { checked = false, typed = '' } = {}) {
  return { acknowledged: view.needsCheck ? !!checked : undefined, typed: view.typed ? String(typed || '') : undefined };
}
