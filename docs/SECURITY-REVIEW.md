# Security review (version 0.2.0, 2026-11-19)

One pass over the whole server and client surface: authentication and sessions, every route, outbound reach, secrets, the web client,
supply chain and deployment. Everything below was checked in the code; where it could be run it was run against a real Kestrel instance
with a throwaway account (own database schema, no Docker, removed afterwards) and with the test suite (`deploy/dev/test.sh`, 212 tests, all passing).
"Fixed" means committed with this review; "Open" items are written down with a suggested fix and were **not** changed because they
need a decision, are larger than a local change, or could not be verified here.

Note added later: this review is a snapshot of version 0.2.0. It covers the Guacamole path (guacd, its relay, the vendored JavaScript client), which was removed afterwards; the own display engine is now the only way, so findings and file names that mention Guacamole or guacd describe code that no longer exists and are kept as the record of what was checked.

No finding lets an anonymous visitor do anything beyond the public routes. Every finding that is exploitable at all needs a valid session.
The server is a single-owner tool, so a session already is the strongest credential; the open items are mostly about what a stolen
session or a compromised target machine can reach.

## Findings by severity

| # | Severity | Finding | Status |
| --- | --- | --- | --- |
| 1 | Medium | Saved connections may point at any host the server reaches (internal services, metadata address, port scan through error messages) | Open (config exists: `JULOS_ALLOWED_HOSTS`) |
| 2 | Medium | Certificates of RDP targets are never checked (both engines), no pinning | Open |
| 3 | Medium | `JULOS_TRUSTED_PROXIES` defaults to every private network | Open (documentation) |
| 4 | Medium | A named pipe in the files folder blocked a thread per download request | **Fixed** |
| 5 | Medium | Failed-login counters used `MapToIPv4()`: one IPv6 connection could rotate through counters, different IPv6 networks shared one | **Fixed** |
| 6 | Medium | Unlimited parallel login requests: the lockout only counts after the 64 MiB Argon2 check | **Fixed** |
| 7 | Low | Account lockout can be used to keep the owner out (up to 15 minutes per request) | Open (accepted trade-off) |
| 8 | Low | `/data` is not writable with the Compose files as shipped, so `secret.key` is never created; README says otherwise | **Fixed** (Compose: volume `julos-data` + `julos-data-init`) |
| 9 | Low | API bodies were limited only by Kestrel's 30 MB default | **Fixed** (64 KiB) |
| 10 | Low | Uploads: up to 20 per minute at 64 MB each held in memory at once; `files/` could fill the disk for 7 days | **Fixed** (4 at a time, 2 GiB quota) |
| 11 | Low | File list and search had no rate limit (up to 50 000 names per search) | **Fixed** |
| 12 | Low | RDP target could make the server allocate a 1 GiB frame buffer (16384 x 16384) | **Fixed** (40 million pixels) |
| 13 | Low | Clipboard transfers allocate the announced size up front (up to 64 MB per session) | Open |
| 14 | Low | Agent icons were put into HTML text (`app.js`) | **Fixed** (DOM image after base64 check) |
| 15 | Low | Guacamole instruction encoder miscounted unpaired UTF-16 surrogates | **Fixed** (not reachable today) |
| 16 | Low | Compose files: no `no-new-privileges`, no `cap_drop`, floating image tags, `host.docker.internal` for the server and guacd | Partly fixed (`no-new-privileges`, `cap_drop`, health check; read-only root and digests open) |
| 17 | Low | `Permissions-Policy: microphone=()` will block the planned microphone feature | Open (coordinate with that work) |
| 18 | Info | `/api/health` is public and names the version | Accepted (release checks use it) |

### 1. Outbound reach of saved connections (Medium, open)

- **Where:** `src/JulOS.Backend/Logic/HostPolicy.cs:12` (empty list allows everything), `src/JulOS.Backend/Logic/ConnectionRules.cs` (`HostPattern` only checks characters), `src/JulOS.Frontend/Remote/RemoteEndpoints.cs` (ticket), `src/JulOS.Integrations/Ssh/SshRelay.cs`, `src/JulOS.Integrations/Display/Rdp/RdpClient.cs:300-324`.
- **Proof:** on the throwaway instance `POST /api/connections` accepted hosts `169.254.169.254`, `postgres`, `localhost` and `127.0.0.1` with port 5432 (HTTP 200 each). The relays then connect from inside the container network, which also contains the PostgreSQL and guacd containers.
- **Impact:** a holder of a session can use the server as a TCP client toward anything it can reach: the database, guacd (4822), the host (`host.docker.internal` is configured for the server and guacd), cloud metadata, other LAN hosts. The outcome is observable: SSH answers distinguish "unreachable", "timeout", "not SSH", "login refused", "host key" (`SshRelay.FailureMessage`), the RDP client distinguishes "host name could not be resolved", "could not be reached", "did not answer like an RDP server" (`RdpClient.cs:300-324`), and guacd error texts are passed to the browser (`GuacRelay.cs:61`). That is a port scanner for someone with a session. No data from those services is returned (the protocols do not match), so this is reconnaissance, not a read primitive.
- **Why open:** the legitimate target is usually the machine next to the server (`host.docker.internal`, a LAN address), so a built-in deny list cannot be complete; the restriction belongs in configuration.
- **Suggested fix:** document `JULOS_ALLOWED_HOSTS` prominently in the README and the Compose files (it exists), ship it filled in the live setup (the VM's name or address). Optionally: refuse link-local (`169.254.0.0/16`, `fe80::/10`) and the Compose service names `postgres`/`guacd` always; resolve names once and connect to the checked address (stops DNS tricks when an allow-list holds names).

### 2. RDP certificates are not verified (Medium, open)

- **Where:** `src/JulOS.Integrations/Display/Rdp/RdpOptions.cs:26` (`IgnoreCertificate = true`, used at `RdpClient.Connection.cs:68`), and the former Guacamole engine's `GuacSpec` (`ignore-cert=true`, removed with that engine).
- **Impact:** someone who can sit between the server and the RDP target (same LAN, ARP/DNS tricks) can read the session and, because NLA is bound to the presented key only, capture the NTLM exchange with the typed password. RDP targets almost always have self-signed certificates, which is why this is the default; SSH has trust on first use, RDP has nothing.
- **Suggested fix:** trust on first use for RDP as for SSH: store the SHA-256 of the certificate per connection (`connections.cert_hash`), accept silently when equal, ask in the UI when it changes. Needs a migration, both engines (guacd has `ignore-cert` and `cert-fingerprints`), and UI, so not done here.

### 3. Trusted proxies default (Medium, open)

- **Where:** `src/JulOS.Frontend/Program.cs:84`: `X-Forwarded-For/Proto/Host` are believed from every loopback and private address; the Compose files can publish port 8080 on a LAN address (`BIND_IP`).
- **Impact:** any host on the LAN can talk to the published port directly and set `X-Forwarded-For`, so the per-address login lockout is bypassed (the per-account lockout still holds), the address shown in the session list is fake, and `X-Forwarded-Proto: https` changes the cookie name and `Secure` flag. The CSRF check compares `Origin` with the forwarded host, but a browser cannot set those headers, so cross-site attacks are not helped. Direct requests from the internet are not "from a private address" and are not trusted.
- **Suggested fix:** set `JULOS_TRUSTED_PROXIES` to the Caddy host only (for example `192.0.2.10/32`) and say so in the Caddyfile examples and the README (the variable is already in both Compose files). Optionally refuse to start with the default when `BIND_IP` is not loopback.

### 4. Named pipe in the files folder (Medium, **fixed**)

- **Where:** `src/JulOS.Backend/Logic/FileBrowser.cs` (`OpenRead`).
- **Proof:** with a FIFO in the root, `GET /api/files/download?path=pipe` did not answer within 6 s (the request thread sits in `open(2)` until a writer appears). Several such requests starve the thread pool. The folder is shared with the target machine's user, so anything that user (or a program in the VM) creates is there.
- **Fix:** the file is opened with `O_NONBLOCK` and refused unless the stream is seekable. After the fix the same request answers 404 in 40 ms and ordinary downloads work (checked on the real server and in `FileBrowserTests`). A pipe is still listed (the .NET file API cannot tell the type); it cannot be opened.

### 5. IPv6 and the login counters (Medium, **fixed**)

- **Where:** `src/JulOS.Frontend/Auth/AuthEndpoints.cs` (`Ip`), now `src/JulOS.Backend/Logic/ClientAddress.cs`.
- **Proof:** `IPAddress.MapToIPv4()` on a real IPv6 address returns an IPv4 address built from its last 32 bits. Ten requests from `2001:db8:7:7:dead:beef:<n>:1` therefore counted as ten different addresses. After the fix they share the key `2001:db8:7:7::/64` (all but three were answered 429 in the concurrency test, see 6).
- **Impact:** a client with IPv6 (behind Caddy this is the normal case) could rotate through addresses of its own /64 and never reach the per-address limit; unrelated IPv6 networks could share one counter and lock each other out. The per-account lockout was not affected.
- **Fix:** IPv4 stays the address, IPv6 counts as its /64 prefix. The session list now shows that prefix for IPv6 devices.

### 6. Parallel logins (Medium, **fixed**)

- **Where:** `src/JulOS.Backend/Services/AuthService.cs` (`LoginAsync`), `LoginLimiter.TryBegin/End`.
- **Reasoning and proof:** failures are counted after the password check (64 MiB, three iterations, at most three at a time in `PasswordHasher`, the rest queue). A burst of N parallel requests all pass the lockout test before the first failure is recorded, so one address could queue thousands of Argon2 checks and keep the login slow for minutes. After the fix at most three checks per address run at once: of ten parallel requests three were checked and seven answered 429 with `retryAfter` 5 within 40 ms.
- **Not changed:** many addresses at once (a botnet) can still saturate the three slots; that needs a rate limit in front (Caddy plugin or firewall).

### 7. Lockout as denial of service (Low, open, accepted)

- **Where:** `AuthService.cs:103` (`limiter.Fail("user:" + name)`), `LoginLimiter.cs`.
- **Impact:** anyone who can reach the login can send five wrong logins for the known user name and lock the owner out for 30 s, doubling up to 15 minutes while the attacker keeps asking (one request per lock expiry). Sessions that already exist are not affected, and the lockout also applies to unknown user names, so it does not reveal whether a name exists.
- **Suggested fix if it ever matters:** do not count (or do not enforce) the account lockout for requests from an address that has signed in before (device cookie), or add the lockout only after a correct password. The mTLS option in the Caddyfile example removes the problem for the internet side.

### 8. `/data` and `secret.key` with the shipped Compose files (Low, **fixed**)

- **Fix:** both Compose files mount the named volume `julos-data` at `/data`; `julos-data-init` (runs once, only `CHOWN`) gives it to `JULOS_UID`, the server mounts it `nocopy`. Checked on a throwaway container: `secret.key` is created, mode 0600, owned by the server user. Details in DEVELOPMENT.md "Container hardening and health". The original finding follows.

- **Where:** `Dockerfile` (`mkdir /data && chown $APP_UID /data`), `docker-compose.yml` / `docker-compose.image.yml` (`user: ${JULOS_UID:-1000}`, no `/data` volume), `README.md:57` (says the image's Compose stacks have a volume at `/data`).
- **Proof:** `docker run --user 1000:1000 ... touch /data/x` on the current image: "Permission denied" (the directory belongs to the image's `app` user).
- **Impact:** without `JULOS_SECRET_KEY` the server cannot create `secret.key`, so storing passwords stays off (safe, but not what the README says). If an operator "fixes" this with a writable container layer instead of a volume, the key is lost with the container and every stored password becomes unreadable (409 "Das gespeicherte Passwort lässt sich nicht lesen"). The server now logs a warning at startup when no key is available.
- **Suggested fix:** a named volume or bind mount for `/data` owned by `JULOS_UID`, or an entrypoint that creates it; fix the README sentence either way. The key file must never live in the upload folder (it is the file browser's root). There is no key rotation; back the key up apart from the database.

### 9 to 12. Request and resource limits (Low, **fixed**)

- **API bodies:** only `/api/auth` and `/api/setup` had a body limit (4 KiB); everything else could carry 30 MB. `ApiGuard` now sets 64 KiB for every route except the two uploads. Checked on Kestrel: a 100 KB JSON body (also chunked, without `Content-Length`) is answered 413, a normal one 200. The in-memory test server has no body limit feature, so this has no unit test.
- **Uploads:** `AgentEndpoints.cs` reads the whole body into memory (14 MB photo, 64 MB file). With 20 per minute and no limit on parallel requests that is gigabytes; now at most four uploads run at once. `ClipboardFileUploader` kept files for 7 days with no size limit (20 x 64 MB a minute); `files/` is now capped at 2 GiB (older folders are removed when needed, otherwise HTTP 507). Tests: `ClipboardFileUploaderTests`.
- **Files list/search:** per user and minute 240 lists, 30 searches (`HttpTests.File_search_is_limited_per_user_and_minute`).
- **Frame buffer:** `RdpClient.Session.cs:233` allocated `width * height * 4` for what the *target* announces, up to 16384 x 16384 = 1 GiB. Now refused above 40 million pixels (about 8K, 160 MB). Existing encoder tests (largest 8704 x 1072) are unaffected.

### 13. Clipboard transfers allocate up front (Low, open)

`ClipboardBridge.cs:253` allocates `new byte[size]` for the size the browser announces (up to 64 MB per upload, plus the batch of up to 10 files, plus an image). With eight sessions per user that is roughly 1 GiB for a signed-in user who wants to; each upload has an idle timeout. Suggested fix: grow the buffer with the data actually received, or cap the sum over all sessions of a user.

### 14 and 15. Client and protocol hardening (Low, **fixed**)

- **Icons:** `app.js` built `<img src="data:image/png;base64,${icon}">` from the agent's answer (window and app icons). The agent is the user's own helper on the target, and the strict CSP (no inline script) would have stopped script execution, but attribute injection and HTML injection were possible if the agent answer were wrong or the socket were replaced. Icons are now created as DOM images after a base64 check (`iconImg`).
- **Guacamole encoder:** `GuacProtocol.CodePoints` did not count an unpaired low surrogate, UTF-8 writes one character for it, so guacd would read one character too few and take the rest of the value as instruction structure (injection of extra connect parameters such as `drive-path`). Proof: with the check disabled, three of four new test cases fail. **Not reachable today:** `System.Text.Json` refuses lone surrogates in the request (checked: `JsonException`), so every password and user name arrives well-formed. Fixed as defence in depth (`GuacProtocol.WellFormed`).

### 16. Compose and images (Low, partly fixed)

- **Done:** `no-new-privileges` and `cap_drop: ALL` on the server (it started, answered `/api/health` and created `secret.key` with both), a health check, and `read_only` as a commented option. **Open:** digests, the read-only root by default (full run not tried), `host.docker.internal` on guacd (guacd is gone since 0.5.0, so that part is moot).

`docker-compose*.yml`: the server container could run with `security_opt: [no-new-privileges:true]` and `cap_drop: [ALL]` (it binds an unprivileged port) and a read-only root file system with a tmpfs for `/tmp`; none of this was tried on a running stack here. Image tags (`postgres:18`, `guacamole/guacd:1.6.0`, `mcr.microsoft.com/dotnet/*:10.0`) float; pinning digests in the live stack avoids surprise updates but needs a bump routine. `host.docker.internal:host-gateway` is on the server (needed for the VM) and also on guacd (needed only if desktops are reached through the host). The workflow uses actions pinned to major tags, not commit hashes.

### 17. Permissions-Policy (Low, open)

`Program.cs:110` sends `microphone=()`, which blocks `getUserMedia` for the page. The microphone work in progress has to change this to `microphone=(self)` together with a user-gesture prompt.

### 18. Public health route (Info)

`/api/health` answers `{"ok", "db", "version"}` without a session (`Program.cs:136`). `docs/RELEASING.md` and the deploy checks read the version there. The version number helps someone choose exploits; an alternative is to drop it from the public answer and let the deploy check use `/api/about` with a session.

## What was checked and found fine

**Authentication and sessions**
- Passwords: Argon2id 64 MiB, 3 iterations, hash format versioned with rehash on login, 12 to 256 characters (`PasswordHasher.cs`, `PasswordPolicy.cs`). Unknown, unconfirmed and wrong users take the same code path (dummy hash), the answer text is the same (`LoginFailed`). The measured time of a wrong password for an existing and a non-existing account was within noise (four samples each on a loaded machine; not a statistical proof, but there is no code difference).
- TOTP: RFC 6238 vectors in tests, all three time steps always computed, comparison fixed-time, a step is consumed atomically in SQL (`update ... where totp_last_step < $2`), so a code works once even for two parallel requests. The second factor is only evaluated after the password is right; failures count against both the address and the account.
- Recovery codes: 80 bits, only SHA-256 stored, consumed with one `delete ... where code_hash`, 16-character length decides between TOTP and recovery code.
- Sessions: 256-bit random cookie, stored as SHA-256, idle 30 days, absolute 180 days, rotation every 12 hours with a 10-minute grace for the old value, a new session on every login (no fixation), logout and "other devices" delete rows, open relays check the stable session id every minute (`IsSessionAliveAsync`).
- Cookie: `HttpOnly`, `SameSite=Strict`, `Path=/`, no `Domain`, `Secure` and the `__Host-` prefix when the request is HTTPS (also behind the proxy via `X-Forwarded-Proto`; covered by `HttpTests.Behind_an_https_proxy_...`). Over HTTP the same cookie is not accepted.
- CSRF: every non-GET route needs `X-JulOS: 1` and an `Origin` equal to the host (after forwarding); a missing or foreign origin is 403 (checked on Kestrel: evil origin 403, no origin 403). WebSocket upgrades check the origin again in each endpoint and the session before that. `OPTIONS` answers 405 and no CORS header is ever sent. DNS rebinding does not help an attacker because the cookie belongs to the real host name.
- Setup: one-time code (60 bits, fixed-time compare, closed after the first account, limited per address), the first account needs a confirmed TOTP.

**Routes** (all `app.Map*` calls: `Program.cs`, `AuthEndpoints`, `RemoteEndpoints`, `ConnectionEndpoints`, `AgentEndpoints`, `SystemEndpoints`, `FileEndpoints`, `AboutEndpoints`): public are only `/api/health`, `/api/auth/me`, `/api/auth/login`, `/api/setup/*` (`ApiGuard.PublicPaths`), everything else under `/api` needs a session before the endpoint runs, and an unknown `/api/...` path is 401, not 404. Static files are public by design (installable app) and contain no data.
- Tickets: 128-bit random, 30 s, single use (removed on the first attempt, also for the wrong user), bound to the user, at most 20 open per user and kind, swept every 15 s.
- Limits: WebSocket messages 64 KiB (Guacamole, SSH) and 1 MiB plus one byte (screen clipboard), instruction structure of browser-to-guacd traffic is validated (`InstructionScanner`), sessions per user 8 (`JULOS_MAX_SESSIONS_PER_USER`), global 16 (Guacamole), 16 (screen), 32 (SSH).
- Screen protocol parsers (`ScreenRelay.Handle`): every case checks the length before slicing, sizes are clamped, unknown types are ignored, text is decoded with replacement, transfer announcements are bounded (10 files, 64 MB each and per batch, 32 MB image, 256 KiB chunks, 120-character names through `ClipboardNames.Sanitize`, executable extensions refused, idle timeout per transfer).
- Connections: parameterised SQL only (no string-built SQL with input anywhere), at most 50 per user, name/host/user validated, changing host, port, kind or user deletes the stored password, changing host, port or kind forgets the SSH host key. Passwords are never returned (`hasPassword` only).
- File browser: relative paths, `..` and dot names refused, symlinks resolved step by step and checked against the real root, descriptor re-checked through `/proc/self/fd`, search never follows links, downloads always `attachment` and `application/octet-stream`, `nosniff` (the `Content-Disposition` of a file name with a quote or a newline was checked on the real server: quoted/`filename*` encoded, no header injection), listings capped. Hardlinks are not detected (documented).
- Agent proxy: fixed paths, window id digits only (12 characters), actions from a list, launch id up to 200 characters and only passed to `Gio.DesktopAppInfo.new` (a desktop file id, not a path or command), answers limited to 600 kB and parsed as JSON, error text of the agent cut to 120 characters. Socket directory 0700, socket 0600.
- `/api/log` accepts only keys of letters/digits and values that are numbers, booleans or strings up to 24 characters, written as JSON (no log forging), 60 per minute.

**Secrets**
- AES-256-GCM: random 12-byte nonce per encryption, 16-byte tag, the connection id as associated data (an entry cannot be moved to another connection), key length checked, plaintext buffers zeroed. `secret.key` is created with mode 0600 (checked), only when missing, atomically (`CreateNew`).
- Logs: no passwords, tickets, cookies, host key fingerprints or TOTP codes are logged; login failures log the normalised user name and the address; the setup code is logged on purpose (that is how it reaches the owner). Error texts to the browser are fixed German texts, RDP errors come from a fixed list or from the library message of the protocol failure.
- SSH: host key checked before the password is sent, trust on first use, a changed key is refused until the exact new fingerprint is confirmed, the key is stored only after a successful login, keyboard-interactive answers are limited to password prompts.

**Web client**
- Stored XSS: every `innerHTML` that contains server or user data goes through `esc` (`shell.js`, `mobile.js`, `desktop.js` with `ctx.esc`) or the data is set with `textContent` (connection names, file names and paths, window titles, app names, session names, remote clipboard text, SSH title). A connection named `<img src=x onerror=alert(1)>` was stored on the test server and the code paths that render it escape it. Numbers (`port`, counts) come from the server as numbers. Remaining unescaped template values are constants in the source.
- CSP (`Program.cs:101-102`): `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'`. The page has one script (a module), no inline handler, no `javascript:` URL, no `eval`, no worker from a blob, no WebAssembly, so this policy already is the strict one asked for; nothing to change. `connect-src 'self'` covers `ws:`/`wss:` of the same origin in current browsers. No violations were seen when the phone layout was loaded and used on the throwaway instance (the desktop layout and the terminal were not exercised in the browser; static check only). `style-src 'unsafe-inline'` stays: the templates use `style="..."` attributes and xterm.js adds style elements when it falls back to the DOM renderer; removing it needs a browser test. Styles cannot run script, and `img-src`/`default-src` stop data leaving the page.
- Other headers: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY` and `frame-ancestors 'none'`, `Cross-Origin-Opener-Policy`/`Cross-Origin-Resource-Policy: same-origin`, HSTS over HTTPS, `Cache-Control: no-store` for `/api`, no `Server` header.
- Links: every `target=_blank` and `window.open` has `noopener` (`desktop.js:358-360,863`, `mobile.js:451`). xterm.js has no link or clipboard (OSC 52) addon loaded; the terminal title event is not used.
- Service worker: `sw.js` caches only same-origin GET requests outside `/api/` (the app shell and static files), network first; authenticated data (`/api/*`, downloads) is never cached. Scope is `/`.
- Local storage holds only layout, favourites and the usage log (no passwords, no tickets).

**Supply chain and deployment**
- `dotnet list package --vulnerable --include-transitive`: no vulnerable package in any project (Npgsql 10.0.3, SSH.NET 2026.0.1, Konscious Argon2 1.3.1, test packages).
- Vendored: guacamole-common-js 1.5.0 and xterm.js 6.0.0 with addons (current releases at the time of the review, unmodified, licences included); guacd 1.6.0 in Compose.
- Dockerfile: multi-stage, runtime image `aspnet`, runs as a non-root user (`USER $APP_UID`; Compose runs as `JULOS_UID`), only `/data` is prepared as writable, no secrets in build arguments.
- Compose: PostgreSQL and guacd have no published port; the server is published on loopback unless `BIND_IP` is set; the database password is required (`:?`).
- Caddy examples: HSTS, `nosniff`, no referrer, `-Server`, WebSocket and long timeouts, optional mTLS (`tools/make-client-cert.sh` keeps the CA key out of the repo via `.gitignore` for `pki/` and `.env`), a LAN-only first stage. Missing in the examples: a rate limit (Caddy needs a plugin) and the trusted-proxy hint from finding 3.

## Not verified here

- The desktop layout, terminal, remote picture and clipboard were not driven in a browser (no permission to type credentials for a target; the relays are covered by the existing integration tests against real guacd, SSH and RDP containers).
- No penetration test against the live address, no TLS or Caddy configuration of the live setup, no review of the Windows/Linux target machines or of the upstream libraries beyond their version.
- The decoders of the own RDP engine (`Display/Rdp`) were read for allocation sizes taken from the server and have bounds and robustness tests (`BitmapRobustnessTests`); they were not fuzzed. A hostile RDP target is the remaining way to reach them.
