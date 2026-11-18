# Development

## Test instance (use it for every change)

```bash
deploy/dev/up.sh        # builds the server, starts PostgreSQL, guacd, the server and two throwaway targets (SSH, RDP desktop)
deploy/dev/test.sh      # runs all tests against the test instance (PostgreSQL, real guacd, the targets)
deploy/dev/smoke.sh     # walk-through on the running instance: setup, login, windows, launch, photo upload, log (resets the database)
```

- The server is on `http://127.0.0.1:18080`, PostgreSQL on `127.0.0.1:45432` (both only local).
- `up.sh` generates random throwaway credentials in `deploy/dev/.env` on first run. The file is not in the repository.
- The SSH target is reachable from the server container as `test-ssh:2222` with user `tester` and the password `DEV_TARGET_PASSWORD` from `deploy/dev/.env`.
- The RDP target is an xrdp + XFCE desktop (`rdp-target:3389`, throwaway login `ubuntu` / `ubuntu`, no published port). It behaves like the Linux VM, including reusing a disconnected session when you connect again.
- `guacd` is published on `127.0.0.1:44822` (local only) so the tests can talk to a real one.
- The test desktop also runs the real agent and clipboard helper from `tools/` (autostart when a session logs in), and shares a folder with the server (`/uploads` there, `~/julos-uploads` in the desktop), like the live setup. The server runs as the desktop user (`user: "1020:1020"`).
- Stop with `docker compose -f deploy/dev/compose.yaml --env-file deploy/dev/.env down` (add `-v` to also drop the database).

## First start and accounts on the test instance

On a fresh database the server logs a one-time **setup code**:

```bash
docker compose -f deploy/dev/compose.yaml --env-file deploy/dev/.env logs julos | grep "Setup-Code"
```

The setup screen (and `POST /api/setup/begin` + `/api/setup/finish`) creates the first account. After that setup is closed.
Recovery on the server console (passwords are typed in the terminal, never passed as arguments):

```bash
docker compose -f deploy/dev/compose.yaml --env-file deploy/dev/.env exec -it julos dotnet JulOS.dll user list
docker compose -f deploy/dev/compose.yaml --env-file deploy/dev/.env exec -it julos dotnet JulOS.dll user passwd NAME
```

`add`, `passwd`, `totp` (new authenticator and recovery codes), `logout` and `remove` are available too.
To start over: `docker compose -f deploy/dev/compose.yaml --env-file deploy/dev/.env down -v`.

## The browser client

`src/JulOS.Frontend/wwwroot` holds the client (plain ES modules, no build step): `shell.js` is the account and home part (setup, login,
connection list, devices), `app.js` is the remote view (display, touch, keyboard, key bar, window bar), `lib/` the unmodified
Guacamole JS client. After changing it, `deploy/dev/up.sh` rebuilds the image; then open `http://127.0.0.1:18080` in a browser (phone
size in the browser's device mode). A fresh database starts at the setup screen; the setup code is in the server log (see above).
To connect, add a Desktop connection to `rdp-target` (login `ubuntu` / `ubuntu`) and a Terminal connection to `test-ssh` port `2222`
(user `tester`, password `DEV_TARGET_PASSWORD` from `deploy/dev/.env`).

## Build and test without Docker

```bash
dotnet build JulOS.slnx -c Release
dotnet test --project tests/JulOS.Backend.Tests/JulOS.Backend.Tests.csproj    # database tests are skipped without JULOS_TEST_DB
```

## Server settings (environment)

| Variable | Meaning |
| --- | --- |
| `JULOS_DB` | PostgreSQL connection string (required) |
| `JULOS_GUACD` | `host:port` of guacd, default `guacd:4822` |
| `JULOS_ALLOWED_HOSTS` | optional comma-separated list of hosts the signed-in user may connect to; empty means any host the server can reach |
| `JULOS_AGENT_SOCKET` | Unix socket of the agent, default `/uploads/.agent/agent.sock` |
| `JULOS_UPLOAD_DIR` | shared folder for photo uploads, default `/uploads` |
| `JULOS_SETUP_CODE` | optional fixed setup code (otherwise a random one is logged while no account exists) |
| `JULOS_TRUSTED_PROXIES` | networks whose `X-Forwarded-*` headers are trusted (default: private networks) |

## Things learned from the test instance

- guacd treats a client that does not acknowledge `sync` instructions as gone ("User is not responding") and aborts after about 15 s. A browser does it by itself; hand-written test clients must echo every `sync`.
- Connecting a second time to a disconnected xrdp session ("reconnected session") works, and a full picture (about 80 KB of image data) arrives about 1.3 s after connecting. The browser client hides its loading overlay as soon as the remote reports a picture size, not when the picture is "not black".
- **Black picture on the real VM came from RDP's graphics pipeline (GFX).** The VM runs xrdp 0.10 (Debian 13), which guacd drives through GFX; the picture then stays black for seconds, sometimes until a resize, and Windows hosts behave alike. The old test target (xrdp 0.9) never used GFX and so never showed it. The test target is now Debian 13 with xrdp 0.10 like the VM, and the desktop connection sets `disable-gfx`: picture after about 0.25 s on a reconnect, no resize nudge needed (with GFX: about 3.5 s and a nudge).
- **Reconnect storm:** two open browser windows on the same connection kicked each other out of the RDP session in turn (each automatic reconnect took the session from the other). Now only one browser connection per user and connection stays open: the newer one takes over and the older one gets status 777 ("session taken over") and stays quiet. Automatic retries only reset their counter after a connection held for 12 s, the client gives up after 20 s without a connection, and it reconnects at once when the phone comes back after more than 4 s (a locked phone leaves a dead connection with the last picture still showing).
- The Guacamole tunnel in the browser asks for the WebSocket subprotocol `guacamole` and aborts if the server does not select it. A plain test client does not notice, so this one was only found in a real browser; the server now selects it and a test checks it.
- A `u8` string literal such as `"\x89PNG"u8` encodes `\x89` as two UTF-8 bytes; binary signatures must be byte arrays.
- The browser's tunnel only handles whole instructions per WebSocket message, so the relay splits the guacd stream on instruction boundaries (a `;` can appear inside a value, for example in copied text).
- Reconnecting from the client: disconnect the old Guacamole client only after detaching it (`S.client = null`). Its "disconnected" event fires at once; if the handler still sees itself as the current client it arms a retry timer that kills the fresh connection after 1.5 s, 3 s, 4.5 s. The server log shows it as three short connections (1 s, 2 s, 4 s) before one holds.
- Never cancel a pending WebSocket send with the "soft" token of a takeover: cancelling a pending `SendAsync`/`ReceiveAsync` aborts the whole socket. Only the read from guacd is cancelled softly, sends use the hard token. Otherwise a slow browser (a phone) that is replaced mid-send gets no "Sitzung übernommen" message and just keeps a frozen picture; a test with a flooding fake guacd covers it.
- Long black picture on an xrdp target, cause found: the session's `xrdp-chansrv` (channel server for clipboard, sound, drives) had died and was never restarted. On every connect xrdp then waits exactly 10 s (`xrdp.log`: "Connecting to chansrv" ... `ERROR xrdp_mm_chansrv_connect: error in trans_connect chan`) before it sends the picture, so "20 kB erreicht nach" in the server log shows about 11.5 s whatever the client does. Check: `pgrep -u "$USER" -a xrdp-chansrv` as the session user must list one process (a target in Docker runs its own chansrv as another user, so `pgrep` without `-u` is misleading). Do not use the socket `xrdp_chansrv_socket_<display>` as a test: it exists right after chansrv starts and is gone again after a client has connected, while connects stay fast. The reliable symptoms are the `ERROR ... trans_connect chan` line in `xrdp.log` and "20 kB erreicht nach" of about 11 s in the server log. Starting it by hand in the session with the session's variables (`DISPLAY`, `XRDP_SOCKET_PATH`, `XRDP_SESSION`, `XRDP_PULSE_*_SOCKET`, `XDG_RUNTIME_DIR`, `DBUS_SESSION_BUS_ADDRESS`, `XAUTHORITY`) brought the picture from 11.5 s down to 3.5 s without closing any window. A fresh login starts chansrv by itself. chansrv also died again within hours of a manual restart, cause still unknown, so `tools/install-chansrv-watch.sh` installs a watcher (autostart, no root) that restarts it after 10 s and writes every outage with time, open RDP connections and the last chansrv lines to the journal (`journalctl -t julos-chansrv-watch`). A connection that is already open does not get the channels back, reconnect once. Without chansrv the dynamic screen size also stops working.
- Selkies (tested as a picture engine, see `IDEAS.md`) registers a RandR monitor called `selkies-primary` on the X display it captures and leaves it behind when it is stopped. The window manager follows that monitor, so after the test every window maximised to the old phone size and the Claude window stayed half a screen wide. Fix: `xrandr --delmonitor selkies-primary`, then `xfwm4 --replace` (windows stay open); the agent's `screen` field is read from Gdk at start and can stay stale, the client does not use it. Any engine that attaches to a live user display has to clean up its monitor on exit.

## Layout

See [ARCHITECTURE.md](ARCHITECTURE.md). SQL migrations are numbered files in `database/`; they are embedded into the backend and applied at start-up, each once, in a transaction.
