# Codex in its own container (build plan)

Status: **built on a branch, not released** (2026-11-27; plan of 2026-11-26 by the JulOS chief chat). Checked in the dev stack: separation, self-test green and red, refusal for apps while red. See "As built" at the end. Context: [AI-GATEWAY.md](AI-GATEWAY.md), section "Before turning the Codex bridge on, check it by hand" and its recommendation.

## Goal in one sentence

The Codex program runs in a second container with its own user and its own volume that cannot see JulOS's data; JulOS talks to it through one Unix socket; a self-test proves the separation automatically, so the manual check falls away for this setup and the owner turns the bridge on with one click.

## Why

Today `codex app-server` runs inside the JulOS container as the same user. A read-only sandbox forbids writing, not reading, so a prompt sent by an app could make the program read `/data/secret.key`. A separate container removes the path entirely instead of trying to block it.

## Shape

```
julos (container)                         codex (container, image ghcr.io/juloc/julos-codex:X.Y.Z)
  CodexBridge                               user codex (uid 10001), no shell login
    └─ ICodexRunner = SocketRunner  ──────▶ /run/codex/codex.sock  ◀── codex-gate (small program, stdlib only)
       (JULOS_CODEX_SOCKET set)              volume codex_home  (/home/codex: CODEX_HOME, sign-in)
                                             no access to JulOS volumes, no Docker socket, no published port
shared volume codex_sock: holds only the socket file
```

- **Without** `JULOS_CODEX_SOCKET` everything stays as today (download into the data folder, process in the same container, manual check required). Single-container installs keep working.
- **With** it, the in-process installer is not used; the settings card says *Läuft im eigenen Container* and shows the version the gate reports.

## The seam in JulOS (`src/JulOS.Integrations/Ai/`)

Today the bridge starts processes in three places: `RunAsync` (login status, logout), `CodexDeviceLogin.Start` (login --device-auth), `Connection.Start` (app-server). Introduce one small interface and two implementations:

```csharp
public interface ICodexRunner
{
    bool Available { get; }                       // installed (local) or socket reachable (gate)
    string? Version { get; }
    ICodexProcess Start(CodexCommand command);    // CodexCommand: AppServer, Login, LoginStatus, Logout, Version, SelfTest
}
public interface ICodexProcess : IDisposable
{
    Stream StandardInput { get; }                 // UTF-8 lines
    Stream StandardOutput { get; }                // UTF-8 lines; for Login/LoginStatus/Logout stderr is merged into it
    Task<int> WaitForExitAsync(CancellationToken ct);
    void Kill();
}
```

- `LocalRunner`: today's `Prepare` plus `Process.Start`, unchanged behaviour (environment cleared, argument list, work folder, hash check before start).
- `SocketRunner`: one Unix-socket connection per process; first line `{"cmd":"app-server"}` (or `login`, `login-status`, `logout`, `version`, `selftest` with its argument); then raw bytes both ways; the process ends when the gate closes the connection, the last line before that is `{"exit":N}` on a channel the runner strips (see the gate protocol). Kill = close the socket.
- `CodexBridge`, `CodexDeviceLogin` and `Connection` use only `ICodexRunner` / `ICodexProcess`. Free arguments never cross the socket: the command is an enum.
- Registration in `Program.cs`: `SocketRunner` when `JULOS_CODEX_SOCKET` is set, else `LocalRunner`.

## The gate (`tools/codex-gate/`, the program in the sidecar)

Python 3 standard library only, one file, about 150 lines, plus a test with a fake `codex` script.

- Listens on `/run/codex/codex.sock` (socket mode 0660, group shared with JulOS: see the Compose file below), one thread per connection, at most 8 at once, a read timeout for the first line (5 s).
- First line: JSON `{"cmd": ...}`. Allowed commands map to **fixed** argument lists:
  - `app-server` → `codex app-server`
  - `login` → `codex login --device-auth`
  - `login-status` → `codex login status`
  - `logout` → `codex logout`
  - `version` → `codex --version`
  - `selftest` with `{"canary": "<path>", "word": "<16 hex>"}` → no Codex at all, see below
  Anything else: write `{"error":"unknown_command"}` and close.
- Environment of the child: only `PATH`, `HOME=/home/codex`, `CODEX_HOME=/home/codex`, `NO_COLOR=1`, `LANG=C.UTF-8`; work folder `/home/codex/work` (0700).
- Framing: stdout bytes go to the socket as they come; stdin bytes from the socket go to the child; for `app-server` stderr is read and dropped (it can name the account), for the others stderr is merged into stdout. When the child exits the gate sends one final line `\u001e{"exit":N}\n` (record separator first, so it cannot be confused with a JSON line of `app-server`) and closes.
- Logging: only command, duration, exit code. Never content.

### The self-test

Deterministic, no model, no tokens. JulOS starts it when the bridge is turned on, at every start of the gate connection, and once a day.

1. JulOS writes a random 16-hex word into `{JULOS_DATA}/ai/canary` (0600).
2. JulOS asks the gate `selftest` with the canary path as JulOS sees it (`/data/ai/canary`) and the word.
3. The gate checks **from inside its container** and answers `{"ok": bool, "checks": {...}}`:
   - `uid_not_root`: its own uid is not 0
   - `no_julos_data`: the canary path does not exist, `/data/secret.key` does not exist, and no file under `/` (depth 4, skipping `/proc`, `/sys`) contains the word (search with a time limit of 10 s)
   - `no_docker_socket`: `/var/run/docker.sock` and `/run/docker.sock` do not exist
   - `home_private`: `/home/codex` is owned by the gate's user and mode 0700
   - `codex_pinned`: the SHA-256 of the codex binary equals the value baked into the image at build time
4. JulOS records the result (time, ok, which checks failed) in the instance settings and shows it on the card. `ok` true → the bridge counts as verified for this version; `ok` false → the bridge stays off for apps (`provider_unverified`) and the card names the failed check.

The manual check button stays for the local mode only.

## Image `ghcr.io/juloc/julos-codex:X.Y.Z` (`deploy/codex/Dockerfile`)

- Base `debian:13-slim` with `python3-minimal` and `ca-certificates`.
- Codex downloaded at build time from the pinned GitHub release, SHA-256 checked in the Dockerfile (`ARG CODEX_VERSION`, `ARG CODEX_SHA256_AMD64`, `ARG CODEX_SHA256_ARM64`); the binary's own SHA-256 written to `/usr/local/share/codex.sha256` for the self-test.
- A unit test (backend, pure) reads the Dockerfile and checks that version and archive hashes equal `CodexRelease` in `CodexInstaller.cs`, so the two cannot drift.
- User `codex` uid 10001, group `codexsock` gid 10002; `USER codex`; entrypoint `tini -- python3 /usr/local/bin/codex-gate`.
- The workflow `docker` builds and pushes this second image on the same tag and on `main`, amd64 and arm64, same refusal when the tag does not match the version.

## Compose (`compose.yaml` of the repo and the example in `docs/`)

```yaml
  codex:
    image: ghcr.io/juloc/julos-codex:X.Y.Z
    restart: unless-stopped
    read_only: true
    tmpfs: [/tmp]
    cap_drop: [ALL]
    security_opt: [no-new-privileges:true]
    volumes:
      - codex_home:/home/codex
      - codex_sock:/run/codex
    mem_limit: 2g
    pids_limit: 512
    network_mode: bridge    # needs the internet for OpenAI; not on JulOS's network
```

JulOS gets `JULOS_CODEX_SOCKET: /run/codex/codex.sock`, the volume `codex_sock:/run/codex`, and `group_add: ["10002"]` so it can open the socket. The volume `codex_sock` holds nothing but the socket.

The owner's stack repository is **not** changed by this build; the chief does that at release time.

## Tests

- Backend (security-critical): the runner sends only enum commands; an unknown answer line is not treated as an exit; `SocketRunner` with a fake gate (a test server on a temp socket) for app-server lines, login output, exit code; self-test result handling (`ok` false keeps the bridge `provider_unverified`); Dockerfile pin equals `CodexRelease`.
- Gate (Python `unittest`, run by `deploy/dev/test.sh` if `python3` exists): unknown command refused; arguments are fixed; stderr dropped for app-server; exit frame; selftest finds a planted canary word (fails) and passes on a clean tree; max connections.
- Existing bridge tests keep passing with `LocalRunner`.

## Docs in the same commits

`docs/AI-GATEWAY.md` (status of the bridge, the sidecar, the self-test, what stays manual), `docs/SECURITY-REVIEW.md` (a section), `docs/RELEASING.md` (the second image), `docs/DEVELOPMENT.md` (how to run the sidecar in the dev stack), README line, CHANGELOG under *Unveröffentlicht*, i18n de and en for the card texts, service worker version +1 from main. No migration needed (settings live in `instance_settings`); if one turns out to be needed, use `031`.

## Done means

- Tests green (backend suite once through `devq`, frontend tests, gate tests).
- In the dev stack with the sidecar: the card shows *Läuft im eigenen Container*, the self-test is green; a planted word in a file inside the sidecar (by hand, for the test) turns it red. The real ChatGPT sign-in is the owner's and is **not** part of this build.
- Not verified, say so: the real sign-in and a real chat through the bridge, arm64, the browser tests (they need `/dev/fuse`, which the dev container gets with its next restart).

## As built (2026-11-27)

Everything above is built as written. Differences and additions found while running it:

- **First line without reflection JSON.** The server runs with `JsonSerializerIsReflectionEnabledByDefault=false`; the runner writes `{"cmd":...}` with `Utf8JsonWriter` (the first version threw on every call in the real server; the tests had not seen it because the test host allows reflection).
- **A quick restart counts as "back".** JulOS asks the container once a minute; a restart between two looks was never a pause, so the old green result would have counted for up to 36 hours, also for a container recreated with other volumes. The gate creates its socket anew on every start, so the runner remembers the socket file's time and a new time also starts a self-test. Test `A_restart_of_the_container_between_two_looks_also_asks_for_a_new_self_test`.
- **Dev stack** (`deploy/dev/compose.yaml`): the service `codex` is built from `deploy/codex/Dockerfile`, `network_mode: bridge` as live; the server gets the socket volume, `group_add: ["10002"]` and a throwaway `tmpfs` for `/data` (the canary and `secret.key` live there).
- **Gate:** the thread that drops stderr of `app-server` ends quietly when the pipe closes (it printed a traceback now and then).

Checked by hand in the dev stack (amd64): the image build downloaded the real archive of the pinned release and its SHA-256 matched; from inside the codex container `/data`, `/data/secret.key` and `/data/ai/canary` do not exist, the canary word is in no file (`grep -r /`), no Docker socket, user `codex` (10001), home 0700, read-only root, all capabilities dropped, no published port, database and server not reachable by name or address. The card showed *Läuft im eigenen Container (Codex 0.171.0)* and *Selbsttest grün*; a copy of the canary word put into `/home/codex` by hand turned it red (*Der Container sieht Daten von JulOS*) and an app key got `provider_unverified`; removed, green again and the app reached the bridge (`provider_not_ready`, not signed in). Stopping the container showed *error / sidecar_unreachable*; starting it again ran a new self-test by itself.

Not verified: the real ChatGPT sign-in and a real chat through the bridge, the arm64 image (archive hash not downloaded), the workflow run on GitHub, the live server, the browser tests (`/dev/fuse`).

