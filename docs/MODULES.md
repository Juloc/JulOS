# Modules: Caddy and DDNS (first stage, read only)

Two optional modules show things you run next to JulOS. Both are **off by default**, **read only** and need a signed-in session like every other
endpoint. The design and the reasoning are in [IDEAS.md](IDEAS.md) ("Desktop modules without a backend yet"); this page says what is built.
Docker is not part of this stage.

## Switches (server environment)

| Variable | Meaning |
| --- | --- |
| `JULOS_CADDY` | `off` (default), `read` |
| `JULOS_CADDYFILE` | path of a Caddyfile **inside the container**, mounted read only (`- /path/to/Caddyfile:/etc/julos/Caddyfile:ro`) |
| `JULOS_CADDY_ADMIN_URL` | optional: address of the Caddy admin API, or better of a filter in front of it (see below). Scheme, host and port only; the path is fixed |
| `JULOS_DDNS` | `off` (default), `read` |
| `JULOS_DDNS_HOSTS` | the names to watch, comma separated, at most 20 (`home.example.org,nas.example.org`) |
| `JULOS_DDNS_FRITZBOX_URL` | option A for the public address: the router in the LAN (`http://192.0.2.1:49000`) |
| `JULOS_DDNS_IP_URL` | option B: an address-lookup service that answers the address as plain text (`GET`). There is no built-in service. If both are set, the router wins |
| `JULOS_DDNS_CACHE_SECONDS` | how long a measurement is reused, default `60`, at least `10`, at most `3600` |

`control` is accepted as a value for later (write access, a separate step), but **does not exist yet** and counts as `read`; the server log says so at start.
An unknown value keeps the module off. Invalid addresses or names are dropped with a log line (without the value). The compose files pass all of these through
(empty means off or unset), `.env.example` has commented examples.

When a module is off, its endpoints answer `404` and the interface shows no window (desktop: no dock icon, no launcher or menu entry) and no card (phone).
`GET /api/modules` (session needed) tells the interface which modules are on: `{"caddy":"off|read","ddns":"off|read"}`; it reveals no other setting.

## Endpoints

All are `GET`, take **no input** (what is checked comes from the server settings, never from the browser), need a session, and are rate limited per user.

| Path | Answer |
| --- | --- |
| `/api/modules/caddy/routes` | `{source, routes:[{domain, path?, kind, targets[], detail?}], warnings[]}`; `source` is `admin`, `caddyfile` or `none` |
| `/api/modules/caddy/reachability` | `{upstreams:[{target, state, ms?}], checkedAt}`; `state` is `up`, `down` or `skipped` |
| `/api/modules/ddns/status` | `{state, publicIp?, source?, checkedAt, hosts:[{host, state, addresses[]}], message?}` |

## Caddy

- **Source.** The Caddyfile (parsed by JulOS, read only, at most 1 MB) and/or the **running configuration** over the admin API: exactly one request,
  `GET /config/apps/http/servers`, no redirects, no proxy from the environment, 5 s limit, answer at most 2 MB. With both set the running configuration is shown;
  when it cannot be read the Caddyfile is shown instead, with a warning. JulOS never sends anything else to Caddy and never changes it.
- **The admin API has no login** and can replace the whole configuration. Do not point `JULOS_CADDY_ADMIN_URL` at it directly if you can avoid it: put a tiny
  filter in front that lets only `GET /config/apps/http/servers` through (a second Caddy with a method and path matcher, see IDEAS.md), or use the Caddyfile only.
  The Caddyfile variant needs no new service and is the safer default.
- **What is shown:** domain, optional path, kind (`proxy`, `files`, `redirect`, `respond`) and target. That is all that is ever taken from the configuration, so
  headers, `basic_auth`, `tls` and DNS-challenge credentials, certificates and response bodies cannot reach the browser. On top, targets lose `user:password@`, query and fragment.
  Environment placeholders such as `{env.TOKEN}` are shown as written, never resolved.
- **What the Caddyfile parser understands:** site blocks (also one site without braces), `reverse_proxy` (with `to`), `php_fastcgi`, `file_server` with `root`, `redir`,
  `respond`, nesting through `handle`, `handle_path`, `route` up to depth 8, at most 500 rows. Not understood: `import`, snippets (skipped), heredocs, named matchers (only
  path matchers are shown). What is not understood is missing from the list, never guessed; a site with nothing recognised shows as `other`. For anything complex use the admin API.
- **Reachability:** "Erreichbarkeit prüfen" makes a plain TCP connect (nothing is sent) from the server to the `proxy` targets that are **in the configuration just read**, 1.5 s each,
  at most 40 targets, 8 at a time, 12 requests per minute per user. The browser cannot name a target, so this cannot be used as a port scanner. Never probed: unix sockets,
  placeholders, port ranges, and addresses that are never a service in the own network (link-local, which includes the cloud metadata address, `0.0.0.0`, multicast), also after name resolution.
  Because the server connects from inside its container, a target such as `localhost:3000` means the JulOS container, not the Caddy host; use addresses that are valid from there.

## DDNS

- **No token, no writing.** JulOS only compares: the current public address against what the normal resolver of the server returns for each listed name.
  `Synchron` = every name resolves to the address, `Nicht synchron` = at least one does not, a name without records is `Name nicht auflösbar`, no address is `Unbekannt`
  (the names are still resolved and shown). The window shows the time of the last check.
- **Public address, option A: the router** (`JULOS_DDNS_FRITZBOX_URL`). One fixed UPnP call (`GetExternalIPAddress`, `POST /igdupnp/control/WANIPConn1`); nothing leaves your network.
  A FRITZ!Box answers without login only when "Statusinformationen über UPnP übertragen" is on (Heimnetz > Netzwerk > Netzwerkeinstellungen > weitere Einstellungen).
  Other routers that speak UPnP-IGD may work at the same path; this was not tested.
- **Option B: a lookup service** (`JULOS_DDNS_IP_URL`). The server calls it on every measurement, so it learns your address and the service learns the server's address; that is why there is no default.
- **Measured on request**, at most once per `JULOS_DDNS_CACHE_SECONDS`; there is no background job, so nothing leaves the server while nobody looks.
- A private or CGNAT address from the source (`10.x`, `100.64.x` ...) is flagged: the connection is probably behind another router and the comparison means little.
- IPv4 only in practice: a name is compared against the address family of the public address. IPv6 is an open question (see below).

## Limits and what is not verified

- Everything was tested against fakes (Caddy admin API, resolver, router, lookup service) and with plain sockets on loopback. **Not tested against a real Caddy, a real router or the phone.**
- The Caddyfile parser is a reader for the common shape, not Caddy's own parser; adapters, `import` and exotic directives are not followed.
- The reachability check says "something accepts TCP connections there", not that the service works.
- Two simultaneous sessions of one user share one rate limit.

## Open questions for you (from IDEAS.md; the defaults above are the safest answers)

**Caddy.** (1) Does Caddy run in Docker or on the host, and is it the same Caddy that fronts JulOS? That decides the path of the mounted Caddyfile or the address of the admin filter.
(2) Do you edit the Caddyfile by hand? Only then is the Caddyfile reading enough; for a later write stage API changes are lost on the next reload unless you let JulOS own a generated part.
(3) Is the read-only list enough for the first version? (4) For writing later: which zones may JulOS create names in, and which upstream networks are allowed? (5) Do you want logs, and where are they? (Not built: no log reading.)

**DDNS.** (1) Which provider and which names (`JULOS_DDNS_HOSTS`)? (2) IPv4 only (default) or IPv6 too? (3) Public address from the router (which one) or from a lookup service?
(4) Should JulOS ever change DNS, or only show whether it is in sync? (5) If it changes DNS: automatically or on a button press? (6) Is it acceptable that anyone holding a valid JulOS session can trigger an update (limited to the listed records)?

## Tests

`tests/JulOS.Backend.Tests/ModuleTests.cs` (parsers, secrets, settings, fixed requests, probing only listed targets, DDNS states and cache, router and lookup answers, redirects),
`ModuleHttpTests.cs` (session, `404` when off, no input, read only, rate limit; needs the test database), `ModuleFakes.cs` (the fakes), `tests/frontend/modules.test.mjs` (labels and texts).
