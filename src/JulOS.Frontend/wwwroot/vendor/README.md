# Vendored libraries (`wwwroot/vendor`)

Third-party code that ships with the client. Nothing is loaded from a CDN at run time. Every file is listed with its SHA-256 (`tests/frontend/editor-logic.test.mjs` recomputes the hashes, so a changed file without a new entry fails the test). Licences are in `licenses.txt` and next to the files. The older `lib/xterm` is described in `lib/README.txt`.

## CodeMirror 6 and js-yaml (the Files editor)

- **What:** the editor of the Files app (`files-editor.js`). `codemirror.mjs` is one ES module with the CodeMirror 6 packages the editor uses (state, view, commands, language, search, lint, autocomplete for bracket closing; YAML, JSON, Markdown, JavaScript, Python and XML languages; the shell, properties, TOML, nginx and Dockerfile modes of `@codemirror/legacy-modes`; `@lezer/*` parsers). `js-yaml.mjs` is js-yaml's `loadAll` (the YAML check) and is loaded only when a YAML file is opened. Size: 0.69 MB and 0.05 MB (0.24 MB gzip together).
- **Licence:** all MIT, texts in `LICENSE.txt` (one file, every package once).
- **Versions (npm):** `@codemirror/view` 6.43.15 (names the folder), state 6.7.6, commands 6.11.1, language 6.13.1, search 6.7.2, lint 6.9.8, autocomplete 6.20.4, lang-yaml 6.1.3, lang-json 6.0.2, lang-markdown 6.5.2, lang-javascript 6.2.5, lang-python 6.2.1, lang-xml 6.1.0, legacy-modes 6.5.5, `@lezer/highlight` 1.2.5, js-yaml 5.5.0, built with esbuild 0.28.2. The exact tree (about 60 packages) with the sha512 of every npm tarball is in `tools/vendor-codemirror/package-lock.json`; `npm ci` refuses a tarball that does not match.
- **Source:** the npm registry (`https://registry.npmjs.org/<package>/-/<package>-<version>.tgz`, the URLs are in the lock file).
- **Build:** `tools/vendor-codemirror.sh` (needs node and npm and network; run it in a throwaway folder, nothing of npm or esbuild is in the image). It runs `npm ci --ignore-scripts` from `tools/vendor-codemirror/` and then `esbuild entry.js --bundle --minify --format=esm --target=es2022 --legal-comments=none` (and the same for `yaml-entry.js`). The build is reproducible: running it again gives the same hashes. To update: change the versions in `tools/vendor-codemirror/package.json`, `npm install --package-lock-only` there, run the script, and copy the new folder name and hashes here, into `files-editor.js` (`CM_DIR`) and into `tests/frontend/editor-logic.test.mjs`.

| File | SHA-256 |
|---|---|
| `codemirror/6.43.15/codemirror.mjs` | `11c7f73b8fc4ce12a28e631acc034a62dd1e547ad1c6a886ff6353082188035f` |
| `codemirror/6.43.15/js-yaml.mjs` | `a9807a6791e55f2c4c31156aff0edd9430abbdf11a1c8aa84358356d331c0f7f` |
| `codemirror/6.43.15/LICENSE.txt` | `d035e0119de7710f28539440da11555e66652a053c7faea8cd9ba102d21a1b4d` |


Files in this folder are copied unchanged from the npm package named in each section. The table lists the SHA-256 of every file as shipped; `tests/frontend/vendor.test.mjs` checks it, so a changed file fails the test. To update a library: download the new package into an empty folder, compare it with the npm registry's integrity value, copy the files, and replace the version and the hashes here (and the entry in `../licenses.txt`).

## pdf.js (`pdfjs/`)

- Package: `pdfjs-dist` **6.5.438** (Apache-2.0), downloaded 2026-11-21. Registry integrity of the tarball: `sha512-vg8Pw1x+AFKy4AMDvhuWl5cOYV8d698UoEANoFFhc79hqXDnZQqnOXoxw3HG907WHOg1JYOvLhP756boddFxpA==` (checked against the SHA-512 of the downloaded file).
- Used by `files-viewer.js` (PDF preview of the Files app). Loaded only when a PDF is opened; not in the service worker's start list.
- Security: CVE-2024-4367 (code execution through a font matrix in a crafted PDF) is fixed in 4.2.67; this version is later. The viewer also sets `isEvalSupported: false`, loads neither the pdf.js scripting sandbox nor its annotation/viewer layer, and the page's CSP (`script-src 'self'`) does not allow `eval` anyway.
- Taken from the package: `build/pdf.min.mjs`, `build/pdf.worker.min.mjs`, `LICENSE`, and from `wasm/` only the plain-JavaScript fallbacks for JBIG2 and JPEG 2000 images with their licence files. Not taken: the `.wasm` files (the page CSP has no `wasm-unsafe-eval`, the viewer sets `useWasm: false`), `cmaps/` (1.7 MB, only for PDFs that use non-embedded CJK fonts), `standard_fonts/` (0.8 MB, the system fonts take over), `iccs/`, `web/` (viewer widgets we do not use), the scripting sandbox, source maps.

| File | SHA-256 |
|---|---|
| `pdfjs/pdf.min.mjs` | `3a88402882cfa3e7e72c2c9b5149f52f0f88baf1c5b62aa11a41abdbc2072440` |
| `pdfjs/pdf.worker.min.mjs` | `1e64c8240e54735ec5913b37acd5134747799cb8c6bb3777c70adbc01232d2aa` |
| `pdfjs/LICENSE` | `0d542e0c8804e39aa7f37eb00da5a762149dc682d7829451287e11b938e94594` |
| `pdfjs/wasm/jbig2_nowasm_fallback.js` | `70150223396487e1ccfef7eac61fc780074ad7fcc9e843b702a8a3bc23107494` |
| `pdfjs/wasm/openjpeg_nowasm_fallback.js` | `2855f1590e7fd7abbbf204cd13a5c09d30c92fe2c9313fb72b64aa763fe8b4f3` |
| `pdfjs/wasm/LICENSE_JBIG2` | `9e66b7f1b934a28b37f3bc4dac97915de1674271e79a0a88182a18ed9731b4d1` |
| `pdfjs/wasm/LICENSE_OPENJPEG` | `a6af136f3e15038a666b61f376612a07d9a4e48cb7c01adbf3e33b3f14ab49b6` |
| `pdfjs/wasm/LICENSE_PDFJS_JBIG2` | `aad3cce09842e00e9e11ad5e8fef8cc02fbc3a3768fe2f007443b9cee37aaee5` |
| `pdfjs/wasm/LICENSE_PDFJS_OPENJPEG` | `717fc62da03292dbb4dd0c8280bd4ce7bb8550dcf31d772bc93455fb50313425` |
