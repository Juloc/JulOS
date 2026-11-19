/* =====================================================================
 * Hell / Dunkel / System: die Wahl der Helligkeit, gemeinsam fuer Handy und Desktop (docs/THEME.md).
 * Gespeichert wird sie als Feld "scheme" der Server-Einstellung "theme" (prefs.js, SettingsRules.cs) und lokal als Rueckfall.
 * Kein DOM, damit es sich mit node pruefen laesst.
 * ===================================================================== */
import { t } from './i18n.js';

/** Die drei Werte in der Reihenfolge der Anzeige: Hell, Dunkel, System (auto = folgt prefers-color-scheme). */
export const SCHEMES = [['light', t('sch.light')], ['dark', t('sch.dark')], ['auto', t('sch.auto')]];
export const isScheme = (v) => SCHEMES.some(([id]) => id === v);
/** Eine unbekannte Angabe (zum Beispiel aus einer alten lokalen Kopie) wird zu "auto". */
export const normalizeScheme = (v) => (isScheme(v) ? v : 'auto');
/** Ist die Oberflaeche dunkel? systemDark: ob das Geraet dunkel will (matchMedia('(prefers-color-scheme: dark)').matches). */
export const isDark = (scheme, systemDark) => (scheme === 'dark' ? true : scheme === 'light' ? false : !!systemDark);
