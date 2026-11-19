# Architecture

Decisions for the server rewrite. The **basic structure stays the one used in the other projects** (solution layout, the split into
host, backend library, integrations, workers and tests, the folders around them). Optimising happens *inside* that structure, never by removing it.

## Principles

1. **Keep the skeleton.** Same solution layout and folder names as the sibling projects, so every project reads the same.
2. **Write it ourselves where that is reasonable.** Full control, small attack surface, speed. A dependency has to earn its place.
3. **Do not hand-roll cryptography or the database driver.** Mistakes there are invisible. Those use the framework or one small, well known library.
4. **Fast by default:** minimal APIs, JSON source generation (no reflection), pooled buffers in the relay, one shared database data source, ReadyToRun, a trimmed non-root runtime image.

## Stack

| Part | Choice |
| --- | --- |
| Server | C# on .NET 10, ASP.NET Core host |
| Browser | Plain ES modules, the Apache Guacamole JS client (desktops) and xterm.js (terminals), served as static files (the PWA) |
| Database | PostgreSQL, plain SQL through Npgsql, SQL migrations in `database/`, no ORM |
| Remote display | `guacd` (container, no published port); the server relays the Guacamole protocol itself. Optional second engine per connection: own RDP client with H.264 (see "Display engines") |
| Terminal | SSH client in the server (SSH.NET behind `ISshSession`), text to the browser, drawn there by xterm.js (see "Native terminal") |
| Linux agent | Python (GTK/Wnck) behind the language-neutral agent API; a Windows agent later in C# |
| Delivery | One image on ghcr.io (amd64, arm64) and a compose file |

## Layout (same shape as the sibling projects)

```
JulOS.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/
  JulOS.Frontend/       ASP.NET host: static client (PWA), HTTP API, WebSocket endpoint, security headers
  JulOS.Backend/        class library: Services, Logic, Data (PostgreSQL access), accounts, sessions, connections
  JulOS.Integrations/   outside communication: the guacd relay with its one-time tickets, the agent client (Unix socket)
  JulOS.Workers/        background jobs: purge expired sessions and tickets, upload clean-up
tests/
  JulOS.Backend.Tests/  unit and integration tests (including a real guacd and PostgreSQL in containers)
database/               numbered SQL migrations
deploy/                 compose files, Caddy examples
docs/                   vision, scope, ideas, architecture, agent API
tools/                  Linux agent, installers, user tool
```

Inside a project, code is grouped by feature (Auth, Remote, Files) and, in the backend, by Services / Logic / Data as in the siblings.

## Written ourselves

The guacd protocol relay and its handshake, the SSH relay (WebSocket protocol, host key policy), one-time connection tickets, sessions with rotating cookies, TOTP (RFC 6238) and recovery codes,
login lockout, the migration runner, the SQL access helpers, security headers and the CSRF/origin checks, the agent client, the upload path.

## Not written ourselves, and why

| What | Use | Why |
| --- | --- | --- |
| HTTP/TLS server | Kestrel (part of the framework) | Mature and fast |
| PostgreSQL wire protocol | Npgsql | A driver is not worth hand-writing |
| Cryptographic primitives (HMAC, AES, random) | .NET base library | No hand-rolled crypto |
| Password hash | Argon2id from one small library (Konscious), hash format versioned | Memory-hard hash is the one place where a custom version would be risky |
| RDP engine | `guacd` | Years of protocol work |
| SSH client | SSH.NET (MIT) behind our `ISshSession` | A full SSH client (key exchange, ciphers, channels) is large and a crypto building block; the interface keeps it replaceable and the relay testable without a network |
| Terminal emulator in the browser | xterm.js (MIT, vendored in `wwwroot/lib/xterm`, no CDN) | Terminal emulation (escape sequences, unicode widths, selection, scrollback, renderers) is years of work; we write the session, key mapping, touch handling and host key dialog around it |

Target for runtime NuGet packages: **three** (Npgsql, the Argon2 library and SSH.NET). Test packages are separate. Versions are centralised in `Directory.Packages.props`.

## Security model

- **Account:** Argon2id for the password, TOTP as second factor, ten one-time recovery codes. Passkeys (WebAuthn) later, see `IDEAS.md`.
- **Session:** 256-bit random value in a cookie (`__Host-` prefix, `Secure`, `HttpOnly`, `SameSite=Strict`), stored only as SHA-256 in PostgreSQL. Idle timeout 30 days, absolute 180 days, rotated every 12 hours with a short grace for parallel requests. Survives server and browser restarts.
- **Lockout:** per address and per account with growing delay.
- **Every API and the WebSocket need a session.** State-changing requests also need a custom header and a matching `Origin`. Static files and the manifest are public so the app can be installed.
- **Connection ticket instead of an encrypted token:** the browser asks for a one-time ticket (random 128 bit, 30 s, kept in memory with the connection settings) and opens the WebSocket with it. The ticket only works for the user who asked for it, and the WebSocket also checks the `Origin`.
- **Target passwords:** by default the password is typed per connection, held in memory for at most 30 s, never logged or stored. On request a connection can keep its password: it is encrypted with AES-256-GCM (key `JULOS_SECRET_KEY`, 32 random bytes, only in the server settings, never in the database; the connection id is bound in as associated data), only the server decrypts it when a ticket is made, and no API ever returns it (the list only says `hasPassword`). Changing host, port, kind or user name of a connection deletes its stored password, so it cannot be redirected to another target. Instead of the variable the server can keep the key in a file: with `JULOS_DATA` set (the image uses `/data`) it creates `secret.key` there once (32 random bytes, readable only by the server) and keeps using it, so a stack needs no secret in its Compose file; the variable wins when both exist. Back up that volume apart from the database. Without a key, storing is off. The price: whoever holds a valid session of the app can open that connection, which is why the login comes first.
- **Open connections follow the session:** every minute the relay checks that the session (by its stable id, which survives cookie rotation) is still valid, so signing out or ending a session closes its desktop and terminals.
- **Headers:** strict CSP, `nosniff`, `frame-ancestors 'none'`, HSTS over HTTPS, no referrer.
- **The first account is created on the setup screen**, guarded by the one-time setup code from the server log, so only whoever can read the log can claim the instance. Setup closes after the first account. The console tool (`tools/julos-user.sh`) is the recovery path; passwords are typed in the terminal, never passed as an argument.

## Test instance (a mirror of live, used for every change)

`deploy/dev/compose.yaml` starts the same stack as production with its own PostgreSQL volume, `guacd`, the server built from the same
Dockerfile, plus **throwaway targets**: an SSH server and an RDP desktop (both with generated test accounts that exist only there).
Everything is tested there first, including a real browser run (login, setup, connect to the terminal and the desktop). Integration tests use the same containers.
Production and the test instance differ only in ports, volumes and secrets.

## First start and connections

- **First start:** with no account the server prints a one-time **setup code** to its log. The browser shows a setup screen asking for that code, a user name and a password,
  then shows the authenticator secret (link and key) and ten recovery codes, and finishes only after a valid authenticator code was entered. Afterwards the setup screen is gone.
  The console tool `tools/julos-user.sh` stays as the recovery path (reset password or authenticator).
- **Connections:** a user keeps a list of connections: name, kind (**Desktop** = RDP, **Terminal** = SSH, **Screen** = VNC later), host, port, user name. The password is typed per connection (a password manager can fill it) or, on request, stored encrypted (see the security model). A connection for the machine the server runs next to is offered at setup. The target host is chosen by the signed-in
  user, so the server may reach anything the signed-in user names; an optional allow-list in the configuration restricts that.
- **Home:** after login the user sees the list, can add, edit and delete connections, and starts one with a tap. The remote view is the existing client.

## Host information and files (desktop UI, read-only)

Both are plain read-only API calls behind the session check (no body, no state); nothing here writes to the host.

- **`GET /api/system/overview`**: CPU load (percent), memory, disk and network rates. `HostSamplerWorker` samples every 2 s (`/proc/stat`, `/proc/meminfo`, `/proc/net/dev`, the file system of the upload folder) and `HostMonitor` turns the counters into rates over the last 6 s at most; a rate needs two samples, so a field is left out (not shown as 0) when there is none or the source cannot be read. The parsers are pure functions on text (`ProcParsers`), tested without Linux.
- **What "host" means inside the container:** `/proc/stat` and `/proc/meminfo` are not namespaced, so **CPU and memory are those of the machine running the Docker daemon** (the server VM), not of a cgroup limit of the container. **Disk** is the file system that holds the upload folder (`JULOS_UPLOAD_DIR`, a bind mount of a folder of that machine in the live setup, so its disk). **Network** is the container's own network namespace: `eth0` of the JulOS container, which means the traffic JulOS itself causes (browser sessions, RDP and SSH relays), not the whole machine's. With `network_mode: host` it would be the machine's. The tile says so.
- **`GET /api/files/list|search|download`**: a read-only file browser for **one** root folder (`JULOS_FILES_ROOT`, default `JULOS_UPLOAD_DIR`; the value `off` switches it off). `FileBrowser` guarantees: paths are relative to the root and `..` is refused; every path is resolved step by step including symlinks (chains, relative targets, loops) and refused when the real result is outside the real root (a link to a target inside the root is fine); names that start with a dot (`.agent`, `.env`) are invisible and unreachable; a download opens the file and then checks where the descriptor really points (`/proc/self/fd`), which closes the gap between check and open; downloads are always `attachment` and `application/octet-stream`, so a stored HTML or SVG file never runs as a page of this address; listings are capped (2000 entries, search 200 hits, 50 000 visited names, depth 16), search never follows links. Not covered: hardlinks (a second directory entry for the same file inside the root) and special files such as pipes. The folder is shared with the target machine's user, so anything that user puts there is visible to the signed-in user; that is the point of a shared folder, and why the root is a single, explicit folder and not the home directory.

## Display engines (per connection)

A desktop connection has an `engine` setting (`connections.engine`, migration 004; `guac` is the default, `stream` the new one). It is chosen in the
connection dialog ("Bildübertragung"); terminals do not use these engines at all (see "Native terminal").

| Engine | Path | Picture | Clipboard |
| --- | --- | --- | --- |
| `guac` | browser, `/api/remote/ws`, relay, `guacd` | tiles through the Guacamole protocol | text both ways (Guacamole streams); images and files through the session helper (see [CLIPBOARD.md](CLIPBOARD.md)) |
| `stream` | browser (WebCodecs), `/api/remote/screen`, `ScreenRelay`, `ScreenStream` (own RDP client, H.264 through libopenh264) | H.264, paced by the browser's acknowledgements | text, images and files both ways (`ClipboardBridge` on the RDP clipboard channel, see [CLIPBOARD.md](CLIPBOARD.md)) |

- **Choice at connect time:** the page asks for a ticket with the engine it wants: the browser override `localStorage` `pd.engine` (a JSON value, for testing) if set, else the connection's `engine`. The server grants `stream` only for desktops and only when the H.264 library is present (`ScreenStream.IsAvailable`); the ticket answer names the engine that was actually granted. The browser asks for `guac` when it cannot decode H.264 with WebCodecs (`StreamClient.supported()`, needs HTTPS or localhost).
- **Fallback:** if a stream session never draws a picture (socket error, closed without a message, an error other than login refused / taken over / too many sessions, no picture within 10 s of the size message, or the decoder failing three times), `app.js` reconnects once with Guacamole and keeps using it until the connection is left (`fallBackFromStream`). A stream that has shown a picture is never swapped out silently.
- **Pacing and acknowledgements:** at most 3 pictures are in flight (`PictureWindow`). The browser acknowledges with the sequence number of a picture, and an acknowledgement covers that picture and every one before it. So a picture the browser never draws (dropped because no decoder was open, lost when the decoder was reopened or failed) cannot leave the window full for good: the next acknowledgement, or the one the browser sends when it drops or loses pictures, clears it. An old acknowledgement after a reset does not count twice, and a number beyond what was sent opens nothing in advance. A key frame request is answered even when the screen stands still and is already sharp. The browser also reopens a decoder that holds pictures for more than a second without drawing them. The server watchdog (2 s without an acknowledgement while the window is full: reset and key frame, with a warning in the log) stays as the last safety net.
- **Lossless refinement of still parts (`Display/Refine`):** H.264 in the browser is 4:2:0, so one-pixel coloured strokes (syntax-coloured text, coloured terminal output) are never pixel sharp, whatever the quantiser (a 4:4:4 stream is not an option: libopenh264 encodes 4:2:0 only, and phone decoders do not take High 4:4:4). `TileRefiner` keeps a grid of 32 x 32 tiles with the time of their last change. A tile that has been quiet for 120 ms (and only while the browser keeps up: at most one picture in flight, paced by a bucket of 25 % of the bit rate) is sent again losslessly as a palette PNG (`TilePng`; tiles of the same row are joined up to 640 px; a rectangle with more than 256 colours - photo, video - is not sent, that belongs to the stream). Server to browser message 40 carries the rectangle and the PNG; `stream.js` draws it on a second canvas above the video. When a refined tile changes again, message 41 (list of rectangles) tells the browser to clear it right before the picture with the change goes out, so an old overlay never stays over new content. Both go through the same ordered socket as the pictures, and the browser applies them in order (`overlayChain`). No extra server CPU was measurable and the latency under motion is unchanged (numbers in [IDEAS.md](IDEAS.md), 2026-11-19, crops in [stream-refine-crops.jpg](stream-refine-crops.jpg)). `JULOS_STREAM_REFINE=off` on the server and `localStorage` `pd.refine` = `"off"` in the browser switch it off (comparison only). Old browser code ignores the new message numbers.
- **Wire protocol:** binary, documented in `ScreenProtocol.cs` and the header of `stream.js`. Message 16 (browser to server, 1 byte: 1 pause, 0 resume) is sent for a session that is in the background. The clipboard is message 6 (server to browser, UTF-8 text, the remote desktop copied) and message 9 (browser to server, UTF-8 text, up to 1 MiB; the remote desktop fetches it when it pastes). `stream.js` presents it to `app.js` through the same stream objects as the Guacamole client (`onclipboard`, `createClipboardStream`), so the clipboard dialog is shared.
- **Wire protocol:** binary, documented in `ScreenProtocol.cs` and the header of `stream.js`. The clipboard is message 6 (server to browser, UTF-8 text, the remote desktop copied) and message 9 (browser to server, UTF-8 text, up to 1 MiB; the remote desktop fetches it when it pastes). `stream.js` presents it to `app.js` through the same stream objects as the Guacamole client (`onclipboard`, `createClipboardStream`), so the clipboard dialog is shared. Images and files have their own messages (7 to 10 to the browser, 10 to 15 from it: chunked, with sizes and names checked on both sides) and callbacks (`onclipimage`, `onclipfiles`, `sendTransfer`, `fetchFile`); the layout is in [CLIPBOARD.md](CLIPBOARD.md).
- **The agent is independent of the engine.** `/api/agent/*` (health, windows, apps, launch, mode), the window bar and uploads talk to the agent through its Unix socket on the server and never see the picture path. They work the same with `guac` and `stream` (checked on the test instance with `stream`: health, window list, launch, window bar).

## Several sessions at once

Contract, caps and UX are in [SESSIONS.md](SESSIONS.md). In short: the browser makes one object per open session (`RemoteSession` in `app.js`, a `Session` from `sessions.js`) and generates a session id (`sid`). The ticket carries it; the server slot is `user:connection:sid` and `SessionRegistry` (`JulOS.Integrations`) enforces the replace-only-yourself rule and the per-user cap (`JULOS_MAX_SESSIONS_PER_USER`, default 8; status 797 with a German message) for Guacamole and the stream engine alike.
Background sessions: the stream engine sends message 16 (pause) and the server stops encoding until it is lifted (the encoder keeps its reference, so the next picture is an ordinary delta with everything that changed); Guacamole sessions stay connected with their view hidden (no layout, paint or size reports).
## Native terminal (SSH)

A connection of kind **Terminal** is not a picture: the server holds the SSH connection and the browser draws real text with xterm.js, so selection, copy and
paste, scrollback and fonts are the browser's own. Guacamole is no longer involved in SSH (`GuacSpec.Terminal` is gone).

- **Backend:** `JulOS.Integrations/Ssh`. `ISshSession` (connect with password, host key callback, read, write, resize) with one implementation, `SshNetSession`
  (SSH.NET: password and keyboard-interactive login, pseudo-terminal `xterm-256color`, keep-alive every 30 s, the shell is read on its own thread through a bounded channel so a slow browser
  throttles the target through the SSH window). `SshRelay` joins a WebSocket and a session. SSH.NET is the one place that knows the library; replacing it means writing another `ISshSession`.
- **Endpoint:** `POST /api/remote/ticket` for a terminal connection answers `engine: "ssh"` and a one-time ticket (`SshTickets`, 30 s, per user, in memory; the request carries `cols`, `rows`, `sid`, and optionally `acceptHostKey`).
  `GET /api/remote/ssh?ticket=` upgrades to a WebSocket (same checks as the other two: session, own origin, ticket). The relay ends when the app session ends (checked every minute), like the other relays.
- **Wire protocol** (binary, first byte = kind; `SshProtocol.cs`, `terminal.js`): browser to server `1` data, `2` size (u16 cols, u16 rows), `3` ping, `4` pause, `5` resume;
  server to browser `1` data, `2` ready (JSON: `hostKey` = `new`, `known` or `changed-accepted`, `fingerprint`), `3` end (JSON: `code`, `message`, for a changed key also `fingerprint` and `previous`). Pause/resume is flow control: the browser asks the
  server to stop reading the shell when it has more than 256 kB it has not drawn yet (a WebSocket has no back pressure of its own). Input messages are limited to 64 kB (the page sends 16 kB pieces).
- **Limits and sessions:** the shared `SessionRegistry` (see [SESSIONS.md](SESSIONS.md)): one WebSocket per slot (user, connection, browser session id; a new one for the same slot replaces the old one, whose shell on the target ends with it), `JULOS_MAX_SESSIONS_PER_USER` per user, and 32 terminals at the same time in total. There is no extra idle timeout: like the other relays, an open session ends with the app session or the browser connection (a shell that is open but idle is the user's business).
  Error codes are the Guacamole ones where they exist (513 busy, 514 timeout, 515 failed, 520 unreachable, 769 login refused, 777 taken over, 797 too many sessions) plus 800 host key changed, 801 shell ended, 802 app session ended. All texts are German and never contain the password.
- **Host key (trust on first use):** the SHA-256 fingerprint of the host key, `"<algorithm> SHA256:<base64>"` as `ssh-keygen -l` prints it, is stored per connection (`connections.host_key`, migration 005).
  First connection: the key is accepted and stored *after* the login succeeded (a target whose password does not fit is not pinned) and the page says so. Later connections: the same key is silent. **A different key is never accepted silently**: the relay refuses before sending the password (end code 800 with both fingerprints), the page shows a warning dialog
  and only the button "Neuen Schlüssel vertrauen" repeats the ticket request with `acceptHostKey` set to exactly the shown fingerprint (a confirmation for any other key does not count); then the new key replaces the old one. Changing host, port or kind of a connection forgets the key (it belonged to the old target); the key is never sent to the browser except as the fingerprint in the warning.
  Key algorithm changes count as changes (the same server offering another algorithm first would show the dialog once). Not covered: a key that is *already* in the database from a man in the middle on the first connection (TOFU's known limit; compare the fingerprint shown with the one on the target).
- **Password:** typed per connection or stored in the same encrypted store as for RDP (nothing new is stored); it only lives in the ticket (30 s, memory) and in the SSH handshake. Key-based login is not built (see IDEAS).
- **Browser:** `terminal.js` (`TerminalView`: xterm.js, WebGL renderer when available, fit, unicode 11, font stack, theme from the app tokens, scrollback 10 000 lines, selection and copy/paste, mouse reporting, flow control, host key dialog) and `terminal-session.js` (`TerminalSession`, the session kind for `Sessions`; it extends `RemoteSession`, so key bar, menu and overlay are the ones of the remote view).
  Size follows the container: xterm fits locally at once, the new columns and rows go to the server (SSH `window-change`) 180 ms after the last change, so a keyboard animation does not flood the target; the terminal never changes the size of its container, so there is no feedback loop. Phone: the page's own input field (`#kb`, the proven Gboard handling) types, the key bar sends X11 keysyms that `keysymBytes` turns into bytes (arrows follow the application cursor mode, modifiers use the xterm parameter form); drag scrolls (as scrollback, as arrow keys in vim/less, as mouse wheel for programs with mouse support), long press selects a word and drag extends it, a small menu copies and pastes, two fingers change the font size. Desktop: xterm takes the keys directly, Ctrl+Shift+C / Ctrl+Insert copy, Ctrl+Shift+V / Shift+Insert paste, Ctrl+C copies when there is a selection and is ^C otherwise, right click opens the copy/paste menu.

## Order of work (one step at a time, each tested before the next)

0. Test instance (`deploy/dev`) with throwaway SSH and RDP targets.
1. Solution skeleton in the layout above: host, static files, headers, health, migration runner, PostgreSQL in compose.
2. ~~Accounts: setup screen with setup code, Argon2id, TOTP, sessions, lockout, console tool, tests.~~ Done.
3. ~~Relay: ticket endpoint and WebSocket to `guacd`, tested against a real `guacd`.~~ Done (`POST /api/remote/ticket`, `GET /api/remote/ws`).
4. ~~Agent client, upload and log endpoints.~~ Done (`/api/agent/*`, `/api/upload`, `/api/log`; tested against the real agent on the test instance).
5. ~~Client: setup and login screens, home with connections (Desktop and Terminal), sign out, device list.~~ Done (`src/JulOS.Frontend/wwwroot`; checked in a real browser against the test instance, not yet on the phone).
6. ~~Switch the compose file over, remove the Node gateway.~~ Done (`docker-compose.yml`: PostgreSQL, guacd, server running as the desktop user; the Node gateway is gone, see git history before this change).
