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
| `GET /.well-known/openid-configuration` | public, read only: issuer, endpoints, what is supported |
| `GET /oidc/jwks` | public, read only: **public** parts of the signing keys (`kty`, `crv`, `x`, `y`, `kid`), never a private part |
| `GET /oidc/authorize` | the app and the **exact** return address must be registered (otherwise an error page, **never a redirect**); `response_type=code` only; PKCE `S256` **required**; `state` required; `nonce` supported; scope `openid` required, `profile` optional, other scopes are ignored; `prompt=none` and `login`; `max_age`; `response_mode`: the answer always comes as a query (`form_post`, which Microsoft's handler asks for by default, is answered as a query too and the handler accepts that; fragment and JWT modes are refused); rate limit 120 per minute and address |
| `POST /oidc/token` | form body only; the app signs in with `client_secret_basic` or `client_secret_post` (never both); code, redirect address and PKCE verifier must match; failed app sign-ins lock the address for a growing time (10 failures); 60 requests per minute; `Cache-Control: no-store` |
| `GET`/`POST /oidc/userinfo` | the access token in the `Authorization: Bearer` header only (never in the address); valid only while the JulOS session lives, the app is active and the person still has a role |
| `GET`/`POST /oidc/end-session` | RP-initiated logout: with a valid `id_token_hint` of **this** person it ends the session at once; otherwise it asks first (a form posting to itself, checked for the same `Origin`). It returns to `post_logout_redirect_uri` only if the hint is valid **and** the address is registered exactly |

There is no CORS: a browser may not read any of these from another site. The pages (sign-in, no access, error, sign-out) cannot be framed
(`X-Frame-Options: DENY`, `frame-ancestors 'none'`, set for every answer of the server) and use no inline script (CSP).
Errors for an unknown app, a wrong return address, a blocked app look **the same** (one page, no hint whether the app exists); `invalid_client` at the token endpoint is the same
for an unknown app and a wrong secret, and a hash is compared in both cases (same time).

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

Settings, Security, **Anmelde-Zentrale** (only the owner; every change needs the second factor and is in the security history). Per app:

- **Name**, **return addresses** (exact, one per line; `https`, or `http` only to `localhost`, `127.0.0.1`, `[::1]`, `*.localhost`; no wildcards, no fragment, no user:password),
  **addresses after sign-out** (optional), the **address for back-channel logout** (optional), the **roles** the app knows (for example `admin, member, viewer`).
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
exists into a queue, so it does not matter which code ended the session. Delivery: only to the **registered address** of that app, no redirects followed, no proxy, 5 seconds;
on failure again after 10 s, 1 min, 5 min, 30 min (five tries), then the failure is final. The result (`oidc.logout.ok`, `oidc.logout.failed`) is in the security history. Also
queued: an app that is **blocked**, a person whose **role** is changed or removed (their sessions in that app), and a **reused code** (below). Deleting an app does not
send anything (block it first if its sessions must end).

## Threats and what answers them

- **A stolen or guessed code:** 256 bit, 60 seconds, once, stored as a hash; bound to the app, the return address, the PKCE challenge and the JulOS session; a wrong address or
  verifier **burns** it. **A code presented twice** revokes what it gave: the access token is deleted and the app's session gets a logout token.
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

## What is missing on purpose

Passkeys and Google sign-in (later steps, BASE.md); refresh tokens (the app keeps its own session); dynamic client registration; implicit/hybrid/password flows;
`form_post`, fragment and JWT responses (the CSP of JulOS has `form-action 'self'`); consent screens (apps are your own); pairwise subject identifiers; encrypted ID tokens; session management through an iframe or
front-channel logout; a way to delete an app **and** end its sessions in one step (block, wait, delete); several owners; roles with rights (a role is a key the app interprets).

## What a security review should look at first

1. `Services/OidcService.cs`: order of the checks in `AuthorizeAsync` and `ExchangeAsync`, especially what burns a code and what does not, and the replay path.
2. `Data/OidcFlowStore.cs`: the atomic redeem (`update ... returning`), the grant and queue SQL, the "ended session" test (a copy of the session validity of `SessionStore`).
3. `Oidc/OidcEndpoints.cs`: the end-session rules (when it signs out without a question), client authentication parsing (Basic, form), the bounce page and its loop guard.
4. `Services/OidcKeys.cs`: creation race, the sealed private key (`SecretBox`, key id as associated data), rotation, what `id_token_hint` accepts.
5. `Oidc/OidcAdminEndpoints.cs`: owner check, that step-up really covers every route (it does through `api/security/**` in `StepUpPolicy`), role changes and logout.
6. Whether the exception in `RouteSecurityTests` for `/oidc/*` write routes (no CSRF header there) is acceptable: token and userinfo have no cookie, end-session checks `Origin`.
7. The outbound call of the logout token (`OidcLogoutService`): the target is an address an owner registered (it can be inside the home network on purpose); no redirect, no proxy.

Tests: `OidcFlowTests`, `OidcLogoutTests`, `OidcKeyTests`, `OidcAdminTests`, `OidcClientTests`, `OidcRulesTests` (they use the test database like the other backend tests).
