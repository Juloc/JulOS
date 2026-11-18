# Architecture

Decisions for the server rewrite. The **basic structure stays the one used in the other projects** (solution layout, the split into
host, backend library, integrations, workers and tests, the folders around them). Optimising happens *inside* that structure, never by removing it.

## Principles

1. **Keep the skeleton.** Same solution layout and folder names as the sibling projects, so every project reads the same.
2. **Write it ourselves where that is reasonable.** Full control, small attack surface, speed. A dependency has to earn its place.
3. **Do not hand-roll cryptography or the database driver.** Mistakes there are invisible. Those use the framework or one small, well known library.
4. **Fast by default:** minimal APIs, JSON source generation (no reflection), pooled buffers in the relay, one shared database data source, ReadyToRun, a trimmed non-root runtime image.

## Stack

| Part | Choice |
| --- | --- |
| Server | C# on .NET 10, ASP.NET Core host |
| Browser | Plain ES modules and the Apache Guacamole JS client, served as static files (the PWA) |
| Database | PostgreSQL, plain SQL through Npgsql, SQL migrations in `database/`, no ORM |
| Remote display | `guacd` (container, no published port); the server relays the Guacamole protocol itself |
| Linux agent | Python (GTK/Wnck) behind the language-neutral agent API; a Windows agent later in C# |
| Delivery | One image on ghcr.io (amd64, arm64) and a compose file |

## Layout (same shape as the sibling projects)

```
JulOS.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/
  JulOS.Frontend/       ASP.NET host: static client (PWA), HTTP API, WebSocket endpoint, security headers
  JulOS.Backend/        class library: Services, Logic, Data (PostgreSQL access), accounts, sessions, tickets
  JulOS.Integrations/   outside communication: the guacd relay, the agent client (Unix socket)
  JulOS.Workers/        background jobs: purge expired sessions and tickets, upload clean-up
tests/
  JulOS.Backend.Tests/  unit and integration tests (including a real guacd and PostgreSQL in containers)
database/               numbered SQL migrations
deploy/                 compose files, Caddy examples
docs/                   vision, scope, ideas, architecture, agent API
tools/                  Linux agent, installers, user tool
gateway/public/         the current web client (moves to src/JulOS.Frontend/wwwroot when the Node gateway is removed)
```

Inside a project, code is grouped by feature (Auth, Remote, Files) and, in the backend, by Services / Logic / Data as in the siblings.

## Written ourselves

The guacd protocol relay and its handshake, one-time connection tickets, sessions with rotating cookies, TOTP (RFC 6238) and recovery codes,
login lockout, the migration runner, the SQL access helpers, security headers and the CSRF/origin checks, the agent client, the upload path.

## Not written ourselves, and why

| What | Use | Why |
| --- | --- | --- |
| HTTP/TLS server | Kestrel (part of the framework) | Mature and fast |
| PostgreSQL wire protocol | Npgsql | A driver is not worth hand-writing |
| Cryptographic primitives (HMAC, AES, random) | .NET base library | No hand-rolled crypto |
| Password hash | Argon2id from one small library (Konscious), hash format versioned | Memory-hard hash is the one place where a custom version would be risky |
| RDP engine | `guacd` | Years of protocol work |

Target for runtime NuGet packages: **two** (Npgsql and the Argon2 library). Test packages are separate. Versions are centralised in `Directory.Packages.props`.

## Security model

- **Account:** Argon2id for the password, TOTP as second factor, ten one-time recovery codes. Passkeys (WebAuthn) later, see `IDEAS.md`.
- **Session:** 256-bit random value in a cookie (`__Host-` prefix, `Secure`, `HttpOnly`, `SameSite=Strict`), stored only as SHA-256 in PostgreSQL. Idle timeout 30 days, absolute 180 days, rotated every 12 hours with a short grace for parallel requests. Survives server and browser restarts.
- **Lockout:** per address and per account with growing delay.
- **Every API and the WebSocket need a session.** State-changing requests also need a custom header and a matching `Origin`. Static files and the manifest are public so the app can be installed.
- **Connection ticket instead of an encrypted token:** the browser asks for a one-time ticket (random 128 bit, 30 s, kept in memory with the connection settings) and opens the WebSocket with it. The desktop password is typed per connection, held in memory for at most 30 s, never logged or stored.
- **Headers:** strict CSP, `nosniff`, `frame-ancestors 'none'`, HSTS over HTTPS, no referrer.
- **The first account is created on the setup screen**, guarded by the one-time setup code from the server log, so only whoever can read the log can claim the instance. Setup closes after the first account. The console tool (`tools/julos-user.sh`) is the recovery path; passwords are typed in the terminal, never passed as an argument.

## Test instance (a mirror of live, used for every change)

`deploy/dev/compose.yaml` starts the same stack as production with its own PostgreSQL volume, `guacd`, the server built from the same
Dockerfile, plus **throwaway targets**: an SSH server and an RDP desktop (both with generated test accounts that exist only there).
Everything is tested there first, including a real browser run (login, setup, connect to the terminal and the desktop). Integration tests use the same containers.
Production and the test instance differ only in ports, volumes and secrets.

## First start and connections

- **First start:** with no account the server prints a one-time **setup code** to its log. The browser shows a setup screen asking for that code, a user name and a password,
  then shows the authenticator secret (link and key) and ten recovery codes, and finishes only after a valid authenticator code was entered. Afterwards the setup screen is gone.
  The console tool `tools/julos-user.sh` stays as the recovery path (reset password or authenticator).
- **Connections:** a user keeps a list of connections: name, kind (**Desktop** = RDP, **Terminal** = SSH, **Screen** = VNC later), host, port, user name. No secret is stored; the
  password is typed per connection (a password manager can fill it). A connection for the machine the server runs next to is offered at setup. The target host is chosen by the signed-in
  user, so the server may reach anything the signed-in user names; an optional allow-list in the configuration restricts that.
- **Home:** after login the user sees the list, can add, edit and delete connections, and starts one with a tap. The remote view is the existing client.

## Order of work (one step at a time, each tested before the next)

0. Test instance (`deploy/dev`) with throwaway SSH and RDP targets.
1. Solution skeleton in the layout above: host, static files, headers, health, migration runner, PostgreSQL in compose.
2. Accounts: setup screen with setup code, Argon2id, TOTP, sessions, lockout, console tool, tests (the Node version on branch `wip/node-auth` is the executable specification).
3. Relay: ticket endpoint and WebSocket to `guacd`, tested against a real `guacd`.
4. Agent client, upload and log endpoints.
5. Client: setup and login screens, home with connections (Desktop and Terminal), sign out, device list.
6. Switch the compose file over, remove the Node gateway.

Until step 6 the Node gateway keeps running, so nothing breaks in between.
