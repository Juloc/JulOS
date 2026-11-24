# Clipboard: text, images and files

Text travels both ways (see [ARCHITECTURE.md](ARCHITECTURE.md), display engine); the section "Text" below says how, "Fehlersuche" says what to check when it does
not. The rest of the page is about images, files and folders.

## Text

| | |
| --- | --- |
| Remote to browser | the remote copies; xrdp announces `CF_UNICODETEXT`, the server fetches it at once and sends it as message 6; the browser puts it into its own clipboard (`navigator.clipboard.writeText`) |
| Browser to remote | Ctrl+V (or Ctrl+Shift+V, Shift+Insert) in the picture: the browser's `paste` event carries the text, the page sends it (message 9) and sends the key combination to the remote **when the remote has accepted the new format list** (see "Paste at the right moment"); the "Text senden" dialog does the same with a button |
| Limit | 1 MiB of UTF-8 in both directions; more is refused **with a message** (toast and diagnosis), not silently |
| Line ends | **LF to the remote**, unless the remote has shown that it wants CR LF. xrdp passes text on unchanged in both directions, so a CR from the browser ended up in every line (terminal, editors). The server switches to CR LF when the remote itself has sent text with CR LF and no lone LF (a Windows host) and back when it sends lone LF. `JULOS_CLIP_EOL=lf` or `crlf` fixes it, `auto` (default) is this behaviour. Text from the remote always arrives with LF. |
| Echo | some servers report text we just gave them as a new copy; it is not forwarded to the browser for 1.5 s after the paste and only while the remote has not copied anything else (before, the same text copied in the remote later never reached the browser) |
| No answer | a data request the remote never answers counts as refused after 15 s, the next one goes out (before, one lost answer blocked every later copy of that session) |
| Selection | xrdp only knows the `CLIPBOARD` selection (Ctrl+C, Ctrl+X), not `PRIMARY` (marking with the mouse, middle click). Marking text in the VM does nothing for the browser; copy it. Pasting from the browser with the middle mouse button does not work either, use Ctrl+V (Ctrl+Shift+V in terminals). |

### Paste at the right moment (RDP)

A paste is two steps: the text goes to the server (message 9, the server announces `CF_UNICODETEXT` to the remote), and the browser presses the paste chord
(it chooses the chord: Ctrl+V, Ctrl+Shift+V in terminals; the server presses no keys). If the chord arrives before the remote has taken the new list, the remote
pastes the old text. So the browser waits for the remote:

- The RDP channel reports the remote's answer to a format list (`FormatsAcknowledged`). `ClipboardBridge` turns it into message 15 kind 8 (`ListAccepted`) when
  **no list is left unanswered** (an answer to an older list does not count), and an answer "refused" into kind 7 (`ListRefused`) as before.
- Message 15's flags have bit 2 set by an RDP server: "I report acceptance". Only then the browser waits (`PasteGate` in `clip-sync.js`); the chord follows on the
  acceptance. After **600 ms** without an answer it is sent anyway. A refusal sends nothing and shows "Die VM hat die Zwischenablage des Browsers abgelehnt." (toast, opens the dialog).
- Without bit 2 (VNC, an older server, before the first message 15) the old fixed pause of 180 ms stays. A new server with an old browser: the browser ignores kind 8 and
  keeps its pause. Tests: `tests/frontend/clip-sync.test.mjs`, `ClipboardBridgeTests`, e2e test 19 (twenty pastes in a row, each time the new text in a real window of the test desktop).
- Not covered: an answer to the previous paste that arrives between two pastes within one round trip can release the next one early (then it behaves like the old pause).

### Copy to the device, retried

A text or picture from the remote is written to the device clipboard at once (`storeVmText`, `showRemoteImage`). Browsers only allow that inside a user gesture
(Safari, phones) and with focus. If it fails, the item **waits** (`devPending`; the toast and the dialog stay as before) and is written again on the next focus,
pointer-down or key-down in the session (not on Ctrl+V: the person is pasting). The decision is `pendingStep` in `clip-sync.js`: the item is dropped when the person
copies or cuts something in the page, when the remote copies something newer, when the session ends and after one minute. A write that works clears it.
Not verified: Safari and a real phone (the logic is tested with faked time, the browser behaviour is known from the documentation).

### Pictures and files into an SSH terminal

A picture or file that is pasted (Ctrl+V with a file or picture on the clipboard) or dropped on an SSH terminal, or chosen with "Datei einfügen" in the terminal's menu
(right click, long press: the entry for the phone), becomes a file on the host. Text paste is unchanged (text wins unless a file that is not a picture is on the clipboard).

- **Folder:** the shell's current folder from **OSC 7** (`ESC ] 7 ; file://host/path BEL`, parsed by `parseOsc7` in `term-paste.js`: only absolute paths without `..` or
  control characters; a host name other than the first one the shell named, that is a `ssh` inside the terminal, makes the folder unknown). Unknown: the home folder, with
  a hint (once per browser) how to switch OSC 7 on (bash: `PROMPT_COMMAND='printf "\e]7;file://%s%s\a" "$HOSTNAME" "$PWD"'`). tmux hides OSC 7 from the outer terminal
  unless passthrough is set up; then the home folder is used.
- **Upload:** the existing path of the Files app, nothing new: `prepare` (confirmation, as for an upload there), dialog, `POST /api/files/remote/upload` per file with the
  pool of SFTP sessions, the strict host-key check (with the same dialogs), password questions as in the Files app, the switch "Dateien schreiben" (`JULOS_FILES_WRITE`
  and the setting; off: the dialog says so and nothing happens), the same limits, name and size checks, progress and cancel, history entries. The server validates the folder
  itself (`RemotePath.Normalize`: absolute, no `..`, must be an existing folder); the path from the browser is input, not truth. Only a saved SSH connection works
  (a quick connection has no stored login to use). Pictures are named `paste-YYYYMMDD-HHMMSS.png`, other names are cleaned (control characters and `/` become `_`) and made unique.
- **Command line:** after a **complete** upload the paths the server reported are written (as a paste, bracketed where the shell knows it) quoted for a POSIX shell
  (`shellQuote`: safe characters stay, otherwise single quotes with `'\''`, a leading `-` gets `./`), separated by spaces, with one space at the end; **no Enter**. A path with a control character
  is never written. A failed or cancelled upload writes nothing; the dialog says what is up.
- Tests: `tests/frontend/term-paste.test.mjs` (OSC 7, quoting, names, clipboard choice), `RemoteFilesHttpTests` (session and header, write switch off, `..` and relative folders, confirmation), e2e test 20.
- Not verified: a real phone (the file picker), Safari, tmux and OSC 7, zsh and fish, Windows targets (the SFTP server there).

### Browser permissions (`clipperm.js`)

What the browser may do is decided by the browser, not by JulOS. JulOS asks for the state first (`navigator.permissions.query` for `clipboard-read` and `clipboard-write`, inside a
try/catch: Firefox and Safari do not know `clipboard-read`, the state is then "unknown") and acts on it:

| Situation | What JulOS does |
| --- | --- |
| Insecure context (plain `http`, not `localhost`): `navigator.clipboard` does not exist | says why ("Diese Seite läuft ohne HTTPS ...") and keeps the way that needs no permission: Ctrl+V in the picture or in the field of the dialog (the `paste` event carries the text), and for copying from the VM the hidden text field with `document.execCommand('copy')` (works without HTTPS, but only straight after a tap or key) |
| `prompt` | a hint **before** the call ("Dein Browser fragt gleich nach Zugriff auf die Zwischenablage, bitte erlauben"), then `readText()`; the first read is only ever made from a user gesture (button "Aus der Zwischenablage holen", key "Text einfügen" of the key bar, Ctrl+V) |
| `denied` | no call; a hint with the way to allow it again (Chrome/Edge: lock icon, site settings, clipboard; Firefox: lock icon, permissions; Safari/iOS: Settings; Android Chrome: site settings) and the fallback Ctrl+V or long press in the field of the dialog |
| Safari, iOS | `readText` shows its own "Einfügen" bubble and only directly inside the gesture: hint before the call |
| Firefox | `readText` only in newer versions and with a bubble, otherwise the `paste` event; the hint says so |
| Android Chrome | reads only while the page is in the foreground and has focus |
| Page without focus when text arrives from the VM | nothing is tried (the browser refuses it); the text waits in the dialog and is stored once the focus returns |

The diagnosis shows the context (secure or not) and both permission states. Texts: `clipperm.*` and `clipdiag.*` in `i18n/de.js` and `en.js`. Where writing does not work, the toast "Text aus der VM kopiert (Symbol Zwischenablage)" opens the
dialog "Text senden" with the text and the button to copy it by hand.

## What the stream carries

| | Own stream |
| --- | --- |
| Browser to remote, image | RDP clipboard channel, `CF_DIB` (the browser sends a 24-bit BMP), **and** for a stored desktop connection with a connected agent additionally as a PNG through the agent (`POST /api/upload`, the clipboard helper `julos-clip.py` puts PNG and BMP on the clipboard of the desktop session) |
| Browser to remote, file or folder | RDP clipboard channel, `FileGroupDescriptorW` (folders as entries with relative paths, a folder before its contents); up to 200 files; the bytes lie as files in a folder of the server until the remote reads them (ranges) |
| Remote to browser, image | sent as soon as the remote copies (`PNG`, else `CF_DIB`) |
| Remote to browser, file or folder | the remote's list is shown (paths, sizes); "Laden" starts a browser download: one file as itself, a folder or several entries as a ZIP, streamed through the server (no size limit) |

Earlier versions also had a second path for the removed Guacamole engine: its guacd (1.6.0) forwarded only text on the RDP clipboard, so images and
files went through the session helper (`POST /api/upload`, `POST /api/upload-file`, `POST /api/clipboard/pull` and `tools/julos-clip.py`). The app does
not use that path any more; the endpoints and the helper stay because they are part of the agent API (see [AGENT_API.md](AGENT_API.md)).

What the remote application sees: an image is a normal clipboard image (Ctrl+V in any program); a file is a file on the clipboard
(Ctrl+V in the file manager or Explorer copies it). Priorities when the remote announces several formats: file list, then text (as before),
then image.

## Limits and errors (all texts in German, shown as a toast)

- **Image:** at most 40 MB as picked on the phone; the browser decodes it, shrinks it to at most 3200 px on the long side and sends a BMP
  of at most 32 MiB (server limit, message "Bild zu groß (höchstens 40 MB).").
- **Files and folders to the remote:** at most **200 files** (empty folders and the folders of the files do not count as files; at most 255 entries in all),
  each file at most **2 GiB** and all files of one paste together at most 2 GiB. The limit is `JULOS_CLIP_MAX_MB` (megabytes, default `2048`, at most
  `65536`; empty or nonsense means the default). The server tells the browser its limits right after connecting (message 14), so it refuses before sending
  anything ("Datei zu groß (höchstens 2 GB).", "Zu viele Dateien (höchstens 200).") and the server checks again ("Die Datei ist leer." for a single empty
  file; an empty file inside a folder or next to others is fine). Folders are read in the browser by drag and drop (`webkitGetAsEntry`, all levels) or by the
  button "Ordner senden" (`webkitdirectory`); the picker does not report empty folders, a drop does.
- **Files and folders from the remote:** at most 10000 entries per selection (more is not offered, as half a selection would be wrong), no limit on the size:
  the server holds one piece of 1 MiB at a time.
- **Progress:** sending shows "Sende name (3/12) … 12,3 MB von 40,0 MB (31 %)" for the whole paste; a download shows what the server has written to the browser so
  far ("Lade name … 12,3 MB von 40,0 MB (31 %)") and the browser's own download list shows it as well.
- **Cancel:** the bar "Sende … Abbrechen" stops a transfer in both directions (a cancel message; the server drops what it has and deletes the files). A transfer with
  no data for 60 s and a closed connection drop it as well. A download that the remote does not answer for 30 s fails ("Die VM antwortet nicht.").
- **No file support on the remote:** "Die VM unterstützt keine Dateien in der Zwischenablage." and "Die VM hat keine Zwischenablage (Kanal fehlt)." (chansrv dead, see DEVELOPMENT.md).
- **Remote changed its clipboard in between:** "Die Auswahl in der VM hat sich geändert." (the server locks the file list with a Lock request when the
  remote can, so a copy that is replaced by a newer one can still be fetched).
- **Disk:** a paste is only taken when the server's disk keeps 256 MiB free after it ("Auf dem Server ist nicht genug Platz."); a write error ends the transfer.

## Security

- Every route and the WebSocket need the signed-in session, as before; changing requests need the `X-JulOS` header and the app's own origin.
  The one new route is the download (below): `GET /api/remote/clip/<ticket>`.
- **Paths are never trusted, and tricks are refused, not trimmed.** A path from the browser (`ClipboardNames.SafePath`) is a relative path of at most 259
  characters and 16 levels; a leading separator, a drive (`C:`), a UNC path, `.` or `..` as a part, an empty part (`a//b`, `a/`) and a part of only dots and
  spaces make the whole paste fail with "Ungültiger Dateiname oder Pfad." (`ResultNotAllowed`), and nothing is written. Each part is cleaned like a file name
  (characters Windows does not allow become `_`, control characters and the ones that fake an extension become `_`, trailing dots and spaces go, device names
  (`CON`, `NUL` ...) get a prefix, at most 120 characters). Two entries that differ only in upper and lower case are refused ("Doppelter Name in der Auswahl.").
  On disk the files do not carry these names at all: they are `f0`, `f1` ... in a folder with a random name, so a path can never reach the disk. A file list
  from the remote is read with the strict `FileGroupDescriptor.PathParts`; entries with a way up, a drive or anything else odd are dropped, so a ZIP only ever contains checked paths.
- Names ending in `.desktop`, `.lnk`, `.scf`, `.url`, `.bat`, `.cmd`, `.exe`, `.msi`, `.com`, `.sh`, `.ps1`, `.vbs`, `.js` are refused, also inside folders: they would run when
  somebody opens them in the remote file manager. "Dateityp nicht erlaubt (.exe)."
- Sizes are checked against the declared size while data arrives (more than declared drops the transfer); chunks are at most 256 kB; the server
  holds at most one upload and one download per session. Uploaded files are written as they arrive (back pressure: the server reads the next chunk only
  after it has written the one before; the browser waits while its send buffer is over 1 MiB), the files of the last complete paste stay until a new one,
  the end of the session or a server restart; a folder a crashed server left behind is removed at the next start when it is older than 12 hours. The folder
  is `JULOS_CLIP_DIR`, by default `julos-clip` in the server's temp folder; give it a disk with room (2 GiB per open session at the default).
- **Download ticket:** the server answers a request for a download (message 14) with a ticket (message 9, state "bereit"); the browser opens
  `/api/remote/clip/<ticket>` itself, so the browser's own download manager saves it (no memory of the page, works for 2 GiB and more on a phone). The ticket
  is random (128 bit), valid once, 30 s, and only for the user of the session; the route refuses `Sec-Fetch-Site: cross-site`. A failure after the first byte aborts the
  connection, so a truncated ZIP never looks complete. At most one download per session runs at a time.
- A BMP from the browser is checked on its header (`DeviceIndependentBitmap.FromBmp`), the images of the helper path on their file header
  (png, jpg, webp, gif), never on the type the sender claims.
- Files from the remote are only listed (path, size) until the user asks for one; the browser saves it under the name the server gives (`Content-Disposition`, cleaned).
- The helper path: the pull answers only to the signed-in session, only when the user pressed the button, only with an image (never files or paths), at most
  14 MB, 30 requests per minute and user. Uploads: 20 per minute and user, and at most four uploads run at the same time (each one is held in memory).
- Files in `~/julos-uploads/files/` stay for 7 days (the helper deletes older ones); photos for 30 minutes, as before. The folder `files/` is capped at 2 GiB: when a new file would not fit, folders older than one day are removed first, and if it still does not fit the upload is refused (HTTP 507, "Der Ordner für Dateien ist voll").

## Stream engine protocol (additions, binary, big endian)

Browser to server: `10 begin` id(4) kind(1: 1 image as BMP, 2 file, 3 empty folder) index(1) count(1) size(8) path (UTF-8, relative, `/`); `11 chunk` id(4) bytes; `12 end` id(4);
`13 cancel` id(4); `14 download` batch(4) path (UTF-8; a file as itself, a folder or empty = everything as ZIP); `15 download cancel` batch(4).
Server to browser: `7 image` format(1: 1 png, 2 bmp) bytes; `8 entries` batch(4) count(2) then flags(1: bit 0 folder) size(8) pathLength(2) path each (count 0:
the list is gone; folders come before what is in them); `9 file state` batch(4) state(1: 0 ready, 1 running, 2 done, 3 failed) done(8) total(8) text (ready: the ticket; failed: the reason);
`10 result` id(4) code(1: 0 ok, 1 too big, 2 not allowed, 3 no clipboard or no file support on the remote, 4 other) text; `14 limits` maxFile(8) maxFiles(2) maxEntries(2) (once after connecting).
Documented in `ScreenProtocol.cs` and the header of `stream.js`. The bridge is `ClipboardBridge` (RDP clipboard channel on one side, these
messages on the other); the download is `ClipDownload` (streams into the HTTP response, `ClipboardZipWriter` for the ZIP: stored, no compression, data descriptors,
ZIP64 for entries of unknown size or 4 GiB and more and for more than 65535 entries).

## Browser

- `Ctrl+V` of an image or file, drag and drop of files **and folders** onto the picture, "Datei senden" and "Ordner senden" (menu row "Funktionen" and the clipboard dialog; a
  file picker that also works on the phone; the folder picker depends on the browser) and the existing photo actions all go through `sendToRemote` in `app.js`, which checks the paste against the
  server's limits (`planSend` in `clip.js`) and sends it over the stream (`client.sendTransfer`, one message series per entry).
- A single image is treated like a copied image and pasted with Ctrl+V when "Foto einfügen" is on (`autoPaste`); files are not pasted
  automatically, the toast says where to paste.
- Images and files from the remote arrive in a card in the clipboard dialog (preview, "Bild kopieren", "Speichern", or a list with one row per top-level entry: "Laden" for a file,
  "Als ZIP" for a folder, "Alles als ZIP laden" for more than one; `topLevel` in `clip.js`).
  The browser first tries `navigator.clipboard.write` for an image (needs HTTPS or localhost and focus); if that is refused the toast says
  "Bild aus der VM – tippen zum Kopieren" and tapping it opens the card.

## Behaviour of the xrdp test desktop (checked)

- xrdp converts only `image/bmp` on the remote side: an image copied as `image/png` alone does not reach the browser (GTK, as used by the
  helper, offers BMP next to PNG; other programs were not checked).
- File streaming needs `/dev/fuse` and `fuse3` in the target (xrdp mounts the client's files as `~/thinclient_drives/.clipboard`); the test
  target has both now. xrdp's `chansrv` keeps the capabilities of the **last connected client**: after a client that did not ask for files it
  offers no file streaming to the next client until `chansrv` restarts (new session or container restart). The stream then answers "Die VM
  unterstützt keine Dateien in der Zwischenablage." Images and text are not affected.

- **Folders and xrdp (checked against the test desktop):** xrdp's `chansrv` only takes flat lists. Entries with a path (`Projekt\a.txt`) and folders are skipped in both
  directions (`clipboard_c2s_in_files: skipping directory not supported` in its log: a pasted folder pastes nothing; a copied folder is not offered, only the plain files
  of a selection are). JulOS sends the list as the specification wants it, so a Windows host (or any server that follows MS-RDPECLIP) gets the structure; on xrdp use
  files, or archive the folder first. Many files, large files and the ZIP download work on xrdp.

## Checked by hand

Own instance from the branch against the dev PostgreSQL, a throwaway xrdp target with `/dev/fuse`: 63 MB in three files sent (progress shown, files on the server's disk, a copy
out of `~/thinclient_drives/.clipboard` has the same SHA-256 as what the browser sent); 30 MB file and a ZIP of three files downloaded through the ticket (SHA-256 and CRC match, a second
use of the ticket gives 404, `Content-Length` and `Content-Disposition` as documented); the server's folder is empty again after the session ends.

## Not verified

A real Windows host (folders both ways, Explorer paste of a file list, `CF_DIB` into Office, copying files in Explorer, files over 4 GiB with ZIP64), Android Chrome (file and folder
picker, share, permission prompts of `navigator.clipboard.write`, the download manager with the ticket) and a Linux VM other than the xrdp test desktop. Not measured: a 2 GiB transfer
(the limits are covered by tests, the largest hand test was 63 MB). The helper path (kept for the agent API) was checked with GTK only, not with other Linux programs.

## Fehlersuche

First look at the **diagnosis**: menu row "Funktionen" or the key "Zwischenablage", dialog "Text senden", at the bottom "Diagnose". It shows (never the content, only direction, time and length):

- *Kanal der VM*: ready, being set up, or missing. Missing means xrdp's `xrdp-chansrv` did not announce the clipboard channel within 8 s after connecting. In the VM: `pgrep -u "$USER" -a xrdp-chansrv` must list one process. If it died and was restarted
  (`tools/install-chansrv-watch.sh`), an open connection does not get the channel back: reconnect once. See [DEVELOPMENT.md](DEVELOPMENT.md) (long black picture).
- *Browser*: whether the page may use the clipboard at all (HTTPS or localhost).
- *Aus der VM*: when the last text came, how long it was and whether the browser could put it into its clipboard (or why not: `NotAllowedError`, no focus, no HTTPS).
- *In die VM*: when the last text went out, and whether the VM fetched it. "Noch nicht abgeholt" means the paste happened on our side but the VM did not ask: press Ctrl+V there (terminals
  take Ctrl+Shift+V), or the program in the VM asks for a target xrdp does not offer.
- *Letzter Fehler*: in plain words.

| Symptom | Likely cause | Check |
| --- | --- | --- |
| Nothing ever arrives in either direction, "Kanal der VM: fehlt" | `xrdp-chansrv` not running in the session, or restarted after the connection was made | `pgrep -u "$USER" -a xrdp-chansrv`, `journalctl -t julos-chansrv-watch`; reconnect |
| VM to browser works only sometimes | the browser refuses to write (no focus, Firefox/Safari without a recent click), diagnosis says "verweigert" or "keinen Fokus" | tap the toast, "Auf dem Handy kopieren"; use Chrome or Edge on HTTPS |
| Browser to VM pastes the old text | before 0.29: the key arrived before xrdp had taken the new list. Now the key waits for the remote's acknowledgement (at most 600 ms); an old server or VNC keeps the fixed 180 ms pause | paste again; the diagnosis shows whether the VM fetched the new text |
| Pasted text has `^M` or one line too many | an old JulOS (before this fix) sent CR LF to xrdp | update; `JULOS_CLIP_EOL=lf` |
| Copied text in the VM does not arrive although the diagnosis is green | the same text was given to the VM within the last 1.5 s (echo filter), or the VM only has it in `PRIMARY` (marked, not copied) | copy with Ctrl+C |
| `xrdp-chansrv` log: `clipboard_event_selection_request: unknown target ...` | a program in the VM asks xrdp for a target it does not offer (`TEXT`, `text/plain`, a numeric atom); xrdp refuses it and the program tries the next. Harmless, unless that program asks for nothing else | `~/.local/share/xrdp/xrdp-chansrv.<display>.log` in the VM |
| Another program owns the clipboard (a clipboard manager such as `xfce4-clipman`) and the text arrives twice or late | each owner change is a new copy for xrdp | stop the manager to test; a request that is never answered is dropped after 15 s |

Server side, one line per event at debug level, never with the text (`Zwischenablage: TextFetchedByVm (52)`: kind and length in characters). Switch it on with
`Logging__LogLevel__JulOS.Integrations.Display.ScreenRelay=Debug` in the environment of the server container, then `docker compose logs julos | grep Zwischenablage`.

### What was found and how it is covered

- Text from the browser went to xrdp with CR LF (reproduced against xrdp 0.10.1: `xclip -o` in the VM returned `\r\n`): fixed, test `ClipboardRealTextTests` (real xrdp) and `ClipboardBridgeTests`.
- Text copied in the VM that equalled the last text pasted from the browser was dropped for the rest of the session (echo filter without an end): fixed, same tests.
- One data request without an answer blocked every later one for the rest of the session (not reproducible with xrdp, which answers or fails by itself; found by reading, covered with a simulated server in `ClipboardConversationTests`).
- Silent failures on the browser side (text too long, no connection, Ctrl+V without text, clipboard write refused): now a message and a line in the diagnosis (`tests/frontend/clipdiag.test.mjs`, `clipperm.test.mjs` with a faked `navigator` for the states allowed, asks, denied, missing, and the fallback, `stream-clip.test.mjs`).
- Browser flows against a real xrdp desktop: `deploy/dev/e2e/tests/e2e.spec.mjs`, test 8 (text from the VM into `navigator.clipboard`, Shift+Home and Shift+Arrow selection and Ctrl+C in a real window, Ctrl+V with a line break into the VM, the diagnosis).

### Not verified

The real development VM with Chromium (not allowed to log in): suspected, not proven, are (1) a restarted `xrdp-chansrv` that leaves an open connection without the clipboard channel (the diagnosis now says so), (2) a program in the VM that
takes over `CLIPBOARD` after each copy, (3) Firefox and Safari on a phone, which only write to the clipboard right after a touch. Other browsers than Chromium, a Windows host (CR LF switching is covered by unit tests only) and clipboard managers were not tested end to end.

### Images do not paste into Chromium, Electron or GTK programs (xrdp offers only image/bmp)

The RDP clipboard carries a pasted picture as `CF_DIB`. xrdp's channel server (`xrdp-chansrv`, 0.10.x) turns that into the X selection target `image/bmp` and answers
nothing else: Chromium, Electron (for example a browser or a chat client in the desktop) and most GTK programs ask for `image/png`, which chansrv does not know, and the
chansrv log says `clipboard_event_selection_request: unknown target image/png`. The program then sees an empty clipboard. A file manager such as Thunar does not paste a
picture at all (it pastes files or text), its message "nothing on the clipboard that can be pasted here" is its normal answer to an image.

Since 0.10.2 the browser therefore sends the picture a second time through the agent when the connection is a stored desktop connection (the server picks the agent of the
connection's host): the clipboard helper in the desktop session sets the image with GTK, which offers PNG and BMP, after the RDP path, so it stays the owner of the clipboard.
Needs: the agent of the host is connected (host dialog) and `julos-clip.py` runs in the desktop session (autostart of the agent installer). Without an agent the server
answers 502 and only the RDP path remains. To check: the diagnosis in the dialog "Text senden" and the debug event `image-agent` (status 200 means the helper took it).

## Clipboard sheet on the phone

The chip and sheet (`clip-panel.js`, see [TOUCH.md](TOUCH.md)) follow the rules above: no read without a gesture unless the permission is `granted` and the page has focus, hint before the browser asks, content never in logs. "Type as text" sends characters as key presses (limit 4000, cancellable), "Send as file" uses the existing file path.
