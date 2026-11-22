/* =====================================================================
 * Symbolfamilie und Zuordnung (docs/ICONS.md): welches Symbol und welche Farbe ein Programm, eine Verbindung oder ein Host bekommt.
 *  - GLYPHS: eigene, einfache Strichsymbole (24 px, mit currentColor gezeichnet), alle von Hand gemalt; keine Marken-Logos. Formen mit Flaeche tragen
 *    class="f", damit der Stil "gefuellt" sie tonen kann (icons.css).
 *  - iconFor / appIconFor / hostIconFor: die EINE Stelle, die das Symbol bestimmt (Kacheln, Dock, Pillen, Fenstertitel, Handy). Reine Funktionen, kein DOM,
 *    getestet in tests/frontend/icons.test.mjs.
 *  - Der Nutzer kann je Verbindung Symbol und Farbe ueberschreiben (Einstellung "appearance", icons.over); ohne Ueberschreibung gilt "automatisch".
 * ===================================================================== */
import { tk } from './i18n.js';
import { tileKind, programOf } from './terminal-programs.js';

const F = ' class="f"';
/** Alle Symbole. Die ersten stehen schon lange im Desktop (Menues, Kacheln), die letzten sind fuer Betriebssysteme, Hosts und Programme neu. */
export const GLYPHS = {
  home: `<path${F} d="M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/>`,
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
  monitor: `<rect${F} x="2.5" y="3.5" width="19" height="13" rx="2"/><path d="M8 21h8M12 16.5V21"/>`,
  terminal: '<path d="m4 17 6-6-6-6M12 19h8"/>',
  spark: `<path${F} d="M12 3l1.9 5.6L20 10l-6.1 1.4L12 17l-1.9-5.6L4 10l6.1-1.4zM19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8z"/>`,
  code: '<path d="m8 7-5 5 5 5M16 7l5 5-5 5"/>',
  sliders: '<path d="M21 4h-7M10 4H3M21 12h-9M8 12H3M21 20h-5M12 20H3M14 2v4M8 10v4M16 18v4"/>',
  folder: `<path${F} d="M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.7-.9l-.8-1.2A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z"/>`,
  server: `<rect${F} x="3" y="4" width="18" height="7" rx="2"/><rect${F} x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/>`,
  cloud: `<path${F} d="M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9z"/>`,
  activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  box: `<path${F} d="m21 8-9-5-9 5v8l9 5 9-5z"/><path d="m3.3 7.5 8.7 5 8.7-5M12 22V12.5"/>`,
  globe: `<circle${F} cx="12" cy="12" r="10"/><path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>`,
  power: '<path d="M12 2v10M18.4 6.6a9 9 0 1 1-12.77.04"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  grid: `<path${F} d="M3 3h7v7H3zM14 3h7v7h-7zM14 14h7v7h-7zM3 14h7v7H3z"/>`,
  x: '<path d="M18 6 6 18M6 6l12 12"/>',
  star: `<path${F} d="m12 3 2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z"/>`,
  minus: '<path d="M5 12h14"/>',
  square: '<rect x="5" y="5" width="14" height="14" rx="2"/>',
  restore: '<rect x="4.5" y="8.5" width="11" height="11" rx="2"/><path d="M8.5 8.5V6.5a2 2 0 0 1 2-2h7a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2h-2"/>',
  left: '<path d="m15 18-6-6 6-6"/>',
  right: '<path d="m9 18 6-6-6-6"/>',
  edit: '<path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>',
  shield: `<path${F} d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>`,
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
  play: '<path d="M7 4.5v15l12-7.5z"/>',
  key: `<circle${F} cx="8" cy="15" r="4"/><path d="m10.8 12.2 8-8M16 7l3 3"/>`,
  cpu: `<rect${F} x="5" y="5" width="14" height="14" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3"/>`,
  ram: '<rect x="3" y="7" width="18" height="10" rx="1.5"/><path d="M7 7V5M12 7V5M17 7V5M7 17v2M12 17v2M17 17v2"/>',
  disk: `<rect${F} x="3" y="5" width="18" height="14" rx="2"/><path d="M7 14.5h6"/><circle cx="17" cy="14.5" r=".6"/>`,
  network: '<path d="M7 4v16m0 0-3-3m3 3 3-3M17 20V4m0 0-3 3m3-3 3 3"/>',
  chev: '<path d="m6 9 6 6 6-6"/>',
  refresh: '<path d="M20 11a8 8 0 0 0-14.5-4.5L4 8M4 4v4h4M4 13a8 8 0 0 0 14.5 4.5L20 16M20 20v-4h-4"/>',
  keyboard: '<rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>',
  file: `<path${F} d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5M9 13h6M9 17h6"/>`,
  download: '<path d="M12 3v12m0 0-4-4m4 4 4-4M5 21h14"/>',
  up: '<path d="m18 15-6-6-6 6"/>',
  check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
  popout: '<path d="M14 4h6v6M20 4l-8.5 8.5M18 14v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4"/>',
  blank: '',
  // neu: Betriebssysteme, Hosts, Programme (neutrale Zeichen, keine Logos)
  windows: `<rect${F} x="3" y="4" width="18" height="16" rx="2"/><path d="M12 4v16M3 12h18"/>`,
  vnc: `<rect${F} x="2.5" y="3.5" width="19" height="13" rx="2"/><path d="M8 21h8M12 16.5V21"/><path d="m10 6.8 5.2 3.5-2.3.7-1.3 2.2z"/>`,
  linux: `<circle${F} cx="12" cy="12" r="9.5"/><path d="m8.2 9.8 3 2.2-3 2.2M13.2 14.6h3.2"/>`,
  layers: `<path${F} d="m12 3 9 4.5-9 4.5-9-4.5z"/><path d="m3 12 9 4.5 9-4.5M3 16.5 12 21l9-4.5"/>`,
  smarthome: `<path${F} d="M3 11 12 3.5 21 11v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z"/><path d="M8.6 14.6a5 5 0 0 1 6.8 0M6.2 12a8.4 8.4 0 0 1 11.6 0M12 18h.01"/>`,
  nas: `<rect${F} x="3" y="4" width="18" height="16" rx="2"/><path d="M7 9h6M7 12.5h6M7 16h6M17 9h.01M17 12.5h.01M17 16h.01"/>`,
  laptop: `<rect${F} x="5" y="5" width="14" height="10" rx="1.5"/><path d="M2.5 19h19"/>`,
  containers: `<rect${F} x="3" y="12" width="8" height="8" rx="1"/><rect${F} x="13" y="12" width="8" height="8" rx="1"/><rect${F} x="8" y="4" width="8" height="8" rx="1"/>`,
  bolt: `<path${F} d="M13 2 4.5 13.5H11L10 22l8.5-11.5H12z"/>`,
  database: `<ellipse${F} cx="12" cy="5.5" rx="8" ry="3"/><path d="M4 5.5v13c0 1.7 3.6 3 8 3s8-1.3 8-3v-13M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>`,
  lock: `<rect${F} x="4.5" y="10.5" width="15" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/>`,
  phone: `<rect${F} x="6.5" y="2.5" width="11" height="19" rx="2.5"/><path d="M11 18.5h2"/>`,
};

/** Die Symbole, die man je Verbindung waehlen kann (Reihenfolge der Auswahl); Beschriftung als Schluessel fuer t() */
export const PICK_KEYS = ['monitor', 'vnc', 'windows', 'terminal', 'linux', 'spark', 'code', 'bolt', 'server', 'layers', 'smarthome', 'nas', 'cpu', 'laptop', 'phone', 'containers', 'database', 'cloud', 'globe', 'lock', 'folder', 'file', 'activity', 'grid', 'shield', 'key', 'star', 'home'];
export const GLYPH_LABEL = {
  monitor: tk('ico.monitor'), vnc: tk('ico.vnc'), windows: tk('ico.windows'), terminal: tk('ico.terminal'), linux: tk('ico.linux'), spark: tk('ico.spark'), code: tk('ico.code'), bolt: tk('ico.bolt'),
  server: tk('ico.server'), layers: tk('ico.layers'), smarthome: tk('ico.smarthome'), nas: tk('ico.nas'), cpu: tk('ico.cpu'), laptop: tk('ico.laptop'), phone: tk('ico.phone'), containers: tk('ico.containers'),
  database: tk('ico.database'), cloud: tk('ico.cloud'), globe: tk('ico.globe'), lock: tk('ico.lock'), folder: tk('ico.folder'), file: tk('ico.file'), activity: tk('ico.activity'), grid: tk('ico.grid'),
  shield: tk('ico.shield'), key: tk('ico.key'), star: tk('ico.star'), home: tk('ico.home'),
};

/** Das SVG eines Symbols (leer, wenn es das Symbol nicht gibt). cls: zusaetzliche Klasse (der Desktop setzt "dk-i") */
export const glyph = (key, cls = '') => `<svg class="fi${cls ? ' ' + cls : ''}" viewBox="0 0 24 24" aria-hidden="true">${GLYPHS[key] || ''}</svg>`;

/**
 * Farben der Kacheln als Verlauf [oben, unten]. Beide Enden tragen ein weisses Symbol mit mindestens 3:1 (getestet).
 * Reihenfolge = Reihenfolge der Auswahl.
 */
export const COLORS = {
  violet: ['#7b78ee', '#5160e6'], blue: ['#3a99dc', '#2f5fe0'], teal: ['#2a8f8a', '#25758f'], mint: ['#2fa08c', '#1c7a70'], sage: ['#3f8a76', '#2c6a5c'],
  gold: ['#c28418', '#a86408'], clay: ['#c0683a', '#94491f'], red: ['#cf4f55', '#a8303d'], magenta: ['#cf5fae', '#8c45d6'], grey: ['#7d849f', '#555b78'], slate: ['#5b6275', '#2c3142'],
};
export const COLOR_LABEL = {
  violet: tk('col.violet'), blue: tk('col.blue'), teal: tk('col.teal'), mint: tk('col.mint'), sage: tk('col.sage'), gold: tk('col.gold'), clay: tk('col.clay'),
  red: tk('col.red'), magenta: tk('col.magenta'), grey: tk('col.grey'), slate: tk('col.slate'),
};

/** Programme des Desktops (keine Verbindung): Symbol und Farbe. */
const APP_ICONS = {
  home: ['home', 'violet'], desktop: ['monitor', 'blue'], vnc: ['vnc', 'teal'], terminal: ['terminal', 'slate'], claude: ['spark', 'clay'], codex: ['code', 'sage'],
  settings: ['sliders', 'grey'], files: ['folder', 'gold'], hosts: ['server', 'mint'], docker: ['containers', 'blue'], widgets: ['grid', 'clay'], caddy: ['globe', 'mint'],
  ddns: ['cloud', 'blue'], monitor: ['activity', 'magenta'], launcher: ['grid', 'violet'],
};
const pack = (key, color, auto = true) => ({ key, color, g: COLORS[color], auto });
/** Symbol und Farbe eines Programms des Desktops (home, files, hosts, claude ...) */
export const appIconFor = (id) => { const [k, c] = APP_ICONS[id] || ['grid', 'violet']; return pack(k, c); };

/**
 * Das Betriebssystem eines Hosts aus dem, was JulOS schon weiss (Host-Liste: os, hostname, agent.platform). Leer, wenn es nichts Brauchbares gibt.
 * @returns {'windows'|'proxmox'|'homeassistant'|'synology'|'raspberry'|'macos'|'debian'|'ubuntu'|'fedora'|'arch'|'alpine'|'linux'|''}
 */
export function osFamily(host) {
  if (!host) return '';
  const os = String(host.os || ''), plat = String(host.agent && host.agent.platform || ''), name = String(host.hostname || '');
  const any = `${os} ${plat}`;
  if (/proxmox|\bpve\b/i.test(any) || /^pve\d*$/i.test(name)) return 'proxmox';
  if (/home ?assistant|hassio|\bhaos\b/i.test(any)) return 'homeassistant';
  if (/synology|\bdsm\b/i.test(any)) return 'synology';
  if (/windows|win32|^win/i.test(any.trim())) return 'windows';
  if (/raspbian|raspberry/i.test(any)) return 'raspberry';
  if (/mac ?os|darwin/i.test(any)) return 'macos';
  for (const f of ['debian', 'ubuntu', 'fedora', 'alpine']) if (new RegExp(f, 'i').test(any)) return f;
  if (/\barch\b|manjaro/i.test(any)) return 'arch';
  return /linux|gnu|^linux/i.test(any) ? 'linux' : '';
}
const OS_GLYPH = { windows: ['windows', 'blue'], proxmox: ['layers', 'gold'], homeassistant: ['smarthome', 'teal'], synology: ['nas', 'grey'], raspberry: ['cpu', 'magenta'], macos: ['laptop', 'grey'], debian: ['linux', 'red'], ubuntu: ['linux', 'clay'], fedora: ['linux', 'blue'], arch: ['linux', 'teal'], alpine: ['linux', 'slate'], linux: ['linux', 'slate'] };
/** Symbol eines Hosts nach seinem Betriebssystem (unbekannt: Server) */
export function hostIconFor(host) { const g = OS_GLYPH[osFamily(host)]; return g ? pack(g[0], g[1]) : pack('server', 'mint'); }

// Eigener Befehl einer Terminal-Kachel: das erste Wort sagt, was es ist
const COMMAND_GLYPH = [
  [/^(docker|podman|docker-compose|lazydocker)$/, 'containers'], [/^(htop|top|btop|glances|nvtop)$/, 'activity'], [/^(vim|vi|nvim|nano|emacs|micro|helix|hx)$/, 'edit'],
  [/^(python3?|node|deno|bun|irb|ipython|lua)$/, 'code'], [/^(mc|ranger|nnn|yazi)$/, 'folder'], [/^(tmux|screen|zellij)$/, 'grid'], [/^(tail|less|journalctl|dmesg|cat)$/, 'file'],
  [/^(claude)$/, 'spark'], [/^(codex)$/, 'code'], [/^(caddy)$/, 'globe'], [/^(psql|mysql|sqlite3|redis-cli)$/, 'database'],
];
const commandWord = (cmd) => { const w = String(cmd || '').trim().split(/\s+/)[0] || ''; return w.slice(w.lastIndexOf('/') + 1).toLowerCase(); };

/** Eine Ueberschreibung ({ k, c }) nur mit Teilen, die es in der Familie gibt */
export function cleanOverride(o) {
  if (!o || typeof o !== 'object') return null;
  const k = PICK_KEYS.includes(o.k) ? o.k : undefined, c = Object.hasOwn(COLORS, o.c) ? o.c : undefined;
  return k || c ? { ...(k ? { k } : {}), ...(c ? { c } : {}) } : null;
}

/**
 * Symbol und Farbe einer Verbindung. Reihenfolge: 1. Ueberschreibung des Nutzers (Symbol und Farbe je fuer sich), 2. Art und Programm (Claude, Codex, eigener Befehl,
 * Shell, VNC, RDP), 3. das Betriebssystem des Hosts, wenn es etwas sagt (RDP-Desktop eines Windows-Rechners, Shell auf Proxmox, Home Assistant, Synology, Windows).
 * @param {{conn?: object, host?: object, override?: {k?: string, c?: string}}} subject
 * @returns {{key: string, color: string, g: string[], auto: boolean}} auto: nichts wurde ueberschrieben
 */
export function iconFor({ conn, host, override } = {}) {
  const kind = tileKind(conn);               // claude | codex | terminal | desktop | vnc
  const fam = osFamily(host), special = ['windows', 'proxmox', 'homeassistant', 'synology'].includes(fam);
  let key, color;
  if (kind === 'claude' || kind === 'codex') [key, color] = APP_ICONS[kind];
  else if (kind === 'vnc') [key, color] = APP_ICONS.vnc;
  else if (kind === 'desktop') [key, color] = fam === 'windows' ? OS_GLYPH.windows : APP_ICONS.desktop;
  else if (conn && programOf(conn) === 'custom') {
    const hit = COMMAND_GLYPH.find(([re]) => re.test(commandWord(conn.customCommand)));
    [key, color] = [hit ? hit[1] : 'bolt', 'slate'];
  } else [key, color] = special ? [OS_GLYPH[fam][0], 'slate'] : APP_ICONS.terminal;       // Shell: auf Proxmox, Home Assistant, Synology, Windows das Zeichen des Hosts, sonst das Terminal
  const o = cleanOverride(override);
  const out = pack(o && o.k || key, o && o.c || color, !o);
  return out;
}
