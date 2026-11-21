# Dialogs as windows, a real editor and a viewer for the Files app (plan, not built)

Status: **planned**, owner's wish of 2026-11-21. **Built so far (not released): the backend part of step 3, the preview endpoint** (`GET /api/files/preview`, `GET /api/files/remote/preview`, `action: preview` in `prepare`, the preview limit setting; see [FILES.md](FILES.md#preview-the-narrow-inline-answer)); no viewer window and no pdf.js yet. The rest is not built. Three wishes: (1) dialogs always open as **windows** so that nothing blocks the whole desktop, (2) an editor that is pleasant for YAML, JSON and the like (VS Code direction, but simple), (3) **look at** video, images, PDF, Word and Excel files. Constraints from [CLAUDE.md](../CLAUDE.md): small, plain JavaScript, no build step, every route behind a session, the confirmation flow of [FILES.md](FILES.md) stays as it is. Libraries below were checked on 2026-11-21 against the npm registry (versions, licences, unpacked sizes) and by unpacking the packages; sizes are files as shipped, not yet measured in a running JulOS.

## A. Dialogs as windows

**Inventory (frontend, `wwwroot`).** Everything modal today uses `<dialog>.showModal()` and blocks the page behind it.

| Group | Where | Purpose |
|---|---|---|
| 19 static dialogs | `index.html`, opened from `app.js`, `shell.js`, `security.js`, `account.js`, `alerts.js`, `folder-dialog.js`, `nettools.js`, `stepup.js`, `hostactions-dialog.js` | connection, folder, host, **host action confirmation (`hostActDlg`)**, connect, quick connect, devices, security, **second factor (`stepUpDlg`)**, account, alerts, network tools, clipboard text, audio, key, theme, password insert, photo, program launcher |
| Files dialogs | one helper, `modal()` in `files-ui.js` (about 15 call sites in `files-app.js`, `files-editor.js`) | **write confirmations** (`confirmDialog`), name input, **progress**, host key, password, properties, history, place chooser, **editor**, discard question |
| Created in code | `widgets.js` (service form), `mobile.js` (`mFilterDlg`, `mActDlg`, `mSessDlg`), `mobile-modules.js` (`mModDlg`) | phone sheets (`m3sheet`) and the widget form |
| Not dialogs, stay as they are | `.sheet` account menu, `dk` menus and launcher in `desktop.js`, `sv-overlay` ("disconnected") and `sv-scrim`, toasts (`notify`), `po-overlay` in the pop-out page | popups, status overlays, messages |

**Proposal: one window block in the existing window manager** (`createWindow` in `desktop.js`: move, size, z-order, focus, themes already exist). New module `desktop-window.js` exports `openDialogWindow({title, content, actions, owner, id})` with the **same contract as `modal()`** (`{el, close(result), promise, setDisabled, setLabel}`; `null` when closed without a button). Because the 15 Files call sites already go through `modal()`, they move by changing that one function. The 19 static dialogs keep their markup and ids: a helper `showDialog(dlg)` replaces `dlg.showModal()` on the desktop and puts the existing `<dialog>` element into a window frame (`dlg.show()`, not `showModal()`), so the logic in `app.js` and the others stays untouched. **Phone keeps the full-screen or bottom sheet** (`m3sheet`, today's behaviour; the phone has no window manager and one thing at a time is right there). The branch is one test: `html.desktop`.

Decisions to build in:

- **Window features:** draggable by the title bar, resizable only where it makes sense (editor, viewer, history; not confirmations), close button, no maximise or dock pill for dialogs, centred over the owner with a small cascade. Several can be open at once. A dialog with a fixed `id` opens once (a second click focuses the open one).
- **Owner and stacking:** a dialog belongs to the window that opened it and is raised together with it, above it; closing the owner closes its dialogs (the editor asks about unsaved changes first, as today). Click or `Alt+Tab`-style focus uses the existing `focusWin`/`zTop`.
- **Keyboard:** a non-modal window cannot trap `Tab`; focus moves into the window on open and **back to the opener on close**. `Escape` closes only the focused dialog window (never an app window) and counts as "cancel". `Ctrl+S` in the editor as today.
- **Confirmations (host actions, files, root).** Content unchanged: host, target host, path, counts, size, "as root", level and warning from `prepare`, the typed host name or tick for dangerous and critical. The **two-step token logic is not touched** (server issues the token, 60 s, once, bound to session, action and exact request). What changes is only the container, so: (1) **closing the window, `Escape`, or the token running out never confirms**: the result is `null`, nothing is sent; the window shows a countdown and disables "Run" after 60 s ("expired, ask again"). (2) For dangerous and critical the **focus starts on Cancel**, `Enter` does not confirm, "Run" needs a click. (3) Since the desktop stays usable, the window title names host and action ("Delete 3 items on <Host>", root marked red) so a window found later is unmistakable; it is raised once when it opens and does not demand attention again. (4) Second factor (`stepUpDlg`) is a window too and sits above the window that needs it; closing it cancels that action.
- **Errors and progress:** a short error stays a toast (`notify`); an error that has to be read (host key changed, `confirmation_mismatch`) is a small window. Copy, move and delete **progress** is a window with Cancel that does not block (jobs already run in the background, `hosttasks.js` is the model).
- **Risk to name:** two confirmations can be open at once and the folder behind them can change; the server judges each token against its exact request, so a stale one is refused (`confirmation_mismatch`), never executed wrongly.

## B. Editor

Needed: syntax colours for YAML, JSON, Markdown, shell, config files (ini, toml, nginx, Dockerfile), logs; line numbers; search and replace; folding; JSON and YAML check with the error line; theme like the app (light and dark, accent); optional minimap and tabs; save through the existing confirmed upload; works on the phone.

| | Monaco 0.57.0 (MIT) | CodeMirror 6 (MIT, modular) | Own editor (textarea + overlay) |
|---|---|---|---|
| Fit | the VS Code editor itself | VS Code-like enough: search, replace, folding, lint, multiple selections | what the dialog is today |
| Without a build step | only the **AMD build, which the README calls deprecated and to be removed**; ESM needs a bundler | one **prebuilt single file** made once (esbuild, recipe kept in the repo); the app itself still has no build step | yes |
| Size | min build 25 MB in the package; the part we need (core 2.5 MB, 0.4 MB CSS, workers 0.3 to 0.4 MB, a few small language files) is **about 5 to 6 MB**, about 1.5 MB over the wire; not measured end to end | measured: YAML, JSON, Markdown, shell, properties, toml, nginx, search, fold, lint in one file **0.65 MB (0.22 MB gzip)**, less without Markdown's HTML and CSS support | 0 |
| Phone | **README: mobile browsers are not supported ("No")** | works with touch and Gboard | works |
| Theme like the app | two built-in themes, colours set from JavaScript | themes are CSS: use the app's CSS variables directly | yes |
| CSP (`worker-src 'self'`) | needs worker URLs of its own and care | none (no workers) | none |
| Minimap | built in | add-on (`@replit/codemirror-minimap`, MIT), optional | no |

**Recommendation: CodeMirror 6, vendored as one prebuilt ES module.** Monaco does not run on the phone, the one client JulOS is built around, its non-bundler build is on its way out, and it is ten times larger; the own editor cannot do folding or search without growing into a bad copy of CodeMirror. Check: JSON uses `jsonParseLinter`; YAML uses `js-yaml` (MIT, loaded only when a YAML file is opened) for the error line; minimap and tabs come later and only if wanted. **Large files:** editing stays limited to the **1 MiB** of the text route (the whole file goes through the confirmed upload); a larger text file opens **read-only** as the end or beginning (a log: the last 256 KiB) through the Range support of the viewer endpoint (C), with "load more". **Delivery:** `wwwroot/vendor/codemirror/<version>/` with the bundle, its licence files, and `wwwroot/vendor/README.md` listing for every library the version, source (npm tarball URL), SHA-256 of the tarball and of every shipped file, the licence and, for CodeMirror, the exact `esbuild` command; one small node test recomputes the hashes. `licenses.txt` is extended in the same commit. No CDN at run time. The service worker already caches same-origin GETs after first use (network first), so vendored files need no change there.

## C. Viewer (look first, edit later)

| Type | How | Licence, size | Limits |
|---|---|---|---|
| png, jpg, gif, webp, svg | `<img>` | browser built-in | SVG only ever as `<img>` (never a document, no script, no external load) |
| mp4, webm, mp3, ogg, flac, wav | `<video>` / `<audio>` on the preview URL, **Range** for seeking | built-in | what the browser plays (no mkv, avi, usually no HEVC); otherwise the viewer offers download |
| pdf | **pdf.js 6.5.438 vendored**, drawn to canvas in our own page (not the browser viewer: Chrome blocks its viewer in sandboxed frames and Android Chrome has none) | Apache-2.0; `pdf.min.mjs` 0.47 MB + worker 1.27 MB (about 0.5 MB gzip together); fonts and wasm folders (about 2 MB) only if needed | `isEvalSupported: false` and scripting off (an old pdf.js had a code-execution bug, CVE-2024-4367; the CSP `script-src 'self'` is the second wall) |
| docx | **docx-preview 0.4.1** + `jszip` | Apache-2.0, 75 KB + 97 KB | layout close to Word, no editing; the mammoth alternative (BSD-2) gives plain HTML and loses layout |
| xlsx, csv | **read-excel-file 9.3.20** (MIT, 48 KB, npm, active) in a worker, values only; csv with a small own parser | MIT | cached values, no formulas or formats, no `.xls`; at most 5000 rows by 50 columns per sheet |
| xlsx alternative | SheetJS `xlsx` | Apache-2.0, but npm stops at 0.18.5 with known prototype-pollution and ReDoS reports; fixed versions only from the vendor's own site | not recommended for files from hosts |

Rendering rules: text from files is always set with `textContent`; Word output is built in a detached element on our page (DOM calls, no `innerHTML` of file text), then shown in an `iframe sandbox` (no scripts, no same-origin) via `srcdoc`, links only `http(s)` with `rel=noopener`; whether blob images load in that frame is checked in step 4.

**Endpoint model (new, narrow).** Today downloads are `attachment` + `application/octet-stream` + `nosniff` and never a page; that stays for `download`. New: `GET /api/files/preview` (shared folder) and `GET /api/files/remote/preview?c=&path=&token=`, in the same files module and under the same `ApiGuard`:

1. **Session needed**, `SameSite=Strict` cookie (a foreign page cannot embed it), rate limit like `download`; same path normalisation, `JULOS_FILES_*` switches and audit as today.
2. **Content-Type from a fixed list by extension** (png, jpeg, gif, webp, svg, mp4, webm, mp3, ogg, flac, wav, pdf; docx, xlsx, csv as `application/octet-stream` for the page's own `fetch`), never from the content; a quick magic-byte check refuses a file that does not match (`415 preview_type`). Anything not on the list is refused: **HTML, XML and unknown types are never served inline.**
3. Headers on every preview answer: `X-Content-Type-Options: nosniff`, `Content-Security-Policy: sandbox; default-src 'none'; img-src 'self'; style-src 'unsafe-inline'` (a direct visit of the URL, even SVG, runs nothing), `Cache-Control: no-store`, `Cross-Origin-Resource-Policy: same-origin`, `Content-Disposition: inline` with the safe name. The global CSP keeps `frame-ancestors 'none'`: nothing is framed, so no change there; `media-src` falls back to `'self'` (a `blob:` source would need one more word, only if root media is wanted).
4. **Size limit** per type (default 50 MiB, a setting in the app as the owner prefers; video and audio unlimited because they are ranged).
5. **Hosts over SFTP:** streamed through the server, never stored; Range is answered with `206` by seeking the open SFTP stream (SSH.NET streams seek). `MaxStreams = 3` per user in `RemoteFileEndpoints.cs` is too tight for a seeking video (the browser opens several ranged requests): previews get their own count (for example 6) and are closed when the window closes.
6. **Root places:** the confirmed read stays: `prepare` with `download` or `text`, a dialog window naming file and "as root", and the token is **valid once**. So no Range and no seeking on root: the viewer fetches the file once with the token (images, PDF, Word, Excel up to the size limit) and shows it from a `blob:`; **video and audio on root stay download-only** (open question 3). The audit entry (`started`, fail-closed) is written as for any root read.

**What the built endpoint does differently from the model above** (decisions made while building, 2026-11-21): (a) the list holds only image, video, audio and PDF (docx, xlsx and csv for the page's own `fetch` come with step 5; **SVG is refused** instead of served as an `<img>`: `preview_type`); (b) the 50 MiB limit applies to images and PDF, video and audio only to `JULOS_FILES_MAX_MB` (ranged, never held); (c) the setting is `security.filesPreviewMb` with `JULOS_FILES_PREVIEW_MB` as fallback, changed like the write switch; (d) the rate limit is 240 per minute (a seeking video asks often), not the 60 of `download`; (e) the CSP also says `media-src 'self'` so that opening a video address directly plays; (f) the preview counts its own streams (6); (g) root: `prepare` with `action: preview` refuses type and size before the token, and the run checks the first bytes; (h) links are read like for a download (a link to a file as that file), a link to a folder is `not_a_file`; the plan's "never followed" holds for counting, copying and deleting.

## D. Order and cut into sessions

Each step is releasable alone; files named are the ones it touches. Files of the other running work (`files-explorer.js`, `desktop-files.js`, `files.css`, the write settings) are only touched where listed, new styles go into **new CSS files** (`dialogwin.css`, `viewer.css`) to avoid clashes.

| # | Step | Size | Touches | Depends on |
|---|---|---|---|---|
| 1 | **Window block + Files dialogs**: `openDialogWindow`, `modal()` delegates to it (desktop), confirmations/progress/step-up rules above, editor opens in a window | M | new `desktop-window.js`, `dialogwin.css`; `desktop.js` (export a small window factory); `files-ui.js`; `files-editor.js` (container only); `sw.js` (shell list), docs | the explorer work released (it edits `files.css`/`files-app.js`) |
| 2 | **Move the 19 static dialogs and the widget form** to windows (`showDialog`), phone unchanged | M | `index.html`, `app.js`, `shell.js`, `security.js`, `account.js`, `alerts.js`, `folder-dialog.js`, `nettools.js`, `stepup.js`, `hostactions-dialog.js`, `widgets.js`, `desktop.css` | 1 |
| 3 | **Preview endpoint + image, video, audio, PDF**: endpoint model above, viewer window, pdf.js vendored, double click opens the viewer by extension | L | new `Files/FilePreview.cs`, `FileEndpoints.cs`, `RemoteFileEndpoints.cs` (route and stream count); new `files-viewer.js`, `viewer.css`, `wwwroot/vendor/pdfjs`, `vendor/README.md`; `files-app.js` (one open handler); `licenses.txt`; backend tests for the endpoint (type list, nosniff, Range, root token, refusal of html) | 1 (window) |
| 4 | **Editor** with CodeMirror 6, JSON/YAML check, read-only tail of big files (needs step 3's Range), theme | M | `files-editor.js` (rewrite inside), new `editor.css`, `vendor/codemirror`, `tools/vendor-codemirror.sh`; `licenses.txt` | 1; the big-file part after 3 |
| 5 | **Word and Excel** viewers | M | `files-viewer.js` (two renderers), `vendor/docx-preview`, `vendor/jszip`, `vendor/read-excel-file`, `licenses.txt` | 3 |

Steps 3 and 4 can run in two sessions at once (disjoint except the one-line open handler in `files-app.js`; do that hook last). Step 2 is mechanical and can wait. Total: about **L to XL** over five releases. Tests (sparingly, as agreed): the preview endpoint is security-relevant (backend tests); the vendor hash check; pure logic of the viewer (type by extension, limits, row cut). Looks and window behaviour are checked by running it in the browser pane, desktop and phone, light and dark.

## Questions for the owner

1. Dialogs in windows: should **confirmations** also be movable windows (plan: yes, with the safeguards above), or stay one fixed, centred window that is only non-blocking?
2. Editor: is **CodeMirror 6** (works on the phone, one 0.65 MB vendored file, minimap only as an add-on) acceptable, or is the built-in minimap of Monaco (not usable on the phone, about 5 to 6 MB) important enough to choose it?
3. **Video and audio on root places** only as download (plan), or worth a blob up to a size limit?
4. Size limit for previews (plan: 50 MiB, a setting) and for the editor (1 MiB edit, read-only beyond)?
5. **Markdown rendered** (marked 18, MIT, with a sanitizer) as a preview: later, or not at all? HTML files are never rendered in any case.
6. Edit **Word and Excel** later (no plan now; LibreOffice-class editing is out of proportion)?

Sources: npm registry entries and tarballs of `monaco-editor` 0.57.0, `pdfjs-dist` 6.5.438, `docx-preview` 0.4.1, `read-excel-file` 9.3.20, `xlsx` 0.18.5, `jszip` 3.10.2, `codemirror` 6 packages (read 2026-11-21); the Monaco README (mobile FAQ, AMD note); the SheetJS issue [git.sheetjs.com #3012](https://git.sheetjs.com/sheetjs/sheetjs/issues/3012) and [Snyk on xlsx](https://security.snyk.io/package/npm/xlsx) for the vulnerability and distribution notes. CVE-2024-4367 and Chrome's behaviour with PDFs in sandboxed frames are from memory and should be re-checked when step 3 starts.
