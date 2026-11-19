/* =====================================================================
 * Programm-Sitzungen (docs/CLI-SESSIONS.md): reine Logik ohne DOM, getestet in tests/frontend/terminal-programs.test.mjs.
 * Eine Terminal-Verbindung startet die Shell ('shell'), Claude Code ('claude'), Codex ('codex') oder einen eigenen Befehl ('custom').
 * Hier: welche Kachel (Symbol, Name) zu einer Verbindung gehoert, welche Verbindung ein Tipp auf "Claude" oeffnet, die Schnelltasten je Programm,
 * die Pruefung des Arbeitsverzeichnisses (wie auf dem Server) und die Texte zu den Hinweisen des Servers.
 * ===================================================================== */
import { tk } from './i18n.js';

export const PROGRAMS = ['shell', 'claude', 'codex', 'custom'];
export const ASSISTANTS = ['claude', 'codex'];

/** Das Programm einer Verbindung (alles Unbekannte oder Fehlende ist die Shell; Desktops haben keins). */
export const programOf = (c) => (c && c.kind === 'terminal' && PROGRAMS.includes(c.program) ? c.program : 'shell');
export const isAssistant = (c) => ASSISTANTS.includes(programOf(c));

/** Symbol und Beschriftung der Art einer Verbindung: 'claude' | 'codex' | 'terminal' | 'desktop' | 'vnc' (Symbole: mobile.js und desktop.js). */
export function tileKind(c) {
  if (!c) return 'desktop';
  if (c.kind !== 'terminal') return c.kind === 'vnc' ? 'vnc' : 'desktop';
  const p = programOf(c);
  return p === 'claude' || p === 'codex' ? p : 'terminal';
}
/** Schluessel (tk) der Art fuer Untertitel: Claude Code, Codex, Terminal, Desktop. */
export const KIND_KEY = { claude: tk('prog.claude'), codex: tk('prog.codex'), terminal: tk('shell.kindTerminal'), desktop: tk('shell.kindDesktop'), vnc: tk('shell.kindVnc') };

/** Die Verbindungen mit diesem Programm. */
export const withProgram = (conns, program) => conns.filter((c) => c.kind === 'terminal' && programOf(c) === program);

/**
 * Was ein Tipp auf "Claude" oder "Codex" tut:
 *  open   genau eine passende Kachel: oeffnen (conn)
 *  choose mehrere: auswaehlen (list)
 *  create keine, aber Terminal-Verbindungen: eine Kachel aus einer davon anlegen (from: die erste, list: alle Terminals)
 *  add    gar keine Terminal-Verbindung: eine neue Verbindung anlegen
 */
export function launchTarget(conns, program) {
  const have = withProgram(conns, program);
  if (have.length === 1) return { action: 'open', conn: have[0] };
  if (have.length > 1) return { action: 'choose', list: have };
  const terms = conns.filter((c) => c.kind === 'terminal');
  if (terms.length) return { action: 'create', from: terms.find((c) => programOf(c) === 'shell') || terms[0], list: terms };
  return { action: 'add' };
}

// Arbeitsverzeichnis: wie auf dem Server (Integrations/Ssh/SshLaunch.cs); der Server prueft immer selbst
const WORKDIR = /^(~|~\/[A-Za-z0-9._+@%:,=/-]*|\/[A-Za-z0-9._+@%:,=/-]*|[A-Za-z0-9._+@%:,=][A-Za-z0-9._+@%:,=/-]*)$/;
export const MAX_WORKDIR = 200, MAX_CUSTOM = 500;
export const validWorkDir = (s) => { s = String(s ?? '').trim(); return s === '' || (s.length <= MAX_WORKDIR && WORKDIR.test(s)); };
export const validCustom = (s) => { s = String(s ?? '').trim(); return s.length <= MAX_CUSTOM && ![...s].some((ch) => ch < ' ' || ch === '\x7f'); };

/* ---------- Schnelltasten ---------- */
// sym = X11-Keysym (wie terminal.js keysymBytes), mods = {ctrl, shift}; text = ein Zeichen, das einfach getippt wird
const K = (id, label, aria, sym, mods = {}) => ({ id, label, aria, sym, mods });
const T = (id, label, aria, text) => ({ id, label, aria, text });
const KEYS = {
  esc: K('esc', 'Esc', tk('term.keyEsc'), 0xff1b),
  tab: K('tab', 'Tab', tk('term.keyTab'), 0xff09),
  shiftTab: K('shiftTab', '⇧Tab', tk('term.keyShiftTab'), 0xff09, { shift: true }),
  ctrlC: K('ctrlC', 'Strg+C', tk('term.keyCtrlC'), 0x63, { ctrl: true }),
  ctrlD: K('ctrlD', 'Strg+D', tk('term.keyCtrlD'), 0x64, { ctrl: true }),
  ctrlO: K('ctrlO', 'Strg+O', tk('term.keyCtrlO'), 0x6f, { ctrl: true }),
  up: K('up', '↑', tk('term.keyUp'), 0xff52),
  down: K('down', '↓', tk('term.keyDown'), 0xff54),
  left: K('left', '←', tk('term.keyLeft'), 0xff51),
  right: K('right', '→', tk('term.keyRight'), 0xff53),
  pgUp: K('pgUp', 'Bild↑', tk('term.keyPgUp'), 0xff55),
  pgDn: K('pgDn', 'Bild↓', tk('term.keyPgDn'), 0xff56),
  enter: K('enter', '⏎', tk('term.keyEnter'), 0xff0d),
  slash: T('slash', '/', tk('term.keySlash'), '/'),
  at: T('at', '@', tk('term.keyAt'), '@'),
};
// Reihenfolge = Reihenfolge in der Leiste; vorn, was man am Handy am haeufigsten braucht
const PRESETS = {
  claude: ['esc', 'shiftTab', 'tab', 'ctrlC', 'ctrlD', 'up', 'down', 'left', 'right', 'enter', 'pgUp', 'pgDn', 'ctrlO', 'slash', 'at'],
  codex: ['esc', 'tab', 'ctrlC', 'ctrlD', 'up', 'down', 'left', 'right', 'enter', 'pgUp', 'pgDn', 'slash', 'at'],
  shell: ['esc', 'tab', 'shiftTab', 'ctrlC', 'ctrlD', 'up', 'down', 'left', 'right', 'pgUp', 'pgDn'],
};
PRESETS.custom = PRESETS.shell;
// Die Tasten in der Zeile der Leiste (hoechstens sechs, damit sie auf ein Handy passen); alle anderen stehen im Blatt "Mehr Tasten"
const PRIMARY = {
  claude: ['esc', 'shiftTab', 'tab', 'ctrlC', 'up', 'down'],
  codex: ['esc', 'tab', 'ctrlC', 'up', 'down', 'enter'],
  shell: ['esc', 'tab', 'ctrlC', 'ctrlD', 'up', 'down'],
};
PRIMARY.custom = PRIMARY.shell;

/** Die Schnelltasten fuer ein Programm (Liste von {id, label, aria, sym, mods} oder {id, label, aria, text}). */
export const quickKeys = (program) => (PRESETS[PRESETS[program] ? program : 'shell']).map((id) => KEYS[id]);
/** Die Tasten fuer die Zeile der Leiste (eine Auswahl aus quickKeys); der Rest steht im Blatt "Mehr Tasten". */
export const primaryKeys = (program) => (PRIMARY[PRIMARY[program] ? program : 'shell']).map((id) => KEYS[id]);

/* ---------- Hinweise des Servers (Nachricht "Bereit", Feld notice) ---------- */
const NOTICE_KEYS = {
  'program-missing': tk('term.noticeProgramMissing'),
  'dir-missing': tk('term.noticeDirMissing'),
  'tmux-missing': tk('term.noticeTmuxMissing'),
  'probe-failed': tk('term.noticeProbeFailed'),
};
/** Die Texte zu den Hinweisen ("a b c" oder Liste); t = Uebersetzer. conn = die Verbindung (Programm und Verzeichnis fuer die Texte). Unbekannte Codes fallen weg. */
export function noticeTexts(notice, conn, t) {
  const codes = Array.isArray(notice) ? notice : String(notice || '').split(/\s+/).filter(Boolean);
  const program = programOf(conn);
  const name = program === 'claude' ? 'claude' : program === 'codex' ? 'codex' : '';
  return codes.filter((c) => NOTICE_KEYS[c]).map((c) => t(NOTICE_KEYS[c], { program: name, dir: (conn && conn.workDir) || '' }));
}
