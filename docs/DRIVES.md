# Drive redirection: a server folder as a drive in the desktop

The own stream engine (not Guacamole) can hand **one folder of the JulOS server** to the remote desktop as a drive (RDP device redirection,
RDPDR / MS-RDPEFS). On a Windows target it shows up as a network drive `JULOS on JulOS`, on xrdp under `~/thinclient_drives/JULOS`. It is the
other direction from the file box: it does not touch a folder of the *browser* (a browser has none to give).

**Off by default, in two places.** Nothing is shared until the server has a root folder *and* a connection asks for it.

## Setting it up

1. Mount one folder into the container and name it in the server settings:

   ```yaml
   services:
     julos:
       volumes:
         - /srv/julos-share:/share:ro      # ":ro" is a second lock; leave it out only if writing is wanted
       environment:
         JULOS_DRIVE_ROOT: /share
   ```

   Mount **only what may be shared**: whoever controls the desktop can read everything below the root (see Security). The user the server
   runs as needs read (and for writing, write) rights on it. Empty or `off` (default): no drive for any connection. The root is checked
   at start (absolute path, an existing folder, not `/`, symlinks resolved once); a bad value is logged and switches the drive off.
2. In the connection dialog (desktop with "Stream"), switch on **"Ordner des Servers als Laufwerk freigeben"**. Then, separately,
   **"Schreiben erlauben"**. Both are off for every new connection. Without a root on the server the dialog says so and the switches stay off.
3. Connect. A toast says "Laufwerk vom Server ist freigegeben (nur Lesen)" (or "Lesen und Schreiben"). The root path is never sent to the
   browser; the connection holds the two switches only (`drive_share`, `drive_write`; writing needs the share).

## What it does

- **Read only is the default and holds for every kind of request:** opening for write, create, overwrite, delete-on-close, rename, delete,
  truncate and set-information all fail with `STATUS_ACCESS_DENIED`, already at open. A read-only drive reports no free space.
- **With "Schreiben erlauben":** create files and folders, write, truncate, rename (inside the root only), delete (a folder only when empty;
  the root is never deleted or renamed). A file is at most **1 GiB** (more: `STATUS_DISK_FULL`). There is no quota for the sum, so size the
  folder's disk accordingly. 256 open handles per session at most.
- The disk is read on a thread of its own (queue of 256 requests); a slow disk never stops the picture. Reads are answered in pieces of at most 1 MiB.
- Names are exactly as on the disk (Linux is case sensitive: `README.TXT` is not `readme.txt`); `.` and `..` are listed, directory
  change notifications are never answered. Tested against the scripted server of the unit tests; not yet on a real Windows host.

## Security: the fence (`DriveJail`, `JailFs`, `DriveService`)

Everything the remote sends is hostile. A path is **accepted or refused, never repaired**.

| Attempt | Result |
| --- | --- |
| `..`, `.`, `\..\x`, `\a\..\b`, `...`, `.. ` | refused (no name starts with a dot) |
| absolute (`C:\x`, `\\server\share`, `\\?\C:\x`, `\/etc`, `//etc`), relative without a leading separator, an empty part (`\a\\b`) | refused |
| a forward slash separates like a backslash (xrdp sends `/datei.txt`); `/etc/passwd` therefore means the folder `etc` *below the share*, never one of the server | accepted, the same rules for every part |
| hidden names (`.env`, `.ssh`, `.git`, anything with a leading dot) | refused to open or create, and not listed |
| names Windows would change (trailing dot or space), `: * ? " < > \|`, NUL, control characters, `file.txt:stream` | refused |
| Unicode tricks: right-to-left override U+202E, other direction marks and isolates, zero width characters, BOM, soft hyphen, line and paragraph separators, private use, unassigned, lone surrogates, U+FFFD | refused |
| look-alikes that become dangerous under NFKC: fullwidth `．．` (`..`), `／`, `＼`, `：`, division slash U+2215, fraction slash U+2044, one dot leader U+2024 and others | refused (checked raw and after NFKC) |
| names over 255 UTF-8 bytes, more than 32 parts, paths over 1024 characters | refused |
| a symlink anywhere in the existing part of a path (also one that points *into* the root), a dangling one, a file symlink | refused, not listed |
| a link swapped in between the check and the open | `openat2` with `RESOLVE_BENEATH \| NO_SYMLINKS \| NO_MAGICLINKS` relative to a descriptor of the root fails; afterwards the descriptor's real path (`/proc/self/fd`) must still be inside the root. Nothing is truncated before that check |
| rename or create toward a hidden, foreign or linked place | refused |
| too many handles, wild file ids, short or random messages | an NTSTATUS answer, never an exception; fuzz test included |

- **Residual risk, stated plainly:** (1) a hardlink that somebody with write access to the folder made *before* is not detected (a second name
  for the same file is not a path trick). (2) Where `openat2` is not available (kernel before 5.6, a strict seccomp profile) the fallback opens
  with `O_NOFOLLOW`, checks the real path afterwards and deletes a file it just created outside; the check before the open still refuses every symlink, so
  this only matters against someone who can create links in the folder at the same moment. `JailFs.UsesOpenat2` tells which one is in use.
  (3) Delete, rename and set-times go by path after the check (there is no `unlinkat` call): the same race, the same people.
  Keep the shared folder free of anything that is not meant to be shared, and out of reach of other users, and none of these matter.
- Windows hosts: a share the user did not ask for appears in Explorer only when the connection switched it on.
- Guacamole connections never get the drive (the switch is forced off for them; its own drive feature is not used).

## Verified, and how

- **Against a real xrdp 0.10 desktop** (throwaway container with `/dev/fuse`, `SYS_ADMIN` and fuse3; `ScreenStreamDriveTests` holds a session with the drive open, then
  `docker exec` into the desktop): chansrv logs `Detected remote drive 'JULOS'`, `~/thinclient_drives/JULOS` lists the files (not `.env`, not a symlink to `/etc`),
  a 3 MB file reads back with the same SHA-256, every write attempt on the read only drive fails and changes nothing on the server. With writing on: `touch`, `echo >`,
  `echo >>`, `mkdir`, `cp` of a 2 MB file (same SHA-256 on the server), `mv` into a folder, `rm`, `rmdir` (refused while not empty) and overwriting a file work;
  creating a hidden name and anything through a symlink does not. Learned there: xrdp asks for missing files and expects `STATUS_NO_SUCH_FILE` (it turns
  `STATUS_OBJECT_NAME_NOT_FOUND` into an I/O error), and it sends paths with `/`.
- **Not verified:** a Windows target (Explorer shows a network drive `JULOS on <computer name>`; the messages follow MS-RDPEFS and FreeRDP's answers, but it was never
  connected), the phone, a Windows host that forbids drive redirection (the code announces an empty list then; unit tested only).
  The dev target needs `/dev/fuse` and fuse3 for the mount (see DEVELOPMENT.md); a container started before that was added does not mount anything.

## Tests

`tests/JulOS.Display.Tests/Drive` (no RDP target needed, real folders and links in a temp directory): `DriveJailTests` (accepted names, every row of the
table above), `JailFsTests` (both opening ways), `DriveServiceTests` (all request kinds, read only in full, escapes, caps, fuzz);
`Channels/RdpdrDriveTests` (handshake, device list, answer to the device, requests only after it); `RemoteHttpTests` (switches, root never
sent, ticket); `LogicTests` (rules). Run with `deploy/dev/test.sh` and `deploy/dev/test-display.sh`.
