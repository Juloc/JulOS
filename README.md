# JulOS

**Your machines, your programs, your files: one personal workspace, in your pocket or in any browser.**
Self-hosted and open source. What we build right now: [docs/SCOPE.md](docs/SCOPE.md). The bigger picture: [docs/VISION.md](docs/VISION.md). Parked ideas: [docs/IDEAS.md](docs/IDEAS.md).

The first piece is a remote desktop built for **phones**: your own Android keyboard (suggestions, autocorrect, images), a
customisable extra-key bar, an in-app window bar instead of the desktop taskbar, dynamic resolution, Material You colours
and an installable PWA. Under the hood it is plain remote access to your own machine (RDP or VNC, streamed as H.264 by JulOS's own display engine),
nothing is stored in the cloud.

[Deutsche Version](README.de.md)

```
phone (PWA)  --HTTPS-->  your reverse proxy  -->  JulOS server (.NET, own login, RDP client + H.264 encoder)  --RDP-->  your machine
                                                        |--> SSH client (terminal, text drawn in the browser)  --SSH-->  your machine
                                                        |--> PostgreSQL (accounts, sessions, saved connections)
                                                        \<-- agent in the desktop session (connects outbound, or unix socket on the same machine): windows, apps, photo paste
```

Status: Linux (X11, XFCE tested) first. Windows is planned and the agent API is built for it (see [docs/AGENT_API.md](docs/AGENT_API.md)).

## Features

- **Android keyboard as it is**: every change (autocorrect, a suggestion replacing a word) is sent as backspaces plus new text.
  Modes: with suggestions, without suggestions (password-style), rich (accepts images, stickers, GIFs from the keyboard).
- **Extra keys**: two collapsible rows (quick: Esc, Tab, Ctrl, Alt, Shift, arrows, Enter, Backspace; more: Ctrl+C/V/X/A/Z/F,
  Alt+Tab, Home, End, Page up/down, ...). Ctrl/Alt/Shift are sticky toggles. Edit mode: add, remove, move keys between rows,
  own combos (`ctrl+shift+t`), text snippets.
- **Desktop browser, real keyboard**: keys go straight to the remote machine. Shift/Ctrl/Alt are sent together with arrows, Home, End etc.
  (Shift+arrows select, Ctrl+arrows jump by word; F1 to F12 go to the remote machine, so F5 does not reload the page there); Ctrl+V first puts your local clipboard on the remote clipboard, Ctrl+C / Ctrl+X in
  the remote copy its text to your local clipboard (needs HTTPS or localhost).
- **Window bar and app mode**: switch between *Desktop* and *Apps*. In Apps mode the session's panel is hidden, windows appear as
  chips (with icons) in the app, a "+" starts any installed program. Windows are maximised. Per-application zoom profiles
  (for example a narrow width for apps with a responsive layout).
- **Photo paste**: pick a photo, it is downscaled, uploaded, put on the remote clipboard and pasted with Ctrl+V.
- **Images and files in the clipboard**: paste an image (Ctrl+V), drop files on the picture or use "Datei senden"; they arrive on the remote clipboard (paste there with Ctrl+V). With the own stream engine an image or file that the remote copies shows up in the clipboard dialog (copy or save it). Limits and per-engine details: [docs/CLIPBOARD.md](docs/CLIPBOARD.md).
- **Password managers**: the login form and a dedicated password dialog are autofill friendly (Bitwarden etc.).
- **Touch**: tap = click, long press = right click, drag, two-finger scroll, pinch zoom (resolution adapts), touchpad mode.
- **PWA**: installable (needs HTTPS), standalone/fullscreen, offline shell. Material You palette from a seed colour or from an image.
- **German and English:** the whole browser interface comes in both languages (choice "Deutsch / English / System" in the settings, saved per user on the server; German is the default). See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#languages).
- **Several sessions at once** (up to 8 per user, RDP and SSH, also two to the same target): on the phone a strip with one chip per session (state dot) and a sheet with cards to switch or close; on a wide screen each session is its own window and the keyboard goes only to the focused one. Sessions in the background keep running but pause the expensive work. See [docs/SESSIONS.md](docs/SESSIONS.md).
- **Status bar, frame rate and pop-out** (desktop and phone): an optional status bar per stream session (frames per second, reaction time, data rate, resolution, encoder), a per-session frame rate (automatic, 60, 30, economy), and on a wide screen a session can move into its own browser window without a second login. See [docs/STATUSBAR.md](docs/STATUSBAR.md) and [docs/SESSIONS.md](docs/SESSIONS.md).
- **Push messages without AI** (Settings, *Push-Meldungen*): your phone or browser gets a Web Push message (VAPID, end-to-end encrypted to the device) when a host reports new security updates, a disk filling up, a failed service or container, an agent that stays offline or a restart that is needed. Each rule can be switched off, quiet hours collect messages into one, the last 50 are listed in the app and a tap opens the host. Fixed rules, no AI; the push service only sees encrypted data. See [docs/PUSH.md](docs/PUSH.md).
- **Files on every host** (desktop window and phone page "Dateien"): the shared folder of the server and every host with an SSH connection, over SFTP with the credentials you saved for the connection (no agent), and for a host whose agent allows it the extra place "Host (Root)" that works **as root** through the agent (own word `files-root`, level "Alles", every action and even every read confirmed, red warnings). It works like an explorer: click selects and double click opens, `Shift`/`Ctrl`/rubber band selection, type to jump, right-click menu, drag and drop, cut/copy/paste (also across hosts), a tree of hosts with pinned folders, a compact list with extensions, dimmed hidden files and sortable columns, and a simple text editor (on the desktop the editor and every dialog of the app are movable windows that do not block the rest; closing one never confirms). Download, upload (pick or drop), new folder or text file, rename, copy, move (also from host to host through the server) and delete, each change confirmed once per action in the app and recorded in a history. Reading works at once; **writing is off until you switch it on in Settings, Security ("Write files"; the environment variable `JULOS_FILES_WRITE=on` is only the fallback)**. The host key must be confirmed, links are never followed, nothing is held in memory, downloads are never shown as a page. See [docs/FILES.md](docs/FILES.md).
- **Reconnect** with animation, keyboard auto-open on text fields, visual viewport aware layout.

## Quick start

Requirements on the target machine: Linux with X11, **xrdp** (and xorgxrdp), `python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0`.
On the Docker host (can be the same machine): Docker with Compose.

```bash
git clone <your fork or this repo> julos && cd julos
cp .env.example .env            # set JULOS_DB_PASSWORD (openssl rand -hex 16); set JULOS_UID/JULOS_GID if your desktop user is not 1000
docker compose up -d --build    # or: docker compose -f docker-compose.image.yml up -d  (ghcr.io image)
docker compose logs julos       # shows the one-time setup code
tools/install-linux-agent.sh    # same machine as the server (forwards to install-agent.sh --desktop)
```

Open `http://<BIND_IP>:8080` (on `localhost` or over HTTPS; the session cookie needs a secure context), enter the setup code, choose a user name and password and
confirm the authenticator code. Then add a connection (Desktop = RDP, Desktop (VNC) = VNC, Terminal = SSH) and tap it. A small dialog (ping, DNS lookup, port test) checks from the server whether the targets of your saved connections answer. The authenticator secret is also shown as a QR code, and password and authenticator can be changed later in the app (phone: Profil, desktop: Einstellungen). A host can keep **one login** (user and password, on the host page, section Login) that terminal, Files and desktop connections of that host all use; a connection can ask for its own. Otherwise the target's password is typed per connection, or stored encrypted on request (needs a key: either `JULOS_SECRET_KEY` in `.env`, `openssl rand -base64 32`, or a volume at `/data` where the server creates `secret.key` itself, as the image's Compose stacks do). The server backs up the database and this key every day into `~/julos-backup` (see [docs/BACKUP.md](docs/BACKUP.md), including how to restore); copy that folder off the machine, or let JulOS copy it to a NAS or an SFTP server itself).
A desktop connection is streamed by JulOS's own display engine (own RDP client on the server, H.264 decoded in the browser with WebCodecs; the browser needs H.264 and a secure context, otherwise the app says so and offers "Neu verbinden"). It plays the desktop's sound and can send the browser's microphone to the desktop (off until you switch it on in the menu). See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
A **VNC** connection (RFB 3.3 to 3.8; no sign-in, VNC password or VeNCrypt with a certificate; Raw, CopyRect, RRE, Hextile, Zlib and ZRLE; text clipboard; the desktop follows the window where the server can) goes through the same engine, picture encoding and sessions; only the first 8 characters of a VNC password count and plain VNC is not encrypted, so use it inside a trusted network or with VeNCrypt. Scope and limits: [docs/VNC.md](docs/VNC.md).
With a GPU in the container the stream engine encodes on it (VAAPI, optionally H.265), see [docs/ENCODERS.md](docs/ENCODERS.md).
Optionally (`JULOS_RDP_GFX=on`, off by default) the stream engine passes on the H.264 that an RDP server sends through its graphics pipeline instead of encoding again, and falls back to its own encoder whenever the server cannot do that; the common xrdp of Debian cannot, see [docs/RDPGFX.md](docs/RDPGFX.md).
If only the browser's connection drops (phone switches network, tab in the background), the stream engine keeps the RDP session on the server for `JULOS_STREAM_KEEP_SECONDS` (default 300, 0 = off) and the browser re-attaches to it without a new login ([docs/SESSIONS.md](docs/SESSIONS.md)). It can also hand one server folder to the desktop as a drive, read only unless a second switch allows writing; off by default in the server settings (`JULOS_DRIVE_ROOT`) and per connection ([docs/DRIVES.md](docs/DRIVES.md)).
Still screen parts of the stream engine are refined losslessly (text and colours pixel exact), its bit rate follows the link on slow connections, and the RDP certificate is remembered on first use (a change raises a warning, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
A Terminal connection is a real terminal in the browser (the server holds the SSH connection, xterm.js draws the text: selection, copy and paste, scrollback). The first connection remembers the target's SSH host key; if it ever changes, the page warns and refuses until you confirm the new key. Login is by password for now.
A Terminal connection can start a program instead of the shell: **Claude Code** or **Codex** (the `claude` and `codex` command line tools on your machine, which sign in there themselves; JulOS stores no credentials for them) or a custom command, in a working directory, optionally inside tmux so the work survives a dropped connection. One tap on a "Claude" or "Codex" tile starts it; at the bottom of the terminal an input bar (multi-line text field, Enter sends) and a row of quick keys (Esc, Tab, Shift+Tab, Ctrl+C, Ctrl+D, arrows, Page up/down) make it usable on a phone. The server checks that the program exists on the target (`command -v`, 5 s) and otherwise starts a normal shell with a visible notice. See [docs/CLI-SESSIONS.md](docs/CLI-SESSIONS.md).
To reach the machine the server runs on, use the host name `host.docker.internal`.

`BIND_IP` in `.env` is the address the server listens on: `127.0.0.1` (default, behind a reverse proxy) or the LAN address.
**Several machines (hosts):** a host is a machine or server with its own connections and its own agent; the desktop interface and the phone shell list them as cards with state, key figures (load, memory, disk) and notes (updates, reboot, failed services), simple actions in the card menu (each one confirmed), and **Details** opens the page with the system details and sections for load, disks, network, updates, services, Docker, logs, actions and history (see [docs/HOSTS.md](docs/HOSTS.md)).
**Updates and actions on hosts run in the background:** after you confirm an action (install updates, repair packages, restart, pull an image) a small entry with a progress bar appears in the menu bar (phone: under the header); a click opens the live output, an error summary in plain words and the full log. Proxmox hosts are updated as a whole (`dist-upgrade`), half configured packages get a **Reparieren** button, and the word `disks` lets the agent read the disks' SMART health through the root helper. See [docs/HOST-AGENT.md](docs/HOST-AGENT.md#background-tasks).
**Agent operation and updates:** the host page (section Agent) shows `Agent 0.x, Update verfügbar` when the server ships a newer agent; **Jetzt aktualisieren** makes the connected agent fetch the new file from your server itself (SHA-256 from the server, same origin only, atomic replace, restart), and a switch per agent (default off) does it automatically on connect. On the target: `julos-agent.py status | stop | uninstall [--yes]`; the log is `~/.local/state/julos/agent.log` (rotated); `install-linux-agent.sh --systemd` runs it as a user service. Agents older than 0.4.0 need one new enrolment before they can update. Details in [docs/AGENT_API.md](docs/AGENT_API.md).
**One agent per machine, one installer:** the agent (`tools/julos-agent.py`, 0.8.0) does everything on a machine: with a desktop it runs in your session (window bar, programs, clipboard) **and** answers the host requests (system values, updates, disks, services, Docker, confirmed actions, and with the word `files-root` the Files app as root); on a server without a desktop it runs as its own system user. `curl -fsSL <server>/agent-files/install-agent.sh -o julos-install-agent.sh && sudo sh julos-install-agent.sh --server <server> --code CODE [--allow updates,logs] [--dry-run]` detects which one this machine is ("Desktop detected"), installs checked files from your server only, leaves the permission file empty unless you name words with `--allow`, and updates an existing installation without a new code. The old `install-host-agent.sh` and `install-linux-agent.sh` still work (they forward). See [docs/HOST-AGENT.md](docs/HOST-AGENT.md).
**Agent on another machine than the server:** open the host page (Hosts view, **Details**, section **Agent**; or **Agent einrichten** on the card): one flow for every machine, you tick what the agent may do and copy the command (or install over SSH). The command downloads the installer from your server (a file you can read first) and enrols the agent with a one-time code. The agent connects outbound to the server over HTTPS, so nothing on the target listens; **Agent entfernen** revokes it. See [docs/AGENT_API.md](docs/AGENT_API.md).
On the same machine, `tools/install-linux-agent.sh` without arguments keeps the old way: the agent files live in `~/julos-uploads` of the target user and the server runs as that user (`JULOS_UID`/`JULOS_GID`). Lost your password or authenticator? `tools/julos-user.sh` resets them on the console.

## Installing the PWA, HTTPS

Browsers only install PWAs and allow the clipboard API on **HTTPS**. Put a reverse proxy in front (Caddy examples: [caddy/Caddyfile.lan-first.example](caddy/Caddyfile.lan-first.example) for a first HTTPS test inside your network,
[caddy/Caddyfile.example](caddy/Caddyfile.example) with client certificate and proxy secret for internet access). Over plain HTTP in the LAN the page works but passwords travel unencrypted.

## Security

Remote control must not be protected by the desktop password alone.

- **Own login:** password (Argon2id) plus authenticator code, ten recovery codes, long-lived rotating sessions kept as hashes in PostgreSQL, lockout with growing delay. Every API and the WebSocket need a session; see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **First start:** the first account is created in the browser with a one-time setup code from the server log. Afterwards the setup is closed.
- **Connections** use one-time tickets (30 s). The target password is typed per connection (held in memory for at most 30 s, never logged) or, on request, stored AES-256-GCM encrypted with a key that lives only in the server settings, either per connection or once per host (the host login, bound to the host's addresses); it is never sent back to the browser. Root has no stored password: root actions go through the agent.
- PostgreSQL has no published port. Open connections end when their session ends.
- **mTLS** in the proxy (optional extra layer): only your phone with a client certificate reaches the login page. Create one with
  `tools/make-client-cert.sh <name>` (you type the passphrase yourself); the Caddy example has the `client_auth` block.
- The agent API is only reachable through the server with a live session. Agents either answer on a Unix socket (mode 0600, same machine) or connect outbound with a per-host token (stored only as a hash on the server, revocable, one-time enrolment code).
- Never expose xrdp (3389) or the server port directly to the internet without the proxy in front.
- **Internet checklist** (details and effort in [docs/SECURITY-REVIEW.md](docs/SECURITY-REVIEW.md)): enter the proxy's address under Settings, Security (trusted proxy addresses; the page warns while it is empty) and let only the proxy reach port 8080, list the allowed target hosts there too, create the recovery key (the backup no longer holds the key; keep the recovery key in your password manager), leave the second factor for actions on, use [caddy/Caddyfile.internet.example](caddy/Caddyfile.internet.example) if hosts with agents connect from outside, and rebuild/pull the images at least monthly (base image patches; see [docs/RELEASING.md](docs/RELEASING.md)). Dependencies are checked weekly (Dependabot, `dotnet list package --vulnerable`).

Roll-out order: proxy config, port forward 443 to the proxy, DNS; optionally a client certificate on top.

## Photos

`POST /api/upload` saves the image in `~/julos-uploads`; `tools/julos-clip.py` (autostarted in the session) puts it on the GTK
clipboard and acknowledges it, then the page sends Ctrl+V. Files are deleted after 30 minutes.
The app itself no longer needs this path: the stream engine sends images and files over RDP itself, see [docs/CLIPBOARD.md](docs/CLIPBOARD.md). The upload API and the helper (which also takes files in `files/<id>/` and answers `pull/`) stay for the agent API.

## Layout

- `src/JulOS.Frontend` host: web client (`wwwroot`: PWA for the phone in Material 3 style, desktop interface with windows for wide screens with a mouse), HTTP API, WebSocket; `src/JulOS.Backend` accounts, sessions, connections (PostgreSQL); `src/JulOS.Integrations` display engine (RDP and VNC clients, H.264), SSH client and agent client; `src/JulOS.Workers` background jobs
- `tests/` (`frontend/` runs with node), `database/` (SQL migrations), `deploy/dev/` the test instance with throwaway SSH and RDP targets (see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md); testing on a real phone: [docs/PHONE-TEST.md](docs/PHONE-TEST.md))
- `tools/julos-agent.py` Linux agent (windows, apps, app mode, outbound link to the server), `tools/julos-clip.py` photo clipboard helper
- `tools/install-agent.sh` (and the thin `tools/install-host-agent.sh`, `tools/install-linux-agent.sh`), `tools/julos-user.sh`, `tools/make-client-cert.sh`
- `CHANGELOG.md` and `docs/RELEASING.md` the versions and how to release one, `docs/AGENT_API.md` the agent API, `docs/HOSTS.md` hosts, `docs/FILES.md` the Files app (shared folder and hosts over SFTP; the explorer rework is specified in `docs/FILES-EXPLORER.md`), `docs/PUSH.md` push messages without AI, `docs/HOST-AGENT.md` the host agent for machines without a desktop (status, updates, disk health, install over SSH, confirmed actions), `docs/ARCHITECTURE.md` the stack and security design, `docs/MODULES.md` the optional modules Services (widgets), Caddy, DDNS and the Home network card (off by default), `caddy/Caddyfile.example`
- `.github/workflows/docker.yml` builds the image for `ghcr.io/<owner>/<repo>` (amd64 + arm64; the SDK stage runs natively, no emulation), `.github/workflows/tests.yml` runs the server tests (with PostgreSQL), the browser-logic tests and a NuGet vulnerability report

## Roadmap

Now: the Remote module (see [docs/SCOPE.md](docs/SCOPE.md)). Next: a module core, then the Docker module and write access for Caddy and DDNS (their read-only first stage exists, off by default), files, Windows agent, multi-host. The goal is one interface for everything you run.

## License

MIT, see [LICENSE](LICENSE). Third-party components and their licences: `src/JulOS.Frontend/wwwroot/licenses.txt` and `src/JulOS.Integrations/Display/NOTICE.md`.
