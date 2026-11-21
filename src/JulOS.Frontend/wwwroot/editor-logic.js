/* =====================================================================
 * Reine Logik des Editors (files-editor.js), ohne DOM und ohne CodeMirror, damit sie sich mit node pruefen laesst (tests/frontend/editor-logic.test.mjs):
 * Zeilenenden, Sprache nach Dateiname, Pruefung von JSON (strikt, eigener Leser mit genauer Stelle) und YAML (Fehler des Parsers js-yaml).
 * ===================================================================== */

/** Zeilenende der Datei: 'crlf' wenn jedes \n ein \r\n ist, 'mixed' bei beidem, sonst 'lf'. Der Editor rechnet \r\n immer zu \n um, darum merken wir es uns und schreiben es so zurueck. */
export function detectEol(text) {
  const lf = (text.match(/\n/g) || []).length, crlf = (text.match(/\r\n/g) || []).length;
  return crlf === 0 ? 'lf' : crlf === lf ? 'crlf' : 'mixed';
}
export const withEol = (text, eol) => (eol === 'crlf' ? text.replace(/\n/g, '\r\n') : text);

/* ---------- Sprache ---------- */
const BY_EXT = {
  yml: 'yaml', yaml: 'yaml',
  json: 'json', webmanifest: 'json', geojson: 'json', jsonc: 'jsonc', json5: 'jsonc',
  md: 'markdown', markdown: 'markdown',
  sh: 'shell', bash: 'shell', zsh: 'shell', ksh: 'shell',
  ini: 'properties', cfg: 'properties', conf: 'properties', properties: 'properties', env: 'properties', service: 'properties', timer: 'properties', socket: 'properties',
  mount: 'properties', target: 'properties', desktop: 'properties', rules: 'properties', repo: 'properties', editorconfig: 'properties', gitconfig: 'properties',
  toml: 'toml', xml: 'xml', svg: 'xml', xsd: 'xml', xsl: 'xml', xslt: 'xml', plist: 'xml', csproj: 'xml', props: 'xml', targets: 'xml',
  py: 'python', js: 'javascript', mjs: 'javascript', cjs: 'javascript',
  log: 'log', out: 'log',
};
const BY_NAME = {
  '.bashrc': 'shell', '.bash_profile': 'shell', '.bash_aliases': 'shell', '.bash_logout': 'shell', '.profile': 'shell', '.zshrc': 'shell', '.zprofile': 'shell',
  'nginx.conf': 'nginx', dockerfile: 'dockerfile', containerfile: 'dockerfile', '.env': 'properties', '.gitconfig': 'properties', '.editorconfig': 'properties',
};
/**
 * Die Sprache (Schluessel fuer vendor/codemirror, entry.js) nach Dateiname; path und firstLine helfen bei Dateien ohne Endung. Unbekannt: 'text' (keine Farben).
 * Reihenfolge: ganzer Name, Dockerfile.xyz, .env.xyz, Endung (conf in einem nginx-Ordner ist nginx), Shebang.
 */
export function langFor(name, path = '', firstLine = '') {
  const n = String(name || '').toLowerCase();
  if (BY_NAME[n]) return BY_NAME[n];
  if (n.startsWith('dockerfile.') || n.endsWith('.dockerfile')) return 'dockerfile';
  if (n.startsWith('.env.')) return 'properties';
  const dot = n.lastIndexOf('.');
  const ext = dot > 0 ? n.slice(dot + 1) : '';
  if (ext === 'conf' && /(^|\/)nginx\//i.test(String(path).replaceAll('\\', '/'))) return 'nginx';
  if (BY_EXT[ext]) return BY_EXT[ext];
  const m = /^#!\s*(\S+)(?:\s+(?:-S\s+)?(\S+))?/.exec(firstLine || '');
  if (m) {
    const base = (x) => String(x || '').split('/').pop().toLowerCase();
    const p = base(m[1]) === 'env' ? base(m[2]) : base(m[1]);
    if (['sh', 'bash', 'zsh', 'ksh', 'dash'].includes(p)) return 'shell';
    if (p === 'python' || p === 'python3') return 'python';
    if (p === 'node') return 'javascript';
  }
  return 'text';
}
/**
 * Einrueckung der Datei: '\t' wenn mehr Zeilen mit Tab als mit Leerzeichen beginnen, sonst die kleinste Einrueckung (2 oder 4 Leerzeichen) der ersten eingerueckten Zeilen;
 * ohne Hinweis 4 bei Python, sonst 2 (YAML erlaubt ohnehin keine Tabs).
 */
export function detectIndent(text, lang = 'text') {
  let tabs = 0, spaces = 0, least = 0;
  const lines = text.slice(0, 20000).split('\n');
  for (const line of lines) {
    if (line[0] === '\t') tabs++;
    else if (line[0] === ' ') { spaces++; const n = /^ */.exec(line)[0].length; if (line.trim() && (!least || n < least)) least = n; }
  }
  if (tabs > spaces && lang !== 'yaml') return '\t';
  const n = least === 4 || least === 8 ? 4 : least === 2 || least === 6 ? 2 : lang === 'python' ? 4 : 2;
  return ' '.repeat(n);
}

/** Welche Sprachen die Pruefung kennt (Fehlerzeile und Markierung) */
export const checkKind = (lang) => (lang === 'json' ? 'json' : lang === 'yaml' ? 'yaml' : '');

/* ---------- JSON (strikt, RFC 8259) ---------- */
const NUM = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;
/**
 * Prueft JSON streng (keine Kommentare, keine Kommas am Ende, nur "..." als Zeichenkette). Gibt null (in Ordnung oder leer) oder { pos, code } zurueck: pos ist die Stelle im Text,
 * code einer von end, value, key, colon, comma, trailing, string, escape, control, data, deep (Texte: files.edit.err.json.<code>). Ein eigener Leser statt JSON.parse, weil die
 * Fehlermeldungen der Browser verschieden sind und keine verlaessliche Stelle nennen.
 */
export function checkJson(text) {
  if (!text.trim()) return null;
  const n = text.length;
  let i = 0;
  const fail = (code, pos = i) => { throw { pos, code }; };
  const ws = () => { while (i < n && (text[i] === ' ' || text[i] === '\t' || text[i] === '\n' || text[i] === '\r')) i++; };
  const str = () => {
    const start = i; i++;
    while (i < n) {
      const c = text.charCodeAt(i);
      if (c === 34) { i++; return; }
      if (c < 32) fail(c === 10 || c === 13 ? 'string' : 'control', c === 10 || c === 13 ? start : i);
      if (c === 92) {
        const e = text[i + 1];
        if (e !== undefined && '"\\/bfnrt'.includes(e)) i += 2;
        else if (e === 'u' && /^[0-9a-fA-F]{4}$/.test(text.slice(i + 2, i + 6))) i += 6;
        else fail('escape', i);
      } else i++;
    }
    fail('string', start);
  };
  const lit = (word) => { if (text.startsWith(word, i)) i += word.length; else fail('value'); };
  const value = (depth) => {
    ws();
    if (i >= n) fail('end', n);
    if (depth > 200) fail('deep');
    const c = text[i];
    if (c === '{') {
      i++; ws();
      if (text[i] === '}') { i++; return; }
      for (;;) {
        ws();
        if (i >= n) fail('end', n);
        if (text[i] !== '"') fail(text[i] === '}' ? 'trailing' : 'key');
        str(); ws();
        if (i >= n) fail('end', n);
        if (text[i] !== ':') fail('colon');
        i++; value(depth + 1); ws();
        if (i >= n) fail('end', n);
        if (text[i] === ',') { i++; continue; }
        if (text[i] === '}') { i++; return; }
        fail('comma');
      }
    } else if (c === '[') {
      i++; ws();
      if (text[i] === ']') { i++; return; }
      for (;;) {
        ws();
        if (text[i] === ']') fail('trailing');
        value(depth + 1); ws();
        if (i >= n) fail('end', n);
        if (text[i] === ',') { i++; continue; }
        if (text[i] === ']') { i++; return; }
        fail('comma');
      }
    } else if (c === '"') str();
    else if (c === 't') lit('true');
    else if (c === 'f') lit('false');
    else if (c === 'n') lit('null');
    else if (c === '-' || (c >= '0' && c <= '9')) { NUM.lastIndex = i; const m = NUM.exec(text); if (!m) fail('value'); i += m[0].length; }
    else fail('value');
  };
  try { value(0); ws(); if (i < n) fail('data'); } catch (e) { if (e && typeof e.pos === 'number') return e; throw e; }
  return null;
}

/* ---------- YAML (Parser js-yaml, nur geladen, wenn eine YAML-Datei offen ist) ---------- */
/**
 * Prueft YAML mit loadAll von js-yaml (mehrere Dokumente mit --- sind erlaubt). Gibt null oder { line, col, reason } zurueck (aus e.mark, 0-basiert; reason ist der englische Text des Parsers).
 * Ein unbekannter Tag (!Ref, !vault ...) ist kein Fehler: viele Programme benutzen eigene Tags, der Parser kennt sie nicht und wuerde sonst jede solche Datei bemaengeln.
 */
export function checkYaml(text, loadAll) {
  if (!text.trim()) return null;
  try { loadAll(text); return null; }
  catch (e) {
    const mark = e && e.mark;
    if (!mark || typeof mark.line !== 'number') return { line: 0, col: 0, reason: String((e && e.message) || e) };
    const reason = String(e.reason || e.message || '');
    if (/unknown\b[^()]*\btag/i.test(reason)) return null;
    return { line: mark.line, col: mark.column || 0, reason };
  }
}

/** Zeile (0-basiert) und Spalte zur Stelle im Text, fuer JSON-Fehler */
export function lineCol(text, pos) {
  const upto = text.slice(0, Math.max(0, Math.min(pos, text.length)));
  const line = (upto.match(/\n/g) || []).length;
  return { line, col: upto.length - (upto.lastIndexOf('\n') + 1) };
}
