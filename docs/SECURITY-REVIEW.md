# Security review (version 0.13.1, 2026-11-19)

Second full pass, written for the case that matters now: JulOS is **reachable from the internet** and behind its login sits a lot: remote desktops, SSH terminals with stored
credentials, agents on the hosts, confirmed host actions (apt, systemctl, Docker, reboot through a root helper), files, backups. The first review (0.2.0) predates all of that
(agents, hosts, host actions, root helper, SSH, backup, modules); its still-valid results are carried over below, its open items were checked again.

Method: read the code of every area below, ran what could be run against a real Kestrel instance with a throwaway database (the in-memory test server of the test suite, own schema,
removed afterwards) and against the dev instance (`deploy/dev`, loopback; it runs an older build, 0.3.0, so it was used only for header and routing probes), and wrote tests where a
claim can be tested. **Nothing was tested against the live address**; the live configuration (Caddy, firewall, environment) was not seen. What could not be verified is listed in the last section.

"Fixed" = committed with this review, each with a test that fails without the change. "Open" = written down with a suggestion and effort, **not** changed because it needs a decision
(operation, key handling, new feature) or is larger than a local change.

## Summary

- **No finding lets an anonymous visitor do anything beyond the public routes.** A test now enumerates every registered route and proves it (see "Routes").
- Passwords: Argon2id, **current**. Dependencies: **no known vulnerability** in any NuGet package (direct or indirect); runtime and base image current as of 2026-11-10 (.NET 10.0.15). One unpatched Ubuntu CVE in a library JulOS only uses for encoding (F-12). Table below.
- Findings: **0 critical, 1 high, 7 medium, 16 low, 8 info** (32; F-24 to F-32 come from the review of the Files-over-SFTP branch, see "Files over SFTP" below). **7 fixed** (F-06, F-07, F-08, F-09, F-14, F-24, F-27), one partly (F-03), the rest open with a suggestion because they need a decision on operation, key handling or a new feature. The one high finding (F-24, a compromised host could make a host-to-host copy write outside the chosen folder on another host) was fixed in the review before the merge.
- The weak spots are not in the login but around it: **where the backup lands** (F-01), **what a stolen session can do without a second confirmation** (F-04, F-17), **missing early warning** (F-18) and **configuration that is optional but should not be** (F-02, F-03).

## Findings

| # | Severity | Finding | Status |
| --- | --- | --- | --- |
| F-01 | Medium | Backup: the key copy (`secret.key`) lies next to the dump, in the same folder and at the same second target; the dump holds TOTP secrets and password hashes unencrypted | Open (decision) |
| F-02 | Medium | Connections may point at any host the server reaches while `JULOS_ALLOWED_HOSTS` is empty (internal services, metadata address, port scan through error texts) | Open (configuration; was 0.2.0 #1) |
| F-03 | Medium | `JULOS_TRUSTED_PROXIES` defaults to every private network: a LAN device that reaches port 8080 can forge client address and protocol | Partly fixed (loud startup warning), open (configuration; was 0.2.0 #3) |
| F-04 | Medium | A stolen session reaches everything the owner can, including host actions, with no second factor (the confirmation proves intent, not identity) | Open (recommendation: step-up TOTP) |
| F-05 | Medium | The JulOS server is the root of trust for every host: it ships the agent, the root helper and the installer, and can update agents (opt-in); a compromised server means code execution as the agent user on every host, and root where the helper or the Docker group is allowed | Open (design, accepted; limits listed) |
| F-06 | Medium | Mutual TLS in the Caddy example makes the agents unusable (they have no client certificate), so the real setup either has no mTLS or no agents | **Fixed** (new validated example `caddy/Caddyfile.internet.example`) |
| F-07 | Low | An agent could make the browser call other routes: a window id such as `../../auth/logout-others?` was put into a request path on click | **Fixed** (server and client accept only numeric ids; test) |
| F-08 | Low | Root helper: a client that connects and sends nothing (or one byte a second) holds one of the four socket slots forever; the agent user can block all helper actions | **Fixed** (15 s deadline for the request line; test) |
| F-09 | Low | Open desktop and terminal connections noticed a logout only after up to 60 s | **Fixed** (15 s; test) |
| F-10 | Low | TOTP secret stored as plain text in the database (`users.totp_secret`) | Open (decision, key handling) |
| F-11 | Low | Login lockout can be used to keep the owner out; counters are in memory (reset by a restart) | Open (accepted; was 0.2.0 #7) |
| F-12 | Low | Agent enrolment: the global limit (60 a minute) lets anyone on the internet block enrolment of a new agent; per-address keys use the full IPv6 address | Open (accepted) |
| F-13 | Low | Compose: PostgreSQL without `cap_drop`/`no-new-privileges`, no resource limits, `latest` in the image Compose file, read-only root only as an option, base images by tag not digest | Open (was 0.2.0 #16) |
| F-14 | Low | GitHub Actions were pinned to major tags; `packages: write` is granted workflow-wide | **Fixed** (pinned to commit SHAs, Dependabot keeps them current); permissions split: open (single job, only pushes on non-PR events) |
| F-15 | Low | Clipboard uploads allocate the announced size up front (up to 64 MB per upload) | Open (was 0.2.0 #13) |
| F-16 | Low | A restore from backup brings back the sessions and agent tokens of that day (the dump holds their hashes), including a stolen cookie that was revoked later | Open (documented; revoke all sessions after a restore) |
| F-17 | Low | Kept RDP sessions (default 300 s) stay alive after a logout (resumable only with a valid session of the same user) | Open (accepted) |
| F-18 | Low | No early warning and no reuse detection: no notice on a new login, rotated session cookies are not tested for reuse, the session list shows only user agent and address | Open (recommendation) |
| F-19 | Info | The installer (`curl … \| sudo sh`) and its `SHA256SUMS` come from the same server over TLS: the checksum protects against corruption, not against a compromised server | Open (accepted, see F-05) |
| F-20 | Info | `libopenh264` 2.4.1 in the image: Ubuntu 24.04 has no fix for CVE-2025-27091 (decoder heap overflow); JulOS only encodes | Info |
| F-21 | Info | `/api/health` is public and names the version; `Host` header is not restricted (`AllowedHosts`) | Accepted (was 0.2.0 #18) |
| F-22 | Info | HSTS has no `includeSubDomains`/`preload`; CSP keeps `style-src 'unsafe-inline'` (script-src is strict) | Info |
| F-23 | Info | Root helper honest limits: with the right word it starts/stops any `*.service` and reads any unit's journal; `apt` treats names with `.`/`+` as patterns | Info (documented in HOST-AGENT.md) |
| F-24 | High | Files over SFTP: a file name from the target such as `../../x` or `a/b` was used as a path part, so copying a folder from a compromised host to another host wrote outside the chosen folder there (same class as scp CVE-2019-6111); recursive delete used it too | **Fixed** (names are hidden by the session and ignored again by `FileTransfer`; 5 tests) |
| F-25 | Medium | Files over SFTP: the write "confirmation" is a flag the client sets (`confirm: true`), not a server-issued token like host actions; with `JULOS_FILES_WRITE=on` a stolen session can upload, overwrite, move and delete on every SSH host without friction | Open (decision; default is off) |
| F-26 | Low | Files audit is written at the end and failures to write it are only logged: a crash or database outage during a write leaves no entry, and the write is not refused (host actions are fail-closed) | Open |
| F-27 | Low | A target that announces small sizes but sends more could exceed the limit for a whole copy (only each file was capped) | **Fixed** (total of received bytes capped; test) |
| F-28 | Low | Recursive delete and copy list a folder and then use paths: a local user of the target who can write in that folder can swap a folder for a symlink in between; effect limited to what the SSH user may do | Open (accepted, documented) |
| F-29 | Low | Pooled SFTP sessions are not closed when the connection is edited or deleted or the app session ends (up to 60 s idle, longer with a running job); they never hold a password | Open (S) |
| F-30 | Info | Names with Unicode format characters (right-to-left override) are shown as they are (spoofing `fdp.exe`-style names) | Info |
| F-31 | Info | After a crash of the server a `.julos-*.part` temporary file can remain on a target (visible, named) | Info |
| F-32 | Info | Download and text view follow symlinks on the target (stat with follow): they read what the SSH user can read | Info (by design) |

### F-01 Backup next to its key (Medium, open, needs a decision)

- **Where:** `src/JulOS.Backend/Services/BackupService.cs` (dump and `secret.key` copy into the same folder, `JULOS_BACKUP_KEY=copy` is the default), `BackupTarget.cs`, `src/JulOS.Workers/SftpBackupTarget.cs` (the same two files to the second target), `src/JulOS.Backend/Data/DbDump.cs` (every table).
- **Attack path:** whoever can read the backup folder, the NAS share or the SFTP account gets the dump (files are written 0600, but that stops at the machine's border) **and** the key that decrypts all stored connection passwords in it, plus the Argon2id hash of the login password and the TOTP secret in plain text. With those, offline guessing of the login password and the TOTP code generator are one step away, and every stored target password (RDP, SSH) is readable.
- **Evidence:** code (files above). `docs/BACKUP.md` describes the dump contents and advises `JULOS_BACKUP_KEY=off` for places others can read, but the **default is `copy`**, and the second target gets the key too unless that switch is off; a default that is safe only when the owner has read the advice is the finding. `JULOS_BACKUP_KEY=off` is the existing escape (key kept elsewhere).
- **Suggested fix:** (a) smallest: for the **second** target never copy the key by default (separate switch), keep the local copy; effort S, but then a dead machine means retyping stored passwords unless the key is stored elsewhere. (b) better: encrypt dump and key copy with a passphrase the owner types once into the settings (AES-256-GCM, key from Argon2id; restore asks for it); effort M. (c) encrypt `totp_secret` in the database with the same box (F-10). Until decided: **put the second target on a place only you can read, or set `JULOS_BACKUP_KEY=off` and keep the key in a password manager.**

### F-02 Outbound reach (Medium, open, configuration)

Unchanged from 0.2.0 #1: `HostPolicy.cs` allows everything when the list is empty; `ConnectionRules.HostPattern` checks characters only. Verified again by code. **Attack path:** a stolen session saves or quick-connects to `postgres:5432`, the metadata address, any LAN address, and reads reachability from the error texts (SSH and RDP distinguish unreachable, timeout, not-SSH, refused). **Fix:** set `JULOS_ALLOWED_HOSTS` to the hosts you really connect to (exact names or addresses, comma separated). The live value was **not verified**. Optional code change (S): refuse link-local (`169.254.0.0/16`, `fe80::/10`) and the Compose service names always.

### F-03 Trusted proxies (Medium, partly fixed)

Unchanged default (`Program.cs`, `ForwardedHeaders`), now with a warning in the log at every start while the variable is empty (test `The_server_warns_at_start...`). **Attack path:** the Caddy example proxies to `<host>:8080`, so that port is open to the LAN; any device there can send `X-Forwarded-For` (rotating addresses defeats the per-address login lockout; the per-account lockout still holds) and `X-Forwarded-Proto: https` (changes cookie name and `Secure` flag). Direct requests from the internet are not from a private address and are not trusted. **Fix (configuration, S):** `JULOS_TRUSTED_PROXIES=<Caddy address>/32` and a firewall rule that lets only Caddy reach port 8080. Caddy itself replaces a client's `X-Forwarded-For` (do not add `trusted_proxies` to the site).

### F-04 Stolen session (Medium, open, recommendation)

A session cookie is `HttpOnly`, `__Host-`, `SameSite=Strict`, so theft needs a compromised browser or device, not a web attack (no XSS found, see "Web client"). Once stolen it opens every connection (stored passwords are used server side), starts terminals on hosts, and can run confirmed host actions: the two-step confirmation is bound to the session and proves intent, not identity. **Suggested fix (M):** step-up for dangerous things: ask for a fresh TOTP code (not the password) before `power.reboot`, `power.poweroff`, `updates.install`, `service.*`, `docker.*`, creating an agent code and removing an agent, and before account changes. Together with a shorter idle timeout (S, `SessionStore.IdleDays` from 30 to 7 for an internet target) this limits what a stolen cookie is worth. Passkeys (see recommendations) are the stronger answer to phishing, which TOTP does not stop (a live proxy relays the code).

### F-05 The server as root of trust (Medium, design)

`/agent-files/` serves `julos-agent.py`, `julos-agent-helper.py` and both installers plus `SHA256SUMS` made from the same bytes (`AgentLinkEndpoints.cs`, `AgentRelease.cs`). The agent verifies a self-update by the SHA-256 the **same server** names (`tools/julos-agent.py` `fetch_verified`, TLS enforced unless `--allow-http`, redirects refused), so the checksum defends against transport damage and a man in the middle, never against the server. Consequences, all by design:

- Anyone who gets code execution on the JulOS container or the repository image can push code to every enrolled agent. **Automatic updates are off by default; keep them off** on hosts where you do not accept that, and update by hand (button) after reading the CHANGELOG.
- The agent runs as an unprivileged user (`NoNewPrivileges`, `ProtectSystem=strict`, empty capability set). The root helper is **not** replaced by the self-update (installer only). Without any permission word, an agent can only read. **A compromised agent plus `--docker-group` is root on that host** (the Docker group is root-equivalent; the installer says so). Allow `reboot`/`services` only where a denial of service by a compromised agent user is acceptable.
- Reaching `permissions`: `/etc/julos-agent/permissions` is root-owned; the helper refuses it unless root owns it and nobody else can write (`read_permissions`). The agent user cannot widen its own rights.

**Hardening that would remove the trust (L):** sign releases (a key outside the server) and let the agent verify the signature, or pin the server certificate in the agent.

### F-06 mTLS example and agents (Medium, fixed)

`caddy/Caddyfile.example` uses `client_auth { mode require_and_verify }`, which every client must pass, **including the agents** (plain Python, no certificate): they cannot connect from outside, so the owner has to choose between the second layer and the agents, and may drop mTLS. New `caddy/Caddyfile.internet.example`: `verify_if_given` plus a matcher that lets only `/api/agent-link`, `/api/agent-link/enroll` and `/agent-files/*` through without a certificate and answers everything else 403 before JulOS sees it. Checked with Caddy 2: the file validates, and in a throwaway Caddy with a throwaway CA: no certificate → 403 on `/` and `/api/connections`, 200 on the three agent paths; valid certificate → 200 everywhere; certificate from another CA → refused in the handshake. **Not verified** on the live Caddy (version, other site blocks). The Caddy address and request-body limit placeholders must be adapted.

### F-07 Agent data in a request path (Low, fixed)

- **Where:** `src/JulOS.Frontend/wwwroot/app.js` (`activateWindow`, window menu), `src/JulOS.Frontend/Agent/AgentEndpoints.cs` (`GET /api/agent/windows` passed the agent's JSON through).
- **Attack path:** a compromised desktop agent reports a window with id `../../auth/logout-others?`; when the user taps that window chip, the browser sends `POST /api/agent/windows/../../auth/logout-others?...` with cookie and `X-JulOS` header, which the URL normalisation turns into another route. Reachable: only routes that accept an empty body (logout, logout-others, a service check, a DDNS update). No data is readable, no XSS.
- **Fix:** the server now drops windows whose id is not a number (`WindowList.Sanitize`, same rule as the POST route), the client filters the list again and `agent()` refuses a path that is not `[A-Za-z0-9_/-]`. Test: `A_window_list_from_an_agent_keeps_only_numeric_window_ids`.

### F-08 Root helper deadline (Low, fixed)

`tools/julos-agent-helper.py`: the socket unit allows 4 connections (`MaxConnections=4`); `readline` had no deadline, so a process of the agent user could open four sockets and keep them silent, blocking every confirmed action until reboot. The request line must now be complete within 15 s (`read_request_line`, also for byte-at-a-time senders). Test `test_a_silent_or_dripping_client_does_not_hold_the_helper` (without the fix it hangs).
Checked and fine in the helper: five fixed verbs, no shell, argument lists, empty environment (fixed `PATH`, `LC_ALL`), package pattern without a trailing `-` (apt "remove") and never a leading `-`, unit pattern `*.service` without a leading `-` and `--` before the unit, `julos-agent*` protected, permission file re-read at every request, output lines cleaned and bounded, hang-up of the agent does not abort a running `apt`. Socket `0660`, group = agent user, `Accept=yes`.

### F-09 to F-23 in short

- **F-09:** `SshRelay.SessionCheck` and `ScreenRelay.SessionCheck` 60 s → 15 s (one cheap query per open session). Logout, "other devices", password change and expiry all end open connections within that time. Immediate teardown (cancel by session id from the registry) would be M effort.
- **F-10:** `users.totp_secret` is plain text (`database/001`), so a database dump or an SQL injection (none found) hands out the TOTP secret. Suggested (S to M): store it as `SecretBox` ciphertext with the user id as associated data, migrate lazily on login; cost: losing `secret.key` then also loses the second factor (recovery codes and `tools/julos-user.sh` stay the way back). Decision needed.
- **F-11:** accepted trade-off, see 0.2.0 #7. For an internet target the mTLS layer removes it for strangers.
- **F-12:** `AgentLinkEndpoints.cs`: `enroll:all` 60 a minute, 10 per address; the code has 50 bits and lives 10 minutes, so guessing is hopeless (600 guesses per window at most), but the shared limit can be used to block a legitimate enrolment. Not worth changing.
- **F-13:** `docker-compose.yml` has `no-new-privileges`, `cap_drop: ALL`, health check, non-root user for the server; PostgreSQL has none of that and no memory/CPU limit. Suggested (S): same two options on PostgreSQL, `mem_limit`/`pids_limit` on both, a version tag instead of `latest` in `docker-compose.image.yml`, try `read_only: true` with `tmpfs: /tmp` on the live stack (verified to start and answer health only), pin base images by digest once Dependabot (added) keeps them current.
- **F-14:** all actions are pinned to commit SHAs with the version as a comment, `.github/dependabot.yml` updates them. Workflow permissions: `tests` read-only; `docker` has `packages: write` for the whole workflow (pull requests do not push). Tag protection for `v*` and branch protection for `main` are GitHub settings: **not verified**, recommended (only the owner may create `v*` tags: a tag publishes an image).
- **F-15, F-16, F-17:** as described in the table; after a restore run `tools/julos-user.sh logout NAME` to end all sessions, and rotate agents if the backup is older than a removal.
- **F-18:** details under "Operations".
- **F-19:** the installer prints nothing to compare against. Improvement (S): the app shows the SHA-256 of the installer next to the command so it can be compared on a second device.
- **F-20:** `libopenh264-7` 2.4.1+dfsg-1 is what the encoder is validated against (Dockerfile comment). Ubuntu 24.04 lists CVE-2025-27091 (decoder) as not yet fixed; JulOS never decodes H.264 on the server (browser decodes), so it is not reachable. Revisit when noble ships a fix or the encoder is validated against 2.6.
- **F-21, F-22:** accepted; `/api/health` is read by the container health check and the release check.
- **F-23:** from the code; apt "regex" names (`a.`) can match several installed packages, `--only-upgrade --no-remove` limits the damage to upgrades.

## Routes

Every endpoint of the application (all `Map*` calls, 92 registrations, plus WebSockets and `/agent-files/`) is covered by `tests/JulOS.Backend.Tests/RouteSecurityTests.cs`. The test reads the routes from the running host (`EndpointDataSource`) and calls each one **without a session but with everything a browser sends** (`X-JulOS`, own `Origin`):

| Route | Without session | Why |
| --- | --- | --- |
| `GET /api/health` | 200 `{ok, db, version}` | container and release checks |
| `GET /api/setup/status`, `POST /api/setup/begin\|finish` | answers | first setup only; needs the setup code (60 bits, fixed-time compare, closed after the first account, 429 after failures); `/begin` and `/finish` answer 409 when an account exists |
| `GET /api/auth/me`, `POST /api/auth/login` | answers | session probe; login (rate limited, same answer for every failure) |
| `GET /agent-files/{name}` | answers | agent sources and `SHA256SUMS` (fixed allow-list, no secret) |
| `POST /api/agent-link/enroll`, `/api/agent-link` (WebSocket) | check their own code/token | the agent has no cookie (limits above) |
| everything else (incl. `/api/remote/screen`, `/api/remote/ssh`, `/api/remote/clip/{ticket}`, all `/api/hosts/...`, host actions, files, modules, tools, settings, `/api/upload*`, `/api/log`) | **401** | |

The test fails when a new route answers anything but 401 without being added to the explicit allow list (with its reason), and when an allow-list entry no longer exists. Further tests in the same class:
unknown `/api/...` paths and methods are 401 (never 404, nothing to enumerate), `OPTIONS`/`TRACE` get no CORS header; **every** state-changing route answers 403 with a valid session but without `X-JulOS`, without `Origin`, with a foreign origin and with `localhost.evil…`; a WebSocket upgrade with valid session **and valid one-time ticket** is refused for a foreign or missing origin and accepted for the right one (so the refusal is the origin check); security headers on `/`, `/api/*`, `/agent-files/*`, static files and 404s.

Also checked (by code or test): ownership: every store query filters by `user_id` (connections, folders, hosts, agents, settings, services, audit; moving a connection into a folder checks both); hosts and agents are found by host id **and** user; SQL is parameterised everywhere (interpolated SQL contains only constants); API bodies 64 KiB (login and setup 4 KiB, uploads their own limits); tickets 128 bit, 30 s, single use, bound to the user; path parameters with `:guid`; file access in `FileBrowser` (fixed in 0.2.0, tests still pass). CORS: no `Access-Control-*` header exists anywhere. `Host` is not restricted but nothing in the output depends on it except the CSRF comparison (a forged `Host` only affects the attacker's own request).

## Authentication and session (checked against the code)

- **Password hash: current.** Argon2id (`Konscious` 1.3.1), 64 MiB, 3 iterations, parallelism 1, 16-byte random salt, 32-byte output, NFKC-normalised input, 12 to 256 characters. OWASP's minimum is 19 MiB / 2 / 1 (or 46 MiB / 1 / 1); RFC 9106's second recommended profile is 64 MiB / 3 / 4. JulOS is above the OWASP minimum and on the RFC profile apart from parallelism (1 on purpose: one lane per check, three checks at a time). Parameters live in the hash string, comparison is `FixedTimeEquals`, a hash with weaker parameters is rewritten at the next login (`needsRehash`), unknown and unconfirmed users take the same path with a dummy hash (same cost, same text). To raise the cost later, change the constants: old hashes upgrade themselves.
- **TOTP:** RFC 6238, SHA-1, 6 digits, ±1 step, all three steps always computed, fixed-time compare, a step is burned atomically in SQL (`update … where totp_last_step < $2`) so one code works once even for parallel requests; evaluated only after the password is right; failures count against address and account. Secret in plain text in the database (F-10).
- **Recovery codes:** 10 × 80 bit, only SHA-256 stored (enough for 80 bits of entropy), consumed with one `delete`; used codes cannot return.
- **Session token:** 256 bit from `RandomNumberGenerator`, only SHA-256 in the database, `__Host-julos_sid` over HTTPS (also behind the proxy through `X-Forwarded-Proto`), `Secure; HttpOnly; SameSite=Strict; Path=/`, no `Domain`; new token on every login (no fixation); rotation every 12 h with a 10-minute grace for the old value; idle 30 days, absolute 180 days (long for an internet target: F-04). Logout and "other devices" delete rows; password or authenticator change ends the other devices; open relays check the stable session id every 15 s.
- **Lockout:** per address (IPv4, IPv6 as /64) and per account, growing wait up to 15 min, at most three password checks at once per address, `Retry-After` set. Failed attempts are logged with name and address (never the password). The lockout counts for unknown names too, so it does not reveal accounts. Client address behind the proxy: only from `JULOS_TRUSTED_PROXIES` (F-03).
- **User enumeration:** same status and text for unknown user, wrong password, wrong code; same Argon2 work. Not re-measured in this pass (0.2.0 measured it); no code path differs.
- **Setup:** after the first confirmed account `/api/setup/begin|finish` answer 409; before that they need the setup code from the server log.
- **CSRF:** `X-JulOS: 1` plus `Origin` equal to the host for every non-GET `/api` route (tested for all routes), `Sec-Fetch-Site: cross-site` refused for the clip download, WebSocket upgrades check the origin in the endpoint (tested).

## Agents, host actions, root helper

- **Enrolment:** code 50 bit (`XXXXX-XXXXX`), SHA-256 stored, 10 minutes, consumed by `delete … returning` (single use even in parallel), a new code replaces the old one, 10 tries a minute per address and 60 overall (F-12). Token `jat_` + 256 bit, SHA-256 stored, revocation (`DELETE /api/hosts/{id}/agent`) is immediate and cuts the link. Token only in `Authorization`, never in a URL. The agent refuses plain `http://` unless `--allow-http`.
- **Link:** hello within 10 s, idle 75 s, text messages ≤ 1 MB (reply ≤ 600 kB), frames ≤ 64 kB, ≤ 32 requests in flight, answers matched by id, fields read one by one with bounds; free text from an agent is reduced to printable characters and short lengths (`AgentLabels`) before it is stored or shown. A hostile agent cannot make the server request anything: the server only sends fixed paths with checked values. It can lie about its state, and (F-07) could once try to steer the browser; fixed. No rate limit on text messages from an agent (Info: CPU only).
- **Host actions:** confirmation token 256 bit (`RandomNumberGenerator`), only the SHA-256 kept in memory, 60 s, single use, bound to user, **session id**, host, action and target (tested in `HostActionTests`); the typed machine name is checked on the server; every check is repeated at run time; targets must be in the lists the agent reported; one writing action per host; pause switch (environment wins), level per host (`agents.control`, default `read`), local words in the permission file; audit written **before** the action is sent and the action is refused when the audit cannot be written; no secrets or output in the audit. Rate limit 30 a minute per user, 20 open confirmations.
- **Not covered by a second factor:** see F-04. **Installer** (`tools/install-host-agent.sh`): TLS required unless `--allow-http`, downloads with `--max-redirs 0 --max-filesize`, SHA-256 against `SHA256SUMS` (F-19), permission file root-owned and empty by default, `--docker-group` is explicit and warned, systemd units with `NoNewPrivileges`, `ProtectSystem=strict`, `CapabilityBoundingSet=` empty, helper installed only when a root word is allowed. Not verified on a real Proxmox/Debian host in this pass (the installer and helper have dry-run and fake-runner tests).

## Stored secrets

- **Connection passwords:** AES-256-GCM, fresh random 12-byte nonce per encryption (no reuse), connection id as associated data, key length checked, plaintext zeroed; `secret.key` is created `0600` atomically; changing host/port/kind/user deletes the stored password; no API returns a password. **No key rotation exists** (a lost or leaked key means re-entering all passwords by hand; recommendation below).
- **SSH host keys / RDP certificates:** trust on first use, a change is refused until the exact new fingerprint is confirmed, the check runs before credentials are sent. The backup SFTP target pins its host key as well (`JULOS_BACKUP_SFTP_HOST_KEY` or a file written at first use).
- **Logs, audit, errors:** no password, token, cookie, TOTP code or file content is logged; texts to the browser are fixed German texts or short bounded codes; the setup code is logged on purpose. Core dumps: the runtime writes none unless `DOTNET_DbgEnableMiniDump` is set; the container sets no such variable (host `core_pattern` not checked).
- **Backups:** F-01, F-16.

## Web client

An independent line-by-line pass over all JavaScript (all ~75 `innerHTML` and attribute sinks, `el()` helper, template literals, `t()` interpolation) found **no XSS or HTML injection**: every server, agent or user value goes through `esc`/`ctx.esc` (escapes `& < > " '`) or `textContent`; the helper outputs of `hostinfo.js`/`hostactions.js` were run with `"><img src=x onerror=…>` in every field (package, unit, container, image, host name, OS, address, audit, job): 0 unescaped hits. The only finding is F-07 (data from an agent reaching a request path). CSP: `script-src 'self'` without `unsafe-inline`/`eval`, `frame-ancestors 'none'`, `object-src 'none'`, `base-uri 'none'`; there is no inline script or handler, so the CSP is effective; the page sets `Permissions-Policy` (microphone only for itself, since the sound feature) and `COOP/CORP same-origin`. No `postMessage`/`message` listener exists (pop-out windows use `window.opener` of the same origin), all `window.open`/`target=_blank` use `noopener`. Service worker: caches only same-origin GET outside `/api/` (app files and the public `/agent-files/`), never authenticated data. `localStorage` holds layout, preferences and the activity log (connection names and short error texts; not cleared at logout, not secrets), no token or password. xterm.js 6.0.0 has no link or OSC 52 clipboard add-on; the terminal title is not used; a web link is opened only on an explicit user action. Not run in a real browser in this pass (code and node tests only).

## Container and delivery

Dockerfile: multi-stage, runtime `aspnet:10.0` (Ubuntu 24.04), `USER $APP_UID` (non-root), `/data` and `/backup` owned by that user, no build secrets (only commit and date as build arguments), nothing but the published output and the agent files copied. Compose: PostgreSQL has no published port, the server is published on `127.0.0.1` unless `BIND_IP` is set, the database password is mandatory, `no-new-privileges` and `cap_drop: ALL` on the server. Gaps: F-13. Workflows: F-14. Image publication: tags `latest`, `main` (every push to main) and `X.Y.Z` (tag must equal `<Version>`); the live stack should name a version tag, not `latest`.

## Supply chain: are hashes and dependencies current?

**Password hashing: yes** (Argon2id 64 MiB / 3 / 1, above the OWASP minimum, rehash on login when the parameters rise). **Dependencies: yes** as of 2026-11-19: `dotnet list package --vulnerable --include-transitive` is empty for all six projects; the NuGet advisory feed does not know every CVE, so the table below also lists a manual search.

| Component | Version | Current? | Known CVEs |
| --- | --- | --- | --- |
| .NET runtime / ASP.NET Core (image, dev image checked) | 10.0.15 (Nov 2026 servicing, 10.0 is the supported line) | yes (10.0.15 is the latest; the image must be rebuilt after each servicing release) | none open against 10.0.15; the earlier ones (Brotli, ASP.NET) are fixed in it. The **live image was not inspected**: its patch level depends on its build date |
| .NET SDK (build image) | 10.0.x floating tag | yes (rebuilt at every release) | none (build time only) |
| Npgsql | 10.0.3 | yes (latest) | none known for 10.x (CVE-2024-32655 fixed in 8.0.3) |
| SSH.NET | 2026.0.1 | yes (latest) | CVE-2026-48798 and CVE-2026-85756 (both `ScpClient` only, fixed in 2026.0.0); JulOS uses `SshClient`/`SftpClient`, never `ScpClient` (grep: no use); Terrapin (CVE-2023-48795) was fixed in 2023 |
| Konscious.Security.Cryptography.Argon2 / Blake2 | 1.3.1 / 1.1.1 | latest on NuGet | none known |
| BouncyCastle.Cryptography (transitive via SSH.NET) | 2.7.0 | yes (latest) | the 2026 advisories (IES padding oracle, PKCS#12, ASN.1 recursion, …) are fixed in 2.7.0 |
| Test packages (xunit.v3 4.0.3, Mvc.Testing 10.0.15) | | Mvc.Testing 11.0.0 exists (.NET 11); not shipped | none |
| `Microsoft.Extensions.*` etc. (framework part) | 10.0.15 | follows the runtime | none |
| xterm.js + fit, unicode11, webgl add-ons (vendored) | 6.0.0 | yes at the time of vendoring; no Dependabot for vendored files (check on each release) | none known (only CVE-2019-0542, fixed in 3.x) |
| Own JavaScript | no third-party code besides xterm.js, no CDN | | |
| libopenh264-7 (image) | 2.4.1+dfsg-1 | upstream is 2.6.x; Ubuntu 24.04 stays on 2.4.1 | CVE-2025-27091 (decoder); unfixed in noble; **not reachable** (encoder only) — F-20 |
| intel-media-va-driver / libva (image) | 24.1.0 / 2.20.0 (Ubuntu 24.04 updates) | current for noble | none open known; only loaded with a GPU device |
| libssl3 / openssl, zlib, libicu, gnutls (image) | 3.0.13-0ubuntu3.16, 1.3, 74.2, 3.8.3 | current for noble at build time | patched Ubuntu builds |
| Python agent (stdlib only) | needs Python ≥ 3.8 (installer checks) | 3.8 is end of life upstream; Debian/Ubuntu ship patched 3.11+/3.12 | depends on the distribution; the agent uses `ssl.create_default_context`, `http.client`, `subprocess` without a shell; recommend Python ≥ 3.10 on hosts |
| PostgreSQL | 18 (tag `postgres:18`, floating minor) | current | the 2026 server advisories need the latest 18.x: **pull and recreate regularly** |
| Caddy (user's, outside the repo) | not seen | not verified | — |

**Permanent process (built):** `.github/dependabot.yml` (NuGet, GitHub Actions, Docker, Docker Compose; weekly; security updates are immediate anyway), `.github/workflows/supply-chain.yml` (weekly `dotnet list package --vulnerable --include-transitive`, fails on a hit, outdated list in the summary), the existing `vulnerabilities` job on every push. **Process (RELEASING.md "Updates and security"):** rebuild and roll out the image at least monthly and after every .NET servicing release (second Tuesday of the month), even without code changes, because the base image is where the fixes arrive; `docker compose pull` for PostgreSQL; re-run `apt` patches by rebuilding.

## Recommendations for the internet target

In order of value for effort:

| # | Measure | Effort | Why |
| --- | --- | --- | --- |
| 1 | `JULOS_TRUSTED_PROXIES=<Caddy>/32` and firewall: only Caddy reaches port 8080 (F-03) | S, 10 min | makes address lockout and logs real |
| 2 | `JULOS_ALLOWED_HOSTS` with the hosts you connect to (F-02) | S | a stolen session cannot scan or reach internals |
| 3 | Backup: second target readable only by you, or `JULOS_BACKUP_KEY=off`; decide on passphrase encryption (F-01) | S now, M for encryption | the backup is the biggest single leak |
| 4 | Client certificate (mTLS) at Caddy with the agent exception: `caddy/Caddyfile.internet.example` | M (CA, one certificate per device, install on phone; a lost phone means revoking by replacing the CA or listing the certificate) | strangers cannot even reach the login; removes the lockout DoS and most scanning |
| 5 | Step-up TOTP for host actions, agent codes and account changes (F-04); idle timeout 7 days | M | limits a stolen cookie |
| 6 | Passkeys (WebAuthn) as an additional or replacing factor | L (server: credential table, registration/assertion endpoints, `Fido2` library or own CBOR/COSE code against ARCHITECTURE principle 3; client: ceremony UI; recovery path) | the only measure that stops live phishing of password + TOTP; also removes password typing on the phone |
| 7 | Rate limit and ban at the proxy: fail2ban or CrowdSec on Caddy's JSON access log (`POST /api/auth/login` with 401), or the `caddy-ratelimit` plugin | M | botnets can saturate the three Argon2 slots (0.2.0 #6) |
| 8 | Login notification and session reuse detection (F-18): push or mail on a new device, revoke a session when a rotated cookie is presented after its grace; name devices | M | early warning for a stolen cookie |
| 9 | Encrypt `totp_secret` (F-10) and add a key rotation command (`JulOS.dll key rotate`: decrypt with the old, encrypt with a new key, in one transaction) | M | rotation does not exist today |
| 10 | Pin base images by digest with Dependabot; PostgreSQL hardening and limits (F-13) | S | reproducible builds, contained damage |
| 11 | Signed agent releases or a pinned server certificate in the agent (F-05) | L | removes the server as single root of trust for the hosts |
| 12 | IP restriction in Caddy (`remote_ip`) when you only sign in from known networks | S, but phones roam | only if it fits how you use it; mTLS is the better filter |

## Operations: what ends what

| Situation | What works today | Gap |
| --- | --- | --- |
| **Cookie stolen** (or device lost) | From another signed-in device: Profil → "Andere Geräte abmelden" (`POST /api/auth/logout-others`) ends every other session; open desktops/terminals close within 15 s. Changing the password does the same. From nothing: `tools/julos-user.sh logout NAME` (ends every session of that user) or `passwd NAME` (also ends them), then a recovery code to get a session first. | No notice that a new device signed in (F-18); a stolen cookie is valid until you notice, up to 30 days idle. After a restore from backup old sessions return (F-16). |
| **Phone lost** | Sign in from another device with password + recovery code, then Profil → new authenticator (needs a session and the password; ends other sessions and the old recovery codes). With mTLS: the certificate on the phone stays valid until the CA or the certificate is replaced (no revocation list in Caddy by default). | Without a second device the console tool is the only way. Passkeys would give a per-device revocation. |
| **Host compromised** | Host dialog → "Agent entfernen": the token dies at once and the link is cut; the host's agent files stay on the host (uninstall script on the host). Stored connection passwords for that host remain valid **on the host**: change them there. SSH host key and RDP certificate pins warn when the host is replaced. A compromised host cannot reach other hosts through JulOS (the agent link is outbound-only and the server asks only fixed questions). | The terminal passwords you typed or stored for that host are known to the attacker: rotate them. |
| **Server (container) compromised** | Everything stored there is exposed: stored passwords (with `secret.key` in `/data`), TOTP secrets, agent tokens can be used to push updates (F-05). Rotate: the login password, TOTP, every stored target password, remove and re-enrol all agents, new `secret.key` (re-enter passwords), new database password. | No rotation tooling for `secret.key`/TOTP (recommendation 9). |
| **Key `secret.key` lost** | Stored passwords unreadable (409 on connect, shown as such); re-enter them. | Back it up apart from the database (F-01). |

## After the merge: reviews that are still due

- **`dateien-sftp`** (Files over SFTP for all hosts; new routes under `Files/` and `Integrations/Ssh`): **reviewed on 2026-11-20, see "Files over SFTP" below** (findings F-24 to F-32). The list of what that review had to cover: path handling on the remote side (traversal, symlinks, `..`), the SFTP host key policy (trust on first use as for terminals), the use of stored passwords, download/upload limits and headers (`Content-Disposition`, `nosniff`, always attachment), ownership checks per host, rate limits, logs (no file names or contents of other users' files), and a new entry in the allow list of `RouteSecurityTests` only if it must be public (it must not). The route test will fail for any new route that answers without a session, which is the first safety net.
- **`host-ui`** (Host view, interface only): review for new HTML sinks fed by agent data when merged (`tests/frontend` helpers + the `esc` rule).

## Files over SFTP (review of the branch, 2026-11-20)

Reviewed on `claude/sicherheit-sftp` (the SFTP branch plus 0.13.2): `src/JulOS.Frontend/Files/*`, `src/JulOS.Integrations/Files/*`, `database/019`, `wwwroot/files-*.js`, `docs/FILES.md`, with the route test, new tests and the existing `RealSftpFilesTests` against the throwaway SSH target of the dev stack (never anything else).

**Fixed before the merge**
- **F-24 (High):** `SshNetFileSession.ListAsync` removed `.`, `..` and control characters but not names containing `/`. A compromised host can list an entry `../../.ssh/authorized_keys`; `FileTransfer` built `Join(dest, name)` without checking and sent it to the other host (copy and move), and used it for recursive delete on the source. Attack path: owner copies a folder from host A (compromised) to host B with writing switched on, A's listing carries the traversal name, B receives a file outside the target folder with B's SSH rights. Fix: `RemotePath.SafeSegment` (no `/`, not `.`/`..`, no control character, not empty) hides such names in the session and is applied again in `FileTransfer` (count, copy, delete); a hidden name counts as skipped, a folder holding one stays "not empty". Tests (fail without the fix): `HostileTargetTests` (copy with three hostile names, delete and count, size lie).
- **F-27 (Low):** the total number of bytes actually received during one copy is capped by `JULOS_FILES_MAX_MB` (before, only each file and the announced sizes), so a target that lies about sizes cannot fill the other host's disk through JulOS. Temporary files are removed.

**Checked and fine**
- **Session and CSRF:** all 15 new routes sit behind `ApiGuard` (route test: 401 without session, 403 without header or with a foreign origin, with a valid session). Upload is the only large body (`LargeBodyPaths`), its limit is set per request; every JSON route has 64 KiB.
- **Ownership:** every endpoint resolves the connection with `ConnectionStore.GetAsync(userId, id)`; for copy the target connection too; jobs are found by user (`FileJobs.Get`), the pool key is (user, connection); `HostPolicy` (`JULOS_ALLOWED_HOSTS`) applies to every request, SSH only (`not_ssh`).
- **Host key:** the callback runs before any credential is sent; first and changed keys need the exact fingerprint in `acceptHostKey` (`HostKeyPolicy.Decide`); stored in the same column as the terminal; stricter than the terminal. Passwords: typed one is used for that connection attempt only, the pool keeps sessions, never the password; nothing logs a password, exception text or file content (`FileErrors.Code` maps exceptions to fixed codes; the audit holds codes and shortened paths); the page keeps a typed password in a local variable only (localStorage holds only the chosen place).
- **Paths:** `RemotePath.Normalize` (absolute, no `..`, no control characters, 4096 characters, 128 parts) for every path from the client; new names `ValidName`; `/` is never deleted, folders need an explicit `recursive`, overwrite needs `overwrite`; links are never followed in recursion, never replaced, deleted as links through the listing handle.
- **Limits and streaming:** list 2000, copy/delete 10 000 items and 20 levels counted before anything changes, a file and an operation `JULOS_FILES_MAX_MB`, 128 KiB buffers, at most 3 streams and 2 jobs per user, 4 sessions per user, rate limits per user and minute; upload through a temporary name, size check, rename; cancel and every failure remove the temporary file (tests with a cancelled and a broken target). Text view 1 MiB, binary refused, JSON value set with `textContent`.
- **Downloads:** `attachment` with a sanitised name, `application/octet-stream`, `nosniff` (global), `Cache-Control: no-store`, an incomplete transfer aborts the connection instead of looking complete.
- **Client:** the new Files pages (`files-app.js`, `files-core.js`) contain no `innerHTML`; names, paths and text are set with `textContent`; the only link is the download. The service worker never caches `/api/`.
- **Resource leaks:** leases are released in `finally`, rejected jobs release them, the pool sweeper closes idle sessions every 10 s (tests in `SftpPoolTests`).

**Open**
- **F-25 (Medium, decision):** writes are on only with `JULOS_FILES_WRITE=on` (default off, a typo stays off), which is the real guard; the `confirm` flag in the request only stops accidental API calls, a stolen session sends it too. With writing on, a stolen session has what the saved SSH credentials have on every host (no more than the terminal gives it, but with an upload and delete API). Suggested (M): the same two steps as host actions: `plan` returns a single-use token (SHA-256 in memory, 60 s, bound to session, connection, action, paths, target, `overwrite`, `recursive`), the write needs it; plus the step-up TOTP of F-04 for delete, overwrite and move. Keep `JULOS_FILES_WRITE=off` on an internet-facing server until then, unless you need writing.
- **F-26 (Low):** write a `started` entry first and refuse the write if it cannot be stored (as `HostActionService` does); effort S to M. Today a failed audit write is only logged.
- **F-28 (Low, accepted):** limit of a path-based protocol; the effect cannot exceed the SSH user's own rights. Do not run a recursive delete or copy in a folder other users can write to (for example `/tmp`) if they are not trusted.
- **F-29 (Low):** close the pool entry when its connection is edited or deleted (`ConnectionEndpoints`) and when the session ends; effort S.
- Root access for files is a later step (FILES.md "Not built"): it needs its own review (a `files-root` word in the permission file, confirmation, history).

## Not verified here

- The live setup: Caddy configuration and version, firewall, the values of `JULOS_ALLOWED_HOSTS` / `JULOS_TRUSTED_PROXIES`, tag and branch protection on GitHub, the patch level of the running image, the host `core_pattern`. No scan, no login attempt, no load against the live address.
- A real browser and a real phone (the client was reviewed by reading and node tests); a real Debian/Proxmox host with systemd for the root helper and installer (tests use fakes and dry runs); real Docker behaviour of the host actions.
- Timing of the login for existing and unknown users was not measured again (code paths are identical; 0.2.0 measured within noise).
- The decoders of the own RDP/VNC engine against hostile targets were not fuzzed (`BitmapRobustnessTests` exist); the vendored xterm.js and the compiled libraries were not audited beyond their versions; the Caddy example was validated and exercised against a throwaway Caddy only.
- Fuzzing, a penetration test by a third party and a review of the Windows/Linux target machines are outside this review.

## What was checked and found fine in 0.2.0 and still holds

Passwords and TOTP step burning, recovery codes, session store and rotation, CSRF/origin checks, ticket handling, WebSocket message limits and protocol parsers, parameterised SQL, file browser (traversal, symlinks, FIFO, download headers), agent proxy with fixed paths, `/api/log` allow-list, AES-GCM box, SSH host key policy. Items of 0.2.0 that changed: #2 (RDP certificates) is fixed by trust on first use (migration 006, `CertificateTrust`); #17 (microphone) is fixed (`microphone=(self)`); #5, #6, #9–#12, #14, #15 stay fixed; #1, #3, #7, #13, #16, #18 are carried as F-02, F-03, F-11, F-15, F-13, F-21.
