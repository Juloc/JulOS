# Vendored libraries (`wwwroot/vendor`)

Third-party code that ships with the client. Nothing is loaded from a CDN at run time. Every file is listed with its SHA-256 (`tests/frontend/editor-logic.test.mjs` recomputes the hashes, so a changed file without a new entry fails the test). Licences are in `licenses.txt` and next to the files. The older `lib/xterm` is described in `lib/README.txt`.

## CodeMirror 6 and js-yaml (the Files editor)

- **What:** the editor of the Files app (`files-editor.js`). `codemirror.mjs` is one ES module with the CodeMirror 6 packages the editor uses (state, view, commands, language, search, lint, autocomplete for bracket closing; YAML, JSON, Markdown, JavaScript, Python and XML languages; the shell, properties, TOML, nginx and Dockerfile modes of `@codemirror/legacy-modes`; `@lezer/*` parsers). `js-yaml.mjs` is js-yaml's `loadAll` (the YAML check) and is loaded only when a YAML file is opened. Size: 0.69 MB and 0.05 MB (0.24 MB gzip together).
- **Licence:** all MIT, texts in `LICENSE.txt` (one file, every package once).
- **Versions (npm):** `@codemirror/view` 6.43.15 (names the folder), state 6.7.6, commands 6.11.1, language 6.13.1, search 6.7.2, lint 6.9.8, autocomplete 6.20.4, lang-yaml 6.1.3, lang-json 6.0.2, lang-markdown 6.5.2, lang-javascript 6.2.5, lang-python 6.2.1, lang-xml 6.1.0, legacy-modes 6.5.5, `@lezer/highlight` 1.2.5, js-yaml 5.5.0, built with esbuild 0.28.2. The exact tree (about 60 packages) with the sha512 of every npm tarball is in `tools/vendor-codemirror/package-lock.json`; `npm ci` refuses a tarball that does not match.
- **Source:** the npm registry (`https://registry.npmjs.org/<package>/-/<package>-<version>.tgz`, the URLs are in the lock file).
- **Build:** `tools/vendor-codemirror.sh` (needs node and npm and network; run it in a throwaway folder, nothing of npm or esbuild is in the image). It runs `npm ci --ignore-scripts` from `tools/vendor-codemirror/` and then `esbuild entry.js --bundle --minify --format=esm --target=es2022 --legal-comments=none` (and the same for `yaml-entry.js`). The build is reproducible: running it again gives the same hashes. To update: change the versions in `tools/vendor-codemirror/package.json`, `npm install --package-lock-only` there, run the script, and copy the new folder name and hashes here, into `files-editor.js` (`CM_DIR`) and into `tests/frontend/editor-logic.test.mjs`.

| File | Bytes | SHA-256 |
|---|---|---|
| `vendor/codemirror/6.43.15/codemirror.mjs` | 693159 | `11c7f73b8fc4ce12a28e631acc034a62dd1e547ad1c6a886ff6353082188035f` |
| `vendor/codemirror/6.43.15/js-yaml.mjs` | 46246 | `a9807a6791e55f2c4c31156aff0edd9430abbdf11a1c8aa84358356d331c0f7f` |
| `vendor/codemirror/6.43.15/LICENSE.txt` | 37957 | `d035e0119de7710f28539440da11555e66652a053c7faea8cd9ba102d21a1b4d` |
