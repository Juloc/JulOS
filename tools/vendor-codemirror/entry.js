// Entry of the vendored editor bundle (built by tools/vendor-codemirror.sh into wwwroot/vendor/codemirror/<version>/codemirror.mjs).
// Only what files-editor.js uses is exported; languages are listed by key so that the app code does not import any package itself.
export { EditorState, Compartment } from '@codemirror/state';
export { EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, highlightSpecialChars, rectangularSelection, dropCursor } from '@codemirror/view';
export { defaultKeymap, history, historyKeymap, indentMore, indentLess, undo, redo, undoDepth, redoDepth } from '@codemirror/commands';
export { HighlightStyle, syntaxHighlighting, bracketMatching, foldGutter, foldKeymap, indentOnInput, StreamLanguage, indentUnit } from '@codemirror/language';
export { search, searchKeymap, highlightSelectionMatches, openSearchPanel, searchPanelOpen } from '@codemirror/search';
export { linter, lintGutter, lintKeymap, forEachDiagnostic } from '@codemirror/lint';
export { closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
export { tags, Tag } from '@lezer/highlight';

import { yaml } from '@codemirror/lang-yaml';
import { json } from '@codemirror/lang-json';
import { markdown } from '@codemirror/lang-markdown';
import { javascript } from '@codemirror/lang-javascript';
import { python } from '@codemirror/lang-python';
import { xml } from '@codemirror/lang-xml';
import { StreamLanguage } from '@codemirror/language';
import { shell } from '@codemirror/legacy-modes/mode/shell';
import { properties } from '@codemirror/legacy-modes/mode/properties';
import { toml } from '@codemirror/legacy-modes/mode/toml';
import { nginx } from '@codemirror/legacy-modes/mode/nginx';
import { dockerFile } from '@codemirror/legacy-modes/mode/dockerfile';

/** key (see editor-logic.js, langFor) -> CodeMirror language extension; unknown keys give plain text (no extension) */
export function language(key) {
  switch (key) {
    case 'yaml': return yaml();
    case 'json': case 'jsonc': return json();
    case 'markdown': return markdown();
    case 'javascript': return javascript();
    case 'python': return python();
    case 'xml': return xml();
    case 'shell': return StreamLanguage.define(shell);
    case 'properties': return StreamLanguage.define(properties);
    case 'toml': return StreamLanguage.define(toml);
    case 'nginx': return StreamLanguage.define(nginx);
    case 'dockerfile': return StreamLanguage.define(dockerFile);
    default: return [];
  }
}
