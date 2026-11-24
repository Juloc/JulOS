# Agent API v1

The server never talks to the desktop directly for window management. A small **agent** runs inside the graphical session
of the remote machine and answers a tiny API (windows, apps, mode, photo/file/clipboard transfer). The server proxies it to the browser
(`/api/agent/*`, `/api/upload*`, `/api/clipboard/pull`, only for a valid session, see below). The Linux agent (`tools/julos-agent.py`) is the
reference implementation; since 0.5.0 it also has a **host mode** for machines without a desktop (system values, updates, disk health, services, Docker; since 0.6.0 also confirmed actions), and since 0.7.0 the **desktop agent answers the host requests too** (one agent per machine), see [HOST-AGENT.md](HOST-AGENT.md). A Windows agent is planned and has to offer the same requests (see [Windows agent](#windows-agent-design-notes)).

## Transports

An agent belongs to exactly **one host** (a machine with its own saved connections, see [HOSTS.md](HOSTS.md)) and serves all its connections. The server picks the transport per host:

| Transport | When | How |
| --- | --- | --- |
| **Agent link** (outbound) | the machine is not the one the server runs on (the normal case) | the agent connects *to the server* over WebSocket, authenticated with a token it got by enrolment. Nothing listens on the remote machine. |
| **Unix socket** | the server runs on the same machine as the desktop | HTTP over `~/julos-uploads/.agent/agent.sock` (directory 0700, socket 0600), mounted into the server container as `/uploads`. Photo/file/clipboard go through the shared folder. |

Choice for a call `…?c=<connection id>` (its host counts) or `…?h=<host id>` (the browser always says which one it means; it must belong to the signed-in user, else 404):

1. the host's agent is connected → its **link**;
2. an agent is enrolled for the host but not connected → no answer (`health`: `{available:false, enrolled:true}`, everything else `502`). The socket is **not**
   used then: it belongs to some other machine;
3. no agent enrolled for the host → the **Unix socket**, as before. Calls without `c` and `h` also use the socket (old clients, scripts).

The Unix socket is machine-wide (it does not know the host); with several hosts on different machines, enrol an agent for each.

## Enrolment and token

In the app, **Agent einrichten** on a host (host dialog, Hosts view) creates a one-time code and shows the commands for the remote machine (the three agent files
come from the server itself, `GET /agent-files/<name>`, so they match its version; the same folder also serves `install-host-agent.sh` and `SHA256SUMS` for the host agent):

```bash
mkdir -p ~/julos && cd ~/julos
for f in julos-agent.py julos-clip.py install-linux-agent.sh; do curl -fsSLO https://SERVER/agent-files/$f; done
bash install-linux-agent.sh --server https://SERVER --code ABCDE-FGHIJ
```

- The code is 50 bits (`XXXXX-XXXXX`), valid **10 minutes**, usable **once**; a newer code for the same host voids the older one. The database only holds its SHA-256.
  `POST /api/hosts/{id}/agent/code` (session) creates it; it is shown once.
- `julos-agent.py --server URL --code CODE` calls `POST /api/agent-link/enroll` `{code, version, platform, hostname?, os?}` (no session; 10 tries per minute and address, 60 in total)
  and gets `{token, host}`. The token is 256 bits of random data (`jat_…`). The agent stores it in `~/.config/julos/agent.json` (0600, directory 0700); the
  server stores only its SHA-256, together with the host id. A new enrolment replaces the token of that host.
- **Agent entfernen** (`DELETE /api/hosts/{id}/agent`) deletes the row: the token is dead at once and a running link is closed. Deleting the host does the same (a host with connections cannot be deleted); deleting a connection leaves the agent alone.
  On the machine, `julos-agent.py --forget` deletes the stored token.
- `GET /api/hosts` (session) lists the user's hosts; each carries its agent as `{online, lastSeen, version, platform, outdated, latest?, updateAvailable, canUpdate, autoUpdate, kind, caps, allow, control, hostSupport}` (absent when none is enrolled; the last five are described in [HOST-AGENT.md](HOST-AGENT.md)) plus `hostname` and `os` as the agent reported them. `latest` is the version this server ships, see [Updating the agent](#updating-the-agent).
- The agent refuses plain `http://` servers (the token would travel in the clear) except `localhost`; `--allow-http` is only for the test instance.

## The link (WebSocket `/api/agent-link`)

The agent opens `wss://SERVER/api/agent-link` with `Authorization: Bearer <token>` (never in the URL: URLs end up in logs). Certificate and name are checked with the
system's certificate store. The server answers `401` for an unknown or revoked token (nothing else about the reason) and `429` after 30 attempts per minute and address.
One agent per host: a new link replaces an old one that the server has not noticed as dead yet.

Messages are JSON text frames `{"t": …}`, plus binary frames for data (**4 bytes id, big endian, then up to 48 kB of payload**; the server accepts up to 64 kB).
Text frames from the agent are limited to 1 MB; larger frames close the link. Requests carry an `id`; every answer carries the same id.

| Direction | Message | Meaning |
| --- | --- | --- |
| agent → server | `{"t":"hello","agent":"0.5.0","platform":"linux-x11","api":1,"hostname":"…","os":"…"}` | first message, within 10 s; `hostname` and `os` are optional display values (up to 64 characters of letters, digits, space and `. - _ + / ( ) ,`, else ignored). `caps` (agents from 0.5.0, the desktop agent from 0.7.0) names what the agent can: `desktop`, `host` and the host abilities `sysinfo`, `updates`, `services`, `docker` (only these words are kept, each once); `allow` lists the write rights it allows locally (only `updates`, `reboot`, `services`, `docker`, `logs`, `files-root`). A **host-mode agent** sends `"platform":"linux-host"` and `"caps":["host","sysinfo",...]`; a **desktop agent 0.7.0** keeps `"platform":"linux-x11"` and sends `"caps":["desktop","host","sysinfo","updates",...]` and `"allow"`, and answers the `/v1/host/*` requests ([HOST-AGENT.md](HOST-AGENT.md)). Older agents send fewer words (an old desktop agent none): the server adds `desktop` (platform not `-host`) and `host` (any host ability), and shows `hostSupport: update` for a desktop agent before 0.7.0. The server asks and shows by these words, never by the platform name |
| server → agent | `{"t":"welcome","server":"0.5.0"}` or `{"t":"bye","reason":"agent_outdated"}` + close | agents older than `AgentLinks.MinAgentVersion` are refused |
| both | `{"t":"hb"}` | the agent sends one every 5 s (from 0.9.1, with a WebSocket ping; the hello carries `"hb": 5`) and drops a link that gives nothing back for 10 s; the server closes a link that is silent for three beats (12 s at least; 75 s for an agent whose hello has no `hb`); the server also pings every 30 s |
| server → agent | `{"t":"req","id":7,"method":"GET","path":"/v1/windows","body":{…}}` | one of the v1 requests below, **fixed paths only** |
| agent → server | `{"t":"res","id":7,"status":200,"body":{…}}` | the answer (body is JSON, at most 600 kB, else `502`); no answer within 8 s gives `502` |
| server → agent | `{"t":"put","id":8,"kind":"image"\|"file","name":"x.pdf","size":N}`, binary frames, `{"t":"end","id":8}` | photo or file for the clipboard |
| agent → server | `{"t":"res","id":8,"status":200\|4xx\|5xx\|504,"body":{"ok":true}\|{"error":"…"}}` | after the clipboard helper answered (`504`: it did not within 8 s) |
| server → agent | `{"t":"pull","id":9,"max":14680064}` | the image on the remote clipboard |
| agent → server | `{"t":"blob","id":9,"size":N}`, binary frames, `{"t":"res","id":9,"status":200,"body":{}}` / `status 204` (no image) / error | PNG only |
| server → agent | `{"t":"cancel","id":8}` | the browser went away: drop what you have |
| server → agent | `{"t":"update","id":10,"path":"/agent-files/julos-agent.py","sha256":"<64 hex>","version":"0.4.0"}` | replace yourself with the file the server ships, see [Updating the agent](#updating-the-agent) (agents from 0.4.0) |
| agent → server | `{"t":"res","id":10,"status":200,"body":{"ok":true,"version":"0.4.0","restarting":true}}` / `4xx`/`502` `{"error":"…"}` | answered **before** the restart (`restarting:false`: that version or a newer one already runs); an error leaves the old file untouched |

Host-mode requests (`GET /v1/host/sysinfo|updates|services|docker`, fixed paths, sent only to an agent that announced the capability) and their answers are described in [HOST-AGENT.md](HOST-AGENT.md); a host agent answers `404` to the desktop requests and to `put`/`pull`. Write requests (stage 3, agent 0.6.0) use `POST /v1/host/...` with a job id (the file requests of agent 0.8.0, `POST /v1/host/files/list|stat|read|create|write|rename|delete|rmdir|mkdir`, need the word `files-root` and use no job: each is one short request, see [Files as root](HOST-AGENT.md#files-as-root-files-root-agent-080)), are answered `403 not_permitted` without the local permission word, and report progress with `{"t":"progress","id":N,...}` messages; see [Actions](HOST-AGENT.md#actions-stage-3).

Limits and behaviour: at most 32 open requests per link and **one transfer at a time** (the second waits up to 10 s, then `502`). Transfers are chunked, so a
big file never sits in one message; the sender waits for each frame to be written (TCP back pressure) and gives up after 15 s per frame and 3 minutes in total.
Image 14 MB, file 64 MB (the server checks before sending; the agent checks again against the announced size and stops on the first extra byte).
The server validates everything before it leaves the server: the image type from the file header, the file name (`ClipboardNames.Sanitize`: one plain name, no
path, no Windows-forbidden characters) and extensions that would run when opened (`.exe`, `.desktop`, `.lnk` …). The agent cleans the name again (and refuses names that start
with a dot, which the helper uses for its answers), writes into its own folder `~/julos-uploads` (`<id>.img.tmp` → `<id>.<ext>` for an image, `files/<id>/<name>` for
a file) and talks to `julos-clip.py` through the same files as in socket mode (`.ok` / `.err`, `pull/<id>.req`). Leftover `.tmp` files of aborted transfers are swept after an hour.

Security properties: the token identifies one host of one user; every answer is routed by id inside that one link, so an agent can answer only for its own
host and never sees or reaches anything of other users. The server only sends fixed requests with checked values, so there is no way to make it connect somewhere
else (no request forgery). The agent link needs no cookie, but also no browser: an `Origin` or cookie from a web page cannot authenticate it. Audit lines in the server log
(`AgentLink`): code created, agent enrolled/removed, link connected/disconnected, rejected token or code (with the address). No code and no token is ever logged.

## Updating the agent

The image ships the newest agent (`tools/julos-agent.py`, served as `GET /agent-files/julos-agent.py`). The server reads its `AGENT_VERSION` and SHA-256 from the file itself and
keeps them until the file changes, so the announcement can never differ from what is served. The host dialog shows `Agent 0.3.0 … Update verfügbar (0.4.0)`
when the agent's version is lower.

**Update now** (`POST /api/hosts/{id}/agent/update`, session, 6 per minute and user; the body is ignored: the browser chooses neither address nor version nor checksum) sends the connected
agent the `update` message above. Refused with `404` (no agent), `409` (not connected, already current, or an agent older than 0.4.0 that does not know `update`: enrol it once again
with **Agent einrichten**), `503` (the server has no agent file), `502` with the agent's reason when the agent refuses or does not answer within 60 s.
On `200` the agent restarts and reports the new version in its next `hello`; the browser polls the host list until it shows it.

What the agent does (all checks before anything is replaced; any failure leaves the old file and no leftovers):

1. `path` must be exactly `/agent-files/julos-agent.py`. The URL is built by the agent from **its own configured server address** plus that path, never taken from the message, so a message cannot name another host.
   Plain `http://` is refused as for enrolment (except localhost or `--allow-http`); redirects are not followed.
2. Download into a temp file in the folder of the agent file (at most 1 MB), SHA-256 compared with `sha256` (`422` on mismatch).
3. The file must compile as Python and its `AGENT_VERSION` must equal `version` and be newer than the running one (never a downgrade; equal or older answers `200` with `restarting:false`).
4. `fsync`, same file mode, then `os.replace` (atomic: the agent is never half replaced). Reply, then `exec` of the new file in the same process (same PID, service and autostart notice nothing).

Only `julos-agent.py` is replaced; the clipboard helper `julos-clip.py` is not (re-run the installer for that).

**Automatic update** (`PUT /api/hosts/{id}/agent/auto-update` `{"enabled":true}`, default off, per agent): when the agent connects (and when the switch is turned on) and is older than the shipped
file and 0.4.0 or newer, the server sends the same `update` message. At most one automatic attempt per host every 10 minutes, so a failing update cannot loop.

## Running the agent (Linux)

```text
julos-agent.py check               Python (3.8+), PyGObject/GTK3/Wnck, DISPLAY: names what is missing (exit code 3), nothing else is touched
julos-agent.py status              version, running (PID), server and link state, how it is started, log file, prerequisites; exit code 3 when it is not running
julos-agent.py stop                ends the running agent (SIGTERM; the systemd unit is stopped if there is one)
julos-agent.py uninstall [--yes]   lists and removes: service, autostart entries, the agent files, the enrolment (token, unless --keep-config) and log. ~/julos-uploads stays
```

`uninstall` asks first (`--yes` skips the question; without a terminal it refuses without `--yes`) and never deletes program files inside a git checkout. Remove the agent in the app as well
(**Agent entfernen**) so the token is dead on the server too.

At start the agent checks its prerequisites and prints what is missing (with the `apt` package) instead of a traceback; it also refuses to start twice for the same user.
The log is `~/.local/state/julos/agent.log` (`$XDG_STATE_HOME`, folder 0700, file 0600), 512 kB per file with three older ones kept; `agent.pid` and `agent.status` (link state for `status`) live next to it.
`install-linux-agent.sh` checks the prerequisites first, starts the agent through an autostart entry or, with `--systemd`, as the user service `julos-agent.service` (`Restart=on-failure`; it waits for the graphical session).

## Authorisation in the server

Every `/api` route needs the signed-in session of the app (cookie), and changing requests need the `X-JulOS` header
and the app's own origin. There is no separate agent session any more.

| Browser route (all take `?c=<connection id>` or `?h=<host id>`) | Agent request |
| --- | --- |
| `GET /api/agent/health` | `GET /v1/health`, answer gets `available: true`; `{available:false}` when there is no agent |
| `GET /api/agent/windows`, `GET /api/agent/apps` | `GET /v1/windows`, `GET /v1/apps` |
| `POST /api/agent/windows/{id}/{activate\|minimize\|close}` | the same path; `id` must be digits, `activate` takes `{maximize?}` |
| `POST /api/agent/apps/launch` `{id}` | `POST /v1/apps/launch` (id up to 200 characters) |
| `POST /api/agent/mode` `{mode}` | `POST /v1/mode` (`apps` or `desktop` only) |

The server sends only these fixed paths with checked values; the browser cannot send free requests to the agent. Answers are passed
through if they are valid JSON and at most 600 kB; an agent that is missing, slow (8 s) or broken gives `502`.

The socket directory is `0700` and the socket `0600`, owned by the user of the graphical session. The server (in its container) has
to run as **that same user** (`user:` in the compose file), otherwise it cannot connect.

## Photo upload

(With the agent link the image travels through the WebSocket instead of the shared folder, see above; the checks and answers are the same.)

`POST /api/upload` takes the image as the request body (png, jpg, webp or gif, up to 14 MB; the type is checked on the file header,
20 per minute and user). The server writes it into the shared folder; the clipboard helper (`tools/julos-clip.py`) in the session
puts it on the clipboard and answers with `<file>.ok` or `<file>.err`. No answer within 8 s gives `504` and the file is removed again.

## File upload and clipboard pull (helper path)

`POST /api/upload-file` takes any file as the request body (up to 64 MB, 20 per minute and user) and its name URL-encoded in the header
`X-File-Name`. The name is cleaned to a plain file name and names that would run when opened (`.exe`, `.desktop`, `.lnk` ...) are refused
with 400. The server writes `files/<id>/<name>` into the shared folder; the helper offers it as a file list on the clipboard
(`x-special/gnome-copied-files` and `text/uri-list`) and answers with `.ok` or `.err` in that folder. No answer within 8 s gives `504` and the
folder is removed again. The helper deletes these folders after 7 days.

`POST /api/clipboard/pull` (30 per minute and user) writes `pull/<id>.req`; the helper answers `<id>.png` (the clipboard image, up to 14 MB),
`<id>.none` (no image: the server answers `204`) or `<id>.err`. Only images, never files or paths. No answer within 8 s gives `504`.
Both are described in [CLIPBOARD.md](CLIPBOARD.md).

Over the link there is no shared folder: `?c=<connection>` (or `?h=<host>`) selects the agent; the same checks, size limits, rate limits and answers apply (`502` if the link breaks).

## Client log

`POST /api/log` `{events:[{...}]}` (at most 12 kB, 40 events, 60 requests per minute and user) writes measurement values into the
server log. Only keys of letters, digits, `_` and `-` (up to 16 characters) with a number, a boolean or a text of up to 24 characters
are kept; everything else is dropped.

## Endpoints

All bodies are JSON. Window ids are opaque numbers (X11 window ids on Linux).

| Method | Path | Body | Answer |
| --- | --- | --- | --- |
| GET | `/v1/health` | | `{ok, api, platform, mode}` |
| GET | `/v1/windows` | | `{api, mode, screen:{w,h}, windows:[{id, title, app, class, pid, minimized, maximized, active, icon}]}` |
| POST | `/v1/windows/<id>/activate` | `{maximize?: true}` | `{ok}` (restores, maximises and focuses) |
| POST | `/v1/windows/<id>/minimize` | | `{ok}` |
| POST | `/v1/windows/<id>/close` | | `{ok}` |
| GET | `/v1/apps` | | `{apps:[{id, name, icon}]}` |
| POST | `/v1/apps/launch` | `{id}` | `{ok}` |
| POST | `/v1/mode` | `{mode: "apps" \| "desktop"}` | `{ok, mode}` |

`icon` is a base64 PNG (32x32), empty if unknown.

## Modes

- `desktop`: the normal desktop with its own taskbar.
- `apps`: the session's own taskbar/panel is hidden (Linux: XFCE panels get `autohide-behavior=2`, the old values are stored in
  `~/.cache/julos-agent-panels.json`). The browser shows its own window bar instead. The agent returns to `desktop` by itself after
  90 s without requests, on exit and after a crash (on next start).

## Rules for other agents

1. Only listen on a local channel (Unix socket, named pipe) or connect outwards (the link). No open network port.
2. Never return window contents, only titles and icons.
3. Keep the API versioned (`api` field). New fields are fine, removing or renaming needs `/v2`.

## Windows agent (design notes)

Not built yet; the link was designed so that it can be:

- The agent speaks exactly the link above (enrolment with the one-time code, token in a file only the user can read: `%LOCALAPPDATA%\JulOS\agent.json`
  with a user-only ACL, or the Credential Manager). Nothing listens on the machine, so there is no firewall rule and no named pipe is needed for the server.
- It must run **in the interactive session** of the user (a Windows service lives in session 0 and sees no windows): autostart through the Startup folder or a
  per-user scheduled task "at logon", no admin rights.
- The v1 requests map to Win32 as follows: window list = `EnumWindows` (visible, top-level, with a title, not tool windows; id = `HWND` as a number; icon from
  `WM_GETICON`/`GetClassLongPtr`), activate = `ShowWindow(SW_RESTORE/SW_MAXIMIZE)` + `SetForegroundWindow` (with the usual `AttachThreadInput` trick), minimize = `SW_MINIMIZE`, close = `WM_CLOSE`,
  apps = Start menu shortcuts (`.lnk`) and `shell:AppsFolder` (UWP), launch = `ShellExecute`, mode `apps` = hide the taskbar (`Shell_TrayWnd`), `desktop` = show it again, with the same 90 s fallback.
- Photo/file/clipboard: the helper's job is done in the agent itself with `OpenClipboard`/`SetClipboardData` (`CF_DIB` or `PNG`, `CF_HDROP` for files), saved files go to
  `%USERPROFILE%\julos-uploads`. The same limits and the same file-name rules apply on the agent side.
- Language: C# with `ClientWebSocket` (system trust store) is the natural fit; one self-contained executable, version reported in `hello`
  (`platform: "windows"`), so the app can offer the right install command per platform.
