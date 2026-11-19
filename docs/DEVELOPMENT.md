# Development

## Test instance (use it for every change)

```bash
deploy/dev/up.sh        # builds the server, starts PostgreSQL, guacd, the server and two throwaway targets (SSH, RDP desktop)
deploy/dev/test.sh      # runs all tests against the test instance (PostgreSQL, real guacd, the targets)
deploy/dev/smoke.sh     # walk-through on the running instance: setup, login, windows, launch, photo upload, log (resets the database)
```

- The server is on `http://127.0.0.1:18080`, PostgreSQL on `127.0.0.1:45432` (both only local).
- `up.sh` generates random throwaway credentials in `deploy/dev/.env` on first run. The file is not in the repository.
- The SSH target is reachable from the server container as `test-ssh:2222` with user `tester` and the password `DEV_TARGET_PASSWORD` from `deploy/dev/.env`, and for the tests on `127.0.0.1:42222` (local only; `JULOS_TEST_SSH`, set by `test.sh`). The SSH tests (`RealSshTests`, `RemoteHttpTests` terminal tests) run the real SSH client against it; without `JULOS_TEST_SSH` they are skipped.
- The RDP target is an xrdp + XFCE desktop (`rdp-target:3389`, throwaway login `ubuntu` / `ubuntu`, no published port). It behaves like the Linux VM, including reusing a disconnected session when you connect again.
- `guacd` is published on `127.0.0.1:44822` (local only) so the tests can talk to a real one.
- The test desktop has sound and a microphone: PipeWire is started in the session by `deploy/dev/rdp-target/julos-audio.sh` (autostart; a real VM has systemd for that) and `pipewire-module-xrdp` adds the devices `xrdp-sink` and `xrdp-source`. It takes a few seconds after the first login of a fresh container before audio works. `deploy/dev/play-tone.sh` plays a 440 Hz tone in the session and `deploy/dev/record-mic.sh` records the microphone to stdout (both are run with `docker exec -i -u ubuntu <rdp-target> bash -s < script`).
- The test desktop also runs the real agent and clipboard helper from `tools/` (autostart when a session logs in), and shares a folder with the server (`/uploads` there, `~/julos-uploads` in the desktop), like the live setup. The server runs as the desktop user (`user: "1020:1020"`).
- The RDP target gets `/dev/fuse` and `SYS_ADMIN` (throwaway container, local only) and has `fuse3`: xrdp mounts the files of a client's clipboard through FUSE, which the clipboard tests for files need. xrdp's `chansrv` keeps the capabilities of the last connected client, so after a Guacamole connection it offers no file streaming to the stream engine until it restarts (`docker restart` of the target, or a new session); see [CLIPBOARD.md](CLIPBOARD.md).
- Stop with `docker compose -f deploy/dev/compose.yaml --env-file deploy/dev/.env down` (add `-v` to also drop the database).

## Testing the agent link (agent on another machine)

The default test instance shares a folder between server and desktop, so it uses the Unix socket. To check the outbound link, run a **second compose project** (own name and ports; do not touch
`julos-dev`) whose server does **not** share the folder (`JULOS_AGENT_SOCKET` pointing nowhere, no shared volume) and whose `rdp-target` has none either:

1. start it, set up a throwaway account, add a desktop connection to `rdp-target`, connect once (this starts the agent in socket mode);
2. in the connection dialog choose **Agent einrichten**; in the desktop container, as the desktop user and with `DISPLAY=:10`, download the files from `http://<server>:8080/agent-files/` and run
   `bash install-linux-agent.sh --server http://<server>:8080 --code CODE --allow-http` (`--allow-http` is only for this plain-HTTP test network);
3. reconnect in the browser: the window bar, the launcher, the photo button and "Bild aus der VM holen" now work over the link; the dialog shows **Verbunden**.

`AgentLinkTests` (enrolment, tokens, routing, transfers with a fake agent in memory) run with `deploy/dev/test.sh`.

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
connection list, devices), `app.js` is the remote view (one `RemoteSession` per open session: display, touch, keyboard routing, key bar, window bar; the shared dialogs, colours and the hidden input field live there too), `sessions.js` is the session contract and manager (see [SESSIONS.md](SESSIONS.md)), `lib/` the unmodified
Guacamole JS client. After changing it, `deploy/dev/up.sh` rebuilds the image; then open `http://127.0.0.1:18080` in a browser (phone
connection list, devices), `app.js` is the remote view (display, touch, keyboard, key bar, window bar), `terminal.js` / `terminal-session.js` the native SSH terminal (see ARCHITECTURE.md), `lib/` the unmodified
Guacamole JS client and xterm.js. After changing it, `deploy/dev/up.sh` rebuilds the image; then open `http://127.0.0.1:18080` in a browser (phone
size in the browser's device mode). A fresh database starts at the setup screen; the setup code is in the server log (see above).
On a wide screen with a mouse (`min-width: 1000px` and `pointer: fine`) `shell.js` loads `desktop.js` and `desktop.css` and shows the desktop
interface instead of the phone home: menu bar, dock, launcher, a Home window and one window per open session (each `RemoteSession` of `app.js` mounts its own view into its window; the Desktop menu tiles or cascades the session windows; the engines are untouched). The key bar and the window bar of the phone layout are not shown there (CSS only, nothing removed): while the session window is in front, the top bar shows its menus instead of the normal ones (`desktop-remote.js`): the connection name (reconnect, send text, end), **Tasten** (Esc, Tab, Windows key, Ctrl+Alt+Del, hold Ctrl/Alt/Shift/Win for the next key, F1 to F12), **Kombinationen** (the editable "Combos" row, same storage `pd.rows4` as the phone key bar, with add/edit/reset), **Ansicht** (zoom, fit to window, maximise, full screen) and, for desktops with an agent, **Fenster** (program mode, start program, the VM's window list). They only call hooks that `app.js` exports as `remoteApi`, so the actions are the key bar's own. **Keyboard:** in the desktop layout every key goes to the remote window while it is the active window, with no keyboard button or mode: `app.js` takes the focus for its hidden input field on the first key (also when a button, menu bar, dock or title bar had the focus) and handles that key itself, so it is not lost; Tab, Esc, arrows, F keys and Ctrl/Alt combinations go to the target (browser shortcuts that cannot be intercepted stay with the browser, Ctrl+Shift+V pastes the local clipboard). Keys are left alone when the focus is in a real text field (launcher search, a dialog field, a menu) or when a menu, the launcher or a dialog is open; with a shell window in front they go nowhere. `desktop.js` gives the focus back after clicks on the remote window, window changes, and when a menu, launcher or dialog closes (`syncKeys`), and mouse presses on buttons, bar, dock and title bars do not take it away. The title bar of the window shows a "Tastatur" tag and a frame while it has the keys; the chip in the top bar says "Tastatur → <name>" or "Tastatur → keins". To test in a browser pane that reports no fine pointer, add `&hwkb` to the address.
The dock has three modes (`localStorage` `pd.dockMode`): `auto` (default: hidden, appears when the mouse rests 0.3 s in the bottom 6 px of the screen, hides 0.7 s after it leaves, windows use the full height), `always` and `toggle` (a small knob collapses it); chosen in Einstellungen or with a right click on the dock. The phone never loads these two files. `?desktop` forces the desktop view and `?phone` the phone view
(useful in a browser whose pointer is not reported as "fine"). The Files window (`desktop-files.js`, read-only: browse, search by name, download; one root folder, see ARCHITECTURE.md) and the system overview (CPU, RAM, disk, network tiles on Start and the System view, `GET /api/system/overview`, see ARCHITECTURE.md for what it measures) is real. Docker has no backend yet and is only
shown as "bald verfügbar" (design notes in `IDEAS.md`). Caddy and DDNS are real but **off by default** and read only ([MODULES.md](MODULES.md)): `modules.js` (data and German texts, tested in `tests/frontend`), `desktop-modules.js` (the two windows, loaded on first open; dock, launcher and menu entries appear only when `GET /api/modules` says the module is on) and `mobile-modules.js` (cards on Dienste, details in a bottom sheet).
On every other screen (phone, tablet, narrow window) `shell.js` loads `mobile.js`, `m3.css` and `mobile.css`: the phone shell in Material 3 style with a bottom
navigation bar (Home, Dienste, Server, Monitoring, Profil), bottom sheets for add, edit and start (the existing dialogs, restyled and with segmented buttons for
kind and engine) and a snackbar. `m3.css` holds the design tokens (colour roles computed from one seed colour with `oklch(from ...)` and `light-dark()`, shape,
type, motion, density) and the components, `mobile.css` the screens. The seed colour and Hell/Dunkel are the same settings `app.js` uses (`pd.seed`, `pd.scheme`),
so the remote view's own colours follow. `?classic` shows the old plain list instead (fallback for testing). The remote view is not touched: connecting hides
the shell, leaving shows it again. Real data: user name, saved connections, devices and sessions, and the server card only if `GET /api/system/overview`
answers (an adapter in `mobile.js` reads `cpuPercent`, `memoryUsed/Total`, `diskUsed/Total`, ...; a 404 hides the card for five minutes). Kept on the device only
(localStorage `pd.m.*`): favourites, how often and when a connection was started (for "Meine Favoriten" and "Aktivitäten"), the default engine for new desktops.
Server (without the API) and Monitoring are marked "bald verfügbar"; a switch in Profil hides them. Test it at 375x812 and 412x915, light and dark.
(useful in a browser whose pointer is not reported as "fine"). Docker has no backend yet and is only
shown as "bald verfügbar" (see `IDEAS.md`); Caddy and DDNS see the line above.
On a wide screen with a mouse the remote picture is requested 1:1 in device pixels (zoom 1, shown at exactly 1 / `devicePixelRatio`); the phone keeps 0.8. A browser that does not report a fine pointer (some test panes) gets 0.8: set `localStorage` `pd.k` to `1` to see the PC rendering.
The display tests run against your own compose project with `JULOS_DEV_NETWORK=my-ui_default deploy/dev/test-display.sh`.
`deploy/dev/test-display.sh` runs the display tests (RDP client, H.264, the clipboard bridge against xrdp) in a container on the network of the test instance; with `JULOS_DEV_PROJECT=<project>` it uses another compose project's network.
The encoder selection, the libva structure layouts (`Vaapi/LayoutTests`, numbers from `layout.c` against the real libva headers: `gcc -I<libva-dev include> layout.c`), the headers of the GPU encoder and the codec strings need neither a GPU nor xrdp; one test lets ffmpeg read the headers (skipped without ffmpeg). The GPU path itself only runs on a machine with a render node: `dotnet JulOS.dll diag encoders`, see [ENCODERS.md](ENCODERS.md).
To test next to other sessions, start your own compose project with other ports and your `wwwroot` mounted over `/app/wwwroot` (no rebuild after
frontend changes), for example an override file with `ports: !override ["127.0.0.1:18280:8080"]` and `docker compose -p my-ui -f deploy/dev/compose.yaml -f override.yaml up -d`.
Cookies belong to the host name, not the port: two stacks opened as `127.0.0.1:...` in one browser overwrite each other's login, so open yours as
`my-ui.localhost:18280`.
To test a terminal change without a rebuild, mount your `wwwroot` as described above and open a Terminal connection to `test-ssh` (the server image must contain the SSH relay, so rebuild once after backend changes). A changed host key can be provoked by editing `connections.host_key` in the database; the dialog and the confirmation path are then visible.
To connect, add a Desktop connection to `rdp-target` (login `ubuntu` / `ubuntu`) and a Terminal connection to `test-ssh` port `2222`
(user `tester`, password `DEV_TARGET_PASSWORD` from `deploy/dev/.env`).

## Display tests against the test desktop

`deploy/dev/test-display.sh` runs `tests/JulOS.Display.Tests` in an SDK container on the test network (`JULOS_DEV_NET` names another compose project's network, for example `julos-xyz_default`). The integration tests (`ScreenStreamTests`) are skipped without `JULOS_TEST_RDP_HOST`; the script sets it and also plays a tone and records the microphone in the desktop in the background, which the sound test (PCM blocks arrive, loud enough) and the microphone test (a 600 Hz tone sent as the browser would arrives in the recording) check. Close any browser tab connected to the same desktop first: a second client takes the xrdp session over.

## Browser tests (real browser, Playwright in Docker)

```bash
deploy/dev/e2e.sh             # builds the stack from this working folder, runs six flows in Chromium, tears the stack down (about 1 minute)
E2E_KEEP=1 deploy/dev/e2e.sh  # leave the stack running afterwards (the next run starts in about 40 s); stop it with: deploy/dev/e2e.sh down
```

Nothing is needed on the host except Docker. The script runs its **own stack** in the compose project `julos-e2e`: the services of `deploy/dev/compose.yaml`
(PostgreSQL, guacd, the server built from your working folder, the SSH target, the RDP desktop) with `deploy/dev/e2e/compose.override.yaml` on top, which
publishes no port. The test runner (Playwright with Chromium, image `deploy/dev/e2e/Dockerfile`, pinned version) joins the network of the server container,
so for the browser the server is `localhost` (a secure context, like on the phone: WebCodecs and the clipboard work). It does not touch `julos-dev` or the stacks of other sessions.

Why an own stack and not the test instance: the instance is shared (`smoke.sh` and other sessions reset its database, its image can be an older build of
another branch), while the tests need a fresh database (first-time setup is one of the flows) and the code of the folder you are in. The price is a few
hundred MB of memory while it runs; the images are cached, so the start is fast. Each run drops the database of its stack (targets and guacd stay up with `E2E_KEEP=1`).

Credentials are throwaway and live outside the repository in `~/.cache/julos-e2e` (`JULOS_E2E_STATE` changes the folder): `env` with random passwords for the
database and the SSH target (kept, because the database volume remembers its password), and the test account that each run creates itself. The RDP target
uses its fixed test login `ubuntu` / `ubuntu`.

What the six flows check (`deploy/dev/e2e/tests/e2e.spec.mjs`, in this order, each builds on the earlier ones: run the whole file; `-g` filters only work after flow 1 has run in the same run):

1. **First-time setup and login:** setup code from the server log, account, authenticator code computed from the shown secret, new device, wrong password is refused, login with password and code.
2. **Create connections:** a desktop (RDP, engine Stream) and a terminal (SSH) through the dialog; both are still there after a reload.
3. **RDP stream shows a picture:** connect with the typed target password, wait until the canvas shows a real picture (not black, several colours) and that the stream engine ran (not the automatic fallback to guacd).
4. **Keyboard and mouse reach the target:** Ctrl+Alt+T opens a terminal in the VM, typed text with special characters sets its window title, a click on its maximise button maximises it (all read back through the agent's window list, i.e. from the VM itself).
5. **Native SSH terminal:** a shell prompt appears, `echo e2e-$((6*7))` prints `e2e-42`.
6. **Layouts:** `/?phone` (390x844, touch) and `/?desktop` (1280x800) load, show the saved connection and log no console errors or page errors.

Against flakiness: every wait is a condition with a timeout (picture, agent, window title, terminal text), never a fixed sleep; each flow uses its own browser context
with the saved login; a failed flow is retried once (flow 1 is not: the account exists after the first try); the whole run has a hard limit of 150 s.
Screenshots and traces of failed flows go to `~/.cache/julos-e2e/out/results` (`npx playwright show-trace <file>/trace.zip`, in any Node setup). The run prints the load first:
the development VM has 6 cores, keep it below about 6 or the waits get slow.

Not covered: the Guacamole engine (the default for older connections) in the browser, H.265 and the GPU encoder, sound and microphone, the clipboard and file transfer,
the files window, several simultaneous sessions, a real touch keyboard, and any other browser than Chromium. `dotnet test` and `test-display.sh` cover the engine
parts; the phone keeps its own checklist in [PHONE-TEST.md](PHONE-TEST.md). The click in flow 4 uses the position of the maximise button of a new terminal window of the test desktop
(`deploy/dev/rdp-target`); if that image gets another window manager theme or size, the position in the test has to follow.

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
| `JULOS_AGENT_SOCKET` | Unix socket of the agent on the same machine, default `/uploads/.agent/agent.sock` (agents on other machines connect outbound, no setting needed; see AGENT_API.md) |
| `JULOS_UPLOAD_DIR` | shared folder for photo uploads, default `/uploads`; its disk is the one the system overview shows |
| `JULOS_FILES_ROOT` | the one folder the read-only file browser shows, default `JULOS_UPLOAD_DIR`; `off` disables the file browser |
| `JULOS_CADDY`, `JULOS_DDNS` | `off` (default) or `read`: the read-only Caddy and DDNS modules; their other settings (`JULOS_CADDYFILE`, `JULOS_CADDY_ADMIN_URL`, `JULOS_DDNS_HOSTS`, `JULOS_DDNS_FRITZBOX_URL`, `JULOS_DDNS_IP_URL`, `JULOS_DDNS_CACHE_SECONDS`) are in [MODULES.md](MODULES.md) |
| `JULOS_SETUP_CODE` | optional fixed setup code (otherwise a random one is logged while no account exists) |
| `JULOS_MAX_SESSIONS_PER_USER` | most remote sessions (RDP, SSH, all engines together) one user may have open at once, default `8` |
| `JULOS_DRIVE_ROOT` | the one folder that connections may hand to the desktop as a drive (stream engine only); empty or `off` (default) disables drive redirection; see DRIVES.md |
| `JULOS_STREAM_KEEP_SECONDS` | how long a stream (RDP) session lives on without a browser after the connection dropped, default `300`, `0` = off, at most `3600`; see SESSIONS.md |
| `JULOS_TRUSTED_PROXIES` | networks whose `X-Forwarded-*` headers are trusted (default: private networks) |

## Things learned from the test instance

- guacd treats a client that does not acknowledge `sync` instructions as gone ("User is not responding") and aborts after about 15 s. A browser does it by itself; hand-written test clients must echo every `sync`.
- Connecting a second time to a disconnected xrdp session ("reconnected session") works, and a full picture (about 80 KB of image data) arrives about 1.3 s after connecting. The browser client hides its loading overlay as soon as the remote reports a picture size, not when the picture is "not black".
- **Black picture on the real VM came from RDP's graphics pipeline (GFX).** The VM runs xrdp 0.10 (Debian 13), which guacd drives through GFX; the picture then stays black for seconds, sometimes until a resize, and Windows hosts behave alike. The old test target (xrdp 0.9) never used GFX and so never showed it. The test target is now Debian 13 with xrdp 0.10 like the VM, and the desktop connection sets `disable-gfx`: picture after about 0.25 s on a reconnect, no resize nudge needed (with GFX: about 3.5 s and a nudge).
- **Reconnect storm:** two open browser windows on the same connection kicked each other out of the RDP session in turn (each automatic reconnect took the session from the other). Now only one browser connection per *session* stays open (the slot is user + connection + the session id the browser generates for each open window): a newer one with the same session id takes over and the older one gets status 777 ("session taken over") and stays quiet; other sessions, also to the same target, are untouched. Two RDP sessions to the same target and user still take the one desktop from each other (the target allows one per user); the older one then shows "taken over" instead of retrying. Automatic retries only reset their counter after a connection held for 12 s, the client gives up after 20 s without a connection, and it reconnects at once when the phone comes back after more than 4 s (a locked phone leaves a dead connection with the last picture still showing).
- The Guacamole tunnel in the browser asks for the WebSocket subprotocol `guacamole` and aborts if the server does not select it. A plain test client does not notice, so this one was only found in a real browser; the server now selects it and a test checks it.
- A `u8` string literal such as `"\x89PNG"u8` encodes `\x89` as two UTF-8 bytes; binary signatures must be byte arrays.
- The browser's tunnel only handles whole instructions per WebSocket message, so the relay splits the guacd stream on instruction boundaries (a `;` can appear inside a value, for example in copied text).
- Reconnecting from the client: disconnect the old Guacamole client only after detaching it (`S.client = null`). Its "disconnected" event fires at once; if the handler still sees itself as the current client it arms a retry timer that kills the fresh connection after 1.5 s, 3 s, 4.5 s. The server log shows it as three short connections (1 s, 2 s, 4 s) before one holds.
- Never cancel a pending WebSocket send with the "soft" token of a takeover: cancelling a pending `SendAsync`/`ReceiveAsync` aborts the whole socket. Only the read from guacd is cancelled softly, sends use the hard token. Otherwise a slow browser (a phone) that is replaced mid-send gets no "Sitzung übernommen" message and just keeps a frozen picture; a test with a flooding fake guacd covers it.
- **Remote size and the window bar (phone):** the remote resolution follows the stage, so anything that changes the stage height resizes the VM. The window bar is only rebuilt when the window list changed, room for it is reserved at connect, and every size change goes through `SizeGate` (`wwwroot/layoutlog.js`, tested by `tests/frontend`, run by `deploy/dev/test.sh`): ignore changes under 8 x 16 device pixels, at most one per second, none while the keyboard slides (0.6 s hold after a keyboard change or a focus change of the key input), at most 6 per 20 s (then 4 s pause, logged as `size-storm`). Viewport events are merged per frame, the `ResizeObserver` only counts real stage changes, and in the apps mode a program switch changes the display size only after two equal polls and at most every 8 s (a VM whose focus flips between two programs used to resize the picture on every 2 s poll). Dialogs use `dvh` (with `vh` as fallback) so they follow the keyboard and the address bar. A feedback loop could not be reproduced with the emulated phone viewport on either engine, so the real-device diagnostics are below.
- **Layout diagnostics (`?debug`):** with `?debug` in the address (kept for the tab in `sessionStorage`) a small box on the picture shows window size, visual viewport, safe-area insets, stage, window bar and dock height, keyboard state, the number of size changes in the last 10 s (sent, reported by the VM, stage, connects) and the last sizes; more than 12 changes in 10 s show a red warning. The server log gets compact `lay`, `sendSize`, `remote-size`, `kbvisible` and `loop` lines (numbers only; `loop` is written without `?debug` too). In the Browser pane (hidden pane: layout and `ResizeObserver` only run when a screenshot renders a frame) a loop can be provoked by toggling `#topbar.hidden` between screenshots. Phone test script and how to reach the dev stack from a phone (USB and `adb reverse`, no open port): [PHONE-TEST.md](PHONE-TEST.md).
- Two instances on `127.0.0.1` with different ports share the session cookie (cookies are not port specific), so logging in to one signs the browser out of the other. Open the second one as `localhost` instead.
- Long black picture on an xrdp target, cause found: the session's `xrdp-chansrv` (channel server for clipboard, sound, drives) had died and was never restarted. On every connect xrdp then waits exactly 10 s (`xrdp.log`: "Connecting to chansrv" ... `ERROR xrdp_mm_chansrv_connect: error in trans_connect chan`) before it sends the picture, so "20 kB erreicht nach" in the server log shows about 11.5 s whatever the client does. Check: `pgrep -u "$USER" -a xrdp-chansrv` as the session user must list one process (a target in Docker runs its own chansrv as another user, so `pgrep` without `-u` is misleading). Do not use the socket `xrdp_chansrv_socket_<display>` as a test: it exists right after chansrv starts and is gone again after a client has connected, while connects stay fast. The reliable symptoms are the `ERROR ... trans_connect chan` line in `xrdp.log` and "20 kB erreicht nach" of about 11 s in the server log. Starting it by hand in the session with the session's variables (`DISPLAY`, `XRDP_SOCKET_PATH`, `XRDP_SESSION`, `XRDP_PULSE_*_SOCKET`, `XDG_RUNTIME_DIR`, `DBUS_SESSION_BUS_ADDRESS`, `XAUTHORITY`) brought the picture from 11.5 s down to 3.5 s without closing any window. A fresh login starts chansrv by itself. chansrv also died again within hours of a manual restart, cause still unknown, so `tools/install-chansrv-watch.sh` installs a watcher (autostart, no root) that restarts it after 10 s and writes every outage with time, open RDP connections and the last chansrv lines to the journal (`journalctl -t julos-chansrv-watch`). A connection that is already open does not get the channels back, reconnect once. Without chansrv the dynamic screen size also stops working.
- Selkies (tested as a picture engine, see `IDEAS.md`) registers a RandR monitor called `selkies-primary` on the X display it captures and leaves it behind when it is stopped. The window manager follows that monitor, so after the test every window maximised to the old phone size and the Claude window stayed half a screen wide. Fix: `xrandr --delmonitor selkies-primary`, then `xfwm4 --replace` (windows stay open); the agent's `screen` field is read from Gdk at start and can stay stale, the client does not use it. Any engine that attaches to a live user display has to clean up its monitor on exit.

## Layout

See [ARCHITECTURE.md](ARCHITECTURE.md). SQL migrations are numbered files in `database/`; they are embedded into the backend and applied at start-up, each once, in a transaction.
- Shift+arrows did not select on a PC, Ctrl+V pasted an old remote clipboard: not the VM, the browser client. Named keys (arrows, Home, End ...) were sent as a bare press and release, so a held Shift/Ctrl never reached the remote (check: key log shows `ff51` without `ffe1`). Ctrl+V was forwarded at once, so the local clipboard never left the browser. Now keys with held modifiers go as a chord (AltGr excluded, it produces characters), Ctrl+V / Shift+Insert wait for the `paste` event, put the text on the remote clipboard and then send the key, and the remote clipboard (Ctrl+C / Ctrl+X) is written to the local one. Test in the Browser pane with `?debug&hwkb`; the pane denies clipboard access, so the paste was tested with a synthetic `paste` event.
