# The shared base of the app family and one sign-in through JulOS (draft, not built)

Status: **agreed draft**, 2026-11-23 (the decisions are at the end). Nothing is built. It answers one question: what do all apps of the family share, and how does a person stay signed in everywhere, also after an update, without making anything less safe.

## What the owner wants

- All new apps are built the same way. .NET, mostly own code; besides Microsoft only a few proven libraries.
- Every app **runs on its own**. Opened through JulOS, a person is **signed in at once**, in every app, also after an update of the app or of JulOS.
- Very safe. Sign-in with passkeys, TOTP, mail and Google where it makes sense.
- JulOS is the hub: the sign-in, the access to AI (the Codex bridge, set up once), the home of all apps.
- Plain names: the repository says what the app is (`os`, `caddy`, `arr`, `watch`, `read`, `ani`, `cook`, `finance`, `receipts`, `notes`, later `health`, `life`; the base is `base`).

## Rules for the base

1. **An app works without JulOS and without any other app.** JulOS adds comfort, never a dependency.
2. **Own code by default.** Allowed besides the .NET runtime and ASP.NET Core: the PostgreSQL driver (Npgsql), a password hash (Argon2id), a WebAuthn library for passkeys, Microsoft's OpenID Connect and Google sign-in handlers, Microsoft's token library (JWT). Crypto, password hashing, WebAuthn and TLS are **never** written by us.
3. **Into the base goes only what two apps need.** A part is first built in a real app, then moved into the base. Anything else stays in the app.
4. **A rule that matters has a number and, where possible, a test** (`AUTH-03`, `SHELL-01`). The agent rules of every repository cite the numbers.
5. **Plain SQL with numbered migrations, no build step in the browser, settings in the app** (as in JulOS today).
6. **One container plus PostgreSQL, a setup assistant at the first start, a Compose file without variables.**

## The parts of the base

| Part | What it gives an app | Comes from |
|---|---|---|
| **Core** | results and errors, validation, time, ids, paging | new, small |
| **Data** | connection, migrations at start, transactions, backup and restore hooks | JulOS (`database/`, `DbDump`) |
| **Accounts** | people, roles, password (Argon2id), TOTP, passkeys, recovery codes, sessions, the list of devices, lockout and rate limits, audit, second factor for dangerous actions | JulOS (`AuthService`, sessions, step-up, audit) plus passkeys and roles |
| **Sign-in with JulOS** | the button and the silent sign-in, sign-out everywhere | new (client: Microsoft's OpenID Connect handler) |
| **Web** | security headers and CSP, request checks against forgery, `/api/health`, version, the self-description of the app, a settings store with its page, the setup assistant, German and English | JulOS |
| **Look** | colours, type, spacing, the shell (menu, person menu), every control, icons, phone first; CSS, one script, one icon file | JulOS (`m3.css`, icons, Appearance), reworked into one kit |
| **Services** | mail (SMTP), push (Web Push), background jobs, files, connections to outside accounts (Google and the like, tokens encrypted), the AI client | JulOS (push), the three Codex bridges (one client) |
| **Project** | Dockerfile, Compose, CI, the agent rules, the rulebook, a start project that is a running app in minutes | new |

Only JulOS has on top: the **sign-in provider**, the **AI gateway**, the **registry and home of the apps**.

Delivery (decided): the base is **copied** into an app by a script, with its version in the files and a check that shows drift. No package server, every app builds alone. NuGet stays possible later for the C# part.

## Sign-in

### The session (the same in every app, taken from JulOS as it is today)

- A random 256-bit value in a cookie (`__Host-` prefix, `Secure`, `HttpOnly`, `SameSite`), stored **only as a hash in PostgreSQL**. Nothing lives in memory and nothing depends on a key that changes at start, so **an update or restart signs nobody out**.
- Idle 30 days, at most 180 days, rotated; one row per device, listed and revocable by the person; a new device is reported (push or mail).
- Changing requests need the session, a custom header and a matching origin.

### With JulOS: one sign-in for everything

Standard OpenID Connect, authorization code with PKCE, nothing else:

1. A person opens an app. The app has no session and sends the browser to JulOS.
2. JulOS has a session (the person is signed in there): it answers **at once, without a form**. Without a session it shows its sign-in.
3. The app receives a one-time code, exchanges it for a signed statement (who, which roles, which JulOS session) and creates **its own session** as above.

So: opened through JulOS, an app is open at once. The app's own session keeps it open across updates of the app; when it ends after weeks, step 1 to 3 run again unseen. JulOS's session keeps JulOS open across its own updates. **Signing out at JulOS, or removing a device there, ends the sessions of all apps** (JulOS tells each app; an app also checks from time to time).

What keeps it safe:

- Each app is registered at JulOS with its exact return address and its own secret; codes live seconds and work once; statements are signed, name the app they are for and are short-lived. The app never sees a password or a passkey.
- No shared cookie over a whole domain (any other service on that domain could read it).
- The sign-in at JulOS is the one door, so it gets the strongest lock (below), a second factor for dangerous actions as today, and its own review before release.
- An app keeps its **own sign-in form** next to "Sign in with JulOS" (decided), so it works on the day JulOS is away; that form has the same protection as JulOS's.

### How a person signs in at JulOS (and at an app that runs alone)

| Way | Role | Note |
|---|---|---|
| **Passkey** | the normal way | cannot be phished, nothing to type; several per person (phone, PC, a key in the drawer) |
| **Password + TOTP** | the fallback | as today; the TOTP secret is stored encrypted |
| **Recovery codes** | when everything is lost | shown once, stored as hashes |
| **Google** | a third way, for comfort (decided) | Microsoft's handler; only for a person who exists already, and a new device still asks for a passkey or TOTP, so a taken-over Google account alone opens nothing |
| **Mail** | messages, never a key by itself | new device, new passkey, sign-in from a new place; a link in a mail does not sign in an administrator |

Connecting Google for data (calendar for the life app) is a separate thing, a **connection** with its own consent and an encrypted token, not a sign-in.

## The Codex bridge through JulOS

Today three apps each carry a copy of the bridge and each is signed in separately. New: the ChatGPT sign-in happens **once in JulOS**. An app asks JulOS with its own key; JulOS forwards, counts and limits per app (budget, model, what may be sent). An app that runs alone can still be given a bridge of its own. The base holds the one client all apps use. See [ASSISTANT.md](ASSISTANT.md) for the bridge itself.

## Order of work

1. The walkthrough of JulOS with the owner and the fixes from it (agreed first).
2. `base` with Core, Data, Web, Look and the start project; the first rules with numbers. **First app on it: the web of `notes`** (small, and its look is the one the owner dislikes).
3. Accounts in the base (taken out of JulOS, plus passkeys and roles). JulOS itself moves onto it.
4. JulOS as sign-in provider; `notes` signs in through it. Review of the whole sign-in before release.
5. The AI gateway in JulOS and the one client; the apps drop their own bridges.
6. One app after the other moves onto the base; an app is split when it moves (`arr` and `watch`, `finance` and `receipts`), not before.

## Decisions of the owner (2026-11-23, asked one by one)

- **Sign-in at JulOS:** a passkey is the normal way; a password with TOTP stays a full second way.
- **Google:** also as a sign-in ("Sign in with Google"), not only as a connection for data. A new device still asks for a passkey or TOTP, and Google only signs in a person who exists already.
- **An app connected to JulOS shows both:** its own sign-in form and "Sign in with JulOS". So an app's own form stays a way in and is protected like JulOS's (rate limit, lockout, second factor, audit); the local accounts of an app are few and named.
- **Other people (family):** accounts in JulOS with a role per app; one place to manage and to lock.
- **The sign-in provider in JulOS:** own code, limited to the one flow above, with Microsoft's token library, and a review of its own before release.
- **Delivery:** the base is copied into an app by a script (version in the files, a check for drift). No package server.
- **Order:** first the walkthrough of JulOS with the owner and the fixes from it, then the base with `notes` as the first app.
