# Agent API v1

The gateway never talks to the desktop directly for window management. A small **agent** runs inside the graphical session
of the remote machine and answers a tiny HTTP API. The gateway proxies it to the browser (`/api/agent/*`, only for a valid
session, see below). The Linux agent (`tools/julos-agent.py`) is the reference implementation; a Windows agent is planned
and has to offer the same endpoints.

## Transport

- Linux: HTTP over a Unix socket, `~/julos-uploads/.agent/agent.sock` (directory 0700, socket 0600). The folder is mounted
  into the gateway container as `/uploads`, so no network port exists.
- Windows (planned): the agent connects **outbound** to the gateway (WebSocket, enrolled with a one-time code) and answers the same
  requests through that channel. Nothing listens on the Windows machine.

## Authorisation in the gateway

`/api/agent/*` requires the header `X-Session: <sid>`. The `sid` is returned by `/api/token` and only becomes valid
once the Guacamole WebSocket of that session has been open for more than 8 seconds. Without it the gateway answers 401.
With `PROXY_SECRET` set, the usual `X-Proxy-Secret` check applies first.

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
