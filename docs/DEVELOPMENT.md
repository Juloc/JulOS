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
- A `u8` string literal such as `"\x89PNG"u8` encodes `\x89` as two UTF-8 bytes; binary signatures must be byte arrays.
- The browser's tunnel only handles whole instructions per WebSocket message, so the relay splits the guacd stream on instruction boundaries (a `;` can appear inside a value, for example in copied text).

## Layout

See [ARCHITECTURE.md](ARCHITECTURE.md). SQL migrations are numbered files in `database/`; they are embedded into the backend and applied at start-up, each once, in a transaction.
