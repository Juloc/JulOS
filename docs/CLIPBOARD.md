# Clipboard: images and files

Text has always travelled both ways (see [ARCHITECTURE.md](ARCHITECTURE.md), display engines). This page is about images and files. The
text path is unchanged.

## What each engine carries

| | Guacamole (`guac`) | Own stream (`stream`) |
| --- | --- | --- |
| Browser to remote, image | upload helper (`POST /api/upload` + `tools/julos-clip.py`, as for photos) | RDP clipboard channel, `CF_DIB` (the browser sends a 24-bit BMP) |
| Browser to remote, file | `POST /api/upload-file`: the file lands in `~/julos-uploads/files/<id>/<name>`, the helper offers it as a file list (`x-special/gnome-copied-files`, `text/uri-list`); one file at a time | RDP clipboard channel, `FileGroupDescriptorW`; the remote reads the bytes when it pastes; up to 10 files at a time |
| Remote to browser, image | on request: "Bild aus der VM holen" asks the helper (`POST /api/clipboard/pull`, answer PNG) | sent as soon as the remote copies (`PNG`, else `CF_DIB`) |
| Remote to browser, file | not possible (guacd has no file clipboard; its drive needs a folder configured) | the remote's file list is shown (name, size); a file is fetched when the user taps "Laden" |

Why two paths: guacd 1.6.0 forwards only text on the RDP clipboard (checked against the test desktop: an `image/png` clipboard stream
does not change the remote clipboard).

What the remote application sees: an image is a normal clipboard image (Ctrl+V in any program); a file is a file on the clipboard
(Ctrl+V in the file manager or Explorer copies it). Priorities when the remote announces several formats: file list, then text (as before),
then image.

## Limits and errors (all texts in German, shown as a toast)

- **Image:** at most 40 MB as picked on the phone; the browser decodes it, shrinks it to at most 3200 px on the long side and sends a BMP
  of at most 32 MiB (server limit, message "Bild zu groß (höchstens 40 MB)."). On the Guacamole path the photo is downscaled to 2000 px and
  sent as JPEG/PNG as before (server limit 14 MB).
- **File:** at most **64 MB** per file, at most **10 files** and 64 MB in all per drop. Checked in the browser before anything is sent and
  again on the server ("Datei zu groß (höchstens 64 MB).", "Zu viele Dateien (höchstens 10).", "Die Datei ist leer."). Folders are skipped
  ("Ordner werden nicht übertragen"). Guacamole: one file at a time ("Mit dieser Bildübertragung geht nur eine Datei auf einmal.").
- **Cancel:** the bar "Sende … Abbrechen" stops a transfer in both directions (a cancel message; the server drops what it has). A transfer with
  no data for 60 s and a closed connection drop it as well. A download that the remote does not answer for 30 s fails ("Die VM antwortet nicht.").
- **No file support on the remote:** "Die VM unterstützt keine Dateien in der Zwischenablage." and "Die VM hat keine Zwischenablage (Kanal fehlt)." (chansrv dead, see DEVELOPMENT.md).
- **Remote changed its clipboard in between:** "Die Auswahl in der VM hat sich geändert." (the server locks the file list with a Lock request when the
  remote can, so a copy that is replaced by a newer one can still be fetched).

## Security

- Every route and the WebSocket need the signed-in session, as before; changing requests need the `X-JulOS` header and the app's own origin.
  Nothing new listens anywhere.
- **Names are never trusted** (`ClipboardNames`): from a name only the last path segment is kept, characters Windows does not allow become `_`,
  control characters and the characters that fake an extension (right-to-left override) become `_`, trailing dots and spaces go, device names
  (`CON`, `NUL` ...) get a prefix, the length is limited to 120. The helper path is `<upload folder>/files/<random id>/<name>` and the full path
  is checked to stay inside that folder. A file list from the remote is read with the strict `FileGroupDescriptor.PathParts` first; entries with
  a way up, a drive or anything else odd are dropped, folders are not transferred.
- Names ending in `.desktop`, `.lnk`, `.scf`, `.url`, `.bat`, `.cmd`, `.exe`, `.msi`, `.com`, `.sh`, `.ps1`, `.vbs`, `.js` are refused: they would run when
  somebody opens them in the remote file manager. "Dateityp nicht erlaubt (.exe)."
- Sizes are checked against the declared size while data arrives (more than declared drops the transfer); chunks are at most 256 kB; the server
  holds at most one upload and one download per session in memory (at most 64 MB each).
- A BMP from the browser is checked on its header (`DeviceIndependentBitmap.FromBmp`), the images of the Guacamole path on their file header
  (png, jpg, webp, gif), never on the type the sender claims.
- Files from the remote are only listed (name, size) until the user asks for one; the browser saves it under the cleaned name.
- The pull answers only to the signed-in session, only when the user pressed the button, only with an image (never files or paths), at most
  14 MB, 30 requests per minute and user. Uploads: 20 per minute and user, and at most four uploads run at the same time (each one is held in memory).
- Files in `~/julos-uploads/files/` stay for 7 days (the helper deletes older ones); photos for 30 minutes, as before. The folder `files/` is capped at 2 GiB: when a new file would not fit, folders older than one day are removed first, and if it still does not fit the upload is refused (HTTP 507, "Der Ordner für Dateien ist voll").

## Stream engine protocol (additions, binary, big endian)

Browser to server: `10 begin` id(4) kind(1: 1 image as BMP, 2 file) index(1) count(1) size(8) name; `11 chunk` id(4) bytes; `12 end` id(4);
`13 cancel` id(4); `14 file request` batch(4) index(1); `15 file cancel` batch(4).
Server to browser: `7 image` format(1: 1 png, 2 bmp) bytes; `8 remote files` batch(4) count(1) then size(8) nameLength(2) name each (count 0:
the list is gone); `9 file chunk` batch(4) index(1) flags(1: 1 last, 2 failed) bytes (a failure carries the reason as text); `10 result` id(4)
code(1: 0 ok, 1 too big, 2 not allowed, 3 no clipboard or no file support on the remote, 4 other) text.
Documented in `ScreenProtocol.cs` and the header of `stream.js`. The bridge is `ClipboardBridge` (RDP clipboard channel on one side, these
messages on the other).

## Browser

- `Ctrl+V` of an image or file, drag and drop of files onto the picture, "Datei senden" (menu row "Funktionen" and the clipboard dialog; a
  file picker that also works on the phone) and the existing photo actions all go through `sendToRemote` in `app.js`, which picks the
  engine's path (`client.nativeClipboard` is true for the stream client).
- A single image is treated like a copied image and pasted with Ctrl+V when "Foto einfügen" is on (`autoPaste`); files are not pasted
  automatically, the toast says where to paste.
- Images and files from the remote arrive in a card in the clipboard dialog (preview, "Bild kopieren", "Speichern", or a file list with "Laden").
  The browser first tries `navigator.clipboard.write` for an image (needs HTTPS or localhost and focus); if that is refused the toast says
  "Bild aus der VM – tippen zum Kopieren" and tapping it opens the card.

## Behaviour of the xrdp test desktop (checked)

- xrdp converts only `image/bmp` on the remote side: an image copied as `image/png` alone does not reach the browser (GTK, as used by the
  helper, offers BMP next to PNG; other programs were not checked).
- File streaming needs `/dev/fuse` and `fuse3` in the target (xrdp mounts the client's files as `~/thinclient_drives/.clipboard`); the test
  target has both now. xrdp's `chansrv` keeps the capabilities of the **last connected client**: after a Guacamole (guacd) connection it
  offers no file streaming to the next client until `chansrv` restarts (new session or container restart). The stream engine then answers "Die VM
  unterstützt keine Dateien in der Zwischenablage." Images and text are not affected.

## Not verified

Android Chrome (file picker, share, permission prompts of `navigator.clipboard.write`), a real Windows host (Explorer paste of a file list, `CF_DIB`
into Office, copying files in Explorer) and a Linux VM other than the xrdp test desktop. Remote image to browser over Guacamole was checked with the
helper only (GTK clipboard), not with other Linux programs.
