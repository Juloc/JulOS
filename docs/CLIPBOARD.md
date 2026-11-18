# Clipboard: images and files

Text has always travelled both ways (see [ARCHITECTURE.md](ARCHITECTURE.md), display engines). This page is about images and files. The
text path is unchanged.

## What each engine can carry

| | Guacamole (`guac`) | Own stream (`stream`) |
| --- | --- | --- |
| Browser to remote, image | upload helper (`/api/upload` + `tools/julos-clip.py`, as for photos) | RDP clipboard channel, `CF_DIB` |
| Browser to remote, file | upload helper: the file lands in the shared upload folder and its path is put on the remote clipboard as a file list | RDP clipboard channel, `FileGroupDescriptorW`; the remote reads the bytes when it pastes |
| Remote to browser, image | agent pull (`GET /v1/clipboard`, only when the user asks for it) | RDP clipboard channel, `PNG` or `CF_DIB`, sent as soon as the remote copies |
| Remote to browser, file | not possible (guacd has no file clipboard; its drive needs a folder configured) | RDP clipboard channel, the list is shown and a file is fetched when the user taps it |

Why: guacd 1.6.0 forwards only text on the RDP clipboard (checked: a `image/png` clipboard stream does not change the remote clipboard).

## Limits and errors (all texts in German, shown as a toast)

- Image: at most 14 MB as sent from the phone (as before). The browser decodes it, shrinks it to at most 4096 px on the long side and
  sends a 24-bit BMP (stream) or a PNG/JPEG (helper). Over the limit: "Bild zu groß (höchstens 14 MB)."
- File: at most **64 MB** per file, at most **10 files** and 64 MB per drop. Checked in the browser before anything is sent and again
  in the server. "Datei zu groß (höchstens 64 MB)." / "Zu viele Dateien (höchstens 10)."
- Folders are not sent (a dropped folder is skipped: "Ordner werden nicht übertragen").
- A transfer can be cancelled (the browser sends a cancel message, the server drops what it has); a stalled transfer (60 s without data)
  and a closed connection drop it as well.
- A remote that has no clipboard channel (xrdp's `chansrv` dead, see DEVELOPMENT.md) answers "Die VM hat keine Zwischenablage (Kanal fehlt)".

## Security

- Every route and the WebSocket need the signed-in session, as before. Nothing new listens anywhere.
- **Names are never trusted.** A name from the browser or the remote is cut to its last segment, characters Windows does not allow become
  `_`, a trailing dot or space and device names (`CON`, `NUL` ...) are fixed, the length is limited. The helper path
  builds `<upload folder>/files/<random id>/<name>` and checks the full path stays inside the folder.
- Names ending in `.desktop`, `.lnk`, `.scf`, `.url`, `.bat`, `.cmd`, `.exe`, `.msi`, `.com`, `.sh`, `.ps1`, `.vbs`, `.js` are refused: they would run
  when somebody opens them in the remote file manager. A refusal says which: "Dateityp nicht erlaubt (.exe)."
- Sizes are checked against the declared size while data arrives (more than declared drops the transfer); chunks are at most 256 kB;
  the server holds at most one upload and one download per session in memory.
- Image bytes are checked on their header (png, jpg, webp, gif, bmp), never on the type the sender claims.
- Files from the remote are only listed (name, size) until the user asks for one; the browser stores it with the sanitized name.
- The agent clipboard pull answers only to the signed-in session, only when the user pressed the button, never returns more than 14 MB and
  only images (no files, no paths).

## Stream engine protocol (additions, binary, big endian)

Browser to server: `10 begin` id(4) kind(1: 1 image as BMP, 2 file) index(1) count(1) size(8) name; `11 chunk` id(4) bytes;
`12 end` id(4); `13 cancel` id(4); `14 file request` batch(4) index(1); `15 file cancel` batch(4).
Server to browser: `7 image` format(1: 1 png, 2 bmp) bytes; `8 remote files` batch(4) count(1) then size(8) nameLength(2) name each
(count 0: the list is gone); `9 file chunk` batch(4) index(1) flags(1: 1 last, 2 failed) bytes (a failure carries the reason as text);
`10 result` id(4) code(1: 0 ok, 1 too big, 2 not allowed, 3 no clipboard on the remote, 4 other) text.
Documented in `ScreenProtocol.cs` and the header of `stream.js`.

Priorities when the remote announces several formats: file list, then text (as before), then image.

## Browser

- `Ctrl+V` of an image or file, drag and drop of files onto the picture, "Datei senden" in the menu (a file picker that also works on the
  phone) and the existing photo actions all go through one function (`sendToRemote`) that picks the engine's path.
- Images and files from the remote arrive in a card in the clipboard dialog (preview, "Kopieren", "Speichern"). The browser first tries
  `navigator.clipboard.write` (needs HTTPS or localhost, focus and a recent tap); if that is refused the toast says
  "Bild aus der VM – tippen zum Kopieren" and the card waits.
