/* =====================================================================
 * Tastennamen, Kuerzel und "Als Text tippen" (reine Logik ohne Oberflaeche, mit node pruefbar: tests/frontend/keys-shortcuts.test.mjs).
 *  - Namen -> X11-Keysyms (KS), Umschalter (MODNAME), Zeichen -> Keysym, eine Tastenkombination lesen (parseChord)
 *  - Kuerzel: eine Folge von Kombinationen ("ctrl+alt+end, alt+tab") pruefen (parseSequence) und als Liste von Tastenereignissen planen (sequenceOps)
 *  - Text als Tastenfolge (textOps): fuer "Als Text tippen" und Textbausteine, mit Laengengrenze
 *  - Vorlagen fuer Windows und Linux, Pruefung einer ganzen Taste (validateKeyDef) und eine Warnung vor Passwoertern in Textbausteinen
 * Beschreibung: docs/TOUCH.md.
 * ===================================================================== */
import { t, tk } from './i18n.js';

export const KS = {
  esc: 0xff1b, escape: 0xff1b, tab: 0xff09, enter: 0xff0d, return: 0xff0d, backspace: 0xff08, bs: 0xff08,
  delete: 0xffff, del: 0xffff, entf: 0xffff, home: 0xff50, pos1: 0xff50, end: 0xff57, ende: 0xff57,
  left: 0xff51, up: 0xff52, right: 0xff53, down: 0xff54, pageup: 0xff55, pgup: 0xff55, bildauf: 0xff55,
  pagedown: 0xff56, pgdn: 0xff56, bildab: 0xff56, insert: 0xff63, einfg: 0xff63, space: 0x20, menu: 0xff67,
  ctrl: 0xffe3, strg: 0xffe3, control: 0xffe3, shift: 0xffe1, umschalt: 0xffe1, alt: 0xffe9, altgr: 0xfe03,
  super: 0xffeb, win: 0xffeb, windows: 0xffeb, meta: 0xffeb, print: 0xff61, printscreen: 0xff61, capslock: 0xffe5,
  plus: 0x2b, minus: 0x2d, comma: 0x2c, semicolon: 0x3b,
};
for (let i = 1; i <= 12; i++) KS['f' + i] = 0xffbd + i;
export const MODSYM = { ctrl: 0xffe3, alt: 0xffe9, shift: 0xffe1, super: 0xffeb, altgr: 0xfe03 };
export const MODNAME = { ctrl: 'ctrl', strg: 'ctrl', control: 'ctrl', alt: 'alt', shift: 'shift', umschalt: 'shift', super: 'super', win: 'super', windows: 'super', meta: 'super', altgr: 'altgr' };
const MOD_SYMS = new Set(Object.values(MODSYM));

export const charKeysym = (ch) => {
  const cp = ch.codePointAt(0);
  if (cp === 10 || cp === 13) return KS.enter;
  if (cp === 9) return KS.tab;
  return cp < 0x100 ? cp : 0x01000000 + cp;
};

/** Eine Kombination wie "ctrl+shift+t" als Liste von Keysyms; wirft einen Error mit verstaendlichem Text. */
export function parseChord(str) {
  const parts = String(str).split('+').map((s) => s.trim().toLowerCase()).filter(Boolean);
  if (!parts.length) throw new Error(t('app.errNoKeys'));
  return parts.map((p) => {
    if (KS[p] !== undefined) return KS[p];
    if ([...p].length === 1) return charKeysym(p);
    throw new Error(t('app.errUnknownKey', { key: p }));
  });
}

/* ---------- Grenzen (der Server prueft dieselben Laengen, SettingsRules.cs) ---------- */
export const LIMITS = { label: 24, value: 200, steps: 12, keysPerStep: 5, typeChars: 4000 };

/* ---------- Kuerzel: eine Folge von Kombinationen ---------- */
/** "ctrl+alt+end, alt+tab" -> ['ctrl+alt+end', 'alt+tab'] (Komma oder Semikolon trennt; das Zeichen selbst heisst comma und semicolon). */
export const splitSteps = (str) => String(str).split(/[,;]/).map((s) => s.trim()).filter(Boolean);

/** Prueft ein Kuerzel und gibt die Schritte als { keys: [Keysym], text } zurueck; wirft einen Error mit verstaendlichem Text. */
export function parseSequence(str) {
  const value = String(str);
  if (value.length > LIMITS.value) throw new Error(t('keys.errTooLong', { max: LIMITS.value }));
  const parts = splitSteps(value);
  if (!parts.length) throw new Error(t('keys.errEmpty'));
  if (parts.length > LIMITS.steps) throw new Error(t('keys.errTooManySteps', { max: LIMITS.steps }));
  return parts.map((p) => {
    const keys = parseChord(p);
    if (keys.length > LIMITS.keysPerStep) throw new Error(t('keys.errTooManyKeys', { max: LIMITS.keysPerStep }));
    return { keys, text: p.toLowerCase().replace(/\s+/g, '') };
  });
}

/**
 * Ein Kuerzel als Liste von Ereignissen: { op: 'down' | 'up', sym } und { op: 'wait', ms }. Jede Kombination drueckt ihre Tasten der Reihe nach und laesst sie in umgekehrter
 * Reihenfolge los (wie sendChord in app.js); zwischen den Kombinationen liegt eine kurze Pause, damit das Ziel jede einzeln sieht (zum Beispiel Win+R, dann Zeichen).
 */
export function sequenceOps(steps, { gap = 12, stepGap = 90 } = {}) {
  const ops = [];
  steps.forEach((step, i) => {
    if (i > 0) ops.push({ op: 'wait', ms: stepGap });
    for (const sym of step.keys) { ops.push({ op: 'down', sym }, { op: 'wait', ms: gap }); }
    for (const sym of [...step.keys].reverse()) { ops.push({ op: 'up', sym }, { op: 'wait', ms: gap }); }
  });
  return ops;
}

/** Welche Tasten sind nach den ersten n Ereignissen noch gedrueckt (in der Reihenfolge des Drueckens)? Fuer das Loslassen bei einem Abbruch. */
export function heldAfter(ops, n = ops.length) {
  const held = [];
  for (const o of ops.slice(0, n)) {
    if (o.op === 'down') held.push(o.sym);
    else if (o.op === 'up') { const i = held.lastIndexOf(o.sym); if (i >= 0) held.splice(i, 1); }
  }
  return held;
}

/* ---------- Text als Tastenfolge ---------- */
/**
 * Macht einen Text zu Tastenereignissen: [{ sym, ch }] (je Zeichen ein Druck und Loslassen). Zeilenenden werden Enter (CR LF und CR zaehlen als eines), ein Tabulator wird Tab,
 * andere Steuerzeichen, Nullbreiten-Zeichen und das feste Leerzeichen (U+00A0 wird ein normales) fallen weg. Hoechstens `max` Zeichen (Rest: truncated).
 * Ergebnis: { keys, count, total, truncated }; total sind die Zeichen nach dem Aufraeumen.
 */
export function textOps(text, { max = LIMITS.typeChars } = {}) {
  const clean = String(text ?? '').replace(/\r\n?/g, '\n').replace(/ /g, ' ').replace(/[​-‍⁠﻿]/g, '');
  const chars = [];
  for (const ch of clean) {
    const cp = ch.codePointAt(0);
    if (cp < 0x20 && ch !== '\n' && ch !== '\t') continue;
    if (cp === 0x7f || (cp >= 0x80 && cp < 0xa0)) continue;
    chars.push(ch);
  }
  const used = chars.slice(0, max);
  return { keys: used.map((ch) => ({ sym: charKeysym(ch), ch })), count: used.length, total: chars.length, truncated: chars.length > used.length };
}

/** Teilt eine Liste in Stuecke (damit das Tippen zwischendurch abgebrochen werden kann). */
export function chunk(list, size) {
  const out = [];
  for (let i = 0; i < list.length; i += Math.max(1, size)) out.push(list.slice(i, i + size));
  return out;
}

/* ---------- Vorlagen ---------- */
/** Vorlagen fuer Kuerzel (nameKey: Woerterbuchschluessel des Namens, value: das Kuerzel). */
export const TEMPLATES = {
  windows: [
    { nameKey: tk('keys.tplCad'), value: 'ctrl+alt+end' },        // im Remote Desktop heisst Strg+Alt+Entf Strg+Alt+Ende
    { nameKey: tk('keys.tplTaskMgr'), value: 'ctrl+shift+esc' },
    { nameKey: tk('keys.tplSwitch'), value: 'alt+tab' },
    { nameKey: tk('keys.tplDesktop'), value: 'win+d' },
    { nameKey: tk('keys.tplExplorer'), value: 'win+e' },
    { nameKey: tk('keys.tplRun'), value: 'win+r' },
    { nameKey: tk('keys.tplLock'), value: 'win+l' },
    { nameKey: tk('keys.tplClose'), value: 'alt+f4' },
    { nameKey: tk('keys.tplSnip'), value: 'win+shift+s' },
    { nameKey: tk('keys.tplCmd'), value: 'win+r, c, m, d, enter' },
  ],
  linux: [
    { nameKey: tk('keys.tplSwitch'), value: 'alt+tab' },
    { nameKey: tk('keys.tplTerminal'), value: 'ctrl+alt+t' },
    { nameKey: tk('keys.tplCadLinux'), value: 'ctrl+alt+delete' },
    { nameKey: tk('keys.tplOverview'), value: 'super' },
    { nameKey: tk('keys.tplDesktop'), value: 'super+d' },
    { nameKey: tk('keys.tplLock'), value: 'super+l' },
    { nameKey: tk('keys.tplClose'), value: 'alt+f4' },
    { nameKey: tk('keys.tplRunLinux'), value: 'alt+f2' },
    { nameKey: tk('keys.tplTermCopy'), value: 'ctrl+shift+c' },
    { nameKey: tk('keys.tplTermPaste'), value: 'ctrl+shift+v' },
  ],
};

/* ---------- Eine ganze Taste pruefen ---------- */
const TOKEN = /^[a-z0-9_-]{1,30}$/;
const PLACE = /^[0-9A-Za-z_-]{1,64}$/;
export const KEY_TYPES = ['chord', 'mod', 'text', 'action', 'seq'];
const hasControl = (s) => /[\u0000-\u001f\u007f-\u009f]/.test(s);

/**
 * Prueft die Eingaben des Tasten-Editors und macht daraus die Taste: { ok: true, def } oder { ok: false, error }. actions: die erlaubten Aktionsnamen (app.js kennt sie).
 * Der Server prueft dieselben Grenzen noch einmal (SettingsRules.cs), also darf hier nichts durchgehen, was er ablehnt.
 */
export function validateKeyDef(input, { actions = [] } = {}) {
  const type = String(input.type || '');
  if (!KEY_TYPES.includes(type)) return { ok: false, error: t('keys.errType') };
  const value = String(input.value ?? '').trim();
  if (!value) return { ok: false, error: t('app.keyNeedValue') };
  let label = String(input.label ?? '').trim() || value.slice(0, 10);
  if (hasControl(label) || label.length > LIMITS.label) return { ok: false, error: t('keys.errLabel', { max: LIMITS.label }) };
  if (value.length > LIMITS.value) return { ok: false, error: t('keys.errTooLong', { max: LIMITS.value }) };
  try {
    if (type === 'chord') parseChord(value);
    else if (type === 'seq') parseSequence(value);
    else if (type === 'mod') { if (!MODNAME[value.toLowerCase()]) return { ok: false, error: t('app.errModValue') }; }
    else if (type === 'action') { if (!actions.includes(value.toLowerCase())) return { ok: false, error: t('app.errActionList', { actions: actions.join(', ') }) }; }
    else if (hasControl(value)) return { ok: false, error: t('keys.errControl') };      // Text: eine Zeile (der Server nimmt keine Steuerzeichen)
  } catch (e) { return { ok: false, error: e.message }; }
  const def = { label, type, value: type === 'mod' ? value.toLowerCase() : value };
  if (input.scope && input.scope !== 'both') def.scope = input.scope;
  if (input.icon) { if (!TOKEN.test(input.icon)) return { ok: false, error: t('keys.errIcon') }; def.icon = input.icon; }
  if (input.conn) { if (!PLACE.test(input.conn)) return { ok: false, error: t('keys.errConn') }; def.conn = input.conn; }
  return { ok: true, def };
}

/** Gilt die Taste in dieser Sitzung? Art (desktop/terminal), und wenn sie an eine Verbindung gebunden ist, nur dort. */
export function keyApplies(def, { kind, connId }) {
  if (def.scope && def.scope !== 'both' && def.scope !== kind) return false;
  if (def.conn && def.conn !== connId) return false;
  return true;
}

/**
 * Sieht ein Textbaustein nach einem Passwort aus? Textbausteine liegen im Klartext in den Einstellungen auf dem Server und sind kein Ort fuer Passwoerter: der Editor fragt vorher nach.
 * Ein Wort wie "Passwort" im Namen zaehlt, ebenso ein Wert ohne Leerzeichen, der Klein- und Grossbuchstaben, Ziffern oder Sonderzeichen mischt (mindestens drei der vier Arten, 8 bis 64 Zeichen).
 */
export function looksLikeSecret(label, value) {
  if (/pass(wort|word|phrase)?|kennwort|\bpwd?\b|\bpin\b|secret|geheim|token|api[-_ ]?key/i.test(String(label))) return true;
  const v = String(value);
  if (v.length < 8 || v.length > 64 || /\s/.test(v)) return false;
  const classes = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((r) => r.test(v)).length;
  return classes >= 3;
}
