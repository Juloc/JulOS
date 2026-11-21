# Vendored libraries (no build step, no CDN)

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
