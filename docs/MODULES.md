# Modules: Services, Caddy, DDNS and the "Home network" card

Three optional modules show and (where stated) change things around JulOS, and one card puts them on the start page with a traffic light.
All are **off by default**, have **one switch each** (`off | read | control`), need a signed-in session like every other endpoint (`ApiGuard`), never show or log a secret
and are tested against fakes. The design and the reasoning are in [IDEAS.md](IDEAS.md) ("Desktop modules without a backend yet").
Caddy, the DDNS updater and similar services stay **separate containers**: JulOS does not replace or embed them, it talks to them through their APIs.

## Switches (server environment)

| Variable | Meaning |
| --- | --- |
| `JULOS_SERVICES` | `off` (default), `read` (show and check) or `control` (also add, change, delete services) |
| `JULOS_SERVICES_HOSTS` | optional: the names or addresses the server may ask for services (comma separated). Without it `JULOS_ALLOWED_HOSTS` is used; without both only addresses in the own network (see below) |
| `JULOS_CADDY` | `off` (default) or `read`. `control` is accepted but counts as `read` (a warning in the log): writing is only a draft, see "Caddy: control (draft)" |
| `JULOS_CADDYFILE` | path of a Caddyfile **inside the container**, mounted read only (`- /path/to/Caddyfile:/etc/julos/Caddyfile:ro`) |
| `JULOS_CADDY_ADMIN_URL` | address of the Caddy admin API, scheme, host and port only (`http://caddy:2019`); the paths are fixed. Read directly, no filter service needed |
| `JULOS_CADDY_ADMIN_AUTH_FILE` | optional: file with the credentials for the admin API, one line: `Bearer TOKEN`, `Basic BASE64` or `user:password` (read on every call, never shown) |
| `JULOS_CADDY_TLS_CONNECT` | optional `host:port` the TLS handshakes for the certificate view connect to (SNI is always the domain). Default: the domain itself on port 443 |
| `JULOS_CADDY_LOG_FILE` | optional: Caddy's access log (JSON lines) as a file the JulOS container can read |
| `JULOS_DDNS` | `off` (default), `read` (compare) or `control` (update through a provider) |
| `JULOS_DDNS_HOSTS` | the names to watch **and, with `control`, the only names that may be changed**, comma separated, at most 20 |
| `JULOS_DDNS_FRITZBOX_URL` / `JULOS_DDNS_IP_URL` | where the public address comes from: the router in the LAN (UPnP) or a lookup service that answers it as plain text. The router wins if both are set; there is no built-in service |
| `JULOS_DDNS_CACHE_SECONDS` | how long a measurement is reused, default `60` (10 to 3600) |
| `JULOS_DDNS_PROVIDER` | for `control`: `cloudflare`, `duckdns` or `generic` |
| `JULOS_DDNS_CLOUDFLARE_ZONE`, `JULOS_DDNS_TOKEN_FILE` | Cloudflare: the zone id and a file with the API token (also the DuckDNS token) |
| `JULOS_DDNS_UPDATE_URL`, `JULOS_DDNS_UPDATE_AUTH_FILE` | generic: update address with `{host}` and `{ip}` (may contain a token and is never shown), optional file with `user:password` or `Bearer ...` |
| `JULOS_DDNS_AUTO`, `JULOS_DDNS_AUTO_SECONDS` | `on` updates automatically (own switch, default `off`, needs `control` and a provider), at most every 5 minutes (smaller values are raised to 300) |

An unknown value keeps a module off. Invalid addresses, names or files are dropped with a log line (without the value). The compose files pass all of these through
(empty means off or unset), `.env.example` has commented examples. When a module is off its endpoints answer `404` and the interface shows no window (desktop: no dock icon, no launcher or menu entry)
and no card (phone). `GET /api/modules` tells the interface which modules are on: `{"caddy":"off|read","ddns":"off|read|control","services":"off|read|control"}`; it reveals no other setting.

## Rights

- Everything needs a session. There is one account type: any signed-in user may use what the switch allows.
- **Services** belong to the user who created them; nobody sees or changes the services of another user. `read` cannot change anything (`403`).
- **Caddy** is read only for everyone. **DDNS `control`**: every signed-in user can press "Update now" (limited to the names in `JULOS_DDNS_HOSTS`, 3 presses per minute, 30 seconds between writes). If that is too much, leave `JULOS_DDNS` at `read`.
- No endpoint takes an address, name or target from the browser. What is asked comes from the server settings or from what a signed-in user saved earlier (services).

## Endpoints

All need a session; reads are rate limited per user; writes need the `X-JulOS` header and a same-origin request like every other change.

| Path | Answer |
| --- | --- |
| `GET /api/modules` | which modules are on (see above) |
| `GET /api/modules/services` | `{services:[{id,name,icon,openUrl,healthUrl,metricsUrl,metrics[],intervalSeconds,headerName,hasKey,template}], canEdit, keyStore, max}` (never the key) |
| `GET /api/modules/services/status` | `{statuses:[{id,state,ms?,httpStatus?,checkedAt,error?,metrics:[{label,unit,format,number?,text?}]}]}`; `state` is `up`, `down` or `unknown` |
| `POST /api/modules/services/{id}/check` | the same for one service, ignoring the interval |
| `POST /api/modules/services`, `PUT .../{id}`, `DELETE .../{id}` | `control` only. `apiKey`: omitted keeps the stored key, `""` removes it |
| `GET /api/modules/caddy/routes` | `{source, routes:[{domain, path?, kind, targets[], detail?}], warnings[]}` |
| `GET /api/modules/caddy/reachability` | `{upstreams:[{target, state, ms?}], checkedAt}` |
| `GET /api/modules/caddy/health` | `{state: up|down|unknown, ms?, upstreams:[{address,requests,fails}], checkedAt, warning?}` |
| `GET /api/modules/caddy/certificates` | `{certificates:[{domain,state,issuer?,notAfter?,daysLeft?,trusted,error?}], ca?, checkedAt}` |
| `GET /api/modules/caddy/config` | `{source, text, warning?}`, the configuration without secrets |
| `GET /api/modules/caddy/log` | `{available, entries[], hosts[], total, warning?}` |
| `GET /api/modules/ddns/status` | `{state, publicIp?, source?, checkedAt, hosts:[{host,state,addresses[]}], message?}` |
| `GET /api/modules/ddns/control` | `{mode, provider?, canUpdate, auto, autoSeconds, lastAt?, lastOutcome?}` |
| `POST /api/modules/ddns/update` | `control` only; `{outcome, changes:[{host,type,oldIp?,newIp,ok,message}], at}` |
| `GET /api/modules/ddns/history` | `{entries:[{at,host,recordType,oldIp?,newIp,trigger,ok,message}]}` (last 50) |

## Services (widgets)

You add a service (name, icon, address to open, optional health address, optional JSON figures address with a mapping "field path -> label and unit", poll interval, optional API key
sent as a header). The desktop shows them in the window **Widgets** (a tile grid), the phone on the **Dienste** page. A tile is green (health answers 2xx), red (error, timeout or non-2xx) or grey (unknown or nothing to ask), with the response time and the figures.

- **Only saved addresses are asked**, by the server, with `GET`: no redirects, no proxy, 4 s per request, answer at most 256 KB, at most 8 requests at once. The browser never names an address.
- **Allow-list.** With `JULOS_SERVICES_HOSTS` (else `JULOS_ALLOWED_HOSTS`) only the listed names may be saved and asked. Without a list only addresses in the own network are asked (private, loopback, CGNAT such as `100.64.x`), checked **after name resolution** at connect time, so a name that resolves to a public address is refused ("address not allowed"). Link-local addresses (including the cloud metadata address), `0.0.0.0` and multicast are **never** asked, even when listed. The "address to open" is only a link for your browser and is not asked.
- **Poll interval** 10 to 3600 s (default 60). There is no background job: a service is asked when someone looks, at most once per interval; the tiles refresh every 30 s while the window or page is open.
- **The API key** is stored encrypted with the server key (`JULOS_SECRET_KEY` or the key file in `JULOS_DATA`, AES-GCM, the service id as associated data), never returned and never logged. Without a server key no key can be saved (the form says so). It is sent as the header you name (`Authorization`, `X-Api-Key`, ...) **only to the figures address**, and to the health address only if that has the same origin or there is no figures address. Put keys in the header, not in the address.
- **Field paths**: `a.b[0].c` or `a.b.0.c`; a final `#` counts the entries of a list or object. Numbers can be shown plain, as sizes (`KB`, `MB`), as a rate (`MB/s`) or as percent; strings and booleans are shown as text; objects and `null` are no figure. Up to 12 figures per service, 30 services per user.
- **Templates** (fill the form from the address of the service):
  - *Jularr (queue)*: health `GET /health/ready` (anonymous), figures from `GET /api/v1/admin/activity/summary`: `activeCount`, `bytesPerSecond`, `importCount`, `errorCount`. *Jularr (library)*: `GET /api/v1/admin/system/dashboard`: `works`, `files`, `openRequests`. Jularr accepts a device token as `Authorization: Bearer <token>` (the one the apps use); use a token of an account that may only read. **This was derived from reading Jularr's source and not tried against a running Jularr**; if a field name differs, change the path in the form.
  - *Caddy UI (health)*: `GET /health/ready` of the Caddy UI (port 8098 in the stack).
  - *Generic (HTTP health)*: `GET /` must answer 2xx.

## Caddy (read)

JulOS reads Caddy through its **admin API directly**, no filter service. Only `GET` requests on four fixed paths are ever sent (`/config/apps/http/servers`, `/config/`, `/reverse_proxy/upstreams`, `/pki/ca/local`), no redirects, no proxy, 5 s, answer at most 2 MB.
Without an admin address the (read only) Caddyfile is used for the routes; with both, the running configuration wins and the Caddyfile is the fallback with a warning.

**Setting up the admin API.** Caddy only listens on `localhost:2019` by default and only accepts requests whose `Host` matches its allowed `origins`. For JulOS in another container put the admin API on a network that only Caddy and JulOS share (never publish port 2019) and allow the name JulOS uses:

```
{
	admin 0.0.0.0:2019 {
		origins caddy:2019
	}
}
```
`JULOS_CADDY_ADMIN_URL=http://caddy:2019`. **The admin API has no login of its own and can replace the whole configuration;** JulOS only sends `GET`, but anyone who can reach that address can write. Keep it on the internal network. If you protect it (Caddy can require mTLS or you put a proxy in front), give JulOS the credentials with `JULOS_CADDY_ADMIN_AUTH_FILE`.

- **Routes and targets:** domain, optional path, kind (`proxy`, `files`, `redirect`, `respond`) and target, nothing else from the configuration. Targets lose `user:password@`, query and fragment. `{env.NAME}` is shown as written, never resolved. "Check reachability" makes a plain TCP connect to the proxy targets **in the configuration just read** (1.5 s each, at most 40, 8 at a time, 12 requests per minute); never to unix sockets, placeholders or link-local/unspecified addresses. The server connects from inside its container, so use addresses that are valid from there. The Caddyfile parser understands the common shape (site blocks, `reverse_proxy`, `php_fastcgi`, `file_server`, `redir`, `respond`, `handle`, `handle_path`, `route`); `import`, snippets and named matchers are not followed.
- **Health:** whether the admin API answers (and how fast), plus what Caddy counts per upstream (`/reverse_proxy/upstreams`: requests and failures). This is the container health as far as the API can tell; JulOS never talks to Docker.
- **Certificates:** for every domain in the routes (not wildcards, addresses, `.local`, `.internal`) one TLS handshake with the domain as SNI (to `JULOS_CADDY_TLS_CONNECT` or the domain on 443), at most 40 domains, 6 at a time, kept for 5 minutes. Shown: issuer, expiry date and days left, whether the chain is trusted by this server, or the TLS error (timeout, refused, handshake). **Warning from 14 days** before expiry; expired and error count as red. Caddy's internal CA (`tls internal`) issues short-lived certificates, so those domains always show "expires soon"; that is expected. If the admin API names an internal CA (`/pki/ca/local`), its name and the expiry of the intermediate are shown (never the certificate).
- **Configuration text:** the running configuration (or the Caddyfile) as text with secrets replaced by `***`: every field whose name contains pass, secret, token, key, auth, credential, hash, salt, cookie, bearer, private, api, cert, email, account, challenge or user, everything under `headers`, and `basic_auth` style blocks of a Caddyfile. It errs on the side of hiding too much. At most 256 KB.
- **Access log:** if `JULOS_CADDY_LOG_FILE` points to Caddy's JSON access log (shared volume, mounted read only), the tail (last 512 KB) gives the newest 100 entries and counters per host (requests, 4xx, 5xx). Only time, host, method, path **without query**, status, duration and size are taken; no visitor addresses, no headers. Caddy creates the file with mode 600 by default; add `mode 0644` to its `output file` block so the JulOS user can read it. Rotation is handled by Caddy; JulOS reads the current file only.

### Caddy UI and what JulOS does not touch

A Caddy UI (a separate management container with its own PostgreSQL) manages routes, certificates, DNS/DDNS, bot protection, a login portal with groups and the live log, and it generates the Caddy configuration from its own database. JulOS is **not** a replacement and does not write a second model of the same data. JulOS reads Caddy and its health; the Caddy UI stays the place where routes are managed. Function by function:

| Caddy UI function | In JulOS |
| --- | --- |
| Domains and routes (managed, custom, quick create) | **taken over as a view only**: routes, targets, reachability. Not edited |
| Certificates (modes, wildcard, provider) | **taken over as a view**: expiry, issuer, errors, warning from 14 days. Not issued or changed |
| DDNS (provider adapter, credentials in the UI or by reference) | **comparison only** (`read`). A second writer for the same names would fight with the Caddy UI. Use `JULOS_DDNS=control` only for names the Caddy UI does not update |
| Provider credentials, DNS management | **not**, stays in the Caddy UI |
| Live log and analytics | **light version**: tail of the access log with counters per host and status. No database, no history |
| Health checks of targets | **taken over** as reachability and Caddy's own upstream counters |
| Access portal, groups, login | **not**, stays in the Caddy UI (it has its own users and database) |
| Bot protection and IP blocks | **not** |
| Import/export, backups, cutover | **not** |
| Write access to routes | **later / draft** (below) |

### Caddy: control (draft, not built)

`JULOS_CADDY=control` is reserved; today it counts as `read`. The intended small, strict write stage, **only through the admin API**, nothing else: add or remove **one route for a new service** at a time.

1. **Preview:** JulOS builds the route JSON (host, upstream) and shows the diff of the route list before and after. Nothing is sent before the user confirms.
2. **Backup:** `GET /config/` is stored (without any redaction, in `JULOS_DATA`, mode 600, the last 20 kept) before every change.
3. **Apply:** `POST` the route into the server's `routes` array with an `@id` (`julos-route-<name>`) so it can be removed with `DELETE /id/julos-route-<name>`; only routes with that prefix may be removed.
4. **Counter-check and rollback:** after the change JulOS asks its own public address (`/api/health`, 3 tries over 15 s). If it does not answer, it loads the backup (`POST /load`) and reports the rollback. If the admin API itself stops answering the change is reported as unverified.
5. **Never:** the route for JulOS itself (matched by the JulOS host name, by an upstream equal to JulOS, or by the `@id` of an existing route), TLS, authentication or global options.

**Why it is only a draft:** the Caddy UI regenerates the Caddyfile and reloads Caddy from its own database. A route added through the admin API is lost at its next reload, and JulOS cannot know when that happens. A write stage is only useful for a Caddy that nothing else reloads, or once the Caddy UI offers the contract below.

**Contract JulOS would expect from a (re)built Caddy UI** (proposal, nothing of it exists yet):
- `GET /health/ready` and `GET /health/live`, anonymous, `200` or `503` (this exists today; the "Caddy UI" service template uses it).
- A token-protected JSON API under `/api/v1`: `GET /routes` -> `{routes:[{id, host, path?, upstream, enabled}]}`; `POST /routes` and `DELETE /routes/{id}` for **new service routes only** with a `dryRun=true` option that returns the diff; every change returns the id of a stored backup; `POST /routes/rollback/{backupId}`. Auth: `Authorization: Bearer <token>`, the token read by JulOS from a file.
- The access log as a shared file in Caddy's JSON format, or `GET /api/v1/stats?window=1h` with counters per host and status class.
With that, JulOS would call the Caddy UI instead of the admin API for writes and would still read through the admin API.

## DDNS

- **Read (`JULOS_DDNS=read`)**: compares the current public address with what the normal resolver of the server returns for each listed name. `Synchron`, `Nicht synchron`, `Name nicht auflösbar` (no records), `Unbekannt` (no address). The address comes from the router (`JULOS_DDNS_FRITZBOX_URL`: one fixed UPnP call `GetExternalIPAddress`; a FRITZ!Box needs "Statusinformationen über UPnP übertragen") or from a lookup service (`JULOS_DDNS_IP_URL`: it learns your address, which is why there is no default). Measured only when someone looks, at most once per `JULOS_DDNS_CACHE_SECONDS`. A private or CGNAT address is flagged. A name is compared in the address family of the public address (IPv4 by default; an IPv6 address makes it AAAA).
- **Control (`JULOS_DDNS=control` and a provider)**: "Update now" in the window or sheet, and optionally automatically (`JULOS_DDNS_AUTO=on`, a separate switch). Rules, all tested:
  - only the names in `JULOS_DDNS_HOSTS`; only the `A` record for an IPv4 address or the `AAAA` record for an IPv6 address;
  - only when the record differs from the public address (a readable provider decides by its own content; otherwise the resolver and the last address JulOS wrote), never with a private or unknown address;
  - **never creates or deletes a record**: a missing record is an error, several records for one name are refused;
  - automatically at most every 5 minutes, 30 seconds between button presses; after a failed write a name is not retried automatically for an hour;
  - every change (old, new, time, button or automatic, result) goes to the history table (`ddns_history`, migration 013, the last 200 rows), shown in the window and the sheet; the last changes also appear in the phone's "Aktivitäten" list.
- **Providers** (a slim interface, `IDnsProvider`: read the records, change an existing one):
  - **Cloudflare**: `JULOS_DDNS_PROVIDER=cloudflare`, `JULOS_DDNS_CLOUDFLARE_ZONE` (zone id) and `JULOS_DDNS_TOKEN_FILE`. Create an **API token limited to this one zone** with the permission "Zone / DNS / Edit" only. JulOS does `GET /zones/{zone}/dns_records?type=&name=` and `PATCH /zones/{zone}/dns_records/{id}` with only `content` (proxy flag and TTL stay). Never `POST` or `DELETE`.
  - **Generic update URL**: `JULOS_DDNS_UPDATE_URL=https://dyn.example.org/update?host={host}&ip={ip}` (+ optional `JULOS_DDNS_UPDATE_AUTH_FILE`). `GET`; 2xx and an answer that does not start with a DynDNS error code (`badauth`, `nohost`, `abuse`, ...) counts as success. The URL can hold a token and is never shown or logged.
  - **DuckDNS**: `JULOS_DDNS_PROVIDER=duckdns`, token in the file; names must be `name.duckdns.org`.
- **If a Caddy UI or another tool already updates the same names** (the Caddy UI has its own DDNS with its own provider adapter), keep `JULOS_DDNS=read` for them: JulOS then shows whether that tool did its job. It has no adapter for every provider and no API of the Caddy UI to call, and two writers for the same name overwrite each other.

## The "Heimnetz" card

On the start page (desktop dashboard and phone home) one card shows **Services** (running or not, which ones are down), **Caddy** (state, number of domains, certificates), **DDNS** (in sync or not) and an overall traffic light: green = all good, amber = needs attention (a certificate expires within 14 days, DDNS out of sync), red = something is down (a service does not answer, Caddy does not answer, a certificate is expired or broken), grey = nothing to report. The worst wins; grey does not count. Only modules the server switched on appear, and a module that does not answer turns only itself grey. A tap opens the window or sheet of that module. The card refreshes every 60 s while the page is open.

## Limits and what is not verified

- Everything is tested against fakes (Caddy admin API, resolver, router, lookup service, DNS providers, services) and with plain sockets on loopback, including a real TLS handshake with a throw-away certificate. A **throw-away Caddy container** (`caddy:2`, admin API on a private network) was used for a hand test of routes, health, certificates, configuration and the access log. **Not tested against the real Caddy, the Caddy UI, Cloudflare, DuckDNS, a real router, Jularr or a phone.**
- The Jularr paths and field names were read from its source; the device-token header was not tried.
- The Caddyfile parser is a reader for the common shape, not Caddy's own parser.
- Reachability says "something accepts TCP there", not that the service works. A TLS handshake says what certificate the connected address presents, which for `JULOS_CADDY_TLS_CONNECT` is what that address presents.
- Two simultaneous sessions of one user share the rate limits.
- Redaction is by name. A secret stored under an innocent name in a Caddy configuration would be shown; the configuration view is for people who may see the Caddy configuration anyway.

## Tests

`tests/JulOS.Backend.Tests`: `ServiceTests` (rules, paths, allow-list, checks, loopback socket), `ServiceHttpTests` (session, switches, key encryption in the database, other users, only saved addresses), `CaddyReadTests` (redaction, log, certificates with a real handshake, fixed requests, credentials from a file), `DdnsUpdateTests` (updater rules, provider adapters, settings), `ModuleTests`, `ModuleHttpTests`, `ModuleFakes`. `tests/frontend/modules.test.mjs` (labels, templates, formats, traffic light logic).

## Open questions for you

(1) Which names should JulOS be allowed to update with `JULOS_DDNS=control`, if any, given that another tool may already do it? (2) Should anything other than a Caddy UI contract ever write to Caddy? (3) Do you want Docker container state here (needs the Docker socket and its own switch, not built)? (4) For services: which ones besides Jularr should get a template?
