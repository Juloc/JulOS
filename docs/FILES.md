# Files: the shared folder and every host over SFTP

The Files app (desktop window and phone page "Dateien") shows **places**:

- **Gemeinsamer Ordner** (shared folder): the one folder of the JulOS server (`JULOS_FILES_ROOT`, default `JULOS_UPLOAD_DIR`). Read only: view the list, search by name, download. This is the old behaviour, unchanged; see [ARCHITECTURE.md](ARCHITECTURE.md) for its guarantees.
- **Every host with an SSH connection**: the files of the SSH user, over **SFTP**, with the credentials you already saved for the connection. List, view text, download, upload (pick or drag and drop), new folder, rename, copy, move (also from host to host through the server) and delete.

A connection that is not SSH (RDP, VNC) is not a place. A host with several SSH connections offers a choice ("Host · Connection"). There is **no root access**: what you can do is what the SSH user can do. Root access is a later, separate step (see [Not built](#not-built-and-decisions)).

## What it needs

- An SSH connection (kind **Terminal**) with a host, port and user name. A saved password is used; if the connection has none, the app asks for it in a dialog, uses it for that one connection, **never stores it and never logs it**.
- SFTP enabled on the host (the OpenSSH default `Subsystem sftp`). Otherwise the app says so (`sftp_unavailable`).
- Reading works out of the box. **Writing is off by default** and has to be switched on, see below.

### Switching writing on

Everything that changes something (upload, overwrite, rename, move, copy, delete, new folder, and the `prepare` before it) is refused with `403 write_off` until you set

```
JULOS_FILES_WRITE=on
```

in the environment of the server container (Compose `environment:` or your `.env`, then recreate the container). The default and any other value (including `true` or `1`) is **off**, and a typo never switches writing on. With writing off the page shows a note and hides the write buttons; reading, viewing and downloading still work. A refused attempt is written to the history.

Other settings: `JULOS_FILES_MAX_MB` (largest file and largest operation, default 2048, range 1 to 65536) and `JULOS_FILES_MAX_SESSIONS` (open SFTP connections per user, default 4, range 1 to 16).

## Security model

| Rule | How |
|---|---|
| **Everything needs the session.** | All `/api/files/remote/*` routes sit behind `ApiGuard`: session cookie, and for every `POST` the header `X-JulOS: 1` and a matching `Origin`. Rate limits per user and minute (list 240, text 120, transfers 60, writes 120, open 20, plan 60). |
| **The host key is never accepted silently.** | The first connection shows algorithm and SHA-256 fingerprint and needs your explicit confirmation (`host_key_new`); the key is then saved in the connection (the same column the terminal uses, so both agree). A **changed** key stops with a warning that shows the known and the new key (`host_key_changed`); only an explicit second confirmation replaces it. This is stricter than the terminal, which remembers the first key without asking. |
| **Paths are normalised on the server, absolute only.** | `.` and empty parts are dropped; a relative path, `..`, control characters, more than 4096 characters or 128 parts are refused (`invalid_path`). An empty path or `~` means the home directory of the SSH user. New names must be one plain segment (no `/`, not `.`/`..`, no control characters, at most 255 bytes). Names with control characters or a `/` on the host are hidden from listings (and counted): a compromised host could otherwise list `../../x` and make a copy to another host write outside the chosen folder (SECURITY-REVIEW F-24). Names the host lists are never used as paths unchecked, also in copy, move and delete. |
| **Only what the SSH user may do.** | JulOS adds no rights. `denied` is shown as such. |
| **Links are shown and never followed.** | A symbolic link is listed as a link, with its kind (to a file, to a folder, broken). Counting, copying and deleting do not follow links: deleting a folder removes a link inside it as a link, never what it points at; copying skips links (a link to a **file** that you select directly is copied as that file). SSH.NET offers no `readlink`, so the target *path* is not shown. The SFTP library follows a link when it is given a path (delete, rename and `Get` would hit the target), so the app deletes and renames links through the handle of the directory listing, and it never replaces a link as a target (`target_is_link`); a real test against the dev SSH target checks that the target of a link survives. |
| **Size and count limits, nothing in memory.** | Lists: 2000 entries (`truncated`). Copy and delete: at most 10 000 entries and 20 levels, counted **before** anything changes. Download and upload stream in 128 KiB pieces and never hold a whole file; a transfer larger than `JULOS_FILES_MAX_MB` is cut and cleaned up, and a copy stops when the bytes actually received exceed it (a host that announces small sizes cannot get more through). Text view: first 1 MiB. |
| **Never a page.** | Downloads are always `attachment` and `application/octet-stream` (plus `nosniff`); there is no HTML or SVG preview. The text view sends the text as a JSON value and the page sets it with `textContent`. Binary files (a NUL byte in the first 8 KiB, or no valid UTF-8) are refused as text (`not_text`). |
| **Writes need a confirmation issued by the server (two steps).** | Like the host actions: first `POST remote/prepare` checks everything and answers with a **token** and a readable summary (host, target host, paths, counts, size, names that would be replaced, a folder with content); the page shows exactly that. Only then the action itself (`upload`, `mkdir`, `rename`, `delete`, `copy`) runs with the token. The token is valid for **60 s**, **once**, and bound to the **session**, the **action** and the **exact request** (connection, source paths, target, name; the count follows from the paths). A changed path or action, another session, a reuse or an expiry is refused (`confirmation_mismatch`, `confirmation_invalid`, `confirmation_expired`; a refused token is used up). Only its SHA-256 is kept in memory, at most 20 open per user. An upload confirmation covers exactly the prepared file names, each once. **Overwriting** and **deleting a folder with its content** stay a second explicit step: the page needs a tick, and the run may only ask for `overwrite` or `recursive` if the server showed it in `prepare` (otherwise `confirmation_mismatch`); the server never replaces anything otherwise (`exists`). There is **no recycle bin**. The root `/` is never deleted. |
| **Every write and every error is in the history, fail-closed.** | Table `files_audit` (migration `019`). Before the first writing step an entry `started` is written; **if it cannot be written, nothing is executed** (`audit_failed`, as with the host actions); the result (`ok`, `failed`, `cancelled`) follows as its own entry, and a job only counts as finished after its result entry is stored. Columns: user, host (the name of the connection), action, path (shortened to 300 characters, control characters replaced), result (`ok`, `failed`, `denied`, `cancelled`), detail code, number of items, time. **Never contents, never passwords.** The last 2000 rows per user are kept; the history survives deleting the connection. The page shows it under the clock icon (`GET /api/files/audit`). |
| **Sessions are short-lived and pooled.** | One SFTP session per user and connection, shared by all requests, closed after **60 s without use**, when the connection drops, **when the connection is edited or deleted, and at sign-out, "sign out all others" and a password change** (an unused session at once, one with a running job when the job ends; never handed out again); at most 4 per user (the longest unused closes first, otherwise `too_many_sessions`). While a place is open the page keeps it alive. A session never holds the password; after it closed, a connection without a saved password asks again. |

## The API

All paths are under `/api/files/`. Errors are `{"error": "<German text>", "code": "<code>"}` (plus `fingerprint`, `algorithm`, `known` for host key problems); the page translates the code.

| Method and path | What |
|---|---|
| `GET info` | `{local, write, maxBytes}` |
| `POST remote/open` | `{connection, password?, acceptHostKey?}` opens (or keeps alive) the session; `{home, hostKey: known\|remembered\|replaced, fingerprint, algorithm}`. Errors `password_required`, `logon_failed`, `host_key_new`, `host_key_changed`, `unreachable`, `timeout`, `sftp_unavailable` |
| `POST remote/close` | `{connection}` |
| `GET remote/list?c=&path=` | entries (`name`, `path`, `kind` = file/directory/link/other, `size`, `modified`, `mode`, `resolved`, `openable`), directories and links to directories first, natural order; `truncated`, `hidden` |
| `GET remote/text?c=&path=` | `{path, text, truncated, size}` |
| `GET remote/download?c=&path=` | the file, streamed |
| `POST remote/prepare` | step 1 of every write: `{action: upload\|mkdir\|rename\|delete\|copy\|move, connection, paths?, dir?, name?, names?, target?}`; checks like the action would, changes nothing; answers `{token, expiresIn, action, host, targetHost?, paths, dir?, name?, names?, files, directories, links, bytes, conflicts, nonEmpty}` (`conflicts`: names that exist at the target and would be replaced; `nonEmpty`: a folder with content is included) |
| `POST remote/upload?c=&dir=&name=&overwrite=1&token=` | raw body; written under a temporary name, then renamed; the token covers the prepared names, each once |
| `POST remote/mkdir`, `remote/rename` | `{connection, dir, name, token}`, `{connection, path, name, overwrite?, token}` (rename stays in the same folder) |
| `POST remote/delete`, `remote/copy` | `{connection, paths, recursive?, token}` and `{connection, paths, target: {connection, dir}, move?, overwrite?, token}` (`move: true` needs a `move` prepare); answer `202 {job}` |
| `GET remote/jobs/{id}`, `POST remote/jobs/{id}/cancel` | state (`running`, `done`, `failed`, `cancelled`), progress (items and bytes, current name), counts, skipped links and skipped existing files |
| `GET audit?limit=` | the history |

Copy, move and delete run as **jobs** (at most two running per user; finished ones stay for ten minutes). The page polls the job and can cancel it.

## Host to host

`copy` with a target on another connection: the server reads from the source host over SFTP and writes to the target host over SFTP **in a stream** (nothing is stored on the server). The file is first written under a temporary name `.julos-<random>.part` in the target folder (created new, never overwritten), checked for the expected size, then renamed (`posix-rename`, which replaces atomically on OpenSSH; otherwise the old file is deleted first). A failure, a cancel or a lost connection removes the temporary file; what was already finished stays. A **move** between hosts copies first and deletes at the source only what arrived safely, so a link that was skipped (and its folder) stays. Both hosts need an open session (open the target in the target chooser; a password-less connection asks there).

## Limits and honest edges

- Copy does not keep permissions, owner or modification time; links and special files are skipped (and counted in the result).
- Rename onto an existing file uses `posix-rename`; a server without it falls back to delete and rename (not atomic). Existence checks before a write are not atomic either; they are a courtesy, the server of the host stays the judge.
- A text file larger than 1 MiB shows its beginning. Nothing is edited in the app.
- The target path of a link is not shown (no `readlink` in the SFTP library).
- Listing a directory with more than 2000 entries shows the first 2000.

## Not built, and decisions

- **Root access** is a later, separate step: a word `files-root` in the permission file of the root helper (per host opt-in, like `updates` or `services`), a confirmation, and the history, in the model of [HOST-AGENT.md](HOST-AGENT.md). Today nothing here uses root and the agent is not involved at all.
- **Key authentication** for the files is not offered; the saved connections hold passwords. Decision: use the same credentials as the terminal, add nothing new.
- **Recycle bin, editing, thumbnails, sorting by size and date, search on hosts**: see [IDEAS.md](IDEAS.md).
- Decision: the first host key must be confirmed (stricter than the terminal) because a file manager moves more data than a shell you watch.
- Decision: a typed password is held only for the one connection attempt; the pool keeps the authenticated session, never the password.

## Tests

`tests/JulOS.Backend.Tests`: `FileTransferTests` (the logic against an in-memory file system: paths, counting, atomic write, copy, move, delete, links, limits, preview), `SftpPoolTests` (sharing, idle, limits, leaks), `RemoteFilesHttpTests` (the whole server: session, header and origin, host key flow, passwords, listing, text, download, prepare and the bound confirmation, upload, rename, jobs, cancel, history, fail-closed audit, pool closing, write switch), `RealSftpFilesTests` (a real SFTP target of the dev stack: list, read, write, rename, delete, links, errors, a large folder, copy between two sessions, cancel, a changed host key). `tests/frontend/files.test.mjs` covers the page logic.
