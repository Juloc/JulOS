# Agent API v1

The server never talks to the desktop directly for window management. A small **agent** runs inside the graphical session
of the remote machine and answers a tiny HTTP API. The server proxies it to the browser (`/api/agent/*`, only for a valid
session, see below). The Linux agent (`tools/julos-agent.py`) is the reference implementation; a Windows agent is planned
and has to offer the same endpoints.

## Transport

- Linux: HTTP over a Unix socket, `~/julos-uploads/.agent/agent.sock` (directory 0700, socket 0600). The folder is mounted
  into the server container as `/uploads`, so no network port exists.
- Windows (planned): the agent connects **outbound** to the server (WebSocket, enrolled with a one-time code) and answers the same
  requests through that channel. Nothing listens on the Windows machine.

## Authorisation in the server

Every `/api` route needs the signed-in session of the app (cookie), and changing requests need the `X-JulOS` header
and the app's own origin. There is no separate agent session any more.

| Browser route | Agent request |
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

`POST /api/upload` takes the image as the request body (png, jpg, webp or gif, up to 14 MB; the type is checked on the file header,
20 per minute and user). The server writes it into the shared folder; the clipboard helper (`tools/julos-clip.py`) in the session
puts it on the clipboard and answers with `<file>.ok` or `<file>.err`. No answer within 8 s gives `504` and the file is removed again.

## File upload and clipboard pull (Guacamole engine)

`POST /api/upload-file` takes any file as the request body (up to 64 MB, 20 per minute and user) and its name URL-encoded in the header
`X-File-Name`. The name is cleaned to a plain file name and names that would run when opened (`.exe`, `.desktop`, `.lnk` ...) are refused
with 400. The server writes `files/<id>/<name>` into the shared folder; the helper offers it as a file list on the clipboard
(`x-special/gnome-copied-files` and `text/uri-list`) and answers with `.ok` or `.err` in that folder. No answer within 8 s gives `504` and the
folder is removed again. The helper deletes these folders after 7 days.

`POST /api/clipboard/pull` (30 per minute and user) writes `pull/<id>.req`; the helper answers `<id>.png` (the clipboard image, up to 14 MB),
`<id>.none` (no image: the server answers `204`) or `<id>.err`. Only images, never files or paths. No answer within 8 s gives `504`.
Both are described in [CLIPBOARD.md](CLIPBOARD.md).

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

1. Only listen on a local channel (Unix socket, named pipe) or connect outwards. No open network port.
2. Never return window contents, only titles and icons.
3. Keep the API versioned (`api` field). New fields are fine, removing or renaming needs `/v2`.
