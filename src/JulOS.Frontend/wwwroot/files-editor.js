/* =====================================================================
 * Editor der Dateien-App (docs/FILES-EXPLORER.md Teil 4, docs/FILES-VIEWER.md Teil B): CodeMirror 6 (vendor/codemirror, ein vorgebautes Bundle, wird erst beim ersten Oeffnen geladen)
 * mit Farben je Dateityp, Zeilennummern, Suchen und Ersetzen, Falten, Klammern, Einrueckung mit Tab, Rueckgaengig und einer Pruefung von JSON und YAML.
 * Gespeichert wird nicht hier: onSave(text) ist die bestaetigte Schreib-Aktion der App (prepare, Dialog, Upload) und gibt true zurueck, wenn die Datei geschrieben wurde.
 * Der Text steht nur im Editor (CodeMirror setzt ihn als Text, nie als HTML). Binaerdateien oeffnen nie hier (die App prueft Endung, Groesse und die Antwort des Servers).
 * Reine Logik (Zeilenenden, Sprache, Pruefung) steht in editor-logic.js; die Farben sind CSS-Variablen in editor.css und folgen Hell und Dunkel der App ohne Zutun.
 * ===================================================================== */
import { t, tk } from './i18n.js';
import { h, modal, noteEl } from './files-ui.js';
import { safeName } from './files-explorer.js';
import { detectEol, withEol, langFor, checkKind, checkJson, checkYaml, lineCol, detectIndent } from './editor-logic.js';

export { detectEol, withEol };

/** Ordner des Bundles; der Name ist die Version von @codemirror/view (tools/vendor-codemirror.sh, vendor/README.md) */
const CM_DIR = new URL('./vendor/codemirror/6.43.15/', import.meta.url).href;
let cmPromise = null, yamlPromise = null;
const loadCm = () => (cmPromise ||= import(CM_DIR + 'codemirror.mjs').catch((e) => { cmPromise = null; throw e; }));
const loadYaml = () => (yamlPromise ||= import(CM_DIR + 'js-yaml.mjs').catch((e) => { yamlPromise = null; throw e; }));

const JSON_ERR = { end: tk('files.edit.err.json.end'), value: tk('files.edit.err.json.value'), key: tk('files.edit.err.json.key'), colon: tk('files.edit.err.json.colon'), comma: tk('files.edit.err.json.comma'),
  trailing: tk('files.edit.err.json.trailing'), string: tk('files.edit.err.json.string'), escape: tk('files.edit.err.json.escape'), control: tk('files.edit.err.json.control'), data: tk('files.edit.err.json.data'), deep: tk('files.edit.err.json.deep') };
const LANG_NAMES = { yaml: 'YAML', json: 'JSON', jsonc: 'JSON', markdown: 'Markdown', shell: 'Shell', properties: 'INI / Config', toml: 'TOML', nginx: 'nginx', dockerfile: 'Dockerfile', xml: 'XML', python: 'Python', javascript: 'JavaScript', log: 'Log' };
/** Texte der Suche, des Faltens und der Zeilensuche von CodeMirror (die englischen Woerter sind die Schluessel) */
const PHRASES = [['Find', tk('files.edit.cm.find')], ['Replace', tk('files.edit.cm.replace')], ['next', tk('files.edit.cm.next')], ['previous', tk('files.edit.cm.previous')], ['all', tk('files.edit.cm.all')],
  ['match case', tk('files.edit.cm.matchCase')], ['regexp', tk('files.edit.cm.regexp')], ['by word', tk('files.edit.cm.byWord')], ['replace', tk('files.edit.cm.replaceOne')], ['replace all', tk('files.edit.cm.replaceAll')],
  ['close', tk('files.edit.cm.close')], ['current match', tk('files.edit.cm.currentMatch')], ['replaced $ matches', tk('files.edit.cm.replacedN')], ['replaced match on line $', tk('files.edit.cm.replacedOne')],
  ['on line', tk('files.edit.cm.onLine')], ['Go to line', tk('files.edit.cm.gotoLine')], ['go', tk('files.edit.cm.go')], ['Fold line', tk('files.edit.cm.fold')], ['Unfold line', tk('files.edit.cm.unfold')],
  ['Folded lines', tk('files.edit.cm.folded')], ['Unfolded lines', tk('files.edit.cm.unfolded')], ['to', tk('files.edit.cm.to')], ['folded code', tk('files.edit.cm.foldedCode')], ['unfold', tk('files.edit.cm.unfoldIt')],
  ['Control character', tk('files.edit.cm.control')]];
const BRACKET_LANGS = new Set(['json', 'jsonc', 'javascript', 'python']);        // Klammern und Anfuehrungszeichen schliessen nur im Quelltext, nicht in Konfigurationsdateien und Prosa

/** Farben: nur Namen, die Werte stehen als Variablen in editor.css (hell und dunkel) */
function highlight(cm) {
  const T = cm.tags, extra = {};
  const log = ['logError', 'logWarn', 'logInfo', 'logDebug', 'logTime'];
  for (const k of log) extra[k] = cm.Tag.define();
  const v = (n) => `var(--ed-${n})`;
  const style = cm.HighlightStyle.define([
    { tag: [T.keyword, T.controlKeyword, T.operatorKeyword, T.definitionKeyword, T.moduleKeyword, T.modifier, T.self, T.tagName], color: v('kw') },
    { tag: [T.string, T.special(T.string), T.regexp, T.escape, T.character, T.attributeValue, T.inserted, T.monospace], color: v('str') },
    { tag: [T.number, T.integer, T.float, T.bool, T.null, T.atom, T.unit, T.color, T.changed], color: v('num') },
    { tag: [T.comment, T.lineComment, T.blockComment, T.docComment, T.quote], color: v('cmt'), fontStyle: 'italic' },
    { tag: [T.propertyName, T.attributeName, T.definition(T.propertyName), T.labelName], color: v('prop') },
    { tag: [T.typeName, T.className, T.namespace], color: v('type') },
    { tag: [T.function(T.variableName), T.function(T.propertyName), T.definition(T.variableName)], color: v('fn') },
    { tag: [T.heading, T.heading1, T.heading2, T.heading3, T.heading4, T.heading5, T.heading6], color: v('kw'), fontWeight: '700' },
    { tag: T.strong, fontWeight: '700' }, { tag: T.emphasis, fontStyle: 'italic' }, { tag: T.strikethrough, textDecoration: 'line-through' },
    { tag: [T.link, T.url], color: v('prop'), textDecoration: 'underline' },
    { tag: [T.meta, T.processingInstruction, T.punctuation, T.separator, T.operator, T.contentSeparator, T.bracket], color: v('punct') },
    { tag: [T.invalid, T.deleted], color: v('bad') },
    { tag: extra.logError, color: v('bad'), fontWeight: '700' }, { tag: extra.logWarn, color: v('num'), fontWeight: '700' }, { tag: extra.logInfo, color: v('prop') },
    { tag: extra.logDebug, color: v('cmt') }, { tag: extra.logTime, color: v('cmt') },
  ]);
  return { style, extra };
}
/** Logdateien: Zeitstempel, Stufen (Fehler, Warnung, Info, Debug) farbig; der Rest bleibt Text */
function logLanguage(cm, extra) {
  const word = (re, name) => (s) => s.match(re) ? name : null;
  const rules = [
    (s) => (s.match(/^\d{4}-\d\d-\d\d[T ]\d\d:\d\d:\d\d(?:[.,]\d+)?(?:Z|[+-]\d\d:?\d\d)?/) || s.match(/^[A-Z][a-z]{2} {1,2}\d{1,2} \d\d:\d\d:\d\d/) || s.match(/^\d\d:\d\d:\d\d(?:[.,]\d+)?/) ? 'logTime' : null),
    word(/^(?:FATAL|CRITICAL|CRIT|EMERG|ALERT|ERROR|ERR|FAIL(?:ED|URE)?|Exception|Traceback)\b/i, 'logError'),
    word(/^WARN(?:ING)?\b/i, 'logWarn'), word(/^(?:INFO|NOTICE)\b/i, 'logInfo'), word(/^(?:DEBUG|TRACE)\b/i, 'logDebug'),
  ];
  return cm.StreamLanguage.define({
    name: 'log',
    tokenTable: Object.fromEntries(Object.keys(extra).map((k) => [k, extra[k]])),
    token(stream) {
      if (stream.eatSpace()) return null;
      for (const r of rules) { const tok = r(stream); if (tok) return tok; }
      if (!stream.match(/^[\w-]+/)) stream.next();
      return null;
    },
  });
}

/**
 * @param {{kit?: boolean, name: string, path: string, text: string, sizeText?: string, readOnly?: boolean, why?: string, key?: string, onSave: (text: string) => Promise<boolean>}} o
 * why: warum nur lesen (Text unter dem Titel). Gibt { promise, close, modified, focus } zurueck; das Versprechen endet, wenn der Editor zu ist.
 */
export function openEditor({ kit = false, name, path, text, sizeText = '', readOnly = false, why = '', key = '', onSave }) {
  const eol = detectEol(text);
  const norm = text.replace(/\r\n/g, '\n');
  const lang = langFor(name, path, norm.slice(0, norm.indexOf('\n') < 0 ? 200 : Math.min(norm.indexOf('\n'), 200)));
  const kind = checkKind(lang);
  let view = null, original = null, busy = false, closing = false, tabFree = false, problem = null, wrap = kit;
  const mark = h('span', { class: 'f-ed-mark', hidden: true, role: 'status', text: t('files.edit.changed') });
  const notes = [];
  if (readOnly && why) notes.push(noteEl(why, 'hint'));
  if (!readOnly && eol === 'mixed') notes.push(noteEl(t('files.edit.mixedEol'), 'hint'));
  const host = h('div', { class: 'f-ed' + (kit ? ' kit' : ''), 'data-lang': lang }, h('p', { class: 'f-ed-load', role: 'status', text: t('files.edit.loading') }));
  const btn = (label, onclick, extra = {}) => h('button', { type: 'button', class: 'f-ed-btn', onmousedown: (e) => e.preventDefault(), onclick, ...extra, text: label });   // mousedown: der Fokus bleibt im Text (Tastatur auf dem Handy)
  const bUndo = btn(t('files.edit.undo'), () => view && cmApi.undo(view), { disabled: true });
  const bRedo = btn(t('files.edit.redo'), () => view && cmApi.redo(view), { disabled: true });
  const bFind = btn(t('files.edit.find'), () => { if (view) { cmApi.openSearchPanel(view); } });
  const bWrap = btn(t('files.edit.wrap'), () => { wrap = !wrap; applyWrap(); }, { 'aria-pressed': String(wrap) });
  const msg = h('button', { type: 'button', class: 'f-ed-msg', hidden: true, onclick: () => jump() });
  const pos = h('span', { class: 'f-ed-pos', text: '' });
  const info = h('span', { class: 'f-ed-info', text: [LANG_NAMES[lang] || t('files.edit.plain'), eol === 'crlf' ? 'CRLF' : 'LF'].join(' · ') });
  const bar = h('div', { class: 'f-ed-bar' }, h('span', { class: 'f-ed-tools' }, ...(readOnly ? [bFind, bWrap] : [bUndo, bRedo, bFind, bWrap])), msg, h('span', { class: 'f-ed-meta' }, pos, info));
  let cmApi = null;
  const modified = () => !!view && !view.state.doc.eq(original);
  const refresh = () => {
    mark.hidden = !modified(); dlg.setDisabled('save', readOnly || busy || !modified());
    if (view && !readOnly) { bUndo.disabled = !cmApi.undoDepth(view.state); bRedo.disabled = !cmApi.redoDepth(view.state); }
  };
  const showStatus = () => {
    if (!view) return;
    const head = view.state.selection.main.head, ln = view.state.doc.lineAt(head);
    pos.textContent = t('files.edit.pos', { line: ln.number, col: head - ln.from + 1 });
    msg.hidden = !(tabFree || kind);
    msg.classList.toggle('bad', !tabFree && !!problem);
    msg.disabled = tabFree || !problem;
    msg.title = msg.textContent = tabFree ? t('files.edit.tabFree') : problem ? t('files.edit.check.err', { line: problem.line + 1, msg: problem.msg }) : kind ? t('files.edit.check.ok', { lang: LANG_NAMES[lang] }) : '';
  };
  function jump() { if (view && problem) { view.dispatch({ selection: { anchor: problem.from }, scrollIntoView: true }); view.focus(); } }
  const wrapC = { c: null };
  function applyWrap() {
    bWrap.setAttribute('aria-pressed', String(wrap));
    if (view) view.dispatch({ effects: wrapC.c.reconfigure(wrap ? cmApi.EditorView.lineWrapping : []) });
  }

  /** Diagnose fuer die Markierung im Text: ab der Stelle bis zum Ende der Zeile (YAML) oder das eine Zeichen (JSON) */
  function diagnose(state, line0, col, msgText, oneChar) {
    const doc = state.doc, ln = doc.line(Math.min(line0 + 1, doc.lines));
    let from = Math.min(ln.from + col, ln.to), to = oneChar ? from + 1 : ln.to;
    if (from >= ln.to) { from = Math.max(ln.from, ln.to - 1); to = ln.to; }
    to = Math.min(Math.max(to, from), doc.length);
    problem = { line: ln.number - 1, from, msg: msgText };
    return [{ from, to, severity: 'error', message: msgText }];
  }
  async function lint(v) {
    const state = v.state, text = state.doc.toString();
    let out = [];
    if (kind === 'json') {
      const r = checkJson(text);
      if (r) { const p = lineCol(text, Math.min(r.pos, Math.max(0, text.length - 1))); out = diagnose(state, p.line, p.col, t(JSON_ERR[r.code]), true); }
    } else {
      let r = null;
      try { r = checkYaml(text, (await loadYaml()).loadAll); } catch { r = null; }          // der Parser laesst sich nicht laden: keine Pruefung, der Editor geht trotzdem
      if (view !== v) return [];
      if (r) out = diagnose(state, r.line, r.col, t('files.edit.err.yaml', { reason: r.reason }), false);
    }
    if (!out.length) problem = null;
    showStatus();
    return out;
  }

  // Auf dem Desktop ein eigenes, in der Groesse veraenderbares Fenster (key: je Datei eins, ein zweites Oeffnen holt es nach vorn); auf dem Handy wie bisher ein Blatt
  const title = h('span', { class: 'f-ed-title' }, h('span', { class: 'f-ed-name', text: safeName(name) }), ' ', mark);
  const dlg = modal({ title: '', kit, id: 'edit', key, win: { width: 960, height: 700, resizable: true }, content: [h('p', { class: 'f-sub', text: `${safeName(path)}${sizeText ? ' · ' + sizeText : ''}` }), ...notes, host, bar], actions: [
    { id: 'close', label: t('common.close'), kind: 'ghost', keepOpen: true, onclick: () => tryClose() },
    ...(readOnly ? [] : [{ id: 'save', label: t('files.edit.save'), kind: 'primary', keepOpen: true, disabled: true, onclick: () => save() }]),
  ] });
  dlg.el.classList.add('f-edit');
  dlg.setTitle(title);
  if (readOnly) dlg.setDisabled('save', true);

  dlg.el.addEventListener('keydown', (e) => { if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 's') { e.preventDefault(); save(); } });
  dlg.el.addEventListener('cancel', (e) => { e.preventDefault(); tryClose(); });                  // Escape (zweimal), Schliessen: fragt bei ungespeicherten Aenderungen nach
  const stop = (e) => { if (modified()) e.preventDefault(); };
  window.addEventListener('beforeunload', stop);
  dlg.promise.then(() => { window.removeEventListener('beforeunload', stop); if (view) { view.destroy(); view = null; } });

  start();
  async function start() {
    let cm;
    try { cm = await loadCm(); } catch { host.replaceChildren(h('p', { class: 'f-ed-load f-msg warn', role: 'alert', text: t('files.edit.loadFail') })); return; }
    if (!dlg.el.isConnected) return;                                // schon geschlossen, bevor der Editor geladen war
    cmApi = cm;
    const { style, extra } = highlight(cm);
    const unit = detectIndent(norm, lang);
    const editorLang = lang === 'log' ? logLanguage(cm, extra) : cm.language(lang);
    wrapC.c = new cm.Compartment();
    const tabKeys = [
      { key: 'Tab', run: (v) => { if (tabFree) return false; if (v.state.selection.ranges.some((r) => !r.empty)) return cm.indentMore(v); v.dispatch(v.state.replaceSelection(unit), { scrollIntoView: true, userEvent: 'input' }); return true; }, shift: (v) => (tabFree ? false : cm.indentLess(v)) },
      // Escape macht Tab frei (der Fokus darf weiter, wie bei Lesegeraeten und Tastaturnutzern ueblich); ein zweites Escape schliesst den Editor (das Fenster fragt bei Aenderungen nach)
      { key: 'Escape', run: () => { if (tabFree) return false; tabFree = true; showStatus(); return true; } },
    ];
    const state = cm.EditorState.create({
      doc: norm,
      extensions: [
        cm.EditorState.phrases.of(Object.fromEntries(PHRASES.map(([k, id]) => [k, t(id)]))),
        cm.EditorState.readOnly.of(readOnly), cm.EditorView.editable.of(!readOnly),           // nur lesen: kein Zeiger im Text, auf dem Handy keine Tastatur (Suchen und Markieren gehen weiter)
        cm.EditorState.tabSize.of(unit === '\t' ? 4 : unit.length),
        cm.indentUnit.of(unit),
        cm.lineNumbers(), cm.highlightActiveLineGutter(), cm.highlightSpecialChars(), cm.history(), cm.foldGutter(), cm.drawSelection(), cm.dropCursor(), cm.indentOnInput(),
        cm.syntaxHighlighting(style), cm.bracketMatching(), ...(BRACKET_LANGS.has(lang) ? [cm.closeBrackets()] : []), cm.rectangularSelection(), cm.highlightActiveLine(), cm.highlightSelectionMatches(),
        cm.search({ top: true }),
        ...(kind ? [cm.linter(lint, { delay: 400 }), cm.lintGutter()] : []),
        wrapC.c.of(wrap ? cm.EditorView.lineWrapping : []),
        cm.keymap.of([
          ...(BRACKET_LANGS.has(lang) ? cm.closeBracketsKeymap : []),
          ...cm.searchKeymap,
          { key: 'Mod-h', run: (v) => { cm.openSearchPanel(v); const r = v.dom.querySelector('.cm-search [name=replace]'); if (r) r.focus(); return true; } },
          ...tabKeys, ...cm.defaultKeymap, ...cm.historyKeymap, ...cm.foldKeymap, ...cm.lintKeymap,
        ]),
        cm.EditorView.contentAttributes.of({ 'aria-label': t('files.edit.label', { name: safeName(name) }), spellcheck: 'false', autocapitalize: 'off', autocorrect: 'off' }),
        editorLang,
        cm.EditorView.updateListener.of((u) => {
          if (u.docChanged || u.selectionSet) { tabFree = false; showStatus(); }
          if (u.transactions.length) refresh();
        }),
        cm.EditorView.domEventHandlers({ blur: () => { if (tabFree) { tabFree = false; showStatus(); } } }),
      ],
    });
    original = state.doc;
    view = new cm.EditorView({ state, parent: host });
    host.querySelector('.f-ed-load')?.remove();
    refresh(); showStatus();
    if (!kit) view.focus();                                          // auf dem Handy oeffnet erst ein Tippen die Tastatur
  }

  async function save() {
    if (readOnly || busy || !view || !modified()) return;
    busy = true; refresh();
    const doc = view.state.doc;
    try { if (await onSave(withEol(doc.toString(), eol))) original = doc; }
    finally { busy = false; refresh(); if (view) view.focus(); }
  }
  async function tryClose() {
    if (closing) return;
    if (modified()) {
      closing = true;
      const ask = modal({ title: t('files.edit.discardTitle'), kit, id: 'discard', focus: 'no', content: [h('p', { class: 'f-sentence', text: t('files.edit.discardText', { name: safeName(name) }) })], actions: [{ id: 'no', label: t('files.edit.keepEditing'), kind: 'ghost' }, { id: 'yes', label: t('files.edit.discard'), kind: 'danger' }] });
      const r = await ask.promise; closing = false;
      if (r !== 'yes') { if (view) view.focus(); return; }
    }
    dlg.close('close');
  }
  return { promise: dlg.promise, close: () => dlg.close('close'), modified, focus: () => dlg.focus && dlg.focus() };
}
