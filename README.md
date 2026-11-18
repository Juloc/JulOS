# JulOS

**Your machines, your programs, your files: one personal workspace, in your pocket or in any browser.**
Self-hosted and open source. What we build right now: [docs/SCOPE.md](docs/SCOPE.md). The bigger picture: [docs/VISION.md](docs/VISION.md). Parked ideas: [docs/IDEAS.md](docs/IDEAS.md).

The first piece is a remote desktop built for **phones**: your own Android keyboard (suggestions, autocorrect, images), a
customisable extra-key bar, an in-app window bar instead of the desktop taskbar, dynamic resolution, Material You colours
and an installable PWA. Under the hood it is plain remote access to your own machine (RDP through Apache Guacamole),
nothing is stored in the cloud.

[Deutsche Version](README.de.md)

```
phone (PWA)  --HTTPS-->  your reverse proxy  -->  gateway (Node + guacamole-lite)  -->  guacd  --RDP-->  xrdp (your Linux desktop)
                                                          \--> agent (unix socket) in the desktop session: windows, apps, photo paste
```

Status: Linux (X11, XFCE tested) first. Windows is planned and the agent API is built for it (see [docs/AGENT_API.md](docs/AGENT_API.md)).

## Features

- **Android keyboard as it is**: every change (autocorrect, a suggestion replacing a word) is sent as backspaces plus new text.
  Modes: with suggestions, without suggestions (password-style), rich (accepts images, stickers, GIFs from the keyboard).
- **Extra keys**: two collapsible rows (quick: Esc, Tab, Ctrl, Alt, Shift, arrows, Enter, Backspace; more: Ctrl+C/V/X/A/Z/F,
  Alt+Tab, Home, End, Page up/down, ...). Ctrl/Alt/Shift are sticky toggles. Edit mode: add, remove, move keys between rows,
  own combos (`ctrl+shift+t`), text snippets.
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
cp .env.example .env            # set TOKEN_KEY (32 chars: openssl rand -hex 16) and RDP_USER
docker compose up -d --build    # or: docker compose -f docker-compose.image.yml up -d  (ghcr.io image)
tools/install-linux-agent.sh    # in the graphical session of the target machine
```

Open `http://<BIND_IP>:8080`, log in with the Linux user's password (typed by you, never stored).

`BIND_IP` in `.env` is the address the gateway listens on: `127.0.0.1` (default, behind a reverse proxy) or the LAN address.
If the Docker host is not the target machine, set `RDP_HOST`. The agent files live in `~/julos-uploads` of the target user,
so running on the same machine is the supported setup for now.

## Installing the PWA, HTTPS

Browsers only install PWAs and allow the clipboard API on **HTTPS**. Put a reverse proxy in front (Caddy examples: [caddy/Caddyfile.lan-first.example](caddy/Caddyfile.lan-first.example) for a first HTTPS test inside your network,
[caddy/Caddyfile.example](caddy/Caddyfile.example) with client certificate and proxy secret for internet access). Over plain HTTP in the LAN the page works but passwords travel unencrypted.

## Security

Remote control must not be protected by the desktop password alone.

- The target (host, port, user) is fixed on the server; the browser only supplies the password, passed on in an AES-encrypted token valid 30 s.
- `guacd` has no published port. Login attempts are rate limited.
- **Proxy mode**: with `PROXY_SECRET` the gateway (HTTP and WebSocket) only answers requests carrying the header `X-Proxy-Secret`
  that your reverse proxy adds. Enable with `tools/enable-proxy-mode.sh`.
- **mTLS** in the proxy: only your phone with a client certificate reaches the login page. Create one with
  `tools/make-client-cert.sh <name>` (you type the passphrase yourself); the Caddy example has the `client_auth` block.
- The agent API is only reachable through the gateway with a live session, over a Unix socket (mode 0600).
- Never expose xrdp (3389) or the gateway port directly to the internet.

Roll-out order: certificate, proxy config, `tools/enable-proxy-mode.sh`, port forward 443 to the proxy, DNS.
To undo proxy mode, empty `PROXY_SECRET` in `.env` and run `docker compose up -d`.

## Photos

`POST /api/upload` saves the image in `~/julos-uploads`; `tools/julos-clip.py` (autostarted in the session) puts it on the GTK
clipboard and acknowledges it, then the page sends Ctrl+V. Files are deleted after 30 minutes.
Guacamole cannot transfer image clipboards over RDP, hence this path.

## Layout

- `gateway/server.js` token server, WebSocket tunnel, upload, agent proxy
- `gateway/public/` the web client (`index.html`, `app.js`, `style.css`, `sw.js`, `manifest.webmanifest`)
- `tools/julos-agent.py` Linux agent (windows, apps, app mode), `tools/julos-clip.py` photo clipboard helper
- `tools/install-linux-agent.sh`, `tools/make-client-cert.sh`, `tools/enable-proxy-mode.sh`
- `docs/AGENT_API.md` the agent API, `docs/ARCHITECTURE.md` the stack and security design, `caddy/Caddyfile.example`
- `.github/workflows/docker.yml` builds the image for `ghcr.io/<owner>/<repo>` (amd64 + arm64)

## Roadmap

Now: the Remote module (see [docs/SCOPE.md](docs/SCOPE.md)). Next: a module core, then Docker, Caddy and DDNS modules, files, Windows agent, multi-host. The goal is one interface for everything you run.

## License

MIT, see [LICENSE](LICENSE). Built on Apache Guacamole (guacd, guacamole-common-js) and guacamole-lite.
