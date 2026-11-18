# JulOS

**Your machines, your programs, your files: one personal workspace, in your pocket or in any browser.**
Self-hosted and open source. What we build right now: [docs/SCOPE.md](docs/SCOPE.md). The bigger picture: [docs/VISION.md](docs/VISION.md). Parked ideas: [docs/IDEAS.md](docs/IDEAS.md).

The first piece is a remote desktop built for **phones**: your own Android keyboard (suggestions, autocorrect, images), a
customisable extra-key bar, an in-app window bar instead of the desktop taskbar, dynamic resolution, Material You colours
and an installable PWA. Under the hood it is plain remote access to your own machine (RDP through Apache Guacamole),
nothing is stored in the cloud.

[Deutsche Version](README.de.md)

```
phone (PWA)  --HTTPS-->  your reverse proxy  -->  JulOS server (.NET, own login)  -->  guacd  --RDP/SSH-->  your machine
                                                        |--> PostgreSQL (accounts, sessions, saved connections)
                                                        \--> agent (unix socket) in the desktop session: windows, apps, photo paste
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
- **Password managers**: the login form and a dedicated password dialog are autofill friendly (Bitwarden etc.).
- **Touch**: tap = click, long press = right click, drag, two-finger scroll, pinch zoom (resolution adapts), touchpad mode.
- **PWA**: installable (needs HTTPS), standalone/fullscreen, offline shell. Material You palette from a seed colour or from an image.
- **Reconnect** with animation, keyboard auto-open on text fields, visual viewport aware layout.

## Quick start

Requirements on the target machine: Linux with X11, **xrdp** (and xorgxrdp), `python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0`.
On the Docker host (can be the same machine): Docker with Compose.

```bash
git clone <your fork or this repo> julos && cd julos
cp .env.example .env            # set JULOS_DB_PASSWORD (openssl rand -hex 16); set JULOS_UID/JULOS_GID if your desktop user is not 1000
docker compose up -d --build    # or: docker compose -f docker-compose.image.yml up -d  (ghcr.io image)
docker compose logs julos       # shows the one-time setup code
tools/install-linux-agent.sh    # in the graphical session of the target machine
```

Open `http://<BIND_IP>:8080` (on `localhost` or over HTTPS; the session cookie needs a secure context), enter the setup code, choose a user name and password and
confirm the authenticator code. Then add a connection (Desktop = RDP, Terminal = SSH) and tap it. The target's password is typed per connection, or stored encrypted on request (needs a key: either `JULOS_SECRET_KEY` in `.env`, `openssl rand -base64 32`, or a volume at `/data` where the server creates `secret.key` itself, as the image's Compose stacks do; back it up like a password).
A desktop connection can also use the experimental stream engine (own RDP client, H.264 in the browser; choose "Stream" under "Bildübertragung" in the connection dialog). Guacamole stays the default, and the app falls back to it when the browser or server cannot do H.264. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
To reach the machine the server runs on, use the host name `host.docker.internal`.

`BIND_IP` in `.env` is the address the server listens on: `127.0.0.1` (default, behind a reverse proxy) or the LAN address.
The agent files live in `~/julos-uploads` of the target user and the server runs as that user (`JULOS_UID`/`JULOS_GID`),
so running on the same machine is the supported setup for now. Lost your password or authenticator? `tools/julos-user.sh` resets them on the console.

## Installing the PWA, HTTPS

Browsers only install PWAs and allow the clipboard API on **HTTPS**. Put a reverse proxy in front (Caddy examples: [caddy/Caddyfile.lan-first.example](caddy/Caddyfile.lan-first.example) for a first HTTPS test inside your network,
[caddy/Caddyfile.example](caddy/Caddyfile.example) with client certificate and proxy secret for internet access). Over plain HTTP in the LAN the page works but passwords travel unencrypted.

## Security

Remote control must not be protected by the desktop password alone.

- **Own login:** password (Argon2id) plus authenticator code, ten recovery codes, long-lived rotating sessions kept as hashes in PostgreSQL, lockout with growing delay. Every API and the WebSocket need a session; see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **First start:** the first account is created in the browser with a one-time setup code from the server log. Afterwards the setup is closed.
- **Connections** use one-time tickets (30 s). The target password is typed per connection (held in memory for at most 30 s, never logged) or, on request, stored AES-256-GCM encrypted with a key that lives only in the server settings; it is never sent back to the browser.
- `guacd` and PostgreSQL have no published port. Open connections end when their session ends.
- **mTLS** in the proxy (optional extra layer): only your phone with a client certificate reaches the login page. Create one with
  `tools/make-client-cert.sh <name>` (you type the passphrase yourself); the Caddy example has the `client_auth` block.
- The agent API is only reachable through the server with a live session, over a Unix socket (mode 0600).
- Never expose xrdp (3389) or the server port directly to the internet without the proxy in front.

Roll-out order: proxy config, port forward 443 to the proxy, DNS; optionally a client certificate on top.

## Photos

`POST /api/upload` saves the image in `~/julos-uploads`; `tools/julos-clip.py` (autostarted in the session) puts it on the GTK
clipboard and acknowledges it, then the page sends Ctrl+V. Files are deleted after 30 minutes.
Guacamole cannot transfer image clipboards over RDP, hence this path.

## Layout

- `src/JulOS.Frontend` host: web client (`wwwroot`: PWA for the phone, desktop interface with windows for wide screens with a mouse), HTTP API, WebSocket; `src/JulOS.Backend` accounts, sessions, connections (PostgreSQL); `src/JulOS.Integrations` guacd relay and agent client; `src/JulOS.Workers` background jobs
- `tests/` (`frontend/` runs with node), `database/` (SQL migrations), `deploy/dev/` the test instance with throwaway SSH and RDP targets (see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md); testing on a real phone: [docs/PHONE-TEST.md](docs/PHONE-TEST.md))
- `tools/julos-agent.py` Linux agent (windows, apps, app mode), `tools/julos-clip.py` photo clipboard helper
- `tools/install-linux-agent.sh`, `tools/julos-user.sh`, `tools/make-client-cert.sh`
- `docs/AGENT_API.md` the agent API, `docs/ARCHITECTURE.md` the stack and security design, `caddy/Caddyfile.example`
- `.github/workflows/docker.yml` builds the image for `ghcr.io/<owner>/<repo>` (amd64 + arm64)

## Roadmap

Now: the Remote module (see [docs/SCOPE.md](docs/SCOPE.md)). Next: a module core, then Docker, Caddy and DDNS modules, files, Windows agent, multi-host. The goal is one interface for everything you run.

## License

MIT, see [LICENSE](LICENSE). Built on Apache Guacamole (guacd, guacamole-common-js).
