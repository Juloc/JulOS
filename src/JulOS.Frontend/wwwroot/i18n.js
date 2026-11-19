/* =====================================================================
 * Sprachen der Oberflaeche (Deutsch und Englisch, docs/DEVELOPMENT.md "Languages"): ein kleines Modul ohne Bibliothek.
 *  - t('schluessel', {name: wert}) liefert den Text in der gewaehlten Sprache; {name} im Text wird ersetzt.
 *  - Rueckfall: fehlt ein Schluessel in der gewaehlten Sprache, kommt der deutsche Text; fehlt er auch dort, kommt ein leerer Text (nie der rohe Schluessel).
 *  - Die Wahl heisst "Deutsch" (de, Standard), "English" (en) oder "System" (nach navigator.language); gespeichert lokal (pd.lang) und als Server-Einstellung "lang"
 *    (prefs.js), eine Aenderung gilt nach dem Neuladen der Seite (die Oberflaeche liest die Sprache beim Start).
 *  - Texte in index.html stehen deutsch im Dokument und tragen data-i18n="schluessel" (Text), data-i18n-title, data-i18n-aria-label, data-i18n-placeholder (Attribute);
 *    applyDom() setzt sie in der Sprache; auch fuer geklonte Vorlagen.
 * Kein DOM noetig (nur applyDom und setDocumentLang brauchen eines), damit es sich mit node pruefen laesst.
 * ===================================================================== */
import de from './i18n/de.js';
import en from './i18n/en.js';

export const LANGS = ['de', 'en'];
/** Die Wahl in den Einstellungen: Deutsch, English oder System. Anzeigenamen stehen in ihrer eigenen Sprache, damit man sie immer findet. */
export const LANG_CHOICES = [['de', 'Deutsch'], ['en', 'English'], ['system', 'System']];
export const DEFAULT_PREF = 'de';
const STORE_KEY = 'pd.lang';
const DICTS = { de, en };
const LOCALES = { de: 'de-DE', en: 'en-GB' };     // en-GB: 24-Stunden-Uhr wie die deutsche Oberflaeche

export const isPref = (v) => v === 'system' || LANGS.includes(v);
export const normalizePref = (v) => (isPref(v) ? v : DEFAULT_PREF);

/** Aus der Wahl und der Browsersprache (navigator.languages oder language) die Sprache der Oberflaeche. */
export function resolveLang(pref, navLangs) {
  if (LANGS.includes(pref)) return pref;
  if (pref !== 'system') return DEFAULT_PREF;
  const list = Array.isArray(navLangs) ? navLangs : navLangs ? [navLangs] : [];
  for (const l of list) {
    const base = String(l).toLowerCase().split('-')[0];
    if (LANGS.includes(base)) return base;
  }
  return DEFAULT_PREF;
}

const browserStore = () => (typeof window === 'undefined' ? null : window.localStorage);
let pref = DEFAULT_PREF, lang = DEFAULT_PREF;
const nav = () => (typeof navigator === 'undefined' ? [] : navigator.languages && navigator.languages.length ? [...navigator.languages] : [navigator.language]);

/** Liest die lokal gespeicherte Wahl (beim Laden des Moduls und in Tests). */
export function loadPref(storage = browserStore(), navLangs = nav()) {
  let v = null;
  try { v = storage && storage.getItem(STORE_KEY); } catch { /* egal */ }
  pref = normalizePref(v);
  lang = resolveLang(pref, navLangs);
  setDocumentLang();
  return pref;
}
/** Merkt eine neue Wahl lokal und stellt die Sprache sofort um (die schon gezeichnete Oberflaeche muss neu geladen werden). */
export function setPref(v, storage = browserStore(), navLangs = nav()) {
  pref = normalizePref(v);
  lang = resolveLang(pref, navLangs);
  try { if (storage) storage.setItem(STORE_KEY, pref); } catch { /* egal */ }
  setDocumentLang();
  return lang;
}
export const getPref = () => pref;
export const getLang = () => lang;
/** Gebietsschema fuer Datum, Uhrzeit und Zahlen (Intl). */
export const locale = () => LOCALES[lang];

const fill = (s, p) => (p ? s.replace(/\{(\w+)\}/g, (m, k) => (p[k] === undefined || p[k] === null ? '' : String(p[k]))) : s);

/** Der Text zu einem Schluessel in der gewaehlten Sprache (Rueckfall Deutsch, sonst leer). */
export function t(key, params) {
  let s = DICTS[lang][key];
  if (s === undefined) s = de[key];
  if (s === undefined) { warnOnce(key); return ''; }
  return fill(s, params);
}
/** Mit Zahl: waehlt "schluessel.one" bei 1, sonst "schluessel.other"; {n} steht fuer die Zahl. */
export function tp(key, n, params) { return t(`${key}.${n === 1 ? 'one' : 'other'}`, { n, ...params }); }
/** Markiert einen Schluessel, der erst spaeter uebersetzt wird (Tabellen, Listen): gibt ihn unveraendert zurueck, damit der Test ihn findet. */
export const tk = (key) => key;
/** Gibt es den Schluessel (in Deutsch)? */
export const hasKey = (key) => de[key] !== undefined;

const warned = new Set();
function warnOnce(key) { if (!warned.has(key)) { warned.add(key); try { console.warn('i18n: Schluessel fehlt:', key); } catch { /* egal */ } } }

/* ---------- Formate ---------- */
/** Zahl mit festen Nachkommastellen im Zahlenformat der Sprache (de: 1,5; en: 1.5). */
export const fmtNumber = (n, digits = 0) => Number(n).toLocaleString(locale(), { minimumFractionDigits: digits, maximumFractionDigits: digits });
/** Datum und Uhrzeit nach den Intl-Optionen im Format der Sprache; ungueltige Eingaben ergeben einen leeren Text. */
export function fmtDate(v, opts = { dateStyle: 'medium', timeStyle: 'short' }) {
  const d = v instanceof Date ? v : new Date(v);
  return isNaN(d) ? '' : d.toLocaleString(locale(), opts);
}
export const fmtTime = (v, opts = { hour: '2-digit', minute: '2-digit' }) => { const d = v instanceof Date ? v : new Date(v); return isNaN(d) ? '' : d.toLocaleTimeString(locale(), opts); };
export const fmtDay = (v, opts = { day: '2-digit', month: '2-digit', year: 'numeric' }) => { const d = v instanceof Date ? v : new Date(v); return isNaN(d) ? '' : d.toLocaleDateString(locale(), opts); };
export const relativeTime = () => new Intl.RelativeTimeFormat(locale(), { numeric: 'auto' });

/* ---------- Dokument ---------- */
export function setDocumentLang() {
  if (typeof document !== 'undefined' && document.documentElement) document.documentElement.lang = lang;
}
const ATTRS = [['data-i18n-title', 'title'], ['data-i18n-aria-label', 'aria-label'], ['data-i18n-placeholder', 'placeholder'], ['data-i18n-alt', 'alt']];
/** Ohne Kindelemente wird der ganze Text ersetzt; mit Kindelementen (zum Beispiel <label>Text <input></label>) nur der erste Textknoten, die Kinder bleiben. */
function setText(el, s) {
  if (!s) return;
  if (!el.children.length) { el.textContent = s; return; }
  const tn = [...el.childNodes].find((n) => n.nodeType === 3 && n.nodeValue.trim());
  if (tn) tn.nodeValue = tn.nodeValue.replace(/\S(?:[\s\S]*\S)?/, () => s);
}
/** Setzt alle Texte und Attribute mit data-i18n* unterhalb von root (auch root selbst) in der gewaehlten Sprache. */
export function applyDom(root = document) {
  const els = [];
  if (root.matches && root.matches('[data-i18n],[data-i18n-title],[data-i18n-aria-label],[data-i18n-placeholder],[data-i18n-alt]')) els.push(root);
  if (root.querySelectorAll) els.push(...root.querySelectorAll('[data-i18n],[data-i18n-title],[data-i18n-aria-label],[data-i18n-placeholder],[data-i18n-alt]'));
  for (const el of els) {
    const k = el.getAttribute('data-i18n');
    if (k) setText(el, t(k));
    for (const [a, to] of ATTRS) { const ak = el.getAttribute(a); if (ak) { const s = t(ak); if (s) el.setAttribute(to, s); } }
  }
  return root;
}

loadPref();
