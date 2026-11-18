# Architecture

Decisions for the server rewrite. Short on purpose: if a rule or layer does not pay for itself, it is not here.

## Stack

| Part | Choice | Why |
| --- | --- | --- |
| Server | C# on .NET 10, ASP.NET Core **minimal APIs** | Same base as the other projects, fast start and low memory, one binary |
| Browser | Plain ES modules, no framework, Apache Guacamole JS client | The browser only speaks JavaScript; the app is small and the client is already written |
| Database | **PostgreSQL**, plain SQL through Npgsql | Same as the other projects. No ORM |
| Remote display | `guacd` (container, no published port) | Mature RDP/VNC/SSH engine; the server only relays |
| Linux agent | Python (GTK/Wnck), Unix socket | The desktop APIs are GTK; the agent API is language-neutral |
| Windows agent (later) | C#, outbound connection | Natural fit, nothing listens on the machine |
| Delivery | One image on ghcr.io (amd64, arm64), compose file | `docker compose up -d` |

## What we deliberately leave out

- **No MVC, Razor Pages, Identity, EF Core, MediatR, AutoMapper, generic repositories.** Each costs startup time, memory and indirection. Endpoints are small functions; data access is a few SQL statements.
- **No layered "Services / Logic / Data" folders for their own sake.** Code is grouped by feature (auth, remote, agent, uploads). A feature gets a folder when it has more than one file.
- **No encrypted connection token.** The old Node server passed RDP settings in an AES token because `guacamole-lite` needed it. Now the browser asks for a one-time ticket (random 128 bit, 30 s, kept in memory, holds the settings including the desktop password) and opens the WebSocket with it.
- **No dependency injection container tricks beyond what the host already gives**, no reflection-driven registration, no source of hidden configuration.
- **No telemetry**, no outbound calls except to `guacd` and the agent.

## Layout

```
server/            the ASP.NET Core host (one project)
  Auth/            accounts, password hash, TOTP, sessions, lockout
  Remote/          ticket, guacd relay (WebSocket <-> guacd), agent proxy
  Files/           photo upload (later: files)
  Db/              Npgsql helper and the SQL migrations (db/NNN_name.sql)
  Program.cs       wiring, headers, static files
server.tests/      unit and integration tests
gateway/public/    the web client (stays, served as static files)
tools/             agent, installers, user tool
```

Performance habits that cost nothing: JSON source generation (no reflection), `ReadyToRun`, trimmed chiseled runtime image, no per-request allocations in the WebSocket relay (pooled buffers, `Pipe` or `Memory<byte>`), a single shared `NpgsqlDataSource`.

## Security model

- **Account:** Argon2id for the password (Konscious, memory 64 MiB, 3 passes), TOTP (RFC 6238) as second factor, ten one-time recovery codes. Passkeys (WebAuthn) are planned after the basics, see `IDEAS.md`.
- **Session:** 256-bit random value in a cookie (`__Host-` prefix, `Secure`, `HttpOnly`, `SameSite=Strict`), stored only as SHA-256 in PostgreSQL. Idle timeout 30 days, absolute 180 days, rotated every 12 hours with a short grace for parallel requests. Survives server and browser restarts.
- **Lockout:** per address and per account with growing delay.
- **Every API and the WebSocket need a session.** State-changing requests also need a custom header and a matching `Origin`. Static files and the manifest are public so the app can be installed.
- **Headers:** strict CSP, `nosniff`, `frame-ancestors 'none'`, HSTS over HTTPS, no referrer.
- **Accounts are created on the server console**, never over the network (`tools/julos-user.sh`). The password is typed in the terminal and never passed as an argument.
- The desktop password is typed per connection, held in memory for at most 30 s, never logged or stored.

## Order of work (one step at a time, each tested before the next)

1. Server skeleton: host, static files, headers, health, migrations, PostgreSQL in compose.
2. Auth: accounts, Argon2id, TOTP, sessions, lockout, console tool, tests (the Node version on branch `wip/node-auth` is the executable specification).
3. Relay: ticket endpoint and WebSocket to `guacd`, tested against a real `guacd`.
4. Agent proxy, upload and log endpoints.
5. Client: login step, sign out, device list.
6. Switch the compose file over, remove the Node gateway.

Until step 6 the Node gateway keeps running, so nothing breaks in between.
