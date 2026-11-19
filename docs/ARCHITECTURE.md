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
| Browser | Plain ES modules and the Apache Guacamole JS client, served as static files (the PWA) |
| Database | PostgreSQL, plain SQL through Npgsql, SQL migrations in `database/`, no ORM |
| Remote display | `guacd` (container, no published port); the server relays the Guacamole protocol itself. Optional second engine per connection: own RDP client with H.264 (see "Display engines") |
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

The guacd protocol relay and its handshake, one-time connection tickets, sessions with rotating cookies, TOTP (RFC 6238) and recovery codes,
login lockout, the migration runner, the SQL access helpers, security headers and the CSRF/origin checks, the agent client, the upload path.

## Not written ourselves, and why

| What | Use | Why |
| --- | --- | --- |
| HTTP/TLS server | Kestrel (part of the framework) | Mature and fast |
| PostgreSQL wire protocol | Npgsql | A driver is not worth hand-writing |
| Cryptographic primitives (HMAC, AES, random) | .NET base library | No hand-rolled crypto |
| Password hash | Argon2id from one small library (Konscious), hash format versioned | Memory-hard hash is the one place where a custom version would be risky |
| RDP engine | `guacd` | Years of protocol work |

Target for runtime NuGet packages: **two** (Npgsql and the Argon2 library). Test packages are separate. Versions are centralised in `Directory.Packages.props`.

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
connection dialog ("Bildübertragung"); terminals always use Guacamole.

| Engine | Path | Picture | Clipboard |
| --- | --- | --- | --- |
| `guac` | browser, `/api/remote/ws`, relay, `guacd` | tiles through the Guacamole protocol | text both ways (Guacamole streams) |
| `stream` | browser (WebCodecs), `/api/remote/screen`, `ScreenRelay`, `ScreenStream` (own RDP client, H.264 through libopenh264) | H.264, paced by the browser's acknowledgements | text both ways (`ClipboardBridge` on the RDP clipboard channel; images and files do not travel), sound from the desktop, microphone to the desktop (see Sound below) |

- **Choice at connect time:** the page asks for a ticket with the engine it wants: the browser override `localStorage` `pd.engine` (a JSON value, for testing) if set, else the connection's `engine`. The server grants `stream` only for desktops and only when the H.264 library is present (`ScreenStream.IsAvailable`); the ticket answer names the engine that was actually granted. The browser asks for `guac` when it cannot decode H.264 with WebCodecs (`StreamClient.supported()`, needs HTTPS or localhost).
- **Fallback:** if a stream session never draws a picture (socket error, closed without a message, an error other than login refused / taken over / too many sessions, no picture within 10 s of the size message, or the decoder failing three times), `app.js` reconnects once with Guacamole and keeps using it until the connection is left (`fallBackFromStream`). A stream that has shown a picture is never swapped out silently.
- **Wire protocol:** binary, documented in `ScreenProtocol.cs` and the header of `stream.js`. The clipboard is message 6 (server to browser, UTF-8 text, the remote desktop copied) and message 9 (browser to server, UTF-8 text, up to 1 MiB; the remote desktop fetches it when it pastes). `stream.js` presents it to `app.js` through the same stream objects as the Guacamole client (`onclipboard`, `createClipboardStream`), so the clipboard dialog is shared.
- **Sound (stream engine only).** Output: `SoundChannel` (RDPSND) takes 16-bit PCM and nothing else (no decoder on the server; every server has PCM, the list is sorted 48000 Hz stereo, 44100 Hz stereo, then the rest) and raises the blocks; `AudioBridge` queues them for the WebSocket sender and `ScreenRelay` sends them as message 32 (channels, rate, server time, PCM). The queue is **bounded** (`AudioBridge.MaxQueuedMs`, 200 ms; the oldest block is dropped), so a stalled or hidden tab never makes the server buffer audio; every block is confirmed to the remote desktop whatever happens to it. Sound is **off per connection until the browser sends message 32 "on"**, which it does only while its `AudioContext` is running (so a suspended tab asks for nothing). In the browser `stream-audio.js` hands the blocks to an AudioWorklet (`stream-audio-worklet.js`: ring buffer, linear resampling to the context rate, jitter buffer with a 60 ms target that grows to 200 ms after an underrun and shrinks again, up to 3 % faster playback to work off a backlog, drop above target + 150 ms; no per-sample code on the main thread). Menu entries "Ton vom Rechner" (on/off, remembered), "Lautstärke" (steps) and "Mikrofon". Windows only talks RDPSND to a client that also announces `rdpdr`, so `ScreenStream` registers an `RdpdrChannel` without devices. Input: `AudioInputChannel` (MS-RDPEAI, dynamic channel `AUDIO_INPUT`) answers version and formats (16-bit PCM only, mono before stereo, rates up to 48 kHz first), confirms Open and Format Change, and sends the microphone in packets of the size the server asked for, each announced by an Incoming Data message. The browser captures mono at the rate the server announced in message 33 (`getUserMedia` with echo cancellation, noise suppression, auto gain; an AudioWorklet averages down to that rate and returns 20 ms chunks), sends them as message 33, and `AudioBridge` converts to the server's format (`PcmResampler`, linear). The microphone is off until the user picks it in the menu (browser permission prompt), a red "Mikrofon an" pill shows while it is live. The server asks for sound only when a program on the desktop records: Windows opens the channel at logon, **xrdp opens it only when a program starts recording**, so the menu cannot know beforehand whether a desktop offers a microphone; it says "wartet auf Programm" until recording starts. Messages 32 and 33 live in their own number range (from 32) next to the picture messages.
- **The agent is independent of the engine.** `/api/agent/*` (health, windows, apps, launch, mode), the window bar and uploads talk to the agent through its Unix socket on the server and never see the picture path. They work the same with `guac` and `stream` (checked on the test instance with `stream`: health, window list, launch, window bar).

## Order of work (one step at a time, each tested before the next)

0. Test instance (`deploy/dev`) with throwaway SSH and RDP targets.
1. Solution skeleton in the layout above: host, static files, headers, health, migration runner, PostgreSQL in compose.
2. ~~Accounts: setup screen with setup code, Argon2id, TOTP, sessions, lockout, console tool, tests.~~ Done.
3. ~~Relay: ticket endpoint and WebSocket to `guacd`, tested against a real `guacd`.~~ Done (`POST /api/remote/ticket`, `GET /api/remote/ws`).
4. ~~Agent client, upload and log endpoints.~~ Done (`/api/agent/*`, `/api/upload`, `/api/log`; tested against the real agent on the test instance).
5. ~~Client: setup and login screens, home with connections (Desktop and Terminal), sign out, device list.~~ Done (`src/JulOS.Frontend/wwwroot`; checked in a real browser against the test instance, not yet on the phone).
6. ~~Switch the compose file over, remove the Node gateway.~~ Done (`docker-compose.yml`: PostgreSQL, guacd, server running as the desktop user; the Node gateway is gone, see git history before this change).
