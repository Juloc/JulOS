# Integrations: Proxmox, Synology, Home Assistant, Docker, Caddy (plan)

Status: **plan only, nothing of this is built** (written for release 0.10.0). It is the build plan for the next agents: one shared integration layer in the backend,
then one adapter per system, each starting read-only. It extends the modules that exist ([MODULES.md](MODULES.md): services widgets, Caddy read, DDNS, the "Heimnetz" card)
and the hosts ([HOSTS.md](HOSTS.md)). The earlier design notes for Docker, Caddy and DDNS are in [IDEAS.md](IDEAS.md) ("Desktop modules without a backend yet"); this page
replaces the open parts of them for Docker and adds Proxmox, Synology and Home Assistant. [SCOPE.md](SCOPE.md) still decides when anything here is started.

**How to read the claims.** Every API detail carries a source number `[S#]` that points to the list at the end. A detail without a number, or marked **zu pruefen**
("to be verified"), was *not* confirmed in public documentation while writing this page (only documentation was read; no device of the home network was contacted).
Fixtures for the tests must therefore be written from the documented schema and checked against the real system by hand once, on the test instance first.
Effort: S = about a day of agent work, M = two to four days, L = a week or more. Benefit: high / medium / low.

No address, host name or token of the real network is written here (working rule 3); examples use placeholders such as `<proxmox-host>`.

## 1. Target picture and principles

### 1.1 What the user gets

One place in JulOS that answers "is my home setup fine?" and, where allowed, "do this one small thing": see Proxmox nodes and guests, containers, the NAS, Home Assistant sensors and the
reverse proxy with a traffic light; start or gracefully shut down a chosen VM; restart a chosen container; trigger a Home Assistant scene; open a machine (VM) as a JulOS host with one tap.
The Heimnetz card becomes the front page for all of it.

### 1.2 Principles (all taken over from MODULES.md and IDEAS.md, none weakened)

1. **Off by default.** An integration does nothing until the server's settings switch it on. Off means its endpoints answer `404`, the UI shows nothing.
2. **One level per integration: `off | read | control`.** `read` sends only requests that do not change anything on the target (the one exception, the DSM login, is named in 3.4). `control` adds a short, fixed list of named actions, never free-form calls.
3. **The ceiling is in the server settings, the UI can only go below it.** `JULOS_<KIND>=read` in the Compose file caps the integration; the stored level in the database may be lower, never higher. A stolen session therefore cannot switch `control` on.
4. **Session and `ApiGuard` on every endpoint**, rate limits per user, the `X-JulOS` header and same-origin on every change, as everywhere.
5. **Secrets** are encrypted with the existing `SecretBox` (AES-GCM, key from `JULOS_SECRET_KEY` or `/data/secret.key`) or read from a file on every use. They are write-only in the API (the list says "gespeichert"), never in a log line, never in an error message, never sent to the browser, never to an agent.
6. **The browser never names a target.** An address is stored when an integration is created, checked against the allow-list then and again at every connect (after name resolution, like `ServiceHosts`); afterwards only fixed endpoints with checked values are called. The browser sends ids that exist in the last snapshot, never a path, a node name or a service name of its own.
7. **Every write is audited** (who, what, target, when, result), including refused and failed attempts. Reads are not.
8. **JulOS never locks itself out.** It never stops, restarts or reconfigures its own container, its database, its socket proxy, or (by default) the reverse proxy in front of it. Every write has a way back (start/shutdown pair, re-enable, rollback) or is not offered.
9. **Fakes, not the real thing, in tests**; the real system only by hand on the test instance.
10. **Existing tools stay in charge.** Dockhand owns stacks, the Caddy UI owns routes, Proxmox owns VMs. JulOS shows and triggers small things; it does not become a second admin tool.

### 1.3 The shared integration layer (milestone M0)

**Configuration of one integration** (a row; env-only integrations like Caddy and DDNS are "virtual" rows, see 1.4):

| Field | Meaning |
| --- | --- |
| `kind` | `proxmox`, `synology`, `homeassistant`, `docker`, later `adguard`, `unifi`, `plex` |
| `name`, `icon`, `sort` | display |
| `base_url` | scheme, host, port only; paths are fixed in the adapter. `https` required for anything that carries credentials, `http` only for a private address and only with an explicit "unencrypted, I know" flag that shows an amber note |
| `level` | `off | read | control`, at most the ceiling from the environment |
| secrets | `integration_secrets` rows: purpose `read` or `control` (two **different** credentials where the target supports it, see 1.5), or `secret_file` (path, read on every use) |
| `tls_pin` | SHA-256 of the server certificate (leaf) when the target has a self-signed certificate; empty = normal chain validation |
| `options` (jsonb) | adapter-specific: selected entities (Home Assistant), selected nodes or guests (Proxmox), label names (Docker) |
| `interval_seconds` | read cache, 10 to 3600, default per kind (Docker 15, Proxmox 20, Home Assistant 30, Synology 60) |
| limits | connect 3 s, request 5 s, body at most 256 KB (per call overridable down to 64 KB and up to 2 MB only where named), at most 4 requests at once per integration |
| `paused` | the per-integration emergency switch (1.6): drops the effective level to `off` at once without deleting anything |

**What the adapter implements** (C# sketch, names are proposals):

```csharp
public interface IIntegration
{
    string Kind { get; }
    // read: one cached poll, returns the tile figures and the typed items (nodes, guests, containers, entities)
    Task<IntegrationSnapshot> PollAsync(IntegrationContext ctx, CancellationToken ct);
    // control: the fixed list of named actions this adapter supports (may be empty)
    IReadOnlyList<ActionDef> Actions { get; }
    Task<ActionResult> ExecuteAsync(IntegrationContext ctx, ActionRequest request, CancellationToken ct);
}
// IntegrationSnapshot: state (up|degraded|down|unknown), level (ok|warn|bad|idle), summary text,
//   metrics[] (same MetricValue as the services widgets: label, unit, format, number?, text?),
//   items[] (id, type, name, state, metrics, hostRef?), checkedAt, ms, error (a short code, never a body)
// ActionDef: name, risk (low|medium|high), needsConfirm, needsTypedName, undo (the name of the opposite action or null)
```

The context carries the decrypted secret only inside the call (zeroed after use, like `SecretBox`), the guarded HTTP client and a `TimeProvider` (so tests control time).

**Error classes** (a short fixed code, like `ServiceChecker` today): `notAllowed`, `timeout`, `refused`, `tooLarge`, `status`, `invalid`, plus new ones: `auth` (401/403: "credentials rejected", shown differently from "down"), `tlsChanged` (pin mismatch; **no request is sent**), `degraded` (answers, but with partial data). A failed poll keeps the last good snapshot visible as "stale" for three intervals, then the tile goes grey. After three failures in a row the integration backs off (doubling, at most 5 minutes); "check now" resets it.

### 1.4 What is reused, and where (concrete)

| Existing piece | File | Reuse |
| --- | --- | --- |
| Guarded outbound HTTP: no redirects, no proxy, connect callback that checks the resolved address against the allow-list, per-call timeout, size limit, 8 at once | `src/JulOS.Backend/Modules/ServiceChecker.cs` (`Handler`, `GetAsync`), `OutboundHttp.cs` (`Create`, `ReadLimitedAsync`) | **Extract** the handler and the limited read into one `GuardedHttp` (M0) that `ServiceChecker` and all adapters use; add the optional pin to it (`SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback`, compare the leaf's SHA-256). `ServiceChecker` keeps its behaviour (its tests stay green) |
| Allow-list and the private-address rule (link-local, `0.0.0.0`, multicast never) | `ServiceHosts` in `ServiceChecker.cs`, `UpstreamTarget.IsForbidden`, `DdnsMonitor.IsPrivate` | Same class, with `JULOS_INTEGRATIONS_HOSTS` (fallback `JULOS_ALLOWED_HOSTS`) as list; an integration's own `base_url` host must pass it on save and at connect |
| Switch parsing `off|read|control`, warnings without values | `ModuleMode.cs`, `ModuleSettings.cs` | Add `JULOS_INTEGRATIONS` (global ceiling) and one switch per kind; effective level = minimum of global, kind and row. `GET /api/modules` gets an `integrations` object (kind -> level) and nothing else |
| Secret storage, write-only, the row id as associated data | `Logic/SecretBox.cs`, `Data/ServiceStore.cs` (`SetSecretAsync`, `GetSecretAsync`) | Table `integration_secrets` with its own id per secret (so `SecretBox.Encrypt(plain, id)` works unchanged); secret by file as in `JULOS_CADDY_ADMIN_AUTH_FILE` and `JULOS_DDNS_TOKEN_FILE` |
| History of changes in a small table, shown as "Verlauf", last N rows | `Data/DdnsHistoryStore.cs`, migration `013_ddns_history.sql`, `ddns` list in `mobile.js` "Aktivitaeten" | Generalise to `integration_audit` (1.7); the phone's Activities list takes the last entries the same way |
| Rate limits per user and endpoint | `Logic/HitCounter.cs`, `Busy(...)` in `ModuleEndpoints.cs` | Same helper with keys `integrations-<kind>-<what>`; plus the write limits in 1.7 |
| Per-service cache by interval, "only who looks asks" | `ServiceChecker._cache` | Same rule for reads: single-flight per integration, so ten open windows cause one poll |
| Figures with units and formats (`number`, `KB`, `MB/s`, percent) and the field-path extractor | `ServiceRules.Extract`, `wwwroot/modules.js` (formats) | The snapshot's `metrics[]` is the same `MetricValue`; the front end draws them with the existing format code |
| TCP reachability with cache | `JulOS.Integrations/Reachability/TcpProbe.cs` | For NAS and Home Assistant hosts (service dot), and for "start VM, wait until it answers" |
| Certificate handshake and expiry warning (14 days) | `Modules/CaddyCertificates.cs` | Also run against the integrations' own HTTPS endpoints (a pinned self-signed certificate expires too) |
| Caddy read | `Modules/CaddyModule.cs` and friends | Wrapped as a virtual integration (adapter around the existing class, no rewrite) |
| DDNS | `Modules/DdnsModule.cs`, `DdnsUpdater.cs`, `Workers/DdnsAutoWorker.cs` | Virtual integration for the tile and the Heimnetz card; its own switch and updater stay as they are |
| Heimnetz card: `worst`, `summarize`, `loadHeimnetz`, `mountHeimnetz` | `wwwroot/heimnetz.js` | One more key `integrations` in `data`, rows grouped per area (1.8); the pure functions stay testable in `tests/frontend/modules.test.mjs` |
| Hosts, status dots, 15 s probe cache, agent link | `Data/HostStore.cs`, `Frontend/Hosts/HostEndpoints.cs`, `wwwroot/hosts.js`, [HOSTS.md](HOSTS.md) | Host links (1.9) |
| Windows and sheets for modules | `wwwroot/desktop-modules.js`, `mobile-modules.js`, `modules.js` | New `integrations.js` (pure logic, tested), `desktop-integrations.js`, `mobile-integrations.js`; German texts in `wwwroot/i18n` |
| Test fakes | `tests/JulOS.Backend.Tests/ModuleFakes.cs` (`FakeCaddyAdmin`, `FakeWeb`, `FakeResolver`), `CaddyReadTests.cs` (real TLS handshake with a throw-away certificate) | New `FakePve`, `FakeDockerProxy`, `FakeHomeAssistant`, `FakeDsm` handlers in the same style |

**Virtual integrations.** Caddy and DDNS are configured only from the environment, have no row and no secret in the UI. The integration list shows them as read-only entries ("aus den Servereinstellungen") so every tile, the card and the future link logic treat all systems alike. Nothing about their switches changes.

**Database (next free migration numbers at build time; today 016 and 017):**

```sql
create table integrations (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references users (id) on delete cascade,
  kind text not null, name text not null, icon text not null default 'plug', sort integer not null default 0,
  base_url text not null, level text not null default 'off', paused boolean not null default false,
  secret_file text not null default '', tls_pin text not null default '',
  options jsonb not null default '{}'::jsonb, interval_seconds integer not null default 30,
  created_at timestamptz not null default now());
create table integration_secrets (
  id uuid primary key default gen_random_uuid(),
  integration_id uuid not null references integrations (id) on delete cascade,
  purpose text not null check (purpose in ('read','control')), secret bytea not null,
  unique (integration_id, purpose));
create table integration_audit (
  id bigserial primary key, at timestamptz not null default now(),
  user_id uuid, integration_id uuid, kind text not null, action text not null, target text not null default '',
  trigger text not null, ok boolean not null, outcome text not null default '', detail text not null default '');
create index integration_audit_at_idx on integration_audit (at desc);
```

The backup dump (`DbDump`, [BACKUP.md](BACKUP.md)) includes the new tables automatically in foreign-key order; check this in the migration test. Secrets in the dump stay encrypted, and the key file is backed up apart, as today.

**Endpoints** (all under `/api/modules/integrations`, all need a session; the level decides the answer: `404` off, `403` for control on a `read` integration):

| Path | Answer |
| --- | --- |
| `GET /api/modules/integrations` | list: id, kind, name, level, effective level, ceiling, `hasSecret` (read / control), `pinned`, `paused`, interval; never a secret |
| `POST`, `PUT`, `DELETE /{id}` | create, change, delete; secrets are write-only (`null` keeps, `""` removes) |
| `GET /{id}/status` | the snapshot (summary, metrics, state, level, stale flag) |
| `POST /{id}/check` | poll now, ignoring the interval (limited to 6 per minute) |
| `GET /{id}/items?type=` | typed items of the last snapshot (guests, containers, entities) |
| `POST /{id}/tls/pin` | `{fingerprint}`: confirm a presented fingerprint (the user compares it with the one shown on the target) |
| `POST /{id}/pause`, `/resume` | the emergency switch |
| `POST /{id}/actions/{name}` | `{target, confirm?}` control only; two steps (1.7) |
| `GET /{id}/audit` | last 50 entries |

### 1.5 Credentials: two keys where possible

A read-only credential and a separate control credential, held in two rows. At level `read` the control secret is **not decrypted and not even needed**; a bug or an exploited read path cannot use it. Proxmox (two API tokens with different roles) and Home Assistant (two users) support this; Docker needs no secret (the socket proxy is the boundary); Synology cannot (see 3.4).

### 1.6 Emergency switches ("Not-Aus") per integration

1. **UI: Pause** on the tile and in the window: the effective level becomes `off` immediately, running polls are cancelled, nothing is deleted. Audited.
2. **Environment: `JULOS_<KIND>=off`** (or `read`) and restart: the hard stop that no session can undo.
3. **Global: `JULOS_INTEGRATIONS=off`** switches every integration off at once.
4. **Automatic downgrade:** three failed control actions in a row, or an answer that violates the expected shape (for example a start request that returns something else than a task id), sets `paused` and writes an audit line. It never retries a write by itself.
5. **On the target:** each section below says how to revoke the token or role there; that is the last resort if JulOS itself is the problem.

### 1.7 Writes: confirmation, limits, audit, the way back

- **Two steps.** `POST .../actions/start` with `{target}` answers `{needsConfirm, confirmId, consequence}` (text for the dialog, for example "VM 104 wird heruntergefahren, Dienste darauf sind weg"). A second call with `confirmId` (one-time, 60 s, bound to session, action and target) executes. High-risk actions (`stop` of a VM, container restart of a flagged container) additionally require the user to type the target's name.
- **Limits:** 6 actions per minute and user, one action per target per 10 s, at most 2 writes at once per integration. After an action the integration polls again at once and the answer is compared with the expectation.
- **Targets come from the last snapshot and an allow-list of the user's choosing** (`options`): an id the server has not seen, or one that is not selected, is refused with `403` and audited.
- **Audit (`integration_audit`)**: `who, integration, action, target (id and display name), trigger (button | auto), ok, outcome, detail` (at most 200 characters, never a response body, never a secret). Also written: level changes, pause/resume, secret set or removed (not the value), pin changed, failed or refused attempts. Kept: the last 1000 rows. Shown as "Verlauf" in the window and in the phone's Activities.
- **Nothing happens automatically** except what 3.5 lists.

### 1.8 How integrations appear in the UI

- **Tile (Dienste page on the phone, widgets window and Start page on the desktop):** name, icon, traffic light (`ok | warn | bad | idle`, same colours as the Heimnetz card) and two to four figures. Examples: `Proxmox: 1 Knoten, 9 von 12 Gaesten laufen, CPU 18 %, RAM 54 %`; `Docker: 23 von 25 laufen, 1 ungesund`; `Home Assistant: 14 Sensoren, 1 nicht verfuegbar, 2 Updates`; `NAS: Volume 71 %, 38 Grad`.
- **Desktop:** a window per integration (like the Caddy window): tabs **Uebersicht** (figures and the list of items), **Verlauf** (audit), **Einstellungen** (address, secrets, pin, level, interval, pause). A tap on an item opens its detail (guest: state, CPU, RAM, uptime, actions).
- **Phone:** a page (sheet) with the same content in one column; items as list rows with a status dot; actions in a bottom sheet with the confirmation.
- **Heimnetz card 2.0:** rows grouped by area, worst colour per area and overall: *Zugang* (Caddy, DDNS), *Rechner* (Proxmox, NAS), *Container* (Docker), *Smart Home* (Home Assistant); plus an **attention list** of the five most urgent single items across all integrations (a guest that should run is stopped, last backup failed, volume over 90 %, certificate within 14 days, container unhealthy, sensor unavailable). Only integrations that are on appear; one that does not answer turns only itself grey (unchanged rule).

### 1.9 Hosts mapping and "open VM"

- A new table `host_links (host_id, integration_id, kind, ref, node)` ties a host to one thing in an integration: a Proxmox guest (`kind=guest`, `ref=<vmid>`, `node`), a Proxmox node, the NAS, the Home Assistant instance, a Docker host. A host has at most one link per integration.
- **Suggestions, not automatic creation:** the Proxmox window lists guests that have no host with **"Als Host anlegen"**: creates a host named like the guest and links it; a guest that already has a connection with the same address is suggested for linking. The NAS and Home Assistant are offered the same way, with their known service port as a first connection (SSH 22 for Home Assistant, the web port for the NAS as a TCP service).
- **Status dot, extended:** green = a service or the agent answers; **amber = the integration says "running" but nothing answers yet** (booting, hung); grey = powered off or unknown. `GET /api/hosts` gains `links: [{integration, kind, ref, powered, state}]`; the old fields stay (HOSTS.md changes in the same commit).
- **"VM oeffnen"** on a linked host = the existing choices: **Verbindung** (RDP/VNC desktop), **Terminal** (SSH), **Agent** (agent window or setup). A stopped guest shows **"Starten und verbinden"** (needs `control`): start, wait for the task and for the TCP probe of the chosen connection (at most 3 minutes, with a visible status), then connect. Without `control` the button is hidden, not disabled.
- **Limit:** hosts are capped at 20 per user ([HOSTS.md](HOSTS.md)). Recommended: raise to 40 and let a guest become a host only when the user chooses it (see 5).

## 2. Integrations

### 2.1 Docker (via a socket proxy)

**Benefit: high. Effort: M for read, S for control. Risk: highest of the read adapters (the Docker API is root on the machine). Default level: `off`; first stage `read`.**

**Why a proxy, never the socket.** Whoever reaches `/var/run/docker.sock` can start a privileged container with `/` mounted. The JulOS container therefore never mounts it ([IDEAS.md](IDEAS.md) design). A socket proxy is the only container with the socket mounted, listens only in an internal Compose network (no published port) and allows API sections by environment variable [S24]. It is one new service; SCOPE rule 4 asks for the reason: the alternative is the socket inside JulOS, which turns any JulOS bug into root on the host.

**Proxy settings for `read`:** `CONTAINERS=1`, `INFO=1`, `IMAGES=1`, `POST=0`; everything else at its default `0` (`EVENTS`, `PING`, `VERSION` default to granted) [S24]. Not `EXEC`, `VOLUMES`, `NETWORKS`, `SERVICES`, `SWARM`, `SYSTEM`, `BUILD`, `COMMIT`, `SECRETS`, `CONFIGS`, `AUTH`. `JULOS_DOCKER_URL=http://<proxy>:2375` (the port is the image's default, **zu pruefen**).

**What the proxy cannot do, and what JulOS does about it.** The proxy filters by section and method, not by container. With `CONTAINERS=1` it also lets through `GET /containers/{id}/json` (environment variables of every container, often secrets) and the other read paths of that section (logs, archive/file copy). JulOS compensates in its own client: **only these requests are ever built**: `GET /_ping`, `GET /version`, `GET /info`, `GET /containers/json`, `GET /containers/{id}/stats?stream=false&one-shot=true`, optionally `GET /containers/{id}/logs`, and for control three `POST`s. No inspect (`/containers/{id}/json`), no archive, no `exec`, no `attach`. Anything JulOS needs from a container (name, image, state, status text with "(healthy)", labels, ports) is in the list response. The "never raw" rule of MODULES.md applies: the browser gets a reduced, named set of fields, never Docker's JSON.

**Read endpoints and fields** [S23 for the stats and logs parameters; the list and `/info` field names are from the Engine API and **zu pruefen** against the fixture]:

| Request | Used for |
| --- | --- |
| `GET /containers/json?all=true` | name, image, `State`, `Status`, `Labels` (`com.docker.compose.project`, `julos.control`, `julos.logs`), ports |
| `GET /info` | running/stopped counts, `MemTotal`, `NCPU`, server version |
| `GET /containers/{id}/stats?stream=false&one-shot=true` | CPU and memory per container. The documented formulas [S23]: CPU % = (cpu_delta / system_cpu_delta) x online CPUs x 100 with `cpu_delta = cpu_stats.cpu_usage.total_usage - precpu_stats.cpu_usage.total_usage`; memory = `memory_stats.usage - memory_stats.stats.inactive_file` (cgroup v2) or `- stats.cache` (v1), against `memory_stats.limit`. With `one-shot` the previous sample may be empty: JulOS keeps its own previous sample per container and computes the delta itself (**zu pruefen** against a real daemon on cgroup v2) |
| `GET /containers/{id}/logs?stdout=1&stderr=1&tail=N&timestamps=1` | works only for the `json-file` and `journald` log drivers [S23]; the body is a multiplexed stream (8-byte frame header) unless the container has a TTY: demultiplex, **zu pruefen** how a TTY container is told apart without inspect (fall back: detect by the first bytes) |

**Stats are expensive** (one request per container, about a second each): the tile uses only `/containers/json` and `/info`; stats are fetched only while the window is open and only for the visible rows (at most 12 at a time), cached 15 s.

**Logs:** read only, per container opt-in by label `julos.logs=true`, `tail` at most 500 lines and 256 KB, never stored, never sent to an agent; the window warns that logs can contain secrets.

**Control stage (`JULOS_DOCKER=control`):** start, stop, restart of selected containers, nothing else.
- Proxy: `ALLOW_START=1`, `ALLOW_STOP=1`, `ALLOW_RESTARTS=1` (the last also covers `kill` in Tecnativa's description [S24]; accepted, JulOS never sends it). **Whether these work with `POST=0` in the Tecnativa image is not stated in its README (zu pruefen); the linuxserver variant documents that they do since a 2024 change [S25].** Prefer `POST=0` plus the three `ALLOW_*`: with `POST=1` the `CONTAINERS` section could also allow `POST /containers/create`.
- **Hardening self-test:** when the level is `control`, JulOS sends at start (and on "check now") a request that must be refused by the proxy and does nothing even if it were allowed, for example `POST /containers/julos-probe-none/exec` and `/update`. Expected `403` from the proxy (a `404` from Docker means the proxy let it through). If a probe is not refused, control is switched off with a visible message.
- Which containers: **opt-in by label `julos.control=true`**, read from the list response and checked by JulOS (the proxy cannot do it). The browser sends the container id; the server looks it up in the last snapshot and checks the label and the exclusions.
- **Never, in code, label or not:** the container JulOS runs in (its id is its hostname inside the container, **zu pruefen**), the proxy, every container of the Compose project that contains JulOS or the proxy (`com.docker.compose.project`), and anything named in `JULOS_DOCKER_PROTECTED` (comma list; recommended: the reverse proxy and the database). Create, remove, exec, pull, image or network changes are never built. **Stacks belong to Dockhand**: a tile link "in Dockhand oeffnen" (an ordinary services widget) is the way to change a stack.
- Verbs: `start` (204 = done, 304 = already running), `stop` and `restart` with `t=10` (seconds before the kill) [S23]. Way back: start/stop are each other's undo. `stop` and `restart` of a flagged container need a typed name.

**Example widget:** `Docker (Server) . 23/25 laufen . 1 ungesund . Speicher 6,1 GB`; rows: container, state dot, CPU, RAM.

**Test plan (fakes):** `FakeDockerProxy` returns fixtures written from the Engine API schema (list, info, stats with cgroup v1 and v2 shapes, a multiplexed log stream and a TTY stream). Tests: only the allowed request shapes are produced (a request recorder asserts that no other path or method is ever sent); the stats formula against hand-computed numbers; label gating and the exclusions (own container, project, protected list); an unknown or unlabelled id is refused and audited; the self-test refuses control when the fake lets `exec` through; logs: tail and size caps, demultiplexing, no storage; level `read` gives `403` for any action; the browser cannot send a path or a name (only ids); `tests/frontend` for the tile summary and the attention list.

**Set-up for the user:** add the proxy service to the Compose file (internal network shared with JulOS only, socket mounted read-only is good hygiene but is not a security boundary), set the variables above, add labels `julos.control=true` / `julos.logs=true` only to containers you want. No secret is needed. Revoke: remove the proxy service.

### 2.2 Proxmox VE

**Benefit: high (hosts, VM state, backups at a glance). Effort: M read, M control. Risk: medium (a token on the hypervisor). Default: `off`; first `read`; `control` only for named guests.**

**Credentials.** The API accepts `Authorization: PVEAPIToken=USER@REALM!TOKENID=SECRET` on `https://<proxmox-host>:8006/api2/json/`; a token needs no CSRF header, and the token secret is shown once at creation [S1, S2].
- **Read token:** a dedicated user `julos@pve` (realm `pve`, no password needed for API use) and a token `read` with privilege separation. Role **PVEAuditor** (the built-in role is the union of all `*.Audit` privileges: `VM.Audit`, `VM.GuestAgent.Audit`, `Sys.Audit`, `Datastore.Audit`, `SDN.Audit`, `Pool.Audit`, `Mapping.Audit` [S8]). Because a privilege-separated token never has more than its user on a path, give the role to **both** the user and the token [S2]:
  `pveum user add julos@pve`, `pveum user token add julos@pve read -privsep 1`, `pveum acl modify / -user julos@pve -role PVEAuditor`, `pveum acl modify / -token 'julos@pve!read' -role PVEAuditor` (syntax of the last two from the manual [S2]; check the role with `pveum role list`, **zu pruefen** on your version).
- **Control token:** a second token `power` with an own role `JulOSPower` containing only `VM.Audit` and `VM.PowerMgmt` (`pveum role add JulOSPower --privs "VM.Audit VM.PowerMgmt"`, syntax from the manual [S2]), granted **per guest path** (`/vms/<vmid>`) and only for the guests you choose. Then Proxmox itself refuses an action on any other guest, even if JulOS were compromised. Never grant `Sys.PowerMgmt` (node shutdown or reboot, `POST /nodes/{node}/status` needs it [S4]), `VM.Console`, `VM.Allocate`, `VM.Config.*` or `Permissions.Modify`.
- Where it goes: the user types the token into the integration form (write-only) or puts it in a file mounted into the container (`secretFile`, one line `julos@pve!read=<secret>`); JulOS never shows it again. Revoke on the target: `pveum user token remove julos@pve read` (and `power`).

**TLS: pin, do not ignore.** A default installation uses a self-signed certificate (the node certificate `/etc/pve/local/pve-ssl.pem` [S9]). JulOS never turns verification off. On first save the server fetches the presented certificate, shows its SHA-256 fingerprint, and the user confirms it after comparing it with the target (on the node: `openssl x509 -in /etc/pve/local/pve-ssl.pem -noout -fingerprint -sha256`; the GUI may show it too, **zu pruefen**). The pin is stored; on a mismatch the handshake fails before any request (no token is sent), the tile says "Zertifikat geaendert". The same idea is already used for RDP (`connections.cert_fingerprint`, migration 006). With a trusted certificate (ACME behind Caddy) the normal chain check is used and no pin is needed.

**Read endpoints and fields:**

| Request | Permission | Fields used |
| --- | --- | --- |
| `GET /cluster/resources` | any authenticated user, items filtered by `VM.Audit`, `Sys.Audit`, `Datastore.Audit`, ... [S3] | per item: `id`, `type` (`node`, `qemu`, `lxc`, `storage`, `pool`, `sdn`), `name`, `node`, `vmid`, `status`, `cpu`, `maxcpu`, `mem`, `maxmem`, `disk`, `maxdisk`, `uptime`, `template`, `tags`, `lock`, storage: `storage`, `content`, `shared` [S3] (types seen in the schema; field types, for example `mem`, **zu pruefen** against a real response). One call feeds the whole tile; the `type` query filter exists (`vm`, `storage`, `node`, `sdn`) [S3] |
| `GET /nodes/{node}/status` | `Sys.Audit` on `/nodes/{node}` [S4] | `uptime`, `cpu`, `loadavg`, `memory{total,used,free}`, `rootfs{total,used,free}`, `pveversion`, `cpuinfo` [S4] |
| `GET /nodes/{node}/qemu/{vmid}/status/current`, `.../lxc/{vmid}/status/current` | `VM.Audit` on `/vms/{vmid}` [S6, S7] | detail of one guest when its row is opened |
| `GET /nodes/{node}/tasks?limit=50&source=all[&typefilter=vzdump][&errors=1]` | own tasks, or all with `Sys.Audit` on the node [S5] | `upid`, `type`, `id` (guest), `status`, `starttime`, `endtime`, `user` [S5]; a `vzdump` task with a status other than `OK` = backup failure; the newest `vzdump` per guest = "last backup" |
| Storage | via `/cluster/resources` (`type=storage`: `disk`, `maxdisk`, `status`) | warn at 85 %, bad at 95 % |
| Backup jobs `GET /cluster/backup`, per-node storage list | **zu pruefen** (not read) | optional later |

Cluster: the token works on every node; the integration stores one address and optionally up to two fallback addresses (**zu pruefen**: behaviour when the first node is down). Polling `GET /cluster/resources` every 20 s is light (one small JSON).

**Control stage (`JULOS_PROXMOX=control`):** four actions on a **selected** guest, using the `power` token:
`POST /nodes/{node}/qemu/{vmid}/status/{start|shutdown|reboot|stop}` and the same under `lxc` [S6, S7]. All need `VM.PowerMgmt` on the guest; the answer is a task id (UPID string) that JulOS follows through the guest's status, not by trusting the reply [S6]. Parameters JulOS may send: `timeout` (shutdown, reboot), `forceStop=0` (shutdown) [S6, S7]; never `skiplock` or `keepActive` (root only [S6]). `reset` is not offered.
- `shutdown` = ACPI/guest shutdown (the way back is `start`); `reboot` likewise; **`stop` is a hard power-off** and needs the typed name plus the extra option `allowHardStop` in the integration's settings.
- Node and `vmid` are taken from the last snapshot, validated (`^[0-9]{3,9}$`, node `^[A-Za-z0-9-]{1,63}$`), and must be in the user's selection. HA-managed guests answer with a different task type (**zu pruefen**); refused in the first version.
- Not built: create, clone, migrate, snapshot, restore, delete, node power, firewall, storage changes.

**Console later.** Embedding a console would use the ticket flow (`POST /nodes/{node}/qemu/{vmid}/vncproxy`, then a WebSocket; needs `VM.Console`), **zu pruefen**. Idea worth testing then: the VNC proxy speaks RFB, and JulOS already has an RFB client in its own stream engine (see [CHANGELOG.md](../CHANGELOG.md) 0.10.0), so the console might reuse it instead of a noVNC page. Not part of M2 to M5; decision in 5.

**Example widget:** `pve . 1 Knoten online . 9/12 Gaeste laufen . CPU 18 % . RAM 54 % . letztes Backup 03:12 OK`; rows: guest, type, state dot, CPU, RAM, uptime; storage bars.

**Test plan:** `FakePve` (handler) with fixtures written from the schema of `cluster/resources`, `status`, `status/current`, `tasks`; header format and absence of redirects; a **real TLS loopback listener with a throw-away certificate** (like `CaddyReadTests`): matching pin passes, a different certificate stops before any request (the fake counts zero requests); allow-list after DNS resolution; `auth` state on 401 and 403; task list to "last backup" logic with OK, failed and missing tasks; control: only the four verbs on `qemu` and `lxc`, ids validated, a guest outside the selection refused and audited, `stop` without the option refused, level `read` gives `403`, the control token is not decrypted at `read` (assert with a secret-store spy), the audit row for success, failure and refusal; no secret in any response or log line (scan captured output); frontend tests for tile summary and attention list.

**Set-up for the user:** 1. create user and read token as above; 2. optional: role `JulOSPower` and the per-guest ACLs, second token; 3. in JulOS: Integrations, add Proxmox, address, paste the token (or file), confirm the fingerprint, choose level; 4. pick the guests for control. Tokens are typed by you.

### 2.3 Home Assistant

**Benefit: medium to high (sensors on the card, scenes from the phone, push alarms). Effort: M read, M control and notify, M for the WebSocket. Risk: medium to high, because a Home Assistant token cannot be scoped. Default: `off`; `read` with an entity selection.**

**Credentials.** A **long-lived access token** created in the user profile (Security section), sent as `Authorization: Bearer TOKEN` [S18]. The token has **no scope of its own**: it acts with the permissions of the account that created it [S21, S22]. Consequences:
- Create a **dedicated non-administrator user** (`julos`) for the token. Administrator-only calls (`POST /api/states/...`, `POST /api/events/...`, `GET /api/error_log`, `POST /api/template`, the config check) are then closed to it [S18]. Community reports from 2021 said tokens of non-administrators failed with `401` in some cases [S21]: **zu pruefen** on the current version; if it fails, the fallback is an administrator token plus the allow-list below, which is worse.
- Even a non-administrator can read all states and call services [S21]. **The real boundary is JulOS's own allow-list**: JulOS never forwards a free call, and it only reads and calls what the user selected. A stolen token is still a stolen user account of Home Assistant; that is why the read and control accounts are separate (`julos-read`, `julos-control`) where the user wants control at all, and why locks, alarms and garage doors are never in a control list (below).
- Prefer `https` (the reverse proxy in front of Home Assistant); a plain `http` Home Assistant on the LAN sends the token in clear text: allowed only with the explicit flag and an amber note.
- Revoke: delete the token in the profile, or disable the user.

**Read:**

| Request | Use |
| --- | --- |
| `GET /api/` | health ("API running.") [S18] |
| `GET /api/config` | version, location name, state (field names **zu pruefen**) [S18] |
| `GET /api/states/<entity_id>` | one selected entity: `state`, `attributes.unit_of_measurement`, `attributes.friendly_name`, `last_changed` [S18]; JulOS asks only for the selected ones (at most 40), a few at a time |
| `GET /api/states` | **only for the entity picker** inside the server (can be large: raise the size limit to 2 MB for this call only); the picker returns `entity_id`, friendly name, domain, device class, unit and nothing else, and hides `person`, `device_tracker`, `camera` by default (privacy) |
| `GET /api/services` | which services exist (validation of the control list) [S18] |
| WebSocket `/api/websocket` | later (M7): handshake `auth_required`, then `auth` (`access_token`), answered by `auth_ok` or `auth_invalid`; messages carry an increasing `id`; `subscribe_entities` with an `entity_ids` filter (full state first, then compact changes), or `subscribe_events` with `event_type: state_changed`; `ping` every 30 s [S19]. Frame and rate limits are not documented, so JulOS sets its own (1 MB per frame, reconnect with backoff, one connection per integration). Needs a background service (the first outbound long-lived connection of the modules): a `HomeAssistantLink` in `JulOS.Integrations` |

**Figures:** numeric `sensor` states become metrics with their unit (temperature, power, battery); `binary_sensor` and `unavailable`/`unknown` states become the traffic light (any selected entity unavailable = amber); `update.*` entities show "Updates verfuegbar". The tile example: `Home Assistant . 14 ausgewaehlt . 1 nicht verfuegbar . Wohnzimmer 21,4 Grad . Leistung 340 W`.

**Control stage (`JULOS_HOMEASSISTANT=control`):** `POST /api/services/<domain>/<service>` with a body of `{"entity_id": "<selected>"}` [S18]; the answer lists the changed states (used to verify) and `?return_response` is not used [S18].
- **"Trigger" class, offered by default:** `scene.turn_on`, `script.turn_on`, `automation.trigger`, `button.press` on selected entities. **"Switch" class, opt-in per entity:** `light.turn_on/off/toggle`, `switch.turn_on/off/toggle`, `input_boolean.*`.
- **Never, in code:** `lock.*`, `alarm_control_panel.*`, `cover.*` (garage, doors, windows), `climate.*` except by explicit later decision, `homeassistant.restart`, `homeassistant.stop`, `hassio.*`, `shell_command.*`, `python_script.*`, `rest_command.*`, `script` entities that the user has not selected, any service not on the list. The list is a constant in the adapter, the selection is the user's.
- Way back: scenes and scripts have none by nature (they are the user's own automations) and are marked "Szene ausloesen" without an undo; toggles undo themselves; the confirmation says what is triggered.

**Notification "netwatch-Alarm" as push:** Home Assistant's mobile app exposes a service `notify.mobile_app_<device_id>` (title, message, Android options `channel`, `importance`, `tag`, `group`) [S20]. JulOS calls `POST /api/services/notify/mobile_app_<device>` with `{"title","message","data":{"channel":"netwatch","importance":"high","tag":"netwatch"}}` **only for notify targets the user selected** (the device id is read from `GET /api/services`). Rules: a test button; automatic sending only for transitions of a **named source to red** (3.5), debounced (three polls), at most 6 per hour, repeated alarms use the same `tag` so they replace each other, a global "Benachrichtigungen aus" switch (`notify = off | manual | auto`, default `manual`). The text contains no secret and no log lines. What a "netwatch report" is and in which format it arrives is **not known to this plan** (zu pruefen, see 2.6): the integration only needs "name, level ok/warn/bad, one text line".

**Test plan:** `FakeHomeAssistant` (REST fixtures from the documented endpoints; a scripted WebSocket server for the handshake and `subscribe_entities`); picker never returns more than the five fields and hides the private domains; only selected entities are read; control: `lock.unlock`, `homeassistant.restart` and an unselected scene are refused and audited, the trigger class works, the switch class only when opted in; changed-state verification; notify only to selected targets, debounce, hourly cap and tag; WebSocket: bad token (`auth_invalid`), reconnect backoff, oversize frame dropped; no secret in output.

**Set-up for the user:** create the Home Assistant user `julos` (non-administrator), log in as it once, create a long-lived token in its profile, give it to JulOS (typed, or in a file); optionally a second user for control; in JulOS select the entities, and the scenes or scripts, and the notify device.

### 2.4 Synology DSM (the NAS)

**Benefit: medium (health and capacity of the NAS). Effort: L read (the credential question is the work), none for control in the first plan. Risk: high if done with an administrator account. Default: `off`. Recommended order: last.**

**What is documented.** The login guide [S10]: `GET /webapi/entry.cgi?api=SYNO.API.Info&version=1&method=query` lists APIs with `path`, `minVersion`, `maxVersion`; login is `SYNO.API.Auth` version 6 (recommended; 3 to 7 exist), `method=login`, parameters `account`, `passwd`, optional `session`, `format` (`cookie` or `sid`), `otp_code`, `enable_device_token`, `device_name`, `device_id`, `enable_syno_token`; the answer has `sid`, `did` (device id) and `synotoken` (CSRF token, to be sent as `SynoToken` on later calls); later calls pass `_sid` (or the cookie); `logout` ends the session. Default ports 5000 (HTTP) and 5001 (HTTPS). Error codes at login: 400 wrong account or password, 401 disabled account, 402 permission denied, 403 two-factor code required, 404 wrong code, 406 two-factor enforced, 407 blocked source IP, 408 to 410 password expired; common: 105 "the session does not have permission", 106 timeout, 107 duplicate login, 119 invalid session, 150 IP differs from the login IP [S10]. The guide shows the password in a GET query; **whether `entry.cgi` also takes the login as `POST` (form body), which would keep the password out of the NAS's request logs, is zu pruefen** and is the preferred form.

**What is not documented, and the problem it causes.** The figures we want come from APIs that Synology does not publish in the guide: `SYNO.Core.System` (info, temperature, volumes), `SYNO.Core.System.Utilization` (CPU, memory, disk, network), `SYNO.Storage.CGI.Storage` (method `load_info`, version 1), `SYNO.Core.Package`, the Container Manager's APIs (names **zu pruefen**) are known only from community libraries [S12, S14] and need a runtime `SYNO.API.Info` query for the versions. And the utilisation data **requires membership of the administrators group**; Home Assistant's own integration says administrator rights are needed for the basic statistics, and that an administrator-group account with *all* application and location access denied can still read utilisation and storage through the API but cannot sign in to the web interface or open files [S11, S13]. A non-administrator account will answer `105` for those calls (**zu pruefen on the real NAS, one account at a time, before anything is built**). Official, documented, per-package APIs do exist for File Station and Download Station [S17] (the Download Station PDF is from 2014; a community project reports DSM 7 uses a `SYNO.DownloadStation2.Task` API instead, **zu pruefen**).

**So there are three options, and this plan recommends deciding before M4 (see 5):**

| Option | What it needs | Safety | Gives |
| --- | --- | --- | --- |
| **A. DSM Web API with a dedicated administrators-group account, all application permissions denied, no shared-folder rights, DSM firewall allowing the DSM port only from JulOS's address** | account, two-factor optional (see below), pin of the DSM certificate | The credential is **administrator-grade** on the NAS: the API is the same for every call, so a stolen password could change settings by API (assume yes, zu pruefen). This is the weakest point of the whole plan | CPU, memory, network, temperatures, volumes, disks, packages, uptime |
| **B. SNMP v3, read only** | SNMP service enabled in DSM (Terminal & SNMP) with an SNMPv3 user [S15]; Synology's enterprise tree `1.3.6.1.4.1.6574` with a published MIB guide [S16] (object identifiers for CPU, memory, temperature **zu pruefen** in the MIB files) | SNMP is read-only by design; no API login on the NAS; DSM has no SNMP traps; a third-party report says its GETBULK support is limited (zu pruefen), so prefer GET/GETNEXT | the same health figures, no packages, no actions |
| **C. health only** | nothing new: the existing services widget against the DSM web port (TCP or HTTP answer), plus the host status dot | nothing is stored | up or down, response time |

**Recommendation:** ship **C immediately** (already possible, see 2.6 and the host link in 1.9), decide **B** (a new runtime dependency, an SNMP client, needs the written reason SCOPE rule 4 asks for; a hand-written SNMPv3 is out of the question: crypto) when the user wants real figures, and use **A** only knowingly and with the controls above.

**If A is chosen, the rules:** `https` only (the guide's default HTTPS port 5001; a plain `http` login is refused) with the DSM certificate pinned as for Proxmox; account in the administrators group but with every application and location denied; the password typed by the user into the form (write-only, `SecretBox`); **two-factor:** with it enforced, the user types a one-time code **once** in the setup dialog (never stored), JulOS logs in with `enable_device_token=yes` and `device_name=JulOS`, keeps only the returned `did` (encrypted) and uses `device_id` afterwards [S10]; if DSM invalidates it, the tile says "Anmeldung abgelaufen" and asks again. Session handling: log in lazily, keep the `sid` in memory only, log out after idle, on `106`/`119` log in once more (never in a loop; three failures pause the integration, because a wrong password counts towards DSM's auto-block, error 407).
**The login creates a session and a log entry on the NAS**: this is the single request in `read` that is not strictly read-only, and the documentation says so.

**Read (option A or later B):** `Utilization` (CPU user/system/other, 1/5/15 min load, memory, network), `Storage load_info` (volumes: size, used, status; disks: temperature, SMART; field names **zu pruefen**), `SYNO.Core.System` (model, version, uptime, temperature), a package/Container Manager list (names zu pruefen), Download Station task counts later. The tile example: `NAS . Volume 1: 71 % . 38 Grad . 4 Platten ok . Up 41 Tage`; thresholds: volume 85 % amber, 95 % red; any disk not "normal" red.

**Control: not planned.** Shutdown has no way back from JulOS (Wake-on-LAN needs a broadcast packet that a bridged container does not send, **zu pruefen**), which breaks principle 8; reboot and fan or power settings add risk without much value. If the user later wants "reboot", it comes as one `control` action behind a typed confirmation, only for option A, and is audited. Wake: only if JulOS or a helper can send the magic packet on the LAN (decision in 5).

**Test plan:** `FakeDsm` handler with fixtures built from the guide's documented shapes (`SYNO.API.Info`, `login` success and each error code 400 to 410, `logout`, `105`/`106`/`119`) and, for the undocumented APIs, hand-written minimal fixtures that are clearly marked as reconstructed; pin test with the TLS loopback listener; the OTP is never stored or logged (assert on the store and on captured output); device-id login path; session expiry then one re-login, then pause after three failures; `http` refused; no secret or `sid` in any output or exception; for SNMP (later) a fake agent over UDP with canned responses.

**Set-up for the user (option C):** a services widget for the NAS's web port (template to add: "Generic (HTTP health)" already exists). Option A or B: see the table; the account or SNMP user is created by you in DSM Control Panel (User & Group; Terminal & SNMP), and every password or key is typed by you.

### 2.5 Caddy

**Benefit: medium (already delivers most of it). Effort: S for the next steps, L for any writing. Risk: low for read; high for writing. Default: as today (`off`).**

**State today** ([MODULES.md](MODULES.md)): through the admin API only `GET` on `/config/apps/http/servers`, `/config/`, `/reverse_proxy/upstreams`, `/pki/ca/local`; routes and targets, TCP reachability of the proxy targets, health of the admin API with Caddy's upstream counters, certificates by TLS handshake (warning from 14 days), configuration text with secrets replaced, the tail of the JSON access log; Caddyfile fallback; the Heimnetz row. The Caddy UI is a separate product that owns routes, certificates, DNS and the access portal; JulOS does not write a second model of them. The API facts match the Caddy documentation: `GET /config/[path]`, `GET /reverse_proxy/upstreams` (`address`, `num_requests`, `fails`), `GET /pki/ca/<id>` [S26].

**What is missing, in order of value:**
1. **Metrics (S).** With the `metrics` global option Caddy serves Prometheus text at `/metrics` on the admin API; it has `caddy_http_requests_total`, `caddy_http_request_duration_seconds`, `caddy_http_response_size_bytes` and the gauge `caddy_reverse_proxy_upstreams_healthy` per upstream [S27]. One more fixed `GET` path (`/metrics`, 5 s, 2 MB) and a small text parser give request rates, error share and "upstream unhealthy" without a log file. Do not sum a counter over its `handler` label (double counting) [S27]. This is the best next step and needs no write access.
2. **A short trend (S).** The card shows "now"; a ring buffer of the last 60 samples in memory (not the database) would show a trend. Optional.
3. **TLS expiry of the other integrations (S).** Reuse `CaddyCertificates` for the integrations' HTTPS endpoints (1.4).
4. **Adapter into the shared layer (S, part of M0):** a virtual integration around `CaddyModule`; no behaviour change.

**Writing stays a draft** ([MODULES.md](MODULES.md) "Caddy: control (draft)"): the Caddy UI regenerates the configuration from its own database, so a route added by the admin API is lost at its next reload. Two paths remain, and neither is started before the user decides (5): the **contract with a rebuilt Caddy UI** (token-protected JSON API with dry-run and rollback ids; JulOS calls it for writes), or a Caddy that nothing else reloads. If JulOS ever writes by the admin API: the draft's steps (preview, backup of `/config/`, `@id`-prefixed routes only, counter-check of JulOS's own address with rollback, never JulOS's own route) apply, and the documented **`Etag`/`If-Match` precondition** (a `412` means re-read) is added so a concurrent change is not overwritten [S26].
Not planned: certificate issuing, DNS challenge settings, global options, the access portal.

**Test plan additions:** `/metrics` fixtures (counters, histograms, the upstream gauge; label parsing, a huge body refused); the existing fakes stay.

### 2.6 Other candidates, briefly

| Candidate | Benefit | Effort | Default | Notes |
| --- | --- | --- | --- | --- |
| **AdGuard Home** | medium | S | `read` as a services template | OpenAPI spec in the project; paths are under `/control`; Basic authentication when the web user has a password [S28]. `GET /control/status`, `GET /control/stats` (fields `num_dns_queries`, `num_blocked_filtering`, `avg_processing_time`, top lists) [S28]. Works **today** with the services widget (header `Authorization`, value `Basic <base64>`), only a template is new. Control: `POST /control/protection` (set protection state and duration) [S28] could be a later `control` action with a time limit; its undo is re-enabling. The account is a full AdGuard admin (no scopes): read it only |
| **UniFi Network (gateway, access points)** | medium | M | later | A local API with an `X-API-KEY` header under `/proxy/network/integration/v1` is described by community projects (sites, devices, device statistics) [S31, S32]; **not verified against Ubiquiti documentation, zu pruefen**. Read only. Would give gateway and access point state and client counts; fits the Hosts model (the gateway as a host) |
| **Plex** | low | S | template only | Port 32400; `X-Plex-Token` as header or query; sessions at `/status/sessions` need a token when authentication on the LAN is required; `/identity` might answer without one [S29, S30] (**zu pruefen**). The token is an account token (broad), so keep it out unless wanted |
| **Jularr** | done | none | template | the template in MODULES.md is the model for all "figures from a JSON address" integrations; untested against a running Jularr |
| **Dockhand** | low | S | link tile | Stacks stay in Dockhand. A community docs site describes a `/api` with session-cookie login and `env` parameter [S33], not verified, and a cookie login is a poor fit; use the services widget as a link and a health check only |
| **Network health / "netwatch" report** | medium | S to M | read | Input contract is **not known** (zu pruefen). Proposal: a JSON address or a file (`JULOS_NETWATCH_FILE`) with `{generatedAt, level: ok|warn|bad, items:[{name, level, text}]}`; mapped to a Heimnetz row and, with 2.3, to a push on the transition to red. Until the real format is known, the services widget with a JSON path to its summary field is the stop-gap |

## 3. Security model

### 3.1 Secrets

- Stored only as `SecretBox` ciphertext (AES-256-GCM) with the secret row's id as associated data, so a row cannot be moved to another integration; or as a file read on each use. The key is in `JULOS_SECRET_KEY` or `/data/secret.key`, apart from the database; a database dump alone reveals nothing ([BACKUP.md](BACKUP.md)).
- Write-only API; no log line, error text, audit line, exception message or health detail contains a secret, a token, a `sid`, a one-time code or a response body (tests scan captured output for the test secrets).
- Control secrets are separate from read secrets and loaded only for a confirmed action (1.5).
- The Synology one-time code and the Proxmox/Home Assistant tokens are typed by the user; nobody and nothing (including agents) types them.
- Changing `base_url`, `kind` or the pin deletes the stored secrets of that integration (same rule as a connection's host change in the architecture notes), so a secret cannot be redirected to another target.

### 3.2 Network segments

JulOS only reaches **allow-listed targets**: the integration's own `base_url` host must pass `JULOS_INTEGRATIONS_HOSTS` (fallback `JULOS_ALLOWED_HOSTS`; without a list only private/loopback/CGNAT addresses), checked after name resolution at connect time, never link-local (cloud metadata), `0.0.0.0` or multicast. No redirects (a redirect could point the token elsewhere), no proxy from the environment. A name that resolves to a public address is refused, which also stops DNS rebinding. The Docker socket proxy and Caddy's admin API live on internal networks shared only with JulOS. JulOS's own container needs no route to the Proxmox or NAS management ports other than the ones configured; in a segmented network (VLANs) the firewall rule is "JulOS address to this port only". Proxmox and DSM can additionally restrict by source address on their side (**zu pruefen** the exact setting per product).

### 3.3 Behaviour on errors

- Any failure: the tile shows grey or red with a short code, never a body or stack; the rest keeps working; no automatic retry of a write, ever.
- Reads: backoff as in 1.3; stale snapshot for three intervals.
- An unexpected answer shape (a missing field, an unexpected type) is `invalid`, not a guess; control actions verify their outcome by polling and report "nicht bestaetigt" when the target does not show the expected state.
- Credentials rejected (`auth`): the integration pauses reads after three attempts (to avoid lockouts such as DSM's auto-block) and tells the user.
- Pin mismatch: no request at all.

### 3.4 What does not become read-only for free

The DSM login is a session and a log entry on the NAS. Docker's list is the same API as everything else, only the proxy limits it. Home Assistant's token can do more than JulOS does; only JulOS's list narrows it. Each section says so; none of these is hidden behind the word "read".

### 3.5 What never happens automatically

No integration writes anything by itself, with **two named exceptions**: (1) DDNS updates, as today, only with their own switch; (2) the push notification of 2.3, only to selected targets, only with `notify = auto`, only on a debounced transition, with an hourly cap. Never automatic: start, stop, restart, shutdown, scene or script triggers, DNS changes outside DDNS, Caddy changes, level changes, secret changes, pin changes, or retrying a failed write.

### 3.6 Threat model

| Threat | What the design does |
| --- | --- |
| **Stolen session** | The ceiling is in the environment, so `control` cannot be enabled from a session. Control needs the two-step confirmation (a one-time id) and, for dangerous actions, the typed name; limits per minute; audit shows what happened; read secrets give an attacker only what `read` shows. Pause per integration; sign-out closes sessions (existing). Weak spot, stated plainly: a thief with a session can do what the user can do within the user's selection (start a selected VM, trigger a selected scene). That is why the selections are small and the dangerous domains are excluded in code |
| **Malicious or wrong response** (a compromised or spoofed target) | Responses are parsed into small typed records with size caps; free text from the target (names, status strings) is length-limited and escaped by the front end (it already uses `esc`); no response body is passed to the browser or the log; no field of a response is ever used as a path, a host or a command (the next request is built from the stored address and the validated ids); the pin or the chain check keeps spoofing out; Home Assistant and Synology names are treated as text only |
| **SSRF** | The browser names no target; stored addresses pass the allow-list on save and at connect; no redirects; resolved-address check against private-only unless listed; ids and node names validated by regular expression; Docker paths are built from constants |
| **Token leak** (logs, backups, screenshots, the front end) | Encrypted storage, write-only API, scrubbed logs and audit, scrub test; files for secrets in Compose (`secretFile`); the dump holds ciphertext only; tokens are revocable on each target and the section says how |
| **Role escalation** (a token that can do more than intended) | Least-privilege roles per target: PVEAuditor for read, per-guest ACL and a custom role for power, never `Sys.PowerMgmt` or `VM.Console`; Docker proxy sections `CONTAINERS`, `INFO`, `IMAGES` and `POST=0`, with the hardening self-test; Home Assistant: non-administrator user plus JulOS's own list and a code-level deny list; Synology: the administrator-grade problem is named and option B/C offered; a per-integration read token that cannot be used for writes |
| **Lockout of the operator** | Never act on JulOS's own container, the proxy, its project, or (by default) the reverse proxy and database; DDNS/Caddy rules unchanged; every write has an undo or is not offered; the Not-Aus is available in the environment, not only in the UI |
| **Denial of service by JulOS against a target** | Single-flight polling, caches, per-integration concurrency 4, backoff, DSM login limits, stats only for visible rows |
| **Supply chain** | No new runtime dependency in M0 to M3 (HTTP, JSON and TLS are in the framework); the one candidate (an SNMP client for option B) needs its written reason first |

## 4. Milestones

Numbers are labels, not the order. **Recommended order: M0, M1, M2, M3, M6a, M5a, M5b, M5c, M6b, M4, then M7.** Reasoning: M1 to M3 give most of the value read-only; the Docker socket proxy is the riskiest part, so it comes with the strictest tests early, while nothing can write yet; the Synology step is last because its credential question is unsolved (2.4).

| ID | Scope | Effort | Depends on | Value |
| --- | --- | --- | --- | --- |
| **M0** Shared integration layer | `IIntegration`, `GuardedHttp` extracted from `ServiceChecker` (with pin), `IntegrationStore`, migrations (016, 017), settings and ceilings, secret rows and files, audit, rate limits, pause, generic endpoints, virtual Caddy and DDNS entries, tile and window shell (desktop and phone), Heimnetz row hook, i18n texts, `GET /api/modules` extension | M to L | none | high (enables everything) |
| **M1** Docker read | socket proxy service in the Compose files (example), `DockerIntegration` read (list, info, stats on demand, logs opt-in), tile, window with rows, `julos.*` labels read, hardening self-test code present but control off | M | M0 | high |
| **M2** Proxmox read | `ProxmoxIntegration` read (`cluster/resources`, node status, tasks, storage), pin flow, tile, window, backup health, attention items | M | M0 | high |
| **M3** Home Assistant read | `HomeAssistantIntegration` read (REST per selected entity, picker, units), tile, selection UI | M | M0 | medium to high |
| **M6a** Hosts link (read only) | `host_links` (migration), "Als Host anlegen", amber status dot, `GET /api/hosts` `links`, "VM oeffnen" (connection, terminal, agent), NAS and Home Assistant as hosts with TCP service dot (option C for the NAS), Heimnetz card 2.0 (areas and attention list) | L | M2 (VM), M1 or M3 optional | high |
| **M5a** Docker control | the three verbs, label gate, exclusions, self-test enforced, confirm dialogs, audit view | S to M | M1 | medium |
| **M5b** Proxmox control | `power` token, four verbs, selection, typed-name stop, task following, "Starten und verbinden" | M | M2, M6a | high |
| **M5c** Home Assistant control and notify | trigger and switch classes, deny list, notify targets, test push, `notify = manual` | M | M3 | medium |
| **M6b** Hosts link, writes | start and connect flow, power state in the host dialog | S | M5b | medium |
| **M4** Synology | per the decision: C immediately (template), B (SNMP) or A (DSM API with pin, device token); read only | L | M0; decision 5.4 | medium |
| **M7** Extras | Caddy `/metrics` and trend, Home Assistant WebSocket `subscribe_entities`, automatic alerts (netwatch to push), UniFi read, AdGuard control `protection`, Proxmox console | each S to M | per item | medium to low |

**Acceptance criteria**

- **M0:** all integration endpoints answer `404` with the switch off and `401` without a session; the level can never exceed the environment ceiling (test: a database row with `control` under a `read` ceiling acts as `read`); a pinned test target passes, a different certificate is refused before any request; the allow-list applies at connect (a name resolving to a public address is refused); secrets never appear in any response, log or audit line (scan test); pause stops polling at once; audit rows for every kind of event; the backup dump contains the new tables and restores them; `ServiceChecker` tests unchanged and green; the Heimnetz card shows a virtual Caddy row exactly as before.
- **M1:** with the proxy fake, only the documented request shapes are produced; tile and window show the right counts and states; stats only for visible rows; logs only for opted-in containers within the caps; on the test instance: a real throw-away proxy (`CONTAINERS=1`, `INFO=1`, `IMAGES=1`, `POST=0`) and a few test containers are listed correctly, and `POST` to any path through the proxy is refused (checked by hand with a harmless request).
- **M2:** all three read areas (nodes, guests, tasks and storage) render from fixtures; backup health logic covers ok, failed, missing; a self-signed test certificate with a pin works and a changed one is shown as "Zertifikat geaendert"; verified by hand once against the real Proxmox with the read token (user types the token): counts match the Proxmox web interface.
- **M3:** only selected entities are read; the picker hides the private domains and returns five fields at most; unavailable states show amber; checked by hand against the real instance with a non-administrator token (and the `401` question of 2.3 answered and written down).
- **M6a:** a guest becomes a host in two taps; the dot is amber while a running guest does not answer yet; opening a linked host offers connection, terminal, agent; `GET /api/hosts` stays compatible (old fields unchanged; front-end test with an old-style payload); the card lists at most five attention items, worst first.
- **M5a to M5c:** every action requires the confirmation step, is audited (success, failure, refusal), is refused outside the selection and for excluded targets; level `read` gives `403`; the control secret is not loaded at `read`; the self-test of the Docker proxy blocks control when it fails; a manual run on the test instance: start and shutdown of a throw-away guest, restart of a labelled test container, a test scene, one test push, and the undo of each.
- **M4:** by decision: option C: tile and dot only; option B or A: figures match the DSM Resource Monitor within the sampling interval; an expired session re-logs in once; a wrong password pauses after three tries; no `sid` or code is ever logged.

**Test strategy for all milestones.** Unit and integration tests in `tests/JulOS.Backend.Tests` with fakes in the style of `ModuleFakes.cs` (HTTP handler fakes, a scripted WebSocket and UDP fake where needed, real TLS loopback with a throw-away certificate for pins); request recorders assert the **complete list of requests** an adapter can produce; pure front-end logic in `tests/frontend/integrations.test.mjs` (tile summary, attention list, level and colour rules, format of figures, host dot with amber). Fixtures are written from the documented schemas of the sources below and kept in the test project; a fixture that was reconstructed (not copied from a real answer) says so in a comment. Real systems only by hand on the test instance ([ARCHITECTURE.md](ARCHITECTURE.md) "Test instance"), one integration at a time, with throw-away tokens that are revoked afterwards. What was not verified goes into the "Limits" section of MODULES.md in the same commit, as the working rules ask.

## 5. Open decisions for the user (with a recommendation)

1. **Default levels.** Recommended: everything `off`; the first thing switched on is **Docker `read`**, then Proxmox `read`, then Home Assistant `read`. `control` only per integration, only after the read stage ran for a while, and only for the objects you pick. (A different answer: a single global `read` for all, with control still per integration.)
2. **May JulOS control containers?** Recommended: **yes, but only start, stop, restart, only containers labelled `julos.control=true`, never JulOS itself, the proxy, their project, the reverse proxy or the database**, and only after the socket proxy self-test passes. Stacks (create, update, remove) stay with Dockhand. If you prefer no write path at all, stop after M1.
3. **Proxmox console embedded?** Recommended: **not now.** First read (M2) and power for chosen guests (M5b); the console needs the `VM.Console` privilege and a WebSocket relay of its own; revisit after M6b, possibly reusing JulOS's RFB engine (2.2). Meanwhile "VM oeffnen" uses the guest's own RDP/SSH/agent.
4. **Synology: which credential?** Recommended: **health only now (option C), then SNMP v3 read-only (option B) if you want figures**, accepting one new runtime dependency; the administrators-group API account (option A) only if you want package or container data from the NAS and accept an administrator-grade credential in JulOS. Please also try, once, whether a non-administrator account can read anything useful (error `105` or not); that answer may change the recommendation.
5. **Notifications through Home Assistant?** Recommended: **yes, manual first** (`notify = manual`: a test button and "send this report to my phone"), automatic push only for a named source going red, after the netwatch format is known; targets limited to your phone's notify service. Please also tell us what the netwatch report looks like (file, JSON address, or e-mail), because the plan cannot invent it.
6. **Where do tokens live?** Recommended: typed in the JulOS form (encrypted in the database) for convenience, a file mounted into the container for the strictest setups; either way typed or created by you, never by an agent.
7. **Unencrypted targets.** Recommended: refuse plain `http` for credentials except a private Home Assistant address with an explicit flag; better put Home Assistant behind Caddy with a certificate.
8. **Hosts cap and guests.** Recommended: raise the cap from 20 to 40 hosts and let a guest become a host only when you choose it, instead of a host per guest.
9. **Caddy.** Recommended: add `/metrics` (read), keep writing a draft until the Caddy UI offers the contract in MODULES.md.
10. **Several Docker hosts or several Proxmox clusters.** Recommended: one instance per integration row (the design allows more rows), start with one of each.

## 6. Limits of this plan

Written from public documentation only: no request was sent to any device. Verified in the documents named below: the Proxmox token header and role/ACL syntax, the permissions of the guest status and power calls and the node status, the `cluster/resources` fields, the task list parameters; the DSM login guide (parameters, device id, error codes); Home Assistant REST paths, bearer header, WebSocket handshake and the notify pattern; the Docker stats and logs parameters and the stats formulas; Caddy's admin endpoints and metrics names; the AdGuard OpenAPI `stats`, `status`, `protection` paths. **Not verified** (marked **zu pruefen** above): Proxmox backup job endpoints, fingerprint display, HA-managed guests, fallback nodes; every DSM API other than login and info (utilisation, storage, packages, Container Manager) and the `POST` login; Home Assistant non-administrator tokens on the current version and the `/api/config` fields; the Docker list and `/info` field names, TTY log detection, the proxy's `POST` behaviour in the Tecnativa image, the proxy port; SNMP object identifiers; the UniFi and Plex APIs; Dockhand's API; the netwatch format. The first agent per integration starts by checking those against the real system (read only, by hand) and fixing this page.

## Sources

- [S1] Proxmox VE API wiki: <https://pve.proxmox.com/wiki/Proxmox_VE_API>
- [S2] Proxmox VE user management (roles, tokens, privilege separation, `pveum` examples): <https://pve.proxmox.com/pve-docs/chapter-pveum.html>
- [S3] `GET cluster/resources` (permissions, `type` enum, return fields), pve-manager source: <https://git.proxmox.com/?p=pve-manager.git;a=blob_plain;f=PVE/API2/Cluster.pm;hb=refs/heads/master>
- [S4] Node status and node power (`Sys.Audit`, `Sys.PowerMgmt`), pve-manager source: <https://git.proxmox.com/?p=pve-manager.git;a=blob_plain;f=PVE/API2/Nodes.pm;hb=refs/heads/master>
- [S5] Task list parameters and fields, pve-manager source: <https://git.proxmox.com/?p=pve-manager.git;a=blob_plain;f=PVE/API2/Tasks.pm;hb=refs/heads/master>
- [S6] Guest status and power methods (QEMU), qemu-server source: <https://git.proxmox.com/?p=qemu-server.git;a=blob_plain;f=src/PVE/API2/Qemu.pm;hb=refs/heads/master>
- [S7] Guest status and power methods (LXC), pve-container source: <https://git.proxmox.com/?p=pve-container.git;a=blob_plain;f=src/PVE/API2/LXC/Status.pm;hb=refs/heads/master>
- [S8] Built-in roles (`PVEAuditor`, `PVEVMUser`), pve-access-control source: <https://git.proxmox.com/?p=pve-access-control.git;a=blob_plain;f=src/PVE/AccessControl.pm;hb=refs/heads/master>
- [S9] Proxmox certificate management: <https://pve.proxmox.com/wiki/Certificate_Management>
- [S10] Synology DSM Login Web API Guide (April 2023): <https://global.download.synology.com/download/Document/Software/DeveloperGuide/Os/DSM/All/enu/DSM_Login_Web_API_Guide_enu.pdf>
- [S11] Home Assistant, Synology DSM integration (administrator rights, two-factor): <https://www.home-assistant.io/integrations/synology_dsm/>
- [S12] Community library, system information APIs (names of `SYNO.Core.System*`, `SYNO.Storage.CGI.Storage`): <https://github.com/N4S4/synology-api/blob/master/documentation/docs/apis/classes/core_sys_info.md>
- [S13] Homarr, Synology integration (utilisation requires the administrators group): <https://homarr.dev/docs/integrations/synology/>
- [S14] Issue mentioning `SYNO.Storage.CGI.Storage` `load_info` (mirror, secondary): <https://github.qingwei-s.workers.dev/007revad/Synology_enable_Deduplication/issues/67>
- [S15] Synology, SNMP setting in DSM: <https://kb.synology.com/en-ca/DSMUC/help/DSMUC/AdminCenter/system_snmp>
- [S16] Synology DiskStation MIB Guide: <https://kb.synology.com/en-uk/DG/Synology_DiskStation_MIB_Guide/2>
- [S17] Synology Download Station Web API (2014 PDF): <https://cndl.synology.cn/download/Document/Software/DeveloperGuide/Package/DownloadStation/All/enu/Synology_Download_Station_Web_API.pdf>
- [S18] Home Assistant REST API: <https://contributors.home-assistant.io/core/internals/rest-api/>
- [S19] Home Assistant WebSocket API: <https://contributors.home-assistant.io/core/internals/websocket-api/>
- [S20] Home Assistant Companion app, basic notifications: <https://companion.home-assistant.io/docs/notifications/notifications-basic/>
- [S21] Community thread on long-lived tokens of non-administrators: <https://community.home-assistant.io/t/long-lived-access-token-issued-by-non-admin-does-not-work/370460>
- [S22] Community thread on read-only API access: <https://community.home-assistant.io/t/read-only-access-to-api/522340>
- [S23] Docker Engine API v1.45 (OpenAPI): <https://docs.docker.com/reference/api/engine/version/v1.45.yaml>
- [S24] Tecnativa docker-socket-proxy: <https://github.com/Tecnativa/docker-socket-proxy>
- [S25] linuxserver socket-proxy documentation (`ALLOW_*` with `POST=0`): <https://docs.linuxserver.io/images/docker-socket-proxy/>
- [S26] Caddy admin API: <https://caddyserver.com/docs/api>
- [S27] Caddy metrics: <https://caddyserver.com/docs/metrics>
- [S28] AdGuard Home OpenAPI specification: <https://raw.githubusercontent.com/AdguardTeam/AdGuardHome/master/openapi/openapi.yaml>
- [S29] Plex forum, sessions API and local authentication: <https://forums.plex.tv/t/api-for-get-what-currently-is-playing-on-the-server/36492>
- [S30] Plex support, finding a token: <https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token>
- [S31] UniFi API surface (community documentation, secondary): <https://hyperb1iss.github.io/unifly/architecture/api-surface/>
- [S32] Community UniFi Network API client: <https://github.com/tmcpro/unifi-network-api>
- [S33] Dockhand API overview (community-hosted documentation, secondary): <https://mintlify.com/Finsys/dockhand/api/overview>
