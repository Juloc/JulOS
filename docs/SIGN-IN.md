# Sign-in centre (JulOS as OpenID Connect provider)

Status: built, **not released**, not yet reviewed by a second pair of eyes (see "What a security review should look at first"). It is the first step of
[BASE.md](BASE.md#sign-in): JulOS becomes the one place where a person signs in, and every app of the family signs in **through JulOS** with the standard
OpenID Connect flow. Nothing of this changes how JulOS itself is used; without a registered app none of it is reachable in a useful way.

## What it is, and what it is not

- **Standard OpenID Connect, authorization code with PKCE (S256) only.** No implicit flow, no hybrid flow, no password grant, no client credentials, no refresh tokens,
  no dynamic registration, no passkeys, no Google sign-in (those are later steps).
- **Our own small code on Microsoft's token library** (`Microsoft.IdentityModel.JsonWebTokens`, signing and checking of the JWTs). No ready-made OIDC server library.
  The code lives in `Logic/OidcRules.cs`, `Logic/OidcTokens.cs`, `Services/Oidc*.cs`, `Data/Oidc*.cs` (backend) and `Oidc/` (endpoints and the pages).
- **Every app keeps its own session.** The token is only the proof, once, that JulOS vouched for the person; the app then makes its own session (BASE.md: updates of
  the app sign nobody out). **Signing out at JulOS ends the app sessions** through back-channel logout (below).
- **People live in JulOS**, with **one role per app**. The first account (the owner) has `admin` in every app without an entry; anyone else needs an entry, otherwise
  there is **no token** (an error page in JulOS, never a redirect with a success).

## The flow

```
App                              Browser                         JulOS
 |  redirect to /oidc/authorize ---->|-------------------------------->|  checks the app and the exact return address
 |                                   |                                 |  session in the cookie? no -> sign-in page; yes -> role? no -> error page
 |<------ redirect to the app: ?code=...&state=...&iss=... -----------|  code: 60 s, once, bound to app, address, PKCE challenge, JulOS session
 |  POST /oidc/token (secret, code, code_verifier) ------------------->|  checks all of it, issues the ID token (5 min) and an access token (5 min)
 |<----------------------------------- id_token, access_token ---------|
 |  checks the ID token (signature from /oidc/jwks, iss, aud, exp, nonce), creates ITS OWN session
 ...
 |<-------- POST logout_token (back channel) --------------------------|  when the JulOS session ends, for every app that got a token for it
```

Without a JulOS session `/oidc/authorize` shows a sign-in page of JulOS (the same checks as the normal sign-in: lockout, second factor, audit) and then continues with
the request. With `prompt=none` it answers `login_required` at once and never shows anything.

**The cookie is `SameSite=Strict`**, and a browser does not send it when the app (another site) sends the person to JulOS. So the first request of such a visit arrives
without it. `/oidc/authorize` then answers with a small page that loads the same address once more **from JulOS itself** (`<meta refresh>`, no script), and now the
cookie comes along. `jb=1` marks the second request, so there is no loop. Checked in a real browser (cross-site navigation between `localhost` and `127.0.0.1`).

## Endpoints (the public ones, one by one)

The OIDC endpoints are the **reasoned exception** to "every API needs a session": an app without a session has to reach them. Each protects itself.
`RouteSecurityTests` lists them; a new public route fails that test until it is entered there on purpose.

| Endpoint | Protection |
|---|---|
| `GET /.well-known/openid-configuration` | public, read only: issuer, endpoints, what is supported, and **`julos_display_name`** (see "The name of the centre"); 120 requests per minute and address |
| `GET /oidc/jwks` | public, read only: **public** parts of the signing keys (`kty`, `crv`, `x`, `y`, `kid`), never a private part |
| `GET /oidc/authorize` | the app and the **exact** return address must be registered (otherwise an error page, **never a redirect**); `response_type=code` only; PKCE `S256` **required**; `state` required; `nonce` supported; scope `openid` required, `profile` optional, other scopes are ignored; `prompt=none` and `login`; `max_age`; `response_mode`: the answer always comes as a query (`form_post`, which Microsoft's handler asks for by default, is answered as a query too and the handler accepts that; fragment and JWT modes are refused); rate limit 120 per minute and address |
| `POST /oidc/token` | form body only; the app signs in with `client_secret_basic` or `client_secret_post` (never both); code, redirect address and PKCE verifier must match; failed app sign-ins lock the address for a growing time (10 failures), **but the lock only turns away wrong secrets**: the right secret is always let through (otherwise whoever shares an app's outgoing address could block its token exchange); 60 requests per minute; `Cache-Control: no-store` |
| `GET`/`POST /oidc/userinfo` | the access token in the `Authorization: Bearer` header only (never in the address); valid only while the JulOS session lives, the app is active and the person still has a role |
| `GET`/`POST /oidc/end-session` | RP-initiated logout: with a valid `id_token_hint` that was issued **for the session being ended and this app** (its `sid` is the one this session got from the app; an old token, a token of another session of the same person and a logout token do not count) it ends the session at once; otherwise it asks first (a form posting to itself; see "The sign-out form" for what is checked). It returns to `post_logout_redirect_uri` only if the hint is valid **and** the address is registered exactly. Without that, the page says "signed out" and, if the request names a `client_id` of an active app with **exactly one** registered address after sign-out, adds a link "Back to <app>" to that registered address (a link, never a redirect; never an address from the request) |

There is no CORS: a browser may not read any of these from another site. The pages (sign-in, no access, error, sign-out) cannot be framed
(`X-Frame-Options: DENY`, `frame-ancestors 'none'`, set for every answer of the server) and use no inline script (CSP).
Errors for an unknown app, a wrong return address, a blocked app look **the same** (one page, no hint whether the app exists); `invalid_client` at the token endpoint is the same
for an unknown app and a wrong secret, and a hash is compared in both cases (same time).

## The sign-out form

The question "Sign out of JulOS?" (an app that sends no valid hint) is a form that posts to `/oidc/end-session`. Two things had to be right:

- **Why it failed in Chromium (0.26.0 and before).** The server sends `Referrer-Policy: no-referrer` on every answer. With that policy Chromium sends **`Origin: null`** on a form
  POST to the same site, and the check "`Origin` equals this host" refused it: the page ended in "Sign-in not possible" and the session stayed. Found by the first real trial of an app.
- **The fix, and why this one.** (1) The page carries a **one-time form token** (`form_token`, hidden field): 256 bit, **bound to the JulOS session** that is shown the page, valid
  10 minutes, **used up by the first POST** (also by a wrong one: a guess burns it). Only a page that this server gave to this very session has it, so a POST from anywhere
  else cannot sign anybody out, with or without a readable `Origin`. It lives in memory (a restart only means the person is asked once more). (2) The `Origin` check stays, and
  **for this one route** `Origin: null` is also accepted, but only together with **`Sec-Fetch-Site: same-origin`** (a header the page cannot forge and a browser always sends for
  a same-site form); a foreign `Origin`, `null` with `cross-site`, `same-site`, `none` or no `Sec-Fetch-Site` are refused. No other route changed: `ApiGuard.SameOrigin` is the same
  as before. Not chosen: relaxing `Referrer-Policy` for `/oidc/`; it would repair only this one symptom, while the token also protects when the header is missing or odd for any other reason.
- Checked for the same problem: the other pages of the centre (sign-in, bounce, no access, errors, signed out) are **GET pages with no form**; the sign-in page posts by `fetch` to
  `/api/auth/login` (the normal sign-in route with its own checks), and the authorize step is a redirect. The only form POST is this one.

## The name of the centre

Apps want to label their button "Sign in with JulOS" without a session. `/api/about` needs one (it names the version and the host system), so the **discovery document** has one
extra field: **`julos_display_name`** (`"JulOS"`). Apps read it from `GET <issuer>/.well-known/openid-configuration`, which they fetch anyway. Nothing else was opened up: no
version, no host data; the document is rate limited (120 per minute and address) and listed by name in `RouteSecurityTests`. Unknown fields are ignored by standard clients.

## Setting it up: what a person must know

- **Register all three addresses of the app exactly** (return, after sign-out, back-channel), as the app's settings card shows them. The return address is compared **exactly**,
  including the port and the trailing path.
- **Tick "App on the local network"** when the app runs behind the same router (almost always); without it a private address is refused for the back channel.
- **The secret is shown once.** The app's "Check connection" cannot test it (JulOS only asks for it when a person signs in).
- **Roles:** the keys must be the app's own role keys; the owner always has `admin`; a person without an entry sees "No access" and gets no token.
- A JulOS that is away locks nobody out: the app's own form keeps working.
- **A public address under `*.localhost` is treated like `localhost`:** `http` is allowed for the issuer and for the app's addresses, and the back channel may use `http` (the check
  looks at the address, not at the name only; every browser and glibc resolve `*.localhost` to 127.0.0.1).

### Two services on one machine (development, containers)

JulOS (the issuer) and the app call each other (description, keys, token; back-channel logout), and the **browser, the app and JulOS must use the same address for each of them**:
the issuer is in every token and the app compares it, the return address is compared exactly. Inside a container `localhost` is the container itself, so it cannot be the address.
The solution that needs nothing on the host (proved in the first real trial with the start app of the shared base):

1. One shared Docker network (`docker network create hubtrial-net`).
2. Give every service a name under `.localhost` and use **the same name as a network alias** on that network, and **the same port inside as is published outside**
   (`ASPNETCORE_HTTP_PORTS` = the published port; `JULOS_PUBLIC_URL=http://julos.localhost:18390`; ports `127.0.0.1:18390:18390`; alias `julos.localhost`; the app likewise as `app.localhost`).
   Then `http://julos.localhost:18390` is the same address from the browser and from the other container.
3. At JulOS register the app's three addresses with these names and tick "App on the local network" (the alias resolves to the Docker network, a private address).
4. Take care that a health check of the image does not ask the old port (the trial had to turn the sample's check off).

## Adding more people (proposal, not built)

Today a person is created only with `dotnet JulOS.dll user add NAME` in a terminal (the password is typed there). A trial, a script or a family member helping needs a terminal.
Proposal, like the invitation link of the shared base: **an invitation link**, made by the **owner only** (settings, with step-up), **valid once and for 24 hours**, stored as a hash,
that opens a page where the invited person chooses name and password and enrols the authenticator code (the same steps as the first set-up, which exist), after which the
owner gives a role per app. Not built here because it is a whole sign-up flow (new public page, token store, migration, rate limits, enrolment), not a small patch; it belongs to
the account work (see IDEAS.md).

## Claims of the ID token

`typ: JWT`, `alg: ES256`, `kid`. Claims:

| Claim | Meaning |
|---|---|
| `iss` | the **public address of JulOS** from the setting (below), **never** taken from the `Host` header |
| `sub` | a stable, random identifier of the person (`users.oidc_sub`): not the user name, not the internal id |
| `aud` | the `client_id` of the app (one value) |
| `exp`, `iat` | five minutes |
| `auth_time` | when the **JulOS session** began (what `max_age` is measured against) |
| `nonce` | as sent |
| `sid` | an identifier of this JulOS session **for this app** (random, not the cookie value); the same in the logout token |
| `amr` | `["pwd", "otp"]` (JulOS signs in with password and second factor today) |
| `name`, `preferred_username` | the user name (with the `profile` scope) |
| `role` | the person's role **in this app** |

The access token is an opaque random value (kept only as a hash) for `/oidc/userinfo`, five minutes. A token (ID or access) is **never** a way into JulOS itself.

## What an app must enter, and what JulOS asks of it

Settings, Security, **Anmelde-Zentrale** (only the owner; every change needs a fresh second factor **even when the step-up switch is off**, and is in the security history). Per app:

- **Name**, **return addresses** (exact, one per line; `https`, or `http` only to `localhost`, `127.0.0.1`, `[::1]`, `*.localhost`; no wildcards, no fragment, no user:password),
  **addresses after sign-out** (optional), the **address for back-channel logout** (optional), the **roles** the app knows (for example `admin, member, viewer`; the owner always gets `admin`, so an app should know that role).
- The back-channel address is **`https` only** (`http` only while the public address of JulOS is itself `http://localhost`, i.e. development). JulOS never connects to loopback, to link-local addresses (169.254.x.x with the cloud metadata, `fe80::/10`) or to reserved ranges. Addresses of the **local network** (10.x, 172.16–31.x, 192.168.x, `fc00::/7`, 100.64/10) are refused unless the per-app tick **"App on the local network"** is set; most of your own apps run there, so it is a deliberate choice with an explanation in the dialog. The check happens when you register **and again at every connection, after the name is resolved** (a name that later points inward does not help); a stored address that is no longer allowed is never posted to.
- JulOS shows the **`client_id` and a secret once** (256 bit of randomness). The secret is stored only as a SHA-256 hash and compared in constant time; a new one can be made at
  any time (the old one stops working at once).
- The **public address of JulOS** (the issuer) must be set once, in the same place (or by `JULOS_PUBLIC_URL`): `https`, no path. Until it is set the sign-in centre is off. The
  server key (`JULOS_SECRET_KEY` or `secret.key`) must exist: without it there are no signing keys.

The app uses the standard client: in ASP.NET Core `AddOpenIdConnect` with `Authority` = the public address, `ClientId`, `ClientSecret`, `ResponseType = "code"`, `UsePkce = true`
(the default), scope `openid profile`, and its own cookie session. `RequireHttpsMetadata` stays on in production. The app must read the `role` claim and refuse an unknown
role. **After a key rotation** a client that has the old key set cached fetches `/oidc/jwks` again when it meets an unknown `kid`; Microsoft's handler does that, but not more
than once per its refresh interval, so a sign-in in the first minutes after a rotation may have to be repeated.
The tests include such an app (`OidcClientTests`, Microsoft's handler against JulOS): it signs in, gets the role, and refuses a changed `state` and a changed `code`.

## Sign-out everywhere (back-channel logout)

When a JulOS session ends **by any way** (sign out, a device removed, expired, password changed, authenticator replaced, account removed), every app that got a token for it
receives a **logout token** (OIDC Back-Channel Logout 1.0): `POST` of `logout_token=<JWT>` (form) to the registered address, header `typ: logout+jwt`, claims `iss`, `aud`,
`iat`, `exp` (2 minutes), `jti`, `events` (`http://schemas.openid.net/event/backchannel-logout`), `sid`, `sub`, **no `nonce`**. The app ends its sessions with that `sid` and
answers `200` or `204`.

How it cannot be forgotten: every token issue writes a **grant** (JulOS session, app, `sid`). A background worker (every 3 seconds) moves every grant whose session no longer
exists into a queue, so it does not matter which code ended the session. Delivery: only to the **registered address** of that app, no redirects followed, no proxy, 5 seconds, **the body of the answer is never read** (only the status line and headers count);
apps are served **at the same time**, within one app in order and only up to the first failure (a dead app does not hold up the others; its other entries wait for the next round). On failure again after 10 s, 1 min, 5 min, 30 min (five tries), then the failure is final. If the signing key or the public address is only **missing for a while** (for example a restart without the server key), the entries **wait** (1 minute, no try used) instead of being dropped. The result (`oidc.logout.ok`, `oidc.logout.failed`) is in the security history as plain `ok` or `failed`; the reason (status, timeout) is only in the server log, so the target address cannot be used as a status oracle by whoever registers it. Also
queued: an app that is **blocked**, a person whose **role** is changed or removed (their sessions in that app), and a **reused code** (below). Deleting an app does not
send anything (block it first if its sessions must end).

## Threats and what answers them

- **A stolen or guessed code:** 256 bit, 60 seconds, once, stored as a hash; bound to the app, the return address, the PKCE challenge and the JulOS session; a wrong address or
  verifier **burns** it. **A code presented twice** revokes what it gave: the access token is deleted and the app's session gets a logout token. Redeeming, grant and access token happen in **one transaction**, so a second request that arrives at the same moment waits for the first and then really revokes (a revoke can no longer run before the winner has written its token).
- **Code interception, authorization code injection:** PKCE S256 is required; the verifier is compared in constant time; `state` is required.
- **Open redirect:** a return address is compared **exactly**; with an unknown app, a blocked app or a wrong address there is an error page and no redirect; errors go to the
  address only after it was checked; `post_logout_redirect_uri` needs a valid hint and an exact entry. No wildcards, no `javascript:`/`data:`.
- **Mix-up:** `iss` in the authorization response (RFC 9207) and in every token.
- **A fake issuer:** the issuer is a setting, never the `Host` header.
- **App secret guessed or leaked:** 256 bit, hash only, constant-time compare, lockout of the address after ten failed sign-ins of an app, new secret at any time, app can be
  blocked at once (its sessions end).
- **A person without a role:** no code, no token, an error page (silent requests get `access_denied`). The role is checked again when the code is exchanged and in userinfo.
- **A forged or stolen token for another app:** `aud` is exactly one `client_id`; the signature is ES256 and the algorithm is fixed (`none` and HS256 are never accepted, also
  not for a hint).
- **Cross-site request to change things:** the administration is under `/api/security/oidc` (session, own `Origin`, custom header, **second factor**, owner only); sign-out
  confirmation is a form checked for the same `Origin`.
- **Clickjacking:** no page can be framed. **Cache:** `no-store` on authorize, token, userinfo and sign-out. **CORS:** none.
- **A stolen database dump:** secrets and codes are hashes, access tokens are hashes, the signing key is encrypted with the server key (which is not in the database).
- **Brute force:** rate limits on authorize (120/min), token (60/min), userinfo, end-session; failed app sign-ins lock the address (growing), sign-in itself keeps its own lockout.
- **A key that leaks:** rotation in the app; the old private part is deleted at once, the old public part stays in `/oidc/jwks` for 10 minutes and for sign-out hints for 30 days.
- **A wrong server key (typo in `JULOS_SECRET_KEY`):** the active signing key cannot be opened. JulOS **stops** (503, a loud error in the log, a warning in the dialog) and **deletes and replaces nothing**; with the right key it continues with the same signing key. A new key ("Renew") is the owner's deliberate decision.
- **The owner account is removed:** the owner is **pinned** (instance setting `oidc.owner`, first set to the oldest confirmed account) and changes only on purpose (`PUT /api/security/oidc/owner`, owner only, second factor, audit `oidc.owner.set`). If the pinned account is gone, **nobody** is owner and nobody inherits `admin` in the apps. To recover, delete the row `oidc.owner` from `instance_settings` in the database: the oldest confirmed account is pinned again at the next use.
- **A sign-out hint from an old token:** a hint signs out silently only the session it was issued for (its `sid`); a logout token is never a hint.
- **Known limits, documented on purpose:** (1) the bounce page lifts `SameSite=Strict` for `authorize` and `end-session` (GET): any site can make a signed-in person's browser ask JulOS for a code, but the code goes only to the registered return address and only an app that checks `state` accepts it (120 requests per minute and address); sign-out asks first without a matching hint. It is needed because the app is another site. (2) `prompt=none` across sites always answers `login_required` (the Strict cookie does not come along and the bounce page is not silent), so silent sign-in between sites does not work. Apps use the normal redirect.

## What is missing on purpose

Passkeys and Google sign-in (later steps, BASE.md); refresh tokens (the app keeps its own session); dynamic client registration; implicit/hybrid/password flows;
`form_post` answers, fragment and JWT responses (the CSP of JulOS has `form-action 'self'`); consent screens (apps are your own); pairwise subject identifiers; encrypted ID tokens; session management through an iframe or
front-channel logout; a way to delete an app **and** end its sessions in one step (block, wait, delete); several owners (the owner is one pinned account; there is an endpoint to hand it over but no button yet); roles with rights (a role is a key the app interprets).

## What a security review should look at first

1. `Services/OidcService.cs`: order of the checks in `AuthorizeAsync` and `ExchangeAsync`, especially what burns a code and what does not, and the replay path.
2. `Data/OidcFlowStore.cs`: the redeem transaction (`BeginRedeemAsync`, what is committed when), the grant and queue SQL, the "ended session" test (a copy of the session validity of `SessionStore`).
3. `Oidc/OidcEndpoints.cs`: the end-session rules (when it signs out without a question), client authentication parsing (Basic, form), the bounce page and its loop guard.
4. `Services/OidcKeys.cs`: creation race, the sealed private key (`SecretBox`, key id as associated data), rotation, what `id_token_hint` accepts.
5. `Oidc/OidcAdminEndpoints.cs`: owner check, that step-up really covers every route (it does through `api/security/**` in `StepUpPolicy`; `StepUpPolicy.Always` makes it independent of the step-up switch), role changes and logout.
6. The exception in `RouteSecurityTests` is now only for `POST /oidc/token`, `/oidc/userinfo` and `/oidc/end-session` by name (no CSRF header there): token and userinfo have no cookie, end-session checks `Origin`.
7. The outbound call of the logout token (`OidcLogoutService`, `OidcNet`): the target filter at connect time (`OidcHttp.Guarded`), the per-app tick for the local network, no redirect, no proxy, no body read.

Tests: `OidcFlowTests`, `OidcLogoutTests`, `OidcKeyTests`, `OidcAdminTests`, `OidcClientTests`, `OidcRulesTests`, `OidcHardeningTests` (they use the test database like the other backend tests).
